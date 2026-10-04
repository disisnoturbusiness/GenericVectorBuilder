using System.Collections.Concurrent;
using System.Data;
using System.Globalization;
using System.Text;
using System.Text.Json;
using GenericVectorBuilder.Core.Contracts;
using Microsoft.Data.SqlClient;

namespace GenericVectorBuilder.Core.Sinks;

/// <summary>
/// Writes vectors to SQL Server 2025's native VECTOR type: one table per pipeline, named
/// dbo.gvb_{pipeline}, in a database the sink creates if it is missing.
/// How a batch is written: SqlBulkCopy into a session temp table, then one MERGE that casts
/// the JSON vector text to VECTOR(n). Bulk copy is far faster than row-by-row inserts, and the
/// MERGE makes the write an upsert, so a retried batch never duplicates.
/// Why retry is done here: SqlBulkCopy has no built-in retry provider and command retry does
/// not apply inside a transaction, so the WHOLE batch (temp table, copy, merge) is retried on
/// a fresh connection. That is safe because the merge is idempotent.
/// What this deliberately does NOT do: turn on PREVIEW_FEATURES or build a DiskANN index. On
/// SQL Server for Linux that index is preview and makes the table read-only; exact
/// VECTOR_DISTANCE search is correct and fast enough at this scale.
/// Why text columns are clamped before the bulk copy: one value longer than its column makes
/// SqlBulkCopy throw a non-retryable error for the whole batch, which drops the sink for the
/// rest of the run on every run. DocKey, TableName and Origin are payload here (deletes go by
/// ChunkId), so clamping only shortens what a search result displays.
/// </summary>
public sealed class SqlVectorSink : ISink
{
   #region Data Members

   private const int MAX_ATTEMPTS = 3;
   private const int DELETE_BATCH = 1000;
   private const int DOC_KEY_CHARS = 450;
   private const int TABLE_NAME_CHARS = 256;
   private const int ORIGIN_CHARS = 4000;
   private static readonly int[] TRANSIENT_ERRORS = { -2, 53, 64, 233, 1205, 4060, 10053, 10054, 10060, 40197, 40501, 40613, 49918, 49919, 49920 };

   private readonly string _serverConnectionString;
   private readonly string _database;
   private readonly ConcurrentDictionary<string, int> _dimensions = new( StringComparer.Ordinal );
   private bool _databaseReady;

   #endregion Data Members

   #region Constructor

   /// <summary>
   /// Creates the sink.
   /// </summary>
   /// <param name="serverConnectionString">Connection string WITHOUT an initial catalog; the sink adds it.</param>
   /// <param name="database">Database to hold the pipeline tables.</param>
   public SqlVectorSink( string serverConnectionString, string database )
   {
      _serverConnectionString = serverConnectionString;
      _database = database;
   }

   #endregion Constructor

   #region Public Methods

   /// <inheritdoc />
   public string Name => "sql";

   /// <inheritdoc />
   public async Task<bool> EnsureCollectionAsync( string collection, int dimension, CancellationToken ct )
   {
      await EnsureDatabaseAsync( ct );
      string table = TableName( collection );
      await using SqlConnection connection = await OpenAsync( ct );
      int? existing = await ExistingDimensionAsync( connection, table, ct );
      if( existing.HasValue && existing.Value != dimension )
      {
         throw new InvalidOperationException( $"SQL table {table} holds {existing}-dim vectors but the embedder produces {dimension}. Reset the pipeline or use a new name." );
      }

      if( existing.HasValue )
      {
         await WidenOriginAsync( connection, table, ct );
      }
      else
      {
         string ddl = $@"CREATE TABLE dbo.{Quote( table )} (
   ChunkId UNIQUEIDENTIFIER NOT NULL CONSTRAINT {Quote( "PK_" + table )} PRIMARY KEY,
   DocKey NVARCHAR({DOC_KEY_CHARS}) NOT NULL,
   TableName NVARCHAR({TABLE_NAME_CHARS}) NOT NULL,
   Origin NVARCHAR({ORIGIN_CHARS}) NOT NULL,
   Ordinal INT NOT NULL,
   ChunkText NVARCHAR(MAX) NOT NULL,
   Metadata NVARCHAR(MAX) NULL,
   Embedding VECTOR({dimension}) NOT NULL,
   UpdatedUtc DATETIME2 NOT NULL );
CREATE INDEX {Quote( "IX_" + table + "_DocKey" )} ON dbo.{Quote( table )} ( DocKey );";
         await ExecAsync( connection, ddl, ct );
      }

      _dimensions[collection] = dimension;
      return !existing.HasValue;
   }

   /// <inheritdoc />
   public Task UpsertAsync( string collection, IReadOnlyList<VectorRecord> records, CancellationToken ct )
   {
      if( records.Count == 0 )
      {
         return Task.CompletedTask;
      }

      DataTable rows = BuildStagingRows( records );
      return WithRetryAsync( connection => MergeBatchAsync( connection, collection, rows, ct ), ct );
   }

   /// <inheritdoc />
   public async Task DeleteAsync( string collection, IReadOnlyList<Guid> chunkIds, CancellationToken ct )
   {
      string sql = $"DELETE FROM dbo.{Quote( TableName( collection ) )} WHERE ChunkId IN ( SELECT CAST( value AS UNIQUEIDENTIFIER ) FROM OPENJSON( @ids ) );";
      foreach( Guid[] batch in chunkIds.Chunk( DELETE_BATCH ) )
      {
         string ids = JsonSerializer.Serialize( batch );
         await WithRetryAsync( async connection =>
         {
            await using var command = new SqlCommand( sql, connection );
            command.Parameters.Add( "@ids", SqlDbType.NVarChar, -1 ).Value = ids;
            await command.ExecuteNonQueryAsync( ct );
         }, ct );
      }
   }

   /// <inheritdoc />
   public async Task<long> CountAsync( string collection, CancellationToken ct )
   {
      string sql = $"SELECT COUNT_BIG(*) FROM dbo.{Quote( TableName( collection ) )};";
      long count = 0;
      await WithRetryAsync( async connection =>
      {
         await using var command = new SqlCommand( sql, connection ) { CommandTimeout = 300 };
         count = Convert.ToInt64( await command.ExecuteScalarAsync( ct ), CultureInfo.InvariantCulture );
      }, ct );

      return count;
   }

   /// <inheritdoc />
   public async Task<IReadOnlyList<SearchHit>> SearchAsync( string collection, float[] vector, int top, CancellationToken ct )
   {
      int dimension = await DimensionAsync( collection, ct );
      string sql = $@"SELECT TOP (@top) ChunkId, DocKey, TableName, ChunkText,
   VECTOR_DISTANCE( 'cosine', Embedding, CAST( @q AS VECTOR({dimension}) ) ) AS Distance
FROM dbo.{Quote( TableName( collection ) )} ORDER BY Distance;";
      var hits = new List<SearchHit>();
      await using SqlConnection connection = await OpenAsync( ct );
      await using var command = new SqlCommand( sql, connection );
      command.Parameters.Add( "@top", SqlDbType.Int ).Value = top;
      command.Parameters.Add( "@q", SqlDbType.NVarChar, -1 ).Value = ToJson( vector );
      await using SqlDataReader reader = await command.ExecuteReaderAsync( ct );
      while( await reader.ReadAsync( ct ) )
      {
         hits.Add( new SearchHit( reader.GetGuid( 0 ), reader.GetString( 1 ), reader.GetString( 2 ), reader.GetString( 3 ), 1.0 - reader.GetDouble( 4 ) ) );
      }

      return hits;
   }

   /// <inheritdoc />
   public async Task DropCollectionAsync( string collection, CancellationToken ct )
   {
      await EnsureDatabaseAsync( ct );
      await using SqlConnection connection = await OpenAsync( ct );
      await ExecAsync( connection, $"DROP TABLE IF EXISTS dbo.{Quote( TableName( collection ) )};", ct );
      _dimensions.TryRemove( collection, out _ );
   }

   /// <summary>
   /// Formats a vector as the JSON array text SQL Server casts to VECTOR. Round-trip float
   /// formatting with the invariant culture, so no locale can turn 0.5 into "0,5".
   /// </summary>
   /// <param name="vector">The vector.</param>
   /// <returns>JSON array text.</returns>
   public static string ToJson( float[] vector )
   {
      var text = new StringBuilder( vector.Length * 12 ).Append( '[' );
      for( int i = 0; i < vector.Length; i++ )
      {
         text.Append( i == 0 ? string.Empty : "," ).Append( vector[i].ToString( "R", CultureInfo.InvariantCulture ) );
      }

      return text.Append( ']' ).ToString();
   }

   #endregion Public Methods

   #region Private Methods

   /// <summary>
   /// Bulk copies a batch into a temp table and merges it into the pipeline table.
   /// </summary>
   /// <param name="connection">Open connection (temp tables live per connection).</param>
   /// <param name="collection">Collection name.</param>
   /// <param name="rows">Staging rows.</param>
   /// <param name="ct">Cancellation.</param>
   private async Task MergeBatchAsync( SqlConnection connection, string collection, DataTable rows, CancellationToken ct )
   {
      int dimension = await DimensionAsync( collection, ct );
      await ExecAsync( connection, $@"CREATE TABLE #gvb_stage ( ChunkId UNIQUEIDENTIFIER NOT NULL PRIMARY KEY, DocKey NVARCHAR({DOC_KEY_CHARS}) NOT NULL,
   TableName NVARCHAR({TABLE_NAME_CHARS}) NOT NULL, Origin NVARCHAR({ORIGIN_CHARS}) NOT NULL, Ordinal INT NOT NULL, ChunkText NVARCHAR(MAX) NOT NULL,
   Metadata NVARCHAR(MAX) NULL, EmbeddingJson NVARCHAR(MAX) NOT NULL );", ct );
      using( var bulk = new SqlBulkCopy( connection ) { DestinationTableName = "#gvb_stage", BulkCopyTimeout = 300 } )
      {
         await bulk.WriteToServerAsync( rows, ct );
      }

      string merge = $@"MERGE dbo.{Quote( TableName( collection ) )} WITH (HOLDLOCK) AS t
USING #gvb_stage AS s ON t.ChunkId = s.ChunkId
WHEN MATCHED THEN UPDATE SET DocKey = s.DocKey, TableName = s.TableName, Origin = s.Origin, Ordinal = s.Ordinal,
   ChunkText = s.ChunkText, Metadata = s.Metadata, Embedding = CAST( s.EmbeddingJson AS VECTOR({dimension}) ), UpdatedUtc = SYSUTCDATETIME()
WHEN NOT MATCHED THEN INSERT ( ChunkId, DocKey, TableName, Origin, Ordinal, ChunkText, Metadata, Embedding, UpdatedUtc )
   VALUES ( s.ChunkId, s.DocKey, s.TableName, s.Origin, s.Ordinal, s.ChunkText, s.Metadata, CAST( s.EmbeddingJson AS VECTOR({dimension}) ), SYSUTCDATETIME() );
DROP TABLE #gvb_stage;";
      await ExecAsync( connection, merge, ct );
   }

   /// <summary>
   /// Builds the in-memory staging rows for bulk copy, clamping text columns to their declared
   /// lengths so one long value cannot fail the whole batch.
   /// </summary>
   /// <param name="records">Vectors to write.</param>
   /// <returns>A DataTable matching #gvb_stage.</returns>
   internal static DataTable BuildStagingRows( IReadOnlyList<VectorRecord> records )
   {
      var table = new DataTable();
      table.Columns.Add( "ChunkId", typeof( Guid ) );
      table.Columns.Add( "DocKey", typeof( string ) );
      table.Columns.Add( "TableName", typeof( string ) );
      table.Columns.Add( "Origin", typeof( string ) );
      table.Columns.Add( "Ordinal", typeof( int ) );
      table.Columns.Add( "ChunkText", typeof( string ) );
      table.Columns.Add( "Metadata", typeof( string ) );
      table.Columns.Add( "EmbeddingJson", typeof( string ) );
      foreach( VectorRecord r in records )
      {
         table.Rows.Add( r.Chunk.ChunkId, Fit( r.Document.DocKey, DOC_KEY_CHARS ), Fit( r.Document.Table, TABLE_NAME_CHARS ),
            Fit( r.Document.Origin, ORIGIN_CHARS ), r.Chunk.Ordinal, r.Chunk.Text, JsonSerializer.Serialize( r.Document.Metadata ), ToJson( r.Vector ) );
      }

      return table;
   }

   /// <summary>
   /// Keeps the END of an over-long value, which for a path is the part that names the file.
   /// </summary>
   /// <param name="value">Value to fit.</param>
   /// <param name="max">Column length in characters.</param>
   /// <returns>The value, or its last <paramref name="max"/> characters.</returns>
   private static string Fit( string value, int max )
   {
      return value.Length <= max ? value : value[^max..];
   }

   /// <summary>
   /// Widens the Origin column of a table created by an older version (NVARCHAR(1024)) so a deep
   /// folder path cannot fail a batch. Growing an NVARCHAR column is a metadata-only change.
   /// Best effort: if the server refuses, the table keeps working exactly as before, so a
   /// failed widen must never take the whole sink down.
   /// </summary>
   /// <param name="connection">Open connection.</param>
   /// <param name="table">Table name.</param>
   /// <param name="ct">Cancellation.</param>
   private static async Task WidenOriginAsync( SqlConnection connection, string table, CancellationToken ct )
   {
      string sql = $@"IF COL_LENGTH( @t, 'Origin' ) BETWEEN 1 AND {ORIGIN_CHARS * 2 - 1}
   ALTER TABLE dbo.{Quote( table )} ALTER COLUMN Origin NVARCHAR({ORIGIN_CHARS}) NOT NULL;";
      await using var command = new SqlCommand( sql, connection ) { CommandTimeout = 300 };
      command.Parameters.Add( "@t", SqlDbType.NVarChar, 300 ).Value = $"dbo.{Quote( table )}";
      try
      {
         await command.ExecuteNonQueryAsync( ct );
      }
      catch( SqlException )
      {
         // Keep the old width; only an origin longer than 1,024 characters is affected.
      }
   }

   /// <summary>
   /// Runs an action on a fresh connection, retrying transient SQL errors with backoff.
   /// </summary>
   /// <param name="action">Work to run.</param>
   /// <param name="ct">Cancellation.</param>
   private async Task WithRetryAsync( Func<SqlConnection, Task> action, CancellationToken ct )
   {
      for( int attempt = 1; ; attempt++ )
      {
         try
         {
            await using SqlConnection connection = await OpenAsync( ct );
            await action( connection );
            return;
         }
         catch( SqlException ex ) when( attempt < MAX_ATTEMPTS && TRANSIENT_ERRORS.Contains( ex.Number ) )
         {
            await Task.Delay( TimeSpan.FromSeconds( attempt * 2 ), ct );
         }
      }
   }

   /// <summary>
   /// Creates the database on first use. The name is passed through QUOTENAME on the server
   /// so it can never break out of the statement.
   /// </summary>
   /// <param name="ct">Cancellation.</param>
   private async Task EnsureDatabaseAsync( CancellationToken ct )
   {
      if( _databaseReady )
      {
         return;
      }

      await using var connection = new SqlConnection( _serverConnectionString );
      await connection.OpenAsync( ct );
      await using var command = new SqlCommand( "IF DB_ID( @db ) IS NULL BEGIN DECLARE @sql NVARCHAR(400) = N'CREATE DATABASE ' + QUOTENAME( @db ); EXEC( @sql ); END", connection );
      command.Parameters.Add( "@db", SqlDbType.NVarChar, 128 ).Value = _database;
      await command.ExecuteNonQueryAsync( ct );
      _databaseReady = true;
   }

   /// <summary>
   /// Reads the VECTOR dimension of an existing pipeline table from sys.columns.
   /// </summary>
   /// <param name="connection">Open connection.</param>
   /// <param name="table">Table name.</param>
   /// <param name="ct">Cancellation.</param>
   /// <returns>The dimension, or null when the table does not exist.</returns>
   private static async Task<int?> ExistingDimensionAsync( SqlConnection connection, string table, CancellationToken ct )
   {
      await using var command = new SqlCommand( "SELECT vector_dimensions FROM sys.columns WHERE object_id = OBJECT_ID( @t ) AND name = 'Embedding';", connection );
      command.Parameters.Add( "@t", SqlDbType.NVarChar, 300 ).Value = $"dbo.{Quote( table )}";
      object? value = await command.ExecuteScalarAsync( ct );
      return value is null or DBNull ? null : Convert.ToInt32( value, CultureInfo.InvariantCulture );
   }

   /// <summary>
   /// Returns the dimension of a collection, reading it from the table when this process has
   /// not ensured it yet (e.g. a search right after a restart).
   /// </summary>
   /// <param name="collection">Collection name.</param>
   /// <param name="ct">Cancellation.</param>
   /// <returns>The dimension.</returns>
   private async Task<int> DimensionAsync( string collection, CancellationToken ct )
   {
      if( _dimensions.TryGetValue( collection, out int known ) )
      {
         return known;
      }

      await using SqlConnection connection = await OpenAsync( ct );
      int? dimension = await ExistingDimensionAsync( connection, TableName( collection ), ct );
      return dimension ?? throw new InvalidOperationException( $"Pipeline '{collection}' has no SQL table yet. Run it first." );
   }

   /// <summary>
   /// Opens a connection to the pipeline database.
   /// </summary>
   /// <param name="ct">Cancellation.</param>
   /// <returns>The open connection.</returns>
   private async Task<SqlConnection> OpenAsync( CancellationToken ct )
   {
      var builder = new SqlConnectionStringBuilder( _serverConnectionString ) { InitialCatalog = _database };
      var connection = new SqlConnection( builder.ConnectionString );
      await connection.OpenAsync( ct );
      return connection;
   }

   /// <summary>
   /// Executes a statement with no result.
   /// </summary>
   /// <param name="connection">Open connection.</param>
   /// <param name="sql">Statement.</param>
   /// <param name="ct">Cancellation.</param>
   private static async Task ExecAsync( SqlConnection connection, string sql, CancellationToken ct )
   {
      await using var command = new SqlCommand( sql, connection ) { CommandTimeout = 300 };
      await command.ExecuteNonQueryAsync( ct );
   }

   /// <summary>
   /// Table name for a collection. Collection names are already sanitized to [a-z0-9_].
   /// </summary>
   /// <param name="collection">Collection name.</param>
   /// <returns>The table name.</returns>
   private static string TableName( string collection )
   {
      return $"gvb_{collection}";
   }

   /// <summary>
   /// Bracket-quotes an identifier, doubling any closing bracket.
   /// </summary>
   /// <param name="identifier">Identifier.</param>
   /// <returns>The quoted identifier.</returns>
   private static string Quote( string identifier )
   {
      return $"[{identifier.Replace( "]", "]]" )}]";
   }

   #endregion Private Methods
}
