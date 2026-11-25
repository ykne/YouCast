# Installing YouCast as a service

YouCast is a headless ASP.NET Core service (.NET 10). It serves YouTube channels and playlists as
**audio** podcast feeds (MP4/AAC, which iOS podcast apps require). Video feeds are not supported:
YouTube no longer offers combined audio+video streams.

## Linux (RPM, systemd)

Build (needs `dotnet-sdk-10.0` and `rpm-build`; paths follow your `%_topdir`, `~/rpmbuild` by default):
```
git archive --prefix=youcast-3.0.0/ -o "$(rpm --eval %_sourcedir)/youcast-3.0.0.tar.gz" HEAD
# the build uses the YoutubeExplode fork from the submodule (clone with --recurse-submodules, or: git submodule update --init):
git -C third_party/YoutubeExplode archive --prefix=YoutubeExplode/ -o "$(rpm --eval %_sourcedir)/youtubeexplode-fast-manifest.tar.gz" HEAD
rpmbuild -ba packaging/youcast.spec
```

Install (the target needs `aspnetcore-runtime-10.0`; dnf pulls it in on Fedora and RHEL/Oracle Linux 8+):
```
sudo dnf install ./youcast-3.0.0-8.x86_64.rpm
sudo editor /etc/youcast.conf              # set ApiKey, see "Configuration" below
sudo systemctl enable --now youcast        # the package does NOT enable the service itself
sudo firewall-cmd --permanent --add-service=youcast && sudo firewall-cmd --reload
```

`/etc/youcast.conf` is `root:youcast` mode 0640 and is kept across upgrades. After editing it, run
`sudo systemctl restart youcast`.

Check it:
```
systemctl status youcast
journalctl -u youcast -f
curl 'http://localhost:22703/FeedService/GetUserFeed?userId=CHANNEL_ID&encoding=Audio&maxLength=3&isPopular=false'
```

The package installs the app in `/usr/libexec/youcast` on purpose: that location gets the `bin_t` SELinux
label, so systemd runs it as `unconfined_service_t`, which may open outbound HTTPS connections.
Under `/usr/lib` it would run as `init_t`, and every request to YouTube would fail with "Permission denied".
No SELinux policy module is needed.

Not supported: RHEL/Oracle Linux 7 (glibc 2.17 is too old for .NET 10). Run YouCast on a newer host
and use `YouTubeProxy` (below) if that host's address is blocked by YouTube.

## Video feeds
`encoding=MP4_360p` and `MP4_720p` give video again. YouTube only offers separate video and audio streams, so on the first
request of an episode YouCast downloads an H.264 video stream (at most the requested height) and the AAC audio track, joins
them with `ffmpeg -c copy` (no transcoding, little CPU) into an MP4 with the index at the front, and serves it with Range
support from `CacheDir`. The first play of a long episode waits while this happens (a 1.5 h episode is ~250 MB at 360p; about
40 s measured at 8 MB/s), later plays are immediate. The newest `PrewarmCount` episodes are prepared when the feed is
requested, so they are usually ready. If joining fails, the audio stream is served instead.

Needs `ffmpeg`: `ffmpeg-free` from EPEL on RHEL/Oracle Linux 9 (`dnf install epel-release` or enable
`ol9_developer_EPEL`), `ffmpeg-free` on Fedora. With `YouTubeProxy` set, every downloaded byte passes through the proxy.

## Configuration

`/etc/youcast.conf` (Linux) or `%ProgramData%\YouCast\youcast.conf` (Windows): `key = value` lines,
`#` comments. Environment variables (`YouCast__ApiKey=...`) and `--Key=value` arguments override the file;
`--conf <path>` selects another file. A commented example is in `youcast.conf.example`.

| Key | Default | Meaning |
|---|---|---|
| `ApiKey` | (required) | YouTube Data API v3 key. The service exits at start-up without it. |
| `ApplicationName` | `YouCast` | Name sent to the Google API. |
| `Host` | `0.0.0.0` | Listen address (`::` for all IPv6). |
| `Port` | `22703` | Listen port. |
| `PublicBaseUrl` | from the request | Base URL written into feeds, e.g. behind a reverse proxy. |
| `IPv4Only` | `true` | Connect to Google/YouTube over IPv4 only. Turn off only if your API key allows your IPv6 address. |
| `YouTubeProxy` | none | `http://`, `https://` or `socks5://` proxy for **YouTube stream lookups and downloads only**. |
| `FFmpegPath` | `ffmpeg` | ffmpeg used to join video and audio. If it is missing, video requests are served as audio. |
| `CacheDir` | `/var/cache/youcast` | Where joined video files are kept (the RPM's unit creates it via `CacheDirectory=`). |
| `CacheMaxGB` | `10` | Cache size limit; the least recently used files are deleted first. |
| `CacheMaxAgeHours` | `12` | A cached video not requested for this long is deleted (`0` = keep until the size limit is reached). |
| `MaxConcurrentMuxes` | `2` | How many videos are downloaded and joined at the same time. |
| `PrewarmCount` | `2` | Newest episodes prepared in the background when a video feed is requested (`0` = off). |
| `FeedRefreshMinutes` | `10` | Feed lists are cached (memory and `CacheDir/feeds`). Within this time a feed costs no YouTube API quota; after it one request looks for new videos. If YouTube cannot be asked (quota, network) the saved feed is served and a warning logged. |
| `FeedFullRefreshDays` | `7` | A cached feed is rebuilt from scratch this often (picks up deleted videos and edited titles). |

### API key
Create a key in the Google Cloud Console (APIs & Services, Credentials) in a project where
**YouTube Data API v3** is enabled. If you restrict the key by IP address, allow the address the service
connects from: the server's public IPv4, and its IPv6 as well if `IPv4Only = false`. Restriction changes can
take a few minutes to apply. Google reports a wrong restriction as `403 API_KEY_IP_ADDRESS_BLOCKED`, which
YouCast returns as `502 YouTube API error (403) ...`.

### YouTube proxy
YouTube blocks many hosting-provider addresses with "Sign in to confirm you're not a bot". Symptoms:
`Audio.m4a` returns 404 and the journal shows `VideoUnavailableException: Video '...' is not available`,
while feed listings still work. Fix: route the stream lookups through a host that YouTube accepts.
```
YouTubeProxy = socks5://proxy.example.com:1080
```
- Only YoutubeExplode traffic uses the proxy. Feed listings still go straight to the Google API, so an
  IP-restricted API key keeps working from the server's own address.
- The proxy must accept connections from the YouCast host without a login (the firewall in front of it too).
  YouCast does not send proxy credentials.
- YouTube issues the stream URL for the **proxy's** address, but it can be downloaded from any address, so
  podcast apps on other networks work.

## Container
`packaging/Containerfile` installs the RPM into an AlmaLinux 10 image (EPEL is enabled for `ffmpeg-free`) and runs the
service as the unprivileged `youcast` user. Build and run commands are at the top of the file. There is no systemd in the
container, so settings come from a mounted `youcast.conf` (or `YouCast__*` environment variables) and the cache is a volume.
The service user is UID/GID 10017. For EL7 hosts with the old podman 1.4.4 see `CONTAINER-EL7.md`.

## Linux (manual)
```
cd src/YouCast.Server
dotnet publish -c Release -o /tmp/youcast-publish
sudo install -d /usr/libexec/youcast && sudo cp -r /tmp/youcast-publish/. /usr/libexec/youcast/
getent group youcast >/dev/null || sudo groupadd -r youcast
getent passwd youcast >/dev/null || sudo useradd -r -g youcast -d /var/empty -s /sbin/nologin youcast
sudo install -m 0640 -o root -g youcast ../../packaging/youcast.conf.example /etc/youcast.conf
sudo install -m 0644 ../../packaging/youcast.service /etc/systemd/system/
sudo install -m 0644 ../../packaging/youcast.xml /etc/firewalld/services/
sudo editor /etc/youcast.conf              # set ApiKey
sudo systemctl daemon-reload && sudo systemctl enable --now youcast
sudo firewall-cmd --reload && sudo firewall-cmd --permanent --add-service=youcast && sudo firewall-cmd --reload
```

## Windows (future)
`UseWindowsService` is already wired in, but this has not been tested. Publish with `-r win-x64`, copy to e.g.
`C:\Program Files\YouCast`, put `youcast.conf` in `%ProgramData%\YouCast\`, then:
`sc create YouCast binPath= "C:\Program Files\YouCast\youcast.exe" start= auto`
and allow the port in Windows Firewall.

## Feed URLs
```
http://HOST:22703/FeedService/GetUserFeed?userId=CHANNEL_ID_OR_NAME&encoding=Audio&maxLength=0&isPopular=false
http://HOST:22703/FeedService/GetPlaylistFeed?playlistId=PLAYLIST_ID&encoding=Audio&maxLength=0&isPopular=false
```
`maxLength=0` means unlimited. `isPopular=true` sorts by view count (numbered titles, synthetic dates).
