# DVD Xporter Render Server: building the Docker image

The render server (`dvdx-server`) is a console app that exports DVD Xporter projects sent to it from the app, so heavy encodes run on another computer. This guide covers turning it into a Docker image and getting that image to other people. How to *run* the image is in the [README in the repository's root folder](../README.md), which is the guide to give anyone who runs it.

## What's here

| File | What it's for |
| --- | --- |
| `Dockerfile` | Builds the image in three stages: the server (.NET 10 SDK), ffmpeg and ffprobe (a static build), and the image that runs (the ASP.NET Core 10 runtime). |
| `docker-compose.yml` | Builds the image, and runs it on this computer to try it. |
| `.env.example` | The settings the compose file reads: the image's name and version, the ffmpeg series, the user and group that own its files, and the folders to mount. Copy it to `.env`. |
| `../.dockerignore` | Keeps the Godot app, build output and local data out of the build. |

The image is built from the **repository's root folder**, not this one, because the server needs the `Dvd-Xporter-Core`, `DiscDecoders` and `FFmpeg` projects beside it. The compose file sets this up (`context: ..`), so run every command below from the root folder.

## What you need

- Docker Engine 24 or newer, with the Compose plugin (`docker compose version` to check). On Windows and macOS, Docker Desktop.
- An internet connection while building: the build downloads the .NET images, NuGet packages and ffmpeg.
- No .NET SDK is needed: the build runs inside Docker.

## 1. Set it up

```sh
cp Render-Server/.env.example Render-Server/.env
```

Edit `Render-Server/.env`:

- `DVDX_IMAGE` and `DVDX_VERSION`: the name and tag the image gets. Keep `dvdx-server` and `latest` to try it here. To publish it, use the registry's name, e.g. `ghcr.io/your-name/dvdx-server` and `1.0.0`.
- `FFMPEG_SERIES`: which ffmpeg is built in (see [ffmpeg](#ffmpeg)).
- The rest (`PUID`, `PGID` and the folders) is only used when you run it here with this compose file.

## 2. Build it

```sh
docker compose -f Render-Server/docker-compose.yml build
```

The first build takes a few minutes; later ones reuse what hasn't changed. Check it worked:

```sh
docker compose -f Render-Server/docker-compose.yml run --rm dvdx-server help
```

This prints the server's usage. If it says ffmpeg or ffprobe is missing, the ffmpeg stage didn't work (see [ffmpeg](#ffmpeg)).

## 3. Try it here (optional)

1. Make the folders in `.env` first, as yourself. Otherwise Docker creates missing ones owned by root, and the server can't write to them:

   ```sh
   mkdir -p Render-Server/data /srv/discs /srv/media/movies /srv/media/shows   # your folders from .env
   ```

2. Start it, and follow what it does. The compose file gives it its settings, so the first time it starts it sets itself up and shows its access key in the log:

   ```sh
   docker compose -f Render-Server/docker-compose.yml up -d
   docker compose -f Render-Server/docker-compose.yml logs -f
   ```

3. In the app, open Settings › Server, choose **Find on Network**, enter the access key, and choose **Test**.

Stop it with `docker compose -f Render-Server/docker-compose.yml down`. An export that's running is stopped cleanly and starts again from the beginning the next time the server runs.

## 4. Get it to others

### Through a registry (recommended)

People can then fetch it with `docker compose pull`, and updates are a new tag. These steps use the GitHub Container Registry; Docker Hub works the same way, with an image name like `your-name/dvdx-server`.

1. Set `DVDX_IMAGE=ghcr.io/your-name/dvdx-server` and `DVDX_VERSION=1.0.0` in `.env`.
2. Log in with a GitHub token that has the `write:packages` scope:

   ```sh
   echo "$GITHUB_TOKEN" | docker login ghcr.io -u your-name --password-stdin
   ```

3. Build and push:

   ```sh
   docker compose -f Render-Server/docker-compose.yml build
   docker compose -f Render-Server/docker-compose.yml push
   ```

4. To also offer it as `latest`, tag and push again:

   ```sh
   docker tag ghcr.io/your-name/dvdx-server:1.0.0 ghcr.io/your-name/dvdx-server:latest
   docker push ghcr.io/your-name/dvdx-server:latest
   ```

5. On GitHub, make the package public (the package's settings › Change visibility), or give people access to it.

#### For ARM servers too (Raspberry Pi 4/5, ARM NAS boxes)

A normal build only runs on the kind of processor it was built on. To build for both kinds at once and push them under one name, use Buildx with its `bake` command, which reads the compose file. Run it from **this** folder, as `bake` reads `.env` from the folder it's run in:

```sh
docker buildx create --use --name dvdx   # once
cd Render-Server
docker buildx bake --set "*.platform=linux/amd64,linux/arm64" --push
```

The server and ffmpeg are prepared on your machine, so this doesn't need emulation and isn't much slower than a normal build.

### As a file (no registry)

Save the image to a file, and send that with the root README:

```sh
docker save dvdx-server:latest | gzip > dvdx-server-1.0.0.tar.gz
```

They load it with `docker load -i dvdx-server-1.0.0.tar.gz`. A saved image only runs on the processor type it was built for (usually x86-64). For an ARM computer, build it there, or run the `bake` command above with `--set "*.platform=linux/arm64"` and `--load` in place of `--push`.

### What to give people

- The image's name (e.g. `ghcr.io/your-name/dvdx-server:1.0.0`), or the saved file.
- The [root README](../README.md), which takes them through running it.

## ffmpeg

The image includes a static ffmpeg build from [BtbN/FFmpeg-Builds](https://github.com/BtbN/FFmpeg-Builds/releases), because distributions' own ffmpeg packages are often several versions behind. (Ubuntu 24.04, which the .NET image is based on, has 6.1.) `FFMPEG_SERIES` picks the series: `8.1` by default, the nearest to the ffmpeg the app is developed with.

- To change it, pick a series listed under the release tagged `latest` (files named `ffmpeg-n<series>-latest-linux64-gpl-<series>.tar.xz`), set `FFMPEG_SERIES`, and build again.
- Each series' `latest` build is updated with that series' fixes. Building again picks those up; build with `--no-cache` to make sure it does.
- If the download fails (the project has renamed its files, say), the build stops at the ffmpeg stage with curl's error. You can change the `curl` line in the `Dockerfile` to another source. Or, in the last stage, replace the ffmpeg `COPY` line with `RUN apt-get update && apt-get install -y ffmpeg && rm -rf /var/lib/apt/lists/*` to use Ubuntu's older version.

These builds are GPL-licensed. If you distribute the image, you're distributing ffmpeg, so follow its licence. In practice: say that the image contains ffmpeg under the GPL, and where its source is (the BtbN release, and ffmpeg's own source for that version).

## Updating it

1. Change the code, then raise `DVDX_VERSION` (e.g. `1.0.1`).
2. Build and push it as above, and tell people the new tag.

When they update (`docker compose pull` then `docker compose up -d`), the server stops cleanly: what it was exporting starts again from the beginning, and what's waiting stays queued. The settings, the access key and the jobs are in the `/data` volume, so they carry over.

## Notes for maintainers

- **Arguments:** the entry point is `dotnet /app/dvdx-server.dll --data /data`, and the command defaults to `run`. A command given to `docker compose run` replaces only `run`, so `setup`, `key`, `new-key` and `help` all work the same way.
- **Folders:** the image sets `DVDX_DISC_LIBRARY=/discs`, `DVDX_MOVIES_LIBRARY=/media/movies` and `DVDX_SHOWS_LIBRARY=/media/shows`, where the compose files mount them, so the server needn't be set up. A folder that isn't mounted isn't there, and the server doesn't use it (it says so when it starts), rather than writing into the container.
- **Stopping:** the server treats SIGTERM (what `docker stop` sends) like Ctrl+C. It stops ffmpeg, deletes the half-made file, and leaves the job to start again. `stop_grace_period` allows a minute for this.
- **Networking:** the compose file uses `network_mode: host`, so the app's network search (UDP multicast and broadcast on port 47831) reaches the server. Docker's default bridge network doesn't pass those through. On Docker Desktop, host networking isn't available the same way; the root README says what to do instead.
- **User:** the container runs as `PUID:PGID`, so exports and copied discs are owned by a real user on the host, not root.
