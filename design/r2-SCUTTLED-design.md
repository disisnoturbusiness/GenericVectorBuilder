# GenericVectorBuilder: design (round 2, 2026-10-02)

Labels:
- **[Un]**: read at URL n (list at the end).
- **[LOCAL]**: measured on linus7795 by the round-2 box probe.
- **[LOCAL r1]**: measured by a round-1 verifier.
- **[CODE path:line]**: opened and checked.
- **[INFERRED]**: my reasoning.
- **[UNVERIFIED]**: not checked.

Paths:
- ADF = /home/dan/ForClaude/AzureDevOpsForager
- AIDF = /home/dan/ForClaude/AIDataForager
- C/ = ADF/AzureDevOpsForager.Core/
- I/ = ADF/AzureDevOpsForager.Indexer/
- S/ = ADF/AzureDevOpsForager.Server/
- A/ = AIDF/AIDataForager.Core/
- AS/ = AIDF/AIDataForager.Server/
- E/ = /home/dan/embed/embed_server.py

ADF line cites are from the working tree. I/AzdoIndexerService.cs has 53 uncommitted added lines.

## 1. What it is

GenericVectorBuilder (GVB) is a standalone .NET 10 ASP.NET app on linus7795 with a browser UI. It reads records from any of these:
- a folder of flat files (CSV with any delimiter, .xlsx, .xls)
- ODBC, through a DSN or a DSN-less connection string
- Azure DevOps Git or TFVC
- GitHub
- a local folder or local git clone

Each record is mapped to text, metadata and a stable key, then chunked. The chunks are embedded on the local GPU service at :8080. One pass writes the vectors to one or more sinks: SQL Server 2025 VECTOR, Qdrant, Azure AI Search, or any Microsoft.Extensions.VectorData (MEVD) store.

Re-runs are incremental:
- Unchanged documents cost nothing.
- Changed documents re-embed only their changed chunks.
- Removed documents are deleted from the sinks.

Code from ADF and AIDF is reused by copy only. Neither repo is touched.

## 2. Architecture

```
ISource -> IDocumentMapper -> IChunker -> IEmbedder -> fan-out -> ISink[0..n]
                     \__________ IStateStore (doc hashes, chunk ids per sink, cursors, runs) ___/
```

```csharp
public interface ISource
{
    Task<SourceProbe> ProbeAsync( CancellationToken ct );   // test connection, list tables/files, 10-row samples
    IAsyncEnumerable<SourceRecord> ReadAsync( SourceCursor? since, CancellationToken ct );
    SourceCursor? CursorAfterRun { get; }                     // watermark or commit SHA; saved only on clean success
}
public sealed record SourceRecord( string Table, string NativeKey, string? ChangeKey,
    IReadOnlyDictionary<string, object?> Fields, string? Body, string Origin );

public interface IDocumentMapper { MapResult Map( SourceRecord r ); }   // Document, or reject reason
public sealed record Document( string DocKey, string Text, string ContentHash,
    IReadOnlyDictionary<string, object?> Metadata, string? Language );

public interface IChunker { IReadOnlyList<Chunk> Split( Document d ); }
public sealed record Chunk( Guid ChunkId, string DocKey, int Ordinal, string Text,
    IReadOnlyDictionary<string, object?> Metadata );

public interface IEmbedder
{
    EmbedderFingerprint Fingerprint { get; }   // model key, repo, eos, dims, prefixes, L2 norm
    Task PreflightAsync( CancellationToken ct );
    Task<float[][]> EmbedAsync( IReadOnlyList<string> texts, EmbedKind kind, CancellationToken ct );
}

public interface ISink
{
    string Name { get; }
    Task EnsureTargetAsync( TargetSpec spec, CancellationToken ct );   // create, or verify dims + fingerprint
    Task UpsertAsync( IReadOnlyList<VectorRecord> batch, CancellationToken ct );
    Task DeleteAsync( IReadOnlyList<Guid> chunkIds, CancellationToken ct );
    Task FinalizeAsync( RunSummary run, CancellationToken ct );        // optional index build
}
public sealed record VectorRecord( Chunk Chunk, ReadOnlyMemory<float> Vector );

public interface IStateStore
{
    Task<IReadOnlyDictionary<string, string>> GetDocHashesAsync( string pipelineId, CancellationToken ct );
    Task<IReadOnlySet<Guid>> GetChunkIdsAsync( string pipelineId, string sink, string docKey, CancellationToken ct );
    Task RecordWrittenAsync( string pipelineId, string sink, IReadOnlyList<Chunk> chunks, CancellationToken ct );
    Task RecordDeletedAsync( string pipelineId, string sink, IReadOnlyList<Guid> chunkIds, CancellationToken ct );
    Task<SourceCursor?> GetCursorAsync( string pipelineId, CancellationToken ct );
    Task SaveCursorAsync( string pipelineId, SourceCursor cursor, CancellationToken ct );
}
```

Run rules [INFERRED]:
- **Skip unchanged documents.** A document is not chunked or embedded when its ContentHash matches the stored hash and every sink already holds it.
- **Chunk ids:** each id is a GUID built from SHA-256(pipelineId | docKey | chunk text | duplicate index).
  - The id stays stable when lines shift. This replaces the StartLine-based id [CODE I/Indexing/CodeChunkDto.cs:88].
  - A GUID is a valid key in Qdrant, Azure AI Search, and SQL `uniqueidentifier`. The MEVD Qdrant and Azure connectors accept Guid keys [U32, read by r1 verifier].
- **Embed only the delta.** Embed the chunk ids that at least one sink is missing. If a sink holds ids for a document that the new split no longer produces, delete them.
- **Fan-out:** each sink has its own bounded queue and its own retry. A failing sink marks itself failed while the others finish, and the run ends as `Partial`.
- **When deletes and the cursor save happen:** both run only if:
  - source enumeration finished with no source-level error, and
  - the document failure ratio is under the threshold (default 5%, matching ADF's 0.95 guard).
- **Rejected files keep their old vectors.**
- This avoids an AIDF bug: a connection failure returns 0 [CODE A/Indexer/IndexerService.cs:292-296], but the watermark is still saved (:93).

**Where MEVD fits:** MEVD Abstractions is GA [U47], and the connectors moved to `CommunityToolkit.VectorData.*` [U30]. One `MevdSink` over any `VectorStoreCollection` covers the long tail; DynamicCollection handles schemas chosen at runtime [U32]. SQL, Qdrant and Azure AI Search are hand-rolled for three reasons:
- The toolkit SQL connector only does float32 and single-row MERGE, and refuses DiskANN outside Azure [U33][U34].
- The toolkit Azure and Qdrant connectors send the whole upsert list in one call, with no batch splitting [U32, read by r1 verifier].
- The toolkit Qdrant connector would force Qdrant.Client to 1.18.1 or later [U27].

## 3. Web app shape on linus7795

| Item | Decision |
|---|---|
| Hosting | Kestrel, plain HTTP, on `0.0.0.0:5080`, which is free today [LOCAL]. It runs as a systemd unit as user `dan`, because it needs /home/dan/share and the SA password file. Uses Microsoft.Extensions.Hosting.Systemd 10.0.12 [LOCAL nuget index]. Drop ADF's win-x64 RID and Hosting.WindowsServices [CODE, reuse-map probe]. |
| Projects | Four projects, all net10.0 (not -windows): `Gvb.Core` (contracts, sources, chunkers, embedders, sinks), `Gvb.Roslyn` (C# chunker plugin, which keeps Roslyn out of Core), `Gvb.Web`, `Gvb.Tests`. House style as in his repos. |
| API layout | Follow the AIDF pattern: `Endpoints/*.Map( app )` classes [CODE AS/Program.cs:85-88], DI of `IEnumerable<ISource>`-style registrations (AIDF :154-160), and a PORT env var (:33). Drop AIDF's CORS AllowAnyOrigin (:69-73). |
| UI | ADF precedent: static `wwwroot`, vanilla JS, no build step, served by `UseDefaultFiles/UseStaticFiles` [CODE S/Server.cs:56-57]. A wizard: Source, then Tables and preview, Mapping, Embedder, Sinks, Run. Defaults everywhere, a big drop zone, one Go button. |
| Background runs | A `BackgroundService` reads a bounded `Channel<RunRequest>` [U20][U21]. One run at a time, because there is one GPU [INFERRED]. Inside a run, stages are linked by bounded channels for backpressure. Cancel goes through a CancellationTokenSource. Use Hangfire 1.8.25 [U24] only if scheduled or persistent queues are wanted later; LLMQuorum.Web is the donor (csproj:8-9). |
| Progress | `TypedResults.ServerSentEvents` over `IAsyncEnumerable<SseItem<RunEvent>>` [U22], at `GET /api/runs/{id}/events`. Event ids let the browser resume with `Last-Event-ID` after a reconnect [U23]. Counters are saved every few seconds, so a reload shows true state. HTTP/1.1 allows 6 connections per host [U23], so the page opens one stream per tab and closes it while the tab is hidden. |
| Restart | Queued runs live in memory and are lost [INFERRED]. On boot, runs marked `Running` become `Interrupted`. A re-run is cheap because the state store skips chunks already written. |
| State store | SQLite at `~/.local/share/gvb/gvb.db` (Microsoft.Data.Sqlite 10.0.12). Chosen so the app's own state needs no SQL Server [INFERRED]. Tables: pipelines, runs, run_events, docs, chunks(sink), cursors, rejects. |
| Data root | `~/.local/share/gvb/{uploads,logs}`. Never beside the binary; ADF wrote side files there [CODE I/AzdoIndexerService.cs:411,:671]. |
| Config | `appsettings.json` holds the port, allowed server-path roots and defaults, plus env vars `GVB_*`. Pipelines are JSON rows in the state DB. |
| Secrets | Lookup order: env `GVB_SECRET_<NAME>` first, then `~/.config/gvb/secrets.json` (mode 600). Same env-first pattern as [CODE C/Services/Utilities/SecretStore.cs:37-41][CODE A/Config.cs:266-283]. Connection strings reference `{secret:NAME}`, which is filled in when the connection opens and never sent to the UI or logs. A built-in provider, `sa-file`, reads the last `pass:` line of /home/dan/mssql-sa-password.txt and strips the CR. Never use SecretBox (constant passphrase :23, salt :47). |
| Auth | None, LAN only, the same as both Server precedents [CODE]. Server-path browsing is limited to configured roots. |

## 4. Source matrix

| Source | Read via | Change key | Incremental | Deletes | Phase |
|---|---|---|---|---|---|
| Folder, server path | System.IO plus the sniffer (section 5). Default root is `/home/dan/share`, the live guest read-write Samba share `share` [LOCAL testparm] | File size and mtime, then SHA-256 | Skip unchanged files. Inside a changed file, skip unchanged rows by row hash | A file that is gone has its docs deleted. A rejected file keeps its old docs | MVP |
| Folder, browser upload | One PUT per file to `uploads/<batch>/`, then the same code path as server mode | SHA-256 | Same as server path | Only if the user ticks "this is the full set" | MVP |
| CSV, pipe, tab, any delimiter | CsvHelper 33.1.0 parser plus GVB's own sniffer | Key columns, else a row hash | Row-hash diff | Keys absent from the snapshot | MVP |
| Excel .xlsx/.xls (.xlsb untested) | ExcelDataReader 3.9.0 with a `Read()`/`NextResult()` loop, not AsDataSet [U17] | As for rows | As for rows | As for rows | MVP+1 |
| ODBC with DSN | System.Data.Odbc 10.0.12. DSNs listed by P/Invoke of `SQLDataSources` in libodbc.so.2 [LOCAL] | Key columns | A watermark column if given (rowversion is best), else a full scan with row hashes | Full-scan key-set diff only. Watermark runs cannot see deletes, so schedule a periodic full run | 2 |
| ODBC, DSN-less | Driver picked from `SQLDrivers` [LOCAL], or a pasted connection string | Same as DSN | Same as DSN | Same as DSN | 2 |
| AzDO Git | AzureDevOpsService (edited) | Blob ObjectId, already parsed but unused [CODE C/Services/Integration/AzureDevOpsService.cs:481] | Compare SHA per file | Path absent | 3 |
| AzDO TFVC | AzureDevOpsService (edited) | ChangeDate [CODE C/Services/Sources/TfvcSourceProvider.cs:84] | ChangeDate later than the stored value | Path absent | 3 |
| GitHub | GitHubService zipball (edited) | Commit SHA from the `owner-repo-<sha>` folder name [CODE GitHubService.cs:129,:150], then SHA-256 per file | Skip the whole repo if the SHA is unchanged | Path absent | 3 |
| Local git clone | `git` 2.43.0 CLI [LOCAL]: `git ls-tree -r -z HEAD` and `git diff --name-status -z old new` | Blob SHA | Diff between the stored HEAD and the current one | `D` entries | 3 |
| Local folder (code) | System.IO plus SourceFilterOptions, always with an explicit filter. The default Include is `**/*.cs` [CODE SourceFilterOptions.cs:26] | Size and mtime, then SHA-256 | Same as folder | Same as folder | MVP+1 |
| Access .mdb/.accdb | odbc-mdbtools 1.0.0 (universe, not installed) [U18]. Its SQL coverage is "limited" [U19] | Key columns | Full scan | Key diff | Later |
| OLE DB | Not available on Linux: `PlatformNotSupportedException` at the constructor [LOCAL r1][U43] | | | | Relay (section 9) |

ODBC notes:
- **Enumeration:**
  - Use FETCH_FIRST=2, NEXT=1, FIRST_USER=31, FIRST_SYSTEM=32, and stop on SQL_NO_DATA. A wrong constant loops forever [LOCAL r1][U41].
  - `odbcinst -q -s` errors when there are zero DSNs, so do not parse its output [LOCAL].
  - `ODBCINI` is honored, so tests can use a temp ini file [LOCAL].
- **Description field:** returns the DSN's `Driver=` value verbatim, which may be a .so path [UNVERIFIED here; r1 verifier read unixODBC DriverManager/SQLDataSources.c].
- **Schema:**
  - `GetSchema("Tables")` takes 3 restrictions; pass null, not "", for the ones you skip [U42].
  - Views are a separate collection.
  - Fallback: `WHERE 1=0` with `CommandBehavior.SchemaOnly`, then `GetSchemaTable()` [LOCAL r1].
  - Query mode, where the user writes a SELECT, is fully supported.
- **Driver:** msodbcsql18 18.7.1.1 on unixODBC 2.3.12 [LOCAL]. Ubuntu 24.04 is a supported target [U46].
- **Measured:** a DSN-less open took 148 ms. Reading 200k rows x 9 columns took 953 ms, with correct types [LOCAL].
- **VECTOR columns:** a SQL 2025 `VECTOR` column reads back as varchar text [LOCAL]. Treat it as text or skip it.

## 5. Folder mode

**Intake: browser upload**
- Use `<input webkitdirectory>` [U1][U2] plus a drop zone built on `webkitGetAsEntry` [U3][U4].
  - Call `webkitGetAsEntry` synchronously inside the `drop` handler.
  - Loop `readEntries()` until it returns an empty array; Chromium returns at most 100 entries per call [U3].
  - A dropped folder's plain `File` has no children, so the entries API is required [U5].
- Skip `getAsFileSystemHandle` and `showDirectoryPicker`. They are Chromium-only [U6][U7] and need a secure context, which `http://linus7795:5080` is not [INFERRED].
- Filter by extension in the browser. Send one `PUT /api/uploads/{batch}/{*relPath}` per file, raw body, 4 in parallel.
- The server streams `Request.Body` to disk and lifts `MaxRequestBodySize` on that endpoint only. The default is 30,000,000 bytes [U8][U11].
- A single multipart POST would fail: it hits that size cap, and it can also exceed `ValueCountLimit` (1024 sections) [U9][U10].
- The path-traversal guard copies the Zip-Slip check [CODE GitHubService.cs:224-229].

**Intake: server path**
- A text box plus a server-side folder browser limited to the configured roots, with a recursive toggle.
- After upload, both modes share one code path.

**Sniffing, per file** (195 of 195 correct on the probe set [LOCAL]):

| Step | Rule | Measured |
|---|---|---|
| Encoding | Check for a BOM, then for binary content (a NUL byte means reject), then try strict UTF-8, then fall back to windows-1252. `StreamReader` BOM detection covers only UTF-8/16/32 [U15]. Register `CodePagesEncodingProvider` at startup; no package is needed on net10 (NU1510). | 195/195. A lenient UTF-8 decode of a cp1252 file gave 482 U+FFFD characters [LOCAL] |
| Delimiter | GVB's own quote-aware field-count consistency score over `, \t \| ;` plus a user-supplied "other". CsvHelper's `DetectDelimiter` is only a cross-check: it looks at the first 4096 characters and silently keeps the configured delimiter when it fails [U13][U14]. | 195/195 for both [LOCAL] |
| Header | Row 0 is a header if it has no numeric or date cells and no duplicates. Rated "strong" if a data column is numeric, "ambiguous" if everything is text; ambiguous files are asked about in the preview. | 195/195. A headerless file was flagged correctly, but row 0 was still consumed as a header. Fix: re-read with `HasHeaderRecord=false` and names `Column1..N` [LOCAL] |
| Records | Always use a real parser, never line counts. In all 65 shape-A files, physical lines did not equal records [LOCAL]. | |
| Quotes | `BadDataFound` is a warning, not a reject, because stray mid-field quotes are common. Guards are quote-count parity plus a per-row column-count check. With `BadDataFound=null`, an unclosed quote swallows the rest of the file [LOCAL]. | |
| Excel | Each sheet is a candidate table. Hidden sheets come back with `VisibleState=hidden` and are skipped by default. Integers arrive as Double, so key formatting must be normalized. Formula cells depend on cached values, which real files have [INFERRED]. A bad file signature throws `HeaderException`, caught per file [LOCAL]. | |

**Grouping:**
- The group key is the ordered list of normalized header names (lowercased, non-alphanumerics stripped). Files with the same key become ONE logical table, configured once.
- In the probe, 3 spellings of `Order ID` collapsed into one table. 200 files gave 3 tables, and every row count matched ground truth [LOCAL].
- Files with the same columns in a different order become a separate group, with a "merge" button [INFERRED].

**Reject list:** never fatal.
- Each entry records file, reason, detail and readable row count. The list downloads as CSV.
- The probe's five rejects: a NUL byte, 0 bytes, header only, ragged (10 of 40 rows the wrong width), and an unclosed quote [LOCAL].
- Rows with the wrong width go to a row-reject list. The whole file is rejected if more than 5% of its rows are bad or quote parity fails [INFERRED threshold].

**Preview and mapping, per logical table:**
- 10 sample rows, each shown with its rendered text and an estimated token count.
- **Key:** tick one or more columns. The whole table is checked for duplicate keys before Go, which is cheap: 54k rows parse in under 1 s [LOCAL]. Without a key, the default is row hash plus file name, so an edited row becomes a delete plus an insert.
- **Text template:** `{Column}` placeholders. The default is `Name: value` lines for the text-like columns.
- **Metadata:** the remaining columns, with types inferred from the sample. Each column can be skipped.
- **Chunker:** `RowChunker`, one row = one doc. A row over the token cap is split with overlap, never truncated silently. ADF truncated silently [CODE I/AzdoIndexerService.cs:490].

**Speed** [LOCAL]:
- Sniffing and grouping 200 files took 163 ms with Parallel x6.
- A 2M-row, 122 MB CSV parsed in 763 ms (1,159 ms with the parity pass, 51 MB peak).
- A 100k-row xlsx streamed in 1.8 s.
- Parsing never limits a run; embedding does. At about 130 chunks/s, 2M rows takes about 4.3 h [INFERRED from LOCAL].

## 6. Sink matrix

| | SQL Server 2025 VECTOR | Qdrant 1.17.0 | Azure AI Search | MEVD long tail |
|---|---|---|---|---|
| Create | A table with `Id int IDENTITY` as the clustered PK (needed for any future DiskANN index [U36]), `ChunkId uniqueidentifier UNIQUE`, `DocKey`, `Text nvarchar(max)`, `Meta nvarchar(max)` holding JSON, and `Embedding VECTOR(n)` float32. Plus a `<table>__manifest` table holding the fingerprint. | Collection with size=n and Cosine distance. If it already exists, read its config and fail on a dim or distance mismatch. AIDF skips this check [CODE A/Services/Search/VectorService.cs:94-97]. | Index with a string key, text, a filterable DocKey, metadata fields, a `Collection(Edm.Single)` vector field and an HNSW profile, on api-version 2026-04-01 [U37][U38][U48]. | `EnsureCollectionExistsAsync` on a DynamicCollection [U32]. |
| Upsert | Per batch of about 500, in one transaction: `SqlBulkCopy` with `SqlVector<float>` into a #temp table [LOCAL r1], then DELETE by ChunkId and INSERT. | Point id = ChunkId. Payload holds doc_key, text and typed metadata; AIDF stores strings only (:268). Batches of 256, wait=true [INFERRED size]. | `upload` action. Batches capped at 1,000 docs AND 16 MB [U39]. At 1536 float32 dims the JSON is about 15-18 KB per doc, so batch by bytes, about 800 docs [INFERRED]. | Upsert by key. GVB splits batches itself, since toolkit connectors send the whole list in one call [U32, r1 verifier]. |
| Delete | DELETE by a ChunkId list taken from the state store. | `DeleteAsync(ids)` | `delete` action by key | `DeleteAsync(keys)` [U32, r1 verifier] |
| Dims | float32 up to 1,998. float16 goes up to 3,996 but is preview: it needs PREVIEW_FEATURES, and `SqlVector<Half>` throws, so it would have to go as JSON strings [U35][LOCAL r1]. MVP is float32 only. | 1536 is routine. No maximum found [UNVERIFIED]. | Up to 4,096 per field, all tiers [U39]. | Each store's own limit. |
| Index | None in the MVP; search uses exact `VECTOR_DISTANCE`. DiskANN is an opt-in Finalize step. It is preview, needs PREVIEW_FEATURES and at least 100 rows, and the table went read-only after the build on this box [U36][LOCAL r1]. It is dropped before each reload. GVB never turns PREVIEW_FEATURES on itself; ADF does, at [CODE C/Services/Storage/SchemaInitializer.cs:340,:550,:642]. | HNSW (default). | HNSW. | Store default. |
| Retry | `SqlBulkCopy` has no RetryLogicProvider, and command retry does not apply inside an explicit transaction [LOCAL r1]. GVB retries the whole batch itself on transient SqlException numbers, with backoff; this is safe because writes are idempotent by ChunkId. Connection-open retry follows the SqlResilience pattern. | Backoff on gRPC Unavailable and DeadlineExceeded; idempotent by id [INFERRED]. | Read the per-doc status and retry only the failed keys; back off on 429/503 [INFERRED]. | Backoff in the wrapper [INFERRED]. |
| Package | Microsoft.Data.SqlClient 7.1.1 (`SqlVector<T>` exists since 6.1.0 [U35]). | Qdrant.Client **1.17.0**, an exact match; Qdrant allows at most 1 minor version of client/server skew [U25][U26]. Move to 1.18.1 only if the toolkit Qdrant connector is added [U27]. Never use 1.19.x against a 1.17 server. | Azure.Search.Documents **12.0.0**, which targets 2026-04-01 [U49]. Because this sink is hand-rolled, the toolkit's 11.7.0 floor [U29] does not apply. | Microsoft.Extensions.VectorData.Abstractions 10.10.0 [U31] and `CommunityToolkit.VectorData.*` 1.0.0 (PgVector 1.0.1); the toolkit needs Abstractions 10.7.0 or later [U27]. SqliteVec is preview only, so skip it [LOCAL r1]. |

Notes:
- Azure AI Search is push-only from this box. Integrated vectorization cannot pull from ODBC or from this box [U40]. It is a billed service, so it is not in the MVP.
- Writing a vector parameter through ODBC is [UNVERIFIED]. The SQL sink uses SqlClient, not ODBC.
- MEVD connectors are float-only [U32, r1 verifier].
- Whether the toolkit SqlServer connector supports the SQL 2025 `VECTOR` type is [UNVERIFIED] on this box [U28]. GVB does not use it.

**Pinned packages:**
- Microsoft.Data.SqlClient 7.1.1
- Qdrant.Client 1.17.0
- Azure.Search.Documents 12.0.0
- Microsoft.Extensions.VectorData.Abstractions 10.10.0
- CsvHelper 33.1.0 [U12]
- ExcelDataReader 3.9.0 [U16]
- System.Data.Odbc 10.0.12 [U45]
- Microsoft.Data.Sqlite 10.0.12
- Microsoft.Extensions.Hosting.Systemd 10.0.12
- Microsoft.CodeAnalysis.CSharp 4.11.0 and Newtonsoft.Json 13.0.3: the versions the round-2 lift built with [LOCAL]

**Not referenced:** System.Text.Encoding.CodePages (redundant on net10 [LOCAL]), System.Data.OleDb, ExcelDataReader.DataSet.

## 7. Embedder

**First choice: localhost:8080.** Requests are `POST /embed` to E/ with `{"inputs":[...],"model":KEY,"is_query":false}`.

| Fact | Label |
|---|---|
| The response is a bare array of float arrays. bge-code-v1 returns 1536 dims, L2-normalized. The same batch always gives the same output. | [LOCAL] |
| A batch of 32 takes 240-259 ms, about 130 docs/s, with 1.05 MB of JSON. The server runs 8 texts per forward pass and does not sort by length. | [LOCAL][CODE E/:121] |
| An unknown `model` silently falls back to the default model. | [CODE E/:200-201][LOCAL] |
| `truncate` is ignored. Inputs are always cut at 2048 tokens. | [CODE E/:129] |
| Only one embedder stays loaded. Requesting another evicts it. | [CODE E/:14][LOCAL r1, which evicted qwen3 by accident] |
| `is_query` does nothing for bge-code-v1 because its prefixes are empty. It matters for qwen3-emb-0.6b. | [CODE E/:27-28,:35][LOCAL cosine 1.0000] |
| Blank input is replaced with `" "`. | [CODE E/:204] |

**Client:** new code. HuggingFaceEmbedder's parser keeps only the first vector of a batch [CODE C/Services/Embedding/HuggingFaceEmbedder.cs:182-198, ran].
- **Preflight:**
  - Call `GET /health` and confirm the requested key is in `embed_profiles`. Two profiles share 1536 dims, so a silent fallback would not show up as a dim mismatch.
  - Then probe-embed one text to read the dims, using the EvalRunner pattern [CODE ADF Eval/Services/EvalRunner.cs:162-168].
- **Batching:** 32 texts per request, 1 request in flight. Sort by length within a window of about 512 texts to cut padding [INFERRED].
- **Checks on every response:** the vector count equals the input count, every dim matches, and every norm is 1 within 1e-3. Any failure fails the whole batch. Never write zero vectors (ADF and AIDF both do on blank input or failure).
- **Token cap:** chunks are capped at about 1,500 estimated tokens, so the server's 2048-token truncation never bites silently [INFERRED].

**EOS decision for bge-code-v1: turn it ON for every fresh GVB target.**
- **Why:**
  - The model pools the last token and was trained with `<|endoftext|>` (id 151643) appended. Its tokenizer.json never appends it.
  - Danny measured with-EOS at **+33% top-1 through the full hybrid (0.508 to 0.676)** on his corpus [LOCAL, memory bge-code-v1-needs-eos-appended.md].
- **Blocker:**
  - :8080's bge profile hard-codes `"eos": False` [CODE E/:27], with no per-request override.
  - The correct EOS path already exists in the server: truncate to maxlen-1, then append, so truncation can never drop the EOS [CODE E/:123-128].
- **Recommended:** accept an `"eos"` request field in `embed_route`, pass it to `embed()`, and advertise it in `/health`.
  - It changes tokenization only, so the same loaded weights serve both modes. Nothing is evicted, and the demo's no-EOS path is untouched.
  - About 6 lines in E/, which is not an ADF/AIDF file. **Needs Danny's yes.** [INFERRED]
- **Fallback without the server change:** the client appends the literal `<|endoftext|>`, which tokenizes to 151643 [LOCAL, tokenizer only].
  - Safe only while the chunk stays under 2047 tokens, because server truncation cuts it off; a length-16 test lost it [LOCAL].
  - The 1,500-token cap makes that likely but not certain, since a character-based estimate can be wrong. Listed as a risk.
- **Do not** append EOS for qwen3-emb-0.6b. Its tokenizer already appends 151643, and its `eos_token_id` is a different id, 151645 [LOCAL, memory].
- jina-code-1.5b already has `eos: True` [CODE E/:39].

**Query and doc vectors must match:**
- Each target's manifest stores the fingerprint `{model key, HF repo, eos, dims, maxlen, prefixes, norm=L2}`.
- The search side must embed queries with the same model key and the same eos setting. `is_query=true` matters only for models with prefixes.
- GVB refuses to write into a target whose fingerprint differs.
- The existing demo vectors are no-EOS [LOCAL, memory], so GVB never appends into those tables.

**Second choice: HF endpoints (TEI).**
- **EOS:** TEI running bge-code-v1 produces no-EOS vectors, because the fast tokenizer omits it [LOCAL, memory]. Those vectors only fit no-EOS targets. Whether TEI honours a literal `<|endoftext|>` in the input text is [UNVERIFIED].
- **Cost:** about $2 per cold wake; the endpoint goes cold after 30 min idle [memory spend-his-money]. Before any run that hits HF, the UI shows the chunk count and asks to confirm.
- **Reuse:** HuggingFaceEmbedder is rewritten:
  - batching (the ParseVector bug)
  - Config sites at :97, :98, :145, :203, :205
  - a zero vector for blank input at :97, :145
  - retry that misses timeouts at :162-179
  - a payload with no model or is_query at :149

## 8. Reuse map

Every row below was checked by the round-2 probe. The 18 lifted files plus a small config stub built in net10 with 0 errors and 1 warning (SecretBox.cs:125, SYSLIB0060) [LOCAL].

**ADF**

| File | Verdict | GVB role | Must change |
|---|---|---|---|
| C/Services/Integration/AzureDevOpsService.cs (530 lines) | edit | AzDO source | The constructor takes plain strings (:74). Replace Console calls with ILogger (:140,:151,:221,:231,:374). Swallowed failures must throw (:141,:152,:176). Drop `_contentCache` (:59,:179,:263). Fix the per-file N+1 calls (:282,:338). Use ObjectId (:481). It reads text only. Repo listing does not exist yet (new code). |
| C/Models/AzureDevOps/ChangesetInfo.cs (55) | as-is | AzDO model | |
| C/Services/Integration/GitHubService.cs (251) | edit (small) | GitHub source | `api.github.com` is hardcoded (:25), and GHE URLs misparse [ran]. The whole zip sits in RAM (:143). The temp dir is wiped only before the next run (:195-196); wipe it after. User-Agent string (:56). Keep the Zip-Slip guard (:224-229). |
| C/Services/Integration/HttpRetry.cs (148) | edit | HTTP retry | Declared `internal` (:26). GET only (:52), but sinks need POST. Retries only HttpRequestException (:77), so timeouts are not retried. Console output (:144). |
| C/Services/Sources/SourceFilterOptions.cs (113) | as-is | Glob filtering | ShouldInclude :53, GlobMatch :91. A null filter means `**/*.cs` only (:26) [ran]. |
| C/Services/Sources/{Tfvc,Git,GitHub}SourceProvider.cs | edit | Adapters onto ISource | Git drops the SHA and sets ChangeDate=null (:96-101). TFVC carries ChangeDate (:84). GitHub walk (:98-106). `ReadAllText` (:116) swallows errors and has no encoding sniff. |
| C/Services/Sources/ISourceProvider.cs | reference | | File-shaped (:49-76). Replaced by ISource. |
| I/Indexing/RoslynChunker.cs (890) | as-is, C# plugin | IChunker for .cs | Needs ImplicitUsings and C# 12 (:75). [ran] It emits nothing for nested types (:113), operators, indexers, events, destructors, or top-level enums and delegates. Class shells are uncapped (one was 9,904 chars). 34 of 1,285 chunks exceed 5000 chars, so add a token-cap splitter after it. Chunk text carries a `// File:` header (:710) and overlap (:655-657). |
| I/Indexing/CodeChunkDto.cs (91) | edit | Chunk DTO | `GetId` embeds StartLine (:88). Use the content-hash GUID instead. |
| I/Indexing/RoslynMetadataExtractor.cs (471) | as-is, optional | C# metadata | 27 public string fields (:15-102) need reflection to become a dictionary. Extract (:134). |
| I/AzdoIndexerService.cs (722) | ideas only | Run engine | **Keep:** staging plus the guard, preflight, per-file error isolation, the run record written in `finally` (:134-144), CapKey (:644). **Avoid:** the hosted cap (:250-261) also caps the local GPU at 1000 files. The guard counts files, not chunks (:201-204). The silent Substring (:490). Side files (:411,:671). The embed protocol (:527-533) is ADF Server's, not :8080's. |
| C/Services/Embedding/IEmbedder.cs (51) | edit | IEmbedder | Eight sync/async methods, no dims or model name. |
| C/Services/Embedding/HuggingFaceEmbedder.cs (222) | rewrite batching, edit the rest | HF embedder | See section 7. |
| C/Services/Search/SqlVectorService.cs (158) | ideas | SQL sink health checks | `COUNT(*)` vs `COUNT(Embedding)` (:91). The `sys.vector_indexes` probe (:107-109). |
| C/Services/Storage/SchemaInitializer.cs (871) | extract helpers | SQL sink | **Keep:** TestConnection :243, DatabaseExists :262, CreateDatabase :276 (quoting :280-281), ExecAsync :753, TryExecAsync :768, ExistsAsync :782, DetectAzureSql :567, RequireVectorType :582. **Do not copy:** PREVIEW_FEATURES ON (:340,:550,:642), the DiskANN smoke table (:654-676), the swap (:696-728). |
| C/Services/Utilities/SqlResilience.cs (61) | as-is | Connection-open retry | Hardcoded Console text (:46). Whether it also covers commands (doc comment :54) is [UNVERIFIED]. No bulk-copy retry. |
| C/Services/Storage/ConnectionStringBuilder.cs (137) | edit | SQL connection strings | Encrypt and TrustServerCertificate (:34,:131). Drop the Windows-auth branch (:85). |
| C/Services/Storage/UsageTelemetry.cs | shape only | Run record | IndexRunRecord (:18-55). |
| C/Services/Utilities/SecretStore.cs | pattern only | Secrets | Env-first Get (:37-41). |
| ADF Eval/Services/EvalRunner.cs | pattern | Dims discovery | Probe-embed (:162-168). |
| Tests/{RoslynChunker,RoslynMetadata,SourceFilter,GitHubService}Tests.cs | as-is | Tests | Retarget from net10.0-windows/win-x64. |
| S/Server.cs (777) | UI precedent | Web shell | `MapGet/MapPost` (:523,:539,:570,:587). Static files (:56-57). Kestrel (:387). Vanilla wwwroot (app.js 735 lines, style.css 786). |

**AIDF**

| File | Verdict | GVB role | Must change |
|---|---|---|---|
| A/Interfaces/IDataSourceAdapter.cs (58) | edit | ISource shape | Keep `FetchItemsAsync(DateTime? since)` (:42) and TestConnectionAsync (:31). Drop :50 and :57. |
| A/Models/DataSource/DataItem.cs (67) | edit | SourceRecord | Metadata dictionary (:60). Make LastModified (:53) nullable. Drop the DataSourceType enum (:9-50). |
| A/Interfaces/IEmbeddingProvider.cs | reference | | Dimension (:23), ProviderName (:17). |
| A/Services/Search/VectorService.cs (309) | edit | Qdrant sink | Create (:79-87). Batches of 100 (:26,:141-154). Replace the MD5 GUID (:283-288) with ChunkId. **Fix:** hardcoded https:false and no API key (:46-50); init errors swallowed with no dim check (:94-97); string-only payload (:268); no delete; Dispose (:298-306). **Drop:** the search methods (:164,:217) and Config use (:43,:44,:47,:48). AIDF pins Qdrant.Client 1.12.0; GVB pins 1.17.0. |
| A/Services/Search/AzureSearchService.cs (573) | reference | Azure sink | EnsureIndexExists (:216-266) returns early if the index exists (:223-227). IndexDocuments (:274-319) uses mergeOrUpload (:282), never splits at 1000 docs/16 MB, only counts failures (:315), and has no retry. SanitizeKey is lossy (:505-521). Field() (:535-552). No vector field. |
| A/Indexer/IndexerService.cs (459) | anti-pattern | | Saves the watermark even after a connect failure (:93 vs :292-296). Cuts payloads at 1500 chars (:394,:411). |
| AS/Program.cs (242) | UI precedent (preferred) | API layout | `Endpoints/*.Map(app)` (:85-88; DataSourceEndpoints.cs :24 status, :81 test-connection). DI (:154-160). PORT env var (:33). Drop CORS (:69-73). |
| A/Config.cs (730) | pattern only | Secrets | Env var `AIDATAFORAGER_{SOURCE}_{CRED}`, then the key file (:266-283). |

**Drop:**
- **ADF:**
  - FileHelper
  - IndexerForm and Program (WinForms)
  - EmbeddingService, Tokenizer and TokenizerResult (E5 WordPiece on CPU, which cannot serve last-token pooling)
  - SmartCache
  - Config, Logger and Global
  - ChunkTextNormalizer (zero callers)
  - SecretBox
- **AIDF:**
  - MethodChunker
  - the Ollama and Onnx providers (they return zero vectors on failure, :65,:91,:96)
  - Logger and Config
  - the *Adapter.cs files (static Config)

**Outside both repos:** LLMQuorum.Web, which uses Hangfire 1.8.25 (csproj:8-9). Relevant only if Hangfire is chosen.

**[UNVERIFIED]:**
- Command retry through the SqlConnection retry provider.
- Tokenizer UNK behaviour. Moot, since the Tokenizer is dropped.
- `IsWindowsService` on Linux. Moot, since it is dropped.

## 9. Later: Windows OLE DB relay

OLE DB is COM and Windows-only. System.Data.OleDb ships a real implementation under `runtimes/win` only [U44][LOCAL r1].

The relay is a small `net10.0-windows` service on a Windows box:
- Connection strings live on the relay as named refs and never cross the wire.
- Build x86 and x64 variants: Jet is 32-bit only, and ACE's bitness follows Office [UNVERIFIED here; a round-1 verifier confirmed it on learn.microsoft.com].
- OleDbConnection does not support `Provider=MSDASQL` [U51]. For Windows ODBC DSNs, the relay uses System.Data.Odbc and the DSN lists in the registry.

Protocol sketch: HTTP with a shared-token header, streaming NDJSON [INFERRED].
- `GET /providers` returns OleDbEnumerator.GetElements [U50] plus the DSNs.
- `GET /refs` returns the named connections.
- `POST /schema {ref}` returns tables and columns via GetOleDbSchemaTable.
- `POST /read {ref, sql, keyCols, watermark?}` streams NDJSON:
  1. `{"t":"cols","cols":[{"n","type"}]}`
  2. `{"t":"row","v":[...]}`, one line per row
  3. `{"t":"end","rows":N}`, or `{"t":"err","msg"}` on failure

On the GVB side it is a single `RelaySource : ISource`, so the ODBC incremental rules apply.

## 10. MVP slice

**Scope:**
- Input: a folder of CSVs, by server path and by browser upload.
- Chunking: RowChunker.
- Embedding: bge-code-v1 on :8080.
- Sinks: SQL VECTOR and Qdrant together.
- UI: preview, mapping, progress, the reject list, and an incremental re-run.

| # | Build | Done-test |
|---|---|---|
| 1 | Solution with 4 net10 projects and central package pins | `dotnet build` gives 0 errors and `dotnet test` is green on linus7795. No OleDb or CodePages package is referenced. |
| 2 | A fixture generator that reproduces the probe set: 195 good and 5 bad files; 3 shapes; comma, pipe and tab; BOM; cp1252; embedded newlines; 3 header spellings. The probe's own files were deleted. | The generator writes its own ground truth. |
| 3 | Sniffer, grouping and reject list (Core only) | 3 tables whose row counts equal ground truth (the probe got 19,045/16,276/18,995). 195/195 correct for encoding, delimiter and header. 5 rejects with the expected reasons. A headerless file yields 4 of 4 rows. |
| 4 | Mapper, template, RowChunker, preview API | A 10-row preview per table. A duplicate-key report. An over-cap row is split and logged, never truncated. |
| 5 | :8080 client | An unknown model key fails preflight. 32 texts give 32 vectors of 1536 dims with norms of 1 ± 1e-3. A mocked short response throws. Throughput is logged; expect about 130/s. |
| 6 | State store and run engine: Channel, BackgroundService, stage channels, cancel, guard | Against an in-memory sink, cancel stops the run within 2 s. After `kill -9` mid-run, the re-run embeds only the missing chunks (check the embed-call count in the log). |
| 7 | SqlVectorSink | (a) `COUNT(*)` = `COUNT(Embedding)` = the state store's chunk count. (b) Querying a row's own vector with `VECTOR_DISTANCE` returns that row at distance ~0. (c) A second run makes 0 embed calls and 0 writes. (d) Editing 1 row replaces exactly 1 row; deleting 1 file removes its rows. (e) `sys.database_scoped_configurations` is identical before and after, so PREVIEW_FEATURES was never flipped. |
| 8 | QdrantSink | The point count equals the SQL row count. The same chunk's vector in Qdrant and in SQL has cosine ≥ 0.99999. An existing collection with the wrong dims fails before any write. |
| 9 | Web UI: upload, server path, tables, mapping, run, SSE, rejects | Claude drives Chrome through the flow. Drop the fixture folder and see 3 tables plus 5 rejects. Map the columns and press Go. Progress reaches 100%, and the counts equal steps 7 and 8. A reload mid-run resumes via Last-Event-ID. The same folder copied to /home/dan/share and run by server path gives identical counts. |
| 10 | EOS (only after Danny says yes to the E/ change) | (a) The eos=true vector for a text T matches embed_load.py's EOS path at cosine ≥ 0.9999. (b) eos=false still matches the demo's existing stored vectors, so the demo does not regress. (c) The manifest records eos=true, and GVB refuses an append with a mismatched fingerprint. |

## 11. Risks / unknowns

| # | Risk | Label | Handling |
|---|---|---|---|
| 1 | :8080 has no EOS switch for bge-code-v1. With-EOS vectors need either an E/ change or the fragile literal-token fallback. | [CODE E/:27][LOCAL] | Decision for Danny (section 7) |
| 2 | The GPU service is shared. A GVB run evicts whatever model the demo or search path had loaded. | [CODE E/:14][LOCAL r1] | Passing EOS as a request flag avoids loading a second profile. Otherwise, run when search is idle. |
| 3 | An unknown model key silently falls back to the default model. jina-code also has 1536 dims, so the dim check would not catch it. | [CODE E/:200-201] | /health preflight |
| 4 | The server truncates at 2048 tokens with no signal, and character-based token estimates can be wrong. | [CODE E/:129][INFERRED] | 1,500-token cap; split and log |
| 5 | bge-code-v1 quality on tabular or prose rows is unmeasured. The 32-doc sanity check ranked the obvious hit 5th. | [UNVERIFIED][LOCAL] | Score it with his evalkit before choosing a default model for flat files |
| 6 | DiskANN on SQL 2025 is preview, and it made the table read-only on this box. | [U36][LOCAL r1] | Off in the MVP. Opt-in Finalize step, dropped before each reload. |
| 7 | float16 needs PREVIEW_FEATURES. `SqlVector<Half>` is unsupported. The PREVIEW_FEATURES-off case is untested. | [U35][LOCAL r1] | float32 only in the MVP |
| 8 | SqlBulkCopy has no retry provider, and command retry is off inside a transaction. | [LOCAL r1] | GVB retries at the batch level |
| 9 | The .NET Qdrant client does not enforce the client/server version-skew policy. | [U25][U26] | Pin 1.17.0; re-pin when the server is upgraded |
| 10 | The Azure AI Search limits page gives both 1,000 docs per batch and 32,000 actions per request. | [LOCAL r1 on U39] | Plan on 1,000 docs and 16 MB |
| 11 | CsvHelper's delimiter detection reads only the first 4096 characters and falls back silently. | [U14] | Use GVB's own sampler |
| 12 | With `BadDataFound=null`, an unclosed quote swallows the rest of the file. | [LOCAL] | Quote-parity and column-width guards |
| 13 | Header detection is ambiguous on all-text files. | [LOCAL] | Ask the user in the preview |
| 14 | Excel cases are untested: formula cached values, password-protected files, .xlsb, and the 4.0 `DBNull` change. | [INFERRED][U17] | Test when a real file shows up |
| 15 | ODBC drivers other than msodbcsql18 are untested, and GetSchema output varies by driver. | [LOCAL r1][INFERRED] | SchemaOnly fallback |
| 16 | mdbtools ODBC coverage is "limited", and read-only behaviour is undocumented. | [U19][INFERRED] | Later, best effort |
| 17 | Folder upload has no documented file-count limit. | [UNVERIFIED] | One PUT per file; test with 200+ files |
| 18 | HTTP/1.1 allows only 6 SSE connections per host across all tabs. | [U23] | One stream per visible tab |
| 19 | The in-memory run queue is lost on restart. | [INFERRED] | Interrupted state plus a cheap re-run; Hangfire if it matters |
| 20 | Uploads can fill the disk under ~/.local/share/gvb. | [INFERRED] | Delete each upload batch after a successful run |
| 21 | RoslynChunker misses some member kinds and produces oversized class shells. | [LOCAL ran] | Token-cap splitter; C# only |
| 22 | MEVD toolkit connectors are untested on this box, including the SqlServer connector against the SQL 2025 VECTOR type. | [U28][UNVERIFIED] | Long tail only; test before use |
| 23 | Whether TEI honours a literal `<|endoftext|>` in the input is unknown. | [UNVERIFIED] | HF targets stay no-EOS unless tested |
| 24 | Embedding is the throughput bottleneck: about 130 chunks/s measured on short snippets, slower for long rows. | [LOCAL][INFERRED] | Show an ETA in the preview |

## URLs

- U1 https://developer.mozilla.org/en-US/docs/Web/API/HTMLInputElement/webkitdirectory
- U2 https://caniuse.com/input-file-directory
- U3 https://developer.mozilla.org/en-US/docs/Web/API/DataTransferItem/webkitGetAsEntry
- U4 https://raw.githubusercontent.com/mdn/browser-compat-data/main/api/DataTransferItem.json
- U5 https://web.dev/articles/read-files
- U6 https://developer.mozilla.org/en-US/docs/Web/API/Window/showDirectoryPicker
- U7 https://raw.githubusercontent.com/Fyrd/caniuse/main/features-json/native-filesystem-api.json
- U8 https://learn.microsoft.com/en-us/aspnet/core/mvc/models/file-uploads?view=aspnetcore-10.0
- U9 https://learn.microsoft.com/en-us/dotnet/api/microsoft.aspnetcore.http.features.formoptions?view=aspnetcore-10.0
- U10 https://github.com/dotnet/aspnetcore/blob/main/src/Http/Http/src/Features/FormFeature.cs
- U11 https://learn.microsoft.com/en-us/aspnet/core/fundamentals/servers/kestrel/options?view=aspnetcore-10.0
- U12 https://www.nuget.org/packages/CsvHelper
- U13 https://raw.githubusercontent.com/JoshClose/CsvHelper/master/src/CsvHelper/Configuration/CsvConfiguration.cs
- U14 https://raw.githubusercontent.com/JoshClose/CsvHelper/33.1.0/src/CsvHelper/CsvParser.cs
- U15 https://learn.microsoft.com/en-us/dotnet/api/system.io.streamreader.-ctor?view=net-10.0
- U16 https://www.nuget.org/packages/ExcelDataReader
- U17 https://github.com/ExcelDataReader/ExcelDataReader
- U18 https://packages.ubuntu.com/noble/odbc-mdbtools
- U19 https://mdbtools.github.io/install/
- U20 https://learn.microsoft.com/en-us/aspnet/core/fundamentals/host/hosted-services?view=aspnetcore-10.0
- U21 https://learn.microsoft.com/en-us/dotnet/core/extensions/channels
- U22 https://learn.microsoft.com/en-us/aspnet/core/release-notes/aspnetcore-10.0?view=aspnetcore-10.0
- U23 https://developer.mozilla.org/en-US/docs/Web/API/Server-sent_events/Using_server-sent_events
- U24 https://www.nuget.org/packages/Hangfire.Core
- U25 https://qdrant.tech/documentation/faq/qdrant-fundamentals/
- U26 https://www.nuget.org/packages/Qdrant.Client
- U27 https://www.nuget.org/packages/CommunityToolkit.VectorData.Qdrant
- U28 https://www.nuget.org/packages/CommunityToolkit.VectorData.SqlServer
- U29 https://www.nuget.org/packages/CommunityToolkit.VectorData.AzureAISearch
- U30 https://learn.microsoft.com/en-us/dotnet/ai/vector-stores/overview
- U31 https://www.nuget.org/packages/Microsoft.Extensions.VectorData.Abstractions
- U32 https://github.com/CommunityToolkit/AI
- U33 https://github.com/CommunityToolkit/AI/blob/main/MEVD/src/SqlServer/SqlServerCommandBuilder.cs
- U34 https://github.com/CommunityToolkit/AI/blob/main/MEVD/src/SqlServer/SqlServerCollection.cs
- U35 https://learn.microsoft.com/en-us/sql/t-sql/data-types/vector-data-type?view=sql-server-ver17
- U36 https://learn.microsoft.com/en-us/sql/t-sql/statements/create-vector-index-transact-sql?view=sql-server-ver17
- U37 https://learn.microsoft.com/en-us/rest/api/searchservice/search-service-api-versions
- U38 https://learn.microsoft.com/en-us/rest/api/searchservice/supported-data-types
- U39 https://learn.microsoft.com/en-us/azure/search/search-limits-quotas-capacity
- U40 https://learn.microsoft.com/en-us/azure/search/vector-search-integrated-vectorization
- U41 https://learn.microsoft.com/en-us/sql/odbc/reference/syntax/sqldatasources-function
- U42 https://learn.microsoft.com/en-us/dotnet/api/system.data.odbc.odbcconnection.getschema?view=net-10.0-pp
- U43 https://github.com/dotnet/runtime/blob/main/src/libraries/System.Data.OleDb/src/System.Data.OleDb.csproj
- U44 https://www.nuget.org/packages/System.Data.OleDb
- U45 https://www.nuget.org/packages/System.Data.Odbc
- U46 https://learn.microsoft.com/en-us/sql/connect/odbc/linux-mac/installing-the-microsoft-odbc-driver-for-sql-server?view=sql-server-ver17
- U47 https://devblogs.microsoft.com/dotnet/ai-vector-data-dotnet-extensions-ga/
- U48 https://learn.microsoft.com/en-us/azure/search/vector-search-how-to-create-index
- U49 https://github.com/Azure/azure-sdk-for-net/blob/main/sdk/search/Azure.Search.Documents/CHANGELOG.md (12.0.0, 2026-05-01: "Added support for 2026-04-01 service version"; checked this session)
- U50 https://learn.microsoft.com/en-us/dotnet/api/system.data.oledb.oledbenumerator?view=net-10.0-pp
- U51 https://learn.microsoft.com/en-us/dotnet/api/system.data.oledb.oledbconnection.connectionstring?view=net-10.0-pp (MSDASQL not supported; checked this session)