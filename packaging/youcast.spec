%global debug_package %{nil}
%global __os_install_post %{nil}
%global ye_ref fast-manifest

Name:           youcast
Version:        3.0.0
Release:        9
Summary:        Serve YouTube channels and playlists as podcast feeds
License:        GPL-2.0-only AND MIT
URL:            https://github.com/i3arnon/YouCast
Source0:        %{name}-%{version}.tar.gz
# YoutubeExplode fork, submodule third_party/YoutubeExplode (branch %{ye_ref}): GetManifestAsync(validateStreams: false) is not in a release yet.
# Created with: git -C third_party/YoutubeExplode archive --prefix=YoutubeExplode/ -o SOURCES/youtubeexplode-%{ye_ref}.tar.gz HEAD
Source1:        youtubeexplode-%{ye_ref}.tar.gz
ExclusiveArch:  x86_64
BuildRequires:  dotnet-sdk-10.0
BuildRequires:  systemd-rpm-macros
Requires:       aspnetcore-runtime-10.0
Requires:       /usr/bin/ffmpeg
Requires(pre):  shadow-utils
%{?systemd_requires}
AutoReqProv:    no

%description
YouCast is a headless service that exposes YouTube channels and playlists as
audio or video podcast RSS feeds. Configure it in /etc/youcast.conf.

%prep
# Unpacks YoutubeExplode/ next to youcast-%{version}/, where Service.csproj looks for it (../../../YoutubeExplode).
%setup -q -b 1
cp ../YoutubeExplode/License.txt LICENSE.YoutubeExplode

%build
export DOTNET_CLI_TELEMETRY_OPTOUT=1 DOTNET_NOLOGO=1
dotnet publish src/YouCast.Server/YouCast.Server.csproj -c Release -r linux-x64 \
    --self-contained false -o publish

%install
install -d %{buildroot}%{_libexecdir}/youcast
cp -a publish/. %{buildroot}%{_libexecdir}/youcast/
rm -f %{buildroot}%{_libexecdir}/youcast/*.pdb
install -Dm0644 packaging/youcast.service %{buildroot}%{_unitdir}/youcast.service
install -Dm0640 packaging/youcast.conf.example %{buildroot}%{_sysconfdir}/youcast.conf
install -Dm0644 packaging/youcast.xml %{buildroot}%{_prefix}/lib/firewalld/services/youcast.xml

%pre
getent group youcast >/dev/null || groupadd -r youcast
getent passwd youcast >/dev/null || \
    useradd -r -g youcast -d /var/empty -s /sbin/nologin -c "YouCast service" youcast
exit 0

%post
%systemd_post youcast.service

%preun
%systemd_preun youcast.service

%postun
%systemd_postun_with_restart youcast.service

%files
%license LICENSE
%license LICENSE.YoutubeExplode
%doc README.md
%{_libexecdir}/youcast/
%{_unitdir}/youcast.service
%{_prefix}/lib/firewalld/services/youcast.xml
%config(noreplace) %attr(0640,root,youcast) %{_sysconfdir}/youcast.conf

%changelog
* Wed Nov 25 2026 ykne <6250736+ykne@users.noreply.github.com> - 3.0.0-9
- Channel lookup uses one API call chosen by the value: @handle, UC channel id, otherwise old username
* Tue Nov 24 2026 ykne <6250736+ykne@users.noreply.github.com> - 3.0.0-8
- A channel given as @handle is looked up by handle (forHandle)
* Mon Nov 23 2026 ykne <6250736+ykne@users.noreply.github.com> - 3.0.0-7
- Cached videos are deleted after 12 hours without being requested (CacheMaxAgeHours, 0 = keep until the size limit is reached)
* Sun Nov 22 2026 ykne <6250736+ykne@users.noreply.github.com> - 3.0.0-6
- Video generation is logged (start, queue wait, manifest/download/join times, reconnects, failures) and log lines carry UTC timestamps
* Tue Nov 17 2026 ykne <6250736+ykne@users.noreply.github.com> - 3.0.0-5
- Feed lists are cached and updated incrementally (about one API call per feed instead of hundreds); the saved feed is served when YouTube cannot be asked
* Mon Nov 16 2026 ykne <6250736+ykne@users.noreply.github.com> - 3.0.0-4
- Feeds are gzip-compressed when the client accepts it (about 6x smaller)

* Sun Nov 15 2026 ykne <6250736+ykne@users.noreply.github.com> - 3.0.0-3
- Feeds no longer list live streams and announced (upcoming) broadcasts, which have nothing to play yet

* Sun Nov 15 2026 ykne <6250736+ykne@users.noreply.github.com> - 3.0.0-2
- Video quality uses the shorter side of the picture, so vertical videos get 360x640 instead of 144x256

* Sat Nov 14 2026 ykne <6250736+ykne@users.noreply.github.com> - 3.0.0-1
- Headless ASP.NET Core service (.NET 10, systemd) replacing the WPF/WCF application
- Audio feeds as MP4/AAC original-language track; video feeds by joining H.264 video and AAC audio with ffmpeg
- Optional SOCKS/HTTP proxy for YouTube access, IP-restricted API key support
- Built against the peer YoutubeExplode source (Source1) for the fast stream lookup
- Downloader reconnects stalled chunks
