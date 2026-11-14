using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using YoutubeExplode;

namespace Service
{
    /// <summary>
    /// YouTube no longer offers muxed audio+video streams. This downloads an H.264 video-only stream and the AAC audio
    /// stream (in YouTube-friendly chunks, through the configured proxy), joins them with "ffmpeg -c copy" into a
    /// faststart MP4 and keeps the result in an LRU disk cache, so that it can be served with Range support.
    /// </summary>
    public sealed class VideoCache
    {
        private static readonly TimeSpan _ffmpegTimeout = TimeSpan.FromMinutes(15);

        private readonly YoutubeClient _youtubeClient;
        private readonly VideoOptions _options;
        private readonly string _cacheDir;
        private readonly string _tempDir;
        private const int _maxQueuedPrewarmJobs = 4;

        // Jobs started by a client request use the demand gate; background pre-warming uses its own single-slot gate and
        // only runs while no client request is waiting or running, so that it can never delay a play request.
        private readonly SemaphoreSlim _demandGate;
        private readonly SemaphoreSlim _prewarmGate = new SemaphoreSlim(1);
        private readonly Dictionary<string, Job> _jobs = new Dictionary<string, Job>();
        private readonly object _lock = new object();
        private readonly object _evictionLock = new object();
        private int _demandJobs;
        private int _activeClients;

        private sealed class Job
        {
            public Task<string> Task;
            public bool IsPrewarm;
            public bool Started;
            public CancellationTokenSource Cancellation = new CancellationTokenSource();
        }

        public VideoCache(YoutubeClient youtubeClient, VideoOptions options)
        {
            _youtubeClient = youtubeClient;
            _options = options;
            _cacheDir = Path.GetFullPath(options.CacheDir ?? VideoOptions.DefaultCacheDir);
            _tempDir = Path.Combine(_cacheDir, "tmp");
            _demandGate = new SemaphoreSlim(Math.Max(1, options.MaxConcurrentMuxes));

            Directory.CreateDirectory(_cacheDir);
            if (Directory.Exists(_tempDir))
            {
                Directory.Delete(_tempDir, recursive: true);
            }

            Directory.CreateDirectory(_tempDir);
        }

        public int PrewarmCount => _options.PrewarmCount;

        /// <summary>Returns the path of the cached MP4 (producing it first if needed), or null on failure.</summary>
        public Task<string> GetAsync(string videoId, int height) => GetAsync(videoId, height, prewarm: false);

        /// <summary>
        /// Marks a client as downloading a video from the cache; background pre-warming pauses until all have finished,
        /// because downloads and clients share the same (often slow) network path.
        /// </summary>
        public IDisposable BeginClient()
        {
            Interlocked.Increment(ref _activeClients);
            return new ClientScope(this);
        }

        private sealed class ClientScope : IDisposable
        {
            private VideoCache _cache;

            public ClientScope(VideoCache cache) => _cache = cache;

            public void Dispose()
            {
                var cache = Interlocked.Exchange(ref _cache, null);
                if (cache != null)
                {
                    Interlocked.Decrement(ref cache._activeClients);
                }
            }
        }

        /// <summary>Starts producing a file in the background at low priority, ignoring the result.</summary>
        public void Prewarm(string videoId, int height) => _ = GetAsync(videoId, height, prewarm: true);

        private Task<string> GetAsync(string videoId, int height, bool prewarm)
        {
            var path = PathFor(videoId, height);
            if (File.Exists(path))
            {
                Touch(path);
                return Task.FromResult(path);
            }

            var key = Path.GetFileName(path);
            lock (_lock)
            {
                if (_jobs.TryGetValue(key, out var existing))
                {
                    if (prewarm || !existing.IsPrewarm || existing.Started)
                    {
                        return existing.Task;
                    }

                    // A client wants a file that is only queued for pre-warming: drop the queued job and run it with priority.
                    existing.Cancellation.Cancel();
                    _jobs.Remove(key);
                }
                else if (prewarm && _jobs.Count(job => job.Value.IsPrewarm) >= _maxQueuedPrewarmJobs)
                {
                    return Task.FromResult<string>(null);
                }

                var created = new Job { IsPrewarm = prewarm };
                if (!prewarm)
                {
                    _demandJobs++;
                }

                created.Task = Task.Run(() => RunJobAsync(created, key, videoId, height, path));
                _jobs[key] = created;
                return created.Task;
            }
        }

        private async Task<string> RunJobAsync(Job job, string key, string videoId, int height, string path)
        {
            try
            {
                var gate = job.IsPrewarm ? _prewarmGate : _demandGate;
                await gate.WaitAsync(job.Cancellation.Token);
                try
                {
                    while (job.IsPrewarm && (Volatile.Read(ref _demandJobs) > 0 || Volatile.Read(ref _activeClients) > 0))
                    {
                        await Task.Delay(TimeSpan.FromSeconds(2), job.Cancellation.Token);
                    }

                    lock (_lock)
                    {
                        job.Cancellation.Token.ThrowIfCancellationRequested();
                        job.Started = true;
                    }

                    return await ProduceAsync(videoId, height, path);
                }
                finally
                {
                    gate.Release();
                }
            }
            catch (OperationCanceledException)
            {
                return null;
            }
            finally
            {
                lock (_lock)
                {
                    if (_jobs.TryGetValue(key, out var current) && ReferenceEquals(current, job))
                    {
                        _jobs.Remove(key);
                    }

                    if (!job.IsPrewarm)
                    {
                        _demandJobs--;
                    }
                }
            }
        }

        private string PathFor(string videoId, int height) =>
            Path.Combine(_cacheDir, $"{videoId}_{height}.mp4");

        private static void Touch(string path)
        {
            try
            {
                File.SetLastWriteTimeUtc(path, DateTime.UtcNow);
            }
            catch
            {
            }
        }

        private async Task<string> ProduceAsync(string videoId, int height, string finalPath)
        {
            var name = $"{videoId}_{height}";
            var videoPath = Path.Combine(_tempDir, name + ".video.mp4");
            var audioPath = Path.Combine(_tempDir, name + ".audio.m4a");
            var outputPath = Path.Combine(_tempDir, name + ".out.mp4");
            try
            {
                if (File.Exists(finalPath))
                {
                    return finalPath;
                }

                var stopwatch = Stopwatch.StartNew();
                var manifest = await _youtubeClient.Videos.Streams.GetManifestAsync(videoId, validateStreams: false);
                var video = StreamSelector.SelectVideo(manifest, height);
                var audio = StreamSelector.SelectAudio(manifest, videoId);
                if (video == null || audio == null)
                {
                    ExceptionHandler.Handle(new InvalidOperationException($"No H.264 video or AAC audio stream for video '{videoId}'."));
                    return null;
                }

                await Task.WhenAll(
                    DownloadAsync(video, videoPath),
                    DownloadAsync(audio, audioPath));
                var downloaded = stopwatch.Elapsed;

                await MuxAsync(videoPath, audioPath, outputPath);
                File.Move(outputPath, finalPath, overwrite: true);

                Trace.TraceInformation(
                    $"Video {name}: {video.VideoQuality} {new FileInfo(finalPath).Length / 1048576} MB, download {downloaded.TotalSeconds:F0}s, total {stopwatch.Elapsed.TotalSeconds:F0}s.");
                Evict(finalPath);
                return finalPath;
            }
            catch (Exception exception)
            {
                ExceptionHandler.Handle(exception);
                return null;
            }
            finally
            {
                foreach (var path in new[] { videoPath, audioPath, outputPath })
                {
                    TryDelete(path);
                }

            }
        }

        // YouTube sometimes serves a chunk extremely slowly or not at all (a stall of minutes). Read with an
        // inactivity timeout and a minimum rate, and when the connection is stuck or crawling, reconnect at
        // the current offset. Still one connection at a time, like any ordinary client.
        private static readonly TimeSpan StallTimeout = TimeSpan.FromSeconds(15);
        private static readonly TimeSpan RateWindow = TimeSpan.FromSeconds(10);
        private const long MinBytesPerWindow = 500 * 1024;
        private const int MaxReconnects = 8;

        private async Task DownloadAsync(YoutubeExplode.Videos.Streams.IStreamInfo streamInfo, string path)
        {
            var length = streamInfo.Size.Bytes;
            var buffer = new byte[128 * 1024];
            var reconnects = 0;
            long written = 0;

            using (var file = File.Create(path))
            {
                while (written < length)
                {
                    using var input = await _youtubeClient.Videos.Streams.GetAsync(streamInfo);
                    input.Position = written;
                    var windowStart = Stopwatch.StartNew();
                    long windowBytes = 0;
                    try
                    {
                        while (written < length)
                        {
                            using var timeout = new CancellationTokenSource(StallTimeout);
                            var read = await input.ReadAsync(buffer, 0, buffer.Length, timeout.Token);
                            if (read == 0)
                            {
                                break;
                            }

                            await file.WriteAsync(buffer, 0, read);
                            written += read;
                            windowBytes += read;
                            if (windowStart.Elapsed >= RateWindow)
                            {
                                if (windowBytes < MinBytesPerWindow)
                                {
                                    throw new TimeoutException($"{windowBytes / 1024} KB in {RateWindow.TotalSeconds:F0} s");
                                }

                                windowBytes = 0;
                                windowStart.Restart();
                            }
                        }

                        if (written < length)
                        {
                            throw new IOException($"Stream ended at {written} of {length} bytes.");
                        }
                    }
                    catch (Exception exception) when (
                        (exception is OperationCanceledException || exception is TimeoutException || exception is IOException)
                        && ++reconnects <= MaxReconnects)
                    {
                        Trace.TraceWarning($"Download of {path} too slow or stuck at {written / 1048576} MB of {length / 1048576} MB ({exception.GetType().Name}); reconnecting ({reconnects}/{MaxReconnects}).");
                    }
                }
            }
        }

        private async Task MuxAsync(string videoPath, string audioPath, string outputPath)
        {
            var startInfo = new ProcessStartInfo(_options.FFmpegPath)
            {
                RedirectStandardError = true,
                RedirectStandardOutput = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            };
            foreach (var argument in new[]
            {
                "-nostdin", "-y", "-loglevel", "error",
                "-i", videoPath, "-i", audioPath,
                "-map", "0:v:0", "-map", "1:a:0",
                "-c", "copy", "-movflags", "+faststart", "-f", "mp4", outputPath,
            })
            {
                startInfo.ArgumentList.Add(argument);
            }

            using (var process = Process.Start(startInfo))
            {
                var error = process.StandardError.ReadToEndAsync();
                _ = process.StandardOutput.ReadToEndAsync();
                using (var timeout = new CancellationTokenSource(_ffmpegTimeout))
                {
                    try
                    {
                        await process.WaitForExitAsync(timeout.Token);
                    }
                    catch (OperationCanceledException)
                    {
                        try
                        {
                            process.Kill(entireProcessTree: true);
                        }
                        catch
                        {
                        }

                        throw new TimeoutException("ffmpeg timed out.");
                    }
                }

                if (process.ExitCode != 0)
                {
                    throw new InvalidOperationException($"ffmpeg failed ({process.ExitCode}): {await error}");
                }
            }
        }

        private void Evict(string keep)
        {
            lock (_evictionLock)
            {
                try
                {
                    var files = new DirectoryInfo(_cacheDir).GetFiles("*.mp4").OrderBy(file => file.LastWriteTimeUtc).ToList();
                    var total = files.Sum(file => file.Length);
                    foreach (var file in files)
                    {
                        if (total <= _options.CacheMaxBytes)
                        {
                            break;
                        }

                        if (string.Equals(file.FullName, keep, StringComparison.Ordinal))
                        {
                            continue;
                        }

                        if (TryDelete(file.FullName))
                        {
                            total -= file.Length;
                        }
                    }
                }
                catch (Exception exception)
                {
                    ExceptionHandler.Handle(exception);
                }
            }
        }

        private static bool TryDelete(string path)
        {
            try
            {
                File.Delete(path);
                return true;
            }
            catch
            {
                return false;
            }
        }
    }
}
