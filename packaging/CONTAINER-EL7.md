# Running the YouCast container on EL7 (podman 1.4.4): workarounds and limits

Tested on Oracle Linux 7.9 with kernel 6.18 and `podman-1.4.4-4.0.1.el7` from `ol7_addons`, rootless, as an
ordinary user (`youtube`). The image is the one from `Containerfile` (AlmaLinux 10). EL7 itself cannot run
.NET 10 (glibc 2.17); the container brings its own userland. This is a workaround setup, not a supported one:
podman 1.4.4 is very old.

## What works
- The EL10 image starts and serves audio and video. Default seccomp is fine (no `clone3` problem), the CPU only needs
  x86-64-v3 (an Intel N100 qualifies), and no firewall change is needed with `--network host`.
- A cold 360p download of a 240 MB episode took about 5 s on this host (no proxy needed there).

## Workarounds
1. **Install**: `yum install podman` (pulls runc, criu, containernetworking-plugins, container-selinux).
2. **Storage driver**: overlay refuses to work on an XFS root formatted without `ftype=1`
   ("backing xfs filesystem is formatted without d_type support"). Use `vfs`:
   system-wide in `/etc/containers/storage.conf` (`driver = "vfs"`), rootless in `~/.config/containers/storage.conf`:
   ```
   [storage]
   driver = "vfs"
   ```
   `vfs` copies every layer, so the 1 GB image takes about 1 GB per layer set; keep tens of GB free. If the driver was
   already initialized as overlay, delete `/var/lib/containers/storage` first ("database configuration mismatch").
3. **Getting the image**: podman 1.4.4 cannot pull from current registries (`unsupported schema version 2`).
   Build or pull on a modern host and copy it:
   ```
   podman save --format docker-archive youcast:3.0.0 | ssh user@el7host 'cat > youcast-image.tar'
   ssh user@el7host 'podman pull docker-archive:$HOME/youcast-image.tar'
   ```
   Use `podman pull docker-archive:...`, not `podman load`: rootless `load` hides the real error behind
   "error pulling ''" and does not read stdin.
4. **Rootless**: needs `/etc/subuid` and `/etc/subgid` entries (`user:100000:65536`) and a working `newuidmap`/`newgidmap`.
   On EL7 those carry **file capabilities** (`getcap /usr/bin/newuidmap` shows `cap_setuid+ep`). Do not add the setuid bit:
   it breaks the mapping ("there might not be enough IDs available in the namespace ... lchown ... invalid argument").
   After changing anything here run `podman system migrate`.
5. **Network**: use `--network host`. The service then listens on the host's port 22703 and no CNI bridge or iptables
   rules are created. Open the port in the host firewall yourself if clients on other machines need it.
6. **Cache volume ownership**: podman 1.x creates a new named volume owned by the container's root, not by the image's
   `youcast` user, so the service cannot write its cache and video requests silently fall back to audio (302).
   The image fixes `youcast` to UID/GID 10017, so give the volume to it from inside the user namespace:
   ```
   podman volume create youcast-cache
   V=$(podman volume inspect youcast-cache --format '{{.Mountpoint}}')
   podman unshare chown 10017:10017 "$V" && podman unshare chmod 750 "$V"
   ```
   Afterwards the host user cannot list the directory directly (it is owned by a sub-UID); use `podman unshare ls`.
   A bind mount of a host directory works the same way.
7. **Run command** (rootless, host network):
   ```
   podman run -d --name youcast --network host \
       -v $HOME/youcast.conf:/etc/youcast.conf:ro,Z -v youcast-cache:/var/cache/youcast:Z localhost/youcast:3.0.0
   ```
   The config file must be mode 0644 so the container user can read it.

## Limitations seen with podman 1.4.4
- `podman logs` was empty even though the service ran, `podman ps` status is unreliable, and `--rm` runs and
  foreground attach fail ("failed to connect to container's attach socket"). Test with `-d` and `curl`; read logs by
  running a modern podman elsewhere, or temporarily start the service with output redirected to a file in the volume.
- `podman rm -f` can leave a stuck container behind after a failed start; `podman stop -t 1 NAME` then `podman rm -f NAME`.
- The containers do not restart by themselves: there is no podman-systemd integration in 1.4.4. Use a normal systemd unit
  that runs `podman start -a youcast` (not tested).
- A Google API key restricted by IP must allow the host's outgoing address, or feed requests return 403.
