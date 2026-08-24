# Ngino Docker Deployment

This directory contains Docker configuration files for deploying the Ngino server and client.

## Files

- `server/Dockerfile` - Build configuration for the Ngino server
- `docker-compose.yml` - Docker Compose configuration with MySQL for Elmah error logging
- `.env.example` - Example environment variables (copy to `.env` and customize)
- `client/Dockerfile` - Build configuration for the Ngino tunnel client
- `client/compose.yml` - Docker Compose configuration for the client
- `client/.env.example` - Example environment variables for the client (copy to `.env` and customize)

A pre-built server image is available at `git.ld50.dev/ngino/ngino-server:latest`.

## Quick Start

1. Copy `.env.example` to `.env` and customize the values:

```bash
cp .env.example .env
```

2. Start the services:

```bash
docker-compose up -d
```

3. Access the admin UI at `http://localhost:5050/admin`

## Production Setup

For production, you should:

1. Update the `NGINO_TOKEN` in `.env` to a secure random value:
   ```bash
   NGINO_TOKEN=$(openssl rand -base64 32)
   ```

2. Update MySQL passwords in `.env`

3. Configure SSL/TLS termination (see "SSL/TLS Setup" below)

4. Consider using named volumes for data persistence

## Docker Compose Services

### ngino-server
- Image: `git.ld50.dev/ngino/ngino-server:latest`
- Port: 5050 (host) → 8080 (container)
- Environment variables are configured in `.env`
- Data volume: `ngino-appdata` for application data

### mysql
- Port: 13306 (host) → 3306 (container) for MySQL access from the host
- Database: `elmah`
- User: `elmah`
- Data volume: `mysql-data`

## Client in Docker

The client container runs on the GPU workstation and spawns llama.cpp sibling containers through the host Docker daemon (`/var/run/docker.sock`). It uses host networking so it can reach those containers and the local Ollama fallback on `localhost`.

1. Copy the client env example and customize:

```bash
cd deploy/docker/client
cp .env.example .env
nano .env   # set NGINO_SERVER, NGINO_TOKEN, NGINO_USE_OLLAMA_MODELS_PATH, ...
```

2. For AMD ROCm, uncomment the `devices` block in `compose.yml` (so the client detects the GPU and picks the ROCm image). For NVIDIA, set `NGINO_LLAMA_CPP_DOCKER_IMAGE=ghcr.io/ggml-org/llama.cpp:server-cuda` in `.env`.

3. Build and start:

```bash
docker compose up -d --build
docker compose logs -f ngino-client
```

Notes:

- The Ollama models directory is mounted read-only at its **identical host path** inside the container. This is required because the client passes that path to sibling `docker run -v` calls, which are resolved by the host daemon against the host filesystem.
- The client must run as root inside the container to access the Docker socket.
- Stopping the container leaves already-spawned llama.cpp containers running; a restarted client reuses them. Remove them with `docker ps -q --filter label=ngino-llamacpp | xargs docker rm -f`.
- Models whose GGUF blob cannot be served by llama.cpp automatically fall back to `NGINO_UPSTREAM` (Ollama), same as the native client.

## Building the Server Image

To build the server image from source:

```bash
docker build -t git.ld50.dev/ngino/ngino-server:latest \
  --file deploy/docker/server/Dockerfile .
```

## Building the Client Image

To build the client image from source:

```bash
docker build -t git.ld50.dev/ngino/ngino-client:latest \
  --file deploy/docker/client/Dockerfile .
```

## SSL/TLS Setup

For production, you should terminate SSL/TLS using a reverse proxy like Nginx or Traefik.

### Example Nginx Configuration

```nginx
server {
    listen 443 ssl http2;
    server_name your-domain.com;

    ssl_certificate /path/to/cert.pem;
    ssl_certificate_key /path/to/key.pem;

    location / {
        proxy_pass http://localhost:5050;
        proxy_http_version 1.1;
        proxy_set_header Upgrade $http_upgrade;
        proxy_set_header Connection "upgrade";
        proxy_set_header Host $host;
        proxy_cache off;
        proxy_buffering off;
    }
}
```

## Troubleshooting

### Server Not Starting

1. Check the container is running: `docker-compose ps`
2. Check server logs: `docker-compose logs ngino-server`
3. Verify the MySQL container is up and reachable
4. Confirm `NGINO_TOKEN` is set in `.env`

### Clients Can't Connect

If a client can't connect to the server:

1. Make sure the server is running: `docker-compose ps`
2. Check server logs: `docker-compose logs ngino-server`
3. Verify the server URL and token in the client configuration
4. For clients running in Docker on the same host, use `host.docker.internal` instead of `localhost`

### View Logs

View all logs:

```bash
docker-compose logs -f
```

View specific service logs:

```bash
docker-compose logs -f ngino-server
docker-compose logs -f ngino-mysql
```

### Check Container Status

```bash
docker-compose ps
```

### Stop Services

Stop services without removing containers:

```bash
docker-compose stop
```

Stop and remove containers:

```bash
docker-compose down
```

To also remove volumes (deletes all data!):

```bash
docker-compose down -v
```

## Migration from Default Configuration

The Docker image uses MySQL for Elmah error logging instead of the default in-memory configuration. The database is automatically created on first run.

If you want to use SQLite instead (not recommended for production), modify the `appsettings.json` in a custom build or mount a custom config file.
