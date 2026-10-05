# Vector engines for benchmarking

Each engine has its own compose file. Every port is bound to 127.0.0.1 only. Passwords come from `/home/dan/gvb-data/engines/secrets.env` (mode 600, outside the repo, never committed); compose files read it with `env_file`, and sinks read the same file. Every container is named `gvb-<engine>`, has a memory limit, and keeps its data under `/home/dan/gvb-data/engines/<engine>`.

Start an engine:

```bash
sudo docker compose -f deploy/engines/pgvector.compose.yaml up -d
```

Stop it again (its data is kept):

```bash
sudo docker compose -f deploy/engines/pgvector.compose.yaml down
```

Stop it with `down` and without `-v`. Every data folder is a bind mount, which `-v` never touches, but `-v` also removes named and anonymous volumes, so the rule is: no `-v` when stopping an engine. The one place the benchmark itself uses `docker compose down -v` is the clean-up of a container that exited while it was starting (the start retry in `ComposeRunner`), and only for a compose file that declares no named volumes; it never uses it to stop a healthy engine.

| Engine | Image | Host port(s) |
|---|---|---|
| pgvector | pgvector/pgvector:0.8.7-pg17-trixie | 5432 |
| mariadb | mariadb:11.8.9 (the daily `gvb-mariadb`, shared and long-running; the benchmark never touches it) | 3306 |
| mariadb-bench | mariadb:11.8.9, same server settings as mariadb, for the benchmark only (`gvbbench-mariadb`, `mariadb-bench.compose.yaml`; run-all starts it on the engine CPUs and stops it) | 13306 (host side only; the benchmark connects to the container's own address on 3306) |
| oracle | gvenzl/oracle-free:23-slim-faststart | 1521 |
| milvus | milvusdb/milvus:v2.6.25 (standalone, embedded etcd, local storage) | 19530, 9091 |
| weaviate | semitechnologies/weaviate:1.39.8 | 8085 (HTTP), 50051 (gRPC) |
| chroma | chromadb/chroma:latest | 8000 |
| elasticsearch | elasticsearch:9.5.3 | 9200 |
| opensearch | opensearchproject/opensearch:3.9.0 | 9201 |
| typesense | typesense/typesense:30.2 | 8108 |
| vespa | vespaengine/vespa:8.754.14 | 8090 (query/feed), 19071 (config) |
| redis | redis:8.10.2 | 6379 |
| mongodb | mongodb/mongodb-atlas-local:8.0.32 | 27017 |
| clickhouse | clickhouse/clickhouse-server:26.3.39.7 | 8123 |
| duckdb, sqlitevec | embedded (in process) | none |

These ports are already taken on linus7795, so never use them: 1433 (SQL Server), 6333/6334 (Qdrant), 8080 (GPU embed service), 5080 (GenericVectorBuilder), 445, 22.
