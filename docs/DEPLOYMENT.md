# Deployment options

Query Gate is a .NET 10 interactive server application backed by SQL Server. Deploy it at the root of a host name, not under a URL subpath. Interactive sessions use SignalR, so allow WebSocket connections and long-lived requests through the hosting platform and any network intermediaries.

## Before deploying

1. Provision an application database on a reachable SQL Server. Use a dedicated SQL account, TLS and a trusted SQL Server certificate. The account needs schema permissions while applying migrations and read/write access during normal operation.
2. Generate an `Encryption:Key` with `openssl rand -base64 32` and an independent `Jwt:Key` with `openssl rand -base64 64`. Back up both securely. Changing or losing the encryption key makes stored managed database connection strings unreadable.
3. Configure `BootstrapAdmin:Email` and `BootstrapAdmin:Password` for initial provisioning. The password needs at least 12 characters, upper and lower case letters, a digit and a non-alphanumeric character. Remove both settings after the administrator has been created. They can otherwise promote the configured user to admin on every startup.
4. Configure `DataProtection:KeyPath` as a persistent writable directory outside the publish directory. Back it up securely. The key files contain sensitive material and are not encrypted by this configuration; protect the directory and backups at rest. Retaining them preserves browser sessions and protected tokens across restarts.
5. Set `Database:ApplyMigrationsOnStartup` to `true` for the first start. Start only one instance, verify that migrations completed, then set it to `false` and restart. For upgrades, back up the database first and repeat with a single instance. Reduce schema permissions after migrations where practical.

The application requires a database connection even at startup. Set the environment to `Production`, use HTTPS and protect credentials in the hosting platform's secret store. Configuration keys use `__` instead of `:` in environment variables, for example `ConnectionStrings__DefaultConnection`. Use a separate least-privilege account for each managed database registered in Query Gate.

For these instructions, use a single app instance. Scaling interactive server sessions requires session affinity, shared data protection keys and additional SignalR and infrastructure planning. See [Microsoft's Blazor hosting guidance](https://learn.microsoft.com/en-us/aspnet/core/blazor/host-and-deploy/server/?view=aspnetcore-10.0).

## Docker

Use the [Docker Compose guide](./DOCKER.md) to build the image and run it with HTTPS, a persistent data protection volume and an existing SQL Server.

## IIS on Windows

1. Install IIS with the WebSocket Protocol feature and the [.NET 10 Hosting Bundle](https://dotnet.microsoft.com/download/dotnet/10.0) on the server. Restart IIS after installing the bundle.
2. From the project directory on a build machine with the .NET 10 SDK, publish the app:

   ```powershell
   dotnet publish DbAccessManager.csproj -c Release -o ./publish
   ```

3. Copy the contents of `publish` to a directory on the server. Preserve the generated `web.config`. Create an IIS site rooted at this directory and an application pool set to **No Managed Code**. Bind HTTPS using a trusted certificate matching the host name.
4. Grant the application pool identity read access to the published files and read/write access to a separate persistent key directory. Set the configuration below as environment variables for the application process, for example using protected IIS `web.config` environment variable settings or machine-level variables. Restrict access to configuration containing secrets. IIS must be restarted or the app pool recycled after changes.

   | Environment variable | Value |
   | --- | --- |
   | `ASPNETCORE_ENVIRONMENT` | `Production` |
   | `ConnectionStrings__DefaultConnection` | SQL Server application database connection string |
   | `Encryption__Key` | Base64 32-byte encryption key |
   | `Jwt__Key` | Independent, persistent JWT signing secret |
   | `BootstrapAdmin__Email`, `BootstrapAdmin__Password` | Initial administrator credentials |
   | `DataProtection__KeyPath` | Persistent writable directory, for example `C:\QueryGate\keys` |
   | `Database__ApplyMigrationsOnStartup` | `true` only while applying migrations |

5. Start the site and check application and IIS logs. Sign in over HTTPS, then remove bootstrap credentials, disable startup migrations and recycle the pool. Check `/health` and test the query workspace. If sign-in or interactivity fails, check SQL connectivity, certificate trust, WebSockets and the app pool identity's permissions.

See [Microsoft's IIS hosting guide](https://learn.microsoft.com/en-us/aspnet/core/host-and-deploy/iis/?view=aspnetcore-10.0) for server setup and environment-variable configuration.

## Azure App Service (Windows)

1. Create a **Windows** App Service with a .NET 10 runtime and one instance. Provide network access to the application SQL Server, including firewall and private networking rules as needed. Enable **HTTPS Only** and **Web sockets** in the App Service settings.
2. In App Service environment variables, configure the same keys listed in the IIS table. Set `DataProtection__KeyPath` to `D:\home\query-gate-keys`, a persistent directory outside the deployed application files. Restrict access to the App Service and backups because the key files are not encrypted by the app. Use Key Vault references or another approved secret management method for credentials.
3. Publish and deploy the *contents* of the publish directory, including `web.config`, using your App Service deployment method:

   ```bash
   dotnet publish DbAccessManager.csproj -c Release -o ./publish
   ```

   For zip deployment, create the zip with the contents of `publish` at its root, then deploy it with `az webapp deploy --resource-group <group> --name <app-name> --src-path <zip-file> --type zip`.
4. Check App Service logs and `/health`, then sign in using HTTPS. After migrations and initial admin creation, set `Database__ApplyMigrationsOnStartup` to `false` and remove both bootstrap settings. App Service settings changes restart the app.

Use the Windows hosting model described above. For other hosting configurations and reverse proxies, follow [Microsoft's App Service](https://learn.microsoft.com/en-us/aspnet/core/host-and-deploy/azure-apps/?view=aspnetcore-10.0) and [proxy configuration guidance](https://learn.microsoft.com/en-us/aspnet/core/host-and-deploy/proxy-load-balancer?view=aspnetcore-10.0), including trusted forwarded headers. Do not trust forwarded headers from arbitrary clients.

## Linux server with systemd

This example runs Kestrel HTTPS directly on port 8443. Use a trusted password-protected PFX certificate matching the host name, and ensure the service account can read it. Configure the firewall to permit the intended clients. The example uses `/opt/query-gate` for published files, `/var/lib/query-gate/keys` for persistent data protection keys, and `/etc/query-gate.env` for secrets.

1. Install the .NET 10 ASP.NET Core runtime on the server. Publish on a machine with the .NET 10 SDK and copy the publish directory contents to `/opt/query-gate`:

   ```bash
   dotnet publish DbAccessManager.csproj -c Release -o ./publish
   ```

2. Create a dedicated `querygate` system user. Grant it read access to the published files and PFX and write access to `/var/lib/query-gate/keys`. Protect the environment file and certificate from other users.
3. Create `/etc/query-gate.env` with the following settings, replacing all placeholders with securely generated values. This is a systemd environment file, not a Compose `.env` file. Quote values as required by systemd and keep the file readable only by the service manager.

   ```text
   ASPNETCORE_ENVIRONMENT=Production
   ASPNETCORE_URLS=https://0.0.0.0:8443
   ASPNETCORE_HTTPS_PORTS=8443
   ASPNETCORE_Kestrel__Certificates__Default__Path=/etc/query-gate/query-gate.pfx
   ASPNETCORE_Kestrel__Certificates__Default__Password=<pfx-password>
   ConnectionStrings__DefaultConnection="Server=<sql-host>;Database=QueryGate;User Id=<user>;Password=<password>;Encrypt=True;TrustServerCertificate=False"
   Encryption__Key=<base64-32-byte-key>
   Jwt__Key=<independent-jwt-secret>
   BootstrapAdmin__Email=<admin-email>
   BootstrapAdmin__Password=<strong-password>
   DataProtection__KeyPath=/var/lib/query-gate/keys
   Database__ApplyMigrationsOnStartup=true
   ```

4. Create `/etc/systemd/system/query-gate.service`:

   ```ini
   [Unit]
   Description=Query Gate
   After=network-online.target
   Wants=network-online.target

   [Service]
   WorkingDirectory=/opt/query-gate
   ExecStart=/usr/bin/dotnet /opt/query-gate/DbAccessManager.dll
   User=querygate
   EnvironmentFile=/etc/query-gate.env
   Restart=on-failure
   RestartSec=10

   [Install]
   WantedBy=multi-user.target
   ```

   Adjust the `dotnet` path if the runtime is installed elsewhere.
5. Run `sudo systemctl daemon-reload` and `sudo systemctl enable --now query-gate`. Check `sudo journalctl -u query-gate -f` and open `https://<host>:8443/health`. Sign in, disable startup migrations and remove bootstrap settings from the environment file, then run `sudo systemctl restart query-gate`.

If terminating HTTPS at a reverse proxy instead, configure WebSockets and [trusted forwarded headers](https://learn.microsoft.com/en-us/aspnet/core/host-and-deploy/proxy-load-balancer?view=aspnetcore-10.0). Restrict direct access to Kestrel and do not accept forwarded headers from untrusted clients. Verify HTTPS redirects, client IP audit records, sign-in and interactive sessions after configuring the proxy.
