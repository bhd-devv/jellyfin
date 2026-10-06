#!/usr/bin/env bash
set -Eeuo pipefail

rclone_pid=
server_pid=

cleanup() {
    trap - EXIT
    # Stop the reader before unmounting its media filesystem.
    if [[ -n "${server_pid}" ]]; then
        kill -TERM "${server_pid}" 2>/dev/null || true
        wait "${server_pid}" 2>/dev/null || true
    fi
    if [[ -n "${rclone_pid}" ]]; then
        kill -TERM "${rclone_pid}" 2>/dev/null || true
        wait "${rclone_pid}" 2>/dev/null || true
    fi
    if mountpoint -q /media; then
        fusermount3 -uz /media || true
    fi
}
trap cleanup EXIT
trap 'exit 143' TERM
trap 'exit 130' INT

mkdir -p /config/config /config/log /cache/tmp /media /rclone-config /rclone-cache
chown app:app /config /config/config /config/log /cache /cache/tmp
chmod 700 /rclone-config

if [[ -n "${RCLONE_CONFIG_BASE64:-}" ]]; then
    # Keep refreshed OAuth tokens on restart. Replace the config only when the
    # bootstrap secret changes, or when the saved config is missing.
    umask 077
    incoming=/rclone-config/rclone.conf.incoming
    if ! printf '%s' "${RCLONE_CONFIG_BASE64}" | base64 --decode > "${incoming}"; then
        rm -f "${incoming}"
        printf '%s\n' 'RCLONE_CONFIG_BASE64 is not valid base64.' >&2
        exit 1
    fi
    incoming_hash=$(sha256sum "${incoming}" | cut -d ' ' -f 1)
    saved_hash=$(cat /rclone-config/bootstrap.sha256 2>/dev/null || true)
    if [[ ! -s /rclone-config/rclone.conf || "${incoming_hash}" != "${saved_hash}" ]]; then
        mv "${incoming}" /rclone-config/rclone.conf
        printf '%s\n' "${incoming_hash}" > /rclone-config/bootstrap.sha256
    else
        rm -f "${incoming}"
    fi
    umask 022
fi
unset RCLONE_CONFIG_BASE64

if [[ ! -s /rclone-config/rclone.conf ]]; then
    printf '%s\n' 'Missing rclone configuration. Set RCLONE_CONFIG_BASE64 in Coolify; see deployment/coolify/README.md.' >&2
    exit 1
fi
chmod 600 /rclone-config/rclone.conf

remote=${GDRIVE_REMOTE:-gdrive:Media}
# Verify credentials and folder access before presenting a media filesystem.
# A typo or an authorization failure must not start Jellyfin against an empty folder.
if ! timeout --foreground 60s rclone lsf "${remote}" \
    --config /rclone-config/rclone.conf --max-depth 1 > /dev/null; then
    printf '%s\n' 'Cannot access the media remote. Check GDRIVE_REMOTE and the Google authorization.' >&2
    exit 1
fi

rclone mount "${remote}" /media \
    --config /rclone-config/rclone.conf \
    --read-only \
    --allow-other \
    --uid "$(id -u app)" \
    --gid "$(id -g app)" \
    --umask 022 \
    --cache-dir /rclone-cache \
    --vfs-cache-mode full \
    --vfs-cache-max-size "${RCLONE_VFS_CACHE_MAX_SIZE:-20G}" \
    --vfs-cache-min-free-space "${RCLONE_VFS_CACHE_MIN_FREE_SPACE:-5G}" \
    --vfs-cache-max-age "${RCLONE_VFS_CACHE_MAX_AGE:-24h}" \
    --dir-cache-time 5m \
    --poll-interval 1m \
    --buffer-size 16M \
    --log-level INFO &
rclone_pid=$!

for ((attempt = 0; attempt < 60; attempt++)); do
    if ! kill -0 "${rclone_pid}" 2>/dev/null; then
        printf '%s\n' 'The media mount failed; Jellyfin will not start.' >&2
        exit 1
    fi
    if mountpoint -q /media; then
        break
    fi
    sleep 1
done
if ! mountpoint -q /media; then
    printf '%s\n' 'Timed out waiting for the media mount.' >&2
    exit 1
fi

# FUSE management and credentials stay with the root supervisor. The server
# runs as the same unprivileged user that owns the persistent Jellyfin volumes.
gosu app "$@" &
server_pid=$!

finished_pid=
status=0
wait -n -p finished_pid "${rclone_pid}" "${server_pid}" || status=$?
if [[ "${finished_pid:-}" == "${rclone_pid}" ]]; then
    rclone_pid=
    printf '%s\n' 'The media mount stopped; stopping Jellyfin so the container can restart.' >&2
    exit 1
fi
server_pid=
exit "${status}"
