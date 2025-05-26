# syntax=docker/dockerfile:1

# DVD Xporter Render Server, as a container. It's built from the repository's root folder, as the server needs the
# projects beside it (Core, DiscDecoders, FFmpeg). See README.md here: in short, from the root folder,
#   docker compose -f Render-Server/docker-compose.yml build

# ---- The server, built on the machine doing the building (it's .NET, so it runs anywhere .NET does) ----
FROM --platform=$BUILDPLATFORM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src
# The project files first, so restoring is only done again when they change
COPY Dvd-Xporter-Core/Dvd-Xporter-Core.csproj Dvd-Xporter-Core/
COPY FFmpeg/FFmpeg.csproj FFmpeg/
COPY DiscDecoders/ DiscDecoders/
COPY Render-Server/Render-Server.csproj Render-Server/
RUN dotnet restore Render-Server/Render-Server.csproj
COPY Dvd-Xporter-Core/ Dvd-Xporter-Core/
COPY FFmpeg/ FFmpeg/
COPY Render-Server/ Render-Server/
RUN dotnet publish Render-Server/Render-Server.csproj -c Release -o /app --no-restore

# ---- ffmpeg and ffprobe: a static build of a recent release (distributions' packages are often older), downloaded for
# the machine it's built for ----
FROM --platform=$BUILDPLATFORM debian:stable-slim AS ffmpeg
ARG TARGETARCH
# A series from https://github.com/BtbN/FFmpeg-Builds/releases (tag "latest"), e.g. 8.1 or 9.0
ARG FFMPEG_SERIES=8.1
RUN apt-get update && apt-get install -y --no-install-recommends ca-certificates curl xz-utils && rm -rf /var/lib/apt/lists/*
RUN case "$TARGETARCH" in \
        amd64) arch=linux64 ;; \
        arm64) arch=linuxarm64 ;; \
        *) echo "There's no ffmpeg build for $TARGETARCH" >&2; exit 1 ;; \
    esac \
    && name="ffmpeg-n${FFMPEG_SERIES}-latest-${arch}-gpl-${FFMPEG_SERIES}" \
    && curl -fsSL "https://github.com/BtbN/FFmpeg-Builds/releases/download/latest/${name}.tar.xz" | tar -xJ \
    && mkdir /ffmpeg && mv "${name}/bin/ffmpeg" "${name}/bin/ffprobe" /ffmpeg/

# ---- What runs ----
FROM mcr.microsoft.com/dotnet/aspnet:10.0
COPY --from=ffmpeg /ffmpeg/ /usr/local/bin/
COPY --from=build /app/ /app/

# The server's settings (with its access key), its jobs and the discs sent for them
VOLUME /data
# Where its folders are mounted, so it needn't be set up: one that isn't mounted isn't used
ENV DVDX_DISC_LIBRARY=/discs \
    DVDX_MOVIES_LIBRARY=/media/movies \
    DVDX_SHOWS_LIBRARY=/media/shows
# The base image has ASP.NET apps listen on 8080; the server listens on its own port, and would warn about it
ENV ASPNETCORE_HTTP_PORTS=
# HTTP for the app; UDP for the app finding it on the network
EXPOSE 47830/tcp 47831/udp

ENTRYPOINT ["dotnet", "/app/dvdx-server.dll", "--data", "/data"]
CMD ["run"]
