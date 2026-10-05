using System.Collections.Concurrent;
using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using DuckDB.NET.Data;
using GenericVectorBuilder.Core.Contracts;
using GenericVectorBuilder.Engines.Common;

namespace GenericVectorBuilder.Engines.Sinks;

/// <summary>
/// Stores vectors in DuckDB with the vss extension: one database file per collection, named
/// gvb_{collection}.duckdb, holding one table named gvb_{collection} and one HNSW index named
/// gvb_{collection}_hnsw on its vector column.
/// Default search: an HNSW index (metric cosine, m and ef_construction from
/// <see cref="DuckDbSinkOptions"/>, 16 and 128 by default) queried with
/// ORDER BY array_cosine_distance( embedding, query ) LIMIT k. DuckDB's optimizer swaps that exact
/// query shape for an index scan (the plan shows HNSW_INDEX_SCAN). ef_search is set per
/// connection with SET hnsw_ef_search.
/// Exact search: the same SELECT ordered by array_cosine_similarity DESC. The HNSW index only
/// answers the distance function, so this shape is a plain sequential scan that compares every
/// stored vector. That is why this sink also implements <see cref="IExactSearchSink"/>.
/// Why persistence needs a switch: DuckDB refuses to create an HNSW index in a database file
/// unless SET hnsw_enable_experimental_persistence = true is on for that database. DuckDB marks
/// file-backed HNSW as experimental and documents that write-ahead-log recovery is not complete
/// for custom indexes, so a crash can lose or damage recent uncommitted work in the index. This
/// sink turns the switch on anyway, because the pipeline has to keep its vectors between runs
/// and an in-memory index would be empty after every restart. In testing, a process killed right
/// after a commit was recovered correctly (rows and index), but the safe habit is to let the sink
/// dispose normally, which writes the log back into the file. If an index ever looks wrong, drop
/// the collection and run the pipeline again.
/// Why vss must be loaded first: a file that contains an HNSW index cannot be modified by a
/// connection that has not run LOAD vss, and running LOAD vss after such a failed attempt
/// hung in testing. This sink runs it as the first statement of every connection, before
/// anything else touches the file. The extension is built into the native library that
/// DuckDB.NET.Data.Full ships, so no download happens.
/// Why the checkpoint threshold is raised: each checkpoint rewrites the whole HNSW index, and a
/// bulk load at DuckDB's 16MB default produced a file about 13 times the size of the raw vectors (see
/// <see cref="DuckDbSinkOptions.CheckpointThreshold"/>). The sink sets 256MB.
/// How deletes work: the HNSW index only marks deleted rows. A replaced or deleted chunk sets a
/// flag, and the next search runs PRAGMA hnsw_compact_index once first, so a search never has to
/// skip over many dead entries and loading is never slowed down by compaction.
/// How payload is stored: chunk_id is the UUID primary key, and doc_key, table_name, origin,
/// ordinal, chunk_text and a JSON metadata column sit beside the FLOAT[n] vector, so a search
/// returns everything it needs in one query.
/// Threading: each collection has one write connection and a bounded pool of search connections
/// (<see cref="DuckDbStore"/>). DuckDB's C API documents that connections are thread-safe but locked
/// while they query, and recommends one connection per thread for parallel performance, so searches
/// rent a connection each and run side by side (<see cref="DuckDbSinkOptions.MaxSearchConnections"/>).
/// Writes, counts, readiness checks and index compaction take exclusive use of the collection, so
/// they run one at a time and never overlap a search. With
/// <see cref="DuckDbSinkOptions.SerializeSearches"/> every search runs on the write connection, one
/// at a time, and the index description says so. Different collections do not block each other.
/// Readiness (<see cref="IIndexFinisher"/>): the HNSW index is updated inside each transaction, so
/// nothing is built after the writes return and <see cref="FinishLoadAsync"/> only confirms that.
/// The proof is the engine's own account (<see cref="GetIndexStateAsync"/>): duckdb_indexes() lists
/// the index, pragma_hnsw_index_info() says how many vectors it holds (it drops at once when rows
/// are deleted), and EXPLAIN of the default search shows an HNSW_INDEX_SCAN node on that index.
/// </summary>
public sealed class DuckDbSink : ISink, IExactSearchSink, IEngineDescription, IIndexFinisher, IDisposable
{
   #region Data Members

   private const string DEFAULT_ENGINE = "DuckDB + vss (HNSW, embedded, in process)";
   private const int MIN_SEARCH_CONNECTIONS = 8;
   private const string VECTOR_COLUMN = "embedding";
   private static readonly Regex COLLECTION_PATTERN = new( "^[A-Za-z0-9_]{1,200}$", RegexOptions.Compiled | RegexOptions.CultureInvariant );
   private static readonly Regex DIMENSION_PATTERN = new( @"FLOAT\[(\d+)\]", RegexOptions.Compiled | RegexOptions.CultureInvariant | RegexOptions.IgnoreCase );
   private static readonly Regex SIZE_PATTERN = new( @"^\d+(\.\d+)?\s?(B|KB|MB|GB|TB|KiB|MiB|GiB|TiB)$", RegexOptions.Compiled | RegexOptions.CultureInvariant | RegexOptions.IgnoreCase );

   private readonly DuckDbSinkOptions _options;
   private readonly ConcurrentDictionary<string, DuckDbStore> _stores = new( StringComparer.Ordinal );
   private readonly object _openLock = new();
   private string? _engine;
   private bool _disposed;

   #endregion Data Members

   #region Constructor

   /// <summary>
   /// Creates the sink with the default folder (~/gvb-data/engines/duckdb). Does no I/O, so the
   /// engine catalog can create it just to read its name and description.
   /// </summary>
   public DuckDbSink() : this( new DuckDbSinkOptions() )
   {
   }

   /// <summary>
   /// Creates the sink with explicit settings.
   /// </summary>
   /// <param name="options">Where the database files live and how the index is tuned.</param>
   public DuckDbSink( DuckDbSinkOptions options )
   {
      _options = options;
   }

   #endregion Constructor

   #region Public Methods

   /// <inheritdoc />
   public string Name => "duckdb";

   /// <inheritdoc />
   public string Engine => _engine ??= DetectEngine();

   /// <inheritdoc />
   public string IndexDescription =>
      $"HNSW (vss extension) FLOAT[n] metric=cosine m={_options.HnswM} ef_construction={_options.HnswEfConstruction}, ef_search={_options.HnswEfSearch} per connection, persistent (hnsw_enable_experimental_persistence=true, checkpoint_threshold={_options.CheckpointThreshold}); exact mode = array_cosine_similarity sequential scan; score = 1 - cosine distance; {ConcurrencyText()}";

   /// <inheritdoc />
   public string ComposeFile => "embedded";

   /// <inheritdoc />
   public string Durability =>
      $"DuckDB's write-ahead log is fsynced at every commit and no DuckDbSink setting changes that (checkpoint_threshold={_options.CheckpointThreshold} only spaces out the checkpoints that write the database file; "
      + "measured with strace: 42 WAL fsyncs for 40 single-record commits plus 2 setup statements), so an operating-system crash or power cut loses no committed row; "
      + "measured with kill -9 right after the last commit and a reopen: all 50, 300 and 2,000 rows and an HNSW index that counted the same number were recovered from the log; "
      + "not tested and documented by DuckDB: the HNSW index is file-backed only through hnsw_enable_experimental_persistence = true, which this sink turns on, and WAL recovery for such custom indexes is not complete, "
      + "so a crash during a checkpoint or a later commit can damage the index while the rows survive";

   /// <inheritdoc />
   public async Task<string> FinishLoadAsync( string collection, CancellationToken ct )
   {
      var clock = Stopwatch.StartNew();
      IndexState state = await GetIndexStateAsync( collection, ct );
      if( !state.Ready )
      {
         throw new InvalidOperationException( $"The DuckDB HNSW index for {collection} is not ready, and it is updated inside each transaction so waiting would not help: {state.Detail}" );
      }

      return $"HNSW is maintained inside every transaction, nothing to build; confirmed in {clock.Elapsed.TotalSeconds:F2} s: {state.Detail}";
   }

   /// <inheritdoc />
   public async Task<IndexState> GetIndexStateAsync( string collection, CancellationToken ct )
   {
      DuckDbStore store = RequireStore( collection );
      await store.EnterExclusiveAsync( ct );
      try
      {
         return await Task.Run( () => ReadIndexState( store, collection ), ct );
      }
      finally
      {
         store.ExitExclusive();
      }
   }

   /// <inheritdoc />
   public async Task<bool> EnsureCollectionAsync( string collection, int dimension, CancellationToken ct )
   {
      DuckDbStore store = OpenStore( collection, create: true );
      await store.EnterExclusiveAsync( ct );
      try
      {
         if( store.Dimension > 0 && store.Dimension != dimension )
         {
            throw new InvalidOperationException( $"The duckdb collection '{collection}' holds {store.Dimension}-value vectors but the embedder produces {dimension}. Reset the pipeline or use a new name." );
         }

         bool created = store.Dimension == 0;
         await Task.Run( () => CreateTableAndIndex( store, collection, dimension, created ), ct );
         store.Dimension = dimension;
         return created;
      }
      finally
      {
         store.ExitExclusive();
      }
   }

   /// <inheritdoc />
   public async Task UpsertAsync( string collection, IReadOnlyList<VectorRecord> records, CancellationToken ct )
   {
      if( records.Count == 0 )
      {
         return;
      }

      DuckDbStore store = RequireStore( collection );
      foreach( VectorRecord record in records )
      {
         CheckVector( store, collection, record.Vector );
      }

      await store.EnterExclusiveAsync( ct );
      try
      {
         await Task.Run( () =>
         {
            foreach( VectorRecord[] batch in LastWins( records ).Chunk( _options.UpsertBatch ) )
            {
               ct.ThrowIfCancellationRequested();
               WriteBatch( store, collection, batch );
            }
         }, ct );
      }
      finally
      {
         store.ExitExclusive();
      }
   }

   /// <inheritdoc />
   public async Task DeleteAsync( string collection, IReadOnlyList<Guid> chunkIds, CancellationToken ct )
   {
      if( chunkIds.Count == 0 )
      {
         return;
      }

      DuckDbStore store = RequireStore( collection );
      await store.EnterExclusiveAsync( ct );
      try
      {
         await Task.Run( () =>
         {
            foreach( Guid[] batch in chunkIds.Chunk( _options.UpsertBatch ) )
            {
               ct.ThrowIfCancellationRequested();
               if( DeleteIds( store, collection, batch ) > 0 )
               {
                  store.NeedsCompaction = true;
               }
            }
         }, ct );
      }
      finally
      {
         store.ExitExclusive();
      }
   }

   /// <inheritdoc />
   public async Task<long> CountAsync( string collection, CancellationToken ct )
   {
      DuckDbStore store = RequireStore( collection );
      await store.EnterExclusiveAsync( ct );
      try
      {
         return await Task.Run( () => CountRows( store, collection ), ct );
      }
      finally
      {
         store.ExitExclusive();
      }
   }

   /// <inheritdoc />
   public Task<IReadOnlyList<SearchHit>> SearchAsync( string collection, float[] vector, int top, CancellationToken ct )
   {
      return KnnAsync( collection, vector, top, exact: false, ct );
   }

   /// <inheritdoc />
   public Task<IReadOnlyList<SearchHit>> SearchExactAsync( string collection, float[] vector, int top, CancellationToken ct )
   {
      return KnnAsync( collection, vector, top, exact: true, ct );
   }

   /// <inheritdoc />
   public async Task DropCollectionAsync( string collection, CancellationToken ct )
   {
      CheckName( collection );
      DuckDbStore? store;
      lock( _openLock )
      {
         _stores.TryGetValue( collection, out store );
      }

      if( store != null )
      {
         await CloseStoreAsync( store, collection, ct );
      }

      foreach( string suffix in new[] { string.Empty, ".wal", ".tmp" } )
      {
         string file = FilePath( collection ) + suffix;
         if( File.Exists( file ) )
         {
            File.Delete( file );
         }
      }
   }

   /// <summary>
   /// How many search connections the sink has opened for a collection in this process. Why it is
   /// public: the tests and the report can show that 8 searchers really ran on 8 connections.
   /// </summary>
   /// <param name="collection">Collection name.</param>
   /// <returns>The count, or 0 when the collection is not open or searches are serialized.</returns>
   public int SearchConnectionsOpened( string collection )
   {
      return _stores.TryGetValue( collection, out DuckDbStore? store ) ? store.SearchConnectionsOpened : 0;
   }

   #endregion Public Methods

   #region Private Methods

   /// <summary>
   /// Runs the one query that serves both the default and the exact search, on a pooled search
   /// connection (or, when searches are serialized, on the write connection).
   /// </summary>
   /// <param name="collection">Collection name.</param>
   /// <param name="vector">Query vector.</param>
   /// <param name="top">How many hits to return.</param>
   /// <param name="exact">True for the sequential-scan query, false for the HNSW one.</param>
   /// <param name="ct">Cancellation.</param>
   /// <returns>Hits, best first, with cosine similarity scores.</returns>
   private async Task<IReadOnlyList<SearchHit>> KnnAsync( string collection, float[] vector, int top, bool exact, CancellationToken ct )
   {
      if( top <= 0 )
      {
         return Array.Empty<SearchHit>();
      }

      DuckDbStore store = RequireStore( collection );
      CheckVector( store, collection, vector );
      if( _options.SerializeSearches )
      {
         await store.EnterExclusiveAsync( ct );
         try
         {
            return await Task.Run( () =>
            {
               CompactIfNeeded( store, collection );
               return RunKnn( store.Connection, store, collection, vector, top, exact );
            }, ct );
         }
         finally
         {
            store.ExitExclusive();
         }
      }

      return await KnnPooledAsync( store, collection, vector, top, exact, ct );
   }

   /// <summary>
   /// Runs the KNN query on a rented search connection. If rows were deleted since the last
   /// compaction the lease is given back first, the index is compacted with exclusive use (a write
   /// to the index, so no search may overlap it), and a connection is rented again.
   /// Why the flag is read while holding a lease: only code with exclusive use changes it, so
   /// while this search holds a connection no delete can be half done.
   /// </summary>
   /// <param name="store">The collection's open store.</param>
   /// <param name="collection">Collection name.</param>
   /// <param name="vector">Query vector.</param>
   /// <param name="top">How many hits to return.</param>
   /// <param name="exact">True for the sequential-scan query, false for the HNSW one.</param>
   /// <param name="ct">Cancellation.</param>
   /// <returns>Hits, best first.</returns>
   private async Task<IReadOnlyList<SearchHit>> KnnPooledAsync( DuckDbStore store, string collection, float[] vector, int top, bool exact, CancellationToken ct )
   {
      SearchConnectionLease<DuckDBConnection> lease = await store.RentSearchAsync( ct );
      try
      {
         if( store.NeedsCompaction )
         {
            lease.Dispose();
            await store.EnterExclusiveAsync( ct );
            try
            {
               await Task.Run( () => CompactIfNeeded( store, collection ), ct );
            }
            finally
            {
               store.ExitExclusive();
            }

            lease = await store.RentSearchAsync( ct );
         }

         DuckDBConnection connection = lease.Connection;
         return await Task.Run( () => RunKnn( connection, store, collection, vector, top, exact ), ct );
      }
      finally
      {
         lease.Dispose();
      }
   }

   /// <summary>
   /// Compacts the HNSW index when deleted rows are waiting in it. The caller must have exclusive
   /// use of the collection.
   /// </summary>
   /// <param name="store">The collection's open store.</param>
   /// <param name="collection">Collection name.</param>
   private static void CompactIfNeeded( DuckDbStore store, string collection )
   {
      if( store.NeedsCompaction )
      {
         Run( store.Connection, $"PRAGMA hnsw_compact_index('{IndexName( collection )}');" );
         store.NeedsCompaction = false;
      }
   }

   /// <summary>
   /// Executes the KNN query. The default query orders by cosine DISTANCE (0 = identical, 2 =
   /// opposite) so the optimizer can use the HNSW index, and the score handed back is 1 - distance.
   /// The exact query orders by cosine SIMILARITY, which the index cannot answer, so DuckDB scans
   /// every row.
   /// </summary>
   /// <param name="connection">The connection to run on: a leased search connection, or the write connection under exclusive use.</param>
   /// <param name="store">The collection's open store, for its dimension.</param>
   /// <param name="collection">Collection name.</param>
   /// <param name="vector">Query vector.</param>
   /// <param name="k">How many neighbours to return.</param>
   /// <param name="exact">True for the sequential scan.</param>
   /// <returns>Hits, best first.</returns>
   private static IReadOnlyList<SearchHit> RunKnn( DuckDBConnection connection, DuckDbStore store, string collection, float[] vector, int k, bool exact )
   {
      using DuckDBCommand command = connection.CreateCommand();
      command.CommandText = KnnSql( store, collection, k, exact );
      command.Parameters.Add( new DuckDBParameter( vector ) );
      var hits = new List<SearchHit>( k );
      using DuckDBDataReader reader = command.ExecuteReader();
      while( reader.Read() )
      {
         double raw = Convert.ToDouble( reader.GetValue( 1 ), CultureInfo.InvariantCulture );
         hits.Add( new SearchHit( reader.GetGuid( 0 ), reader.GetString( 2 ), reader.GetString( 3 ), reader.GetString( 4 ), exact ? raw : 1.0 - raw ) );
      }

      return hits;
   }

   /// <summary>
   /// The nearest-neighbour statement, with the query vector as parameter $1. One place builds it
   /// so the search and the readiness check's EXPLAIN can never describe different statements.
   /// </summary>
   /// <param name="store">The collection's open store.</param>
   /// <param name="collection">Collection name.</param>
   /// <param name="k">How many neighbours to return.</param>
   /// <param name="exact">True for the similarity (sequential scan) form, false for the distance (HNSW) form.</param>
   /// <returns>The SQL text.</returns>
   private static string KnnSql( DuckDbStore store, string collection, int k, bool exact )
   {
      string function = exact ? "array_cosine_similarity" : "array_cosine_distance";
      string direction = exact ? "DESC" : "ASC";
      return $@"SELECT chunk_id, {function}( {VECTOR_COLUMN}, $1::FLOAT[{store.Dimension}] ) AS score, doc_key, table_name, chunk_text
FROM {Quote( collection )} ORDER BY score {direction} LIMIT {k.ToString( CultureInfo.InvariantCulture )};";
   }

   /// <summary>
   /// Reads the index state from DuckDB's catalog and its HNSW info pragma, and checks the plan of
   /// the default search. Runs under the collection's gate on its connection.
   /// </summary>
   /// <param name="store">The collection's open store.</param>
   /// <param name="collection">Collection name.</param>
   /// <returns>The engine's own account of its index.</returns>
   private static IndexState ReadIndexState( DuckDbStore store, string collection )
   {
      string index = IndexName( collection );
      long total = CountRows( store, collection );
      using( DuckDBCommand listed = store.Connection.CreateCommand() )
      {
         listed.CommandText = "SELECT sql FROM duckdb_indexes() WHERE table_name = $1 AND index_name = $2;";
         listed.Parameters.Add( new DuckDBParameter( TableName( collection ) ) );
         listed.Parameters.Add( new DuckDBParameter( index ) );
         if( listed.ExecuteScalar() is not string definition || !definition.Contains( "HNSW", StringComparison.OrdinalIgnoreCase ) )
         {
            return new IndexState( false, null, total, $"not ready: duckdb_indexes() lists no HNSW index {index} on {TableName( collection )}" );
         }
      }

      long indexed = ReadIndexedCount( store, index );
      bool usesIndex = PlanUsesHnsw( store, collection, index );
      var problems = new List<string>();
      if( indexed != total )
      {
         problems.Add( $"pragma_hnsw_index_info() counts {indexed} vectors in the index but the table holds {total}" );
      }

      if( !usesIndex )
      {
         problems.Add( "EXPLAIN of the default search shows no HNSW_INDEX_SCAN on the index" );
      }

      string facts = $"duckdb_indexes() lists {index}, pragma_hnsw_index_info() counts {indexed} vectors of {total} rows, EXPLAIN of the default search shows HNSW_INDEX_SCAN on it = {usesIndex}";
      return new IndexState( problems.Count == 0, indexed, total, problems.Count == 0 ? facts : $"not ready: {string.Join( "; ", problems )} ({facts})" );
   }

   /// <summary>
   /// Reads how many vectors the HNSW index holds from pragma_hnsw_index_info().
   /// </summary>
   /// <param name="store">The collection's open store.</param>
   /// <param name="index">Index name.</param>
   /// <returns>The vector count the index reports, or 0 when it reports nothing.</returns>
   private static long ReadIndexedCount( DuckDbStore store, string index )
   {
      using DuckDBCommand command = store.Connection.CreateCommand();
      command.CommandText = "SELECT count FROM pragma_hnsw_index_info() WHERE index_name = $1;";
      command.Parameters.Add( new DuckDBParameter( index ) );
      object? count = command.ExecuteScalar();
      return count is null or DBNull ? 0 : Convert.ToInt64( count, CultureInfo.InvariantCulture );
   }

   /// <summary>
   /// Plans the default search (nothing is executed) and reports whether the plan contains an
   /// HNSW_INDEX_SCAN node on the named index. The plan comes back as JSON because the text form
   /// wraps long names across lines.
   /// </summary>
   /// <param name="store">The collection's open store.</param>
   /// <param name="collection">Collection name.</param>
   /// <param name="index">Index name.</param>
   /// <returns>True when the plan scans that index.</returns>
   private static bool PlanUsesHnsw( DuckDbStore store, string collection, string index )
   {
      using DuckDBCommand command = store.Connection.CreateCommand();
      command.CommandText = "EXPLAIN ( FORMAT JSON ) " + KnnSql( store, collection, 10, exact: false );
      command.Parameters.Add( new DuckDBParameter( Enumerable.Repeat( 1f, store.Dimension ).ToArray() ) );
      using DuckDBDataReader reader = command.ExecuteReader();
      if( !reader.Read() )
      {
         return false;
      }

      using JsonDocument plan = JsonDocument.Parse( reader.GetString( 1 ) );
      return plan.RootElement.EnumerateArray().Any( node => ScansHnswIndex( node, index ) );
   }

   /// <summary>
   /// Walks an EXPLAIN (FORMAT JSON) tree looking for an HNSW_INDEX_SCAN node on the named index.
   /// </summary>
   /// <param name="node">A plan node.</param>
   /// <param name="index">Index name.</param>
   /// <returns>True when this node or any child scans that index.</returns>
   private static bool ScansHnswIndex( JsonElement node, string index )
   {
      if( node.TryGetProperty( "name", out JsonElement name ) && name.GetString() == "HNSW_INDEX_SCAN"
         && node.TryGetProperty( "extra_info", out JsonElement info ) && info.TryGetProperty( "HNSW Index", out JsonElement used ) && used.GetString() == index )
      {
         return true;
      }

      return node.TryGetProperty( "children", out JsonElement children ) && children.EnumerateArray().Any( child => ScansHnswIndex( child, index ) );
   }

   /// <summary>
   /// Creates the table and the HNSW index when they are missing. The index statement always runs
   /// (IF NOT EXISTS) so a collection whose first run stopped between the two statements gets
   /// its index on the next ensure instead of silently searching without one.
   /// </summary>
   /// <param name="store">The collection's open store.</param>
   /// <param name="collection">Collection name.</param>
   /// <param name="dimension">Vector length.</param>
   /// <param name="createTable">True when the table does not exist yet.</param>
   private void CreateTableAndIndex( DuckDbStore store, string collection, int dimension, bool createTable )
   {
      if( createTable )
      {
         Run( store.Connection, $@"CREATE TABLE {Quote( collection )} (
   chunk_id UUID PRIMARY KEY,
   doc_key VARCHAR NOT NULL,
   table_name VARCHAR NOT NULL,
   origin VARCHAR NOT NULL,
   ordinal INTEGER NOT NULL,
   chunk_text VARCHAR NOT NULL,
   metadata JSON,
   {VECTOR_COLUMN} FLOAT[{dimension.ToString( CultureInfo.InvariantCulture )}] NOT NULL );" );
      }

      Run( store.Connection, $@"CREATE INDEX IF NOT EXISTS {QuoteIndex( collection )} ON {Quote( collection )} USING HNSW ( {VECTOR_COLUMN} )
WITH ( metric = 'cosine', m = {_options.HnswM.ToString( CultureInfo.InvariantCulture )}, ef_construction = {_options.HnswEfConstruction.ToString( CultureInfo.InvariantCulture )} );" );
   }

   /// <summary>
   /// Replaces one batch inside a single transaction: delete any rows with the same chunk ids,
   /// then append the new ones with DuckDB's bulk appender. The appender has no upsert, and this
   /// pair makes a retried batch harmless. If rows were replaced the search compacts the index.
   /// </summary>
   /// <param name="store">The collection's open store.</param>
   /// <param name="collection">Collection name.</param>
   /// <param name="batch">Records to write, with no repeated chunk id.</param>
   private static void WriteBatch( DuckDbStore store, string collection, VectorRecord[] batch )
   {
      using DuckDBTransaction transaction = store.Connection.BeginTransaction();
      int replaced = DeleteIds( store, collection, batch.Select( r => r.Chunk.ChunkId ) );
      using( DuckDBAppender appender = store.Connection.CreateAppender( TableName( collection ) ) )
      {
         foreach( VectorRecord r in batch )
         {
            appender.CreateRow()
               .AppendValue( r.Chunk.ChunkId )
               .AppendValue( r.Document.DocKey )
               .AppendValue( r.Document.Table )
               .AppendValue( r.Document.Origin )
               .AppendValue( r.Chunk.Ordinal )
               .AppendValue( r.Chunk.Text )
               .AppendValue( JsonSerializer.Serialize( r.Document.Metadata ) )
               .AppendValue( r.Vector )
               .EndRow();
         }
      }

      transaction.Commit();
      if( replaced > 0 )
      {
         store.NeedsCompaction = true;
      }
   }

   /// <summary>
   /// Deletes chunk ids in one statement. The ids are written into the SQL as quoted UUID text
   /// because they come from <see cref="Guid"/> values, which can only hold hex digits and
   /// dashes, and a list that long is cheaper to send as text than as a bound parameter.
   /// </summary>
   /// <param name="store">The collection's open store.</param>
   /// <param name="collection">Collection name.</param>
   /// <param name="chunkIds">Chunk ids to remove. Ids that are not stored are ignored.</param>
   /// <returns>How many rows were really removed.</returns>
   private static int DeleteIds( DuckDbStore store, string collection, IEnumerable<Guid> chunkIds )
   {
      var sql = new StringBuilder( $"DELETE FROM {Quote( collection )} WHERE chunk_id IN (" );
      bool first = true;
      foreach( Guid id in chunkIds )
      {
         sql.Append( first ? "'" : ",'" ).Append( id.ToString( "D" ) ).Append( '\'' );
         first = false;
      }

      sql.Append( ");" );
      using DuckDBCommand command = store.Connection.CreateCommand();
      command.CommandText = sql.ToString();
      return command.ExecuteNonQuery();
   }

   /// <summary>
   /// Keeps only the last record for each chunk id, so a single call that repeats an id (the
   /// pipeline can resend one) cannot hit the primary key. This matches what a plain upsert does.
   /// </summary>
   /// <param name="records">The records as received.</param>
   /// <returns>The records with no repeated chunk id, in their original order.</returns>
   private static IEnumerable<VectorRecord> LastWins( IReadOnlyList<VectorRecord> records )
   {
      var last = new Dictionary<Guid, int>( records.Count );
      for( int i = 0; i < records.Count; i++ )
      {
         last[records[i].Chunk.ChunkId] = i;
      }

      if( last.Count == records.Count )
      {
         return records;
      }

      return records.Where( ( r, i ) => last[r.Chunk.ChunkId] == i );
   }

   /// <summary>
   /// Counts the rows in the collection's table exactly. DuckDB is not eventually consistent,
   /// so no flush is needed first.
   /// </summary>
   /// <param name="store">The collection's open store.</param>
   /// <param name="collection">Collection name.</param>
   /// <returns>The number of stored chunks.</returns>
   private static long CountRows( DuckDbStore store, string collection )
   {
      using DuckDBCommand command = store.Connection.CreateCommand();
      command.CommandText = $"SELECT count(*) FROM {Quote( collection )};";
      return Convert.ToInt64( command.ExecuteScalar(), CultureInfo.InvariantCulture );
   }

   /// <summary>
   /// Returns the open store for a collection that already exists, opening its file if this
   /// process has not yet. Never creates a file.
   /// </summary>
   /// <param name="collection">Collection name.</param>
   /// <returns>The open store, with its dimension known.</returns>
   private DuckDbStore RequireStore( string collection )
   {
      DuckDbStore store = OpenStore( collection, create: false );
      if( store.Dimension == 0 )
      {
         throw new InvalidOperationException( $"The duckdb collection '{collection}' has no table yet. Run the pipeline first." );
      }

      return store;
   }

   /// <summary>
   /// Opens (or returns the already open) connection for a collection. The first statement is
   /// always LOAD vss: a file that holds an HNSW index cannot be modified by a connection that
   /// has not loaded it. Then it turns on file-backed HNSW, applies the memory limit and ef_search,
   /// and reads the stored vector dimension.
   /// </summary>
   /// <param name="collection">Collection name.</param>
   /// <param name="create">True to create the file when it is missing.</param>
   /// <returns>The store; its Dimension is 0 when the file has no table yet.</returns>
   private DuckDbStore OpenStore( string collection, bool create )
   {
      CheckName( collection );
      ObjectDisposedException.ThrowIf( _disposed, this );
      lock( _openLock )
      {
         if( _stores.TryGetValue( collection, out DuckDbStore? known ) )
         {
            return known;
         }

         string file = FilePath( collection );
         if( !create && !File.Exists( file ) )
         {
            throw new InvalidOperationException( $"The duckdb collection '{collection}' does not exist. Run the pipeline first." );
         }

         CheckSize( _options.MemoryLimit, "memory limit" );
         CheckSize( _options.CheckpointThreshold, "checkpoint threshold" );

         Directory.CreateDirectory( _options.DirectoryPath );
         string connectionString = $"Data Source={file}";
         var connection = new DuckDBConnection( connectionString );
         try
         {
            connection.Open();
            PrepareConnection( connection );
            Run( connection, $"SET memory_limit = '{_options.MemoryLimit}';" );
            Run( connection, $"SET checkpoint_threshold = '{_options.CheckpointThreshold}';" );
            var store = new DuckDbStore( connection, () => OpenSearchConnection( connectionString ), MaxSearchConnections(), WaitLimit(), $"duckdb collection '{collection}'" )
            {
               Dimension = ReadDimension( connection, collection )
            };
            _stores[collection] = store;
            return store;
         }
         catch
         {
            connection.Dispose();
            throw;
         }
      }
   }

   /// <summary>
   /// Runs the per-connection setup every connection of this sink gets, in this order: load vss
   /// (first, because a file with an HNSW index cannot be used by a connection that has not),
   /// turn on file-backed HNSW, set ef_search. Why every connection repeats it: DuckDB keeps
   /// ef_search per connection (measured: setting it on one connection leaves another at its
   /// default), and a new connection to a file does not know the vss settings until it loads vss.
   /// </summary>
   /// <param name="connection">An open connection.</param>
   private void PrepareConnection( DuckDBConnection connection )
   {
      LoadExtension( connection );
      Run( connection, "SET hnsw_enable_experimental_persistence = true;" );
      Run( connection, $"SET hnsw_ef_search = {_options.HnswEfSearch.ToString( CultureInfo.InvariantCulture )};" );
   }

   /// <summary>
   /// Opens one more search connection on a collection's file. DuckDB.NET gives every connection
   /// opened on the same file name in this process the same database instance, so the new
   /// connection sees what the write connection committed.
   /// </summary>
   /// <param name="connectionString">The exact connection string the write connection used, because the instance is found by it.</param>
   /// <returns>The prepared connection.</returns>
   private DuckDBConnection OpenSearchConnection( string connectionString )
   {
      var connection = new DuckDBConnection( connectionString );
      try
      {
         connection.Open();
         PrepareConnection( connection );
         return connection;
      }
      catch
      {
         connection.Dispose();
         throw;
      }
   }

   /// <summary>
   /// The search connection limit: the configured value, never below the benchmark's widest pass.
   /// </summary>
   /// <returns>Connections.</returns>
   private int MaxSearchConnections()
   {
      return Math.Max( MIN_SEARCH_CONNECTIONS, _options.MaxSearchConnections );
   }

   /// <summary>
   /// The longest any wait for a connection or for exclusive use may last.
   /// </summary>
   /// <returns>At least one second.</returns>
   private TimeSpan WaitLimit()
   {
      return TimeSpan.FromSeconds( Math.Max( 1, _options.ConnectionWaitSeconds ) );
   }

   /// <summary>
   /// The sentence about concurrency that the index description ends with.
   /// </summary>
   /// <returns>Text for the report.</returns>
   private string ConcurrencyText()
   {
      if( _options.SerializeSearches )
      {
         return "searches run one at a time (single connection)";
      }

      return $"searches run concurrently, one connection per searcher (opened as searchers arrive, at most {MaxSearchConnections()}), writes run one at a time and never overlap a search";
   }

   /// <summary>
   /// Closes one collection's connections once nothing is running on it, and forgets the store.
   /// A store that another caller already closed is left alone.
   /// </summary>
   /// <param name="store">The store to close.</param>
   /// <param name="collection">Collection name.</param>
   /// <param name="ct">Cancellation.</param>
   private async Task CloseStoreAsync( DuckDbStore store, string collection, CancellationToken ct )
   {
      try
      {
         await store.EnterExclusiveAsync( ct );
      }
      catch( ObjectDisposedException )
      {
         return;
      }

      try
      {
         lock( _openLock )
         {
            _stores.TryRemove( collection, out _ );
         }

         store.CloseConnections();
      }
      finally
      {
         store.ExitExclusive();
      }
   }

   /// <summary>
   /// Refuses a size setting that is not a number and a unit, because the text is written into a
   /// SET statement.
   /// </summary>
   /// <param name="value">The setting, such as "8GB".</param>
   /// <param name="what">Plain name of the setting for the message.</param>
   private static void CheckSize( string value, string what )
   {
      if( !SIZE_PATTERN.IsMatch( value ) )
      {
         throw new InvalidOperationException( $"'{value}' is not a DuckDB {what}. Use a number and a unit, such as 256MB." );
      }
   }

   /// <summary>
   /// Loads the vss extension (HNSW index and the array distance functions) into a connection.
   /// </summary>
   /// <param name="connection">An open connection.</param>
   private static void LoadExtension( DuckDBConnection connection )
   {
      try
      {
         Run( connection, "LOAD vss;" );
      }
      catch( Exception ex )
      {
         throw new InvalidOperationException( $"DuckDB could not load the vss extension. It is built into the DuckDB.NET.Data.Full package, so a plain DuckDB.NET.Data reference will not work. {ex.Message}", ex );
      }
   }

   /// <summary>
   /// Builds the engine name with real versions by opening a throwaway in-memory database, so
   /// the report names the DuckDB and vss builds actually in use even before any collection
   /// exists. Falls back to a generic name if the extension cannot load.
   /// </summary>
   /// <returns>The display name.</returns>
   private static string DetectEngine()
   {
      try
      {
         using var connection = new DuckDBConnection( "Data Source=:memory:" );
         connection.Open();
         LoadExtension( connection );
         using DuckDBCommand command = connection.CreateCommand();
         command.CommandText = "SELECT version() || '|' || (SELECT extension_version FROM duckdb_extensions() WHERE extension_name = 'vss');";
         string[] versions = ( (string)command.ExecuteScalar()! ).Split( '|' );
         return $"DuckDB {versions[0].TrimStart( 'v' )} + vss {versions[1]} (HNSW, embedded, in process)";
      }
      catch( Exception )
      {
         return DEFAULT_ENGINE;
      }
   }

   /// <summary>
   /// Reads the vector dimension from the table's column type (FLOAT[1024]).
   /// </summary>
   /// <param name="connection">An open connection.</param>
   /// <param name="collection">Collection name.</param>
   /// <returns>The dimension, or 0 when the table does not exist.</returns>
   private static int ReadDimension( DuckDBConnection connection, string collection )
   {
      using DuckDBCommand command = connection.CreateCommand();
      command.CommandText = $"SELECT data_type FROM duckdb_columns() WHERE table_name = $1 AND column_name = '{VECTOR_COLUMN}';";
      command.Parameters.Add( new DuckDBParameter( TableName( collection ) ) );
      if( command.ExecuteScalar() is not string type )
      {
         return 0;
      }

      Match match = DIMENSION_PATTERN.Match( type );
      return match.Success ? int.Parse( match.Groups[1].Value, CultureInfo.InvariantCulture ) : 0;
   }

   /// <summary>
   /// Refuses a vector whose length differs from the collection's, or that holds NaN or infinity
   /// (cosine distance on those is meaningless and can confuse the index), with a plain message.
   /// </summary>
   /// <param name="store">The collection's open store.</param>
   /// <param name="collection">Collection name.</param>
   /// <param name="vector">The vector to check.</param>
   private static void CheckVector( DuckDbStore store, string collection, float[] vector )
   {
      if( vector.Length != store.Dimension )
      {
         throw new InvalidOperationException( $"A vector with {vector.Length} values was sent to the duckdb collection '{collection}', which holds {store.Dimension}-value vectors." );
      }

      foreach( float value in vector )
      {
         if( !float.IsFinite( value ) )
         {
            throw new InvalidOperationException( $"A vector sent to the duckdb collection '{collection}' holds a value that is not a finite number." );
         }
      }
   }

   /// <summary>
   /// Refuses collection names that could change the meaning of the SQL or the file name.
   /// Collection names are already sanitized to [a-z0-9_] by the pipeline; this is a backstop.
   /// </summary>
   /// <param name="collection">Collection name.</param>
   private static void CheckName( string collection )
   {
      if( !COLLECTION_PATTERN.IsMatch( collection ) )
      {
         throw new ArgumentException( $"'{collection}' is not a valid collection name. Use letters, digits and underscores only.", nameof( collection ) );
      }
   }

   /// <summary>
   /// Executes a statement that returns nothing.
   /// </summary>
   /// <param name="connection">An open connection.</param>
   /// <param name="sql">The statement.</param>
   private static void Run( DuckDBConnection connection, string sql )
   {
      using DuckDBCommand command = connection.CreateCommand();
      command.CommandText = sql;
      command.ExecuteNonQuery();
   }

   /// <summary>
   /// Full path of a collection's database file.
   /// </summary>
   /// <param name="collection">Collection name.</param>
   /// <returns>The absolute path.</returns>
   private string FilePath( string collection )
   {
      return Path.Combine( _options.DirectoryPath, $"{TableName( collection )}.duckdb" );
   }

   /// <summary>
   /// Table name for a collection, following the "gvb_" + collection convention.
   /// </summary>
   /// <param name="collection">Collection name.</param>
   /// <returns>The table name.</returns>
   private static string TableName( string collection )
   {
      return $"gvb_{collection}";
   }

   /// <summary>
   /// Name of the collection's HNSW index.
   /// </summary>
   /// <param name="collection">Collection name.</param>
   /// <returns>The index name.</returns>
   private static string IndexName( string collection )
   {
      return $"{TableName( collection )}_hnsw";
   }

   /// <summary>
   /// Double-quotes the table name. The collection name was already checked against a strict
   /// pattern, so no quote can appear in it.
   /// </summary>
   /// <param name="collection">Collection name.</param>
   /// <returns>The quoted table name.</returns>
   private static string Quote( string collection )
   {
      return $"\"{TableName( collection )}\"";
   }

   /// <summary>
   /// Double-quotes the index name, for the same reason as <see cref="Quote"/>.
   /// </summary>
   /// <param name="collection">Collection name.</param>
   /// <returns>The quoted index name.</returns>
   private static string QuoteIndex( string collection )
   {
      return $"\"{IndexName( collection )}\"";
   }

   /// <summary>
   /// Closes one store's connections when nothing runs on it within the wait limit.
   /// </summary>
   /// <param name="store">The store.</param>
   /// <returns>False when it stayed busy and was left open.</returns>
   private static bool TryCloseStore( DuckDbStore store )
   {
      try
      {
         store.EnterExclusiveAsync( CancellationToken.None ).GetAwaiter().GetResult();
      }
      catch( TimeoutException )
      {
         return false;
      }

      try
      {
         store.CloseConnections();
      }
      finally
      {
         store.ExitExclusive();
      }

      return true;
   }


   #endregion Private Methods

   #region IDisposable

   /// <summary>
   /// Closes every open connection once the searches and writes running on it have finished,
   /// waiting at most the connection wait limit for each collection. Closing the last connection
   /// to a file writes the write-ahead log back into the main file.
   /// </summary>
   /// <exception cref="TimeoutException">A collection was still busy after the limit, so its connections were left open.</exception>
   public void Dispose()
   {
      if( _disposed )
      {
         return;
      }

      _disposed = true;
      var busy = new List<string>();
      lock( _openLock )
      {
         foreach( KeyValuePair<string, DuckDbStore> pair in _stores )
         {
            if( !TryCloseStore( pair.Value ) )
            {
               busy.Add( pair.Key );
            }
         }

         _stores.Clear();
      }

      if( busy.Count > 0 )
      {
         throw new TimeoutException( $"The duckdb collection(s) {string.Join( ", ", busy )} stayed busy for {WaitLimit().TotalSeconds:F0} s while the sink was disposed, so their connections were left open." );
      }
   }

   #endregion IDisposable
}
