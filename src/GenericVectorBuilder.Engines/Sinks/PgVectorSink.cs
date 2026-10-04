using System.Diagnostics;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using GenericVectorBuilder.Core.Contracts;
using GenericVectorBuilder.Engines.Common;
using Npgsql;
using NpgsqlTypes;
using Pgvector;
using Pgvector.Npgsql;

namespace GenericVectorBuilder.Engines.Sinks;

/// <summary>
/// Stores vectors in PostgreSQL 17 with the pgvector 0.8.7 extension: one table per pipeline
/// named gvb_{pipeline} (the chunk id is the uuid primary key) and one HNSW index on its
/// vector column named gvb_{pipeline}_hnsw.
/// Columns: id, doc_key, table_name, origin, ordinal, text, meta (jsonb, one entry per
/// document metadata key) and embedding vector(n). Why table_name and not table: "table" is a
/// reserved word in SQL.
/// Default search: an HNSW index (vector_cosine_ops, m 16, ef_construction 128) queried with
/// ORDER BY embedding &lt;=&gt; query LIMIT k and hnsw.ef_search from
/// <see cref="PgVectorSinkOptions.HnswEfSearch"/>. Why ef_search is 100: pgvector defaults it to
/// 40, and an index scan can return at most ef_search rows, so it must never be below the hits
/// asked for; 100 is also the beam width the Redis sink uses, so the two compare like for like.
/// Measured 2026-10-03 on this box (shared with other jobs, load average 6 to 20 on 8 threads),
/// 100,000 random unit vectors of 1024 dimensions, m 16, ef_construction 128, 20 queries,
/// recall@10 against an in-memory brute force:
///   ef_search   recall@10   ms p50
///   40          0.06        6.5
///   100         0.08        7.4
///   400         0.28        19.4
///   1000        0.58        41.9
///   exact scan  1.00        427
/// Random vectors give a graph nothing to exploit, so this is the worst case and a floor for
/// real embeddings, not a forecast. The same sweep on 10,000 vectors reached 0.49 at 100 and
/// 1.00 at 1000. The load of 100,000 vectors took 1,088 seconds (about 92 vectors a second)
/// because every insert searches and rewires the graph while the index is being kept current.
/// pgvector's fast route is to load first and run CREATE INDEX afterwards (parallel build),
/// which a streaming pipeline cannot do, so this sink always inserts into the live index.
/// Why enable_seqscan is switched off for the default search: on a small table the planner can
/// prefer a sequential scan, which would make the "approximate" search silently exact. Turned
/// off for the one transaction only, so the HNSW path is what every default search measures.
/// Exact search: the same ORDER BY with enable_indexscan switched off for the transaction, so
/// the planner scans the whole table and keeps the best k. JIT is also switched off for both
/// modes: compiling a query this short is pure overhead.
/// Why scores are 1 - distance: pgvector's cosine operator returns distance (1 - cosine
/// similarity), and every sink reports similarity.
/// Why upserts are INSERT ... ON CONFLICT DO UPDATE per record, pipelined in one transaction
/// per batch: a retried batch is harmless, a repeated id inside one batch is allowed, and the
/// whole row (metadata included) is replaced.
/// Why the extension is created in a separate connection first: Npgsql learns the vector type
/// when a connection opens. A pooled connection opened before CREATE EXTENSION would never
/// know it, so the extension is created once before the pooled data source exists.
/// Readiness: the HNSW index is updated inside each insert's own transaction, so nothing is
/// built after the writes return and <see cref="FinishLoadAsync"/> only confirms that. The proof
/// is the engine's own account (<see cref="GetIndexStateAsync"/>): pg_indexes lists the index,
/// pg_index says it is valid, ready and live, EXPLAIN of the default search shows an Index Scan on
/// it, and that search, run for real, returns its rows. Why indexed vectors are not reported:
/// PostgreSQL keeps no entry count for an HNSW index. pg_class.reltuples of the index is the
/// table's sampled estimate (19,000 for a 20,000-row table in testing), and a walk of the graph
/// is no count either: an iterative scan (hnsw.iterative_scan = relaxed_order) over a 20,000-row
/// table returned 19,999 rows at ef_search 100, 19,995 at 40 and 18,771 at 1,000, because an
/// approximate graph can leave nodes without an inbound link and the scan's stopping rule moves
/// with ef_search. A check that demanded every row from the walk failed a healthy index.
/// Limits: HNSW on the vector type holds at most 2000 dimensions, so a larger embedder is
/// refused with a clear message instead of failing on index creation. NUL characters are
/// removed from text because PostgreSQL cannot store them.
/// </summary>
public sealed class PgVectorSink : ISink, IExactSearchSink, IEngineDescription, IIndexFinisher, IDisposable
{
   #region Data Members

   private const int READY_WAIT_SECONDS = 120;
   private const int PROBE_TIMEOUT_SECONDS = 120;
   private const int PROBE_TOP = 10;
   private const int MAX_HNSW_DIMENSION = 2000;
   private const int MAX_EF_SEARCH = 1000;
   private const int MAX_IDENTIFIER_BYTES = 63;
   private const int DELETE_BATCH = 5000;
   private const int MAX_ATTEMPTS = 3;
   private const string SEARCH_SETTINGS = "SET LOCAL enable_seqscan = off; SET LOCAL jit = off; SET LOCAL hnsw.ef_search = ";
   private const string EXACT_SETTINGS = "SET LOCAL enable_indexscan = off; SET LOCAL jit = off";
   private static readonly Regex COLLECTION_PATTERN = new( "^[A-Za-z0-9_]{1,200}$", RegexOptions.Compiled | RegexOptions.CultureInvariant );

   private readonly PgVectorSinkOptions _options;
   private readonly SemaphoreSlim _openGate = new( 1, 1 );
   private NpgsqlDataSource? _dataSource;

   #endregion Data Members

   #region Constructor

   /// <summary>
   /// Creates a sink for the local pgvector container with the password from the secrets file.
   /// Opens no connection until the first call, so the engine catalog can create it just to read
   /// its name and description when the container is down.
   /// </summary>
   public PgVectorSink() : this( PgVectorSinkOptions.LocalDefaults() )
   {
   }

   /// <summary>
   /// Creates a sink with explicit settings.
   /// </summary>
   /// <param name="options">Connection and index settings.</param>
   public PgVectorSink( PgVectorSinkOptions options )
   {
      _options = options;
   }

   #endregion Constructor

   #region Public Methods

   /// <inheritdoc />
   public string Name => "pgvector";

   /// <inheritdoc />
   public string Engine => "PostgreSQL 17 + pgvector 0.8.7";

   /// <inheritdoc />
   public string IndexDescription =>
      $"HNSW vector_cosine_ops m={_options.HnswM} ef_construction={_options.HnswEfConstruction}, hnsw.ef_search={_options.HnswEfSearch} per query, float32 vector(n), cosine; exact mode = same query with index scans off (sequential scan)";

   /// <inheritdoc />
   public string ComposeFile => "pgvector.compose.yaml";

   /// <inheritdoc />
   public string Durability =>
      "fsync on, synchronous_commit on, full_page_writes on, wal_sync_method fdatasync (PostgreSQL defaults; pgvector.compose.yaml sets only shared_buffers, "
      + "maintenance_work_mem and max_wal_size): every commit is flushed to the write-ahead log before it returns and HNSW index changes are WAL-logged, "
      + "so a crash loses no committed row (max_wal_size 4GB only spaces out checkpoints; measured with docker kill, which is SIGKILL, right after a 2,000-vector load and a restart: "
      + "all 2,000 rows were there and the HNSW index was valid and used by the default search)";

   /// <inheritdoc />
   public async Task<string> FinishLoadAsync( string collection, CancellationToken ct )
   {
      var clock = Stopwatch.StartNew();
      IndexState state = await WaitUntilReadyAsync( collection, ct );
      return $"HNSW is maintained inside every insert, nothing to build; confirmed in {clock.Elapsed.TotalSeconds:F1} s: {state.Detail}";
   }

   /// <inheritdoc />
   public async Task<IndexState> GetIndexStateAsync( string collection, CancellationToken ct )
   {
      string table = TableName( collection );
      string index = IndexName( collection );
      await using NpgsqlConnection connection = await OpenAsync( ct );
      long total = await CountRowsAsync( connection, table, ct );
      ( bool listed, bool usable, string definition ) = await ReadIndexCatalogAsync( connection, table, index, ct );
      if( !listed )
      {
         return new IndexState( false, null, total, $"not ready: pg_indexes has no index {index} on {table}" );
      }

      int efSearch = Math.Min( Math.Max( _options.HnswEfSearch, 10 ), MAX_EF_SEARCH );
      string settings = SEARCH_SETTINGS + efSearch;
      string select = $"SELECT id FROM {Quote( table )} ORDER BY embedding <=> ( SELECT embedding FROM {Quote( table )} LIMIT 1 ) LIMIT {PROBE_TOP}";
      bool defaultUsesIndex = await PlanUsesIndexAsync( connection, settings, select, index, ct );
      long wanted = Math.Min( PROBE_TOP, total );
      long returned = defaultUsesIndex ? await CountRowsOfSearchAsync( connection, settings, select, ct ) : 0;
      var problems = new List<string>();
      if( !usable )
      {
         problems.Add( "pg_index says the index is not valid and ready" );
      }

      if( !defaultUsesIndex )
      {
         problems.Add( "EXPLAIN of the default search does not show an Index Scan on the hnsw index" );
      }

      if( returned < wanted )
      {
         problems.Add( $"the default search returned {returned} of {wanted} rows" );
      }

      string facts = $"pg_indexes lists {index} ({definition}), pg_index valid and ready = {usable}, EXPLAIN of the default search uses Index Scan on it = {defaultUsesIndex}, "
         + $"that search returned {returned} of {wanted} rows; PostgreSQL keeps no entry count for an HNSW index, so indexed vectors are not reported";
      return new IndexState( problems.Count == 0, null, total, problems.Count == 0 ? facts : $"not ready: {string.Join( "; ", problems )} ({facts})" );
   }

   /// <inheritdoc />
   public async Task<bool> EnsureCollectionAsync( string collection, int dimension, CancellationToken ct )
   {
      string table = TableName( collection );
      await using NpgsqlConnection connection = await OpenAsync( ct );
      int? existing = await ExistingDimensionAsync( connection, table, ct );
      if( existing.HasValue && existing.Value != dimension )
      {
         throw new InvalidOperationException( $"PostgreSQL table {table} holds {existing}-dim vectors but the embedder produces {dimension}. Reset the pipeline or use a new name." );
      }

      if( dimension > MAX_HNSW_DIMENSION )
      {
         throw new InvalidOperationException( $"pgvector can build an HNSW index on at most {MAX_HNSW_DIMENSION} dimensions, but the embedder produces {dimension}." );
      }

      await using NpgsqlTransaction transaction = await connection.BeginTransactionAsync( ct );
      if( !existing.HasValue )
      {
         await ExecuteAsync( connection, transaction, CreateTableSql( table, dimension ), ct );
      }

      await ExecuteAsync( connection, transaction, CreateIndexSql( collection, table ), ct );
      await transaction.CommitAsync( ct );
      return !existing.HasValue;
   }

   /// <inheritdoc />
   public async Task UpsertAsync( string collection, IReadOnlyList<VectorRecord> records, CancellationToken ct )
   {
      string sql = UpsertSql( TableName( collection ) );
      foreach( VectorRecord[] batch in records.Chunk( _options.UpsertBatch ) )
      {
         await WithRetryAsync( async () =>
         {
            await using NpgsqlConnection connection = await OpenAsync( ct );
            await using NpgsqlTransaction transaction = await connection.BeginTransactionAsync( ct );
            await using NpgsqlBatch commands = new( connection, transaction );
            foreach( VectorRecord record in batch )
            {
               commands.BatchCommands.Add( UpsertCommand( sql, record ) );
            }

            await commands.ExecuteNonQueryAsync( ct );
            await transaction.CommitAsync( ct );
         }, ct );
      }
   }

   /// <inheritdoc />
   public async Task DeleteAsync( string collection, IReadOnlyList<Guid> chunkIds, CancellationToken ct )
   {
      string sql = $"DELETE FROM {Quote( TableName( collection ) )} WHERE id = ANY( @ids )";
      foreach( Guid[] batch in chunkIds.Chunk( DELETE_BATCH ) )
      {
         await WithRetryAsync( async () =>
         {
            await using NpgsqlConnection connection = await OpenAsync( ct );
            await using var command = new NpgsqlCommand( sql, connection );
            command.Parameters.AddWithValue( "ids", batch );
            await command.ExecuteNonQueryAsync( ct );
         }, ct );
      }
   }

   /// <inheritdoc />
   public async Task<long> CountAsync( string collection, CancellationToken ct )
   {
      await using NpgsqlConnection connection = await OpenAsync( ct );
      await using var command = new NpgsqlCommand( $"SELECT count(*) FROM {Quote( TableName( collection ) )}", connection );
      return (long)( await command.ExecuteScalarAsync( ct ) ?? 0L );
   }

   /// <inheritdoc />
   public Task<IReadOnlyList<SearchHit>> SearchAsync( string collection, float[] vector, int top, CancellationToken ct )
   {
      int efSearch = Math.Min( Math.Max( _options.HnswEfSearch, top ), MAX_EF_SEARCH );
      return QueryAsync( collection, vector, top, SEARCH_SETTINGS + efSearch, ct );
   }

   /// <inheritdoc />
   public Task<IReadOnlyList<SearchHit>> SearchExactAsync( string collection, float[] vector, int top, CancellationToken ct )
   {
      return QueryAsync( collection, vector, top, EXACT_SETTINGS, ct );
   }

   /// <inheritdoc />
   public async Task DropCollectionAsync( string collection, CancellationToken ct )
   {
      await using NpgsqlConnection connection = await OpenAsync( ct );
      await ExecuteAsync( connection, null, $"DROP TABLE IF EXISTS {Quote( TableName( collection ) )}", ct );
   }

   #endregion Public Methods

   #region Private Methods

   /// <summary>
   /// Polls <see cref="GetIndexStateAsync"/> until the engine says the index is ready, with a
   /// deadline. A synchronous index is ready at once, so the loop only matters if something
   /// else (a concurrent REINDEX, a CREATE INDEX CONCURRENTLY) is still running.
   /// </summary>
   /// <param name="collection">Collection name.</param>
   /// <param name="ct">Cancellation.</param>
   /// <returns>The ready state.</returns>
   /// <exception cref="InvalidOperationException">The index was not ready before the deadline.</exception>
   private async Task<IndexState> WaitUntilReadyAsync( string collection, CancellationToken ct )
   {
      var clock = Stopwatch.StartNew();
      int pauseMs = 500;
      while( true )
      {
         IndexState state = await GetIndexStateAsync( collection, ct );
         if( state.Ready )
         {
            return state;
         }

         if( clock.Elapsed.TotalSeconds > READY_WAIT_SECONDS )
         {
            throw new InvalidOperationException( $"The pgvector index for {collection} was not ready after {READY_WAIT_SECONDS} seconds: {state.Detail}" );
         }

         await Task.Delay( pauseMs, ct );
         pauseMs = Math.Min( pauseMs * 2, 5000 );
      }
   }

   /// <summary>
   /// Counts the rows of a table exactly.
   /// </summary>
   /// <param name="connection">Open connection.</param>
   /// <param name="table">Table name.</param>
   /// <param name="ct">Cancellation.</param>
   /// <returns>The row count.</returns>
   private static async Task<long> CountRowsAsync( NpgsqlConnection connection, string table, CancellationToken ct )
   {
      await using var command = new NpgsqlCommand( $"SELECT count(*) FROM {Quote( table )}", connection ) { CommandTimeout = PROBE_TIMEOUT_SECONDS };
      return (long)( await command.ExecuteScalarAsync( ct ) ?? 0L );
   }

   /// <summary>
   /// Reads the index from the catalogs: its definition from pg_indexes and its validity flags
   /// from pg_index (indisvalid false means a failed or running build, indisready false means
   /// inserts are not being applied to it yet).
   /// </summary>
   /// <param name="connection">Open connection.</param>
   /// <param name="table">Table name.</param>
   /// <param name="index">Index name.</param>
   /// <param name="ct">Cancellation.</param>
   /// <returns>Whether pg_indexes lists it, whether it is valid and ready, and its definition from USING onwards.</returns>
   private static async Task<( bool Listed, bool Usable, string Definition )> ReadIndexCatalogAsync( NpgsqlConnection connection, string table, string index, CancellationToken ct )
   {
      const string SQL = "SELECT x.indexdef, i.indisvalid AND i.indisready AND i.indislive FROM pg_indexes x "
         + "JOIN pg_index i ON i.indexrelid = ( quote_ident( x.schemaname ) || '.' || quote_ident( x.indexname ) )::regclass "
         + "WHERE x.schemaname = current_schema() AND x.tablename = @table AND x.indexname = @index";
      await using var command = new NpgsqlCommand( SQL, connection ) { CommandTimeout = PROBE_TIMEOUT_SECONDS };
      command.Parameters.AddWithValue( "table", table );
      command.Parameters.AddWithValue( "index", index );
      await using NpgsqlDataReader reader = await command.ExecuteReaderAsync( ct );
      if( !await reader.ReadAsync( ct ) )
      {
         return ( false, false, string.Empty );
      }

      string definition = reader.GetString( 0 );
      int usingAt = definition.IndexOf( "USING", StringComparison.Ordinal );
      return ( true, reader.GetBoolean( 1 ), usingAt >= 0 ? definition[usingAt..] : definition );
   }

   /// <summary>
   /// Asks the planner how it would run a statement under the given settings and reports whether
   /// the plan has an Index Scan on the named index. Why EXPLAIN and not just trusting the
   /// settings: a plan that fell back to a sequential scan would make the "approximate" search
   /// silently exact.
   /// </summary>
   /// <param name="connection">Open connection.</param>
   /// <param name="settings">SET LOCAL statements the search would run under.</param>
   /// <param name="select">The SELECT to explain.</param>
   /// <param name="index">Index name to look for.</param>
   /// <param name="ct">Cancellation.</param>
   /// <returns>True when the plan scans that index.</returns>
   private static async Task<bool> PlanUsesIndexAsync( NpgsqlConnection connection, string settings, string select, string index, CancellationToken ct )
   {
      await using NpgsqlTransaction transaction = await connection.BeginTransactionAsync( ct );
      await ExecuteAsync( connection, transaction, settings, ct );
      await using var command = new NpgsqlCommand( "EXPLAIN ( FORMAT JSON ) " + select, connection, transaction ) { CommandTimeout = PROBE_TIMEOUT_SECONDS };
      string plan = Convert.ToString( await command.ExecuteScalarAsync( ct ), CultureInfo.InvariantCulture ) ?? string.Empty;
      await transaction.CommitAsync( ct );
      using JsonDocument document = JsonDocument.Parse( plan );
      return document.RootElement.EnumerateArray().Any( entry => ScansIndex( entry.GetProperty( "Plan" ), index ) );
   }

   /// <summary>
   /// Walks an EXPLAIN plan tree looking for an Index Scan node on the named index.
   /// </summary>
   /// <param name="node">A plan node.</param>
   /// <param name="index">Index name.</param>
   /// <returns>True when this node or any child scans that index.</returns>
   private static bool ScansIndex( JsonElement node, string index )
   {
      if( node.TryGetProperty( "Index Name", out JsonElement name ) && name.GetString() == index
         && node.TryGetProperty( "Node Type", out JsonElement type ) && type.GetString()!.Contains( "Index", StringComparison.Ordinal ) )
      {
         return true;
      }

      return node.TryGetProperty( "Plans", out JsonElement children ) && children.EnumerateArray().Any( child => ScansIndex( child, index ) );
   }

   /// <summary>
   /// Runs a search statement under the given settings, inside a transaction so SET LOCAL applies,
   /// and counts the rows it returns. The caller has already had the statement explained, so a
   /// plan that bypassed the index cannot pass this check by returning every row from a scan.
   /// </summary>
   /// <param name="connection">Open connection.</param>
   /// <param name="settings">SET LOCAL statements the search runs under.</param>
   /// <param name="select">The SELECT to run.</param>
   /// <param name="ct">Cancellation.</param>
   /// <returns>How many rows the statement returned.</returns>
   private static async Task<long> CountRowsOfSearchAsync( NpgsqlConnection connection, string settings, string select, CancellationToken ct )
   {
      await using NpgsqlTransaction transaction = await connection.BeginTransactionAsync( ct );
      await ExecuteAsync( connection, transaction, settings, ct );
      await using var command = new NpgsqlCommand( select, connection, transaction ) { CommandTimeout = PROBE_TIMEOUT_SECONDS };
      long rows = 0;
      await using( NpgsqlDataReader reader = await command.ExecuteReaderAsync( ct ) )
      {
         while( await reader.ReadAsync( ct ) )
         {
            rows++;
         }
      }

      await transaction.CommitAsync( ct );
      return rows;
   }

   /// <summary>
   /// Opens a pooled connection, creating the data source on first use.
   /// </summary>
   /// <param name="ct">Cancellation.</param>
   /// <returns>An open connection.</returns>
   private async Task<NpgsqlConnection> OpenAsync( CancellationToken ct )
   {
      NpgsqlDataSource source = _dataSource ?? await CreateDataSourceAsync( ct );
      return await source.OpenConnectionAsync( ct );
   }

   /// <summary>
   /// Creates the vector extension (once, over a throwaway connection) and then the pooled data
   /// source that maps the vector type. See the class remarks for why the order matters.
   /// </summary>
   /// <param name="ct">Cancellation.</param>
   /// <returns>The shared data source.</returns>
   private async Task<NpgsqlDataSource> CreateDataSourceAsync( CancellationToken ct )
   {
      await _openGate.WaitAsync( ct );
      try
      {
         if( _dataSource is null )
         {
            NpgsqlConnectionStringBuilder connectionString = BuildConnectionString();
            connectionString.Pooling = false;
            await using( var bootstrap = new NpgsqlConnection( connectionString.ConnectionString ) )
            {
               await bootstrap.OpenAsync( ct );
               await ExecuteAsync( bootstrap, null, "CREATE EXTENSION IF NOT EXISTS vector", ct );
            }

            var builder = new NpgsqlDataSourceBuilder( BuildConnectionString().ConnectionString );
            builder.UseVector();
            _dataSource = builder.Build();
         }

         return _dataSource;
      }
      finally
      {
         _openGate.Release();
      }
   }

   /// <summary>
   /// Builds the connection string from the options.
   /// </summary>
   /// <returns>The connection string builder.</returns>
   private NpgsqlConnectionStringBuilder BuildConnectionString()
   {
      return new NpgsqlConnectionStringBuilder
      {
         Host = _options.Host,
         Port = _options.Port,
         Database = _options.Database,
         Username = _options.User,
         Password = _options.Password,
         CommandTimeout = _options.CommandTimeoutSeconds,
         Timeout = 15,
         MaxPoolSize = 16,
      };
   }

   /// <summary>
   /// Runs one search inside a transaction so the per-query settings (SET LOCAL) apply to this
   /// query only and cannot leak to the next user of the pooled connection.
   /// </summary>
   /// <param name="collection">Collection name.</param>
   /// <param name="vector">Query vector.</param>
   /// <param name="top">Hits wanted.</param>
   /// <param name="settings">SET LOCAL statements that pick the index or the scan.</param>
   /// <param name="ct">Cancellation.</param>
   /// <returns>Hits, best first, with cosine similarity.</returns>
   private async Task<IReadOnlyList<SearchHit>> QueryAsync( string collection, float[] vector, int top, string settings, CancellationToken ct )
   {
      string sql = $"SELECT id, doc_key, table_name, text, embedding <=> @query FROM {Quote( TableName( collection ) )} ORDER BY embedding <=> @query LIMIT @top";
      await using NpgsqlConnection connection = await OpenAsync( ct );
      await using NpgsqlTransaction transaction = await connection.BeginTransactionAsync( ct );
      await ExecuteAsync( connection, transaction, settings, ct );
      await using var command = new NpgsqlCommand( sql, connection, transaction );
      command.Parameters.AddWithValue( "query", new Vector( vector ) );
      command.Parameters.AddWithValue( "top", top );
      var hits = new List<SearchHit>( top );
      await using( NpgsqlDataReader reader = await command.ExecuteReaderAsync( ct ) )
      {
         while( await reader.ReadAsync( ct ) )
         {
            hits.Add( new SearchHit( reader.GetGuid( 0 ), reader.GetString( 1 ), reader.GetString( 2 ), reader.GetString( 3 ), 1.0 - reader.GetDouble( 4 ) ) );
         }
      }

      await transaction.CommitAsync( ct );
      return hits;
   }

   /// <summary>
   /// Reads the vector dimension of a table. Null means the table does not exist.
   /// </summary>
   /// <param name="connection">Open connection.</param>
   /// <param name="table">Table name.</param>
   /// <param name="ct">Cancellation.</param>
   /// <returns>The dimension, or null.</returns>
   private static async Task<int?> ExistingDimensionAsync( NpgsqlConnection connection, string table, CancellationToken ct )
   {
      const string SQL = "SELECT ( SELECT a.atttypmod FROM pg_attribute a WHERE a.attrelid = r.oid AND a.attname = 'embedding' AND NOT a.attisdropped ) "
         + "FROM ( SELECT to_regclass( @name )::oid AS oid ) r WHERE r.oid IS NOT NULL";
      await using var command = new NpgsqlCommand( SQL, connection );
      command.Parameters.AddWithValue( "name", Quote( table ) );
      await using NpgsqlDataReader reader = await command.ExecuteReaderAsync( ct );
      if( !await reader.ReadAsync( ct ) )
      {
         return null;
      }

      if( await reader.IsDBNullAsync( 0, ct ) )
      {
         throw new InvalidOperationException( $"PostgreSQL table {table} exists but has no embedding column. Drop it or use a new name." );
      }

      return reader.GetInt32( 0 );
   }

   /// <summary>
   /// Runs one SQL statement that returns no rows.
   /// </summary>
   /// <param name="connection">Open connection.</param>
   /// <param name="transaction">Transaction to run in, or null.</param>
   /// <param name="sql">The statement.</param>
   /// <param name="ct">Cancellation.</param>
   private static async Task ExecuteAsync( NpgsqlConnection connection, NpgsqlTransaction? transaction, string sql, CancellationToken ct )
   {
      await using var command = new NpgsqlCommand( sql, connection, transaction );
      await command.ExecuteNonQueryAsync( ct );
   }

   /// <summary>
   /// SQL that creates a pipeline's table.
   /// </summary>
   /// <param name="table">Table name.</param>
   /// <param name="dimension">Vector length.</param>
   /// <returns>The statement.</returns>
   private static string CreateTableSql( string table, int dimension )
   {
      return $"CREATE TABLE IF NOT EXISTS {Quote( table )} ( id uuid PRIMARY KEY, doc_key text NOT NULL, table_name text NOT NULL, origin text NOT NULL, "
         + $"ordinal integer NOT NULL, text text NOT NULL, meta jsonb NOT NULL DEFAULT '{{}}', embedding vector({dimension}) NOT NULL )";
   }

   /// <summary>
   /// SQL that creates the HNSW index if it is missing. Run for an existing table too, so a
   /// table whose index was dropped by hand gets it back.
   /// </summary>
   /// <param name="collection">Collection name.</param>
   /// <param name="table">Table name.</param>
   /// <returns>The statement.</returns>
   private string CreateIndexSql( string collection, string table )
   {
      return $"CREATE INDEX IF NOT EXISTS {Quote( IndexName( collection ) )} ON {Quote( table )} USING hnsw ( embedding vector_cosine_ops ) "
         + $"WITH ( m = {_options.HnswM}, ef_construction = {_options.HnswEfConstruction} )";
   }

   /// <summary>
   /// SQL that inserts one record or replaces the row with the same id.
   /// </summary>
   /// <param name="table">Table name.</param>
   /// <returns>The statement.</returns>
   private static string UpsertSql( string table )
   {
      return $"INSERT INTO {Quote( table )} ( id, doc_key, table_name, origin, ordinal, text, meta, embedding ) "
         + "VALUES ( @id, @doc_key, @table_name, @origin, @ordinal, @text, @meta, @embedding ) "
         + "ON CONFLICT ( id ) DO UPDATE SET doc_key = EXCLUDED.doc_key, table_name = EXCLUDED.table_name, origin = EXCLUDED.origin, "
         + "ordinal = EXCLUDED.ordinal, text = EXCLUDED.text, meta = EXCLUDED.meta, embedding = EXCLUDED.embedding";
   }

   /// <summary>
   /// Builds the upsert command for one record.
   /// </summary>
   /// <param name="sql">The upsert statement.</param>
   /// <param name="record">The record.</param>
   /// <returns>A batch command with its parameters filled in.</returns>
   private static NpgsqlBatchCommand UpsertCommand( string sql, VectorRecord record )
   {
      var metadata = record.Document.Metadata.ToDictionary( p => p.Key, p => Clean( p.Value ) );
      var command = new NpgsqlBatchCommand( sql );
      command.Parameters.AddWithValue( "id", record.Chunk.ChunkId );
      command.Parameters.AddWithValue( "doc_key", Clean( record.Document.DocKey ) );
      command.Parameters.AddWithValue( "table_name", Clean( record.Document.Table ) );
      command.Parameters.AddWithValue( "origin", Clean( record.Document.Origin ) );
      command.Parameters.AddWithValue( "ordinal", record.Chunk.Ordinal );
      command.Parameters.AddWithValue( "text", Clean( record.Chunk.Text ) );
      command.Parameters.Add( new NpgsqlParameter( "meta", NpgsqlDbType.Jsonb ) { Value = JsonSerializer.Serialize( metadata ) } );
      command.Parameters.AddWithValue( "embedding", new Vector( record.Vector ) );
      return command;
   }

   /// <summary>
   /// Removes NUL characters, which PostgreSQL text and jsonb cannot hold.
   /// </summary>
   /// <param name="value">Any text.</param>
   /// <returns>The text without NULs.</returns>
   private static string Clean( string value )
   {
      return value.Contains( '\0' ) ? value.Replace( "\0", string.Empty ) : value;
   }

   /// <summary>
   /// Retries a database call on transient connection failures with a short backoff.
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
         catch( NpgsqlException ex ) when( attempt < MAX_ATTEMPTS && ex.IsTransient )
         {
            await Task.Delay( TimeSpan.FromSeconds( attempt * 2 ), ct );
         }
      }
   }

   /// <summary>
   /// Table name for a pipeline. Pipeline names are already sanitized to [a-z0-9_]; this checks
   /// that again because the name goes into SQL text.
   /// </summary>
   /// <param name="collection">Pipeline collection name.</param>
   /// <returns>The table name, shortened with a hash when it would pass PostgreSQL's 63-byte limit.</returns>
   private static string TableName( string collection )
   {
      return Identifier( collection, "gvb_", string.Empty );
   }

   /// <summary>
   /// HNSW index name for a pipeline.
   /// </summary>
   /// <param name="collection">Pipeline collection name.</param>
   /// <returns>The index name.</returns>
   private static string IndexName( string collection )
   {
      return Identifier( collection, "gvb_", "_hnsw" );
   }

   /// <summary>
   /// Builds a PostgreSQL identifier. PostgreSQL silently cuts identifiers at 63 bytes, and a
   /// pipeline name can be 60 characters, so two long names could otherwise collide. Over the
   /// limit, the name is cut and an 8-character hash of the full name is appended.
   /// </summary>
   /// <param name="collection">Pipeline collection name.</param>
   /// <param name="prefix">Text before the name.</param>
   /// <param name="suffix">Text after the name.</param>
   /// <returns>An identifier of at most 63 bytes.</returns>
   private static string Identifier( string collection, string prefix, string suffix )
   {
      if( !COLLECTION_PATTERN.IsMatch( collection ) )
      {
         throw new ArgumentException( $"'{collection}' is not a valid pipeline name. Use letters, digits and underscores.", nameof( collection ) );
      }

      string full = prefix + collection + suffix;
      if( full.Length <= MAX_IDENTIFIER_BYTES )
      {
         return full;
      }

      string hash = Convert.ToHexString( SHA1.HashData( Encoding.UTF8.GetBytes( full ) ) )[..8].ToLowerInvariant();
      return full[..( MAX_IDENTIFIER_BYTES - 9 )] + "_" + hash;
   }

   /// <summary>
   /// Quotes an identifier so its exact spelling is kept.
   /// </summary>
   /// <param name="identifier">A table or index name already checked by <see cref="Identifier"/>.</param>
   /// <returns>The quoted identifier.</returns>
   private static string Quote( string identifier )
   {
      return "\"" + identifier + "\"";
   }

   #endregion Private Methods

   #region IDisposable

   /// <summary>
   /// Closes every pooled connection if the data source was ever created.
   /// </summary>
   public void Dispose()
   {
      _dataSource?.Dispose();
      _openGate.Dispose();
   }

   #endregion IDisposable
}
