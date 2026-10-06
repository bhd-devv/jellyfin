# syntax=docker/dockerfile:1

FROM rclone/rclone:1.70.3@sha256:34c729127386abec1c610b2aa024e39b4498dc2b4a72a0798ae21fbdc1b0493b AS rclone-assets

FROM node:24-bookworm-slim AS web-build
RUN apt-get update \
    && apt-get install --no-install-recommends -y ca-certificates git \
    && rm -rf /var/lib/apt/lists/*
WORKDIR /src/web
ARG FRONTEND_REPOSITORY
ARG FRONTEND_COMMIT
RUN printf '%s\n' "${FRONTEND_COMMIT}" | grep -Eq '^[0-9a-f]{40}$' \
    && git init \
    && git remote add origin "${FRONTEND_REPOSITORY}" \
    && git fetch --depth=1 origin "${FRONTEND_COMMIT}" \
    && git checkout --detach FETCH_HEAD \
    && test "$(git rev-parse HEAD)" = "${FRONTEND_COMMIT}"
RUN --mount=type=cache,target=/root/.npm npm ci --no-audit --no-fund
COPY deployment/coolify/webpack.container.cjs ./webpack.container.cjs
ENV NODE_ENV=production NODE_OPTIONS=--max-old-space-size=3072
RUN JELLYFIN_VERSION="${FRONTEND_COMMIT}" \
    ./node_modules/.bin/webpack --config webpack.container.cjs

FROM mcr.microsoft.com/dotnet/sdk:10.0-noble AS server-build
WORKDIR /src/server
COPY . .
RUN --mount=type=cache,target=/root/.nuget/packages \
    dotnet publish Jellyfin.Server/Jellyfin.Server.csproj \
    --configuration Release --output /out --self-contained false

FROM mcr.microsoft.com/dotnet/aspnet:10.0-noble AS runtime
USER root
RUN apt-get update \
    && apt-get install --no-install-recommends -y \
       bash ca-certificates curl fonts-dejavu-core fontconfig fuse3 gnupg gosu \
       libfontconfig1 libfreetype6 tzdata util-linux \
    && mkdir -p /etc/apt/keyrings \
    && curl --fail --silent --show-error --location \
       https://repo.jellyfin.org/jellyfin_team.gpg.key \
       | gpg --dearmor --output /etc/apt/keyrings/jellyfin.gpg \
    && printf 'deb [arch=%s signed-by=/etc/apt/keyrings/jellyfin.gpg] https://repo.jellyfin.org/ubuntu noble main\n' \
       "$(dpkg --print-architecture)" > /etc/apt/sources.list.d/jellyfin.list \
    && apt-get update \
    && apt-get install --no-install-recommends -y jellyfin-ffmpeg7 \
    && rm -rf /var/lib/apt/lists/* \
    && mkdir -p /config/config /config/log /cache/tmp /media /rclone-config /rclone-cache \
    && chown -R app:app /config /cache \
    && printf 'user_allow_other\n' > /etc/fuse.conf

WORKDIR /jellyfin
COPY --from=server-build /out/ /jellyfin/
COPY --from=web-build /src/web/dist/ /jellyfin-web/
COPY --from=rclone-assets /usr/local/bin/rclone /usr/local/bin/rclone
COPY --chmod=755 deployment/coolify/entrypoint.sh /usr/local/bin/jellyfin-entrypoint

ENV HOME=/config \
    JELLYFIN_DATA_DIR=/config \
    JELLYFIN_CONFIG_DIR=/config/config \
    JELLYFIN_LOG_DIR=/config/log \
    JELLYFIN_CACHE_DIR=/cache \
    JELLYFIN_WEB_DIR=/jellyfin-web \
    TMPDIR=/cache/tmp \
    DOTNET_CLI_TELEMETRY_OPTOUT=1

ARG FRONTEND_REPOSITORY
ARG FRONTEND_COMMIT
LABEL org.opencontainers.image.title="Jellyfin personal fork" \
      org.opencontainers.image.source="https://github.com/bohdan2704/jellyfin" \
      io.jellyfin.frontend.repository="${FRONTEND_REPOSITORY}" \
      io.jellyfin.frontend.commit="${FRONTEND_COMMIT}"

EXPOSE 8096
# The supervisor manages FUSE as root and drops privileges for Jellyfin.
USER root
ENTRYPOINT ["/usr/local/bin/jellyfin-entrypoint"]
CMD ["dotnet", "/jellyfin/jellyfin.dll", "--ffmpeg", "/usr/lib/jellyfin-ffmpeg/ffmpeg"]
