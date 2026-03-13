Here is folder for docker container which allows logging in to various sites.

## Overview

Uses the [Kasm Chromium](https://hub.docker.com/r/kasmweb/chromium) Docker image to run a full Chromium browser in a Linux container, accessible via a web-based interface. Browser profile data and downloads are persisted to the host.

## Quick Start

```bash
cd docker
docker compose up -d
```

Then open **https://localhost:6901** in your browser.

- **Username:** `kasm_user`
- **Password:** `password`

> The certificate is self-signed, so you'll need to accept the browser warning.

## Persisted Data

| Host Path              | Container Path                                    | Purpose                          |
|------------------------|---------------------------------------------------|----------------------------------|
| `./chromium-profile/`  | `/home/kasm-default-profile/.config/chromium`     | Chromium profile (cookies, logins, extensions) |
| `./downloads/`         | `/home/kasm-user/Downloads`                       | Downloaded files                 |

These folders are created automatically on first run.

## Configuration

Edit `docker-compose.yml` to change:

| Variable   | Default    | Description                     |
|------------|------------|---------------------------------|
| `VNC_PW`   | `password` | Password for the web interface  |
| `shm_size` | `2g`       | Shared memory (increase if Chromium crashes) |
| Port `6901`| `6901`     | Web interface port              |

## Commands

```bash
# Start
docker compose up -d

# Stop
docker compose down

# View logs
docker compose logs -f chromium

# Rebuild / pull latest image
docker compose pull && docker compose up -d
```