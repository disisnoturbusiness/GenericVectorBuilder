#!/usr/bin/env bash
# Drops the renamed copies of ClickHouse's system log tables in the benchmark container (gvb-clickhouse).
#
# Run it once after the first start with a changed clickhouse-config/gvb-system-logs.xml. When the TTL
# (or any other definition) of a system log table changes, ClickHouse renames the table it finds on disk
# to <name>_0 (then _1 and so on) at the first flush and starts a new one. The renamed copy has the old
# definition, so it never expires and keeps its rows for good. This drops every MergeTree table in the
# system database whose name ends in an underscore and a number (SYNC, so the files go now and not after
# the 480 s drop delay). It never touches a live log table: those are named exactly as the image names
# them. The data is benchmark-only logs, nothing else.
# It flushes the logs first: ClickHouse renames a table whose definition changed at the first flush, not
# at start, so a script that listed the tables before that flush would miss the renamed copies.
# Safe to run again: with nothing left to drop it only prints the folder size twice.
set -euo pipefail

SECRETS="${GVB_ENGINE_SECRETS:-/home/dan/gvb-data/engines/secrets.env}"
URL="http://127.0.0.1:8123/"
CONTAINER="gvb-clickhouse"
DATA_FOLDER="/var/lib/clickhouse"
LIMIT_SECONDS=300
RENAMED_COPY_PATTERN='_[0-9]+$'

PASSWORD="$(grep '^CLICKHOUSE_PASSWORD=' "$SECRETS" | tail -n 1 | cut -d= -f2-)"
if [ -z "$PASSWORD" ]
then
   echo "CLICKHOUSE_PASSWORD is not in $SECRETS" >&2
   exit 1
fi

# Runs one statement; fails loud on a connection problem, an HTTP error or a timeout.
run()
{
   curl --silent --show-error --fail-with-body --max-time "$LIMIT_SECONDS" \
      --config <(printf 'header = "X-ClickHouse-User: gvb"\nheader = "X-ClickHouse-Key: %s"\n' "$PASSWORD") \
      --data-binary "$1" "$URL"
}

# Bytes in the data folder, as the container sees it.
folder_bytes()
{
   timeout "$LIMIT_SECONDS" docker exec "$CONTAINER" du -sb "$DATA_FOLDER" | cut -f1
}

run "SYSTEM FLUSH LOGS" > /dev/null
before="$(folder_bytes)"
echo "data folder before: $before bytes"
tables="$(run "SELECT name FROM system.tables WHERE database = 'system' AND engine = 'MergeTree' AND match(name, '$RENAMED_COPY_PATTERN') ORDER BY name FORMAT TSV")"
for table in $tables
do
   run "DROP TABLE system.$table SYNC" > /dev/null
   echo "dropped system.$table"
done

after="$(folder_bytes)"
echo "data folder after: $after bytes"
