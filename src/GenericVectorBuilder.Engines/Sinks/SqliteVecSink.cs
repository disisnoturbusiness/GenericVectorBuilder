using System.Collections.Concurrent;
using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Text.RegularExpressions;
using GenericVectorBuilder.Core.Contracts;
using GenericVectorBuilder.Engines.Common;
using Microsoft.Data.Sqlite;

namespace GenericVectorBuilder.Engines.Sinks;

/// <summary>
/// Stores vectors in SQLite using the sqlite-vec extension: one database file per collection,
/// named gvb_{collection}.sqlite, holding one vec0 virtual table named gvb_{collection}.
/// How search works: vec0 has no ANN index. Every query compares the query vector with every
/// stored vector (float32, cosine distance) and keeps the best k. That makes the default search
/// exact, so this sink also implements <see cref="IExactSearchSink"/> with the very same query.
/// Why that is still worth benchmarking: it is the cheapest possible vector store (a file, no
/// server, no index to build) and sets the floor that every indexed engine has to beat.
/// How payload is stored: the chunk id is the table's text primary key, the vector is the
/// float[n] column, and doc key, table, origin, ordinal, text and metadata JSON are auxiliary
/// columns. Auxiliary columns hold values of any length and come back with the KNN query, so a
/// search needs no second lookup.
/// Threading: a SQLite connection is not safe to share, so each collection has ONE connection
/// and a gate that lets one operation run at a time. Different collections do not block each
/// other.
/// Why the connection is kept open: the extension must be loaded into every connection, and the
/// benchmark should time the search, not the connection setup.
/// Readiness (<see cref="IIndexFinisher"/>): there is no index to finish, so
/// <see cref="GetIndexStateAsync"/> reports ready with the detail "no index, exact scan by design",
/// plus what can be read from the engine to back that up: the table really is a vec0 virtual
/// table, the exact row count, and the plan SQLite gives the search.
/// </summary>
public sealed class SqliteVecSink : ISink, IExactSearchSink, IEngineDescription, IIndexFinisher, IDisposable
{
   #region Data Members

   private const int WRITE_BATCH = 2000;
   private const int MAX_K = 4096;
   private const string DEFAULT_ENGINE = "SQLite + sqlite-vec (vec0, embedded, in process)";
   private static readonly Regex COLLECTION_PATTERN = new( "^[A-Za-z0-9_]{1,200}$", RegexOptions.Compiled | RegexOptions.CultureInvariant );
   private static readonly Regex DIMENSION_PATTERN = new( @"embedding\s+float\[(\d+)\]", RegexOptions.Compiled | RegexOptions.CultureInvariant | RegexOptions.IgnoreCase );

   private readonly SqliteVecSinkOptions _options;
   private readonly ConcurrentDictionary<string, SqliteVecStore> _stores = new( StringComparer.Ordinal );
   private readonly object _openLock = new();
   private string? _engine;
   private bool _disposed;

   #endregion Data Members

   #region Constructor

   /// <summary>
   /// Creates the sink with the default folder (~/gvb-data/engines/sqlitevec). Does no I/O, so
   /// the engine catalog can create it just to read its name and description.
   /// </summary>
   public SqliteVecSink() : this( new SqliteVecSinkOptions() )
   {
   }

   /// <summary>
   /// Creates the sink with explicit settings.
   /// </summary>
   /// <param name="options">Where the database files live.</param>
   public SqliteVecSink( SqliteVecSinkOptions options )
   {
      _options = options;
   }

   #endregion Constructor

   #region Public Methods

   /// <inheritdoc />
   public string Name => "sqlitevec";

   /// <inheritdoc />
   public string Engine => _engine ??= DetectEngine();

   /// <inheritdoc />
   public string IndexDescription => "vec0 brute-force scan, no ANN index (exact), float32, cosine distance, default chunk_size=1024; score = 1 - cosine distance";

   /// <inheritdoc />
   public string ComposeFile => "embedded";

   /// <inheritdoc />
   public string Durability =>
      "PRAGMA journal_mode=WAL and PRAGMA synchronous=NORMAL (set by this sink when it opens the file): each commit is appended to the -wal file and handed to the operating system, "
      + "but the WAL is fsynced only when SQLite checkpoints it (measured with strace: 40 single-record commits caused 3 WAL fsyncs), so a crash of the process loses nothing "
      + "(measured with kill -9 and a reopen: all 50, 300 and 2,000 rows were there), while an operating-system crash or power cut can lose the newest commits since the last checkpoint and leaves the database consistent";

   /// <inheritdoc />
   public async Task<string> FinishLoadAsync( string collection, CancellationToken ct )
   {
      var clock = Stopwatch.StartNew();
      IndexState state = await GetIndexStateAsync( collection, ct );
      if( !state.Ready )
      {
         throw new InvalidOperationException( $"The sqlitevec collection '{collection}' is not usable: {state.Detail}" );
      }

      return $"nothing to build, every search scans all vectors; checked in {clock.Elapsed.TotalSeconds:F2} s: {state.Detail}";
   }

   /// <inheritdoc />
   public async Task<IndexState> GetIndexStateAsync( string collection, CancellationToken ct )
   {
      SqliteVecStore store = RequireStore( collection );
      await store.Gate.WaitAsync( ct );
      try
      {
         return await Task.Run( () => ReadIndexState( store, collection ), ct );
      }
      finally
      {
         store.Gate.Release();
      }
   }

   /// <inheritdoc />
   public async Task<bool> EnsureCollectionAsync( string collection, int dimension, CancellationToken ct )
   {
      SqliteVecStore store = OpenStore( collection, create: true );
      await store.Gate.WaitAsync( ct );
      try
      {
         if( store.Dimension > 0 && store.Dimension != dimension )
         {
            throw new InvalidOperationException( $"The sqlitevec collection '{collection}' holds {store.Dimension}-value vectors but the embedder produces {dimension}. Reset the pipeline or use a new name." );
         }

         if( store.Dimension > 0 )
         {
            return false;
         }

         string ddl = $@"CREATE VIRTUAL TABLE {Quote( collection )} USING vec0(
   chunk_id text primary key,
   embedding float[{dimension}] distance_metric=cosine,
   +doc_key text,
   +table_name text,
   +origin text,
   +ordinal integer,
   +chunk_text text,
   +metadata text );";
         using SqliteCommand command = store.Connection.CreateCommand();
         command.CommandText = ddl;
         command.ExecuteNonQuery();
         store.Dimension = dimension;
         return true;
      }
      finally
      {
         store.Gate.Release();
      }
   }

   /// <inheritdoc />
   public async Task UpsertAsync( string collection, IReadOnlyList<VectorRecord> records, CancellationToken ct )
   {
      if( records.Count == 0 )
      {
         return;
      }

      SqliteVecStore store = RequireStore( collection );
      foreach( VectorRecord record in records )
      {
         CheckVector( store, collection, record.Vector );
      }

      await store.Gate.WaitAsync( ct );
      try
      {
         await Task.Run( () =>
         {
            foreach( VectorRecord[] batch in records.Chunk( WRITE_BATCH ) )
            {
               ct.ThrowIfCancellationRequested();
               WriteBatch( store, collection, batch );
            }
         }, ct );
      }
      finally
      {
         store.Gate.Release();
      }
   }

   /// <inheritdoc />
   public async Task DeleteAsync( string collection, IReadOnlyList<Guid> chunkIds, CancellationToken ct )
   {
      if( chunkIds.Count == 0 )
      {
         return;
      }

      SqliteVecStore store = RequireStore( collection );
      await store.Gate.WaitAsync( ct );
      try
      {
         await Task.Run( () =>
         {
            foreach( Guid[] batch in chunkIds.Chunk( WRITE_BATCH ) )
            {
               ct.ThrowIfCancellationRequested();
               DeleteBatch( store, collection, batch );
            }
         }, ct );
      }
      finally
      {
         store.Gate.Release();
      }
   }

   /// <inheritdoc />
   public async Task<long> CountAsync( string collection, CancellationToken ct )
   {
      SqliteVecStore store = RequireStore( collection );
      await store.Gate.WaitAsync( ct );
      try
      {
         return await Task.Run( () => CountRows( store, collection ), ct );
      }
      finally
      {
         store.Gate.Release();
      }
   }

   /// <inheritdoc />
   public Task<IReadOnlyList<SearchHit>> SearchAsync( string collection, float[] vector, int top, CancellationToken ct )
   {
      return KnnAsync( collection, vector, top, ct );
   }

   /// <inheritdoc />
   public Task<IReadOnlyList<SearchHit>> SearchExactAsync( string collection, float[] vector, int top, CancellationToken ct )
   {
      return KnnAsync( collection, vector, top, ct );
   }

   /// <inheritdoc />
   public async Task DropCollectionAsync( string collection, CancellationToken ct )
   {
      CheckName( collection );
      SqliteVecStore? store;
      lock( _openLock )
      {
         _stores.TryRemove( collection, out store );
      }

      if( store != null )
      {
         await store.Gate.WaitAsync( ct );
         store.Connection.Dispose();
         store.Gate.Release();
      }

      foreach( string suffix in new[] { string.Empty, "-wal", "-shm", "-journal" } )
      {
         string file = FilePath( collection ) + suffix;
         if( File.Exists( file ) )
         {
            File.Delete( file );
         }
      }
   }

   #endregion Public Methods

   #region Private Methods

   /// <summary>
   /// Reads what SQLite says about the collection's table: that it is a vec0 virtual table, how
   /// many rows it holds, and the plan of the search query. Runs under the collection's gate.
   /// </summary>
   /// <param name="store">The collection's open store.</param>
   /// <param name="collection">Collection name.</param>
   /// <returns>Ready when the table is a vec0 table, with the evidence in the detail.</returns>
   private static IndexState ReadIndexState( SqliteVecStore store, string collection )
   {
      long total = CountRows( store, collection );
      using SqliteCommand definition = store.Connection.CreateCommand();
      definition.CommandText = "SELECT sql FROM sqlite_master WHERE type = 'table' AND name = $name;";
      definition.Parameters.AddWithValue( "$name", TableName( collection ) );
      if( definition.ExecuteScalar() is not string ddl || !ddl.Contains( "vec0", StringComparison.OrdinalIgnoreCase ) )
      {
         return new IndexState( false, null, total, $"not ready: {TableName( collection )} is not a vec0 virtual table in sqlite_master" );
      }

      using SqliteCommand plan = store.Connection.CreateCommand();
      plan.CommandText = $"EXPLAIN QUERY PLAN SELECT chunk_id, distance FROM {Quote( collection )} WHERE embedding MATCH $query AND k = $k ORDER BY distance;";
      plan.Parameters.AddWithValue( "$query", ToBlob( new float[store.Dimension] ) );
      plan.Parameters.AddWithValue( "$k", 10 );
      var steps = new List<string>();
      using( SqliteDataReader reader = plan.ExecuteReader() )
      {
         while( reader.Read() )
         {
            steps.Add( reader.GetString( 3 ) );
         }
      }

      return new IndexState( true, null, total, $"no index, exact scan by design: {TableName( collection )} is a vec0 virtual table holding {total} vectors and every search compares all of them (EXPLAIN QUERY PLAN: {string.Join( " | ", steps )})" );
   }

   /// <summary>
   /// Runs the one KNN query that serves both the default and the exact search.
   /// </summary>
   /// <param name="collection">Collection name.</param>
   /// <param name="vector">Query vector.</param>
   /// <param name="top">How many hits to return.</param>
   /// <param name="ct">Cancellation.</param>
   /// <returns>Hits, best first, with cosine similarity scores.</returns>
   private async Task<IReadOnlyList<SearchHit>> KnnAsync( string collection, float[] vector, int top, CancellationToken ct )
   {
      if( top <= 0 )
      {
         return Array.Empty<SearchHit>();
      }

      SqliteVecStore store = RequireStore( collection );
      CheckVector( store, collection, vector );
      await store.Gate.WaitAsync( ct );
      try
      {
         return await Task.Run( () => RunKnn( store, collection, vector, Math.Min( top, MAX_K ) ), ct );
      }
      finally
      {
         store.Gate.Release();
      }
   }

   /// <summary>
   /// Executes the KNN query. vec0 returns cosine DISTANCE (0 = identical, 2 = opposite), so
   /// the score handed back is 1 - distance, the cosine similarity every sink reports.
   /// </summary>
   /// <param name="store">The collection's open store.</param>
   /// <param name="collection">Collection name.</param>
   /// <param name="vector">Query vector.</param>
   /// <param name="k">How many neighbours to return, at most 4096 (a vec0 limit).</param>
   /// <returns>Hits, best first.</returns>
   private static IReadOnlyList<SearchHit> RunKnn( SqliteVecStore store, string collection, float[] vector, int k )
   {
      string sql = $@"SELECT chunk_id, distance, doc_key, table_name, chunk_text FROM {Quote( collection )}
WHERE embedding MATCH $query AND k = $k ORDER BY distance;";
      using SqliteCommand command = store.Connection.CreateCommand();
      command.CommandText = sql;
      command.Parameters.AddWithValue( "$query", ToBlob( vector ) );
      command.Parameters.AddWithValue( "$k", k );
      var hits = new List<SearchHit>( k );
      using SqliteDataReader reader = command.ExecuteReader();
      while( reader.Read() )
      {
         hits.Add( new SearchHit( Guid.Parse( reader.GetString( 0 ) ), reader.GetString( 2 ), reader.GetString( 3 ), reader.GetString( 4 ), 1.0 - reader.GetDouble( 1 ) ) );
      }

      return hits;
   }

   /// <summary>
   /// Replaces one batch inside a single transaction: delete any existing row with the same
   /// chunk id, then insert the new one. vec0 has no upsert statement, and this pair makes a
   /// retried batch harmless.
   /// </summary>
   /// <param name="store">The collection's open store.</param>
   /// <param name="collection">Collection name.</param>
   /// <param name="batch">Records to write.</param>
   private static void WriteBatch( SqliteVecStore store, string collection, VectorRecord[] batch )
   {
      using SqliteTransaction transaction = store.Connection.BeginTransaction();
      using SqliteCommand delete = store.Connection.CreateCommand();
      delete.Transaction = transaction;
      delete.CommandText = $"DELETE FROM {Quote( collection )} WHERE chunk_id = $id;";
      SqliteParameter deleteId = delete.Parameters.Add( "$id", SqliteType.Text );

      using SqliteCommand insert = store.Connection.CreateCommand();
      insert.Transaction = transaction;
      insert.CommandText = $@"INSERT INTO {Quote( collection )} ( chunk_id, embedding, doc_key, table_name, origin, ordinal, chunk_text, metadata )
VALUES ( $id, $embedding, $docKey, $table, $origin, $ordinal, $text, $metadata );";
      SqliteParameter[] p =
      {
         insert.Parameters.Add( "$id", SqliteType.Text ),
         insert.Parameters.Add( "$embedding", SqliteType.Blob ),
         insert.Parameters.Add( "$docKey", SqliteType.Text ),
         insert.Parameters.Add( "$table", SqliteType.Text ),
         insert.Parameters.Add( "$origin", SqliteType.Text ),
         insert.Parameters.Add( "$ordinal", SqliteType.Integer ),
         insert.Parameters.Add( "$text", SqliteType.Text ),
         insert.Parameters.Add( "$metadata", SqliteType.Text )
      };

      foreach( VectorRecord r in batch )
      {
         string id = r.Chunk.ChunkId.ToString( "D" );
         deleteId.Value = id;
         delete.ExecuteNonQuery();
         p[0].Value = id;
         p[1].Value = ToBlob( r.Vector );
         p[2].Value = r.Document.DocKey;
         p[3].Value = r.Document.Table;
         p[4].Value = r.Document.Origin;
         p[5].Value = r.Chunk.Ordinal;
         p[6].Value = r.Chunk.Text;
         p[7].Value = JsonSerializer.Serialize( r.Document.Metadata );
         insert.ExecuteNonQuery();
      }

      transaction.Commit();
   }

   /// <summary>
   /// Deletes one batch of chunk ids inside a single transaction. Ids that are not stored are
   /// ignored.
   /// </summary>
   /// <param name="store">The collection's open store.</param>
   /// <param name="collection">Collection name.</param>
   /// <param name="batch">Chunk ids to remove.</param>
   private static void DeleteBatch( SqliteVecStore store, string collection, Guid[] batch )
   {
      using SqliteTransaction transaction = store.Connection.BeginTransaction();
      using SqliteCommand delete = store.Connection.CreateCommand();
      delete.Transaction = transaction;
      delete.CommandText = $"DELETE FROM {Quote( collection )} WHERE chunk_id = $id;";
      SqliteParameter id = delete.Parameters.Add( "$id", SqliteType.Text );
      foreach( Guid chunkId in batch )
      {
         id.Value = chunkId.ToString( "D" );
         delete.ExecuteNonQuery();
      }

      transaction.Commit();
   }

   /// <summary>
   /// Counts the rows in the collection's table exactly. SQLite is not eventually consistent,
   /// so no flush is needed first.
   /// </summary>
   /// <param name="store">The collection's open store.</param>
   /// <param name="collection">Collection name.</param>
   /// <returns>The number of stored chunks.</returns>
   private static long CountRows( SqliteVecStore store, string collection )
   {
      using SqliteCommand command = store.Connection.CreateCommand();
      command.CommandText = $"SELECT count(*) FROM {Quote( collection )};";
      return Convert.ToInt64( command.ExecuteScalar(), CultureInfo.InvariantCulture );
   }

   /// <summary>
   /// Returns the open store for a collection that already exists, opening its file if this
   /// process has not yet. Never creates a file.
   /// </summary>
   /// <param name="collection">Collection name.</param>
   /// <returns>The open store, with its dimension known.</returns>
   private SqliteVecStore RequireStore( string collection )
   {
      SqliteVecStore store = OpenStore( collection, create: false );
      if( store.Dimension == 0 )
      {
         throw new InvalidOperationException( $"The sqlitevec collection '{collection}' has no table yet. Run the pipeline first." );
      }

      return store;
   }

   /// <summary>
   /// Opens (or returns the already open) connection for a collection: loads the sqlite-vec
   /// extension, switches the file to WAL journaling and reads the stored vector dimension.
   /// </summary>
   /// <param name="collection">Collection name.</param>
   /// <param name="create">True to create the file when it is missing.</param>
   /// <returns>The store; its Dimension is 0 when the file has no table yet.</returns>
   private SqliteVecStore OpenStore( string collection, bool create )
   {
      CheckName( collection );
      ObjectDisposedException.ThrowIf( _disposed, this );
      lock( _openLock )
      {
         if( _stores.TryGetValue( collection, out SqliteVecStore? known ) )
         {
            return known;
         }

         string file = FilePath( collection );
         if( !create && !File.Exists( file ) )
         {
            throw new InvalidOperationException( $"The sqlitevec collection '{collection}' does not exist. Run the pipeline first." );
         }

         Directory.CreateDirectory( _options.DirectoryPath );
         var builder = new SqliteConnectionStringBuilder { DataSource = file, Pooling = false };
         var connection = new SqliteConnection( builder.ConnectionString );
         try
         {
            connection.Open();
            LoadExtension( connection );
            Run( connection, "PRAGMA journal_mode=WAL;" );
            Run( connection, "PRAGMA synchronous=NORMAL;" );
            var store = new SqliteVecStore( connection ) { Dimension = ReadDimension( connection, collection ) };
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
   /// Loads the sqlite-vec extension into a connection.
   /// </summary>
   /// <param name="connection">An open connection.</param>
   private static void LoadExtension( SqliteConnection connection )
   {
      try
      {
         connection.LoadVector();
      }
      catch( Exception ex )
      {
         throw new InvalidOperationException( $"SQLite could not load the sqlite-vec extension (vec0). It ships in the sqlite-vec package under runtimes/linux-x64/native. {ex.Message}", ex );
      }
   }

   /// <summary>
   /// Builds the engine name with real versions by opening a throwaway in-memory database, so
   /// the report names the SQLite and sqlite-vec builds actually in use even before any
   /// collection exists. Falls back to a generic name if the extension cannot load.
   /// </summary>
   /// <returns>The display name.</returns>
   private static string DetectEngine()
   {
      try
      {
         using var connection = new SqliteConnection( "Data Source=:memory:" );
         connection.Open();
         LoadExtension( connection );
         using SqliteCommand command = connection.CreateCommand();
         command.CommandText = "SELECT sqlite_version() || '|' || vec_version();";
         string[] versions = ( (string)command.ExecuteScalar()! ).Split( '|' );
         return $"SQLite {versions[0]} + sqlite-vec {versions[1].TrimStart( 'v' )} (vec0, embedded, in process)";
      }
      catch( Exception )
      {
         return DEFAULT_ENGINE;
      }
   }

   /// <summary>
   /// Reads the vector dimension from the table's stored CREATE statement.
   /// </summary>
   /// <param name="connection">An open connection.</param>
   /// <param name="collection">Collection name.</param>
   /// <returns>The dimension, or 0 when the table does not exist.</returns>
   private static int ReadDimension( SqliteConnection connection, string collection )
   {
      using SqliteCommand command = connection.CreateCommand();
      command.CommandText = "SELECT sql FROM sqlite_master WHERE type = 'table' AND name = $name;";
      command.Parameters.AddWithValue( "$name", TableName( collection ) );
      if( command.ExecuteScalar() is not string ddl )
      {
         return 0;
      }

      Match match = DIMENSION_PATTERN.Match( ddl );
      return match.Success ? int.Parse( match.Groups[1].Value, CultureInfo.InvariantCulture ) : 0;
   }

   /// <summary>
   /// Refuses a vector whose length differs from the collection's, with a plain message
   /// instead of the extension's terse one.
   /// </summary>
   /// <param name="store">The collection's open store.</param>
   /// <param name="collection">Collection name.</param>
   /// <param name="vector">The vector to check.</param>
   private static void CheckVector( SqliteVecStore store, string collection, float[] vector )
   {
      if( vector.Length != store.Dimension )
      {
         throw new InvalidOperationException( $"A vector with {vector.Length} values was sent to the sqlitevec collection '{collection}', which holds {store.Dimension}-value vectors." );
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
   /// Executes a statement that returns nothing, or ignores the single value it returns.
   /// </summary>
   /// <param name="connection">An open connection.</param>
   /// <param name="sql">The statement.</param>
   private static void Run( SqliteConnection connection, string sql )
   {
      using SqliteCommand command = connection.CreateCommand();
      command.CommandText = sql;
      command.ExecuteScalar();
   }

   /// <summary>
   /// Converts a vector to the little-endian float32 blob that vec0 expects.
   /// </summary>
   /// <param name="vector">The vector.</param>
   /// <returns>The raw bytes.</returns>
   private static byte[] ToBlob( float[] vector )
   {
      return MemoryMarshal.AsBytes( vector.AsSpan() ).ToArray();
   }

   /// <summary>
   /// Full path of a collection's database file.
   /// </summary>
   /// <param name="collection">Collection name.</param>
   /// <returns>The absolute path.</returns>
   private string FilePath( string collection )
   {
      return Path.Combine( _options.DirectoryPath, $"{TableName( collection )}.sqlite" );
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
   /// Double-quotes the table name. The collection name was already checked against a strict
   /// pattern, so no quote can appear in it.
   /// </summary>
   /// <param name="collection">Collection name.</param>
   /// <returns>The quoted table name.</returns>
   private static string Quote( string collection )
   {
      return $"\"{TableName( collection )}\"";
   }

   #endregion Private Methods

   #region IDisposable

   /// <summary>
   /// Closes every open connection. Closing the last connection to a WAL database folds the
   /// log back into the main file.
   /// </summary>
   public void Dispose()
   {
      if( _disposed )
      {
         return;
      }

      _disposed = true;
      lock( _openLock )
      {
         foreach( SqliteVecStore store in _stores.Values )
         {
            store.Connection.Dispose();
         }

         _stores.Clear();
      }
   }

   #endregion IDisposable
}
