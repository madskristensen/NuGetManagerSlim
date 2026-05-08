using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Community.VisualStudio.Toolkit;

namespace NuGetManagerSlim.Services
{
    /// <summary>
    /// Disk-backed second tier for the in-memory feed caches in
    /// <see cref="NuGetFeedService"/>. Lets the very first search on a fresh
    /// VS session be served without a network round-trip when the same query
    /// was seen in a prior session.
    ///
    /// Storage layout: %LocalAppData%\NuGetManagerSlim\FeedCache\&lt;sha256(key)&gt;.json
    /// Each file holds a small envelope: { "expiresUtc": "...", "payload": ... }.
    /// </summary>
    /// <typeparam name="T">Payload type, serialized with System.Text.Json.</typeparam>
    internal sealed class FeedMetadataDiskCache<T> where T : class
    {
        private readonly string _dir;
        private readonly TimeSpan _ttl;
        private readonly long _maxBytes;
        private readonly object _writeGate = new();
        private long _bytesSinceLastSweep;
        private const long SweepEvery = 5 * 1024 * 1024;

        public FeedMetadataDiskCache(string subdirectory, TimeSpan ttl, long maxBytes)
        {
            _dir = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "NuGetManagerSlim",
                "FeedCache",
                subdirectory);
            _ttl = ttl;
            _maxBytes = maxBytes;
        }

        public async Task<T?> ReadAsync(string key, CancellationToken cancellationToken)
        {
            try
            {
                var path = GetPath(key);
                if (!File.Exists(path)) return null;

                using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 4096, useAsync: true);
                var envelope = await JsonSerializer.DeserializeAsync<Envelope>(stream, JsonOptions, cancellationToken).ConfigureAwait(false);
                if (envelope == null) return null;

                if (envelope.ExpiresUtc <= DateTime.UtcNow)
                {
                    TryDelete(path);
                    return null;
                }

                if (envelope.Payload.ValueKind == JsonValueKind.Undefined ||
                    envelope.Payload.ValueKind == JsonValueKind.Null)
                {
                    return null;
                }

                // Touch lastAccess so LRU sweeps prefer keeping recently-read entries.
                try { File.SetLastAccessTimeUtc(path, DateTime.UtcNow); } catch { }

                return envelope.Payload.Deserialize<T>(JsonOptions);
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex)
            {
                // Disk cache is best-effort; corruption / IO failures fall through to the network.
                await ex.LogAsync();
                return null;
            }
        }

        public async Task WriteAsync(string key, T payload, CancellationToken cancellationToken)
        {
            if (payload == null) return;

            try
            {
                Directory.CreateDirectory(_dir);
                var path = GetPath(key);
                var tmp = path + ".tmp";

                var envelope = new Envelope
                {
                    ExpiresUtc = DateTime.UtcNow.Add(_ttl),
                    Payload = JsonSerializer.SerializeToElement(payload, JsonOptions),
                };

                using (var stream = new FileStream(tmp, FileMode.Create, FileAccess.Write, FileShare.None, 4096, useAsync: true))
                {
                    await JsonSerializer.SerializeAsync(stream, envelope, JsonOptions, cancellationToken).ConfigureAwait(false);
                }

                // Atomic move - readers never see a half-written file.
                if (File.Exists(path)) File.Delete(path);
                File.Move(tmp, path);

                Interlocked.Add(ref _bytesSinceLastSweep, new FileInfo(path).Length);
                if (Interlocked.Read(ref _bytesSinceLastSweep) >= SweepEvery)
                {
                    Interlocked.Exchange(ref _bytesSinceLastSweep, 0);
                    _ = Task.Run(SweepLru);
                }
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex)
            {
                await ex.LogAsync();
            }
        }

        public void Clear()
        {
            try
            {
                if (!Directory.Exists(_dir)) return;
                foreach (var f in Directory.EnumerateFiles(_dir))
                    TryDelete(f);
            }
            catch (Exception ex)
            {
                _ = ex.LogAsync();
            }
        }

        private void SweepLru()
        {
            lock (_writeGate)
            {
                try
                {
                    if (!Directory.Exists(_dir)) return;

                    var files = new DirectoryInfo(_dir).GetFiles()
                        .OrderByDescending(f => f.LastAccessTimeUtc)
                        .ToArray();

                    long total = 0;
                    foreach (var f in files)
                    {
                        total += f.Length;
                        if (total > _maxBytes)
                            TryDelete(f.FullName);
                    }
                }
                catch (Exception ex)
                {
                    _ = ex.LogAsync();
                }
            }
        }

        private string GetPath(string key)
        {
            using var sha = SHA256.Create();
            var hash = sha.ComputeHash(Encoding.UTF8.GetBytes(key));
            var sb = new StringBuilder(hash.Length * 2);
            foreach (var b in hash) sb.Append(b.ToString("x2"));
            return Path.Combine(_dir, sb.ToString() + ".json");
        }

        private static void TryDelete(string path)
        {
            try { if (File.Exists(path)) File.Delete(path); } catch { }
        }

        private static readonly JsonSerializerOptions JsonOptions = new()
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            WriteIndented = false,
        };

        private sealed class Envelope
        {
            public DateTime ExpiresUtc { get; set; }
            public JsonElement Payload { get; set; }
        }
    }
}
