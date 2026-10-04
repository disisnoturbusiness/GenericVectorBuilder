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
/// feature on only in <see cref="DATABASE"/>, which exists for the benchmark alone and is
/// dropped when its last table is.
/// How a load works: rows are written with the builder's own SQL sink (bulk copy + MERGE, the
/// same loader and speed as the "sql" target), then <see cref="FinishLoadAsync"/> copies the
/// table into gvb_{collection}_ann and builds the index there, timed together.
/// Why the copy: a vector index needs a clustered primary key on one 4-byte INT column
/// (measured on CU9: "must have a clustered primary key on a single 4 byte INT column"), and
/// the builder's tables are keyed by a GUID chunk id. The copy gets an INT IDENTITY key and
/// keeps ChunkId as a plain column, so hits map back to the same ids.
/// </summary>
public sealed class SqlDiskAnnSink : ISink, IExactSearchSink, IEngineDescription, IIndexFinisher
{
   #region Data Members

   /// <summary>The throwaway database this target writes to.</summary>
   public const string DATABASE = "GvbBenchDiskAnn";

   private readonly string _serverConnectionString;
   private readonly string _connectionString;
   private SqlVectorSink _inner;
   private string _buildParameters = "not built yet";

   #endregion Data Members

   #region Constructor

   /// <summary>
   /// Creates the target.
   /// </summary>
   /// <param name="serverConnectionString">Connection string without a database.</param>
   /// <param name="serverVersion">First line of @@VERSION, for the report.</param>
   public SqlDiskAnnSink( string serverConnectionString, string serverVersion )
   {
      _serverConnectionString = serverConnectionString;
      _connectionString = new SqlConnectionStringBuilder( serverConnectionString ) { InitialCatalog = DATABASE }.ConnectionString;
      _inner = new SqlVectorSink( serverConnectionString, DATABASE );
      Engine = serverVersion;
   }

   #endregion Constructor

   #region Public Methods

   /// <inheritdoc />
   public string Name => "sql-diskann";

   /// <inheritdoc />
   public string Engine { get; }

   /// <inheritdoc />
   public string IndexDescription => $"DiskANN (preview) via VECTOR_SEARCH, cosine, build {_buildParameters}";

   /// <inheritdoc />
   public string ComposeFile => "always-on";

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
   public async Task<IReadOnlyList<SearchHit>> SearchAsync( string collection, float[] vector, int top, CancellationToken ct )
   {
      string sql = $@"DECLARE @qv VECTOR({vector.Length}) = CAST( @q AS VECTOR({vector.Length}) );
SELECT t.ChunkId, t.DocKey, t.TableName, t.ChunkText, s.distance
FROM VECTOR_SEARCH( TABLE = dbo.{Quote( AnnTable( collection ) )} AS t, COLUMN = Embedding, SIMILAR_TO = @qv, METRIC = 'cosine', TOP_N = @top ) AS s
ORDER BY s.distance;";
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

   /// <inheritdoc />
   public Task<IReadOnlyList<SearchHit>> SearchExactAsync( string collection, float[] vector, int top, CancellationToken ct )
   {
      return _inner.SearchAsync( collection, vector, top, ct );
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
      exists.Parameters.Add( "@db", SqlDbType.NVarChar, 128 ).Value = DATABASE;
      if( await exists.ExecuteScalarAsync( ct ) is DBNull or null )
      {
         return;
      }

      await ExecAsync( $"DROP TABLE IF EXISTS dbo.{Quote( AnnTable( collection ) )};", ct );
      await _inner.DropCollectionAsync( collection, ct );
      string drop = $@"IF NOT EXISTS ( SELECT 1 FROM {Quote( DATABASE )}.sys.tables WHERE is_ms_shipped = 0 )
BEGIN
   ALTER DATABASE {Quote( DATABASE )} SET SINGLE_USER WITH ROLLBACK IMMEDIATE;
   DROP DATABASE {Quote( DATABASE )};
END";
      SqlConnection.ClearAllPools();
      await using var command = new SqlCommand( drop, connection ) { CommandTimeout = 300 };
      await command.ExecuteNonQueryAsync( ct );
      _inner = new SqlVectorSink( _serverConnectionString, DATABASE );
   }

   /// <summary>
   /// Copies the loaded table into an INT-keyed table and builds the DiskANN index on it.
   /// </summary>
   /// <param name="collection">Collection name.</param>
   /// <param name="ct">Cancellation.</param>
   /// <returns>The index's build parameters as SQL Server reports them.</returns>
   public async Task<string> FinishLoadAsync( string collection, CancellationToken ct )
   {
      string ann = Quote( AnnTable( collection ) );
      int dimension = await DimensionAsync( collection, ct );
      await ExecAsync( $@"DROP TABLE IF EXISTS dbo.{ann};
CREATE TABLE dbo.{ann} ( Id INT IDENTITY(1,1) NOT NULL CONSTRAINT {Quote( "PK_" + AnnTable( collection ) )} PRIMARY KEY CLUSTERED,
   ChunkId UNIQUEIDENTIFIER NOT NULL, DocKey NVARCHAR(450) NOT NULL, TableName NVARCHAR(256) NOT NULL, ChunkText NVARCHAR(MAX) NOT NULL,
   Embedding VECTOR({dimension}) NOT NULL );
INSERT INTO dbo.{ann} WITH (TABLOCK) ( ChunkId, DocKey, TableName, ChunkText, Embedding )
   SELECT ChunkId, DocKey, TableName, ChunkText, Embedding FROM dbo.{Quote( Table( collection ) )} ORDER BY ChunkId;", ct );
      await ExecAsync( $"CREATE VECTOR INDEX {Quote( IndexName( collection ) )} ON dbo.{ann} ( Embedding ) WITH ( METRIC = 'cosine', TYPE = 'diskann' );", ct );
      await using var connection = new SqlConnection( _connectionString );
      await connection.OpenAsync( ct );
      await using var command = new SqlCommand( "SELECT TOP (1) build_parameters FROM sys.vector_indexes WHERE object_id = OBJECT_ID( @t );", connection );
      command.Parameters.Add( "@t", SqlDbType.NVarChar, 300 ).Value = $"dbo.{ann}";
      _buildParameters = Convert.ToString( await command.ExecuteScalarAsync( ct ) ) ?? "unknown";
      return $"copied to dbo.{AnnTable( collection )} (INT key) and built DiskANN, parameters {_buildParameters}";
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
         await using var create = new SqlCommand( $"IF DB_ID( N'{DATABASE}' ) IS NULL CREATE DATABASE {Quote( DATABASE )};", connection );
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
   /// Runs one statement in the throwaway database with no time limit (index builds are slow).
   /// </summary>
   /// <param name="sql">Statement.</param>
   /// <param name="ct">Cancellation.</param>
   private async Task ExecAsync( string sql, CancellationToken ct )
   {
      await using var connection = new SqlConnection( _connectionString );
      await connection.OpenAsync( ct );
      await using var command = new SqlCommand( sql, connection ) { CommandTimeout = 0 };
      await command.ExecuteNonQueryAsync( ct );
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
