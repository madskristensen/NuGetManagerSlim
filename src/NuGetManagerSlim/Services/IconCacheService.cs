using System;
using System.Collections.Generic;
using System.IO;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
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
    /// across threads without copying.
    /// </summary>
    public sealed class IconCacheService
    {
        public static IconCacheService Instance { get; } = new IconCacheService();

        private const int DecodePixelWidth = 28;
        private static readonly HttpClient _http = CreateClient();

        private readonly string _cacheDir;
        private readonly Dictionary<string, ImageSource?> _memCache = new(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<string, Task<ImageSource?>> _inFlight = new(StringComparer.OrdinalIgnoreCase);
        private readonly object _gate = new();

        private IconCacheService()
        {
            _cacheDir = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "NuGetManagerSlim",
                "IconCache");
        }

        private static HttpClient CreateClient()
        {
            var handler = new HttpClientHandler { AutomaticDecompression = System.Net.DecompressionMethods.GZip | System.Net.DecompressionMethods.Deflate };
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
                if (_memCache.TryGetValue(key, out var hit)) return Task.FromResult(hit);
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
                    try { bytes = File.ReadAllBytes(path); } catch { bytes = null; }
                }

                if (bytes == null || bytes.Length == 0)
                {
                    bytes = await DownloadAsync(url, cancellationToken).ConfigureAwait(false);
                    if (bytes != null && bytes.Length > 0)
                    {
                        try
                        {
                            Directory.CreateDirectory(_cacheDir);
                            File.WriteAllBytes(path, bytes);
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

        private async Task<byte[]?> DownloadAsync(string url, CancellationToken cancellationToken)
        {
            try
            {
                using var resp = await _http.GetAsync(url, HttpCompletionOption.ResponseContentRead, cancellationToken).ConfigureAwait(false);
                if (!resp.IsSuccessStatusCode) return null;
                return await resp.Content.ReadAsByteArrayAsync().ConfigureAwait(false);
            }
            catch
            {
                return null;
            }
        }

        private string GetCachePath(string url)
        {
            using var sha = SHA256.Create();
            var hash = sha.ComputeHash(Encoding.UTF8.GetBytes(url));
            var sb = new StringBuilder(hash.Length * 2);
            foreach (var b in hash) sb.Append(b.ToString("x2"));
            return Path.Combine(_cacheDir, sb.ToString() + ".bin");
        }
    }
}
