---
name: run-and-verify
description: 'Build, test, run and browser-verify the Query Gate app. Use when asked to run the project, build, run tests, start the site, check a UI change in a browser, or when dotnet build fails with a locked DbAccessManager.exe or the site will not load.'
---

# Run and verify Query Gate

.NET 10 Blazor Server app. Project: `DbAccessManager.csproj`. Tests: `tests\DbAccessManager.Tests`.

## Commands (PowerShell, no `&&`)

| Goal | Command |
|---|---|
| Build | `dotnet build DbAccessManager.csproj` |
| Tests | `dotnet test tests\DbAccessManager.Tests` |
| Run (http, port 5175) | `dotnet run` |
| Run (https, port 7245) | `dotnet run --launch-profile https` |

## Procedure

1. Check for a running app first: `Get-Process DbAccessManager`. A running app locks `bin\Debug\net10.0\DbAccessManager.exe` and `dotnet build` / `dotnet test` fail with MSB3027. Ask the user to stop it, or build to another folder: `dotnet build DbAccessManager.csproj -o <scratch-folder>`.
2. Build, then run the tests. The expected baseline is 0 errors and all tests passing.
3. To verify UI in a browser, run a scratch copy on another port so the user's instance is untouched:
   `$env:ASPNETCORE_ENVIRONMENT='Development'; $env:ASPNETCORE_URLS='http://localhost:5199'; Start-Process dotnet -ArgumentList "<scratch-folder>\DbAccessManager.dll" -WorkingDirectory <repo> -WindowStyle Hidden`
   Wait about 15 seconds. Static assets are fingerprinted, so rebuild and restart after any CSS or JS edit.
4. Never write the user's password into files. If they offer login credentials for testing, use them only in the browser session.
5. After testing, stop the scratch process by port (`Get-NetTCPConnection -LocalPort 5199`, then `Stop-Process -Id`) and delete the scratch folder and logs.

## Troubleshooting

- `object already exists` on startup: the database has tables from older migrations. See the `ef-migrations` skill.
- SQL certificate error: add `TrustServerCertificate=True` to the connection string.
- Site not loading on 7245: the default profile is http on 5175. Use `--launch-profile https`.
- Crash at startup with SQL error 40 or 64: the SQL Server is unreachable (VPN or network). Check with `Test-NetConnection <host> -Port 1433`.
- Errors are written to the `AppLogs` table and actions to `AuditLogs`. `sqlcmd -S <server> -d <db> -E -C` can read them.
- Settings and Query pages once failed only on the first render. See the `blazor-page-conventions` skill.
