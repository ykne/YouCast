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

        // Players such as iOS Podcasts fetch an episode in many small Range requests, each following our redirect.
        // Resolving the stream URL through YouTube takes seconds, so reuse it (YouTube URLs stay valid for hours).
        private readonly Cache<string, string> _audioUriCache = new Cache<string, string>(TimeSpan.FromMinutes(30));

        public YoutubeFeed(string applicationName, string apiKey, bool ipv4Only = true, string youtubeProxy = null, VideoOptions videoOptions = null)
        {
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
            return await GetFeedAsync();

            async Task<ItunesFeed> GetFeedAsync()
            {
                var channel =
                    await GetChannelAsync(userId) ??
                    await FindChannelAsync(userId) ??
                    throw new FeedNotFoundException($"Channel '{userId}' was not found.");

                var arguments = new Arguments(
                    channel.ContentDetails.RelatedPlaylists.Uploads,
                    encoding,
                    maxLength,
                    isPopular);

                return new ItunesFeed(
                    GetTitle(channel.Snippet.Title, arguments),
                    channel.Snippet.Description,
                    new Uri(string.Format(_channelUrlFormat, channel.Id)))
                {
                    ImageUrl = new Uri(channel.Snippet.Thumbnails.Medium.Url),
                    Items = await GenerateItemsAsync(
                        baseAddress,
                        channel.Snippet.PublishedAtDateTimeOffset.GetValueOrDefault().UtcDateTime,
                        arguments)
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
            return await GetFeedAsync();

            async Task<ItunesFeed> GetFeedAsync()
            {
                var arguments =
                    new Arguments(
                        playlistId,
                        encoding,
                        maxLength,
                        isPopular);

                var playlistRequest = _youtubeService.Playlists.List("snippet");
                playlistRequest.Id = playlistId;
                playlistRequest.MaxResults = 1;

                var playlist =
                    (await playlistRequest.ExecuteAsync()).Items?.FirstOrDefault() ??
                    throw new FeedNotFoundException($"Playlist '{playlistId}' was not found.");

                return new ItunesFeed(
                    GetTitle(playlist.Snippet.Title, arguments),
                    playlist.Snippet.Description,
                    new Uri(string.Format(_playlistUrlFormat, playlist.Id)))
                {
                    ImageUrl = new Uri(playlist.Snippet.Thumbnails.Medium.Url),
                    Items = await GenerateItemsAsync(
                        baseAddress,
                        playlist.Snippet.PublishedAtDateTimeOffset.GetValueOrDefault().UtcDateTime,
                        arguments)
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
            Arguments arguments)
        {
            IEnumerable<PlaylistItem> playlistItems = (await GetPlaylistItemsAsync(arguments)).ToList();
            var isVideo = _videoCache != null && arguments.Encoding != "Audio";
            if (isVideo)
            {
                var height = StreamSelector.ParseHeight(arguments.Encoding);
                foreach (var playlistItem in playlistItems.Take(_videoCache.PrewarmCount))
                {
                    _videoCache.Prewarm(playlistItem.Snippet.ResourceId.VideoId, height);
                }
            }

            var userVideos = playlistItems.Select(_ => GenerateItem(_, baseAddress, arguments, isVideo));
            if (arguments.IsPopular)
            {
                userVideos = await SortByPopularityAsync(userVideos, playlistItems, startDate);
            }

            return userVideos;
        }

        private async Task<IEnumerable<PlaylistItem>> GetPlaylistItemsAsync(Arguments arguments)
        {
            var playlistItems = new List<PlaylistItem>();
            var nextPageToken = string.Empty;
            while (nextPageToken != null && playlistItems.Count < arguments.MaxLength)
            {
                var playlistItemsListRequest = _youtubeService.PlaylistItems.List("snippet");
                playlistItemsListRequest.PlaylistId = arguments.PlaylistId;
                playlistItemsListRequest.MaxResults = 50;
                playlistItemsListRequest.PageToken = nextPageToken;
                playlistItemsListRequest.Fields = "items(id,snippet),nextPageToken";

                var playlistItemsListResponse = await playlistItemsListRequest.ExecuteAsync();
                var notLive = await ExcludeLiveAsync(playlistItemsListResponse.Items);
                playlistItems.AddRange(notLive);
                nextPageToken = playlistItemsListResponse.NextPageToken;
            }

            return playlistItems.Take(arguments.MaxLength);
        }

        // Live streams and announced (upcoming) broadcasts have no media streams yet, so serving them would only
        // give 404s. They are left out of the feed and show up once YouTube has turned them into normal videos.
        // One videos.list call per page of 50 items (1 quota unit).
        private async Task<IList<PlaylistItem>> ExcludeLiveAsync(IList<PlaylistItem> items)
        {
            if (items.Count == 0)
            {
                return items;
            }

            var request = _youtubeService.Videos.List("snippet");
            request.Id = string.Join(",", items.Select(_ => _.Snippet.ResourceId.VideoId).Distinct());
            request.MaxResults = 50;
            request.Fields = "items(id,snippet/liveBroadcastContent)";
            var live = (await request.ExecuteAsync()).Items.
                Where(_ => _.Snippet?.LiveBroadcastContent is "live" or "upcoming").
                Select(_ => _.Id).
                ToHashSet();
            return live.Count == 0
                ? items
                : items.Where(_ => !live.Contains(_.Snippet.ResourceId.VideoId)).ToList();
        }

        private static SyndicationItem GenerateItem(PlaylistItem playlistItem, string baseAddress, Arguments arguments, bool isVideo)
        {
            var item = new SyndicationItem(
                playlistItem.Snippet.Title,
                string.Empty,
                new Uri(string.Format(_videoUrlFormat, playlistItem.Snippet.ResourceId.VideoId)))
            {
                Id = playlistItem.Snippet.ResourceId.VideoId,
                PublishDate = playlistItem.Snippet.PublishedAtDateTimeOffset.GetValueOrDefault().UtcDateTime,
                Summary = new TextSyndicationContent(playlistItem.Snippet.Description),
            };

            // YouTube no longer offers muxed audio+video streams: video is built on demand (see VideoCache). Without
            // video support every encoding (including legacy MP4_* subscriptions) is served as MP4/AAC audio.
            var videoId = playlistItem.Snippet.ResourceId.VideoId;
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
            IEnumerable<PlaylistItem> playlistItems,
            DateTime startDate)
        {
            var videos = await GetVideosAsync(playlistItems.Select(_ => _.Snippet.ResourceId.VideoId).Distinct());
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
