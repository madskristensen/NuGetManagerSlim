using System;
using System.IO;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using NuGetManagerSlim.ViewModels;

namespace NuGetManagerSlim.Services
{
    /// <summary>
    /// Persists the last selected view mode (All packages / Installed / Updates /
    /// Vulnerable) so it can be restored on the next session. Visual Studio
    /// independently persists the view-mode menu controller's anchor icon across
    /// restarts, so without restoring the mode the toolbar icon, the dropdown
    /// check mark, and the package list end up disagreeing on startup (issue #23).
    /// Storage is a single JSON file under %LocalAppData%.
    /// </summary>
    public sealed class ViewModePreferenceService : IViewModePreferenceService
    {
        private static readonly JsonSerializerOptions SerializerOptions = new()
        {
            WriteIndented = false,
        };

        private readonly string _filePath;
        private readonly SemaphoreSlim _gate = new(1, 1);

        public ViewModePreferenceService()
            : this(DefaultPath())
        {
        }

        public ViewModePreferenceService(string filePath)
        {
            _filePath = filePath;
        }

        private static string DefaultPath()
        {
            var dir = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "NuGetManagerSlim");
            return Path.Combine(dir, "viewmode.json");
        }

        public async Task<PackageViewMode?> GetAsync(CancellationToken cancellationToken)
        {
            await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                if (!File.Exists(_filePath)) return null;

                using var stream = new FileStream(
                    _filePath, FileMode.Open, FileAccess.Read, FileShare.Read,
                    bufferSize: 4096, useAsync: true);
                var state = await JsonSerializer
                    .DeserializeAsync<ViewModeState>(stream, SerializerOptions, cancellationToken)
                    .ConfigureAwait(false);

                if (state?.ViewMode != null
                    && Enum.TryParse<PackageViewMode>(state.ViewMode, out var mode))
                {
                    return mode;
                }

                return null;
            }
            catch (OperationCanceledException) { throw; }
            catch
            {
                // Corrupt/unreadable file - fall back to the default mode.
                return null;
            }
            finally
            {
                _gate.Release();
            }
        }

        public async Task SaveAsync(PackageViewMode viewMode, CancellationToken cancellationToken)
        {
            await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                var dir = Path.GetDirectoryName(_filePath);
                if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
                    Directory.CreateDirectory(dir);

                var tmp = _filePath + ".tmp";
                using (var stream = new FileStream(
                    tmp, FileMode.Create, FileAccess.Write, FileShare.None,
                    bufferSize: 4096, useAsync: true))
                {
                    await JsonSerializer
                        .SerializeAsync(stream, new ViewModeState { ViewMode = viewMode.ToString() }, SerializerOptions, cancellationToken)
                        .ConfigureAwait(false);
                }

                if (File.Exists(_filePath))
                    File.Delete(_filePath);
                File.Move(tmp, _filePath);
            }
            catch (OperationCanceledException) { throw; }
            catch
            {
                // Best-effort; never block a view-mode switch on a write failure.
            }
            finally
            {
                _gate.Release();
            }
        }

        private sealed class ViewModeState
        {
            public string? ViewMode { get; set; }
        }
    }
}
