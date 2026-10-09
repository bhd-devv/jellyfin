# Jellyfin personal fork on Coolify

The root `compose.yaml` builds the backend from this repository and the web
frontend from the repository and full commit SHA specified in `build.args`.
The image includes .NET 10, Jellyfin FFmpeg 7, and rclone 1.70.3 pinned by image
digest. A root supervisor manages the FUSE mount and launches Jellyfin as the
unprivileged .NET `app` user. Jellyfin serves the API and frontend on port 8096.

## Storage

| Compose volume | Container path | Contents | Back up? |
| --- | --- | --- | --- |
| `jellyfin-data` | `/config` | Database, settings, users, metadata, logs, plugins, ASP.NET keys | Yes |
| `jellyfin-cache` | `/cache` | Jellyfin cache, temporary files, transcoding output | No |
| `rclone-config` | `/rclone-config` | Google Drive configuration and refreshed OAuth tokens | Yes, securely |
| `rclone-cache` | `/rclone-cache` | Cached media chunks downloaded from Google Drive | No |

All four volumes live on the deployment server and survive container rebuilds.
Use local SSD storage for them. Keep the database on local disk, not Google Drive.
The cache is disposable when the application is stopped; it is not a backup.
Deleting the stack's volumes (for example, `docker compose down -v`) deletes its data.
The Jellyfin volumes do not have disk quotas in this Compose file. Allow disk
space for metadata and concurrent transcoding, and monitor usage.

Google Drive is mounted read-only at `/media`. The VFS `full` cache stores accessed
media chunks locally and supports seeking; it does not mirror the entire Drive.
Files that are read completely can also be cached completely. The default cache
target is 20 GB, with 5 GB of disk space reserved and a 24-hour unused-file age.
These are eviction targets, not hard quotas: open files can exceed the targets.
The filesystem backing `rclone-cache` must support sparse files (for example ext4
or XFS); use the server's local SSD, not a FAT/exFAT volume.

The supervisor checks the remote folder and waits for FUSE before starting Jellyfin.
It stops Jellyfin and exits if the rclone process exits, allowing Compose to restart
the container. Temporary cloud errors can still affect reads while the mount process
remains running; this does not guarantee uninterrupted access during a Drive outage.
Keeping both processes in the same container avoids shared host mount propagation
and avoids losing the media mount when separate containers are recreated.

## Google Drive authorization (one time)

1. Create a `Media` folder in your Google Drive and put media inside it (for
   example `Media/Movies` and `Media/Shows`). You can use another existing folder;
   set `GDRIVE_REMOTE` to match it in Coolify.
2. Install rclone on your computer if needed. On macOS, `brew install rclone` is
   one option. From a private working directory, create a dedicated config:

   ```sh
   rclone --config ./rclone.conf config
   ```

   Select **New remote**, name it `gdrive`, and select **Google Drive** (`drive`).
   Configure your own Google OAuth client following
   [rclone's instructions](https://rclone.org/drive/#making-your-own-client-id):
   enable the Google Drive API and create a **Desktop app** OAuth client.
   Enter its client ID and client secret in rclone. The shared rclone OAuth
   client is being retired during 2026, so do not rely on it for deployment.
   Choose the **read-only** scope (`drive.readonly`), then complete browser
   authorization with your Google account. Publish the OAuth consent app to
   **Production** for persistent use; in Testing, refresh tokens can expire
   after seven days. Answer **No** to Shared Drive for a personal My Drive;
   otherwise select the Shared Drive containing your media.
3. Verify that the chosen folder is accessible:

   ```sh
   rclone --config ./rclone.conf lsf gdrive:Media --max-depth 1
   ```

4. Encode the dedicated config:

   ```sh
   base64 < ./rclone.conf | tr -d '\n'
   ```

   Copy the result into the **runtime secret** `RCLONE_CONFIG_BASE64` in Coolify.
   It contains Google credentials. Base64 is encoding, not encryption; do not
   commit the result or use it as a Docker build argument. Config files and local
   `.env` files are excluded from Git and Docker's build context.

On first start, the supervisor writes the config to the private, writable
`rclone-config` volume. rclone can save refreshed OAuth tokens there. Reusing
the same bootstrap secret preserves the refreshed config; changing the secret
replaces it. You can remove the bootstrap secret after initial setup, provided
the config volume is retained and securely backed up. Reauthorize and replace
the secret if Google revokes access.

## First deployment

1. The deployment server must run Linux with FUSE available. On Ubuntu/Debian:

   ```sh
   sudo apt-get update
   sudo apt-get install -y fuse3
   sudo modprobe fuse
   ls -l /dev/fuse
   ```

   The `/dev/fuse` device must exist on the server. Compose adds the device,
   `SYS_ADMIN`, and an AppArmor override for the mount. It does not require
   `privileged: true`. The Jellyfin process itself runs without root privileges.
2. Push this backend branch to GitHub. In Coolify create an Application from
   `bohdan2704/jellyfin`, select `bh-fork-personal`, choose the Docker Compose
   build pack, and set the Compose location to `/compose.yaml` with base directory
   `/`. Save the settings and select **Load a Compose File** (or **Reload Compose
   File**, depending on the Coolify version). Confirm that the parsed `jellyfin`
   service appears under Domains. Use Coolify's normal Compose build so the
   Dockerfile arguments are passed.
3. Add these runtime environment variables in Coolify:

   | Variable | Default / value | Purpose |
   | --- | --- | --- |
   | `RCLONE_CONFIG_BASE64` | The secret created above | Bootstrap Google authorization |
   | `GDRIVE_REMOTE` | `gdrive:Media` | The remote and media folder; use `gdrive:` for its root |
   | `RCLONE_VFS_CACHE_MAX_SIZE` | `20G` | Local media cache size target |
   | `RCLONE_VFS_CACHE_MIN_FREE_SPACE` | `5G` | Free disk space target |
   | `RCLONE_VFS_CACHE_MAX_AGE` | `24h` | Maximum age of unused cached files |
   | `TZ` | `Europe/Kyiv` | Server timezone |

   Folder names with spaces are supported; enter the actual value without shell
   quotes in Coolify. A missing config, inaccessible folder, or failed mount
   prevents Jellyfin from starting.
4. Assign your HTTPS domain to the `jellyfin` service, targeting internal port
   8096: enter a value such as `https://jellyfin.example.com:8096` in its Domains
   field. The suffix selects the container port; the public URL remains
   `https://jellyfin.example.com` on standard HTTPS port 443.
   The service only exposes that internal port; Coolify's proxy handles
   public access and TLS. No separate database, Redis, or frontend service is needed.
5. Deploy, open the domain, and complete setup. Add libraries using paths such
   as `/media/Movies` and `/media/Shows`. In Jellyfin's Networking settings,
   configure the actual Coolify proxy address as a Known Proxy for forwarded headers.
6. Verify scanning, playback, seeking, and transcoding against your actual Drive.
   Changes made in Drive require a scheduled or manual Jellyfin library scan;
   rclone mounts do not provide the normal local filesystem change notifications.

The frontend build uses Node 24 and can consume several GB of RAM. The container
webpack wrapper keeps the frontend production configuration but limits module
build concurrency and the JavaScript minifier to one worker. A server or
dedicated build server with at least 6 GB of available build memory is a practical
starting point; streaming/transcoding resource requirements depend on your media.

If testing outside Coolify, add a local port mapping such as `8096:8096` in a
Compose override. The production file leaves routing to Coolify.

## Frontend updates

1. Push frontend changes to `https://github.com/bohdan2704/jellyfin-web`.
2. Set `FRONTEND_COMMIT` in `compose.yaml` to the full SHA of a pushed commit.
3. Commit and push the backend change, then deploy in Coolify (or enable auto-deploy
   for the backend branch).

Changing the SHA invalidates the frontend checkout/build cache. There is no need
to disable Docker caching or configure a separate frontend application. The SHA
must be available in the frontend repository and compatible with the backend.
The current frontend and backend both identify themselves as version 12.1.0.
This Dockerfile expects the frontend repository to be publicly accessible.

## Operational notes

- Back up `jellyfin-data` while Jellyfin is stopped, or use Jellyfin's supported
  backup feature. Database migrations can make restoring an older image alone
  insufficient for rollback; keep a data backup before server upgrades.
- No hardware acceleration is enabled by default. Add GPU-specific devices and
  permissions after choosing the server hardware, then configure Jellyfin.
- Docker log rotation is configured separately from Jellyfin's files in `/config/log`.
- Drive API/download limits and internet bandwidth affect scanning and playback.
  Video still passes through your server. Avoid aggressive full-library analysis
  (such as generating trickplay for every remote movie) until you measure usage.
- If startup fails, inspect the container logs. Missing authorization, an incorrect
  remote/folder, and a missing FUSE device are reported before Jellyfin starts.

References: [Coolify Docker Compose](https://coolify.io/docs/knowledge-base/docker/compose),
[rclone mount](https://rclone.org/commands/rclone_mount/),
[Google Drive remote](https://rclone.org/drive/).
