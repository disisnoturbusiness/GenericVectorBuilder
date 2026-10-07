#!/usr/bin/env bash
# Writes deploy/bench/image-pins.json: for each of the 17 container targets of the vector benchmark,
# the image reference its compose file names and the image id Docker holds for that reference now.
#
# Why: the benchmark checks the image every engine container runs against these ids and ends a
# target with an error when they differ, so a run on another build of an engine (a floating tag such
# as chromadb/chroma:latest that was pulled again) is never taken for the build the earlier sessions
# measured. Run this once, after the images are pulled and before the benchmark build is frozen, and
# commit the result. The compose files are read, never changed; Docker is only asked "image inspect".
#
# Usage: deploy/bench/make-image-pins.sh
# Overrides, for tests: GVB_DOCKER (command that stands for "sudo -n docker"), GVB_ENGINES_DIR (folder of
# the compose files), GVB_PINS_OUT (file to write), GVB_DOCKER_TIMEOUT (seconds per docker call, 30).
# Fails loud (non-zero exit, a message naming the target and the step) when a compose file or its image
# line is missing, when two services of one file name different images, when Docker does not answer
# within the time limit or does not know the image, or when its answer is not a sha256 image id. The
# output file is written to a temporary file in its own folder and moved into place only when every
# target succeeded, so a failed run never leaves a partial file.
set -euo pipefail

here="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
engines="${GVB_ENGINES_DIR:-$here/../engines}"
out="${GVB_PINS_OUT:-$here/image-pins.json}"
docker_cmd="${GVB_DOCKER:-sudo -n docker}"
limit="${GVB_DOCKER_TIMEOUT:-30}"

# Target name -> compose file. sql and sql-diskann share one container, as do qdrant and qdrant-hnsw;
# the mariadb target runs in the benchmark's own gvbbench-mariadb (mariadb-bench.compose.yaml), never the daily one.
declare -A files=(
   [chroma]=chroma.compose.yaml
   [clickhouse]=clickhouse.compose.yaml
   [elasticsearch]=elasticsearch.compose.yaml
   [mariadb]=mariadb-bench.compose.yaml
   [milvus]=milvus.compose.yaml
   [mongodb]=mongodb.compose.yaml
   [opensearch]=opensearch.compose.yaml
   [oracle]=oracle.compose.yaml
   [pgvector]=pgvector.compose.yaml
   [qdrant]=qdrant.compose.yaml
   [qdrant-hnsw]=qdrant.compose.yaml
   [redis]=redis.compose.yaml
   [sql]=mssql.compose.yaml
   [sql-diskann]=mssql.compose.yaml
   [typesense]=typesense.compose.yaml
   [vespa]=vespa.compose.yaml
   [weaviate]=weaviate.compose.yaml
)

fail() {
   echo "make-image-pins: $*" >&2
   exit 1
}

command -v timeout >/dev/null || fail "the 'timeout' command is missing"
command -v python3 >/dev/null || fail "python3 is missing (it writes the JSON)"
[[ "$limit" =~ ^[0-9]+$ ]] || fail "GVB_DOCKER_TIMEOUT must be whole seconds, not '$limit'"
out_dir="$(dirname "$out")"
[[ -d "$out_dir" ]] || fail "the output folder $out_dir does not exist"

rows="$(mktemp "$out_dir/.image-pins-rows.XXXXXX")"
trap 'rm -f "$rows" "${tmp:-}"' EXIT

for target in $(printf '%s\n' "${!files[@]}" | LC_ALL=C sort); do
   compose="$engines/${files[$target]}"
   [[ -f "$compose" ]] || fail "$target: compose file $compose is missing"
   refs="$(sed -nE 's/^[[:space:]]+image:[[:space:]]*"?([^[:space:]"#]+).*/\1/p' "$compose" | LC_ALL=C sort -u)"
   [[ -n "$refs" ]] || fail "$target: no 'image:' line in $compose"
   [[ "$(printf '%s\n' "$refs" | wc -l)" -eq 1 ]] || fail "$target: $compose names more than one image ($(printf '%s' "$refs" | tr '\n' ' '))"
   # shellcheck disable=SC2086  # the docker command is deliberately split into words
   id="$(timeout "$limit" $docker_cmd image inspect --format '{{.Id}}' "$refs" 2>&1)" \
      || fail "$target: 'docker image inspect $refs' failed or did not answer within ${limit}s: $id"
   [[ "$id" =~ ^sha256:[0-9a-f]{64}$ ]] || fail "$target: docker answered '$id' for $refs, which is not a sha256 image id"
   printf '%s\t%s\t%s\n' "$target" "$refs" "$id" >> "$rows"
done

tmp="$(mktemp "$out_dir/.image-pins.XXXXXX")"
python3 - "$rows" "$tmp" <<'PY'
import json, sys
pins = {}
for line in open(sys.argv[1], encoding="utf-8"):
    target, ref, image_id = line.rstrip("\n").split("\t")
    pins[target] = {"ref": ref, "id": image_id}
with open(sys.argv[2], "w", encoding="utf-8", newline="\n") as handle:
    json.dump(pins, handle, indent=2, sort_keys=True)
    handle.write("\n")
PY
chmod 0644 "$tmp"
mv -f "$tmp" "$out"
echo "make-image-pins: wrote ${#files[@]} pins to $out"
