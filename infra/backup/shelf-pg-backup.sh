#!/usr/bin/env bash
# Dumps the Shelf PostgreSQL database and uploads it to a separate S3-compatible bucket.
# Usage and configuration: infra/storage.md. Requires pg_dump and pg_restore (server major
# version or newer), curl >= 7.76 and coreutils. PostgreSQL connection: standard libpq
# variables (PGHOST, PGPORT, PGDATABASE, PGUSER, PGPASSWORD or PGPASSFILE, PGSSLMODE).
set -euo pipefail

: "${SHELF_BACKUP_S3_ENDPOINT:?set SHELF_BACKUP_S3_ENDPOINT}"
: "${SHELF_BACKUP_S3_REGION:?set SHELF_BACKUP_S3_REGION}"
: "${SHELF_BACKUP_S3_BUCKET:?set SHELF_BACKUP_S3_BUCKET}"
: "${SHELF_BACKUP_S3_ACCESS_KEY:?set SHELF_BACKUP_S3_ACCESS_KEY}"
: "${SHELF_BACKUP_S3_SECRET_KEY:?set SHELF_BACKUP_S3_SECRET_KEY}"
prefix="${SHELF_BACKUP_S3_PREFIX:-postgres/}"

work="$(mktemp -d)"
trap 'rm -rf "$work"' EXIT
dump="$work/shelf.dump"

pg_dump --no-password --format=custom --file="$dump"
# A truncated or unreadable archive fails here instead of being uploaded.
pg_restore --list "$dump" >/dev/null

key="${prefix}shelf-$(date -u +%Y%m%dT%H%M%SZ).dump"
endpoint="${SHELF_BACKUP_S3_ENDPOINT%/}"
if [ "${SHELF_BACKUP_S3_FORCE_PATH_STYLE:-false}" = true ]; then
  url="$endpoint/$SHELF_BACKUP_S3_BUCKET/$key"
else
  url="${endpoint%%://*}://$SHELF_BACKUP_S3_BUCKET.${endpoint#*://}/$key"
fi

# Credentials reach curl through its standard input, never its arguments (visible in ps).
# Some curl versions omit x-amz-content-sha256, which AWS S3 requires: send it explicitly.
printf 'user = "%s:%s"\n' "$SHELF_BACKUP_S3_ACCESS_KEY" "$SHELF_BACKUP_S3_SECRET_KEY" |
  curl --silent --show-error --fail-with-body --config - \
    --aws-sigv4 "aws:amz:$SHELF_BACKUP_S3_REGION:s3" \
    --header "x-amz-content-sha256: $(sha256sum "$dump" | cut -d' ' -f1)" \
    --upload-file "$dump" "$url"

echo "Uploaded $key ($(stat -c %s "$dump") bytes)"
