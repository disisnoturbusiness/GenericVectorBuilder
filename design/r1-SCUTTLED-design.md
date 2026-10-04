# VectorForager: design (draft 2026-10-02)

Working name, rename freely. Nothing in AzureDevOpsForager or AIDataForager gets modified. Code moves by copy only.

**Labels.** `[Un]` = confirmed at URL n (list at the end). `[LOCAL]` = confirmed by running it on linus7795 (survey probes, 2026-10-02). `[CODE]` = confirmed by reading the cited file. `[INFERRED]` = reasoning, not tested. `[UNVERIFIED]` = claimed somewhere, not checked.

**Path prefixes.** `ADF.Core/` = `/home/dan/ForClaude/AzureDevOpsForager/AzureDevOpsForager.Core/`, plus `ADF.Indexer/` and `ADF.Eval/` alongside it. `AIDF.Core/` = `/home/dan/ForClaude/AIDataForager/AIDataForager.Core/`, plus `AIDF.Indexer/`.

---

## 1. What it is

VectorForager is a standalone .NET 10 tool. It reads any configured source: Azure DevOps TFVC or Git, GitHub, a local folder or Git clone, any ODBC DSN, or any OLE DB provider on Windows. Each file or row becomes text plus metadata. That text is chunked and embedded once, and the vectors are written to one or more sinks: SQL Server 2025 `VECTOR`, Azure AI Search, Qdrant, or any store with an MEVD connector. Runs are incremental by content hash and source watermark, so a rerun embeds only what changed. A pipeline is one JSON file: source, mapping, chunker, embedder, sinks.

---

## 2. Architecture

```
ISource -> SourceRecord -> IDocumentMapper -> SourceDocument -> IChunker -> Chunk -> IEmbedder -> VectorRecord -> ISink x N
                                    \__________________ IStateStore (per pipeline, per sink, per doc) __________________/
```

```csharp
public sealed record SourceRecord( string Key, IReadOnlyDictionary<string, object?> Fields, string? Version );

public interface ISource
{
    string Kind { get; }                 // "odbc", "oledb", "azdo-git", "tfvc", "github", "folder", "git"
    bool IsSupportedHere { get; }        // OleDb: OperatingSystem.IsWindows()
    Task TestAsync( CancellationToken ct );
    IAsyncEnumerable<SourceRecord> ReadAsync( string? sinceVersion, CancellationToken ct );
    IAsyncEnumerable<string> ReadKeysAsync( CancellationToken ct );   // delete detection
}

public sealed record SourceDocument( string Key, string Text, IReadOnlyDictionary<string, string> Metadata,
                                     string ContentHash, string? Version );

public interface IDocumentMapper
{
    SourceDocument Map( SourceRecord record );   // template -> Text, columns -> Metadata, SHA-256(Text) -> ContentHash
}

public sealed record Chunk( string Id, string DocKey, int Ordinal, string Text, IReadOnlyDictionary<string, string> Metadata );

public interface IChunker
{
    bool CanChunk( SourceDocument doc );
    IReadOnlyList<Chunk> Split( SourceDocument doc );
}

public interface IEmbedder
{
    string ModelId { get; }
    int Dimension { get; }               // probed at startup, never configured
    int MaxBatch { get; }
    Task<float[][]> EmbedPassagesAsync( IReadOnlyList<string> texts, CancellationToken ct );
    Task<float[]> EmbedQueryAsync( string text, CancellationToken ct );
}

public sealed record VectorRecord( Chunk Chunk, float[] Vector );
public sealed record SinkSchema( string Name, int Dimension, string ModelId, IReadOnlyList<string> FilterFields );

public interface ISink
{
    string Name { get; }
    int MaxDimension { get; }
    Task EnsureAsync( SinkSchema schema, CancellationToken ct );   // create, or verify dim + model id; refuse on mismatch
    Task UpsertAsync( IReadOnlyList<VectorRecord> batch, CancellationToken ct );
    Task DeleteAsync( IReadOnlyList<string> chunkIds, CancellationToken ct );
    Task FinalizeAsync( CancellationToken ct );                     // SQL: build vector index. Others: no-op.
}
```

**MEVD verdict:** Microsoft.Extensions.VectorData is not the sink layer; `ISink` is. MEVD connectors sit inside `ISink` for Qdrant, Azure AI Search and the long tail. The SQL sink is hand-rolled because the toolkit SQL connector upserts by single-row MERGE with no bulk copy, is float32 only [U17], and refuses DiskANN on non-Azure SQL [U18]. The verdict itself is [INFERRED].
- Abstractions 10.10.0 stable [U14], GA 2025-05 [U13].
- Connectors moved to CommunityToolkit [U15]. `CommunityToolkit.VectorData.SqlServer`, `.Qdrant` and `.AzureAISearch` are 1.0.0 stable, published 2026-07-22. `.PgVector` is 1.0.1. (NuGet API nuspecs, read by survey; no page URL.)

**Run rules.**
1. **Probe the embedder.** Embed one string and read the length. This is the pattern in `ADF.Eval/Services/EvalRunner.cs:162-168` [CODE]. Refuse any sink where `Dimension > MaxDimension` before reading a single row.
2. **Skip unchanged docs.** Read the source from the stored cursor. The mapper hashes the text, and `IStateStore` skips any doc whose hash is unchanged for every sink.
3. **Stable chunk ids.** Chunk id = deterministic GUID from SHA-256(pipeline | docKey | chunkTextHash | duplicate ordinal).
   - Not from start line. Both old schemes shift on any edit: `ADF.Indexer/Indexing/CodeChunkDto.cs:88` and `AIDF.Core/Services/Utilities/MethodChunker.cs:250` [CODE].
   - A GUID is valid as a Qdrant point id, an Azure key and a SQL key [INFERRED].
4. **Embed and fan out.** Embed in batches, then send each batch to all sinks with `Task.WhenAll`.
5. **Commit per sink.** State is kept per (pipeline, sink, docKey): hash plus chunk ids.
   - A doc commits for a sink only after that sink acks.
   - Stale ids (old set minus new set) are deleted per sink.
   - A failed sink gets those docs again next run. Upserts are idempotent.
6. **Save the cursor last.** The source cursor saves only after the read completes and every sink commits. AIDF saves its watermark even when the source failed to connect, so a skipped source loses its window for good (`AIDF.Indexer/IndexerService.cs:93`, `:292-296`) [CODE].
7. **Detect deletes.** Optional full key scan (`ReadKeysAsync`) diffed against the state store. Watermark sources need it, because they never see deletes.
8. **Carry over from `ADF.Indexer/AzdoIndexerService.cs`** [CODE]:
   - Staging-then-swap with a 95% completion guard for full rebuilds (:162, :190, :204, :212).
   - Preflight check (:160) and endpoint warm-up (:373).
   - Per-doc error isolation with a capped log (:411, :495-500).
   - Run record written in `finally` (:143).
   - 5000-char chunk cap (:56).
   - `CapKey` FNV (:644).
9. **State store.** One SQLite file per install (`Microsoft.Data.Sqlite`, already used by AIDF). State stays out of every sink, so sinks can be added or dropped freely [INFERRED].

---

## 3. Source matrix

| Source | Windows | Linux | How configured sources are found | Incremental | Code origin |
|---|---|---|---|---|---|
| AzDO Git | yes | yes: REST over netstandard2.0 [CODE] | Org URL + PAT. Repo listing via REST is new code [INFERRED] | Blob SHA per file: parsed at `AzureDevOpsService.cs:481`, dropped at `GitSourceProvider.cs:96-101` [CODE] | ADF, lift |
| AzDO TFVC | yes | yes [CODE] | Org URL + PAT + path | `ChangeDate` (`TfvcSourceProvider.cs:84`) [CODE] + text hash | ADF, lift |
| GitHub | yes | yes [CODE] | Repo URL in https, ssh or `owner/repo` form (`GitHubService.cs:75`) [CODE]. No listing | Zipball carries no SHA or author [CODE]. Text hash. A commit-SHA short-circuit is new [INFERRED] | ADF, lift |
| Folder | yes | yes [INFERRED] | Path + globs (`SourceFilterOptions`) | mtime + text hash | From the disk walk at `GitHubSourceProvider.cs:222-230` [CODE] |
| Local Git | yes | yes [INFERRED] | Path to a clone | `git diff --name-status <lastSha> HEAD` [INFERRED] | New. Neither repo has any local-git code [CODE] |
| ODBC | yes | yes, needs unixODBC [LOCAL][U3] | `SQLDataSources` P/Invoke [U6][LOCAL] | rowversion / watermark / hash (table below) | New |
| OLE DB | yes | **no**: throws `PlatformNotSupportedException` [LOCAL][U1] | `OleDbEnumerator.GetElements()` for providers [U9]. `.udl` files [U10, search snippet only]. Pasted connection string | Same as ODBC | New |
| Jira | yes | yes [INFERRED] | Instance URL + token | Honors `since` (`JiraAdapter.cs:93`) [CODE] | AIDF, later |
| Confluence | yes | yes [INFERRED] | Instance URL + token | Honors `since` (`ConfluenceAdapter.cs:217`) [CODE] | AIDF, later |

The AIDF GitHub and AzDO adapters are not worth bringing. The ADF versions are better, and the AIDF GitHub adapter ignores `since` (`GitHubAdapter.cs:87-97`) [CODE].

**DB table change strategy (ODBC and OLE DB). Pick one per pipeline.**

| Mode | Needs | Updates | Deletes | Per-run cost |
|---|---|---|---|---|
| rowversion | SQL Server `rowversion` column, `WHERE rv > ?` | yes | needs key scan | changed rows |
| watermark | a modified datetime or int the app actually maintains. Use `>=` and hash-dedupe the boundary rows | yes, if maintained | needs key scan | changed rows |
| hash (default) | key column only | yes | yes (key diff) | full read; embeds only changed rows |

Hash mode is the default: it works on any driver, and reading costs far less than embedding [INFERRED].

---

## 4. ODBC / OLE DB detail

**Discover**
- **ODBC DSNs:** P/Invoke `SQLDataSources` (`libodbc.so.2` on Linux [LOCAL]; `odbc32.dll` on Windows [INFERRED]).
  - Constants: FETCH_NEXT=1, FETCH_FIRST=2, FETCH_FIRST_USER=31, FETCH_FIRST_SYSTEM=32. A wrong constant loops forever [LOCAL].
  - Description = driver name [U6].
  - Honors `ODBCINI`/`ODBCSYSINI`, so tests can use a temp ini [LOCAL].
  - Also list drivers via `SQLDrivers` so a user can build a DSN-less string [INFERRED].
- **Windows bitness:** there are two odbcad32 views, 64-bit and 32-bit [U8]. A 64-bit runner sees 64-bit DSNs only [INFERRED]. Registry fallback is `HKLM`/`HKCU\SOFTWARE\ODBC\Odbc.ini` [U7].
- **OLE DB providers:** `OleDbEnumerator.GetElements()` returns a DataTable [U9]. A 64-bit process cannot load a 32-bit-only provider such as Jet 4.0 [INFERRED].
- **linus7795 today:** zero DSNs. The only driver is `ODBC Driver 18 for SQL Server` 18.7.1.1 [LOCAL].

**Pick**
- **Tables:** `GetSchema("Tables")` takes 3 restrictions (catalog, schema, table). Pass skipped ones as `null`, not `""`. A 4th restriction throws [LOCAL][U11]. `Views` is its own collection [LOCAL].
- **Columns:** `GetSchema("Columns")` returns the raw SQLColumns shape [LOCAL].
- **Fallback:** collections vary by driver [INFERRED]. Fall back to `SELECT * FROM t WHERE 1=0` + `GetSchemaTable()`. Not `TOP 0`, which is SQL Server dialect [INFERRED].
- **Key suggestion:** `GetSchema("Indexes")` (SQLStatistics rules) [U11]. Otherwise the user picks.
- **OLE DB:** `GetSchema` / `GetOleDbSchemaTable` exist [U12]. Untested anywhere [UNVERIFIED].
- **Free SQL** instead of a table:
  - It must project the key column.
  - The watermark is applied as `SELECT * FROM ( <query> ) q WHERE q.wm > ?`. Derived-table syntax on non-SQL-Server drivers is [UNVERIFIED].
  - ODBC parameters are positional `?` [INFERRED].

**Map (pipeline JSON, written by Claude from `vf discover` output)**

```json
{
  "name": "customers",
  "source":   { "kind": "odbc", "dsn": "Sales", "table": "dbo.Customers" },
  "key":      [ "CustomerId" ],
  "text":     "{Name}\nCity: {City}\nNotes: {Notes}",
  "metadata": [ "Region", "Status", "ModifiedAt" ],
  "filterable": [ "Region", "Status" ],
  "change":   { "mode": "hash" },
  "chunk":    { "kind": "row", "maxChars": 5000, "overflow": "text" },
  "embedder": { "kind": "http", "url": "http://linus7795:8080/embed", "model": "bge-code-v1" },
  "sinks":    [ "sql-local", "qdrant-local" ]
}
```

- **Secrets:** connection strings with passwords live in env vars or the secret store, never in pipeline JSON. Keep the AIDF env pattern `{APP}_{SOURCE}_{CRED}` (`AIDF.Core/Config.cs:266-286`) [CODE].
- **Template rendering:** use the invariant culture for dates and decimals, or hashes change between machines and every row re-embeds [INFERRED].
- **Empty values:** a template line whose only field is null is dropped.
- **Binary columns:** BLOBs are skipped.

**Batch read:** one forward-only reader with `CommandBehavior.SequentialAccess` and a configurable timeout, with rows buffered into embed batches. There is no paging, because `TOP`, `LIMIT` and `FETCH FIRST` differ by dialect [INFERRED]. Restart safety comes from the hash and watermark state.

---

## 5. Sink matrix

| | SQL Server 2025 VECTOR | Azure AI Search | Qdrant |
|---|---|---|---|
| Impl | Hand-rolled, Microsoft.Data.SqlClient 7.1.1 [LOCAL restore] | MEVD AzureAISearch connector 1.0.0 (pins Azure.Search.Documents 11.7.0) | MEVD Qdrant connector 1.0.0. Fallback: lifted AIDF `VectorService` on Qdrant.Client 1.19.0 [U27] |
| Max dims | float32 1,998, float16 3,996 [U5]. 1999 and 3997 rejected here [LOCAL] | 4,096 per field, all tiers [U24] | Not a constraint at these sizes [UNVERIFIED] |
| Types | float32. float16 is preview and needs `PREVIEW_FEATURES = ON` [U5]; the OFF case is untested | Single, Half, Int16, SByte, Byte [U23]. MEVD is float only [U19] | Float32, plus Float16 via the direct client [LOCAL]. MEVD is float only [U19] |
| Create | `VECTOR(n)` from the probed dim. Lift `ValidateVectorCapabilitiesAsync` (`SchemaInitializer.cs:300`, `:567-675`) [CODE] | Vector field + HNSW profile, api-version 2026-04-01 stable [U22][U25]. Must check an existing index; AIDF never diffs it (`AzureSearchService.cs:223-227`) [CODE] | `VectorParams{Size, Cosine}`. Must check an existing collection's size; AIDF never does [CODE] |
| Upsert | SqlBulkCopy into staging with `SqlVector<float>` (500 rows OK [LOCAL]), then MERGE to live. float16: bulk copy JSON strings, because `SqlVector<Half>` throws [LOCAL]. The MEVD SQL connector is single-row MERGE [U17] | `mergeOrUpload`, one doc per chunk. Max 1,000 docs and 16 MB per batch [U24]. Push only: integrated vectorization can't pull from arbitrary ODBC [U26] | Batched upsert (AIDF used 100) [CODE] |
| Delete | `DELETE` by ChunkId list [INFERRED] | Delete action by key [INFERRED] | By point id. By payload filter needs the direct client [INFERRED] |
| Keys | ChunkId GUID. DiskANN needs an int clustered PK [U20] | GUID string. AIDF `SanitizeKey` collides `a/b` with `a_b` (`:505-521`) [CODE], so never derive keys from raw source keys | GUID. AIDF used an MD5-derived GUID, with the original id in payload (`:283-288`, `:273`) [CODE] |
| Index | DiskANN is preview [U20] and needs 100+ rows [U20]. Here it built only with PREVIEW_FEATURES=ON, then the table went read-only [LOCAL]. With OFF: "Unknown object type 'VECTOR'" [LOCAL]. Rule: load, build in `FinalizeAsync`, drop before the next load. No index = exact `VECTOR_DISTANCE` scan | HNSW, writes stay live [INFERRED] | HNSW, writes stay live [INFERRED] |
| Retry | `SqlResilience` (12 tries, 3-15 s) [CODE]. Use it on writes too; the ADF indexer doesn't [CODE] | `HttpRetry` reworked for POST [CODE gap] | Client-level retry [UNVERIFIED] |

**Others:** PgVector, SQLite-vec, Redis, Weaviate, Cosmos DB, DocumentDB and In-Memory all come through one generic `MevdSink` [U15]. In-Memory doubles as the unit-test sink.

---

## 6. Embedders

| Embedder | Exists today | Notes |
|---|---|---|
| **Local GPU service, linus7795:8080** | **Yes, running.** uvicorn bound to `0.0.0.0:8080` [LOCAL, ps] | Detail below the table [CODE `/home/dan/embed/embed_server.py`] |
| HF Inference Endpoint | Yes: `ADF.Core/Services/Embedding/HuggingFaceEmbedder.cs` | The same `{inputs}` client fits the local service [CODE]. Cold after idle, and each wake costs money (memory notes) [UNVERIFIED here] |
| ONNX in-process | Code in both repos | ADF `EmbeddingService.cs` [CODE]: a real batched pass, but E5 prefixes are hardcoded (:119, :128, :361) and maxlen is 512 (:54). AIDF `OnnxEmbeddingProvider.cs` [CODE]: CPU-only, 384 max, pads every call. The CPU fallback for a box with no CUDA |
| Ollama | `AIDF.Core/Services/Embedding/OllamaEmbeddingProvider.cs` | Legacy `/api/embeddings`, one text per call (:282), and returns a **zero vector on failure** (:299-303) [CODE]. Must throw instead. Not seen running on linus7795 [LOCAL, box survey] |
| OpenAI-compatible / Azure OpenAI / TEI | No | New small class, `{model, input[]}` shape [INFERRED] |

**Local GPU service details** [CODE `/home/dan/embed/embed_server.py`]:
- **Request:** `POST /embed {inputs: str|list, model, is_query}` returns a list of vectors (:3, :195-204).
- **Batching:** 8 per GPU batch (:121).
- **Truncation:** maxlen 2048 tokens, truncated silently (:28).
- **Profiles:** bge-code-v1 1536, qwen3-emb-0.6b 1024, jina-code-1.5b 1536 (:25-42).
- **One embedder resident:** asking for another evicts the current one (:14).

**IEmbedder edits:**
- Start from the ADF interface (async, batch, query/passage split) and add `ModelId`, `Dimension` and `MaxBatch`.
- Throw on any failure.
- Send list `inputs`.
- One retry path through `HttpRetry`.

**Default:** `HttpEmbedder` to `:8080` on both boxes. The Windows box embeds over the LAN [INFERRED]; firewall reachability is [UNVERIFIED].

---

## 7. Reuse map (only files the surveys read)

| From | To | Action |
|---|---|---|
| `ADF.Core/Services/Integration/HttpRetry.cs` | `Core/Http/HttpRetry.cs` | **edit**: make it public, take `Func<HttpRequestMessage>` so POST can resend, use ILogger, retry timeouts |
| `ADF.Core/Services/Integration/AzureDevOpsService.cs` | `Sources.Code/AzureDevOps/AzureDevOpsClient.cs` | **edit**: ILogger instead of Console (:140, :221, :374), throw instead of an empty list, keep `ObjectId` (:481), fix the N+1 metadata calls (:282, :338) |
| `ADF.Core/Models/AzureDevOps/ChangesetInfo.cs` | `Sources.Code/AzureDevOps/` | **as-is** (dependency) |
| `ADF.Core/Services/Integration/GitHubService.cs` | `Sources.Code/GitHub/GitHubClient.cs` | **as-is**, plus a User-Agent change (:56) and temp cleanup after each run (:196) |
| `ADF.Core/Services/Sources/{Tfvc,Git,GitHub}SourceProvider.cs` | `Sources.Code/*Source.cs` | **edit**: adapt onto `ISource`. Git keeps the blob SHA as `Version`. The GitHub disk walk becomes `FolderSource` |
| `ADF.Core/Services/Sources/ISourceProvider.cs` | none | **drop**: file-shaped, replaced by `ISource` |
| `ADF.Core/Services/Sources/SourceFilterOptions.cs` | `Core/Filtering/` | **as-is** |
| `ADF.Indexer/Indexing/RoslynChunker.cs` | `Chunking.CSharp/` | **as-is** (needs ImplicitUsings on). Chunk id computed outside |
| `ADF.Indexer/Indexing/CodeChunkDto.cs` | `Chunking.CSharp/` | **as-is**. Ignore its id (:88) |
| `ADF.Indexer/Indexing/RoslynMetadataExtractor.cs` | `Chunking.CSharp/` | **as-is**, plus a small adapter from `FileMetadata` to the metadata dictionary |
| `ADF.Indexer/AzdoIndexerService.cs` | `Core/Pipeline/PipelineRunner.cs` | **rewrite**, keeping the ideas in section 2 |
| `ADF.Indexer/IndexerForm.cs`, `ADF.Core/FileHelper.cs` | none | **drop** |
| `ADF.Core/Config.cs` | `Core/Options/*` | **rewrite** as typed per-pipeline options. Keep the range-validate checks (:645-700) |
| `ADF.Core/Services/Storage/SchemaInitializer.cs` | `Sinks.SqlServer/SqlSchema.cs` | **edit**: keep :243-292, :300, :567-675, :753-790 and staging/swap. Drop CodeFiles/CodeChunks DDL, FTS, the `SearchCode` proc and telemetry. Dim comes from the probe |
| `ADF.Core/Services/Search/SqlVectorService.cs` | `Sinks.SqlServer/SqlServerSink` (health) | **rewrite**: keep only the idea (embedded vs total, index present) |
| `ADF.Core/Services/Utilities/SqlResilience.cs` | `Core/Sql/` | **as-is** |
| `ADF.Core/Services/Embedding/IEmbedder.cs` | `Core/Embedding/` | **edit**: add ModelId, Dimension, MaxBatch |
| `ADF.Core/Services/Embedding/HuggingFaceEmbedder.cs` | `Embedders/HttpEmbedder.cs` | **edit**: dim and templates from options (:98, :203), list inputs (:68-85, :105-121), no sync-over-async (:53, :61), `HttpRetry` instead of the bespoke loop (:162-179) |
| `ADF.Core/Services/Embedding/EmbeddingService.cs` | `Embedders/OnnxEmbedder.cs` | **edit** (later): prefixes and maxlen from options, check hidden size against the probe |
| `ADF.Core/Services/Embedding/Tokenizer.cs` | none | **drop**. Use Microsoft.ML.Tokenizers, as `ADF.Core/Services/Reranking/BgeReranker.cs:9` already does |
| `ADF.Core/Services/Utilities/SecretStore.cs`, `SecretBox.cs` | `Core/Secrets/` | **rewrite**: keep env-first `Get(name)`. SecretBox is obfuscation-grade by its own doc (:9-12) and uses a constant key and SHA1 PBKDF2 (:23, :47, :125) |
| `ADF.Eval/Services/EvalRunner.cs` :162-168 | `Core/Embedding/EmbedderProbe.cs` | **rewrite** (pattern only) |
| `AIDF.Core/Interfaces/IDataSourceAdapter.cs` | `Core/Sources/ISource.cs` | **rewrite**: keep the `IAsyncEnumerable` + `since` shape, drop `GetMetadataFieldsAsync` and `FetchItemByIdAsync` |
| `AIDF.Core/Models/DataSource/DataItem.cs` | `Core/Models/SourceDocument.cs` | **edit**: add ContentHash and Version, use a string kind |
| `AIDF.Core/Models/DataSource/DataSourceType.cs` | none | **drop** (closed enum) |
| `AIDF.Core/Services/DataSources/{Jira,Confluence}Adapter.cs` | `Sources.Saas/` (later) | **edit**: instance options instead of static Config |
| `AIDF.Core/Services/DataSources/{GitHub,AzureDevOps}Adapter.cs` | none | **drop** |
| `AIDF.Core/Services/Search/VectorService.cs` | `Sinks.Qdrant/QdrantSink.cs` (fallback) | **edit**: options instead of Config, check the existing dim, delete by id and filter, https + API key (:46-50), stop swallowing errors (:94-97), Qdrant.Client 1.19.0 |
| `AIDF.Core/Services/Search/AzureSearchService.cs` | none | **reference only**: no vector field, writer or query [CODE]. MEVD replaces it |
| `AIDF.Core/Interfaces/IEmbeddingProvider.cs` | merged into `IEmbedder` | **drop** (sync, no cancellation) |
| `AIDF.Core/Services/Embedding/OllamaEmbeddingProvider.cs` | `Embedders/OllamaEmbedder.cs` | **edit**: throw instead of returning a zero vector, batch input |
| `AIDF.Core/Services/Embedding/OnnxEmbeddingProvider.cs`, `Tokenizer.cs` | none | **drop**: the ADF ONNX path batches properly |
| `AIDF.Core/Services/Utilities/MethodChunker.cs` | none | **drop**: Roslyn wins |
| `AIDF.Indexer/IndexerService.cs` | none | **drop**: loop shape only. Watermark bug noted in section 2 |
| `/home/dan/embed/embed_server.py` | unchanged, called over HTTP | **as-is** |

New code with no donor: ODBC source + DSN P/Invoke, OLE DB source, row mapper, text/markdown/row chunker, SQL sink write path, `MevdSink`, state store, CLI.

---

## 8. Platform decision

**Facts:**
- OLE DB is Windows-only:
  - It throws on Linux [LOCAL].
  - The package ships `runtimes/win` only [LOCAL][U2].
  - Non-Windows TFMs compile a stub [U1].
- ODBC works on both [LOCAL][U3].
- DSNs and OLE DB providers are per-machine, so a runner only sees what is configured on the box it runs on [INFERRED].
- The GPU, SQL 2025 and Qdrant are all on linus7795 [LOCAL].

| Option | Gets | Costs |
|---|---|---|
| A. WinForms, `net10.0-windows` only (like the ADF indexer) | OLE DB, ODBC and Windows DSNs in one exe. A familiar shape | Can't run on linus7795, next to the GPU and both sinks. Mouse-heavy |
| **B. Cross-platform core + `vf` CLI on both OSes. OLE DB in a `net10.0-windows` plugin. A picker UI later, optional** | Runs next to the data on either box. Claude writes the pipeline JSON from `vf discover` output, so input is near zero. OLE DB lights up only on Windows | No picker UI on day one |
| C. Web UI (Blazor) on linus7795 + headless runner | Click-to-pick DSN, table and columns from any browser | Still blind to Windows DSNs and OLE DB without a Windows agent. Most code |

**Recommendation: B.** Same core, so a WinForms or web picker can be bolted on later without a rewrite [INFERRED].

```
vf discover dsns | drivers | providers        (providers: Windows only)
vf discover tables  --dsn X
vf discover columns --dsn X --table Y
vf run pipeline.json [--rebuild] [--dry-run]
vf status pipeline.json
```

Projects:
- `VectorForager.Core`
- `.Sources.Code`
- `.Sources.Odbc`
- `.Sources.OleDb` (`net10.0-windows`)
- `.Chunking.CSharp`
- `.Embedders`
- `.Sinks.SqlServer`
- `.Sinks.Mevd`
- `.Cli`
- `.Tests`

All are `net10.0` with ImplicitUsings on, except `.Sources.OleDb`. The box has SDK 10.0.112 [LOCAL].

---

## 9. MVP slice

ODBC table on local SQL 2025 -> GPU service on :8080 -> SQL VECTOR + Qdrant, all on linus7795. Don't touch the `AzureDevOpsForager` database, `CodeChunks`, or the `aidataforager_code` collection.

1. **Skeleton.** Core, Sources.Odbc, Embedders, Sinks.SqlServer, Sinks.Mevd, Cli, Tests. Copy HttpRetry, SqlResilience, IEmbedder and HuggingFaceEmbedder, with the section 7 edits.
2. **DSN.** A user DSN in `~/.odbc.ini` pointing at `localhost:1433` via ODBC Driver 18 (there are none today [LOCAL]). Tests use a temp `ODBCINI` [LOCAL]. The password comes from an env var, never the ini or the JSON.
3. **Discovery.** `vf discover dsns|tables|columns`: SQLDataSources P/Invoke + `GetSchema`, with the `WHERE 1=0` fallback.
4. **Source table.** Pick a table already on the box, read-only.
5. **Mapping and chunking.** Row mapper (template, metadata, key, SHA-256) + row chunker: one row = one chunk, 5000-char cap, overflow goes to a token-window text chunker.
6. **Embedder.** `HttpEmbedder` to :8080 with `model` sent explicitly. Probe the dim (expect 1536), then confirm `/health` `loaded_embed` names the requested model.
7. **SQL sink.** A new `VectorForager` database. Staging table, SqlBulkCopy with `SqlVector<float>`, MERGE to live, delete by ChunkId. No DiskANN in the MVP.
8. **Qdrant sink.** MEVD connector on gRPC 6334, collection `vf_<pipeline>`. If the connector fights back, swap in the lifted AIDF `VectorService` (already proven against this server [LOCAL]).
9. **State.** SQLite state store + run record.
10. **Incremental behavior.**
    - Run 1 embeds N.
    - Run 2 embeds 0.
    - Update one row: 1 embed, and the old chunk is gone from both sinks.
    - Delete one row: gone from both sinks after the key scan.

**Done when three independent checks agree:**
- **Counts:** the source row count via ODBC = SQL sink rows = Qdrant point count.
- **Vector fidelity:** re-embed 3 rows directly with curl to :8080. Cosine against the stored vector in SQL and in Qdrant >= 0.9999.
- **Retrieval agreement:** 5 queries, top-5 chunk ids from SQL `VECTOR_DISTANCE` vs Qdrant. Exact vs HNSW, so allow near-ties [INFERRED].

---

## 10. Open risks and unknowns

| # | Risk | Label |
|---|---|---|
| 1 | **bge-code-v1 EOS conflict.** `embed_server.py:27-32` omits EOS to match the hosted TEI endpoint (cosine 0.999997 without EOS vs 0.8238 with). A memory note says bge-code-v1 needs EOS or retrieval degrades. Queries must match how the index was built, so record the EOS choice with the model id in sink metadata | Conflict. Code [CODE], quality claim [UNVERIFIED] |
| 2 | All three local embed profiles are code-search models or prompts (:25-42). Quality on business-row text is unmeasured. A text profile means editing `embed_server.py`, which the ADF work uses. Safer to copy it to a second service on another port. VRAM fit on the 12 GB card is unmeasured | [CODE] / [UNVERIFIED] |
| 3 | An unknown `model` key silently falls back to bge-code-v1 (:199-201). jina-code-1.5b is also 1536, so a dim probe won't catch a typo. Check `/health` | [CODE] |
| 4 | One embedder stays resident. Two pipelines on different models thrash the GPU | [CODE] / [INFERRED] |
| 5 | DiskANN on SQL 2025 Linux is preview, needs PREVIEW_FEATURES=ON, and makes the table read-only. Exact-scan cost at large row counts is unmeasured | [LOCAL][U20] / [UNVERIFIED] |
| 6 | float16 VECTOR is preview. The PREVIEW_FEATURES=OFF behavior for float16 is untested | [U5] / [UNVERIFIED] |
| 7 | The MEVD Qdrant and Azure connectors are untested on this box, including their delete-by-key surface | [UNVERIFIED] |
| 8 | Which api-version Azure.Search.Documents 12.0.0 targets. The MEVD connector pins 11.7.0 | [UNVERIFIED] / NuGet nuspec |
| 9 | ODBC schema collections vary by driver. Only the SQL Server driver has been tested | [INFERRED] / [LOCAL] |
| 10 | OLE DB is untested end to end. 32-bit-only providers won't load in a 64-bit runner | [UNVERIFIED] / [INFERRED] |
| 11 | Windows box to linus7795:8080. The service binds 0.0.0.0 [LOCAL]. The LAN firewall is unchecked | [UNVERIFIED] |
| 12 | Connection-string secrets. The ADF SecretBox is obfuscation-grade. A real store (DataProtection or DPAPI, per-user path) is needed before any remote DSN | [CODE] / [INFERRED] |
| 13 | Throughput. ADF measured one-chunk-per-call faster than batching (`AzdoIndexerService.cs:485-486`), on a different setup. The GPU service batches 8. The right batch size and parallelism are unmeasured | [CODE] / [UNVERIFIED] |
| 14 | Watermark columns the app doesn't maintain, or timestamp ties, miss updates. Hash mode is the safe default | [INFERRED] |
| 15 | Qdrant.Client 1.19.0 against server 1.17.0 works, but there is no official compatibility table | [LOCAL][U28] |

---

## URLs

- U1 https://github.com/dotnet/runtime/blob/main/src/libraries/System.Data.OleDb/src/System.Data.OleDb.csproj
- U2 https://www.nuget.org/packages/System.Data.OleDb
- U3 https://www.nuget.org/packages/System.Data.Odbc
- U4 https://learn.microsoft.com/en-us/sql/connect/odbc/linux-mac/installing-the-microsoft-odbc-driver-for-sql-server?view=sql-server-ver17
- U5 https://learn.microsoft.com/en-us/sql/t-sql/data-types/vector-data-type?view=sql-server-ver17
- U6 https://learn.microsoft.com/en-us/sql/odbc/reference/syntax/sqldatasources-function
- U7 https://learn.microsoft.com/en-us/sql/odbc/reference/install/registry-entries-for-data-sources
- U8 https://learn.microsoft.com/en-us/sql/relational-databases/native-client/odbc/data-source-names-and-64-bit-operating-systems?view=sql-server-ver15
- U9 https://learn.microsoft.com/en-us/dotnet/api/system.data.oledb.oledbenumerator?view=net-10.0-pp
- U10 https://learn.microsoft.com/en-us/host-integration-server/core/creating-a-connection-string1 (search snippet only)
- U11 https://learn.microsoft.com/en-us/dotnet/api/system.data.odbc.odbcconnection.getschema?view=net-10.0-pp
- U12 https://learn.microsoft.com/en-us/dotnet/api/system.data.oledb.oledbconnection?view=net-10.0-pp
- U13 https://devblogs.microsoft.com/dotnet/ai-vector-data-dotnet-extensions-ga/
- U14 https://www.nuget.org/packages/Microsoft.Extensions.VectorData.Abstractions
- U15 https://github.com/CommunityToolkit/AI
- U16 https://learn.microsoft.com/en-us/semantic-kernel/concepts/vector-store-connectors/out-of-the-box-connectors/
- U17 https://github.com/CommunityToolkit/AI/blob/main/MEVD/src/SqlServer/SqlServerCommandBuilder.cs
- U18 https://github.com/CommunityToolkit/AI/blob/main/MEVD/src/SqlServer/SqlServerCollection.cs
- U19 https://github.com/CommunityToolkit/AI/tree/main/MEVD/src
- U20 https://learn.microsoft.com/en-us/sql/t-sql/statements/create-vector-index-transact-sql?view=sql-server-ver17
- U21 https://techcommunity.microsoft.com/blog/sqlserver/released-general-availability-of-microsoft-data-sqlclient-6-1/4453101 (search snippet only)
- U22 https://learn.microsoft.com/en-us/rest/api/searchservice/search-service-api-versions
- U23 https://learn.microsoft.com/en-us/rest/api/searchservice/supported-data-types
- U24 https://learn.microsoft.com/en-us/azure/search/search-limits-quotas-capacity
- U25 https://learn.microsoft.com/en-us/azure/search/vector-search-how-to-create-index
- U26 https://learn.microsoft.com/en-us/azure/search/vector-search-integrated-vectorization
- U27 https://www.nuget.org/packages/Qdrant.Client
- U28 https://github.com/qdrant/qdrant-dotnet , https://qdrant.tech/documentation/interfaces/