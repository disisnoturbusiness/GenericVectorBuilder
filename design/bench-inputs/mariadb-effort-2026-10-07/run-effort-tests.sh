#!/bin/bash
# Re-runs MariaDbEffortTests.Recall_AtTheDefaultEffort_IsMeasured (524 and 2,000 vectors) against the benchmark's OWN container
# gvbbench-mariadb, started from deploy/engines/mariadb-bench.compose.yaml on the engine CPUs 2-3,6-7 (the file's own pinned start),
# and saves everything under $OUT. The daily container gvb-mariadb and database gvb on it are never connected to.
# Guard rails: every wait has a deadline; the container is stopped (data kept) by the trap on any exit; progress goes to $OUT/progress.txt.
set -u
LANE=/home/dan/gvb-work/lanes/v8-fix-sweep
REPO=$LANE/repo-maria
OUT=$LANE/maria-evidence
COMPOSE=$REPO/deploy/engines/mariadb-bench.compose.yaml
REPEATS=${REPEATS:-10}
mkdir -p "$OUT"
: > "$OUT/progress.txt"
exec > >(tee -a "$OUT/run.log") 2>&1
progress() { echo "$(date -u +%FT%TZ) $*" | tee -a "$OUT/progress.txt" >/dev/null; }

STARTED_BY_ME=0
cleanup() {
   if [ "$STARTED_BY_ME" = 1 ]; then
      progress "cleanup: docker compose down (data kept)"
      timeout 120 docker compose -f "$COMPOSE" down 2>&1 | tail -3
   fi
   progress "exit"
}
trap cleanup EXIT

progress "start"
echo "date: $(date -u +%FT%TZ)"
echo "host: $(hostname) $(uname -r)"
echo "commit of the tree under test: $(cd /home/dan/ForClaude/GenericVectorBuilder && git rev-parse HEAD)"
echo "test file md5: $(md5sum $REPO/tests/GenericVectorBuilder.Engines.Tests/MariaDbEffortTests.cs)"
echo "compose file md5: $(md5sum $COMPOSE)"
echo "image: $(docker image inspect mariadb:11.8.9 --format '{{.Id}}')"
echo "load: $(cat /proc/loadavg)"
DAILY_BEFORE=$(docker inspect gvb-mariadb --format '{{.Id}} {{.State.StartedAt}} restarts={{.RestartCount}} cpuset={{.HostConfig.CpusetCpus}}')
echo "daily before: $DAILY_BEFORE"

if docker ps -a --format '{{.Names}}' | grep -q '^gvbbench-mariadb$'; then
   echo "REFUSED: container gvbbench-mariadb already exists; someone else may be using it"
   exit 3
fi
if ss -ltn | grep -q ':13306 '; then
   echo "REFUSED: port 13306 is in use"
   exit 3
fi

progress "starting gvbbench-mariadb"
GVB_ENGINE_CPUS=2-3,6-7 timeout 300 docker compose -f "$COMPOSE" up -d 2>&1 | tail -5
STARTED_BY_ME=1
for i in $(seq 1 60); do
   h=$(docker inspect gvbbench-mariadb --format '{{.State.Health.Status}}' 2>/dev/null || echo none)
   [ "$h" = healthy ] && break
   sleep 3
done
if [ "$h" != healthy ]; then
   echo "FAILED: gvbbench-mariadb is not healthy after 180 s ($h)"
   exit 4
fi
echo "bench container: $(docker inspect gvbbench-mariadb --format '{{.Id}} image={{.Image}} cpuset={{.HostConfig.CpusetCpus}} started={{.State.StartedAt}}')"
echo "bench container threads on CPUs: $(for p in $(docker top gvbbench-mariadb -eo pid | tail -n +2); do taskset -cp $p 2>/dev/null; done | sed 's/.*: //' | sort | uniq -c | tr '\n' ';')"
PW=$(grep '^MARIADB_ROOT_PASSWORD=' /home/dan/gvb-data/engines/secrets.env | cut -d= -f2-)
echo "server version: $(docker exec gvbbench-mariadb mariadb -uroot -p"$PW" -N -e 'select version()' 2>&1)"
echo "settings read from the running bench container with SHOW GLOBAL VARIABLES (name = value):"
docker exec gvbbench-mariadb mariadb -uroot -p"$PW" -N -e "SHOW GLOBAL VARIABLES WHERE Variable_name IN ('innodb_flush_log_at_trx_commit','innodb_doublewrite','log_bin','innodb_buffer_pool_size','mhnsw_max_cache_size','mhnsw_ef_search','mhnsw_default_m','mhnsw_default_distance')" 2>&1 | awk '{print "   SETTING " tolower($1) " = " $2}' | sort
unset PW
progress "container healthy"

cd "$REPO" || exit 5
export GVB_BENCH_MARIADB_PORT=13306
for n in $(seq 1 $REPEATS); do
   progress "repeat $n (the theory runs 524 and 2000 vectors)"
   echo "===== repeat $n, $(date -u +%FT%TZ)"
   # The client is held to the client CPUs, like the benchmark's own client.
   taskset -c 0-1,4-5 timeout 1200 dotnet test tests/GenericVectorBuilder.Engines.Tests -c Release --no-build \
      --filter "FullyQualifiedName~MariaDbEffortTests.Recall_AtTheDefaultEffort_IsMeasured" \
      --logger "console;verbosity=detailed" > "$OUT/test-repeat-$n.txt" 2>&1
   echo "exit $?"
   grep -E "vectors, effort in use|RECALL@|  ef [0-9]+|Passed|Failed|Total tests|error" "$OUT/test-repeat-$n.txt"
done
echo "leftover tables in the bench container: $(docker exec gvbbench-mariadb mariadb -uroot -p"$(grep '^MARIADB_ROOT_PASSWORD=' /home/dan/gvb-data/engines/secrets.env | cut -d= -f2-)" -N -e "select count(*) from information_schema.tables where table_name like 'gvb_gvbbench_mdbef%'" 2>&1)"
DAILY_AFTER=$(docker inspect gvb-mariadb --format '{{.Id}} {{.State.StartedAt}} restarts={{.RestartCount}} cpuset={{.HostConfig.CpusetCpus}}')
echo "daily after:  $DAILY_AFTER"
[ "$DAILY_BEFORE" = "$DAILY_AFTER" ] && echo "daily container unchanged: yes" || echo "daily container unchanged: NO"
progress "tests done"
