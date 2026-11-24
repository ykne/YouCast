using System.IO.Compression;
using System.ServiceModel.Syndication;
using System.Text;
using System.Xml;
using Google;
using Microsoft.AspNetCore.ResponseCompression;
using Service;
using YouCast.Server;

var builder = WebApplication.CreateBuilder(new WebApplicationOptions { Args = args, ContentRootPath = AppContext.BaseDirectory });

// Precedence (low -> high): appsettings.json, youcast.conf, environment (YouCast__Key), command line.
// Config file: /etc/youcast.conf on Linux, %ProgramData%\YouCast\youcast.conf on Windows, or --conf <path>.
var confPath = builder.Configuration["conf"] ?? ConfFile.DefaultPath;
if (builder.Configuration["conf"] != null && !File.Exists(confPath))
{
    Console.Error.WriteLine($"Config file not found: {confPath}");
    return 1;
}

builder.Configuration.Sources.Clear();
builder.Configuration.
    AddJsonFile("appsettings.json", optional: true).
    AddConfFile(confPath).
    AddEnvironmentVariables().
    AddCommandLine(args);

// Timestamps (UTC) on the log lines; when started by systemd the journal adds its own.
if (!Microsoft.Extensions.Hosting.Systemd.SystemdHelpers.IsSystemdService())
{
    builder.Logging.AddSimpleConsole(options =>
    {
        options.TimestampFormat = "yyyy-MM-dd HH:mm:ss'Z' ";
        options.UseUtcTimestamp = true;
    });
}

// No-ops unless actually started by systemd / the Windows Service Control Manager.
builder.Host.UseSystemd();
builder.Host.UseWindowsService(options => options.ServiceName = "YouCast");

var apiKey = builder.Configuration["YouCast:ApiKey"];
if (string.IsNullOrWhiteSpace(apiKey))
{
    Console.Error.WriteLine($"YouCast:ApiKey is not set (config file: {confPath}).");
    return 1;
}

var listenHost = builder.Configuration["YouCast:Host"] ?? "0.0.0.0";
var port = builder.Configuration.GetValue("YouCast:Port", 22703);
builder.WebHost.UseUrls($"http://{(listenHost.Contains(':') ? $"[{listenHost}]" : listenHost)}:{port}");
var publicBaseUrl = builder.Configuration["YouCast:PublicBaseUrl"]?.TrimEnd('/');

// Video needs ffmpeg (stream copy only) and a writable cache directory; without them video requests get audio.
VideoOptions videoOptions = null;
var ffmpegPath = builder.Configuration["YouCast:FFmpegPath"] ?? "ffmpeg";
if (VideoOptions.IsFFmpegAvailable(ffmpegPath))
{
    videoOptions = new VideoOptions
    {
        FFmpegPath = ffmpegPath,
        CacheDir = builder.Configuration["YouCast:CacheDir"] ?? VideoOptions.DefaultCacheDir,
        CacheMaxBytes = (long)(builder.Configuration.GetValue("YouCast:CacheMaxGB", 10.0) * 1024 * 1024 * 1024),
        CacheMaxAge = TimeSpan.FromHours(builder.Configuration.GetValue("YouCast:CacheMaxAgeHours", 12.0)),
        MaxConcurrentMuxes = builder.Configuration.GetValue("YouCast:MaxConcurrentMuxes", 2),
        PrewarmCount = builder.Configuration.GetValue("YouCast:PrewarmCount", 2),
    };
}
else
{
    Console.Error.WriteLine($"ffmpeg not found ('{ffmpegPath}'): video feeds disabled, serving audio instead.");
}

// Feed lists are kept (in memory and as files under <CacheDir>/feeds) so that YouTube API quota is spent only on what changed.
var feedCache = new FeedCacheOptions
{
    Dir = Path.Combine(builder.Configuration["YouCast:CacheDir"] ?? VideoOptions.DefaultCacheDir, "feeds"),
    RefreshInterval = TimeSpan.FromMinutes(builder.Configuration.GetValue("YouCast:FeedRefreshMinutes", 10.0)),
    FullRefreshInterval = TimeSpan.FromDays(builder.Configuration.GetValue("YouCast:FeedFullRefreshDays", 7.0)),
};

YoutubeFeed CreateFeed()
{
    try
    {
        return new YoutubeFeed(
            builder.Configuration["YouCast:ApplicationName"] ?? "YouCast",
            apiKey,
            builder.Configuration.GetValue("YouCast:IPv4Only", true),
            builder.Configuration["YouCast:YouTubeProxy"],
            videoOptions,
            feedCache);
    }
    catch (Exception exception) when (videoOptions != null && exception is IOException or UnauthorizedAccessException)
    {
        Console.Error.WriteLine($"Video cache directory '{videoOptions.CacheDir}' is not usable ({exception.Message}): video feeds disabled, serving audio instead.");
        return new YoutubeFeed(
            builder.Configuration["YouCast:ApplicationName"] ?? "YouCast",
            apiKey,
            builder.Configuration.GetValue("YouCast:IPv4Only", true),
            builder.Configuration["YouCast:YouTubeProxy"],
            feedCache: feedCache);
    }
}

builder.Services.AddSingleton(CreateFeed());

// Feeds are large XML (a 1000-item feed is ~2.7 MB) and compress about 5-8x. Only the feeds are compressed: video/mp4 stays as it is
// (it is served with Range support), and the short text/plain error bodies are not worth it. iOS offers gzip and deflate, no Brotli.
builder.Services.AddResponseCompression(options =>
{
    options.MimeTypes = new[] { "application/rss+xml" };
    options.Providers.Add<GzipCompressionProvider>();
});
builder.Services.Configure<GzipCompressionProviderOptions>(options => options.Level = CompressionLevel.Optimal);

var app = builder.Build();

app.UseResponseCompression();

// Turn upstream failures into short, accurate responses instead of unhandled 500s with stack traces.
app.Use(async (context, next) =>
{
    try
    {
        await next();
    }
    catch (Exception exception) when (!context.Response.HasStarted)
    {
        var logger = context.RequestServices.GetRequiredService<ILoggerFactory>().CreateLogger("YouCast");
        var (status, message) = exception switch
        {
            FeedNotFoundException notFound => (StatusCodes.Status404NotFound, notFound.Message),
            GoogleApiException google => (
                StatusCodes.Status502BadGateway,
                $"YouTube API error ({(int)google.HttpStatusCode}): {google.Error?.Message ?? google.Message}"),
            HttpRequestException => (StatusCodes.Status502BadGateway, "Could not reach YouTube."),
            _ => (StatusCodes.Status500InternalServerError, "Internal error."),
        };

        if (status == StatusCodes.Status500InternalServerError)
        {
            logger.LogError(exception, "Unhandled error for {Path}", context.Request.Path);
        }
        else
        {
            logger.LogWarning("{Path} -> {Status}: {Message}", context.Request.Path, status, message);
        }

        context.Response.Clear();
        context.Response.StatusCode = status;
        context.Response.ContentType = "text/plain; charset=utf-8";
        await context.Response.WriteAsync(message);
    }
});
// One log line per media request (what the podcast app asked for and what it got), to diagnose playback problems.
app.Use(async (context, next) =>
{
    var path = context.Request.Path;
    if (!path.StartsWithSegments("/FeedService/Video.mp4") && !path.StartsWithSegments("/FeedService/Audio.m4a"))
    {
        await next();
        return;
    }

    var stopwatch = System.Diagnostics.Stopwatch.StartNew();
    using var client = path.StartsWithSegments("/FeedService/Video.mp4")
        ? context.RequestServices.GetRequiredService<YoutubeFeed>().BeginVideoClient()
        : null;
    try
    {
        await next();
    }
    finally
    {
        var request = context.Request;
        var logger = context.RequestServices.GetRequiredService<ILoggerFactory>().CreateLogger("YouCast.Media");
        logger.LogInformation(
            "{Method} {Path}{Query} range={Range} -> {Status} length={Length} in {Elapsed} ms{Aborted} ua={UserAgent}",
            request.Method,
            path.Value,
            request.QueryString.Value,
            request.Headers.Range.ToString(),
            context.Response.StatusCode,
            context.Response.ContentLength,
            stopwatch.ElapsedMilliseconds,
            context.RequestAborted.IsCancellationRequested ? " (client aborted)" : string.Empty,
            request.Headers.UserAgent.ToString());
    }
});

var feed = app.MapGroup("/FeedService");

feed.MapGet("/GetUserFeed", async (HttpContext http, YoutubeFeed youtube, string userId, string encoding, int maxLength, bool isPopular) =>
    Rss(await youtube.GetUserFeedAsync(BaseAddress(http, publicBaseUrl), userId, encoding, maxLength, isPopular)));

feed.MapGet("/GetPlaylistFeed", async (HttpContext http, YoutubeFeed youtube, string playlistId, string encoding, int maxLength, bool isPopular) =>
    Rss(await youtube.GetPlaylistFeedAsync(BaseAddress(http, publicBaseUrl), playlistId, encoding, maxLength, isPopular)));

// Video is built on demand (H.264 + AAC joined with ffmpeg, cached on disk, served with Range support).
// If that is not possible the audio stream is served instead, so that something still plays.
feed.MapMethods("/Video.mp4", new[] { "GET", "HEAD" }, async (YoutubeFeed youtube, string videoId, string encoding) =>
{
    var path = await youtube.GetVideoFileAsync(videoId, encoding);
    return path != null
        ? Results.File(path, "video/mp4", enableRangeProcessing: true)
        : RedirectOrNotFound(await youtube.GetAudioUriAsync(videoId));
});

feed.MapMethods("/Audio.m4a", new[] { "GET", "HEAD" }, async (YoutubeFeed youtube, string videoId) =>
    RedirectOrNotFound(await youtube.GetAudioUriAsync(videoId)));

app.Run();
return 0;

static string BaseAddress(HttpContext http, string publicBaseUrl) =>
    $"{publicBaseUrl ?? $"{http.Request.Scheme}://{http.Request.Host}{http.Request.PathBase}"}/FeedService";

static IResult RedirectOrNotFound(string uri) =>
    uri == null ? Results.NotFound() : Results.Redirect(uri);

static IResult Rss(SyndicationFeed feed)
{
    using var stream = new MemoryStream();
    using (var writer = XmlWriter.Create(stream, new XmlWriterSettings { Encoding = new UTF8Encoding(false), Async = false }))
    {
        feed.GetRss20Formatter().WriteTo(writer);
    }

    return Results.Bytes(stream.ToArray(), "application/rss+xml; charset=utf-8");
}
