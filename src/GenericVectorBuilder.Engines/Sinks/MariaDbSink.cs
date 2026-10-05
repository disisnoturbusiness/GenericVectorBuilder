using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.RegularExpressions;
using GenericVectorBuilder.Core.Contracts;
using GenericVectorBuilder.Engines.Common;
using MySqlConnector;

namespace GenericVectorBuilder.Engines.Sinks;

/// <summary>
/// Writes vectors to MariaDB 11.8 over the MySQL protocol (MySqlConnector): one InnoDB table per
/// pipeline named gvb.gvb_{pipeline}, keyed by chunk id (BINARY(16)), with the payload
/// in plain columns (doc_key, table, origin, ordinal, text, meta as a JSON object, written without
/// the HTML-safe escaping so a quote or accent stays readable in a SQL client) and the vector
/// in a VECTOR(n) column that holds raw float32 values.
/// Default search: a VECTOR INDEX (MariaDB's HNSW variant, DISTANCE=cosine, M 16) queried with
/// ORDER BY VEC_DISTANCE_COSINE(embedding, query) LIMIT n, with the beam width
/// (mhnsw_ef_search, 100 unless set) taken from <see cref="MariaDbSinkOptions.HnswEfSearch"/>.
/// Why the key is BINARY(16) and not MariaDB's UUID type: UUID rejects any value whose version
/// and variant bits are not valid ("Incorrect uuid value"), and chunk ids are derived from hashes,
/// so many are not valid UUIDs.
/// Why there is no ef_construction here: MariaDB exposes only M and DISTANCE when the index is
/// created. The build-time candidate list is fixed inside the server (SHOW VARIABLES LIKE
/// 'mhnsw%' lists default distance, default M, ef_search and the cache size, nothing else), so
/// the "M 16, ef_construction 128" recipe the other engines use can only be matched on M.
/// Measured 2026-10-03: running the INSERTs with mhnsw_ef_search set to 128 or 512 (20,000
/// clustered vectors of 1024 dimensions) loaded in 129 s each against 131 s with the default,
/// and recall stayed within build-to-build noise, so the search setting does not reach the build.
/// Why mhnsw_ef_search is 100: the benchmark compares engines at the same search effort, and the
/// other HNSW sinks in this tree (pgvector, Milvus, Chroma, DuckDB, Typesense, Redis, OpenSearch,
/// Oracle, Vespa) all default to 100, so MariaDB gets 100 too. MariaDB's own default is 20. The sink
/// used 3200 until 2026-10-04; that made MariaDB walk 32 times the candidates of the others and its
/// latency and queries per second were not comparable. The price of matching the effort is recall,
/// which falls off with size. Measured 2026-10-04 on this box, 1024-dimension random unit vectors
/// (the hardest input for a graph), M 16, 50 queries, recall@10 against an in-memory brute force:
///   vectors   ef 20                   ef 100                  ef 3200
///   524       0.886, 0.908, 0.892     0.986, 0.996, 0.994     0.986, 0.996, 0.994
///   2,000     0.512, 0.492, 0.522     0.914, 0.888, 0.924     0.992, 0.992, 0.998
/// (three separate loads each; the graph is not built the same way twice, so expect about 0.03 of
/// movement). The p50 was 1.5 to 2.1 ms at ef 100 on both sizes.
/// At 100,000 vectors (older measurement, 20 queries, shared box so milliseconds are rough) ef 100
/// found 0.52 of the true neighbours of clustered data and 0.09 of uniform random data.
/// A run that wants equal recall instead of equal effort must raise
/// <see cref="MariaDbSinkOptions.HnswEfSearch"/>, and the report prints the value used (the index
/// description, and the readiness detail, which reads the applied value back from the server).
/// The curve behind that, measured 2026-10-03 (shared box, so milliseconds are rough), 100,000
/// vectors of 1024 dimensions, M 16, 20 queries, recall@10 against an in-memory brute force.
/// Clustered means 500 centres, each point its centre plus noise (0.7 of a random unit vector),
/// normalized; neighbours inside a cluster are close to ties, so this is a hard set. Uniform
/// random is the worst case:
///   mhnsw_ef_search   clustered recall / ms p50   uniform random recall / ms p50
///   20                0.48 / 2.1                  0.02 / 2.7
///   100               0.52 / 1.4                  0.09 / 4.7
///   200               0.61 / 1.5                  0.16 / 14
///   400               0.61 / 3.5                  0.26 / 11
///   800               0.69 / 4.7                  0.39 / 30
///   1600              0.78 / 11                   0.60 / 39
///   3200              0.83 / 16                   0.81 / 62
///   6400              0.84 / 21                   0.85 / 59
/// Latency rises slowly and recall keeps climbing to about 3200, then flattens (10000, the
/// server's maximum, gave 0.87 and 0.90 at 40 to 70 ms). The exact scan took about 400 ms on both.
/// The contract and scale tests use uniform random vectors, so their recall is a floor.
/// Load speed: 100,000 vectors of 1024 dimensions took 486 s (about 200 rows per second, one core)
/// because every row also updates the graph. Two tables loaded at the same time took 577 s and
/// 785 s each (the box was also busy with other containers), so loading tables in parallel
/// raises the total rate but not the rate of any one table.
/// Why mhnsw_ef_search is set per statement (SET STATEMENT ... FOR SELECT): it is a session
/// variable, and pooled connections are reset between uses, so a plain SET would silently
/// revert. SET STATEMENT also leaves the server's own setting alone.
/// Exact search: the same query with IGNORE INDEX on the vector index, so the optimizer reads
/// every row, computes every distance and sorts (EXPLAIN shows type ALL with filesort).
/// Why the vector goes over the wire as raw bytes: a VECTOR column stores little-endian float32,
/// so the bytes of the float array are the value. Text through VEC_FromText would format and
/// then parse every dimension.
/// Why upserts use INSERT ... ON DUPLICATE KEY UPDATE: it is one statement, needs no existence
/// check, and a re-sent batch overwrites rather than duplicates. Rows are written in batches of
/// <see cref="MariaDbSinkOptions.UpsertBatch"/>.
/// Why the server needs a big mhnsw_max_cache_size (4G in the compose file): the graph is read
/// into a cache, and the 16M default holds only a few thousand 1024-dimension vectors, so a
/// larger index is re-read from disk on every query.
/// Readiness: the vector index is updated inside each INSERT's own transaction, so nothing is
/// built after the writes return and <see cref="FinishLoadAsync"/> only confirms that
/// (<see cref="GetIndexStateAsync"/>). The proof is the engine's own account: information_schema
/// lists the VECTOR index, EXPLAIN of the default search names it as the key, and a search that
/// asks for 1,000 rows must get nearly all of them back through the graph. Why that search: on a
/// freshly loaded 2,000-vector table the first search for 1,000 rows returned 481 to 519 rows on
/// all 13 loads measured, and for 600 to 1,001 rows 489 to 514 (2 loads per size); the same
/// search run again returned every row, a pause of 5 seconds before the first search changed
/// nothing, and searches for 10, 100, 300 or 2,000 rows (first on a load) did not show it. So the
/// first poll of the readiness check fails and the second passes, and finishing takes about
/// 0.6 s, almost all of it the pause between the two polls. A check that only asked for the
/// default top 10 would never see this. Why the walk names the index (FORCE INDEX) and the default
/// search's own EXPLAIN does not: measured 2026-10-04 on a freshly loaded 524-vector table, the
/// optimizer chose a full scan for a LIMIT of 524 (the whole table) until InnoDB refreshed its row
/// statistics, which took 17.5 s over 7 polls on both the old and the new search effort, so the
/// finish step reported 17.5 s for an engine that builds nothing. The walk only asks whether the
/// graph returns the rows, so it forces the index; the EXPLAIN of the LIMIT 10 search stays
/// unforced and must still name the index by itself. Why there is no exact indexed count: MariaDB keeps the
/// graph in a hidden InnoDB table (name#i#01) that information_schema does not list and that
/// SELECT refuses ("doesn't exist"), so the engine offers no per-index row count to read. Why the
/// walk is held to 90 percent and not 100: it asks an approximate graph search for rows, which is
/// allowed to stop short of a perfect answer.
/// </summary>
public sealed class MariaDbSink : ISink, IExactSearchSink, IEngineDescription, IIndexFinisher, IDisposable
{
   #region Data Members

   private const int READY_WAIT_SECONDS = 120;
   private const int PROBE_TIMEOUT_SECONDS = 120;
   private const int WALK_ROWS = 1000;
   private const double WALK_MIN_FRACTION = 0.9;
   private const int DELETE_BATCH = 1000;
   private const int MAX_ATTEMPTS = 3;
   private const string INDEX_NAME = "vec_idx";
   private static readonly JsonSerializerOptions META_JSON = new() { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };
   private static readonly Regex DIMENSION_PATTERN = new( @"^vector\((\d+)\)$", RegexOptions.Compiled | RegexOptions.CultureInvariant | RegexOptions.IgnoreCase );

   private readonly MariaDbSinkOptions _options;
   private readonly MySqlDataSource _dataSource;
   private bool _databaseReady;

   #endregion Data Members

   #region Constructor

   /// <summary>
   /// Creates a sink for the local MariaDB container with the password from the secrets file.
   /// Opens no connection, so the engine catalog can create it just to read its name.
   /// </summary>
   public MariaDbSink() : this( MariaDbSinkOptions.LocalDefaults() )
   {
   }

   /// <summary>
   /// Creates a sink with explicit settings.
   /// </summary>
   /// <param name="options">Connection and index settings.</param>
   /// <exception cref="ArgumentOutOfRangeException">The search effort is outside the 1 to 10,000 the server accepts.</exception>
   public MariaDbSink( MariaDbSinkOptions options )
   {
      if( options.HnswEfSearch < 1 || options.HnswEfSearch > MariaDbSinkOptions.MAX_EF_SEARCH )
      {
         throw new ArgumentOutOfRangeException( nameof( options ), options.HnswEfSearch,
            $"mhnsw_ef_search must be 1 to {MariaDbSinkOptions.MAX_EF_SEARCH} (the range the server accepts); got {options.HnswEfSearch}." );
      }

      _options = options;
      var builder = new MySqlConnectionStringBuilder
      {
         Server = options.Host,
         Port = (uint)options.Port,
         UserID = options.User,
         Password = options.Password ?? string.Empty,
         SslMode = MySqlSslMode.None,
         ConnectionTimeout = 15,
         DefaultCommandTimeout = 3600,
         MaximumPoolSize = 20,
         GuidFormat = MySqlGuidFormat.None
      };
      _dataSource = new MySqlDataSourceBuilder( builder.ConnectionString ).Build();
   }

   #endregion Constructor

   #region Public Methods

   /// <inheritdoc />
   public string Name => "mariadb";

   /// <inheritdoc />
   public string Engine => "MariaDB 11.8.9 (InnoDB + VECTOR INDEX)";

   /// <inheritdoc />
   public string IndexDescription =>
      $"VECTOR INDEX (HNSW variant) DISTANCE=cosine, M={_options.HnswM} (no ef_construction setting exists), mhnsw_ef_search={_options.HnswEfSearch} "
      + $"per statement ({( _options.HnswEfSearch == MariaDbSinkOptions.DEFAULT_EF_SEARCH ? "the ef 100 most engines here use, so the search effort matches; " : string.Empty )}"
      + "MariaDB's own default is 20; recall@10 at ef 100 falls as the set grows (random 1024-dimension vectors, measured 2026-10-04: 0.99 at 524, 0.89 to 0.92 at 2,000; "
      + "an earlier run gave about 0.09 at 100,000)), "
      + "mhnsw_max_cache_size 4G; exact mode = IGNORE INDEX full scan";

   /// <inheritdoc />
   public string ComposeFile => "mariadb.compose.yaml";

   /// <inheritdoc />
   public string Durability =>
      "innodb_flush_log_at_trx_commit=2 (set in mariadb-bench.compose.yaml for the benchmark's own container gvbbench-mariadb, and in mariadb.compose.yaml for the daily gvb-mariadb, "
      + "which has the same server settings): the InnoDB redo log is written to the operating system at every commit but fsynced about once a second, "
      + "so a crash of the mariadbd process loses nothing, while an operating-system crash or power cut can lose the last second of commits; innodb_doublewrite is on (default), "
      + "the binary log is off, and the vector graph is an InnoDB table under the same log (settings read from the running server with SHOW VARIABLES; the crash behaviour is "
      + "InnoDB's documented behaviour for this setting and was not tested on either container)";

   /// <inheritdoc />
   public async Task<string> FinishLoadAsync( string collection, CancellationToken ct )
   {
      var clock = Stopwatch.StartNew();
      ( IndexState state, int polls, string firstProblem ) = await WaitUntilReadyAsync( collection, ct );
      string earlier = polls > 1 ? $" (poll 1 of {polls} said: {firstProblem})" : string.Empty;
      return $"VECTOR INDEX is maintained inside every INSERT, nothing to build; confirmed after {polls} poll(s) in {clock.Elapsed.TotalSeconds:F1} s{earlier}: {state.Detail}";
   }

   /// <inheritdoc />
   public async Task<IndexState> GetIndexStateAsync( string collection, CancellationToken ct )
   {
      await using MySqlConnection connection = await _dataSource.OpenConnectionAsync( ct );
      long total = Convert.ToInt64( await ProbeScalarAsync( connection, $"SELECT COUNT(*) FROM {Table( collection )}", ct ), CultureInfo.InvariantCulture );
      object? type = await ProbeScalarAsync( connection,
         "SELECT INDEX_TYPE FROM information_schema.STATISTICS WHERE TABLE_SCHEMA = @db AND TABLE_NAME = @tbl AND INDEX_NAME = @idx", ct,
         ( "@db", _options.Database ), ( "@tbl", TableName( collection ) ), ( "@idx", INDEX_NAME ) );
      if( type is not string indexType || !string.Equals( indexType, "VECTOR", StringComparison.OrdinalIgnoreCase ) )
      {
         return new IndexState( false, null, total, $"not ready: information_schema.STATISTICS has no VECTOR index {INDEX_NAME} on {TableName( collection )}" );
      }

      if( total == 0 )
      {
         return new IndexState( true, 0, 0, $"information_schema lists VECTOR index {INDEX_NAME}; no vectors stored yet" );
      }

      byte[] probe = (byte[])( await ProbeScalarAsync( connection, $"SELECT embedding FROM {Table( collection )} LIMIT 1", ct ) )!;
      int applied = Convert.ToInt32( await ProbeScalarAsync( connection, $"SET STATEMENT mhnsw_ef_search = {_options.HnswEfSearch} FOR SELECT @@mhnsw_ef_search", ct ), CultureInfo.InvariantCulture );
      string defaultKey = await ExplainKeyAsync( connection, $"SET STATEMENT mhnsw_ef_search = {_options.HnswEfSearch} FOR EXPLAIN " + SearchSql( collection, 10, string.Empty ), probe, ct );
      int wanted = (int)Math.Min( total, WALK_ROWS );
      string walkSql = $"SELECT chunk_id FROM {Table( collection )} FORCE INDEX ( {INDEX_NAME} ) ORDER BY VEC_DISTANCE_COSINE( embedding, @q ) LIMIT {wanted}";
      string walkKey = await ExplainKeyAsync( connection, $"SET STATEMENT mhnsw_ef_search = {_options.HnswEfSearch} FOR EXPLAIN " + walkSql, probe, ct );
      int walked = defaultKey == INDEX_NAME && walkKey == INDEX_NAME ? await WalkAsync( connection, walkSql, probe, ct ) : 0;
      var problems = new List<string>();
      if( defaultKey != INDEX_NAME )
      {
         problems.Add( $"EXPLAIN of the default search names key '{defaultKey}', not {INDEX_NAME}" );
      }

      if( applied != _options.HnswEfSearch )
      {
         problems.Add( $"the sink asked for mhnsw_ef_search {_options.HnswEfSearch} but the server applied {applied}" );
      }

      if( walked < Math.Ceiling( wanted * WALK_MIN_FRACTION ) )
      {
         problems.Add( $"a search at mhnsw_ef_search {_options.HnswEfSearch} returned only {walked} of {wanted} requested rows through the index (EXPLAIN key '{walkKey}')" );
      }

      string facts = $"information_schema lists {INDEX_NAME} as INDEX_TYPE VECTOR, EXPLAIN of the default search uses key {defaultKey}, the server applied mhnsw_ef_search {applied} "
         + $"(asked for {_options.HnswEfSearch}, read back from the server), a search at that effort "
         + $"returned {walked} of {wanted} requested rows through the index; MariaDB has no per-index row count (the graph is a hidden InnoDB table), so indexed vectors are not reported";
      return new IndexState( problems.Count == 0, null, total, problems.Count == 0 ? facts : $"not ready: {string.Join( "; ", problems )} ({facts})" );
   }

   /// <inheritdoc />
   public async Task<bool> EnsureCollectionAsync( string collection, int dimension, CancellationToken ct )
   {
      await EnsureDatabaseAsync( ct );
      object? type = await ScalarAsync(
         "SELECT COLUMN_TYPE FROM information_schema.COLUMNS WHERE TABLE_SCHEMA = @db AND TABLE_NAME = @tbl AND COLUMN_NAME = 'embedding'",
         ct, ( "@db", _options.Database ), ( "@tbl", TableName( collection ) ) );
      if( type is string definition )
      {
         Match match = DIMENSION_PATTERN.Match( definition );
         int existing = match.Success ? int.Parse( match.Groups[1].Value, CultureInfo.InvariantCulture ) : 0;
         if( existing != dimension )
         {
            throw new InvalidOperationException( $"MariaDB table {TableName( collection )} holds {existing}-dim vectors but the embedder produces {dimension}. Reset the pipeline or use a new name." );
         }

         return false;
      }

      await ExecuteAsync( CreateTableSql( collection, dimension ), ct );
      return true;
   }

   /// <inheritdoc />
   public async Task UpsertAsync( string collection, IReadOnlyList<VectorRecord> records, CancellationToken ct )
   {
      string table = Table( collection );
      IEnumerable<VectorRecord> latest = records.Reverse().DistinctBy( r => r.Chunk.ChunkId ).Reverse();
      foreach( VectorRecord[] batch in latest.Chunk( _options.UpsertBatch ) )
      {
         await WithRetryAsync( () => WriteBatchAsync( table, batch, ct ), ct );
      }
   }

   /// <inheritdoc />
   public async Task DeleteAsync( string collection, IReadOnlyList<Guid> chunkIds, CancellationToken ct )
   {
      string table = Table( collection );
      foreach( Guid[] batch in chunkIds.Chunk( DELETE_BATCH ) )
      {
         string ids = string.Join( ",", batch.Select( id => $"X'{Convert.ToHexString( IdBytes( id ) )}'" ) );
         await WithRetryAsync( () => ExecuteAsync( $"DELETE FROM {table} WHERE chunk_id IN ({ids})", ct ), ct );
      }
   }

   /// <inheritdoc />
   public async Task<long> CountAsync( string collection, CancellationToken ct )
   {
      object? count = await ScalarAsync( $"SELECT COUNT(*) FROM {Table( collection )}", ct );
      return Convert.ToInt64( count, CultureInfo.InvariantCulture );
   }

   /// <inheritdoc />
   public Task<IReadOnlyList<SearchHit>> SearchAsync( string collection, float[] vector, int top, CancellationToken ct )
   {
      string select = SearchSql( collection, top, string.Empty );
      return RunSearchAsync( $"SET STATEMENT mhnsw_ef_search = {_options.HnswEfSearch} FOR {select}", vector, ct );
   }

   /// <inheritdoc />
   public Task<IReadOnlyList<SearchHit>> SearchExactAsync( string collection, float[] vector, int top, CancellationToken ct )
   {
      return RunSearchAsync( SearchSql( collection, top, $"IGNORE INDEX ( {INDEX_NAME} )" ), vector, ct );
   }

   /// <inheritdoc />
   public Task DropCollectionAsync( string collection, CancellationToken ct )
   {
      return ExecuteAsync( $"DROP TABLE IF EXISTS {Table( collection )}", ct );
   }

   #endregion Public Methods

   #region Private Methods

   /// <summary>
   /// Polls <see cref="GetIndexStateAsync"/> until the engine says the index is ready, with a
   /// deadline. A synchronous index is ready at once, so the loop only matters if something
   /// else (an ALTER TABLE, a rebuild) is still running.
   /// </summary>
   /// <param name="collection">Collection name.</param>
   /// <param name="ct">Cancellation.</param>
   /// <returns>The ready state, how many polls it took, and what the first poll said when it was not ready (empty otherwise).</returns>
   /// <exception cref="InvalidOperationException">The index was not ready before the deadline.</exception>
   private async Task<( IndexState State, int Polls, string FirstProblem )> WaitUntilReadyAsync( string collection, CancellationToken ct )
   {
      var clock = Stopwatch.StartNew();
      int pauseMs = 500;
      int polls = 0;
      string firstProblem = string.Empty;
      while( true )
      {
         IndexState state = await GetIndexStateAsync( collection, ct );
         polls++;
         if( state.Ready )
         {
            return ( state, polls, firstProblem );
         }

         if( polls == 1 )
         {
            firstProblem = state.Detail;
         }

         if( clock.Elapsed.TotalSeconds > READY_WAIT_SECONDS )
         {
            throw new InvalidOperationException( $"The MariaDB vector index for {collection} was not ready after {READY_WAIT_SECONDS} seconds: {state.Detail}" );
         }

         await Task.Delay( pauseMs, ct );
         pauseMs = Math.Min( pauseMs * 2, 5000 );
      }
   }

   /// <summary>
   /// Runs one readiness query with its own short timeout (the connection default is an hour, which
   /// suits a bulk load but not a status check) and returns its first value.
   /// </summary>
   /// <param name="connection">Open connection.</param>
   /// <param name="sql">The statement.</param>
   /// <param name="ct">Cancellation.</param>
   /// <param name="parameters">Named parameters.</param>
   /// <returns>The first column of the first row, or null when there is none.</returns>
   private static async Task<object?> ProbeScalarAsync( MySqlConnection connection, string sql, CancellationToken ct, params (string Name, object Value)[] parameters )
   {
      await using MySqlCommand command = new( sql, connection ) { CommandTimeout = PROBE_TIMEOUT_SECONDS };
      foreach( ( string name, object value ) in parameters )
      {
         command.Parameters.AddWithValue( name, value );
      }

      return await command.ExecuteScalarAsync( ct );
   }

   /// <summary>
   /// Runs EXPLAIN for a nearest-neighbour statement and returns the index the optimizer chose.
   /// </summary>
   /// <param name="connection">Open connection.</param>
   /// <param name="explainSql">The EXPLAIN statement (possibly inside SET STATEMENT), with parameter @q.</param>
   /// <param name="probe">Query vector bytes.</param>
   /// <param name="ct">Cancellation.</param>
   /// <returns>The key column of the plan row, or "none" when no index is used.</returns>
   private static async Task<string> ExplainKeyAsync( MySqlConnection connection, string explainSql, byte[] probe, CancellationToken ct )
   {
      await using MySqlCommand command = new( explainSql, connection ) { CommandTimeout = PROBE_TIMEOUT_SECONDS };
      command.Parameters.AddWithValue( "@q", probe );
      await using MySqlDataReader reader = await command.ExecuteReaderAsync( ct );
      int key = reader.GetOrdinal( "key" );
      while( await reader.ReadAsync( ct ) )
      {
         if( !reader.IsDBNull( key ) )
         {
            return reader.GetString( key );
         }
      }

      return "none";
   }

   /// <summary>
   /// Runs the index-driven search and counts the rows that come back.
   /// </summary>
   /// <param name="connection">Open connection.</param>
   /// <param name="sql">The nearest-neighbour SELECT.</param>
   /// <param name="probe">Query vector bytes.</param>
   /// <param name="ct">Cancellation.</param>
   /// <returns>How many rows the search returned.</returns>
   private async Task<int> WalkAsync( MySqlConnection connection, string sql, byte[] probe, CancellationToken ct )
   {
      await using MySqlCommand command = new( $"SET STATEMENT mhnsw_ef_search = {_options.HnswEfSearch} FOR {sql}", connection ) { CommandTimeout = PROBE_TIMEOUT_SECONDS };
      command.Parameters.AddWithValue( "@q", probe );
      await using MySqlDataReader reader = await command.ExecuteReaderAsync( ct );
      int rows = 0;
      while( await reader.ReadAsync( ct ) )
      {
         rows++;
      }

      return rows;
   }

   /// <summary>
   /// Builds the nearest-neighbour SELECT. The ORDER BY repeats the distance expression instead
   /// of using the select alias because the optimizer only picks the vector index when it sees
   /// the function itself, with a LIMIT.
   /// </summary>
   /// <param name="collection">Collection name.</param>
   /// <param name="top">Hits wanted.</param>
   /// <param name="hint">Index hint placed after the table name, or empty.</param>
   /// <returns>The statement, with the query vector as parameter @q.</returns>
   private string SearchSql( string collection, int top, string hint )
   {
      return "SELECT chunk_id, doc_key, `table`, `text`, VEC_DISTANCE_COSINE( embedding, @q ) AS dist "
         + $"FROM {Table( collection )} {hint} ORDER BY VEC_DISTANCE_COSINE( embedding, @q ) LIMIT {top}";
   }

   /// <summary>
   /// Runs one nearest-neighbour statement and converts the rows to hits.
   /// </summary>
   /// <param name="sql">The statement from <see cref="SearchSql"/>, possibly wrapped in SET STATEMENT.</param>
   /// <param name="vector">Query vector.</param>
   /// <param name="ct">Cancellation.</param>
   /// <returns>Hits, best first, with cosine similarity.</returns>
   private async Task<IReadOnlyList<SearchHit>> RunSearchAsync( string sql, float[] vector, CancellationToken ct )
   {
      await using MySqlConnection connection = await _dataSource.OpenConnectionAsync( ct );
      await using MySqlCommand command = new( sql, connection );
      command.Parameters.AddWithValue( "@q", ToBytes( vector ) );
      await using MySqlDataReader reader = await command.ExecuteReaderAsync( ct );
      var hits = new List<SearchHit>();
      while( await reader.ReadAsync( ct ) )
      {
         var chunkId = new Guid( (byte[])reader.GetValue( 0 ), bigEndian: true );
         hits.Add( new SearchHit( chunkId, reader.GetString( 1 ), reader.GetString( 2 ), reader.GetString( 3 ), 1.0 - reader.GetDouble( 4 ) ) );
      }

      return hits;
   }

   /// <summary>
   /// Writes one batch as a single multi-row INSERT that overwrites rows with the same chunk id.
   /// </summary>
   /// <param name="table">Quoted table name.</param>
   /// <param name="batch">Records to write.</param>
   /// <param name="ct">Cancellation.</param>
   private async Task WriteBatchAsync( string table, VectorRecord[] batch, CancellationToken ct )
   {
      var sql = new StringBuilder( 256 + batch.Length * 80 );
      sql.Append( $"INSERT INTO {table} ( chunk_id, doc_key, `table`, origin, ordinal, `text`, meta, embedding ) VALUES " );
      await using MySqlConnection connection = await _dataSource.OpenConnectionAsync( ct );
      await using MySqlCommand command = connection.CreateCommand();
      for( int i = 0; i < batch.Length; i++ )
      {
         VectorRecord r = batch[i];
         sql.Append( i == 0 ? string.Empty : "," ).Append( $"( @c{i}, @d{i}, @t{i}, @o{i}, @n{i}, @x{i}, @m{i}, @v{i} )" );
         command.Parameters.AddWithValue( $"@c{i}", IdBytes( r.Chunk.ChunkId ) );
         command.Parameters.AddWithValue( $"@d{i}", r.Document.DocKey );
         command.Parameters.AddWithValue( $"@t{i}", r.Document.Table );
         command.Parameters.AddWithValue( $"@o{i}", r.Document.Origin );
         command.Parameters.AddWithValue( $"@n{i}", r.Chunk.Ordinal );
         command.Parameters.AddWithValue( $"@x{i}", r.Chunk.Text );
         command.Parameters.AddWithValue( $"@m{i}", JsonSerializer.Serialize( r.Document.Metadata, META_JSON ) );
         command.Parameters.AddWithValue( $"@v{i}", ToBytes( r.Vector ) );
      }

      sql.Append( " ON DUPLICATE KEY UPDATE doc_key = VALUES( doc_key ), `table` = VALUES( `table` ), origin = VALUES( origin ), "
         + "ordinal = VALUES( ordinal ), `text` = VALUES( `text` ), meta = VALUES( meta ), embedding = VALUES( embedding )" );
      command.CommandText = sql.ToString();
      await command.ExecuteNonQueryAsync( ct );
   }

   /// <summary>
   /// Builds the CREATE TABLE statement for a pipeline.
   /// </summary>
   /// <param name="collection">Collection name.</param>
   /// <param name="dimension">Vector length.</param>
   /// <returns>The statement.</returns>
   private string CreateTableSql( string collection, int dimension )
   {
      return $@"CREATE TABLE {Table( collection )} (
   chunk_id BINARY(16) NOT NULL PRIMARY KEY,
   doc_key VARCHAR(512) NOT NULL,
   `table` VARCHAR(255) NOT NULL,
   origin TEXT NOT NULL,
   ordinal INT NOT NULL,
   `text` LONGTEXT NOT NULL,
   meta JSON NOT NULL,
   embedding VECTOR({dimension}) NOT NULL,
   VECTOR INDEX {INDEX_NAME} ( embedding ) M={_options.HnswM} DISTANCE=cosine
) ENGINE=InnoDB";
   }

   /// <summary>
   /// Creates the database on first use.
   /// </summary>
   /// <param name="ct">Cancellation.</param>
   private async Task EnsureDatabaseAsync( CancellationToken ct )
   {
      if( !_databaseReady )
      {
         await ExecuteAsync( $"CREATE DATABASE IF NOT EXISTS {Quote( _options.Database )}", ct );
         _databaseReady = true;
      }
   }

   /// <summary>
   /// Runs a statement that returns nothing.
   /// </summary>
   /// <param name="sql">Statement.</param>
   /// <param name="ct">Cancellation.</param>
   private async Task ExecuteAsync( string sql, CancellationToken ct )
   {
      await using MySqlConnection connection = await _dataSource.OpenConnectionAsync( ct );
      await using MySqlCommand command = new( sql, connection );
      await command.ExecuteNonQueryAsync( ct );
   }

   /// <summary>
   /// Runs a statement and returns the first column of its first row.
   /// </summary>
   /// <param name="sql">Statement.</param>
   /// <param name="ct">Cancellation.</param>
   /// <param name="parameters">Named parameters.</param>
   /// <returns>The value, or null when no row came back.</returns>
   private async Task<object?> ScalarAsync( string sql, CancellationToken ct, params (string Name, object Value)[] parameters )
   {
      await using MySqlConnection connection = await _dataSource.OpenConnectionAsync( ct );
      await using MySqlCommand command = new( sql, connection );
      foreach( ( string name, object value ) in parameters )
      {
         command.Parameters.AddWithValue( name, value );
      }

      return await command.ExecuteScalarAsync( ct );
   }

   /// <summary>
   /// Retries a call on lost connections, timeouts and deadlocks with a short backoff.
   /// </summary>
   /// <param name="call">The call. Must be safe to repeat.</param>
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
         catch( Exception ex ) when( attempt < MAX_ATTEMPTS && IsTransient( ex ) && !ct.IsCancellationRequested )
         {
            await Task.Delay( TimeSpan.FromSeconds( attempt * 2 ), ct );
         }
      }
   }

   /// <summary>
   /// True for failures that a repeat can cure: a dropped connection, a timeout or a deadlock.
   /// </summary>
   /// <param name="ex">The failure.</param>
   /// <returns>Whether to retry.</returns>
   private static bool IsTransient( Exception ex )
   {
      return ex is MySqlException { IsTransient: true } or MySqlException { ErrorCode: MySqlErrorCode.LockDeadlock or MySqlErrorCode.LockWaitTimeout }
         or TimeoutException;
   }

   /// <summary>
   /// A chunk id as the 16 bytes stored in the BINARY(16) key, in the standard (big-endian) order
   /// so the hex shown in a SQL client reads like the usual GUID text.
   /// </summary>
   /// <param name="id">The chunk id.</param>
   /// <returns>16 bytes.</returns>
   private static byte[] IdBytes( Guid id )
   {
      return id.ToByteArray( bigEndian: true );
   }

   /// <summary>
   /// The bytes of a vector as MariaDB stores it: little-endian float32, no header.
   /// </summary>
   /// <param name="vector">The vector.</param>
   /// <returns>A new byte array.</returns>
   private static byte[] ToBytes( float[] vector )
   {
      return MemoryMarshal.AsBytes( vector.AsSpan() ).ToArray();
   }

   /// <summary>
   /// Backtick-quotes an identifier, doubling any backtick in it.
   /// </summary>
   /// <param name="identifier">The identifier.</param>
   /// <returns>The quoted identifier.</returns>
   private static string Quote( string identifier )
   {
      return "`" + identifier.Replace( "`", "``" ) + "`";
   }

   /// <summary>
   /// Fully qualified, quoted table name for a pipeline.
   /// </summary>
   /// <param name="collection">Pipeline collection name.</param>
   /// <returns>database.table, quoted.</returns>
   private string Table( string collection )
   {
      return $"{Quote( _options.Database )}.{Quote( TableName( collection ) )}";
   }

   /// <summary>
   /// Table name for a pipeline. Pipeline names are already sanitized to [a-z0-9_].
   /// </summary>
   /// <param name="collection">Pipeline collection name.</param>
   /// <returns>The table name.</returns>
   private static string TableName( string collection )
   {
      return $"gvb_{collection}";
   }

   #endregion Private Methods

   #region IDisposable

   /// <summary>
   /// Closes the connection pool.
   /// </summary>
   public void Dispose()
   {
      _dataSource.Dispose();
   }

   #endregion IDisposable
}
