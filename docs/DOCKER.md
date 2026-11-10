# Docker deployment

The Compose setup runs Query Gate with sensible defaults so it works out-of-the-box for local evaluation, container environments, or production deployments.

## Quickstart (Zero Configuration)

You only need Docker Engine with Docker Compose installed.

1. Clone the repository:

   ```bash
   git clone https://github.com/arjunreddy-001/query-gate.git
   cd query-gate
   ```

2. Start the services:

   ```bash
   docker compose up -d
   docker compose logs -f query-gate
   ```

3. Open `http://localhost:5176` in your browser.
4. On your first visit, Query Gate launches the setup wizard (`/Account/Setup`) where you can create your root administrator account directly in the browser (Appwrite style).
5. Once created, sign in and start registering databases and inviting team members.

The stack automatically starts:

- A bundled **SQL Server 2022** container with data persisted in the `mssql-data` volume.
- Automatic Entity Framework Core migrations on startup.
- Auto-generated 32-byte encryption key and 64-byte JWT key persisted in the `data-protection-keys` volume.

## HTTPS Requirement

Query Gate uses Blazor Server interactive circuits (WebSockets), antiforgery tokens, and secure cookies. In production or remote deployments, browsers require a secure context (HTTPS). Accessing the application over unencrypted plain HTTP on remote hosts will cause interactive actions (such as database registration or query execution in the workspace) to fail.

To ensure full functionality, Query Gate must be accessed over HTTPS. Two setups are supported:

1. **Reverse proxy or Coolify (Recommended)**: Your external reverse proxy terminates TLS and routes traffic to port `5176`.
2. **Direct Kestrel development certificate**: Mount a generated `.pfx` certificate for local container testing.

## Reverse Proxies and Coolify

When deploying Query Gate in production, it is standard practice to run the container behind a reverse proxy (such as Caddy, Nginx, or Traefik) or a deployment manager like Coolify.

Query Gate listens on plain HTTP port 5176 inside the container. The reverse proxy handles external HTTPS traffic, manages SSL/TLS certificates, and proxies requests internally to port 5176. `ASPNETCORE_FORWARDEDHEADERS_ENABLED` is enabled by default so forwarded HTTPS headers (`X-Forwarded-Proto`, `X-Forwarded-For`) are recognized automatically.

### Deploying on Coolify

Query Gate can be deployed directly as a Docker Compose resource in Coolify:

1. In Coolify, create a new Project and select **Docker Compose**.
2. Connect your Git repository or paste the `compose.yaml` contents.
3. Set your custom domain (for example, `querygate.yourdomain.com`). Coolify will configure Traefik or Caddy to proxy traffic to internal port `5176` and issue Let's Encrypt TLS certificates automatically.
4. Deploy the application.
5. Visit your domain and complete the web setup wizard to create your root administrator account.

### Direct Kestrel HTTPS (Optional Dev Cert)

If you want the ASP.NET Core Kestrel server to serve HTTPS directly:

1. Generate a development certificate on your host machine:

   ```bash
   dotnet dev-certs https -ep ./cert.pfx -p "DevPassword123"
   ```

2. Mount `./cert.pfx:/https/cert.pfx:ro` and configure `ASPNETCORE_URLS`, `ASPNETCORE_Kestrel__Certificates__Default__Path`, and `ASPNETCORE_Kestrel__Certificates__Default__Password` in `compose.yaml` or `.env`.

### Forwarded Headers

Ensure any external reverse proxy forwards `X-Forwarded-For` and `X-Forwarded-Proto` headers so client IP addresses and HTTPS schemes are recorded accurately in Query Gate audit logs and circuit handlers.

## Customizing via .env

Copy `.env.example` to `.env` to override defaults (all settings are optional):

```bash
cp .env.example .env
```

| Variable                   | Description                                                              | Default                               |
| -------------------------- | ------------------------------------------------------------------------ | ------------------------------------- |
| `PORT`                     | Host port mapped to Query Gate                                           | `5176`                                |
| `DB_PASSWORD`              | SA password for the bundled SQL Server 2022 container                    | `QueryGateDefault!2026`               |
| `DB_CONNECTION_STRING`     | Optional custom connection string if using an external database          | (uses bundled `mssql` container)      |
| `ENCRYPTION_KEY`           | Optional static base64 32-byte key (auto-generated in volume if omitted) | (persisted in `/var/query-gate/keys`) |
| `JWT_KEY`                  | Optional static base64 64-byte key (auto-generated in volume if omitted) | (persisted in `/var/query-gate/keys`) |
| `BOOTSTRAP_ADMIN_EMAIL`    | Optional headless admin email (leave empty for web setup wizard)         | (empty)                               |
| `BOOTSTRAP_ADMIN_PASSWORD` | Optional headless admin password (leave empty for web setup wizard)      | (empty)                               |
| `APPLY_MIGRATIONS`         | Apply EF Core migrations on startup                                      | `true`                                |

### Using an External SQL Server

If you prefer to connect to an existing enterprise SQL Server instead of running the bundled container:

1. In `.env`, set `DB_CONNECTION_STRING` to your database address and credentials.
   - On Docker Desktop, use `host.docker.internal` to reach a SQL Server on the host.
   - On Linux hosts, use the host IP or bridge network IP.
2. In `compose.yaml`, you can comment out the `mssql` service and the `depends_on` block if you do not need the container.

## Operations and Upgrades

- **Logs**: `docker compose logs -f query-gate` shows startup and runtime logs.
- **Stop**: `docker compose down` stops the containers while preserving all data volumes.
- **Backups**: Back up both Docker volumes regularly:
  - `mssql-data`: contains the application database, audit logs, and configurations.
  - `data-protection-keys`: contains the encryption key used to decrypt saved database connections and user session tokens. Losing this volume makes saved connection strings unreadable if `ENCRYPTION_KEY` was not statically provided.
- **Upgrades**:
  1. Pull new code or update your image tag.
  2. Run `docker compose up -d --build`.
  3. Migrations will run automatically on startup when `APPLY_MIGRATIONS=true`.

See the [user guide](./USER-GUIDE.md) and [admin cheat sheet](./ADMIN-CHEATSHEET.md) for application usage.
