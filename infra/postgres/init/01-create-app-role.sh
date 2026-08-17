#!/bin/sh
# ---------------------------------------------------------------------------
# Creates the restricted runtime database role.
#
# Runs once, on first initialisation of the data volume.
#
# Two roles, on purpose:
#
#   POSTGRES_USER  (superuser)  - used ONLY by the migration job. Owns the schema
#                                 and holds DDL rights.
#   APP_DB_USER    (restricted) - used by the Admin API and the Agent API. Created
#                                 here with no object privileges at all; the initial
#                                 migration grants it exactly what it needs, which
#                                 on the audit table is INSERT and SELECT and
#                                 nothing else.
#
# That split is what makes "the application cannot rewrite history" a property of
# the database rather than a promise made by application code.
# ---------------------------------------------------------------------------
set -eu

: "${APP_DB_USER:?APP_DB_USER must be set}"
: "${APP_DB_PASSWORD:?APP_DB_PASSWORD must be set}"

echo "endpoint-platform: creating restricted application role '${APP_DB_USER}'..."

# Values are passed as psql variables rather than interpolated by the shell.
# format() with %I / %L then emits a correctly quoted identifier and literal, so
# neither the role name nor the password can break out of its syntactic context.
# \gexec runs each generated statement - note that psql does NOT substitute
# variables inside dollar-quoted blocks, which is why this uses \gexec instead of
# a DO $$ ... $$ block.
psql -v ON_ERROR_STOP=1 \
     --username "$POSTGRES_USER" \
     --dbname "$POSTGRES_DB" \
     --set=appuser="$APP_DB_USER" \
     --set=apppassword="$APP_DB_PASSWORD" \
     --set=dbname="$POSTGRES_DB" <<'EOSQL'
\echo 'Creating role if absent...'
SELECT format('CREATE ROLE %I LOGIN PASSWORD %L', :'appuser', :'apppassword')
WHERE NOT EXISTS (SELECT 1 FROM pg_roles WHERE rolname = :'appuser');
\gexec

\echo 'Ensuring role password and LOGIN attribute...'
SELECT format('ALTER ROLE %I LOGIN PASSWORD %L', :'appuser', :'apppassword');
\gexec

-- The application role must never be able to create databases or roles.
SELECT format('ALTER ROLE %I NOSUPERUSER NOCREATEDB NOCREATEROLE NOREPLICATION NOBYPASSRLS', :'appuser');
\gexec

-- Deny the implicit PUBLIC grants. Anything the application needs is granted
-- explicitly by the initial migration.
REVOKE ALL ON SCHEMA public FROM PUBLIC;

SELECT format('REVOKE ALL ON DATABASE %I FROM PUBLIC', :'dbname');
\gexec

SELECT format('GRANT CONNECT ON DATABASE %I TO %I', :'dbname', :'appuser');
\gexec

\echo 'Restricted application role is ready.'
EOSQL

echo "endpoint-platform: application role '${APP_DB_USER}' created."
