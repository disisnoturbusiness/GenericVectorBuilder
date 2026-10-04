using System.Data;
using System.Diagnostics;
using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Data.SqlClient;
using Microsoft.Data.SqlTypes;

namespace GenericVectorBuilder.Bench.Data;

/// <summary>
/// Reads a pipeline's table (dbo.gvb_{pipeline} in the GenericVectorBuilder database) into
/// memory, READ-ONLY: one SELECT, no locks held beyond the read, nothing written.
/// Why rows come in ChunkId order: the clustered key streams without a sort, and "--limit N"
/// then always means the same N rows, so a replicate and a later bench agree on the data.
/// Why VECTOR is read natively as SqlVector&lt;float&gt;: it arrives as 4 bytes a float instead
/// of about 12 characters of JSON text a float. If the driver or server refuses that, the
/// reader falls back to CAST( Embedding AS NVARCHAR(MAX) ) and parses the JSON.
/// </summary>
public sealed class PipelineReader
{
   #region Data Members

   private static readonly Regex PIPELINE_NAME = new( "^[a-z0-9_]{1,100}$", RegexOptions.Compiled );

   private readonly string _connectionString;

   #endregion Data Members

   #region Constructor

   /// <summary>
   /// Creates the reader.
   /// </summary>
   /// <param name="serverConnectionString">Connection string without a database.</param>
   /// <param name="database">Database holding the pipeline tables.</param>
   public PipelineReader( string serverConnectionString, string database )
   {
      _connectionString = new SqlConnectionStringBuilder( serverConnectionString ) { InitialCatalog = database, ApplicationName = "GenericVectorBuilder.Bench" }.ConnectionString;
   }

   #endregion Constructor

   #region Public Methods

   /// <summary>How the vectors were read on the last load ("native SqlVector" or "JSON text").</summary>
   public string VectorReadMode { get; private set; } = "not read yet";

   /// <summary>Seconds the last load took.</summary>
   public double LoadSeconds { get; private set; }

   /// <summary>
   /// Loads up to <paramref name="limit"/> rows of a pipeline.
   /// </summary>
   /// <param name="pipeline">Pipeline name (lower-case letters, digits, underscores).</param>
   /// <param name="limit">Most rows to read, or null for all.</param>
   /// <param name="withText">Also read chunk text (needed to replicate, not to benchmark).</param>
   /// <param name="ct">Cancellation.</param>
   /// <returns>The rows.</returns>
   public async Task<PipelineData> LoadAsync( string pipeline, int? limit, bool withText, CancellationToken ct )
   {
      if( !PIPELINE_NAME.IsMatch( pipeline ) )
      {
         throw new ArgumentException( $"'{pipeline}' is not a pipeline name (lower-case letters, digits and underscores only)." );
      }

      Stopwatch clock = Stopwatch.StartNew();
      await using var connection = new SqlConnection( _connectionString );
      await connection.OpenAsync( ct );
      (long rows, int dimension) = await ShapeAsync( connection, pipeline, ct );
      int capacity = (int)Math.Min( rows, limit ?? long.MaxValue );
      PipelineData data;
      try
      {
         data = await ReadAsync( connection, pipeline, capacity, dimension, withText, native: true, ct );
         VectorReadMode = "native SqlVector<float>";
      }
      catch( Exception ex ) when( ex is InvalidCastException or NotSupportedException or SqlException )
      {
         data = await ReadAsync( connection, pipeline, capacity, dimension, withText, native: false, ct );
         VectorReadMode = $"JSON text (native read failed: {ex.Message})";
      }

      LoadSeconds = clock.Elapsed.TotalSeconds;
      return data;
   }

   #endregion Public Methods

   #region Private Methods

   /// <summary>
   /// Row count (exact, so the buffers are never sized short) and VECTOR dimension of the
   /// pipeline table.
   /// </summary>
   /// <param name="connection">Open connection.</param>
   /// <param name="pipeline">Pipeline name.</param>
   /// <param name="ct">Cancellation.</param>
   /// <returns>Rows and dimension.</returns>
   private static async Task<(long Rows, int Dimension)> ShapeAsync( SqlConnection connection, string pipeline, CancellationToken ct )
   {
      await using var shape = new SqlCommand( "SELECT vector_dimensions FROM sys.columns WHERE object_id = OBJECT_ID( @t ) AND name = 'Embedding';", connection );
      shape.Parameters.Add( "@t", SqlDbType.NVarChar, 300 ).Value = $"dbo.gvb_{pipeline}";
      object? dimension = await shape.ExecuteScalarAsync( ct );
      if( dimension is null or DBNull )
      {
         throw new InvalidOperationException( $"There is no table dbo.gvb_{pipeline} with an Embedding column. Build the pipeline first." );
      }

      await using var count = new SqlCommand( $"SELECT COUNT_BIG(*) FROM dbo.[gvb_{pipeline}];", connection ) { CommandTimeout = 0 };
      long rows = Convert.ToInt64( await count.ExecuteScalarAsync( ct ), CultureInfo.InvariantCulture );
      return ( rows, Convert.ToInt32( dimension, CultureInfo.InvariantCulture ) );
   }

   /// <summary>
   /// Streams the rows into memory.
   /// </summary>
   /// <param name="connection">Open connection.</param>
   /// <param name="pipeline">Pipeline name.</param>
   /// <param name="capacity">Rows to read.</param>
   /// <param name="dimension">Vector length.</param>
   /// <param name="withText">Read chunk text.</param>
   /// <param name="native">Read VECTOR as SqlVector (true) or as JSON text (false).</param>
   /// <param name="ct">Cancellation.</param>
   /// <returns>The rows.</returns>
   private static async Task<PipelineData> ReadAsync( SqlConnection connection, string pipeline, int capacity, int dimension, bool withText, bool native, CancellationToken ct )
   {
      string text = withText ? "ChunkText" : "CAST( N'' AS NVARCHAR(MAX) )";
      string vector = native ? "Embedding" : "CAST( Embedding AS NVARCHAR(MAX) )";
      string sql = $"SELECT TOP (@limit) ChunkId, DocKey, TableName, Origin, Ordinal, {text}, Metadata, {vector} FROM dbo.[gvb_{pipeline}] ORDER BY ChunkId;";
      await using var command = new SqlCommand( sql, connection ) { CommandTimeout = 0 };
      command.Parameters.Add( "@limit", SqlDbType.Int ).Value = capacity;
      var data = new PipelineData( pipeline, capacity, dimension ) { HasText = withText };
      var interned = new Dictionary<string, string>( StringComparer.Ordinal );
      await using SqlDataReader reader = await command.ExecuteReaderAsync( CommandBehavior.SequentialAccess, ct );
      while( await reader.ReadAsync( ct ) && data.Count < capacity )
      {
         Guid id = reader.GetGuid( 0 );
         string docKey = reader.GetString( 1 );
         string table = Intern( interned, reader.GetString( 2 ) );
         string origin = Intern( interned, reader.GetString( 3 ) );
         int ordinal = reader.GetInt32( 4 );
         string chunkText = reader.GetString( 5 );
         string? metadata = reader.IsDBNull( 6 ) ? null : reader.GetString( 6 );
         ReadOnlyMemory<float> values = native ? reader.GetFieldValue<SqlVector<float>>( 7 ).Memory : JsonSerializer.Deserialize<float[]>( reader.GetString( 7 ) )!;
         data.Add( id, docKey, table, origin, ordinal, chunkText, metadata, values.Span );
      }

      return data;
   }

   /// <summary>
   /// Returns one shared instance per distinct string (tables and origins repeat on every row).
   /// </summary>
   /// <param name="pool">The pool.</param>
   /// <param name="value">A value.</param>
   /// <returns>The pooled instance.</returns>
   private static string Intern( Dictionary<string, string> pool, string value )
   {
      if( pool.TryGetValue( value, out string? existing ) )
      {
         return existing;
      }

      pool[value] = value;
      return value;
   }

   #endregion Private Methods
}
