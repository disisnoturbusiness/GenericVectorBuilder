using System.Data;
using System.Globalization;
using GenericVectorBuilder.Engines.Common;
using Microsoft.Data.SqlClient;

namespace GenericVectorBuilder.Bench.Targets;

/// <summary>
/// Reads from SQL Server what the two SQL targets need to prove about their index: the catalog
/// (sys.vector_indexes, sys.indexes, the vector index's internal graph table) and the actual
/// execution plan of a real search.
/// Why a real search under SET STATISTICS XML ON: a catalog row says an index exists, not that the
/// optimiser used it. The actual plan is the server's record of the operators that ran and the
/// rows they produced. The search statements are built here and used by the sinks as well, so the
/// plan is of the very statement the benchmark times.
/// </summary>
public static class SqlProbe
{
   #region Data Members

   /// <summary>Hits asked for by the probe searches, the same as the benchmark's default.</summary>
   public const int PROBE_TOP = 10;

   private const int QUERY_LIMIT_SECONDS = 120;

   #endregion Data Members

   #region Public Methods

   /// <summary>
   /// The exact search statement: every row is compared with VECTOR_DISTANCE, and the table's
   /// vector index (when it has one) is not used.
   /// </summary>
   /// <param name="table">Table name without schema.</param>
   /// <param name="dimension">Vector length.</param>
   /// <returns>The statement; it takes @top and @q (the query as JSON text).</returns>
   public static string ExactSearchSql( string table, int dimension )
   {
      return $@"SELECT TOP (@top) ChunkId, DocKey, TableName, ChunkText,
   VECTOR_DISTANCE( 'cosine', Embedding, CAST( @q AS VECTOR({dimension}) ) ) AS Distance
FROM dbo.{Quote( table )} ORDER BY Distance;";
   }

   /// <summary>
   /// The DiskANN search statement: VECTOR_SEARCH through the table's vector index.
   /// </summary>
   /// <param name="table">Table name without schema.</param>
   /// <param name="dimension">Vector length.</param>
   /// <returns>The statement; it takes @top and @q (the query as JSON text).</returns>
   public static string VectorSearchSql( string table, int dimension )
   {
      return $@"DECLARE @qv VECTOR({dimension}) = CAST( @q AS VECTOR({dimension}) );
SELECT t.ChunkId, t.DocKey, t.TableName, t.ChunkText, s.distance
FROM VECTOR_SEARCH( TABLE = dbo.{Quote( table )} AS t, COLUMN = Embedding, SIMILAR_TO = @qv, METRIC = 'cosine', TOP_N = @top ) AS s
ORDER BY s.distance;";
   }

   /// <summary>
   /// Bracket-quotes an identifier.
   /// </summary>
   /// <param name="identifier">Identifier.</param>
   /// <returns>The quoted identifier.</returns>
   public static string Quote( string identifier )
   {
      return $"[{identifier.Replace( "]", "]]" )}]";
   }

   /// <summary>
   /// Reads a table's row count, vector dimension and index list.
   /// </summary>
   /// <param name="connectionString">Connection string including the database.</param>
   /// <param name="table">Table name without schema.</param>
   /// <param name="ct">Cancellation.</param>
   /// <returns>The facts, or null when the table does not exist.</returns>
   public static async Task<TableFacts?> ReadTableAsync( string connectionString, string table, CancellationToken ct )
   {
      await using var connection = new SqlConnection( connectionString );
      await connection.OpenAsync( ct );
      string name = $"dbo.{Quote( table )}";
      if( await ScalarAsync( connection, "SELECT OBJECT_ID( @t );", name, ct ) is null or DBNull )
      {
         return null;
      }

      long rows = Convert.ToInt64( await ScalarAsync( connection, $"SELECT COUNT_BIG(*) FROM {name};", null, ct ), CultureInfo.InvariantCulture );
      object? dimension = await ScalarAsync( connection, "SELECT vector_dimensions FROM sys.columns WHERE object_id = OBJECT_ID( @t ) AND name = 'Embedding';", name, ct );
      var indexes = new List<string>();
      await using var command = new SqlCommand( "SELECT name, type_desc FROM sys.indexes WHERE object_id = OBJECT_ID( @t ) AND type > 0 ORDER BY index_id;", connection ) { CommandTimeout = QUERY_LIMIT_SECONDS };
      command.Parameters.Add( "@t", SqlDbType.NVarChar, 300 ).Value = name;
      await using SqlDataReader reader = await command.ExecuteReaderAsync( ct );
      while( await reader.ReadAsync( ct ) )
      {
         indexes.Add( $"{reader.GetString( 0 )} {reader.GetString( 1 )}" );
      }

      return new TableFacts( rows, dimension is null or DBNull ? 0 : Convert.ToInt32( dimension, CultureInfo.InvariantCulture ), indexes );
   }

   /// <summary>
   /// Reads a table's vector index from sys.vector_indexes, and the row count of the index's
   /// internal graph table (one row per vector the graph covers).
   /// </summary>
   /// <param name="connectionString">Connection string including the database.</param>
   /// <param name="table">Table name without schema.</param>
   /// <param name="ct">Cancellation.</param>
   /// <returns>The index, or null when the table has none.</returns>
   public static async Task<VectorIndexFacts?> ReadVectorIndexAsync( string connectionString, string table, CancellationToken ct )
   {
      await using var connection = new SqlConnection( connectionString );
      await connection.OpenAsync( ct );
      string name = $"dbo.{Quote( table )}";
      const string INDEX_SQL = "SELECT TOP (1) name, vector_index_type, distance_metric, is_disabled, build_parameters FROM sys.vector_indexes WHERE object_id = OBJECT_ID( @t );";
      string? index = null;
      string type = string.Empty;
      string metric = string.Empty;
      bool disabled = false;
      string build = string.Empty;
      await using( var command = new SqlCommand( INDEX_SQL, connection ) { CommandTimeout = QUERY_LIMIT_SECONDS } )
      {
         command.Parameters.Add( "@t", SqlDbType.NVarChar, 300 ).Value = name;
         await using SqlDataReader reader = await command.ExecuteReaderAsync( ct );
         if( await reader.ReadAsync( ct ) )
         {
            ( index, type, metric, disabled, build ) = ( reader.GetString( 0 ), reader.GetString( 1 ), reader.GetString( 2 ), reader.GetBoolean( 3 ), reader.IsDBNull( 4 ) ? string.Empty : reader.GetString( 4 ) );
         }
      }

      if( index == null )
      {
         return null;
      }

      object? graph = await ScalarAsync( connection, @"SELECT SUM( ps.row_count ) FROM sys.internal_tables it
JOIN sys.dm_db_partition_stats ps ON ps.object_id = it.object_id AND ps.index_id IN ( 0, 1 )
WHERE it.parent_object_id = OBJECT_ID( @t ) AND it.internal_type_desc = 'VECTOR_INDEX_GRAPH_EDGE_TABLE';", name, ct );
      return new VectorIndexFacts( index, type, metric, disabled, build, graph is null or DBNull ? null : Convert.ToInt64( graph, CultureInfo.InvariantCulture ) );
   }

   /// <summary>
   /// Runs one search statement for real with SET STATISTICS XML ON and returns its actual plan.
   /// </summary>
   /// <param name="connectionString">Connection string including the database.</param>
   /// <param name="sql">A search statement taking @top and @q.</param>
   /// <param name="queryJson">The query vector as JSON text.</param>
   /// <param name="ct">Cancellation.</param>
   /// <returns>The plan, and how many hits the search returned.</returns>
   /// <exception cref="InvalidOperationException">The server returned no plan.</exception>
   public static async Task<PlanRun> ActualPlanAsync( string connectionString, string sql, string queryJson, CancellationToken ct )
   {
      await using var connection = new SqlConnection( connectionString );
      await connection.OpenAsync( ct );
      await using( var on = new SqlCommand( "SET STATISTICS XML ON;", connection ) { CommandTimeout = QUERY_LIMIT_SECONDS } )
      {
         await on.ExecuteNonQueryAsync( ct );
      }

      await using var command = new SqlCommand( sql, connection ) { CommandTimeout = QUERY_LIMIT_SECONDS };
      command.Parameters.Add( "@top", SqlDbType.Int ).Value = PROBE_TOP;
      command.Parameters.Add( "@q", SqlDbType.NVarChar, -1 ).Value = queryJson;
      int hits = 0;
      string? xml = null;
      await using( SqlDataReader reader = await command.ExecuteReaderAsync( ct ) )
      {
         do
         {
            bool isPlan = reader.FieldCount == 1 && reader.GetName( 0 ).Contains( "Showplan", StringComparison.OrdinalIgnoreCase );
            while( await reader.ReadAsync( ct ) )
            {
               hits += isPlan ? 0 : 1;
               xml = isPlan ? Convert.ToString( reader.GetValue( 0 ), CultureInfo.InvariantCulture ) : xml;
            }
         }
         while( await reader.NextResultAsync( ct ) );
      }

      return new PlanRun( SqlPlan.Parse( xml ?? throw new InvalidOperationException( "SQL Server returned no execution plan." ) ), hits );
   }

   /// <summary>
   /// Index state of the "sql" target: the builder's exact scan, which must use no vector index.
   /// Ready when the table has no vector index and the actual plan of a real exact search is a
   /// table scan with no vector index operator. Never throws for a server problem.
   /// </summary>
   /// <param name="serverConnectionString">Connection string without a database.</param>
   /// <param name="database">Database holding the table.</param>
   /// <param name="table">Table name without schema.</param>
   /// <param name="ct">Cancellation.</param>
   /// <returns>The state with its evidence.</returns>
   public static async Task<IndexState> ExactStateAsync( string serverConnectionString, string database, string table, CancellationToken ct )
   {
      string connection = new SqlConnectionStringBuilder( serverConnectionString ) { InitialCatalog = database }.ConnectionString;
      try
      {
         TableFacts? facts = await ReadTableAsync( connection, table, ct );
         if( facts == null )
         {
            return new IndexState( false, null, null, $"table dbo.{table} does not exist in {database}" );
         }

         if( facts.Rows == 0 )
         {
            return new IndexState( false, 0, 0, $"table dbo.{table} in {database} is empty, so there is nothing to search" );
         }

         VectorIndexFacts? index = await ReadVectorIndexAsync( connection, table, ct );
         PlanRun run = await ActualPlanAsync( connection, ExactSearchSql( table, facts.Dimension ), await StoredQueryAsync( connection, table, ct ), ct );
         return JudgeExact( database, table, facts, index, run );
      }
      catch( SqlException ex )
      {
         return new IndexState( false, null, null, $"could not read the plan of a search in {database}: {ex.Message}" );
      }
   }

   /// <summary>
   /// Index state of the "sql-diskann" target. Ready when the table has an enabled DiskANN index
   /// whose graph covers every row, the actual plan of a real VECTOR_SEARCH contains a vector
   /// index operator on that index, and the actual plan of the exact search on the SAME table
   /// uses no vector index. Never throws for a server problem.
   /// </summary>
   /// <param name="connectionString">Connection string including the database.</param>
   /// <param name="database">Database name, for the report.</param>
   /// <param name="table">The indexed table, without schema.</param>
   /// <param name="ct">Cancellation.</param>
   /// <returns>The state with its evidence.</returns>
   public static async Task<IndexState> DiskAnnStateAsync( string connectionString, string database, string table, CancellationToken ct )
   {
      try
      {
         TableFacts? facts = await ReadTableAsync( connectionString, table, ct );
         if( facts == null )
         {
            return new IndexState( false, null, null, $"table dbo.{table} does not exist in {database}, so no DiskANN index was built" );
         }

         if( facts.Rows == 0 )
         {
            return new IndexState( false, 0, 0, $"table dbo.{table} in {database} is empty, so there is nothing to index or search" );
         }

         VectorIndexFacts? index = await ReadVectorIndexAsync( connectionString, table, ct );
         if( index == null )
         {
            return new IndexState( false, 0, facts.Rows, $"NOT a DiskANN search: sys.vector_indexes lists no vector index on dbo.{table} ({facts.Rows:N0} rows), so VECTOR_SEARCH has nothing to use" );
         }

         string query = await StoredQueryAsync( connectionString, table, ct );
         PlanRun seek = await ActualPlanAsync( connectionString, VectorSearchSql( table, facts.Dimension ), query, ct );
         PlanRun exact = await ActualPlanAsync( connectionString, ExactSearchSql( table, facts.Dimension ), query, ct );
         return JudgeDiskAnn( database, table, facts, index, seek, exact );
      }
      catch( SqlException ex )
      {
         return new IndexState( false, null, null, $"could not read the DiskANN index of {database}: {ex.Message}" );
      }
   }

   /// <summary>
   /// Turns the facts read for the "sql" target into the verdict: ready only when the table has
   /// no vector index, the actual plan of a real exact search is a table scan with no vector
   /// index operator, and the search returned hits. Pure, so every verdict is tested without a server.
   /// </summary>
   /// <param name="database">Database name.</param>
   /// <param name="table">Searched table.</param>
   /// <param name="facts">Table facts.</param>
   /// <param name="index">The table's vector index, or null when it has none.</param>
   /// <param name="run">Plan of a real exact search.</param>
   /// <returns>The verdict.</returns>
   public static IndexState JudgeExact( string database, string table, TableFacts facts, VectorIndexFacts? index, PlanRun run )
   {
      bool ready = index == null && run.Plan.VectorSeek == null && run.Plan.Scan != null && run.Hits > 0;
      string evidence = index == null ? "sys.vector_indexes lists no index on the table" : $"sys.vector_indexes lists {index.Name} ({index.Type}) on the table";
      return new IndexState( ready, 0, facts.Rows, $"{( ready ? "no index used, exact scan by design" : "NOT an exact scan" )}: dbo.{table} in {database} holds {facts.Rows:N0} rows; {evidence}; "
         + $"indexes: {string.Join( ", ", facts.Indexes )}; a real search under SET STATISTICS XML ON ran: {run.Plan.Describe()}, {run.Hits} hits" );
   }

   /// <summary>
   /// Turns the facts read from the catalog and the two actual plans into the verdict of the
   /// "sql-diskann" target: ready only when the index is an enabled DiskANN index whose graph
   /// covers every row, the real VECTOR_SEARCH plan has a vector index operator on that index
   /// and returned hits, and the real exact search on the same table scanned it without one.
   /// Pure, so every verdict is tested without a server.
   /// </summary>
   /// <param name="database">Database name.</param>
   /// <param name="table">Indexed table.</param>
   /// <param name="facts">Table facts.</param>
   /// <param name="index">The vector index.</param>
   /// <param name="seek">Plan of a real VECTOR_SEARCH.</param>
   /// <param name="exact">Plan of a real exact search on the same table.</param>
   /// <returns>The verdict.</returns>
   public static IndexState JudgeDiskAnn( string database, string table, TableFacts facts, VectorIndexFacts index, PlanRun seek, PlanRun exact )
   {
      var problems = new List<string>();
      if( index.Disabled || !index.Type.Equals( "DiskANN", StringComparison.OrdinalIgnoreCase ) )
      {
         problems.Add( $"the index is {( index.Disabled ? "disabled" : index.Type )}" );
      }

      if( index.GraphRows != facts.Rows )
      {
         problems.Add( $"the graph covers {index.GraphRows?.ToString( "N0" ) ?? "an unknown number"} of {facts.Rows:N0} rows" );
      }

      if( seek.Plan.VectorSeek == null || !string.Equals( seek.Plan.VectorSeek.Index, index.Name, StringComparison.Ordinal ) || seek.Hits == 0 )
      {
         problems.Add( "the plan of a real VECTOR_SEARCH has no vector index operator on that index, or it returned no hits" );
      }

      if( exact.Plan.VectorSeek != null || exact.Plan.Scan == null )
      {
         problems.Add( "the exact search did not scan the table" );
      }

      string verdict = problems.Count == 0 ? "DiskANN index built and used" : $"NOT ready: {string.Join( "; ", problems )}";
      return new IndexState( problems.Count == 0, index.GraphRows, facts.Rows, $"{verdict}. sys.vector_indexes: {index.Name} on dbo.{table} in {database}, {index.Type}, {index.Metric}, {( index.Disabled ? "disabled" : "enabled" )}, build {index.Build}; "
         + $"graph table rows {index.GraphRows?.ToString( "N0" ) ?? "?"} of {facts.Rows:N0} table rows. Real VECTOR_SEARCH plan: {seek.Plan.Describe()}, {seek.Hits} hits. "
         + $"The exact mode searches the SAME table (dbo.{table}, database {database}) and its real plan is: {exact.Plan.Describe()}, {exact.Hits} hits" );
   }

   #endregion Public Methods

   #region Private Methods

   /// <summary>
   /// Reads one stored vector as JSON text, to use as the probe query. Why a stored vector: the
   /// probe needs a real query of the right dimension and no embedding service.
   /// </summary>
   /// <param name="connectionString">Connection string including the database.</param>
   /// <param name="table">Table name without schema.</param>
   /// <param name="ct">Cancellation.</param>
   /// <returns>The vector as JSON text.</returns>
   /// <exception cref="InvalidOperationException">The table is empty.</exception>
   private static async Task<string> StoredQueryAsync( string connectionString, string table, CancellationToken ct )
   {
      await using var connection = new SqlConnection( connectionString );
      await connection.OpenAsync( ct );
      object? value = await ScalarAsync( connection, $"SELECT TOP (1) CAST( Embedding AS NVARCHAR(MAX) ) FROM dbo.{Quote( table )};", null, ct );
      return value is null or DBNull ? throw new InvalidOperationException( $"dbo.{table} holds no rows, so there is no query vector to probe with." ) : Convert.ToString( value, CultureInfo.InvariantCulture )!;
   }

   /// <summary>
   /// Runs a scalar query with an optional @t parameter and a time limit.
   /// </summary>
   /// <param name="connection">Open connection.</param>
   /// <param name="sql">Query.</param>
   /// <param name="table">Value for @t, or null when the query has no @t.</param>
   /// <param name="ct">Cancellation.</param>
   /// <returns>The first value.</returns>
   private static async Task<object?> ScalarAsync( SqlConnection connection, string sql, string? table, CancellationToken ct )
   {
      await using var command = new SqlCommand( sql, connection ) { CommandTimeout = QUERY_LIMIT_SECONDS };
      if( table != null )
      {
         command.Parameters.Add( "@t", SqlDbType.NVarChar, 300 ).Value = table;
      }

      return await command.ExecuteScalarAsync( ct );
   }

   #endregion Private Methods
}

/// <summary>
/// A table as the SQL Server catalog describes it.
/// </summary>
/// <param name="Rows">Rows stored.</param>
/// <param name="Dimension">Length of the Embedding vector column, or 0 when unknown.</param>
/// <param name="Indexes">Every index as "name TYPE", e.g. "PK_x CLUSTERED".</param>
public sealed record TableFacts( long Rows, int Dimension, IReadOnlyList<string> Indexes );

/// <summary>
/// A vector index as sys.vector_indexes describes it.
/// </summary>
/// <param name="Name">Index name.</param>
/// <param name="Type">Index type, "DiskANN".</param>
/// <param name="Metric">Distance metric.</param>
/// <param name="Disabled">True when the index is disabled.</param>
/// <param name="Build">Build parameters as the server reports them.</param>
/// <param name="GraphRows">Rows of the index's internal graph table (one per indexed vector), or null when not found.</param>
public sealed record VectorIndexFacts( string Name, string Type, string Metric, bool Disabled, string Build, long? GraphRows );

/// <summary>
/// One real search run with its actual plan.
/// </summary>
/// <param name="Plan">The actual plan's operators.</param>
/// <param name="Hits">Rows the search returned.</param>
public sealed record PlanRun( PlanFacts Plan, int Hits );
