#!/bin/sh
# Build the application connection string from DB_PASSWORD when a full
# DB_CONNECTION_STRING was not provided. Compose cannot expand a ${} default
# nested inside another ${} default, which made Dokploy send a different
# password than the one SQL Server was initialized with.
set -eu

if [ -n "${DB_CONNECTION_STRING:-}" ]; then
    export ConnectionStrings__DefaultConnection="$DB_CONNECTION_STRING"
elif [ -z "${ConnectionStrings__DefaultConnection:-}" ]; then
    password="${DB_PASSWORD:-QueryGateDefault!2026}"
    server="${DB_SERVER:-mssql}"
    database="${DB_NAME:-QueryGate}"
    user="${DB_USER:-sa}"
    ConnectionStrings__DefaultConnection="$(
        printf 'Server=%s;Database=%s;User Id=%s;Password=%s;TrustServerCertificate=True;Encrypt=False' \
            "$server" "$database" "$user" "$password"
    )"
    export ConnectionStrings__DefaultConnection
fi

exec dotnet DbAccessManager.dll "$@"
