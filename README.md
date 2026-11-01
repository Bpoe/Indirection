# Indirection

A lightweight, self-hosted service for creating and resolving URL short links.

Create short links under your own domain and manage them through the browser UI at `/admin` or a small REST API. You choose the link codes, including nested paths such as `docs/setup`.

## How to Use

### Run with Docker Compose

The included [`compose.yaml`](compose.yaml) runs Indirection on port 8080 with a persistent SQLite database.

~~~bash
git clone https://github.com/Bpoe/Indirection.git
cd Indirection

export INDIRECTION_API_KEY="$(openssl rand -base64 32)"
docker compose pull
docker compose run --rm --no-deps indirection --migrate
docker compose up -d
~~~

Open [http://localhost:8080/admin](http://localhost:8080/admin) and enter the API key to create your first link. A link named `docs` redirects requests for `http://localhost:8080/docs` to its destination.

The `--migrate` command initializes or updates the SQLite database and must be run before starting a new installation or an upgraded version. The database is stored in a Docker volume and survives container recreation. Avoid exposing the standalone HTTP port directly to the internet; use HTTPS through a reverse proxy for public deployments.

### HTTPS with Traefik

I use [Traefik](https://traefik.io/traefik/) as a separate ingress service. It handles HTTPS certificates automatically through Let's Encrypt and routes each domain to the right container. The following is a minimal example for running Traefik and Indirection in separate Compose projects.

Point your domain's DNS record to the server, make sure inbound TCP ports 80 and 443 reach Traefik, and create a shared Docker network:

~~~bash
docker network create proxy
~~~

**1. Traefik:** Create `traefik/compose.yaml`:

~~~yaml
services:
  traefik:
    image: traefik:v3.5
    restart: unless-stopped
    command:
      - --providers.docker=true
      - --providers.docker.exposedbydefault=false
      - --entrypoints.web.address=:80
      - --entrypoints.websecure.address=:443
      - --entrypoints.web.http.redirections.entrypoint.to=websecure
      - --entrypoints.web.http.redirections.entrypoint.scheme=https
      - --certificatesresolvers.letsencrypt.acme.email=${ACME_EMAIL}
      - --certificatesresolvers.letsencrypt.acme.storage=/letsencrypt/acme.json
      - --certificatesresolvers.letsencrypt.acme.httpchallenge=true
      - --certificatesresolvers.letsencrypt.acme.httpchallenge.entrypoint=web
    ports:
      - "80:80"
      - "443:443"
    volumes:
      - /var/run/docker.sock:/var/run/docker.sock:ro
      - ./letsencrypt:/letsencrypt
    networks:
      - proxy

networks:
  proxy:
    external: true
~~~

Create `traefik/.env` containing `ACME_EMAIL=you@example.com`, then run `docker compose up -d` from the `traefik` directory. Traefik stores its certificates in `./letsencrypt`.

**2. Indirection:** In a separate directory, use this `compose.yaml` instead of the standalone one. The Traefik labels configure the hostname, HTTPS entry point, certificate resolver, and internal service port:

~~~yaml
services:
  indirection:
    image: ghcr.io/bpoe/indirection:latest
    restart: unless-stopped
    volumes:
      - data:/data
    secrets:
      - source: ApiKey
        target: Authentication__ApiKey
    networks:
      - proxy
    labels:
      - "traefik.enable=true"
      - "traefik.docker.network=proxy"
      - "traefik.http.routers.indirection.rule=Host(`${INDIRECTION_HOST}`)"
      - "traefik.http.routers.indirection.entrypoints=websecure"
      - "traefik.http.routers.indirection.tls.certresolver=letsencrypt"
      - "traefik.http.services.indirection.loadbalancer.server.port=8080"

volumes:
  data:

networks:
  proxy:
    external: true

secrets:
  ApiKey:
    file: ./secrets/indirection-api-key
~~~

Create a `.env` file beside that Compose file containing `INDIRECTION_HOST=go.example.com`, then create the API-key secret and start Indirection:

~~~bash
mkdir -p secrets
printf '%s' "$(openssl rand -base64 32)" > secrets/indirection-api-key
chmod 600 secrets/indirection-api-key

docker compose pull
docker compose run --rm --no-deps indirection --migrate
docker compose up -d
~~~

Now open `https://go.example.com/admin`. Indirection doesn't publish a host port in this configuration; Traefik reaches it over the shared `proxy` network and handles TLS.

### API key configuration

Indirection protects its management UI's API calls and `/api/v1/links` endpoints with an API key sent in the `X-Api-Key` header. Public redirects do not need a key.

The included Compose file gets `INDIRECTION_API_KEY` from your shell and mounts it as a Docker secret. The Traefik example above instead reads a local secret file. Both approaches provide `/run/secrets/Authentication__ApiKey` inside the container, which Indirection reads automatically.

You can also configure the key from a `.env` file. For that approach, replace the Indirection service's `secrets:` block with:

~~~yaml
    environment:
      Authentication__ApiKey: ${INDIRECTION_API_KEY:?Set INDIRECTION_API_KEY in .env}
~~~

Remove the top-level `secrets:` block as well and add `INDIRECTION_API_KEY=your-generated-key` to the same `.env` file. Docker Compose uses the file for variable substitution; it does not automatically pass every `.env` value into the container. File-backed Docker secrets are preferable to putting credentials in container environment variables.

Keep keys and `.env` files out of source control, and use HTTPS for management access.

### API example

You can also create and manage links without the UI:

~~~bash
curl -X PUT https://go.example.com/api/v1/links/docs \
  -H "X-Api-Key: YOUR_API_KEY" \
  -H "Content-Type: application/json" \
  -d '{"destination":"https://example.com/some/long/url"}'
~~~

Visiting `https://go.example.com/docs` then returns an HTTP 302 redirect to the destination.

## Technical Overview

Indirection is built with **.NET 10** and **ASP.NET Core**, using **Entity Framework Core with SQLite** for durable storage and **HybridCache** for fast, in-process link resolution. It deliberately keeps the architecture small and is designed to run as a single instance.

See [docs/design.md](docs/design.md) for the architecture, API details, and design decisions.
