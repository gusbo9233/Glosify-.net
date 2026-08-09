#!/usr/bin/env bash
# Rebuilds the local development database from scratch.
#
#   ./scripts/dev-db-reset.sh
#
# The checked-in migration history starts with a complete InitialCreate migration, so
# local development and CI exercise the same supported path as a new deployment.
set -euo pipefail

cd "$(dirname "$0")/.."

CONTAINER=glosify-sql
DB=glosifydb
SA_PASSWORD=Local_Dev_Only_1
LOCAL_CONNECTION_STRING="Server=localhost,1433;Database=$DB;User Id=sa;Password=$SA_PASSWORD;Encrypt=True;TrustServerCertificate=True;"
# -C trusts the container's self-signed certificate, -b aborts on the first error, and
# -I enables QUOTED_IDENTIFIER, which the filtered indexes in this schema require.
SQLCMD=(docker exec -i "$CONTAINER" /opt/mssql-tools18/bin/sqlcmd
        -S localhost -U sa -P "$SA_PASSWORD" -C -b -I)

if ! docker ps --format '{{.Names}}' | grep -qx "$CONTAINER"; then
    echo "The '$CONTAINER' container is not running. Start it with:"
    echo "  docker compose -f docker-compose.dev.yml up -d"
    exit 1
fi

echo "==> Dropping and recreating [$DB]"
"${SQLCMD[@]}" -Q "
    IF DB_ID('$DB') IS NOT NULL
    BEGIN
        ALTER DATABASE [$DB] SET SINGLE_USER WITH ROLLBACK IMMEDIATE;
        DROP DATABASE [$DB];
    END
    CREATE DATABASE [$DB];" >/dev/null

echo "==> Applying EF Core migrations"
dotnet tool restore >/dev/null
# Do not let user secrets or an inherited environment variable redirect this
# destructive development helper to Azure or any other database.
ConnectionStrings__DefaultConnection="$LOCAL_CONNECTION_STRING" \
dotnet ef database update \
    --project Glosify/Glosify.csproj \
    --no-build

COUNT=$("${SQLCMD[@]}" -d "$DB" -h -1 -W \
    -Q "SET NOCOUNT ON; SELECT COUNT(*) FROM sys.tables;" | tr -d '[:space:]')
MIGRATIONS=$("${SQLCMD[@]}" -d "$DB" -h -1 -W \
    -Q "SET NOCOUNT ON; SELECT COUNT(*) FROM [__EFMigrationsHistory];" | tr -d '[:space:]')

echo
echo "Done. [$DB] has $COUNT tables and $MIGRATIONS migration(s) recorded as applied."
echo "Run 'dotnet run --project Glosify' and register a local account at"
echo "https://localhost:7032/Account/Register to sign in."
