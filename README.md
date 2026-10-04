# GenericVectorBuilder

Point it at data, it embeds it on the GPU box and writes the vectors to SQL Server 2025 and Qdrant. Re-runs only embed what changed.

Web UI: http://linus7795.lan:5080

## What works today (2026-10-02)

- **Sources:** a folder of CSV, pipe, tab or semicolon files and Excel workbooks (.xlsx, .xls). The folder can be dragged into the browser or picked from `~/share`.
- **Folder mode:** the app works out the delimiter, header row and encoding of each file. Files with the same header become one table, so 200 monthly exports show up as one table. Each table gets a 10-row preview. Broken files go to a reject list with a reason, and the rest of the folder still runs.
- **Destinations:** SQL Server 2025 native `VECTOR` (database `GenericVectorBuilder`, one `dbo.gvb_<pipeline>` table per pipeline) and Qdrant (`gvb_<pipeline>` collections). Both are written in one pass.
- **Embedder:** the FastAPI service at `localhost:8080`. The default is `qwen3-emb-0.6b`.
- **Incremental runs:** unchanged rows cost nothing. A changed row is replaced. A row is deleted only if its file was read cleanly, or if the file is confirmed gone. A file that fails to read, a subfolder that can't be opened, or a workbook that won't open keeps its old vectors. If a run would remove more than half of what a destination held, nothing is deleted until you tick "Allow large deletes". If one destination fails, the other still finishes.
- **Reset:** each pipeline has a reset button. You click it twice, and it drops the pipeline from SQL and Qdrant.
- **Qdrant search is exact.** Measured on 760k AdventureWorks vectors, no HNSW `ef` setting reached 99% top-1 agreement with exact search. The best was 97.5% at ef 2048. Exact search costs about 120 ms per query. The measurement is in `QdrantSink.EXACT_SEARCH`.

Not built yet: ODBC, Azure DevOps, GitHub, Azure AI Search, and the Windows OLEDB relay.

## Measured search quality

The live test (`tests/.../Live/RetrievalQualityGateTests.cs`) covers 45 rows in three file formats and asks 15 paraphrased questions. The score is how many questions return the right row first. The gate is 13/15.

| Embedder | SQL Server | Qdrant |
|---|---|---|
| qwen3-emb-0.6b (default) | 13/15 | 13/15 |
| bge-code-v1 + end-of-text token | 11/15 | 11/15 |
| bge-code-v1, no end-of-text token | 2/15 | 2/15 |

Fifteen questions is a small set, so treat the 13 vs 11 margin as directional. The question "who can renew an expiring web certificate" is ambiguous: the SSL-expired ticket is a fair answer to it. I left it in rather than editing the test after seeing results.

## Scale check (AdventureWorks, 2026-10-02/03)

- **First build:** 760,167 rows became 760,479 chunks in both SQL and Qdrant, with 0 errors. It took 3 h 22 m at 58 rows/s, with the GPU at 96%.
- **Re-run over the same folder:** 760,167 rows were read and all were unchanged. 0 were embedded and 0 deleted, in 16 seconds.

## Run

```bash
dotnet run --project src/GenericVectorBuilder.Web -c Release
```

The SQL password is read from `GVB_SQL_PASSWORD`, or else from the last `pass:` line of `~/mssql-sa-password.txt`. Settings live in `src/GenericVectorBuilder.Web/appsettings.json` under `Gvb`.

Running it as a service: see `deploy/genericvectorbuilder.service`. It is not installed yet.

## Test

```bash
dotnet test --filter "Category!=Live"
```

```bash
dotnet test --filter "Category=Live"
```

The live tests need the GPU service, SQL Server and Qdrant to be running, and they fail rather than skip when any of those is down. They create and drop their own `GenericVectorBuilder_Test` database and their own collections.

## Layout

- `src/GenericVectorBuilder.Core`
  - `Sources/Files`: parser, sniffer, scanner, folder source
  - `Mapping`, `Chunking`, `Embedding`
  - `Sinks`: SQL Server, Qdrant
  - `State`: SQLite
  - `Pipeline`: runner and progress
- `src/GenericVectorBuilder.Web`: minimal API, run queue (one run at a time, one GPU), and `wwwroot` UI
- `tests/GenericVectorBuilder.Tests`: unit tests, plus `Live/` for the quality gate
- `design/`: the two scuttled paper designs and the verdicts that killed them
