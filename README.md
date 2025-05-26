# DVD Xporter

DVD Xporter turns DVD and Blu-ray rips into Emby-ready libraries. You plan each movie or show in the app, then export it.

Encoding can take hours, so the export can run on another computer instead: the **render server**. You keep working in the app on your laptop while a home server or NAS does the encoding, straight into its Emby library. This guide sets up the render server with Docker. (To build the Docker image yourself, see [Render-Server/README.md](Render-Server/README.md).)

## Before you start

> **Only run the render server on a local network you trust.** What the app sends it (projects and whole discs) isn't encrypted. Anyone on your network with the server's access key can export with it, writing into its libraries. Never make its port reachable from the internet (no port forwarding on your router).

You need:

- **A computer to run it on**, ideally the one that has your Emby libraries and disc rips, with Docker Engine and its Compose plugin. Run `docker compose version` to check; if it's missing, see [Docker's install guide](https://docs.docker.com/engine/install/). On a NAS, install Docker or Container Manager from its app store.
- **The render server image**: either its name in a registry (e.g. `ghcr.io/your-name/dvdx-server:1.0.0`), or a saved image file (e.g. `dvdx-server-1.0.0.tar.gz`) from whoever made it.
- **The DVD Xporter app**, on the computer you'll send exports from.

Linux is the easiest to run it on: the app can then find the server by itself. On Windows or macOS (Docker Desktop), it works too, but you enter its address in the app yourself (see [Docker Desktop](#docker-desktop-windows-and-macos)).

## 1. Get the image

If you were given a **name**, there's nothing to do yet: it's downloaded in step 4.

If you were given a **file**, load it:

```sh
docker load -i dvdx-server-1.0.0.tar.gz
```

It prints the image's name (e.g. `Loaded image: dvdx-server:latest`). You'll need that name in step 2.

## 2. Make its folder

The server needs a folder of its own on the host. Its settings, access key and jobs are kept there, along with discs sent for jobs while they run. In that folder, create two files.

`compose.yaml`:

```yaml
services:
  dvdx-server:
    image: ${DVDX_IMAGE}
    container_name: dvdx-server
    hostname: dvdx-server
    restart: unless-stopped
    user: "${PUID}:${PGID}"
    network_mode: host
    stdin_open: true
    tty: true
    stop_grace_period: 1m
    environment:
      DVDX_SERVER_NAME: ${SERVER_NAME:-}
      DVDX_ACCESS_KEY: ${ACCESS_KEY:-}
    volumes:
      - ./data:/data
      - ${DISC_LIBRARY}:/discs
      - ${MOVIES_LIBRARY}:/media/movies
      - ${SHOWS_LIBRARY}:/media/shows
```

`.env`, with your own values:

```sh
# The image from step 1
DVDX_IMAGE=ghcr.io/your-name/dvdx-server:1.0.0

# What the app calls the server, e.g. Basement Server
SERVER_NAME=Basement Server
# Its access key: leave it blank, and the server makes one the first time it starts
ACCESS_KEY=

# The user and group that will own what the server writes. Run "id -u" and "id -g" to see yours
PUID=1000
PGID=1000

# Folders on this computer
DISC_LIBRARY=/srv/discs
MOVIES_LIBRARY=/srv/media/movies
SHOWS_LIBRARY=/srv/media/shows
```

What the folders are for:

| Setting | Inside the server it's | What it is |
| --- | --- | --- |
| `DISC_LIBRARY` | `/discs` | Your disc rips: ISOs, `VIDEO_TS` or `BDMV` folders, in folders of any depth. Discs are found by their contents, so names don't matter. Discs you copy to the server from the app go here too. |
| `MOVIES_LIBRARY` | `/media/movies` | Your Emby **Movies** library folder. |
| `SHOWS_LIBRARY` | `/media/shows` | Your Emby **TV Shows** library folder. |

Things to know:

- **Don't want it writing into Emby's folders?** Export into one folder instead, e.g. `EXPORTS=/srv/exports`: replace the two library lines under `volumes:` with `- ${EXPORTS}:/media/exports`, and add `DVDX_EXPORT_FOLDER: /media/exports` under `environment:`. Each title still gets its own folder there, laid out as Emby expects.
- **No Disc Library, or no TV Shows library?** Leave out its line under `volumes:`. A folder that isn't mounted isn't used: with no Disc Library, the app sends each job the discs it needs, and they're deleted when the job's done.
- **Read-only Disc Library?** Add `:ro` to the end of the `/discs` line (`${DISC_LIBRARY}:/discs:ro`). The app can then still use the discs, but can't copy discs into it.
- **Make the folders before you start.** Make them yourself, and check that the `PUID`/`PGID` user can write to them. Docker creates any that are missing as owned by root, and the server can't write to those:

  ```sh
  mkdir -p data
  ```

## 3. Its settings

There's nothing to set up. The server uses the folders mounted at `/discs`, `/media/movies` and `/media/shows`, and the name and key from `environment:` in `compose.yaml`. The first time it starts, it saves its settings in `./data`, makes its **access key** (unless you chose one), and shows the key in its log (step 4). Note the key down for step 5; you can see it again at any time (see [Everyday use](#everyday-use)).

What can go under `environment:`:

| Setting | What it is |
| --- | --- |
| `DVDX_SERVER_NAME` | What the app calls it. Blank for the computer's name. |
| `DVDX_ACCESS_KEY` | The access key, if you'd rather choose it. Blank: it makes one. |
| `DVDX_PORT` | The port it listens on, if not 47830. |
| `DVDX_EXPORT_FOLDER` | One folder to export into, inside the server, instead of the Emby libraries. |
| `DVDX_DISC_LIBRARY`, `DVDX_MOVIES_LIBRARY`, `DVDX_SHOWS_LIBRARY` | Other folders inside the server, if you mount them somewhere else. |

They're read each time the server starts, so to change one, change it and run `docker compose up -d`. A setting that's blank is left as it was.

**Rather answer questions?** Setup can set the name, port and key instead of `environment:`:

```sh
docker compose run --rm dvdx-server setup
```

The folders always come from where they're mounted, whatever's answered there; to export into one folder, use `DVDX_EXPORT_FOLDER`.

## 4. Start it

```sh
docker compose up -d
```

If the image is in a registry, this downloads it first. Check that it's running:

```sh
docker compose logs
```

You should see the warning, its access key the first time, how many discs it found, and a line like `Listening on 192.168.1.20:47830`. It now starts by itself with the computer, unless you stop it.

## 5. Connect the app

In DVD Xporter, on the computer you'll send exports from:

1. Open **File › Settings › Server**.
2. Choose **Find on Network** and pick the server. If it isn't listed, type its address instead, e.g. `192.168.1.20` (add `:port` if you changed the port in step 3).
3. Enter the **access key** from the server's log (step 4).
4. Choose **Test**. It should say `✓ Connected to "Basement Server"`. Then choose **Save**.

From then on:

- **Project overview:** discs the server has get a server icon. A disc that's only on the server counts as found, shown as **On server**. **Copy to Server...** puts a disc in its Disc Library to keep.
- **Add Disc › From Disc Library...:** lists the server's discs too. Those only on the server have the server icon. When you add one, the server reads it.
- **Everywhere else:** a disc that's only on the server is used like one on your computer, streamed from the server. You can preview it in the Clip Renamer, test it in Transcoding, and export it from either computer.
- **Export step:** choose **Export on: (your server)** to send files to it. Any discs it hasn't got go with the job, and are deleted when the job's done.
- **File › Server Jobs...:** shows what it's exporting and what's waiting, and lets you cancel jobs.

## Everyday use

Run these in the server's folder:

| To | Run |
| --- | --- |
| See what it's doing | `docker compose logs -f` (Ctrl+C stops following; the server keeps running) |
| See its access key | `docker compose run --rm dvdx-server key` |
| Make a new access key | `docker compose run --rm dvdx-server new-key`, then `docker compose restart`, and enter the new key in the app. (If you set `ACCESS_KEY` in `.env`, change it there instead.) |
| Type its commands | `docker attach dvdx-server`, then `jobs`, `rescan` (look for discs again) or `key`. Press **Ctrl+P then Ctrl+Q** to leave it running. (Ctrl+C there stops the server.) |
| Stop it | `docker compose down` |
| Start it | `docker compose up -d` |

It looks through the Disc Library when it starts. It looks again before a job if a disc isn't found, when you type `rescan`, and when you press Rescan in the app (F5). So discs you add or rename are picked up without restarting it.

## Updating it

If the image is in a registry, change `DVDX_IMAGE` to the new version if it has one, then:

```sh
docker compose pull
docker compose up -d
```

If you were sent a new file, `docker load` it first, then run `docker compose up -d`.

Stopping or updating never loses work:
- An export that's running stops cleanly, and starts again from the beginning when the server is back. Anything it had already finished for that job is deleted and made again, so no half-made files are left.
- Waiting jobs stay queued.
- Its settings and access key are kept in `./data`.

## Docker Desktop (Windows and macOS)

Docker Desktop doesn't pass the network search through to containers, so **Find on Network** won't find the server. Instead:

1. In `compose.yaml`, replace `network_mode: host` with:

   ```yaml
       ports:
         - "47830:47830"
   ```

2. Write the folders in `.env` the way Docker Desktop expects. On Windows that's, e.g., `DISC_LIBRARY=D:/Discs`. Then share those drives with Docker Desktop if it asks.
3. In the app, type the computer's address (its IP address on your network) into Settings › Server, rather than searching.

## Firewalls

If the app can't reach the server, let these in on the server's computer, from your local network only:

- **TCP 47830**: the app talking to the server (or the port you chose).
- **UDP 47831**: the app finding the server (**Find on Network**).

For example, with `ufw` on Linux:

```sh
sudo ufw allow from 192.168.1.0/24 to any port 47830 proto tcp
sudo ufw allow from 192.168.1.0/24 to any port 47831 proto udp
```

## Troubleshooting

- **"... isn't there, so it isn't used":** a folder isn't mounted where the server looks for it (`/discs`, `/media/movies`, `/media/shows`). Check the `volumes:` lines and the folders in `.env`.
- **Setup asks to make a folder, or says it couldn't:** answer `n`, and use the paths inside the server (`/discs`, `/media/movies`, ...). A folder made anywhere else is inside the container, not on your computer, and is lost when it stops. Check the folders on your computer exist and are in `.env`.
- **Exports fail with "Permission denied":** the `PUID`/`PGID` user can't write to the library folders. Fix the folders' owner or permissions, or change `PUID`/`PGID`, then run `docker compose up -d` again.
- **The app says "didn't accept the access key":** check the key with `docker compose run --rm dvdx-server key`.
- **"Couldn't listen on port 47830":** something else on the computer uses that port. Choose another with `DVDX_PORT` under `environment:` (or run setup again), then enter `address:port` in the app.
- **Find on Network doesn't find it:** it needs `network_mode: host` (Linux) and UDP 47831 open. You can always type the address instead.

## Where things are kept

Everything is in `./data`, which is `/data` inside the server:

| Path | What it is |
| --- | --- |
| `server.json` | Its settings, including the access key. Keep it private. |
| `jobs.json` | Its jobs: running, waiting and finished. |
| `Jobs/<id>/` | Each job's copy of its project, and its logs, until you remove the job in the app. Also the discs sent for it, until it's done. |
| `disc-fingerprints.json` | A cache of the Disc Library's discs, so looking through it again is quick. |

To start over, stop the server, delete `./data`, and set it up again.
