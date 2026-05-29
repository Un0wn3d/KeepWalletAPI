#!/usr/bin/env bash
set -euo pipefail

POSTGRES_DB="${POSTGRES_DB:-KeepWallet}"
POSTGRES_USER="${POSTGRES_USER:-KeepWallet}"
POSTGRES_PASSWORD="${POSTGRES_PASSWORD:-KeepWallet}"
POSTGRES_PORT="${POSTGRES_PORT:-5432}"
API_PORT="${API_PORT:-8080}"
PGDATA="${PGDATA:-/var/lib/postgresql/data}"

export ASPNETCORE_URLS="${ASPNETCORE_URLS:-http://+:${API_PORT}}"
export ConnectionStrings__DefaultConnection="${ConnectionStrings__DefaultConnection:-Host=127.0.0.1;Port=${POSTGRES_PORT};Database=${POSTGRES_DB};Username=${POSTGRES_USER};Password=${POSTGRES_PASSWORD}}"
export Jwt__Issuer="${Jwt__Issuer:-${JWT_ISSUER:-KeepWalletAPI}}"
export Jwt__Audience="${Jwt__Audience:-${JWT_AUDIENCE:-KeepWalletClient}}"
export Jwt__Key="${Jwt__Key:-${JWT_KEY:-replace-this-with-a-long-random-secret-key-min-32-chars}}"
export Auth__UseSecureCookies="${Auth__UseSecureCookies:-${AUTH_USE_SECURE_COOKIES:-false}}"

mkdir -p "${PGDATA}" /run/postgresql
chown -R postgres:postgres "${PGDATA}" /run/postgresql
chmod 700 "${PGDATA}"

find_pg_bin() {
    local name="$1"

    if command -v "${name}" >/dev/null 2>&1; then
        command -v "${name}"
        return 0
    fi

    local candidate
    candidate="$(find /usr/lib/postgresql -type f -name "${name}" 2>/dev/null | sort -V | tail -n 1 || true)"
    if [[ -n "${candidate}" ]]; then
        echo "${candidate}"
        return 0
    fi

    echo "Required PostgreSQL binary '${name}' was not found." >&2
    return 1
}

postgres_bin="$(find_pg_bin postgres)"
pg_ctl_bin="$(find_pg_bin pg_ctl)"
initdb_bin="$(find_pg_bin initdb)"
psql_bin="$(find_pg_bin psql)"

echo "Using PostgreSQL binaries from $(dirname "${postgres_bin}")"

wait_for_postgres() {
    local attempt
    for attempt in $(seq 1 60); do
        if gosu postgres "${psql_bin}" -p "${POSTGRES_PORT}" -U postgres -d postgres -c "SELECT 1;" >/dev/null 2>&1; then
            return 0
        fi
        sleep 1
    done

    echo "PostgreSQL did not become ready in time." >&2
    return 1
}

start_postgres_bg() {
    gosu postgres "${postgres_bin}" -D "${PGDATA}" -c "listen_addresses=127.0.0.1" -p "${POSTGRES_PORT}" &
    POSTGRES_PID=$!
    wait_for_postgres
}

stop_postgres() {
    if gosu postgres "${psql_bin}" -p "${POSTGRES_PORT}" -U postgres -d postgres -c "SELECT 1;" >/dev/null 2>&1; then
        gosu postgres "${pg_ctl_bin}" -D "${PGDATA}" -m fast -w stop >/dev/null 2>&1 || true
    fi
}

APP_PID=""
POSTGRES_PID=""

cleanup() {
    if [[ -n "${APP_PID}" ]] && kill -0 "${APP_PID}" >/dev/null 2>&1; then
        kill "${APP_PID}" >/dev/null 2>&1 || true
        wait "${APP_PID}" >/dev/null 2>&1 || true
    fi

    stop_postgres

    if [[ -n "${POSTGRES_PID}" ]] && kill -0 "${POSTGRES_PID}" >/dev/null 2>&1; then
        wait "${POSTGRES_PID}" >/dev/null 2>&1 || true
    fi
}

trap cleanup EXIT SIGINT SIGTERM

if [[ ! -s "${PGDATA}/PG_VERSION" ]]; then
    echo "Initializing PostgreSQL data directory at ${PGDATA}"
    gosu postgres "${initdb_bin}" -D "${PGDATA}" >/dev/null
    start_postgres_bg

    gosu postgres "${psql_bin}" -v ON_ERROR_STOP=1 -p "${POSTGRES_PORT}" -U postgres -d postgres <<SQL
DO \$\$
BEGIN
    IF NOT EXISTS (SELECT 1 FROM pg_roles WHERE rolname = '${POSTGRES_USER}') THEN
        EXECUTE format('CREATE ROLE %I LOGIN PASSWORD %L', '${POSTGRES_USER}', '${POSTGRES_PASSWORD}');
    ELSE
        EXECUTE format('ALTER ROLE %I WITH LOGIN PASSWORD %L', '${POSTGRES_USER}', '${POSTGRES_PASSWORD}');
    END IF;
END
\$\$;

SELECT 'CREATE DATABASE "${POSTGRES_DB}" OWNER "${POSTGRES_USER}"'
WHERE NOT EXISTS (SELECT 1 FROM pg_database WHERE datname = '${POSTGRES_DB}')
\gexec
SQL

    export PGPASSWORD="${POSTGRES_PASSWORD}"
    for sql_file in /docker-entrypoint-initdb.d/*.sql; do
        gosu postgres "${psql_bin}" -v ON_ERROR_STOP=1 -h 127.0.0.1 -p "${POSTGRES_PORT}" -U "${POSTGRES_USER}" -d "${POSTGRES_DB}" -f "${sql_file}"
    done
    unset PGPASSWORD

    stop_postgres
    POSTGRES_PID=""
fi

echo "Starting PostgreSQL on port ${POSTGRES_PORT}"
start_postgres_bg

echo "Starting KeepWallet API on port ${API_PORT}"
dotnet /app/KeepWalletAPI.dll &
APP_PID=$!

wait -n "${POSTGRES_PID}" "${APP_PID}"
