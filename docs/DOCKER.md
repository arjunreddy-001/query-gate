# Docker deployment

The Compose setup runs Query Gate as a non-root .NET 10 container with HTTPS. It uses an existing SQL Server, not a database container. It is designed for a single application instance.

## Prerequisites

- Docker Engine with Docker Compose and access to the .NET 10 images on Microsoft Container Registry.
- A reachable SQL Server and a dedicated application database. The application SQL account needs permission to apply EF Core migrations when migrations are enabled and read/write permissions for normal operation. Back up an existing database before upgrading.
- A password-protected PFX certificate whose host name matches the address users will open and whose certificate chain is trusted by their browsers. Place it outside the build context or in `certs/`, and ensure the container's non-root user can read it.
- OpenSSL to generate independent keys.

## Configure and start

1. Clone the repository and copy `.env.example` to `.env`. Restrict access to `.env`; it contains credentials. The example uses single quotes so Compose treats dollar signs literally.
2. Set `DB_CONNECTION_STRING` to the application database. From a container, `localhost` is the container, not the host. Docker Desktop supports `host.docker.internal` for a SQL Server on the host. Use a host name and port reachable from the Docker network on other platforms. Use TLS and a trusted SQL Server certificate; do not set `TrustServerCertificate=True` in production.
3. Generate an `ENCRYPTION_KEY` with `openssl rand -base64 32` and an independent `JWT_KEY` with `openssl rand -base64 64`. Store and back up both securely. The encryption key must remain unchanged to decrypt stored managed database connection strings.
4. Set `BOOTSTRAP_ADMIN_EMAIL` and `BOOTSTRAP_ADMIN_PASSWORD`. The password must be at least 12 characters and include upper and lower case letters, a digit and a non-alphanumeric character. Set `TLS_CERT_PATH` to the PFX file path, relative to `compose.yaml` or absolute, and set `TLS_CERT_PASSWORD`.
5. Set `APPLY_MIGRATIONS=true` and start **one** instance:

   ```bash
   docker compose up -d --build
   docker compose logs -f query-gate
   ```

6. Once the application starts and migrations finish, open `https://localhost:8443` or the host name covered by your certificate at port 8443. Sign in as the bootstrap administrator. Set `APPLY_MIGRATIONS=false`, remove the bootstrap email and password values from `.env`, and run `docker compose up -d` to recreate the container with the new configuration.

The certificate must match the name actually used to access the site. Port 8443 must be reachable by users. For public deployments, restrict network access appropriately and use a certificate from a trusted issuer.

## Operations and upgrades

- `docker compose logs -f query-gate` shows startup and runtime logs. `docker compose down` stops the application and preserves the named volume.
- Back up the SQL database, `ENCRYPTION_KEY`, `JWT_KEY`, and the `data-protection-keys` Docker volume securely. ASP.NET Core data protection keys in the volume are not encrypted by this setup: protect the host, backups and volume access. Keep the volume across container replacements or browser sessions and protected tokens can become invalid.
- On upgrade, back up first. Set `APPLY_MIGRATIONS=true`, rebuild and start only one instance, verify startup, then set it back to `false` and recreate the container. Migrations require elevated schema permissions; remove those permissions after the migration where practical.
- This setup terminates TLS in Kestrel. If using a reverse proxy instead, configure trusted forwarded headers and HTTPS separately; do not expose an unsecured application endpoint.
- Bootstrap credentials are used to create or promote the specified administrator on startup. Remove them after provisioning, and manage future accounts through the application.
- Managed databases are separate from the application database. Register each using a dedicated least-privilege SQL account reachable from the container.

See the [user guide](./USER-GUIDE.md) and [admin cheat sheet](./ADMIN-CHEATSHEET.md) for application setup.
