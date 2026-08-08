#!/usr/bin/env bash
# Rebuilds the local development database from scratch.
#
#   ./scripts/dev-db-reset.sh
#
# Why this exists instead of `dotnet ef database update`: the migration history in this
# repo starts mid-life. No migration ever creates Quizzes, words, and the other original
# tables, because the production database predates the first migration. Running the
# migrations against an empty database therefore fails on the first ALTER.
#
# So the schema is created from the *current model* with `dotnet ef dbcontext script`,
# and every existing migration is then recorded as already applied. The result is a
# database that matches the model and accepts future migrations normally.
#
# This affects local development only. Production keeps applying migrations in order.
set -euo pipefail

cd "$(dirname "$0")/.."

CONTAINER=glosify-sql
DB=glosifydb
SA_PASSWORD=Local_Dev_Only_1
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

echo "==> Scripting the current model"
SCHEMA=$(mktemp -t glosify-schema)
trap 'rm -f "$SCHEMA"' EXIT
dotnet ef dbcontext script --project Glosify/Glosify.csproj --no-build -o "$SCHEMA" >/dev/null
# sqlcmd chokes on the UTF-8 BOM that the scripter emits.
perl -i -pe 's/^\x{ef}\x{bb}\x{bf}// if $. == 1' "$SCHEMA"

# The model and the real database disagree here. Migration 20260529191258_AddCollections
# created FK_Quizzes_Collections_CollectionId with NO ACTION, but the model leaves the
# optional relationship unconfigured, so the scripter emits EF's SET NULL default. That
# gives AspNetUsers two delete paths into Quizzes (direct, and via Collections) and SQL
# Server rejects the whole batch with "may cause cycles or multiple cascade paths".
# Production has NO ACTION, so that is what local development should have too.
perl -i -pe 's/(FK_Quizzes_Collections_CollectionId.*?)\s+ON DELETE SET NULL/$1/' "$SCHEMA"

echo "==> Creating the schema"
# sqlcmd reads the script from stdin; `-i /dev/stdin` does not work through docker exec.
# It also reports errors on stdout, so failures are surfaced rather than discarded.
"${SQLCMD[@]}" -d "$DB" < "$SCHEMA"

echo "==> Recording existing migrations as applied"
PRODUCT_VERSION=$(dotnet ef --version 2>/dev/null | tail -1 | tr -d '[:space:]')
{
    echo "SET NOCOUNT ON;"
    echo "CREATE TABLE [__EFMigrationsHistory] ("
    echo "  [MigrationId] nvarchar(150) NOT NULL,"
    echo "  [ProductVersion] nvarchar(32) NOT NULL,"
    echo "  CONSTRAINT [PK___EFMigrationsHistory] PRIMARY KEY ([MigrationId]));"
    for file in Glosify/Migrations/*.cs; do
        case "$file" in
            *.Designer.cs | *ModelSnapshot.cs) continue ;;
        esac
        id=$(basename "$file" .cs)
        echo "INSERT INTO [__EFMigrationsHistory] VALUES (N'$id', N'$PRODUCT_VERSION');"
    done
} | "${SQLCMD[@]}" -d "$DB"

COUNT=$("${SQLCMD[@]}" -d "$DB" -h -1 -W \
    -Q "SET NOCOUNT ON; SELECT COUNT(*) FROM sys.tables;" | tr -d '[:space:]')
MIGRATIONS=$("${SQLCMD[@]}" -d "$DB" -h -1 -W \
    -Q "SET NOCOUNT ON; SELECT COUNT(*) FROM [__EFMigrationsHistory];" | tr -d '[:space:]')

echo
echo "Done. [$DB] has $COUNT tables and $MIGRATIONS migrations recorded as applied."
echo "Run 'dotnet run --project Glosify' and register a local account at"
echo "https://localhost:7032/Identity/Account/Register to sign in."
