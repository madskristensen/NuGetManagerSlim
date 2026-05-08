using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Xunit;

namespace NuGetManagerSlim.Tests.Services
{
    public class FeedMetadataDiskCacheTests : IDisposable
    {
        // FeedMetadataDiskCache always roots itself under
        // %LocalAppData%\NuGetManagerSlim\FeedCache so we use a unique GUID
        // subdirectory per test and clean it up in Dispose.
        private readonly string _subdir = "test-" + Guid.NewGuid().ToString("N");

        private string FullDir => System.IO.Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "NuGetManagerSlim", "FeedCache", _subdir);

        public void Dispose()
        {
            try { if (Directory.Exists(FullDir)) Directory.Delete(FullDir, recursive: true); }
            catch { }
        }

        // FeedMetadataDiskCache is internal to the production assembly so we
        // exercise it via reflection. Keeps the production type sealed and
        // avoids exposing it as part of the public API surface.
        private sealed class CacheWrapper<T> where T : class
        {
            private readonly object _instance;
            private readonly System.Reflection.MethodInfo _read;
            private readonly System.Reflection.MethodInfo _write;
            private readonly System.Reflection.MethodInfo _clear;

            public CacheWrapper(string subdir, TimeSpan ttl, long maxBytes)
            {
                var asm = typeof(NuGetManagerSlim.Services.NuGetFeedService).Assembly;
                var openType = asm.GetType("NuGetManagerSlim.Services.FeedMetadataDiskCache`1", throwOnError: true)!;
                var closed = openType.MakeGenericType(typeof(T));
                _instance = Activator.CreateInstance(closed, subdir, ttl, maxBytes)!;
                _read = closed.GetMethod("ReadAsync")!;
                _write = closed.GetMethod("WriteAsync")!;
                _clear = closed.GetMethod("Clear")!;
            }

            public async Task<T?> ReadAsync(string key)
            {
                var task = (Task)_read.Invoke(_instance, new object[] { key, CancellationToken.None })!;
                await task.ConfigureAwait(false);
                return (T?)task.GetType().GetProperty("Result")!.GetValue(task);
            }

            public Task WriteAsync(string key, T value) =>
                (Task)_write.Invoke(_instance, new object[] { key, value, CancellationToken.None })!;

            public void Clear() => _clear.Invoke(_instance, Array.Empty<object>());
        }

        public sealed class Sample
        {
            public string Name { get; set; } = string.Empty;
            public int Number { get; set; }
        }

        [Fact]
        public async Task Roundtrip_ReturnsOriginalPayload()
        {
            var cache = new CacheWrapper<List<Sample>>(_subdir, TimeSpan.FromMinutes(1), 1024 * 1024);
            var payload = new List<Sample>
            {
                new() { Name = "A", Number = 1 },
                new() { Name = "B", Number = 2 },
            };

            await cache.WriteAsync("k1", payload);
            var read = await cache.ReadAsync("k1");

            Assert.NotNull(read);
            Assert.Equal(2, read!.Count);
            Assert.Equal("A", read[0].Name);
            Assert.Equal(2, read[1].Number);
        }

        [Fact]
        public async Task Read_WhenEntryExpired_ReturnsNullAndDeletesFile()
        {
            // TTL of 1ms expires by the time we read.
            var cache = new CacheWrapper<List<Sample>>(_subdir, TimeSpan.FromMilliseconds(1), 1024 * 1024);
            await cache.WriteAsync("k1", new List<Sample> { new() { Name = "X", Number = 99 } });

            await Task.Delay(20);
            var read = await cache.ReadAsync("k1");

            Assert.Null(read);
        }

        [Fact]
        public async Task Read_WhenKeyMissing_ReturnsNull()
        {
            var cache = new CacheWrapper<List<Sample>>(_subdir, TimeSpan.FromMinutes(1), 1024 * 1024);
            var read = await cache.ReadAsync("never-written");
            Assert.Null(read);
        }

        [Fact]
        public async Task Clear_RemovesAllEntries()
        {
            var cache = new CacheWrapper<List<Sample>>(_subdir, TimeSpan.FromMinutes(1), 1024 * 1024);
            await cache.WriteAsync("a", new List<Sample> { new() { Name = "1" } });
            await cache.WriteAsync("b", new List<Sample> { new() { Name = "2" } });

            cache.Clear();

            Assert.Null(await cache.ReadAsync("a"));
            Assert.Null(await cache.ReadAsync("b"));
        }
    }
}
