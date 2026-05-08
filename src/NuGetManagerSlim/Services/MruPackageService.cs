using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;
using NuGet.Versioning;
using NuGetManagerSlim.Models;

namespace NuGetManagerSlim.Services
{
    /// <summary>
    /// Persists the most-recently-used packages (installed or updated) so the
    /// Browse view can render instantly on open without waiting for a remote
    /// feed query. Storage is a single JSON file under %LocalAppData%.
    /// </summary>
    public sealed class MruPackageService : IMruPackageService
    {
        private const int MaxEntries = 50;

        private static readonly JsonSerializerOptions SerializerOptions = new()
        {
            WriteIndented = false,
            DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        };

        private readonly string _filePath;
        private readonly SemaphoreSlim _gate = new(1, 1);

        // In-memory snapshot keyed by package id (case-insensitive). Most-recent first.
        private List<MruEntry>? _entries;

        public MruPackageService()
            : this(DefaultPath())
        {
        }

        public MruPackageService(string filePath)
        {
            _filePath = filePath;
        }

        private static string DefaultPath()
        {
            var dir = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "NuGetManagerSlim");
            return Path.Combine(dir, "mru.json");
        }

        public async Task<IReadOnlyList<PackageModel>> GetRecentAsync(CancellationToken cancellationToken)
        {
            await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                await EnsureLoadedAsync(cancellationToken).ConfigureAwait(false);
                return _entries!
                    .Select(ToModel)
                    .ToList();
            }
            finally
            {
                _gate.Release();
            }
        }

        public async Task RecordAsync(PackageModel package, CancellationToken cancellationToken)
        {
            if (package == null || string.IsNullOrEmpty(package.PackageId))
                return;

            await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                await EnsureLoadedAsync(cancellationToken).ConfigureAwait(false);

                var existing = _entries!.FindIndex(e =>
                    string.Equals(e.PackageId, package.PackageId, StringComparison.OrdinalIgnoreCase));
                if (existing >= 0)
                    _entries.RemoveAt(existing);

                var version = package.InstalledVersion?.ToNormalizedString()
                              ?? package.LatestStableVersion?.ToNormalizedString()
                              ?? package.LatestPrereleaseVersion?.ToNormalizedString();

                _entries.Insert(0, new MruEntry
                {
                    PackageId = package.PackageId,
                    Version = version,
                    Authors = package.Authors,
                    Description = package.Description,
                    IconUrl = package.IconUrl,
                    SourceName = package.SourceName,
                    LastUsedUtc = DateTime.UtcNow,
                });

                if (_entries.Count > MaxEntries)
                    _entries.RemoveRange(MaxEntries, _entries.Count - MaxEntries);

                await SaveAsync(cancellationToken).ConfigureAwait(false);
            }
            finally
            {
                _gate.Release();
            }
        }

        private async Task EnsureLoadedAsync(CancellationToken cancellationToken)
        {
            if (_entries != null) return;

            try
            {
                if (File.Exists(_filePath))
                {
                    using var stream = new FileStream(
                        _filePath, FileMode.Open, FileAccess.Read, FileShare.Read,
                        bufferSize: 4096, useAsync: true);
                    _entries = await JsonSerializer
                        .DeserializeAsync<List<MruEntry>>(stream, SerializerOptions, cancellationToken)
                        .ConfigureAwait(false)
                        ?? new List<MruEntry>();
                    return;
                }
            }
            catch (OperationCanceledException) { throw; }
            catch
            {
                // Corrupt file - start fresh rather than failing the UI.
            }

            _entries = new List<MruEntry>();
        }

        private async Task SaveAsync(CancellationToken cancellationToken)
        {
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
                        .SerializeAsync(stream, _entries, SerializerOptions, cancellationToken)
                        .ConfigureAwait(false);
                }

                if (File.Exists(_filePath))
                    File.Delete(_filePath);
                File.Move(tmp, _filePath);
            }
            catch (OperationCanceledException) { throw; }
            catch
            {
                // MRU is best-effort; never block install/update on a write failure.
            }
        }

        private static PackageModel ToModel(MruEntry e)
        {
            NuGetVersion? version = null;
            if (!string.IsNullOrEmpty(e.Version))
                NuGetVersion.TryParse(e.Version, out version);

            return new PackageModel
            {
                PackageId = e.PackageId ?? string.Empty,
                LatestStableVersion = version != null && !version.IsPrerelease ? version : null,
                LatestPrereleaseVersion = version,
                Authors = e.Authors,
                Description = e.Description,
                IconUrl = e.IconUrl,
                SourceName = e.SourceName,
            };
        }

        private sealed class MruEntry
        {
            public string? PackageId { get; set; }
            public string? Version { get; set; }
            public string? Authors { get; set; }
            public string? Description { get; set; }
            public string? IconUrl { get; set; }
            public string? SourceName { get; set; }
            public DateTime LastUsedUtc { get; set; }
        }
    }
}
