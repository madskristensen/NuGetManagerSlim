using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Community.VisualStudio.Toolkit;

namespace NuGetManagerSlim.Services
{
    /// <summary>
    /// In-process icon cache with a disk-backed second tier. Decodes images to
    /// the actual display size (28px) so the package list isn't carrying full
    /// 512x512 PNGs in memory, and freezes the BitmapImage so it can be shared
    /// across threads without copying. The on-disk cache is bounded by an LRU
    /// index file so a long-running VS install doesn't accumulate icons forever.
    /// </summary>
    public sealed class IconCacheService
    {
        public static IconCacheService Instance { get; } = new IconCacheService();

        private const int DecodePixelWidth = 28;
        // 25 MB on-disk cap. Roughly thousands of typical 28px PNGs - plenty for
        // any realistic browse session.
        private const long MaxDiskBytes = 25L * 1024 * 1024;
        // Cap concurrent icon downloads so a long page of fresh results doesn't
        // starve other HTTP traffic (search, metadata) on a slow link.
        private const int MaxConcurrentDownloads = 8;
        private static readonly TimeSpan IndexFlushDelay = TimeSpan.FromSeconds(5);
        private const string IndexFileName = "index.json";

        private static readonly HttpClient _http = CreateClient();
        private static readonly SemaphoreSlim _downloadGate = new(MaxConcurrentDownloads, MaxConcurrentDownloads);

        private readonly string _cacheDir;
        private readonly string _indexPath;
        private readonly Dictionary<string, ImageSource?> _memCache = new(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<string, Task<ImageSource?>> _inFlight = new(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<string, IndexEntry> _index = new(StringComparer.OrdinalIgnoreCase);
        private readonly object _gate = new();
        private readonly object _indexGate = new();
        private bool _indexLoaded;
        private bool _flushScheduled;

        private IconCacheService()
        {
            _cacheDir = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "NuGetManagerSlim",
                "IconCache");
            _indexPath = Path.Combine(_cacheDir, IndexFileName);
        }

        private static HttpClient CreateClient()
        {
            // The framework-default per-host connection limit (2) serializes icon
            // downloads behind a long page of fresh results. Lift it to match the
            // download-gate semaphore so we can actually run them in parallel.
            try
            {
                if (ServicePointManager.DefaultConnectionLimit < 24)
                    ServicePointManager.DefaultConnectionLimit = 24;
            }
            catch { }

            var handler = new HttpClientHandler { AutomaticDecompression = DecompressionMethods.GZip | DecompressionMethods.Deflate };
            var client = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(8) };
            client.DefaultRequestHeaders.UserAgent.ParseAdd("NuGetManagerSlim/1.0");
            return client;
        }

        public Task<ImageSource?> GetIconAsync(string? url, CancellationToken cancellationToken = default)
        {
            if (string.IsNullOrWhiteSpace(url)) return Task.FromResult<ImageSource?>(null);
            var key = url!;

            lock (_gate)
            {
                if (_memCache.TryGetValue(key, out var hit))
                {
                    TouchIndex(key);
                    return Task.FromResult(hit);
                }
                if (_inFlight.TryGetValue(key, out var pending)) return pending;

                var task = LoadAsync(key, cancellationToken);
                _inFlight[key] = task;
                return task;
            }
        }

        private async Task<ImageSource?> LoadAsync(string url, CancellationToken cancellationToken)
        {
            try
            {
                var path = GetCachePath(url);
                byte[]? bytes = null;
                if (File.Exists(path))
                {
                    try { bytes = await ReadAllBytesAsync(path, cancellationToken).ConfigureAwait(false); }
                    catch { bytes = null; }
                }

                if (bytes == null || bytes.Length == 0)
                {
                    bytes = await DownloadGatedAsync(url, cancellationToken).ConfigureAwait(false);
                    if (bytes != null && bytes.Length > 0)
                    {
                        try
                        {
                            Directory.CreateDirectory(_cacheDir);
                            await WriteAllBytesAsync(path, bytes, cancellationToken).ConfigureAwait(false);
                        }
                        catch (Exception ex) { await ex.LogAsync(); }
                    }
                }

                ImageSource? image = bytes != null ? Decode(bytes) : null;

                lock (_gate)
                {
                    _memCache[url] = image;
                    _inFlight.Remove(url);
                }

                if (bytes != null && bytes.Length > 0)
                    RecordIndex(url, Path.GetFileName(path), bytes.Length);

                return image;
            }
            catch (Exception ex)
            {
                await ex.LogAsync();
                lock (_gate)
                {
                    _memCache[url] = null;
                    _inFlight.Remove(url);
                }
                return null;
            }
        }

        // .NET Framework 4.8 doesn't ship File.ReadAllBytesAsync / WriteAllBytesAsync,
        // so we use FileStream with useAsync: true to get true overlapped I/O.
        private static async Task<byte[]> ReadAllBytesAsync(string path, CancellationToken cancellationToken)
        {
            using var stream = new FileStream(
                path, FileMode.Open, FileAccess.Read, FileShare.Read,
                bufferSize: 4096, useAsync: true);
            using var ms = new MemoryStream(capacity: (int)Math.Min(stream.Length, int.MaxValue));
            await stream.CopyToAsync(ms, 81920, cancellationToken).ConfigureAwait(false);
            return ms.ToArray();
        }

        private static async Task WriteAllBytesAsync(string path, byte[] bytes, CancellationToken cancellationToken)
        {
            using var stream = new FileStream(
                path, FileMode.Create, FileAccess.Write, FileShare.None,
                bufferSize: 4096, useAsync: true);
            await stream.WriteAsync(bytes, 0, bytes.Length, cancellationToken).ConfigureAwait(false);
        }

        private static ImageSource? Decode(byte[] bytes)
        {
            try
            {
                using var ms = new MemoryStream(bytes);
                var bmp = new BitmapImage();
                bmp.BeginInit();
                bmp.CacheOption = BitmapCacheOption.OnLoad;
                bmp.CreateOptions = BitmapCreateOptions.IgnoreColorProfile;
                bmp.DecodePixelWidth = DecodePixelWidth;
                bmp.StreamSource = ms;
                bmp.EndInit();
                if (bmp.CanFreeze) bmp.Freeze();
                return bmp;
            }
            catch
            {
                return null;
            }
        }

        private async Task<byte[]?> DownloadGatedAsync(string url, CancellationToken cancellationToken)
        {
            await _downloadGate.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                return await DownloadAsync(url, cancellationToken).ConfigureAwait(false);
            }
            finally
            {
                _downloadGate.Release();
            }
        }

        private async Task<byte[]?> DownloadAsync(string url, CancellationToken cancellationToken)
        {
            // One retry on transient failures (5xx, network blip) with jittered backoff.
            for (var attempt = 0; attempt < 2; attempt++)
            {
                try
                {
                    using var resp = await _http.GetAsync(url, HttpCompletionOption.ResponseContentRead, cancellationToken).ConfigureAwait(false);
                    if ((int)resp.StatusCode >= 500 && attempt == 0)
                    {
                        await JitteredDelayAsync(cancellationToken).ConfigureAwait(false);
                        continue;
                    }
                    if (!resp.IsSuccessStatusCode) return null;
                    return await resp.Content.ReadAsByteArrayAsync().ConfigureAwait(false);
                }
                catch (OperationCanceledException) { throw; }
                catch (HttpRequestException) when (attempt == 0)
                {
                    await JitteredDelayAsync(cancellationToken).ConfigureAwait(false);
                }
                catch
                {
                    return null;
                }
            }
            return null;
        }

        private static Task JitteredDelayAsync(CancellationToken cancellationToken)
        {
            // 200-500ms backoff. Cheap for a single retry, prevents synchronized
            // re-tries against a flapping host.
            var ms = 200 + new Random().Next(0, 300);
            return Task.Delay(ms, cancellationToken);
        }

        private string GetCachePath(string url)
        {
            using var sha = SHA256.Create();
            var hash = sha.ComputeHash(Encoding.UTF8.GetBytes(url));
            var sb = new StringBuilder(hash.Length * 2);
            foreach (var b in hash) sb.Append(b.ToString("x2"));
            return Path.Combine(_cacheDir, sb.ToString() + ".bin");
        }

        // ---- LRU index ----

        private sealed class IndexEntry
        {
            public string File { get; set; } = string.Empty;
            public long SizeBytes { get; set; }
            public DateTime LastAccessUtc { get; set; }
        }

        private void EnsureIndexLoaded()
        {
            if (_indexLoaded) return;
            lock (_indexGate)
            {
                if (_indexLoaded) return;
                _indexLoaded = true;
                try
                {
                    if (!File.Exists(_indexPath)) return;
                    var json = File.ReadAllText(_indexPath);
                    var loaded = JsonSerializer.Deserialize<Dictionary<string, IndexEntry>>(json, JsonOpts);
                    if (loaded != null)
                    {
                        foreach (var kvp in loaded)
                            _index[kvp.Key] = kvp.Value;
                    }
                }
                catch (Exception ex)
                {
                    _ = ex.LogAsync();
                }
            }
        }

        private void RecordIndex(string url, string file, long sizeBytes)
        {
            EnsureIndexLoaded();
            bool needsEvict;
            lock (_indexGate)
            {
                _index[url] = new IndexEntry
                {
                    File = file,
                    SizeBytes = sizeBytes,
                    LastAccessUtc = DateTime.UtcNow,
                };
                needsEvict = _index.Values.Sum(e => e.SizeBytes) > MaxDiskBytes;
            }

            if (needsEvict) EvictLru();
            ScheduleFlush();
        }

        private void TouchIndex(string url)
        {
            EnsureIndexLoaded();
            lock (_indexGate)
            {
                if (_index.TryGetValue(url, out var entry))
                    entry.LastAccessUtc = DateTime.UtcNow;
            }
            ScheduleFlush();
        }

        private void EvictLru()
        {
            List<KeyValuePair<string, IndexEntry>> snapshot;
            lock (_indexGate)
                snapshot = _index.OrderBy(kvp => kvp.Value.LastAccessUtc).ToList();

            long total;
            lock (_indexGate) total = _index.Values.Sum(e => e.SizeBytes);

            foreach (var kvp in snapshot)
            {
                if (total <= MaxDiskBytes) break;
                try
                {
                    var path = Path.Combine(_cacheDir, kvp.Value.File);
                    if (File.Exists(path)) File.Delete(path);
                }
                catch { }
                lock (_indexGate)
                {
                    if (_index.Remove(kvp.Key)) total -= kvp.Value.SizeBytes;
                }
            }
        }

        private void ScheduleFlush()
        {
            lock (_indexGate)
            {
                if (_flushScheduled) return;
                _flushScheduled = true;
            }

            _ = Task.Run(async () =>
            {
                try
                {
                    await Task.Delay(IndexFlushDelay).ConfigureAwait(false);
                    string json;
                    lock (_indexGate)
                    {
                        _flushScheduled = false;
                        json = JsonSerializer.Serialize(_index, JsonOpts);
                    }
                    Directory.CreateDirectory(_cacheDir);
                    var tmp = _indexPath + ".tmp";
                    File.WriteAllText(tmp, json);
                    if (File.Exists(_indexPath)) File.Delete(_indexPath);
                    File.Move(tmp, _indexPath);
                }
                catch (Exception ex)
                {
                    _ = ex.LogAsync();
                    lock (_indexGate) _flushScheduled = false;
                }
            });
        }

        private static readonly JsonSerializerOptions JsonOpts = new()
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            WriteIndented = false,
        };
    }
}
