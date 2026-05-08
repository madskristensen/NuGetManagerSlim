using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;
using Community.VisualStudio.Toolkit;
using Microsoft.VisualStudio.Shell;

namespace NuGetManagerSlim.Services
{
    /// <summary>
    /// Lightweight structured logger that writes to the "NuGet Manager Slim"
    /// Output Window pane. Logging is best-effort: if the pane can't be created
    /// (e.g. running outside the VS shell during unit tests) entries are kept
    /// in a small in-memory ring buffer so they're still available for the
    /// CopyDiagnostics command. Verbose entries (per-keystroke search timings,
    /// cache hit/miss) are gated behind <see cref="VerboseEnabled"/> so the
    /// pane stays quiet by default.
    /// </summary>
    internal static class DiagnosticsLogger
    {
        private const int RingCapacity = 512;
        private static readonly object _gate = new();
        private static readonly LinkedList<string> _ring = new();
        private static OutputWindowPane? _pane;
        private static bool _paneInitInFlight;

        /// <summary>
        /// Enables verbose logging (per-search timings, cache hit/miss, cancellations).
        /// Off by default; flip via the (future) Tools &gt; Options page or for ad-hoc
        /// debugging by setting the NUGET_MANAGER_SLIM_VERBOSE environment variable.
        /// </summary>
        public static bool VerboseEnabled { get; set; } =
            string.Equals(Environment.GetEnvironmentVariable("NUGET_MANAGER_SLIM_VERBOSE"), "1", StringComparison.Ordinal);

        public static void Info(string message) => Write("INFO", message);

        public static void Warn(string message) => Write("WARN", message);

        public static void Verbose(string message)
        {
            if (!VerboseEnabled) return;
            Write("VERB", message);
        }

        public static IDisposable Time(string operation)
        {
            if (!VerboseEnabled) return NoopDisposable.Instance;
            return new Timer(operation);
        }

        /// <summary>
        /// Returns a snapshot of the in-memory ring buffer for the
        /// CopyDiagnostics command. Newest entries last.
        /// </summary>
        public static string[] Snapshot()
        {
            lock (_gate)
            {
                var arr = new string[_ring.Count];
                var i = 0;
                foreach (var line in _ring) arr[i++] = line;
                return arr;
            }
        }

        private static void Write(string level, string message)
        {
            var line = $"[{DateTime.Now:HH:mm:ss.fff}] {level} {message}";
            lock (_gate)
            {
                _ring.AddLast(line);
                while (_ring.Count > RingCapacity) _ring.RemoveFirst();
            }

            // Best-effort write to the Output pane. We never block the caller.
            try
            {
                EnsurePaneAndWrite(line);
            }
            catch
            {
                // Logging must never throw.
            }
        }

        private static void EnsurePaneAndWrite(string line)
        {
            var pane = _pane;
            if (pane != null)
            {
                _ = pane.WriteLineAsync(line);
                return;
            }

            // Create the pane lazily on first use. Uses a flag so concurrent
            // Write() calls don't queue multiple creation tasks.
            lock (_gate)
            {
                if (_pane != null)
                {
                    _ = _pane.WriteLineAsync(line);
                    return;
                }
                if (_paneInitInFlight) return;
                _paneInitInFlight = true;
            }

            // Fire-and-forget: log writes must never block the caller. The
            // ring buffer above already preserves the message even if the
            // pane creation fails (running outside VS shell, etc.).
#pragma warning disable VSSDK007
            ThreadHelper.JoinableTaskFactory.RunAsync(async () =>
            {
                try
                {
                    var created = await VS.Windows.CreateOutputWindowPaneAsync("NuGet Manager Slim");
                    string[] backlog;
                    lock (_gate)
                    {
                        _pane = created;
                        backlog = new string[_ring.Count];
                        var i = 0;
                        foreach (var l in _ring) backlog[i++] = l;
                    }
                    foreach (var l in backlog)
                        await created.WriteLineAsync(l);
                }
                catch
                {
                    // Running outside VS shell (tests). The ring buffer still
                    // captures everything for diagnostics purposes.
                }
                finally
                {
                    lock (_gate) _paneInitInFlight = false;
                }
            }).FileAndForget("vs/nugetmanagerslim/diagnostics/createpane");
#pragma warning restore VSSDK007
        }

        private sealed class Timer : IDisposable
        {
            private readonly string _op;
            private readonly Stopwatch _sw = Stopwatch.StartNew();

            public Timer(string op) { _op = op; }

            public void Dispose()
            {
                _sw.Stop();
                Verbose($"{_op} took {_sw.ElapsedMilliseconds} ms");
            }
        }

        private sealed class NoopDisposable : IDisposable
        {
            public static readonly NoopDisposable Instance = new();
            public void Dispose() { }
        }
    }
}
