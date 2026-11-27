using System;
using System.Collections.Concurrent;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace Service
{
    public sealed class FeedCacheOptions
    {
        /// <summary>Directory for the persisted cache (survives restarts); null keeps the cache in memory only.</summary>
        public string Dir { get; set; }

        /// <summary>Within this time after a refresh a feed is served from the cache without any API call.</summary>
        public TimeSpan RefreshInterval { get; set; } = TimeSpan.FromMinutes(10);

        /// <summary>A feed is rebuilt from scratch this often (picks up deleted videos and edited titles).</summary>
        public TimeSpan FullRefreshInterval { get; set; } = TimeSpan.FromDays(7);

        /// <summary>Channel/playlist details (title, description, uploads playlist) are looked up again after this time.</summary>
        public TimeSpan ChannelRefreshInterval { get; set; } = TimeSpan.FromDays(1);

        /// <summary>Unknown channels are remembered for this time so dead subscriptions do not cost quota on every poll.</summary>
        public TimeSpan NotFoundInterval { get; set; } = TimeSpan.FromHours(6);

        /// <summary>After a failed refresh the cached feed is served as it is for this long before the API is tried again.</summary>
        public TimeSpan RetryInterval { get; set; } = TimeSpan.FromMinutes(1);
    }

    public sealed class CachedItem
    {
        public string VideoId { get; set; }
        public string Title { get; set; }
        public string Description { get; set; }
        public DateTime PublishedAt { get; set; }

        /// <summary>A live stream, announced broadcast or not yet processed video: kept (so it is known) but left out of the feed until it is a normal video.</summary>
        public bool Pending { get; set; }
    }

    public sealed class CachedPlaylist
    {
        public System.Collections.Generic.List<CachedItem> Items { get; set; } = new();
        public long TotalResults { get; set; }

        /// <summary>True when Items holds the whole playlist (not cut at the requested maxLength).</summary>
        public bool Complete { get; set; }

        public DateTime FetchedAt { get; set; }
        public DateTime FullRefreshAt { get; set; }
        public DateTime RetryAt { get; set; }
    }

    public sealed class CachedChannel
    {
        public bool NotFound { get; set; }
        public string PlaylistId { get; set; }
        public string Id { get; set; }
        public string Title { get; set; }
        public string Description { get; set; }
        public string ImageUrl { get; set; }
        public DateTime PublishedAt { get; set; }
        public DateTime FetchedAt { get; set; }
        public DateTime RetryAt { get; set; }
    }

    /// <summary>
    /// Keeps cached objects in memory and, when a directory is given, as JSON files (one per key) so that a restart does not
    /// cause every feed to be fetched from YouTube again. Work on one key is serialised with <see cref="WithLockAsync"/>.
    /// </summary>
    public sealed class FeedStore
    {
        private readonly string _dir;
        private readonly ConcurrentDictionary<string, object> _memory = new();
        private readonly ConcurrentDictionary<string, SemaphoreSlim> _locks = new();

        public FeedStore(string dir)
        {
            _dir = dir;
            if (_dir != null)
            {
                try
                {
                    Directory.CreateDirectory(_dir);
                }
                catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
                {
                    Console.Error.WriteLine($"Feed cache directory '{_dir}' is not usable ({exception.Message}): feeds are cached in memory only.");
                    _dir = null;
                }
            }
        }

        public async Task<T> WithLockAsync<T>(string key, Func<Task<T>> action)
        {
            var gate = _locks.GetOrAdd(key, _ => new SemaphoreSlim(1, 1));
            await gate.WaitAsync();
            try
            {
                return await action();
            }
            finally
            {
                gate.Release();
            }
        }

        public T Get<T>(string key) where T : class
        {
            if (_memory.TryGetValue(key, out var cached))
            {
                return (T)cached;
            }

            if (_dir == null)
            {
                return null;
            }

            try
            {
                var path = PathFor(key);
                if (!File.Exists(path))
                {
                    return null;
                }

                var value = JsonSerializer.Deserialize<T>(File.ReadAllBytes(path));
                if (value != null)
                {
                    _memory[key] = value;
                }

                return value;
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or JsonException)
            {
                Console.Error.WriteLine($"Feed cache entry '{key}' could not be read ({exception.Message}): ignored.");
                return null;
            }
        }

        public void Set<T>(string key, T value) where T : class
        {
            _memory[key] = value;
            if (_dir == null)
            {
                return;
            }

            try
            {
                var path = PathFor(key);
                var temp = path + ".tmp";
                File.WriteAllBytes(temp, JsonSerializer.SerializeToUtf8Bytes(value));
                File.Move(temp, path, overwrite: true);
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                Console.Error.WriteLine($"Feed cache entry '{key}' could not be saved ({exception.Message}).");
            }
        }

        private string PathFor(string key) =>
            Path.Combine(_dir, Convert.ToHexString(SHA1.HashData(Encoding.UTF8.GetBytes(key))).ToLowerInvariant() + ".json");
    }
}
