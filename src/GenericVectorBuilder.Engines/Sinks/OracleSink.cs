using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using GenericVectorBuilder.Core.Contracts;
using GenericVectorBuilder.Engines.Common;
using Oracle.ManagedDataAccess.Client;

namespace GenericVectorBuilder.Engines.Sinks;

/// <summary>
/// Writes vectors to Oracle AI Database Free (the 23ai Free line; the container reports itself as
/// 26ai 23.26) and searches them with SQL: one table per pipeline named gvb_{pipeline}, holding
/// the chunk id (RAW(16), the Guid in big-endian byte order so the hex matches Guid "N" format),
/// doc_key, table_name (a column cannot be called "table" in Oracle), origin, ordinal,
/// chunk_text (CLOB), meta (a native JSON column holding the document metadata keyed by its
/// original names, where the other engines use meta_{key} fields) and the embedding as
/// VECTOR(dimension, FLOAT32).
/// Default search: an in-memory HNSW index (CREATE VECTOR INDEX ... ORGANIZATION INMEMORY
/// NEIGHBOR GRAPH, DISTANCE COSINE, TYPE HNSW, NEIGHBORS 16, EFCONSTRUCTION 128) queried with
/// FETCH APPROX FIRST n ROWS ONLY WITH TARGET ACCURACY PARAMETERS ( EFSEARCH n ), n from <see cref="OracleSinkOptions.HnswEfSearch"/>.
/// Exact search: FETCH EXACT FIRST n ROWS ONLY, which Oracle answers with a full scan of the
/// table and never touches the index.
/// Scores are cosine similarity: VECTOR_DISTANCE with COSINE returns 1 minus the similarity.
/// Why an HNSW index and not IVF: the in-memory graph is the closest match to what every other
/// engine in the benchmark uses. It lives in the database's vector memory pool, which Free does
/// not size by default; deploy/engines/oracle-init sets it (768 MB) and the index creation here
/// fails with a plain-English message when the pool is missing.
/// Why writes are delete then insert inside one transaction: one array-bound round trip per
/// batch, the result is the same however many times a batch is retried, and a re-upsert replaces
/// every column including the metadata.
/// Why every write and every DDL statement goes through one process-wide gate (WRITE_GATE): measured
/// 2026-10-03, creating an HNSW index while another session commits rows into a different
/// HNSW-indexed table can hang for good (the two sessions wait on each other through a library cache
/// lock and a row lock, which Oracle's deadlock detector does not see), or fail with ORA-00054 or
/// ORA-00060 (wrapped in ORA-51906) on the shared dictionary table SYS.COMMIT_SCN_LOG$. One writer at
/// a time inside this process rules that out. A second process writing to the same database at the
/// same moment is not covered; the lock conflicts it causes are retried (see WithRetryAsync), but a
/// benchmark should not run two Oracle writers at once.
/// Why <see cref="RefreshIndexAsync"/> exists: Oracle merges new rows into the HNSW graph in the
/// background and in steps, and searches scan the not yet merged rows exactly, so a fresh bulk load
/// searches slowly until the graph is rebuilt. Measured 2026-10-04 on this box: 2,000 vectors loaded
/// into a table whose HNSW index was created empty left the graph at 0 vectors
/// (V$VECTOR_INDEX.NUM_VECTORS 0, V$VECTOR_CHANGE_LOG_PARTITION.NUM_INSERTS 2,000) and the plan of
/// the default search still said VECTOR INDEX HNSW SCAN, so the plan alone proves nothing. A rebuild
/// took 5 s and left the graph at 2,000 with an empty change log.
/// Why <see cref="FinishLoadAsync"/> rebuilds and then reads those views: "Ready" needs four things
/// from Oracle itself. The graph holds every row (V$VECTOR_INDEX.NUM_VECTORS equals the table's
/// COUNT(*)), no inserts or deletes wait in the change log, the index is VALID in USER_INDEXES, and
/// EXPLAIN PLAN of the default search shows VECTOR INDEX HNSW SCAN. The V$ views need a login the
/// application user does not have, see <see cref="OracleSinkOptions.EvidenceUser"/>.
/// Quirk: Oracle stores an empty string as NULL, so an empty origin or chunk text comes back as
/// an empty string only because this sink maps NULL back to "".
/// </summary>
public sealed class OracleSink : ISink, IExactSearchSink, IEngineDescription, IIndexFinisher
{
   #region Data Members

   private const int DELETE_BATCH = 1000;
   private const int MAX_ATTEMPTS = 5;
   private const int MAX_TABLE_NAME = 60;
   private const int MAX_INLINE_TEXT_BYTES = 32000;
   private const int MAX_KEY_BYTES = 4000;
   private const int NO_SUCH_TABLE = 942;
   private const int COMMAND_SECONDS = 300;
   private const int STEADY_READS = 2;
   private const string TABLE_PREFIX = "gvb_";
   private static readonly string[] LOCK_CONFLICT_CODES = { "ORA-00054", "ORA-00060" };
   private static readonly Regex SAFE_NAME = new( "^[a-z0-9_]+$", RegexOptions.Compiled );
   private static readonly Regex DIMENSION_PATTERN = new( @"^VECTOR\(\s*(\d+)", RegexOptions.Compiled | RegexOptions.IgnoreCase );
   private static readonly SemaphoreSlim WRITE_GATE = new( 1, 1 );
   private static readonly TimeSpan POLL_INTERVAL = TimeSpan.FromSeconds( 1 );
   private static readonly TimeSpan PROGRESS_INTERVAL = TimeSpan.FromSeconds( 15 );

   private readonly OracleSinkOptions _options;
   private readonly string _connectionString;
   private readonly string _evidenceConnectionString;

   #endregion Data Members

   #region Constructor

   /// <summary>
   /// Creates a sink for the local Oracle container with the login from the secrets file.
   /// </summary>
   public OracleSink() : this( OracleSinkOptions.LocalDefaults() )
   {
   }

   /// <summary>
   /// Creates a sink with explicit settings. Nothing connects until the first call, so the
   /// engine catalog can create every sink without every engine up.
   /// </summary>
   /// <param name="options">Connection and index settings.</param>
   public OracleSink( OracleSinkOptions options )
   {
      _options = options;
      _connectionString = new OracleConnectionStringBuilder
      {
         UserID = options.User,
         Password = options.Password ?? string.Empty,
         DataSource = $"{options.Host}:{options.Port}/{options.ServiceName}",
         ConnectionTimeout = 30,
      }.ConnectionString;
      _evidenceConnectionString = string.IsNullOrEmpty( options.EvidenceUser )
         ? _connectionString
         : new OracleConnectionStringBuilder
         {
            UserID = options.EvidenceUser,
            Password = options.EvidencePassword ?? string.Empty,
            DataSource = $"{options.Host}:{options.Port}/{options.ServiceName}",
            ConnectionTimeout = 30,
         }.ConnectionString;
   }

   #endregion Constructor

   #region Public Methods

   /// <inheritdoc />
   public string Name => "oracle";

   /// <inheritdoc />
   public string Engine => "Oracle AI Database Free 23.26 (23ai line, VECTOR FLOAT32)";

   /// <inheritdoc />
   public string IndexDescription =>
      $"HNSW in-memory neighbor graph NEIGHBORS={_options.HnswNeighbors} EFCONSTRUCTION={_options.HnswEfConstruction}, EFSEARCH={_options.HnswEfSearch} per query, cosine; exact mode = FETCH EXACT FIRST (full scan)";

   /// <inheritdoc />
   public string ComposeFile => "oracle.compose.yaml";

   /// <inheritdoc />
   public string Durability =>
      "By these settings a committed row should survive a crash or power loss. The sink uses a plain COMMIT and commit_logging, commit_wait and commit_write are unset in the database, so every commit "
      + "waits for its redo to be written. Measured 2026-10-04: 50 separate client commits raised V$SYSSTAT 'redo synch writes' by 55 (the 50 commits plus the CREATE and DROP "
      + "of the probe table), and the log writer and the datafile writer hold their files open with O_DSYNC (open flags 02110002, filesystemio_options none). The database "
      + "runs NOARCHIVELOG (V$DATABASE.LOG_MODE), so redo serves crash recovery only and there is no point-in-time restore. The HNSW graph lives in the 768 MB vector memory "
      + "pool (oracle-init/01-vector-memory.sh) and is not the durable copy; the table is. "
      + "Not tested by cutting power; whether the disk's own write cache reaches the media was not checked.";

   /// <summary>
   /// Optional receiver for progress lines while <see cref="FinishLoadAsync"/> works (one line
   /// every 15 seconds). Why: the graph rebuild can take minutes and must show it is moving.
   /// </summary>
   public Action<string>? Progress { get; set; }

   /// <inheritdoc />
   public async Task<bool> EnsureCollectionAsync( string collection, int dimension, CancellationToken ct )
   {
      string table = TableName( collection );
      await using OracleConnection connection = await OpenAsync( ct );
      int? existing = await ExistingDimensionAsync( connection, table, ct );
      bool created = false;
      if( existing.HasValue )
      {
         if( existing.Value != dimension )
         {
            throw new InvalidOperationException( $"Oracle table {table} holds {existing}-dim vectors but the embedder produces {dimension}. Reset the pipeline or use a new name." );
         }
      }
      else
      {
         await ExecuteAsync( connection, CreateTableSql( table, dimension ), ct );
         created = true;
      }

      await EnsureIndexAsync( connection, table, ct );
      return created;
   }

   /// <inheritdoc />
   public async Task UpsertAsync( string collection, IReadOnlyList<VectorRecord> records, CancellationToken ct )
   {
      string table = TableName( collection );
      foreach( VectorRecord[] batch in records.Chunk( _options.UpsertBatch ) )
      {
         await WithRetryAsync( () => GatedAsync( () => WriteBatchAsync( table, batch, ct ), ct ), ct );
      }
   }

   /// <inheritdoc />
   public async Task DeleteAsync( string collection, IReadOnlyList<Guid> chunkIds, CancellationToken ct )
   {
      string table = TableName( collection );
      foreach( Guid[] batch in chunkIds.Chunk( DELETE_BATCH ) )
      {
         await WithRetryAsync( () => GatedAsync( () => DeleteBatchAsync( table, batch, ct ), ct ), ct );
      }
   }

   /// <inheritdoc />
   public async Task<long> CountAsync( string collection, CancellationToken ct )
   {
      await using OracleConnection connection = await OpenAsync( ct );
      await using OracleCommand command = NewCommand( connection );
      command.CommandText = $"SELECT COUNT(*) FROM {TableName( collection )}";
      return Convert.ToInt64( await command.ExecuteScalarAsync( ct ) );
   }

   /// <inheritdoc />
   public Task<IReadOnlyList<SearchHit>> SearchAsync( string collection, float[] vector, int top, CancellationToken ct )
   {
      return QueryAsync( collection, vector, top, $"APPROX FIRST :top ROWS ONLY WITH TARGET ACCURACY PARAMETERS ( EFSEARCH {Math.Max( _options.HnswEfSearch, top )} )", ct );
   }

   /// <inheritdoc />
   public Task<IReadOnlyList<SearchHit>> SearchExactAsync( string collection, float[] vector, int top, CancellationToken ct )
   {
      return QueryAsync( collection, vector, top, "EXACT FIRST :top ROWS ONLY", ct );
   }

   /// <summary>
   /// Rebuilds the HNSW graph so it holds every committed row (DBMS_VECTOR.REBUILD_INDEX).
   /// Not part of any interface; call it after a bulk load when search speed matters.
   /// Why it exists: Oracle keeps new rows in a change journal and merges them into the graph in
   /// the background, in steps. Until a merge covers the newest rows, every search scans them
   /// exactly on top of the graph walk. Measured 2026-10-03, 100,000 random 1024-dimension vectors
   /// loaded in one pass: default search p50 500 to 925 ms (two runs) before a rebuild and 3.5 ms
   /// after; the rebuild took 284 s on Free's two CPU threads, so the sink does not do it on its own.
   /// </summary>
   /// <param name="collection">Pipeline collection name.</param>
   /// <param name="ct">Cancellation.</param>
   public async Task RefreshIndexAsync( string collection, CancellationToken ct )
   {
      string index = IndexName( TableName( collection ) ).ToUpperInvariant();
      await WithRetryAsync( () => GatedAsync( async () =>
      {
         await using OracleConnection connection = await OpenAsync( ct );
         await ExecuteAsync( connection, $"BEGIN DBMS_VECTOR.REBUILD_INDEX( '{index}' ); END;", ct, _options.IndexWaitMinutes * 60 );
      }, ct ), ct );
   }

   /// <summary>
   /// Rebuilds the HNSW graph so it holds every committed row, then reads Oracle's own views until
   /// they agree (see the class remarks for the four readings). Skips the rebuild when the views
   /// already say the graph is current. Throws a plain message when the views cannot be read (at
   /// once, before any rebuild) or when the state is not ready within
   /// <see cref="OracleSinkOptions.IndexWaitMinutes"/>.
   /// </summary>
   /// <param name="collection">Collection name as passed to the sink.</param>
   /// <param name="ct">Cancellation.</param>
   /// <returns>The engine's own evidence, plus how long the rebuild took.</returns>
   public async Task<string> FinishLoadAsync( string collection, CancellationToken ct )
   {
      var clock = Stopwatch.StartNew();
      string table = TableName( collection );
      Readout readout = await ReadStateAsync( collection, ct );
      if( readout.EvidenceMissing )
      {
         throw new InvalidOperationException( readout.State.Detail );
      }

      string action = "graph already current, no rebuild needed";
      if( !readout.State.Ready )
      {
         using var limit = CancellationTokenSource.CreateLinkedTokenSource( ct );
         limit.CancelAfter( TimeSpan.FromMinutes( _options.IndexWaitMinutes ) );
         try
         {
            await RebuildWithHeartbeatAsync( collection, limit.Token );
         }
         catch( OperationCanceledException ) when( !ct.IsCancellationRequested )
         {
            throw new InvalidOperationException( $"Oracle did not finish rebuilding the HNSW graph of {table} within {_options.IndexWaitMinutes} minutes. Last reading before the rebuild: {readout.State.Detail}" );
         }

         action = $"graph rebuilt in {clock.Elapsed.TotalSeconds:F1} s";
         readout = await WaitUntilReadyAsync( collection, limit.Token, ct );
      }

      return readout.State.Ready
         ? $"{readout.State.Detail}; {action}"
         : throw new InvalidOperationException( $"Oracle did not finish indexing {table} within {_options.IndexWaitMinutes} minutes, or its views still say the graph is not ready after the rebuild. Last reading: {readout.State.Detail}" );
   }

   /// <summary>
   /// Reads the index state once from Oracle's own views and plan, without changing anything.
   /// IndexedVectors is V$VECTOR_INDEX.NUM_VECTORS (what the in-memory graph holds) and
   /// TotalVectors is the table's COUNT(*). When the views cannot be read, Ready is false and
   /// Detail says why.
   /// </summary>
   /// <param name="collection">Collection name as passed to the sink.</param>
   /// <param name="ct">Cancellation.</param>
   /// <returns>The engine's account of its index.</returns>
   public async Task<IndexState> GetIndexStateAsync( string collection, CancellationToken ct )
   {
      Readout readout = await ReadStateAsync( collection, ct );
      return readout.State;
   }

   /// <inheritdoc />
   public async Task DropCollectionAsync( string collection, CancellationToken ct )
   {
      string table = TableName( collection );
      await GatedAsync( async () =>
      {
         await using OracleConnection connection = await OpenAsync( ct );
         try
         {
            await ExecuteAsync( connection, $"DROP TABLE {table} PURGE", ct );
         }
         catch( OracleException ex ) when( ex.Number == NO_SUCH_TABLE )
         {
            // Already gone.
         }
      }, ct );
   }

   #endregion Public Methods

   #region Private Methods

   /// <summary>
   /// What one reading found.
   /// </summary>
   /// <param name="State">The engine's account of its index.</param>
   /// <param name="EvidenceMissing">True when the V$ views could not be read, which waiting cannot fix.</param>
   private sealed record Readout( IndexState State, bool EvidenceMissing );

   /// <summary>
   /// The index's row in V$VECTOR_INDEX plus the changes still waiting in its change log.
   /// </summary>
   /// <param name="Found">False when Oracle lists no such vector index.</param>
   /// <param name="Vectors">Vectors in the in-memory HNSW graph.</param>
   /// <param name="UsedCount">How many queries have used the index.</param>
   /// <param name="PendingInserts">Inserted rows not yet merged into the graph.</param>
   /// <param name="PendingDeletes">Deleted rows not yet removed from the graph.</param>
   private sealed record IndexViews( bool Found, long Vectors, long UsedCount, long PendingInserts, long PendingDeletes );

   /// <summary>
   /// Runs the graph rebuild while a heartbeat reports "still working" every fifteen seconds, so a
   /// long blocking call shows it is moving.
   /// </summary>
   /// <param name="collection">Collection name.</param>
   /// <param name="ct">Cancellation.</param>
   private async Task RebuildWithHeartbeatAsync( string collection, CancellationToken ct )
   {
      using var heartbeat = CancellationTokenSource.CreateLinkedTokenSource( ct );
      Task beat = HeartbeatAsync( $"Oracle {TableName( collection )}: rebuilding the HNSW graph", heartbeat.Token );
      try
      {
         await RefreshIndexAsync( collection, ct );
      }
      finally
      {
         await heartbeat.CancelAsync();
         await beat;
      }
   }

   /// <summary>
   /// Reports progress every fifteen seconds until stopped.
   /// </summary>
   /// <param name="what">What is being waited for.</param>
   /// <param name="stop">Cancelled by the caller when the work ends.</param>
   private async Task HeartbeatAsync( string what, CancellationToken stop )
   {
      var clock = Stopwatch.StartNew();
      try
      {
         while( true )
         {
            await Task.Delay( PROGRESS_INTERVAL, stop );
            Progress?.Invoke( $"{what}, {clock.Elapsed.TotalSeconds:F0} s so far" );
         }
      }
      catch( OperationCanceledException )
      {
         // Cancellation is how the caller ends the heartbeat; nothing failed.
      }
   }

   /// <summary>
   /// Polls the state until it is ready on <see cref="STEADY_READS"/> reads in a row or the
   /// deadline passes.
   /// </summary>
   /// <param name="collection">Collection name.</param>
   /// <param name="deadline">Token that is cancelled when the wait runs out of time.</param>
   /// <param name="ct">The caller's own cancellation, to tell a deadline from a cancel.</param>
   /// <returns>The last reading; the caller checks its Ready flag. Throws at once when the views cannot be read.</returns>
   private async Task<Readout> WaitUntilReadyAsync( string collection, CancellationToken deadline, CancellationToken ct )
   {
      Readout last = new( new IndexState( false, null, null, "no reading taken yet" ), EvidenceMissing: false );
      int steady = 0;
      DateTime nextProgress = DateTime.UtcNow + PROGRESS_INTERVAL;
      try
      {
         while( true )
         {
            last = await ReadStateAsync( collection, deadline );
            if( last.EvidenceMissing )
            {
               throw new InvalidOperationException( last.State.Detail );
            }

            steady = last.State.Ready ? steady + 1 : 0;
            if( steady >= STEADY_READS )
            {
               return last;
            }

            if( DateTime.UtcNow >= nextProgress )
            {
               Progress?.Invoke( $"Oracle {TableName( collection )}: waiting for the index to settle. {last.State.Detail}" );
               nextProgress = DateTime.UtcNow + PROGRESS_INTERVAL;
            }

            await Task.Delay( POLL_INTERVAL, deadline );
         }
      }
      catch( OperationCanceledException ) when( !ct.IsCancellationRequested )
      {
         return last with { State = last.State with { Ready = false } };
      }
   }

   /// <summary>
   /// Reads the four kinds of evidence (graph and change log views, catalog status, row count,
   /// query plan) and decides whether the default search now uses a finished graph.
   /// </summary>
   /// <param name="collection">Collection name.</param>
   /// <param name="ct">Cancellation.</param>
   /// <returns>The reading.</returns>
   private async Task<Readout> ReadStateAsync( string collection, CancellationToken ct )
   {
      string table = TableName( collection );
      string index = IndexName( table ).ToUpperInvariant();
      long stored = await CountAsync( collection, ct );
      IndexViews views;
      try
      {
         views = await ReadViewsAsync( _options.User.ToUpperInvariant(), index, ct );
      }
      catch( OracleException ex )
      {
         string login = string.IsNullOrEmpty( _options.EvidenceUser ) ? _options.User : _options.EvidenceUser;
         return new Readout( new IndexState( false, null, stored,
            $"cannot read the V$VECTOR_* views as {login}: {ex.Message.Split( '\n' )[0]}. Set OracleSinkOptions.EvidenceUser and EvidencePassword, or grant SELECT on V_$VECTOR_INDEX and V_$VECTOR_CHANGE_LOG_PARTITION to the application login" ), EvidenceMissing: true );
      }

      if( !views.Found )
      {
         return new Readout( new IndexState( false, null, stored, $"Oracle lists no vector index {index} in V$VECTOR_INDEX (not created, or its graph is not in memory)" ), EvidenceMissing: false );
      }

      string status = await CatalogStatusAsync( table, index, ct );
      string plan = await PlanAsync( table, ct );
      bool ready = views.Vectors == stored && views.PendingInserts == 0 && views.PendingDeletes == 0 && status == "VALID" && plan.Contains( "HNSW", StringComparison.Ordinal );
      string detail = $"HNSW graph holds {views.Vectors} of {stored} rows, change log waiting: {views.PendingInserts} inserts and {views.PendingDeletes} deletes, "
         + $"USER_INDEXES status {status}, plan of the default search: {plan}, index used by {views.UsedCount} queries so far";
      return new Readout( new IndexState( ready, views.Vectors, stored, detail ), EvidenceMissing: false );
   }

   /// <summary>
   /// Reads V$VECTOR_INDEX and V$VECTOR_CHANGE_LOG_PARTITION for one index with the evidence login.
   /// </summary>
   /// <param name="owner">Index owner, upper case.</param>
   /// <param name="index">Index name, upper case.</param>
   /// <param name="ct">Cancellation.</param>
   /// <returns>The index's counts.</returns>
   private async Task<IndexViews> ReadViewsAsync( string owner, string index, CancellationToken ct )
   {
      await using var connection = new OracleConnection( _evidenceConnectionString );
      await connection.OpenAsync( ct );
      long objn;
      long vectors;
      long used;
      await using( OracleCommand command = NewCommand( connection ) )
      {
         command.BindByName = true;
         command.CommandText = "SELECT index_objn, NVL( num_vectors, 0 ), NVL( index_used_count, 0 ) FROM v$vector_index WHERE owner = :owner AND index_name = :index_name";
         command.Parameters.Add( new OracleParameter( "owner", OracleDbType.Varchar2 ) { Value = owner } );
         command.Parameters.Add( new OracleParameter( "index_name", OracleDbType.Varchar2 ) { Value = index } );
         await using OracleDataReader reader = await command.ExecuteReaderAsync( ct );
         if( !await reader.ReadAsync( ct ) )
         {
            return new IndexViews( false, 0, 0, 0, 0 );
         }

         objn = Convert.ToInt64( reader.GetValue( 0 ) );
         vectors = Convert.ToInt64( reader.GetValue( 1 ) );
         used = Convert.ToInt64( reader.GetValue( 2 ) );
      }

      await using OracleCommand changes = NewCommand( connection );
      changes.BindByName = true;
      changes.CommandText = "SELECT NVL( SUM( NVL( num_inserts, 0 ) ), 0 ), NVL( SUM( NVL( num_deletes, 0 ) ), 0 ) FROM v$vector_change_log_partition WHERE index_objn = :objn";
      changes.Parameters.Add( new OracleParameter( "objn", OracleDbType.Int64 ) { Value = objn } );
      await using OracleDataReader changeReader = await changes.ExecuteReaderAsync( ct );
      await changeReader.ReadAsync( ct );
      return new IndexViews( true, vectors, used, Convert.ToInt64( changeReader.GetValue( 0 ) ), Convert.ToInt64( changeReader.GetValue( 1 ) ) );
   }

   /// <summary>
   /// Reads the catalog status of the vector index with the application login (USER_INDEXES).
   /// </summary>
   /// <param name="table">Table name.</param>
   /// <param name="index">Index name, upper case.</param>
   /// <param name="ct">Cancellation.</param>
   /// <returns>The status, e.g. VALID, or MISSING when the catalog has no such index.</returns>
   private async Task<string> CatalogStatusAsync( string table, string index, CancellationToken ct )
   {
      await using OracleConnection connection = await OpenAsync( ct );
      await using OracleCommand command = NewCommand( connection );
      command.BindByName = true;
      command.CommandText = "SELECT status FROM user_indexes WHERE table_name = :table_name AND index_name = :index_name";
      command.Parameters.Add( new OracleParameter( "table_name", OracleDbType.Varchar2 ) { Value = table.ToUpperInvariant() } );
      command.Parameters.Add( new OracleParameter( "index_name", OracleDbType.Varchar2 ) { Value = index } );
      object? status = await command.ExecuteScalarAsync( ct );
      return status is null or DBNull ? "MISSING" : Convert.ToString( status ) ?? "MISSING";
   }

   /// <summary>
   /// Asks Oracle how it would run the default search (EXPLAIN PLAN, nothing is executed) and
   /// returns the plan's scan and table access operations on one line, e.g. "VECTOR INDEX HNSW SCAN
   /// > TABLE ACCESS BY INDEX ROWID" (a full scan reads "TABLE ACCESS FULL").
   /// </summary>
   /// <param name="table">Table name.</param>
   /// <param name="ct">Cancellation.</param>
   /// <returns>The operations, innermost first, separated by " > ".</returns>
   private async Task<string> PlanAsync( string table, CancellationToken ct )
   {
      await using OracleConnection connection = await OpenAsync( ct );
      int dimension = await ExistingDimensionAsync( connection, table, ct ) ?? throw new InvalidOperationException( $"Oracle table {table} does not exist." );
      string statement = "gvbr" + Guid.NewGuid().ToString( "N" )[..16];
      await using( OracleCommand explain = NewCommand( connection ) )
      {
         explain.BindByName = true;
         explain.CommandText = $"EXPLAIN PLAN SET STATEMENT_ID = '{statement}' FOR SELECT chunk_id, VECTOR_DISTANCE( embedding, :query, COSINE ) AS distance FROM {table} "
            + $"ORDER BY distance FETCH APPROX FIRST 10 ROWS ONLY WITH TARGET ACCURACY PARAMETERS ( EFSEARCH {Math.Max( _options.HnswEfSearch, 10 )} )";
         explain.Parameters.Add( new OracleParameter( "query", OracleDbType.Vector ) { Value = new float[dimension] } );
         await explain.ExecuteNonQueryAsync( ct );
      }

      var operations = new List<string>();
      await using( OracleCommand read = NewCommand( connection ) )
      {
         read.BindByName = true;
         read.CommandText = "SELECT operation || CASE WHEN options IS NULL THEN '' ELSE ' ' || options END FROM plan_table WHERE statement_id = :statement_id ORDER BY id DESC";
         read.Parameters.Add( new OracleParameter( "statement_id", OracleDbType.Varchar2 ) { Value = statement } );
         await using OracleDataReader reader = await read.ExecuteReaderAsync( ct );
         while( await reader.ReadAsync( ct ) )
         {
            operations.Add( reader.GetString( 0 ) );
         }
      }

      await ExecuteAsync( connection, $"DELETE FROM plan_table WHERE statement_id = '{statement}'", ct );
      return string.Join( " > ", operations.Where( o => o.Contains( "SCAN", StringComparison.Ordinal ) || o.Contains( "TABLE ACCESS", StringComparison.Ordinal ) ) );
   }

   /// <summary>
   /// Opens a connection from the driver's pool. Failing to connect throws here, on the first
   /// call that needs the database.
   /// </summary>
   /// <param name="ct">Cancellation.</param>
   /// <returns>An open connection; disposing it returns it to the pool.</returns>
   private async Task<OracleConnection> OpenAsync( CancellationToken ct )
   {
      var connection = new OracleConnection( _connectionString );
      await connection.OpenAsync( ct );
      return connection;
   }

   /// <summary>
   /// Runs one statement that returns no rows.
   /// </summary>
   /// <param name="connection">Open connection.</param>
   /// <param name="sql">The statement.</param>
   /// <param name="ct">Cancellation.</param>
   /// <param name="timeoutSeconds">Longest the statement may run.</param>
   private static async Task ExecuteAsync( OracleConnection connection, string sql, CancellationToken ct, int timeoutSeconds = COMMAND_SECONDS )
   {
      await using OracleCommand command = NewCommand( connection, timeoutSeconds );
      command.CommandText = sql;
      await command.ExecuteNonQueryAsync( ct );
   }

   /// <summary>
   /// Creates a command with a time limit. Why: the driver's default is no limit at all, and a
   /// hung statement would otherwise hold the run for ever.
   /// </summary>
   /// <param name="connection">Open connection.</param>
   /// <param name="timeoutSeconds">Longest the command may run.</param>
   /// <returns>The command; the caller disposes it.</returns>
   private static OracleCommand NewCommand( OracleConnection connection, int timeoutSeconds = COMMAND_SECONDS )
   {
      OracleCommand command = connection.CreateCommand();
      command.CommandTimeout = timeoutSeconds;
      return command;
   }

   /// <summary>
   /// Runs a nearest-neighbour query. The only difference between the default and the exact
   /// search is the FETCH clause, so both go through here. Checked with EXPLAIN PLAN on
   /// 2026-10-03: FETCH APPROX plans as VECTOR INDEX HNSW SCAN and FETCH EXACT as TABLE ACCESS FULL.
   /// </summary>
   /// <param name="collection">Pipeline collection name.</param>
   /// <param name="vector">Query vector.</param>
   /// <param name="top">Hits wanted.</param>
   /// <param name="fetchClause">The FETCH clause after the word FETCH.</param>
   /// <param name="ct">Cancellation.</param>
   /// <returns>Hits, best first, with cosine similarity.</returns>
   private async Task<IReadOnlyList<SearchHit>> QueryAsync( string collection, float[] vector, int top, string fetchClause, CancellationToken ct )
   {
      await using OracleConnection connection = await OpenAsync( ct );
      await using OracleCommand command = NewCommand( connection );
      command.BindByName = true;
      command.InitialLOBFetchSize = -1;
      command.CommandText =
         $"SELECT chunk_id, doc_key, table_name, chunk_text, VECTOR_DISTANCE( embedding, :query, COSINE ) AS distance FROM {TableName( collection )} " +
         $"ORDER BY distance FETCH {fetchClause}";
      command.Parameters.Add( new OracleParameter( "query", OracleDbType.Vector ) { Value = vector } );
      command.Parameters.Add( new OracleParameter( "top", OracleDbType.Int32 ) { Value = top } );
      var hits = new List<SearchHit>( top );
      await using OracleDataReader reader = await command.ExecuteReaderAsync( ct );
      while( await reader.ReadAsync( ct ) )
      {
         hits.Add( new SearchHit( ToGuid( (byte[])reader.GetValue( 0 ) ), Text( reader, 1 ), Text( reader, 2 ), Text( reader, 3 ), 1.0 - reader.GetDouble( 4 ) ) );
      }

      return hits;
   }

   /// <summary>
   /// Writes one batch in a single transaction: delete any rows with these ids, then insert the
   /// new ones.
   /// </summary>
   /// <param name="table">Table name.</param>
   /// <param name="batch">Records to write.</param>
   /// <param name="ct">Cancellation.</param>
   private async Task WriteBatchAsync( string table, VectorRecord[] batch, CancellationToken ct )
   {
      CheckKeyLengths( batch );
      await using OracleConnection connection = await OpenAsync( ct );
      await using OracleTransaction transaction = connection.BeginTransaction();
      await DeleteRowsAsync( connection, table, batch.Select( r => r.Chunk.ChunkId ).ToArray(), ct );
      await using OracleCommand insert = NewCommand( connection );
      insert.BindByName = true;
      insert.ArrayBindCount = batch.Length;
      insert.CommandText =
         $"INSERT INTO {table} ( chunk_id, doc_key, table_name, origin, ordinal, chunk_text, meta, embedding ) " +
         "VALUES ( :id, :doc_key, :table_name, :origin, :ordinal, :chunk_text, :meta, :embedding )";
      insert.Parameters.Add( new OracleParameter( "id", OracleDbType.Raw ) { Value = batch.Select( r => ToBytes( r.Chunk.ChunkId ) ).ToArray() } );
      insert.Parameters.Add( new OracleParameter( "doc_key", OracleDbType.Varchar2 ) { Value = batch.Select( r => r.Document.DocKey ).ToArray() } );
      insert.Parameters.Add( new OracleParameter( "table_name", OracleDbType.Varchar2 ) { Value = batch.Select( r => r.Document.Table ).ToArray() } );
      insert.Parameters.Add( new OracleParameter( "origin", OracleDbType.Varchar2 ) { Value = batch.Select( r => r.Document.Origin ).ToArray() } );
      insert.Parameters.Add( new OracleParameter( "ordinal", OracleDbType.Int32 ) { Value = batch.Select( r => r.Chunk.Ordinal ).ToArray() } );
      insert.Parameters.Add( new OracleParameter( "chunk_text", TextBindType( batch ) ) { Value = batch.Select( r => r.Chunk.Text ).ToArray() } );
      insert.Parameters.Add( new OracleParameter( "meta", OracleDbType.Varchar2 ) { Value = batch.Select( r => JsonSerializer.Serialize( r.Document.Metadata ) ).ToArray() } );
      insert.Parameters.Add( new OracleParameter( "embedding", OracleDbType.Vector ) { Value = batch.Select( r => r.Vector ).ToArray() } );
      await insert.ExecuteNonQueryAsync( ct );
      await transaction.CommitAsync( ct );
   }

   /// <summary>
   /// Picks the bind type for the chunk texts of one batch. A VARCHAR2 bind inserts into the CLOB
   /// column about eight times faster than a CLOB bind (measured 2026-10-03 on this box: 500 rows
   /// of 1024-dimension vectors in 73 ms against 590 to 790 ms), because a CLOB bind creates a
   /// temporary LOB for every row. It works for texts up to about 32,000 bytes, so a batch with a
   /// larger text falls back to the CLOB bind for the whole batch.
   /// </summary>
   /// <param name="batch">The batch.</param>
   /// <returns>The Oracle type to bind the texts as.</returns>
   private static OracleDbType TextBindType( VectorRecord[] batch )
   {
      return batch.All( r => Encoding.UTF8.GetByteCount( r.Chunk.Text ) <= MAX_INLINE_TEXT_BYTES ) ? OracleDbType.Varchar2 : OracleDbType.Clob;
   }

   /// <summary>
   /// Refuses a key, table or origin value that does not fit its VARCHAR2(4000) column, with a
   /// message that says which one, instead of Oracle's bare ORA-12899.
   /// </summary>
   /// <param name="batch">The batch.</param>
   private static void CheckKeyLengths( VectorRecord[] batch )
   {
      foreach( VectorRecord r in batch )
      {
         foreach( ( string field, string value ) in new[] { ( "doc_key", r.Document.DocKey ), ( "table", r.Document.Table ), ( "origin", r.Document.Origin ) } )
         {
            if( Encoding.UTF8.GetByteCount( value ) > MAX_KEY_BYTES )
            {
               throw new InvalidOperationException( $"The {field} of document '{r.Document.DocKey[..Math.Min( 60, r.Document.DocKey.Length )]}...' is longer than the {MAX_KEY_BYTES} bytes an Oracle VARCHAR2 column holds." );
            }
         }
      }
   }

   /// <summary>
   /// Deletes one batch of chunks in its own transaction.
   /// </summary>
   /// <param name="table">Table name.</param>
   /// <param name="ids">Chunk ids.</param>
   /// <param name="ct">Cancellation.</param>
   private async Task DeleteBatchAsync( string table, Guid[] ids, CancellationToken ct )
   {
      await using OracleConnection connection = await OpenAsync( ct );
      await using OracleTransaction transaction = connection.BeginTransaction();
      await DeleteRowsAsync( connection, table, ids, ct );
      await transaction.CommitAsync( ct );
   }

   /// <summary>
   /// Runs work while holding the process-wide write gate, so only one write or DDL statement
   /// is in flight in this process at a time (see the class remarks for why).
   /// </summary>
   /// <param name="work">The work.</param>
   /// <param name="ct">Cancellation.</param>
   private static async Task GatedAsync( Func<Task> work, CancellationToken ct )
   {
      await WRITE_GATE.WaitAsync( ct );
      try
      {
         await work();
      }
      finally
      {
         WRITE_GATE.Release();
      }
   }

   /// <summary>
   /// Deletes rows by chunk id with one array-bound statement. Missing ids are ignored.
   /// </summary>
   /// <param name="connection">Open connection (inside the caller's transaction).</param>
   /// <param name="table">Table name.</param>
   /// <param name="ids">Chunk ids.</param>
   /// <param name="ct">Cancellation.</param>
   private static async Task DeleteRowsAsync( OracleConnection connection, string table, Guid[] ids, CancellationToken ct )
   {
      await using OracleCommand delete = NewCommand( connection );
      delete.BindByName = true;
      delete.ArrayBindCount = ids.Length;
      delete.CommandText = $"DELETE FROM {table} WHERE chunk_id = :id";
      delete.Parameters.Add( new OracleParameter( "id", OracleDbType.Raw ) { Value = ids.Select( ToBytes ).ToArray() } );
      await delete.ExecuteNonQueryAsync( ct );
   }

   /// <summary>
   /// Reads the vector dimension of a table, or null when the table does not exist. Oracle
   /// reports a vector column's shape as text such as "VECTOR(1024,FLOAT32,DENSE)".
   /// </summary>
   /// <param name="connection">Open connection.</param>
   /// <param name="table">Table name.</param>
   /// <param name="ct">Cancellation.</param>
   /// <returns>The dimension, or null.</returns>
   private static async Task<int?> ExistingDimensionAsync( OracleConnection connection, string table, CancellationToken ct )
   {
      await using OracleCommand command = NewCommand( connection );
      command.BindByName = true;
      command.CommandText = "SELECT column_name, vector_info FROM user_tab_columns WHERE table_name = :table_name";
      command.Parameters.Add( new OracleParameter( "table_name", OracleDbType.Varchar2 ) { Value = table.ToUpperInvariant() } );
      bool tableFound = false;
      await using OracleDataReader reader = await command.ExecuteReaderAsync( ct );
      while( await reader.ReadAsync( ct ) )
      {
         tableFound = true;
         Match match = DIMENSION_PATTERN.Match( reader.IsDBNull( 1 ) ? string.Empty : reader.GetString( 1 ) );
         if( reader.GetString( 0 ) == "EMBEDDING" && match.Success )
         {
            return int.Parse( match.Groups[1].Value );
         }
      }

      return tableFound
         ? throw new InvalidOperationException( $"Oracle table {table} exists but has no vector column. Drop it or use a new name." )
         : null;
   }

   /// <summary>
   /// Creates the HNSW vector index when the table does not have it yet, so a table left behind
   /// by an interrupted Ensure is repaired on the next one.
   /// </summary>
   /// <param name="connection">Open connection.</param>
   /// <param name="table">Table name.</param>
   /// <param name="ct">Cancellation.</param>
   private async Task EnsureIndexAsync( OracleConnection connection, string table, CancellationToken ct )
   {
      string index = IndexName( table );
      await using( OracleCommand check = NewCommand( connection ) )
      {
         check.BindByName = true;
         check.CommandText = "SELECT COUNT(*) FROM user_indexes WHERE index_name = :index_name";
         check.Parameters.Add( new OracleParameter( "index_name", OracleDbType.Varchar2 ) { Value = index.ToUpperInvariant() } );
         if( Convert.ToInt32( await check.ExecuteScalarAsync( ct ) ) > 0 )
         {
            return;
         }
      }

      try
      {
         await WithRetryAsync( () => GatedAsync( () => ExecuteAsync( connection, CreateIndexSql( table, index ), ct ), ct ), ct );
      }
      catch( OracleException ex )
      {
         throw new InvalidOperationException(
            $"Oracle could not create the HNSW vector index on {table}. HNSW indexes live in the database's vector memory pool (vector_memory_size), which deploy/engines/oracle.compose.yaml sets up; a missing or full pool is the usual cause. Oracle said: {ex.Message}", ex );
      }
   }

   /// <summary>
   /// The CREATE TABLE statement for one pipeline.
   /// </summary>
   /// <param name="table">Table name.</param>
   /// <param name="dimension">Vector length.</param>
   /// <returns>The statement.</returns>
   private static string CreateTableSql( string table, int dimension )
   {
      return $"CREATE TABLE {table} ( chunk_id RAW(16) NOT NULL, doc_key VARCHAR2(4000), table_name VARCHAR2(4000), origin VARCHAR2(4000), " +
         $"ordinal NUMBER(10) NOT NULL, chunk_text CLOB, meta JSON, embedding VECTOR({dimension}, FLOAT32) NOT NULL, CONSTRAINT pk_{table} PRIMARY KEY ( chunk_id ) )";
   }

   /// <summary>
   /// The CREATE VECTOR INDEX statement for one pipeline: in-memory HNSW over the embedding
   /// column with cosine distance.
   /// </summary>
   /// <param name="table">Table name.</param>
   /// <param name="index">Index name.</param>
   /// <returns>The statement.</returns>
   private string CreateIndexSql( string table, string index )
   {
      return $"CREATE VECTOR INDEX {index} ON {table} ( embedding ) ORGANIZATION INMEMORY NEIGHBOR GRAPH DISTANCE COSINE " +
         $"PARAMETERS ( TYPE HNSW, NEIGHBORS {_options.HnswNeighbors}, EFCONSTRUCTION {_options.HnswEfConstruction} )";
   }

   /// <summary>
   /// True when Oracle gave up on a lock fight with another session: ORA-00054 (lock held) or
   /// ORA-00060 (deadlock), which index creation reports wrapped in ORA-51906.
   /// </summary>
   /// <param name="ex">The Oracle error.</param>
   /// <returns>Whether the failure is a lock conflict.</returns>
   private static bool IsLockConflict( OracleException ex )
   {
      return LOCK_CONFLICT_CODES.Any( code => ex.Message.Contains( code, StringComparison.Ordinal ) );
   }

   /// <summary>
   /// Retries a call on failures Oracle marks recoverable (dropped connection, listener hiccup)
   /// and on lock conflicts with another session, with a short backoff. The calls passed in are
   /// transactions or statements that are safe to repeat.
   /// </summary>
   /// <param name="call">The call.</param>
   /// <param name="ct">Cancellation.</param>
   private static async Task WithRetryAsync( Func<Task> call, CancellationToken ct )
   {
      for( int attempt = 1; ; attempt++ )
      {
         try
         {
            await call();
            return;
         }
         catch( OracleException ex ) when( attempt < MAX_ATTEMPTS && ( ex.IsRecoverable || IsLockConflict( ex ) ) )
         {
            await Task.Delay( TimeSpan.FromSeconds( attempt * 2 ), ct );
         }
      }
   }

   /// <summary>
   /// Table name of a pipeline. Pipeline names are sanitized to [a-z0-9_], which is checked here
   /// again because the name goes into SQL text. A name too long for comfortable Oracle
   /// identifiers is cut and given a hash of the full name so two long names cannot collide.
   /// </summary>
   /// <param name="collection">Pipeline collection name.</param>
   /// <returns>The table name.</returns>
   private static string TableName( string collection )
   {
      if( !SAFE_NAME.IsMatch( collection ) )
      {
         throw new ArgumentException( $"'{collection}' is not a valid pipeline name; only a-z, 0-9 and underscore are allowed.", nameof( collection ) );
      }

      string name = TABLE_PREFIX + collection;
      if( name.Length <= MAX_TABLE_NAME )
      {
         return name;
      }

      string hash = Convert.ToHexString( SHA256.HashData( Encoding.UTF8.GetBytes( name ) ) )[..8].ToLowerInvariant();
      return name[..( MAX_TABLE_NAME - 9 )] + "_" + hash;
   }

   /// <summary>
   /// Name of a table's vector index.
   /// </summary>
   /// <param name="table">Table name.</param>
   /// <returns>The index name.</returns>
   private static string IndexName( string table )
   {
      return table + "_vidx";
   }

   /// <summary>
   /// Chunk id as the 16 bytes stored in the RAW(16) column. Big-endian order makes the stored
   /// hex read the same as the Guid's "N" format.
   /// </summary>
   /// <param name="id">The id.</param>
   /// <returns>The bytes.</returns>
   private static byte[] ToBytes( Guid id )
   {
      return id.ToByteArray( bigEndian: true );
   }

   /// <summary>
   /// The Guid stored in a RAW(16) value.
   /// </summary>
   /// <param name="bytes">The 16 bytes.</param>
   /// <returns>The id.</returns>
   private static Guid ToGuid( byte[] bytes )
   {
      return new Guid( bytes, bigEndian: true );
   }

   /// <summary>
   /// Reads a text column, turning NULL (how Oracle stores an empty string) back into "".
   /// </summary>
   /// <param name="reader">Reader positioned on a row.</param>
   /// <param name="ordinal">Column number.</param>
   /// <returns>The text.</returns>
   private static string Text( OracleDataReader reader, int ordinal )
   {
      return reader.IsDBNull( ordinal ) ? string.Empty : reader.GetString( ordinal );
   }

   #endregion Private Methods
}
