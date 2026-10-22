#!/bin/sh
set -eu

for database in fcg_catalog fcg_payments fcg_notifications; do
  exists="$(psql --set=ON_ERROR_STOP=1 --tuples-only --no-align \
    --command="SELECT 1 FROM pg_database WHERE datname = '${database}'")"

  if [ "$exists" != "1" ]; then
    psql --set=ON_ERROR_STOP=1 --command="CREATE DATABASE \"${database}\""
    printf 'Created database %s\n' "$database"
  else
    printf 'Database %s already exists\n' "$database"
  fi
done
