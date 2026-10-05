using System.Data;
using GenericVectorBuilder.Core.Contracts;
using GenericVectorBuilder.Core.Sinks;
using GenericVectorBuilder.Engines.Common;
using Microsoft.Data.SqlClient;

namespace GenericVectorBuilder.Bench.Targets;

/// <summary>
/// SQL Server 2025 searched through a DiskANN vector index (VECTOR_SEARCH), as a benchmark
/// target beside the builder's exact VECTOR_DISTANCE sink.
/// Why a throwaway database of its own: on SQL Server for Linux the DiskANN index is a preview
/// feature, it needs PREVIEW_FEATURES switched on for the database, and it makes the table
/// read-only. None of that may touch the builder's real database, so this target turns the
/// feature on only in its own database (<see cref="DATABASE"/> unless a test names another),
/// which exists for the benchmark alone and is dropped when its last table is.
/// How a load works: rows are written with the builder's own SQL sink (bulk copy + MERGE, the
/// same loader and speed as the "sql" target), then <see cref="FinishLoadAsync"/> copies the
/// table into gvb_{collection}_ann and builds the index there, timed together.
/// Why the copy: a vector index needs a clustered primary key on one 4-byte INT column
/// (measured on CU9: "must have a clustered primary key on a single 4 byte INT column"), and
/// the builder's tables are keyed by a GUID chunk id. The copy gets an INT IDENTITY key and
/// keeps ChunkId as a plain column, so hits map back to the same ids.
/// Which table each mode searches: BOTH the default search (VECTOR_SEARCH through the index)
/// and the exact mode (VECTOR_DISTANCE over every row) read the SAME table, gvb_{collection}_ann
/// in the same database, so a paired comparison differs only in the search method, not in the
/// copy of the data or the database. The earlier exact mode searched the loader's table, a
/// second copy with a different row layout.
/// Why the index state is proven from the plan: <see cref="GetIndexStateAsync"/> runs a real
/// search of each kind under SET STATISTICS XML ON and reports the operators the server ran.
/// </summary>
public sealed class SqlDiskAnnSink : ISink, IExactSearchSink, IEngineDescription, IIndexFinisher
{
   #region Data Members

   /// <summary>The throwaway database this target writes to.</summary>
   public const string DATABASE = "GvbBenchDiskAnn";

   private const int INDEX_BUILD_LIMIT_SECONDS = 6000;
   private const int STATEMENT_LIMIT_SECONDS = 300;

   private readonly string _serverConnectionString;
   private readonly string _connectionString;
   private readonly string _database;
   private readonly string _durability;
   private SqlVectorSink _inner;
   private string _buildParameters = "not built yet";

   #endregion Data Members

   #region Constructor

   /// <summary>
   /// Creates the target.
   /// </summary>
   /// <param name="serverConnectionString">Connection string without a database.</param>
   /// <param name="serverVersion">First line of @@VERSION, for the report.</param>
   /// <param name="durability">What a crash can lose (see <see cref="SqlDurability.ReadAsync"/>), or null for "not stated".</param>
   /// <param name="database">Database to write to; tests pass their own so they never share the benchmark's.</param>
   /// <param name="composeFile">Compose file that starts the server, or "always-on" for the native service.</param>
   public SqlDiskAnnSink( string serverConnectionString, string serverVersion, string? durability = null, string database = DATABASE, string composeFile = "always-on" )
   {
      ComposeFile = composeFile;
      _serverConnectionString = serverConnectionString;
      _database = database;
      _connectionString = new SqlConnectionStringBuilder( serverConnectionString ) { InitialCatalog = database }.ConnectionString;
      _inner = new SqlVectorSink( serverConnectionString, database );
      _durability = durability ?? "not stated";
      Engine = serverVersion;
   }

   #endregion Constructor

   #region Public Methods

   /// <inheritdoc />
   public string Name => "sql-diskann";

   /// <inheritdoc />
   public string Engine { get; }

   /// <inheritdoc />
   public string IndexDescription => $"DiskANN (preview) via VECTOR_SEARCH, cosine, build {_buildParameters}; the exact mode scans the same table";

   /// <inheritdoc />
   public string Durability => _durability;

   /// <inheritdoc />
   public string ComposeFile { get; }

   /// <inheritdoc />
   public async Task<bool> EnsureCollectionAsync( string collection, int dimension, CancellationToken ct )
   {
      await EnsureDatabaseAsync( ct );
      await ExecAsync( $"DROP TABLE IF EXISTS dbo.{Quote( AnnTable( collection ) )};", ct );
      return await _inner.EnsureCollectionAsync( collection, dimension, ct );
   }

   /// <inheritdoc />
   public Task UpsertAsync( string collection, IReadOnlyList<VectorRecord> records, CancellationToken ct )
   {
      return _inner.UpsertAsync( collection, records, ct );
   }

   /// <inheritdoc />
   public Task DeleteAsync( string collection, IReadOnlyList<Guid> chunkIds, CancellationToken ct )
   {
      return _inner.DeleteAsync( collection, chunkIds, ct );
   }

   /// <inheritdoc />
   public async Task<long> CountAsync( string collection, CancellationToken ct )
   {
      string sql = $@"IF OBJECT_ID( N'dbo.{AnnTable( collection )}' ) IS NOT NULL SELECT COUNT_BIG(*) FROM dbo.{Quote( AnnTable( collection ) )};
ELSE SELECT COUNT_BIG(*) FROM dbo.{Quote( Table( collection ) )};";
      await using var connection = new SqlConnection( _connectionString );
      await connection.OpenAsync( ct );
      await using var command = new SqlCommand( sql, connection ) { CommandTimeout = 300 };
      return Convert.ToInt64( await command.ExecuteScalarAsync( ct ) );
   }

   /// <inheritdoc />
   public Task<IReadOnlyList<SearchHit>> SearchAsync( string collection, float[] vector, int top, CancellationToken ct )
   {
      return RunSearchAsync( SqlProbe.VectorSearchSql( AnnTable( collection ), vector.Length ), vector, top, ct );
   }

   /// <summary>
   /// Exact search: VECTOR_DISTANCE over every row of the SAME table and database the default
   /// search reads (gvb_{collection}_ann), so the paired comparison changes only the search
   /// method. The table's vector index is not used by this statement, which
   /// <see cref="GetIndexStateAsync"/> proves from the plan of a real run.
   /// </summary>
   /// <param name="collection">Collection name.</param>
   /// <param name="vector">Query vector.</param>
   /// <param name="top">Hits wanted.</param>
   /// <param name="ct">Cancellation.</param>
   /// <returns>Hits, best first.</returns>
   public Task<IReadOnlyList<SearchHit>> SearchExactAsync( string collection, float[] vector, int top, CancellationToken ct )
   {
      return RunSearchAsync( SqlProbe.ExactSearchSql( AnnTable( collection ), vector.Length ), vector, top, ct );
   }

   /// <summary>
   /// Drops the table, and the whole throwaway database once it holds no table.
   /// </summary>
   /// <param name="collection">Collection name.</param>
   /// <param name="ct">Cancellation.</param>
   public async Task DropCollectionAsync( string collection, CancellationToken ct )
   {
      await using var connection = new SqlConnection( _serverConnectionString );
      await connection.OpenAsync( ct );
      await using var exists = new SqlCommand( "SELECT DB_ID( @db );", connection );
      exists.Parameters.Add( "@db", SqlDbType.NVarChar, 128 ).Value = _database;
      if( await exists.ExecuteScalarAsync( ct ) is DBNull or null )
      {
         return;
      }

      await ExecAsync( $"DROP TABLE IF EXISTS dbo.{Quote( AnnTable( collection ) )};", ct );
      await _inner.DropCollectionAsync( collection, ct );
      string drop = $@"IF NOT EXISTS ( SELECT 1 FROM {Quote( _database )}.sys.tables WHERE is_ms_shipped = 0 )
BEGIN
   ALTER DATABASE {Quote( _database )} SET SINGLE_USER WITH ROLLBACK IMMEDIATE;
   DROP DATABASE {Quote( _database )};
END";
      SqlConnection.ClearAllPools();
      await using var command = new SqlCommand( drop, connection ) { CommandTimeout = 300 };
      await command.ExecuteNonQueryAsync( ct );
      _inner = new SqlVectorSink( _serverConnectionString, _database );
   }

   /// <summary>
   /// Copies the loaded table into an INT-keyed table, builds the DiskANN index on it, and checks
   /// from the catalog and a real search plan that the index exists and is used.
   /// </summary>
   /// <param name="collection">Collection name.</param>
   /// <param name="ct">Cancellation.</param>
   /// <returns>The index's build parameters as SQL Server reports them, and the graph size.</returns>
   /// <exception cref="InvalidOperationException">The index was built but is not ready or not used.</exception>
   public async Task<string> FinishLoadAsync( string collection, CancellationToken ct )
   {
      string ann = Quote( AnnTable( collection ) );
      int dimension = await DimensionAsync( collection, ct );
      await ExecAsync( $@"DROP TABLE IF EXISTS dbo.{ann};
CREATE TABLE dbo.{ann} ( Id INT IDENTITY(1,1) NOT NULL CONSTRAINT {Quote( "PK_" + AnnTable( collection ) )} PRIMARY KEY CLUSTERED,
   ChunkId UNIQUEIDENTIFIER NOT NULL, DocKey NVARCHAR(450) NOT NULL, TableName NVARCHAR(256) NOT NULL, ChunkText NVARCHAR(MAX) NOT NULL,
   Embedding VECTOR({dimension}) NOT NULL );
INSERT INTO dbo.{ann} WITH (TABLOCK) ( ChunkId, DocKey, TableName, ChunkText, Embedding )
   SELECT ChunkId, DocKey, TableName, ChunkText, Embedding FROM dbo.{Quote( Table( collection ) )} ORDER BY ChunkId;", INDEX_BUILD_LIMIT_SECONDS, ct );
      await ExecAsync( $"CREATE VECTOR INDEX {Quote( IndexName( collection ) )} ON dbo.{ann} ( Embedding ) WITH ( METRIC = 'cosine', TYPE = 'diskann' );", INDEX_BUILD_LIMIT_SECONDS, ct );
      VectorIndexFacts? index = await SqlProbe.ReadVectorIndexAsync( _connectionString, AnnTable( collection ), ct );
      _buildParameters = index?.Build ?? "unknown";
      IndexState state = await GetIndexStateAsync( collection, ct );
      if( !state.Ready )
      {
         throw new InvalidOperationException( $"The DiskANN index on dbo.{AnnTable( collection )} was built but is not ready or not used: {state.Detail}" );
      }

      return $"copied {state.TotalVectors:N0} rows to dbo.{AnnTable( collection )} (INT key) and built DiskANN, parameters {_buildParameters}, graph covers {state.IndexedVectors:N0} rows";
   }

   /// <summary>
   /// Proves from SQL Server itself that the DiskANN index exists and is used: the catalog row,
   /// the graph table's row count, and the actual plan of a real VECTOR_SEARCH. Also records the
   /// plan of the exact mode on the same table.
   /// </summary>
   /// <param name="collection">Collection name.</param>
   /// <param name="ct">Cancellation.</param>
   /// <returns>The state and the evidence it rests on.</returns>
   public Task<IndexState> GetIndexStateAsync( string collection, CancellationToken ct )
   {
      return SqlProbe.DiskAnnStateAsync( _connectionString, _database, AnnTable( collection ), ct );
   }

   /// <summary>
   /// The table the index lives on and searches read: gvb_{collection}_ann.
   /// </summary>
   /// <param name="collection">Collection name.</param>
   /// <returns>The table name.</returns>
   public static string AnnTable( string collection )
   {
      return $"gvb_{collection}_ann";
   }

   #endregion Public Methods

   #region Private Methods

   /// <summary>
   /// Creates the throwaway database if missing and turns PREVIEW_FEATURES on in it (and only
   /// in it). Done as separate batches because the setting must be on before a statement that
   /// uses the feature is even parsed.
   /// </summary>
   /// <param name="ct">Cancellation.</param>
   private async Task EnsureDatabaseAsync( CancellationToken ct )
   {
      await using ( var connection = new SqlConnection( _serverConnectionString ) )
      {
         await connection.OpenAsync( ct );
         await using var create = new SqlCommand( $"IF DB_ID( N'{_database.Replace( "'", "''" )}' ) IS NULL CREATE DATABASE {Quote( _database )};", connection );
         await create.ExecuteNonQueryAsync( ct );
      }

      await ExecAsync( "ALTER DATABASE SCOPED CONFIGURATION SET PREVIEW_FEATURES = ON;", ct );
   }

   /// <summary>
   /// VECTOR dimension of the loaded table, from sys.columns.
   /// </summary>
   /// <param name="collection">Collection name.</param>
   /// <param name="ct">Cancellation.</param>
   /// <returns>The dimension.</returns>
   private async Task<int> DimensionAsync( string collection, CancellationToken ct )
   {
      await using var connection = new SqlConnection( _connectionString );
      await connection.OpenAsync( ct );
      await using var command = new SqlCommand( "SELECT vector_dimensions FROM sys.columns WHERE object_id = OBJECT_ID( @t ) AND name = 'Embedding';", connection );
      command.Parameters.Add( "@t", SqlDbType.NVarChar, 300 ).Value = $"dbo.{Quote( Table( collection ) )}";
      object? value = await command.ExecuteScalarAsync( ct );
      return value is null or DBNull ? throw new InvalidOperationException( $"dbo.{Table( collection )} has not been loaded." ) : Convert.ToInt32( value );
   }

   /// <summary>
   /// Runs one statement in the throwaway database within <see cref="STATEMENT_LIMIT_SECONDS"/>.
   /// </summary>
   /// <param name="sql">Statement.</param>
   /// <param name="ct">Cancellation.</param>
   private Task ExecAsync( string sql, CancellationToken ct )
   {
      return ExecAsync( sql, STATEMENT_LIMIT_SECONDS, ct );
   }

   /// <summary>
   /// Runs one statement in the throwaway database with an explicit time limit. Why a limit even
   /// for the index build: a statement that never returns must fail the load with the server's
   /// timeout message instead of holding the run until its backstop.
   /// </summary>
   /// <param name="sql">Statement.</param>
   /// <param name="seconds">Time limit in seconds.</param>
   /// <param name="ct">Cancellation.</param>
   private async Task ExecAsync( string sql, int seconds, CancellationToken ct )
   {
      await using var connection = new SqlConnection( _connectionString );
      await connection.OpenAsync( ct );
      await using var command = new SqlCommand( sql, connection ) { CommandTimeout = seconds };
      await command.ExecuteNonQueryAsync( ct );
   }

   /// <summary>
   /// Runs a search statement that takes @top and @q and maps its rows to hits.
   /// </summary>
   /// <param name="sql">The statement (see <see cref="SqlProbe"/>).</param>
   /// <param name="vector">Query vector.</param>
   /// <param name="top">Hits wanted.</param>
   /// <param name="ct">Cancellation.</param>
   /// <returns>Hits, best first, with cosine similarity as the score.</returns>
   private async Task<IReadOnlyList<SearchHit>> RunSearchAsync( string sql, float[] vector, int top, CancellationToken ct )
   {
      var hits = new List<SearchHit>( top );
      await using var connection = new SqlConnection( _connectionString );
      await connection.OpenAsync( ct );
      await using var command = new SqlCommand( sql, connection );
      command.Parameters.Add( "@top", SqlDbType.Int ).Value = top;
      command.Parameters.Add( "@q", SqlDbType.NVarChar, -1 ).Value = SqlVectorSink.ToJson( vector );
      await using SqlDataReader reader = await command.ExecuteReaderAsync( ct );
      while( await reader.ReadAsync( ct ) )
      {
         hits.Add( new SearchHit( reader.GetGuid( 0 ), reader.GetString( 1 ), reader.GetString( 2 ), reader.GetString( 3 ), 1.0 - Convert.ToDouble( reader.GetValue( 4 ) ) ) );
      }

      return hits;
   }

   /// <summary>
   /// Table name of a collection, the same rule as the builder's SQL sink.
   /// </summary>
   /// <param name="collection">Collection name.</param>
   /// <returns>The table name.</returns>
   private static string Table( string collection )
   {
      return $"gvb_{collection}";
   }

   /// <summary>
   /// Name of a collection's vector index.
   /// </summary>
   /// <param name="collection">Collection name.</param>
   /// <returns>The index name.</returns>
   private static string IndexName( string collection )
   {
      return $"vix_gvb_{collection}";
   }

   /// <summary>
   /// Bracket-quotes an identifier.
   /// </summary>
   /// <param name="identifier">Identifier.</param>
   /// <returns>The quoted identifier.</returns>
   private static string Quote( string identifier )
   {
      return $"[{identifier.Replace( "]", "]]" )}]";
   }

   #endregion Private Methods
}
