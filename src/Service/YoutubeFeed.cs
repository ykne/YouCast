using System;
using System.Collections.Generic;
using System.Linq;
using System.ServiceModel.Syndication;
using System.Threading.Tasks;
using System.Xml.Linq;
using Google.Apis.Services;
using Google.Apis.YouTube.v3.Data;
using MoreLinq;
using YoutubeExplode;
using YoutubeExplode.Videos.Streams;
using Video = Google.Apis.YouTube.v3.Data.Video;
using YouTubeService = Google.Apis.YouTube.v3.YouTubeService;

namespace Service
{
    public sealed class YoutubeFeed
    {
        private const string _channelUrlFormat = "http://www.youtube.com/channel/{0}";
        private const string _videoUrlFormat = "http://www.youtube.com/watch?v={0}";
        private const string _playlistUrlFormat = "http://www.youtube.com/playlist?list={0}";

        private readonly YoutubeClient _youtubeClient;
        private readonly YouTubeService _youtubeService;
        private readonly VideoCache _videoCache;
        private readonly FeedCacheOptions _cacheOptions;
        private readonly FeedStore _store;

        // Players such as iOS Podcasts fetch an episode in many small Range requests, each following our redirect.
        // Resolving the stream URL through YouTube takes seconds, so reuse it (YouTube URLs stay valid for hours).
        private readonly Cache<string, string> _audioUriCache = new Cache<string, string>(TimeSpan.FromMinutes(30));

        public YoutubeFeed(string applicationName, string apiKey, bool ipv4Only = true, string youtubeProxy = null, VideoOptions videoOptions = null, FeedCacheOptions feedCache = null)
        {
            _cacheOptions = feedCache ?? new FeedCacheOptions();
            _store = new FeedStore(_cacheOptions.Dir);
            _youtubeClient = new YoutubeClient(new System.Net.Http.HttpClient(HttpHandlers.Create(ipv4Only, youtubeProxy)));
            if (videoOptions != null)
            {
                _videoCache = new VideoCache(_youtubeClient, videoOptions);
            }
            _youtubeService =
                new YouTubeService(
                    new BaseClientService.Initializer
                    {
                        ApiKey = apiKey,
                        ApplicationName = applicationName,
                        HttpClientFactory = new GoogleHttpClientFactory(ipv4Only)
                    });
        }

        public async Task<ItunesFeed> GetUserFeedAsync(
            string baseAddress,
            string userId,
            string encoding,
            int maxLength,
            bool isPopular)
        {
            var channel = await GetCachedChannelAsync("channel:" + userId, LookupAsync);
            if (channel.NotFound)
            {
                throw new FeedNotFoundException($"Channel '{userId}' was not found.");
            }

            var arguments = new Arguments(channel.PlaylistId, encoding, maxLength, isPopular);
            return new ItunesFeed(
                GetTitle(channel.Title, arguments),
                channel.Description,
                new Uri(string.Format(_channelUrlFormat, channel.Id)))
            {
                ImageUrl = new Uri(channel.ImageUrl),
                Items = await GenerateItemsAsync(baseAddress, channel.PublishedAt, arguments, uploads: true)
            };

            async Task<CachedChannel> LookupAsync()
            {
                var found = await GetChannelAsync(userId) ?? await FindChannelAsync(userId);
                return found == null
                    ? null
                    : new CachedChannel
                    {
                        Id = found.Id,
                        PlaylistId = found.ContentDetails.RelatedPlaylists.Uploads,
                        Title = found.Snippet.Title,
                        Description = found.Snippet.Description,
                        ImageUrl = found.Snippet.Thumbnails.Medium.Url,
                        PublishedAt = found.Snippet.PublishedAtDateTimeOffset.GetValueOrDefault().UtcDateTime,
                    };
            }

            async Task<Channel> GetChannelAsync(string id)
            {
                var listRequestForId = _youtubeService.Channels.List("snippet,contentDetails");
                listRequestForId.Id = id;
                listRequestForId.MaxResults = 1;
                listRequestForId.Fields = "items(contentDetails,id,snippet)";

                var channelListResponse = await listRequestForId.ExecuteAsync();
                return channelListResponse.Items?.SingleOrDefault();
            }

            async Task<Channel> FindChannelAsync(string username)
            {
                var listRequestForUsername = _youtubeService.Channels.List("snippet,contentDetails");
                listRequestForUsername.ForUsername = username;
                listRequestForUsername.MaxResults = 1;
                listRequestForUsername.Fields = "items(contentDetails,id,snippet)";

                var channelListResponse = await listRequestForUsername.ExecuteAsync();
                return channelListResponse.Items?.SingleOrDefault();
            }
        }

        public async Task<ItunesFeed> GetPlaylistFeedAsync(
            string baseAddress,
            string playlistId,
            string encoding,
            int maxLength,
            bool isPopular)
        {
            var playlist = await GetCachedChannelAsync("playlist:" + playlistId, LookupAsync);
            if (playlist.NotFound)
            {
                throw new FeedNotFoundException($"Playlist '{playlistId}' was not found.");
            }

            var arguments = new Arguments(playlistId, encoding, maxLength, isPopular);
            return new ItunesFeed(
                GetTitle(playlist.Title, arguments),
                playlist.Description,
                new Uri(string.Format(_playlistUrlFormat, playlist.Id)))
            {
                ImageUrl = new Uri(playlist.ImageUrl),
                Items = await GenerateItemsAsync(baseAddress, playlist.PublishedAt, arguments, uploads: false)
            };

            async Task<CachedChannel> LookupAsync()
            {
                var playlistRequest = _youtubeService.Playlists.List("snippet");
                playlistRequest.Id = playlistId;
                playlistRequest.MaxResults = 1;

                var found = (await playlistRequest.ExecuteAsync()).Items?.FirstOrDefault();
                return found == null
                    ? null
                    : new CachedChannel
                    {
                        Id = found.Id,
                        PlaylistId = found.Id,
                        Title = found.Snippet.Title,
                        Description = found.Snippet.Description,
                        ImageUrl = found.Snippet.Thumbnails.Medium.Url,
                        PublishedAt = found.Snippet.PublishedAtDateTimeOffset.GetValueOrDefault().UtcDateTime,
                    };
            }
        }

        /// <summary>
        /// Path of an MP4 with H.264 video and AAC audio for the video (built on demand, see <see cref="VideoCache"/>),
        /// or null when video is unavailable (no ffmpeg, no suitable streams, failure).
        /// </summary>
        public IDisposable BeginVideoClient() => _videoCache?.BeginClient();

        public Task<string> GetVideoFileAsync(string videoId, string encoding) =>
            _videoCache == null
                ? Task.FromResult<string>(null)
                : _videoCache.GetAsync(videoId, StreamSelector.ParseHeight(encoding));

        public async Task<string> GetAudioUriAsync(string videoId)
        {
            if (_audioUriCache.TryGet(videoId, out var cachedUri))
            {
                return cachedUri;
            }

            var uri = await ResolveAudioUriAsync(videoId);
            if (uri != null)
            {
                _audioUriCache.Set(videoId, uri);
            }

            return uri;
        }

        private async Task<string> ResolveAudioUriAsync(string videoId)
        {
            try
            {
                // Skip YoutubeExplode's per-stream probing (about 100 streams, seconds of requests); check only the pick.
                var streamManifest = await _youtubeClient.Videos.Streams.GetManifestAsync(videoId, validateStreams: false);
                var audio = StreamSelector.SelectAudio(streamManifest, videoId);
                if (audio == null || !await _youtubeClient.Videos.Streams.IsAvailableAsync(audio))
                {
                    return null;
                }

                return audio.Url;
            }
            catch (Exception exception)
            {
                ExceptionHandler.Handle(exception);
                return null;
            }
        }

        private async Task<IEnumerable<SyndicationItem>> GenerateItemsAsync(
            string baseAddress,
            DateTime startDate,
            Arguments arguments,
            bool uploads)
        {
            var playlistItems = await GetPlaylistItemsAsync(arguments, uploads);
            var isVideo = _videoCache != null && arguments.Encoding != "Audio";
            if (isVideo)
            {
                var height = StreamSelector.ParseHeight(arguments.Encoding);
                foreach (var playlistItem in playlistItems.Take(_videoCache.PrewarmCount))
                {
                    _videoCache.Prewarm(playlistItem.VideoId, height);
                }
            }

            var userVideos = playlistItems.Select(_ => GenerateItem(_, baseAddress, arguments, isVideo));
            if (arguments.IsPopular)
            {
                userVideos = await SortByPopularityAsync(userVideos, playlistItems, startDate);
            }

            return userVideos;
        }

        // Channel and playlist details: cached (a day), so that a feed request does not start with one or two API calls;
        // unknown channels are remembered too. When YouTube cannot be asked, the last known details are used.
        private Task<CachedChannel> GetCachedChannelAsync(string key, Func<Task<CachedChannel>> lookup) =>
            _store.WithLockAsync(
                key,
                async () =>
                {
                    var now = DateTime.UtcNow;
                    var cached = _store.Get<CachedChannel>(key);
                    if (cached != null)
                    {
                        var interval = cached.NotFound ? _cacheOptions.NotFoundInterval : _cacheOptions.ChannelRefreshInterval;
                        if (now - cached.FetchedAt < interval || now < cached.RetryAt)
                        {
                            return cached;
                        }
                    }

                    CachedChannel fresh;
                    try
                    {
                        fresh = await lookup() ?? new CachedChannel { NotFound = true };
                    }
                    catch (Exception exception) when (cached is { NotFound: false } && exception is not OperationCanceledException)
                    {
                        Console.Error.WriteLine($"warn: YouCast.Cache: {key} could not be refreshed, using the saved details: {exception.Message}");
                        cached.RetryAt = now + _cacheOptions.RetryInterval;
                        return cached;
                    }

                    fresh.FetchedAt = now;
                    _store.Set(key, fresh);
                    return fresh;
                });

        private async Task<IList<CachedItem>> GetPlaylistItemsAsync(Arguments arguments, bool uploads)
        {
            if (arguments.IsPopular)
            {
                // Popularity ordering needs fresh view counts; not cached.
                var playlist = await FetchPlaylistAsync(arguments.PlaylistId, arguments.MaxLength);
                return Visible(playlist, arguments.MaxLength);
            }

            return await _store.WithLockAsync(
                "items:" + arguments.PlaylistId,
                async () => Visible(await RefreshPlaylistAsync(arguments.PlaylistId, arguments.MaxLength, uploads), arguments.MaxLength));
        }

        private static IList<CachedItem> Visible(CachedPlaylist playlist, int maxLength) =>
            playlist.Items.Where(_ => !_.Pending).Take(maxLength).ToList();

        // Within the refresh interval the saved list is served without asking YouTube. After it, a channel's uploads
        // (newest first) cost one request when nothing is new; a plain playlist is compared by its first page and size.
        // If YouTube cannot be asked (quota, network) the saved list is served and the error logged.
        private async Task<CachedPlaylist> RefreshPlaylistAsync(string playlistId, int maxLength, bool uploads)
        {
            var key = "items:" + playlistId;
            var now = DateTime.UtcNow;
            var cached = _store.Get<CachedPlaylist>(key);
            var needsDeeper = cached != null && !cached.Complete && cached.Items.Count(_ => !_.Pending) < maxLength;
            if (cached != null &&
                (now < cached.RetryAt || (!needsDeeper && now - cached.FetchedAt < _cacheOptions.RefreshInterval)))
            {
                return cached;
            }

            try
            {
                var updated = cached == null || needsDeeper || now >= cached.FullRefreshAt
                    ? await FetchPlaylistAsync(playlistId, maxLength)
                    : await UpdatePlaylistAsync(cached, playlistId, maxLength, uploads);
                _store.Set(key, updated);
                return updated;
            }
            catch (Exception exception) when (cached != null && exception is not OperationCanceledException)
            {
                Console.Error.WriteLine($"warn: YouCast.Cache: {playlistId} could not be refreshed, serving the saved feed: {exception.Message}");
                cached.RetryAt = now + _cacheOptions.RetryInterval;
                return cached;
            }
        }

        private async Task<CachedPlaylist> FetchPlaylistAsync(string playlistId, int maxLength)
        {
            var result = new CachedPlaylist();
            var visible = 0;
            var nextPageToken = string.Empty;
            while (nextPageToken != null && visible < maxLength)
            {
                var page = await GetPageAsync(playlistId, nextPageToken);
                if (nextPageToken.Length == 0)
                {
                    result.TotalResults = page.PageInfo?.TotalResults ?? 0;
                }

                var items = await ToCachedItemsAsync(page.Items);
                result.Items.AddRange(items);
                visible += items.Count(_ => !_.Pending);
                nextPageToken = page.NextPageToken;
            }

            var now = DateTime.UtcNow;
            result.Complete = nextPageToken == null;
            result.FetchedAt = now;
            result.FullRefreshAt = now + _cacheOptions.FullRefreshInterval + TimeSpan.FromMinutes(Random.Shared.Next(0, 12 * 60));
            return result;
        }

        private async Task<CachedPlaylist> UpdatePlaylistAsync(CachedPlaylist cached, string playlistId, int maxLength, bool uploads)
        {
            // Streams that were live or announced: show them once they are normal videos, drop them when they are gone.
            var pending = cached.Items.Where(_ => _.Pending).Take(50).ToList();
            if (pending.Count > 0)
            {
                var states = await GetVideoStatesAsync(pending.Select(_ => _.VideoId));
                foreach (var item in pending)
                {
                    if (states.TryGetValue(item.VideoId, out var isLive))
                    {
                        item.Pending = isLive;
                    }
                    else
                    {
                        cached.Items.Remove(item);
                    }
                }
            }

            var first = await GetPageAsync(playlistId, string.Empty);
            var total = first.PageInfo?.TotalResults ?? 0;
            var firstItems = first.Items ?? new List<PlaylistItem>();
            if (!uploads)
            {
                var count = Math.Min(firstItems.Count, cached.Items.Count);
                var same = total == cached.TotalResults &&
                    firstItems.Take(count).Select(_ => _.Snippet.ResourceId.VideoId).SequenceEqual(cached.Items.Take(count).Select(_ => _.VideoId));
                if (!same)
                {
                    return await FetchPlaylistAsync(playlistId, maxLength);
                }

                cached.FetchedAt = DateTime.UtcNow;
                return cached;
            }

            // Uploads are newest first: everything in front of the first known video is new.
            var known = cached.Items.Select(_ => _.VideoId).ToHashSet();
            var newItems = new List<PlaylistItem>();
            var page = first;
            var foundKnown = false;
            while (true)
            {
                foreach (var item in page.Items ?? new List<PlaylistItem>())
                {
                    if (known.Contains(item.Snippet.ResourceId.VideoId))
                    {
                        foundKnown = true;
                        break;
                    }

                    newItems.Add(item);
                }

                if (foundKnown || page.NextPageToken == null)
                {
                    break;
                }

                page = await GetPageAsync(playlistId, page.NextPageToken);
            }

            var added = new List<CachedItem>();
            foreach (var batch in newItems.Batch(50))
            {
                added.AddRange(await ToCachedItemsAsync(batch.ToList()));
            }

            var kept = cached.Items;
            if (!foundKnown)
            {
                // Nothing of the saved list is in the playlist any more: it was read to the end, so this is the whole playlist.
                kept = new List<CachedItem>();
                cached.Complete = true;
            }
            else
            {
                // Videos inside the range of the first page that are no longer returned were deleted or made private.
                var firstIds = firstItems.Select(_ => _.Snippet.ResourceId.VideoId).ToHashSet();
                var lastKnown = kept.FindLastIndex(_ => firstIds.Contains(_.VideoId));
                kept = kept.Where((item, index) => index > lastKnown || firstIds.Contains(item.VideoId)).ToList();
            }

            added.AddRange(kept);
            cached.Items = added;
            cached.TotalResults = total;
            cached.FetchedAt = DateTime.UtcNow;
            return cached;
        }

        private async Task<PlaylistItemListResponse> GetPageAsync(string playlistId, string pageToken)
        {
            var request = _youtubeService.PlaylistItems.List("snippet");
            request.PlaylistId = playlistId;
            request.MaxResults = 50;
            request.PageToken = pageToken;
            request.Fields = "items(id,snippet),nextPageToken,pageInfo";
            return await request.ExecuteAsync();
        }

        private async Task<List<CachedItem>> ToCachedItemsAsync(IList<PlaylistItem> items)
        {
            if (items == null || items.Count == 0)
            {
                return new List<CachedItem>();
            }

            var states = await GetVideoStatesAsync(items.Select(_ => _.Snippet.ResourceId.VideoId));
            return items.Select(_ => new CachedItem
            {
                VideoId = _.Snippet.ResourceId.VideoId,
                Title = _.Snippet.Title,
                Description = _.Snippet.Description,
                PublishedAt = _.Snippet.PublishedAtDateTimeOffset.GetValueOrDefault().UtcDateTime,
                Pending = states.TryGetValue(_.Snippet.ResourceId.VideoId, out var isLive) && isLive,
            }).ToList();
        }

        // Live streams and announced (upcoming) broadcasts have no media streams yet, so serving them would only
        // give 404s. They are left out of the feed (Pending) and show up once YouTube has turned them into normal videos.
        // One videos.list call per page of 50 items (1 quota unit). Videos YouTube does not return (deleted, private) are absent from the result.
        private async Task<Dictionary<string, bool>> GetVideoStatesAsync(IEnumerable<string> videoIds)
        {
            var request = _youtubeService.Videos.List("snippet");
            request.Id = string.Join(",", videoIds.Distinct());
            request.MaxResults = 50;
            request.Fields = "items(id,snippet/liveBroadcastContent)";
            var response = await request.ExecuteAsync();
            return (response.Items ?? new List<Video>()).
                GroupBy(_ => _.Id).
                ToDictionary(_ => _.Key, _ => _.First().Snippet?.LiveBroadcastContent is "live" or "upcoming");
        }

        private static SyndicationItem GenerateItem(CachedItem playlistItem, string baseAddress, Arguments arguments, bool isVideo)
        {
            var item = new SyndicationItem(
                playlistItem.Title,
                string.Empty,
                new Uri(string.Format(_videoUrlFormat, playlistItem.VideoId)))
            {
                Id = playlistItem.VideoId,
                PublishDate = playlistItem.PublishedAt,
                Summary = new TextSyndicationContent(playlistItem.Description),
            };

            // YouTube no longer offers muxed audio+video streams: video is built on demand (see VideoCache). Without
            // video support every encoding (including legacy MP4_* subscriptions) is served as MP4/AAC audio.
            var videoId = playlistItem.VideoId;
            item.ElementExtensions.Add(
                new XElement(
                    "enclosure",
                    new XAttribute("type", isVideo ? "video/mp4" : "audio/mp4"),
                    new XAttribute(
                        "url",
                        isVideo
                            ? baseAddress + $"/Video.mp4?videoId={videoId}&encoding={arguments.Encoding}"
                            : baseAddress + $"/Audio.m4a?videoId={videoId}")).CreateReader());

            return item;
        }

        private async Task<IEnumerable<SyndicationItem>> SortByPopularityAsync(
            IEnumerable<SyndicationItem> userVideos,
            IEnumerable<CachedItem> playlistItems,
            DateTime startDate)
        {
            var videos = await GetVideosAsync(playlistItems.Select(_ => _.VideoId).Distinct());
            var videoDictionary = videos.ToDictionary(_ => _.Id, _ => _);
            userVideos = userVideos.
                OrderByDescending(_ => videoDictionary[_.Id].Statistics.ViewCount.GetValueOrDefault()).
                ToList();
            var i = 0;
            foreach (var userVideo in userVideos)
            {
                userVideo.PublishDate = startDate.AddDays(i);
                i++;
                userVideo.Title = new TextSyndicationContent($"{i}. {userVideo.Title.Text}");
            }

            return userVideos;
        }

        private async Task<IEnumerable<Video>> GetVideosAsync(IEnumerable<string> videoIds) =>
            (await Task.WhenAll(videoIds.Batch(50).Select(GetVideoBatchAsync))).SelectMany(_ => _);

        private async Task<IEnumerable<Video>> GetVideoBatchAsync(IEnumerable<string> videoIds)
        {
            var statisticsRequest = _youtubeService.Videos.List("statistics");
            statisticsRequest.Id = string.Join(",", videoIds);
            statisticsRequest.MaxResults = 50;
            statisticsRequest.Fields = "items(id,statistics)";
            return (await statisticsRequest.ExecuteAsync()).Items;
        }

        private static string GetTitle(string title, Arguments arguments) =>
            arguments.IsPopular ? $"{title} (By Popularity)" : title;
    }
}
