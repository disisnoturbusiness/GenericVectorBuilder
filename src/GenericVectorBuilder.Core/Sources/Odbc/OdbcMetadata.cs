using System.Data;
using System.Data.Odbc;
using System.Diagnostics;

namespace GenericVectorBuilder.Core.Sources.Odbc;

/// <summary>
/// One column of a table as the catalog describes it.
/// </summary>
/// <param name="Name">Column name.</param>
/// <param name="SqlType">ODBC SQL type code (SQLColumns DATA_TYPE).</param>
/// <param name="TypeName">The database's own type name, e.g. "nvarchar" or "geography".</param>
/// <param name="Nullable">True when the column allows NULL (or the driver does not say).</param>
internal sealed record OdbcColumn( string Name, short SqlType, string TypeName, bool Nullable );

/// <summary>
/// The columns of a table the source reads, and the names of those it leaves out.
/// </summary>
/// <param name="Columns">Readable columns, in table order.</param>
/// <param name="Skipped">Names of binary and unsupported columns, in table order.</param>
internal sealed record OdbcTableShape( IReadOnlyList<OdbcColumn> Columns, IReadOnlyList<string> Skipped );

/// <summary>
/// Catalog work shared by the page's catalog and the run's source: listing tables and views,
/// cheap row counts, the readable columns of a table, the SELECT that reads them, and a sample.
/// Why the catalog functions of ODBC (SQLTables, SQLColumns) and not information_schema: they
/// work the same on every driver, so nothing here is written for one database.
/// Why every SQL statement quotes its identifiers with the driver's quote character: table and
/// column names come from the database and may hold spaces, quotes or keywords. They are never
/// pasted into SQL raw.
/// </summary>
internal static class OdbcMetadata
{
   #region Data Members

   /// <summary>Table type for base tables.</summary>
   public const string TABLE = "TABLE";

   /// <summary>Table type for views.</summary>
   public const string VIEW = "VIEW";

   private const int COUNT_TIMEOUT_SECONDS = 2;
   private const int PREVIEW_MAX_CHARS = 500;
   private static readonly TimeSpan COUNT_BUDGET = TimeSpan.FromSeconds( 10 );

   /// <summary>Schemas that hold the database's own objects, never the user's data.</summary>
   private static readonly HashSet<string> SYSTEM_SCHEMAS = new( StringComparer.OrdinalIgnoreCase ) { "sys", "INFORMATION_SCHEMA", "pg_catalog" };

   /// <summary>
   /// Row counts for every user table on SQL Server, read from the partition metadata the server
   /// keeps anyway, so listing a database with a billion-row table costs one small query.
   /// </summary>
   private const string SQL_SERVER_COUNTS = @"SELECT s.name, o.name, SUM( p.rows )
FROM sys.objects AS o JOIN sys.schemas AS s ON s.schema_id = o.schema_id JOIN sys.partitions AS p ON p.object_id = o.object_id AND p.index_id IN ( 0, 1 )
WHERE o.type = 'U' GROUP BY s.name, o.name";

   /// <summary>The same count for one table, by name parameters.</summary>
   private const string SQL_SERVER_COUNT_ONE = @"SELECT SUM( p.rows )
FROM sys.objects AS o JOIN sys.schemas AS s ON s.schema_id = o.schema_id JOIN sys.partitions AS p ON p.object_id = o.object_id AND p.index_id IN ( 0, 1 )
WHERE o.type = 'U' AND s.name = ? AND o.name = ?";

   #endregion Data Members

   #region Public Methods

   /// <summary>
   /// Lists the user tables and views of the connection's current database, sorted by schema
   /// and name. System tables and the sys, INFORMATION_SCHEMA and pg_catalog schemas are left out.
   /// </summary>
   /// <param name="connection">Open connection.</param>
   /// <param name="withCounts">True to add row counts where they are cheap.</param>
   /// <param name="ct">Cancellation.</param>
   /// <returns>The tables and views.</returns>
   public static List<OdbcTableInfo> ListTables( OdbcConnection connection, bool withCounts, CancellationToken ct )
   {
      var found = new Dictionary<(string Schema, string Name), OdbcTableInfo>();
      AddTables( found, connection.GetSchema( "Tables" ), TABLE );
      ct.ThrowIfCancellationRequested();
      AddTables( found, connection.GetSchema( "Views" ), VIEW );
      List<OdbcTableInfo> tables = found.Values
         .OrderBy( t => t.Schema, StringComparer.OrdinalIgnoreCase )
         .ThenBy( t => t.Name, StringComparer.OrdinalIgnoreCase )
         .ToList();
      return withCounts ? AddRowCounts( connection, tables, ct ) : tables;
   }

   /// <summary>
   /// Tells whether a name is a view, by asking the catalog's view listing for it.
   /// </summary>
   /// <param name="connection">Open connection.</param>
   /// <param name="table">The table or view.</param>
   /// <returns>True for a view.</returns>
   public static bool IsView( OdbcConnection connection, OdbcSelection table )
   {
      DataTable views = connection.GetSchema( "Views", new[] { null, SchemaRestriction( table ), table.Name } );
      return Matching( views, table ).Count > 0;
   }

   /// <summary>
   /// Works out which columns of a table are read. The catalog (SQLColumns) is asked first
   /// because it reports the real SQL type of every column. When it knows nothing about the table
   /// (a driver with a weak catalog, or a table the login may not see), the table is queried with
   /// "WHERE 1=0" instead, which either describes the columns or fails with the database's own
   /// reason (not found, permission denied), so the caller gets a true error rather than an
   /// empty table.
   /// </summary>
   /// <param name="connection">Open connection.</param>
   /// <param name="table">The table.</param>
   /// <param name="quote">The driver's quote character.</param>
   /// <param name="ct">Cancellation.</param>
   /// <returns>The readable and skipped columns.</returns>
   /// <exception cref="OdbcException">The table cannot be queried.</exception>
   /// <exception cref="InvalidOperationException">No column of the table can be read as text.</exception>
   public static OdbcTableShape LoadShape( OdbcConnection connection, OdbcSelection table, string? quote, CancellationToken ct )
   {
      DataTable catalog = connection.GetSchema( "Columns", new[] { null, SchemaRestriction( table ), table.Name, null } );
      ct.ThrowIfCancellationRequested();
      List<OdbcColumn> columns = Matching( catalog, table )
         .OrderBy( r => Convert.ToInt32( r["ORDINAL_POSITION"], System.Globalization.CultureInfo.InvariantCulture ) )
         .Select( r => new OdbcColumn( Text( r, "COLUMN_NAME" ), Convert.ToInt16( r["DATA_TYPE"], System.Globalization.CultureInfo.InvariantCulture ),
            Text( r, "TYPE_NAME" ), !( r["NULLABLE"] is not DBNull && Convert.ToInt32( r["NULLABLE"], System.Globalization.CultureInfo.InvariantCulture ) == 0 ) ) )
         .ToList();
      if( columns.Count == 0 )
      {
         columns = DescribeByQuery( connection, table, quote );
      }

      var readable = columns.Where( c => OdbcValues.IsSupported( c.SqlType ) ).ToList();
      if( readable.Count == 0 )
      {
         throw new InvalidOperationException( "None of this table's columns hold text, numbers or dates, so there is nothing to read." );
      }

      return new OdbcTableShape( readable, columns.Where( c => !OdbcValues.IsSupported( c.SqlType ) ).Select( c => c.Name ).ToList() );
   }

   /// <summary>
   /// The SELECT that reads a table's readable columns, every identifier quoted.
   /// </summary>
   /// <param name="shape">The table's columns.</param>
   /// <param name="table">The table.</param>
   /// <param name="quote">The driver's quote character.</param>
   /// <returns>The statement.</returns>
   public static string BuildSelect( OdbcTableShape shape, OdbcSelection table, string? quote )
   {
      return $"SELECT {string.Join( ", ", shape.Columns.Select( c => OdbcNaming.Quote( c.Name, quote ) ) )} FROM {OdbcNaming.QualifiedName( table, quote )}";
   }

   /// <summary>
   /// Reads the first rows of a table, formatted exactly as a run formats them, then cancels the
   /// statement. Values longer than 500 characters are cut, since the page only shows them.
   /// Why cancel: closing a reader with rows left makes the driver drain the whole result first;
   /// measured on a 2.4 million row query that took 3.6 seconds against 17 ms with a cancel.
   /// </summary>
   /// <param name="connection">Open connection.</param>
   /// <param name="shape">The table's columns.</param>
   /// <param name="table">The table.</param>
   /// <param name="quote">The driver's quote character.</param>
   /// <param name="maxRows">How many rows to read.</param>
   /// <param name="ct">Cancellation.</param>
   /// <returns>The rows.</returns>
   public static List<IReadOnlyList<string?>> Sample( OdbcConnection connection, OdbcTableShape shape, OdbcSelection table, string? quote, int maxRows, CancellationToken ct )
   {
      var rows = new List<IReadOnlyList<string?>>( maxRows );
      using var command = new OdbcCommand( BuildSelect( shape, table, quote ), connection );
      using CancellationTokenRegistration registration = ct.Register( command.Cancel );
      OdbcDataReader reader = command.ExecuteReader();
      try
      {
         while( rows.Count < maxRows && reader.Read() )
         {
            var values = new string?[shape.Columns.Count];
            for( int i = 0; i < values.Length; i++ )
            {
               string? value = OdbcValues.Read( reader, i, shape.Columns[i].SqlType );
               values[i] = value != null && value.Length > PREVIEW_MAX_CHARS ? value[..PREVIEW_MAX_CHARS] + "..." : value;
            }

            rows.Add( values );
         }
      }
      finally
      {
         command.Cancel();
         reader.Dispose();
      }

      ct.ThrowIfCancellationRequested();
      return rows;
   }

   /// <summary>
   /// A cheap row count for one table: SQL Server's partition metadata, else null. Nothing is
   /// counted by scanning, because a preview must stay quick on a huge table.
   /// </summary>
   /// <param name="connection">Open connection.</param>
   /// <param name="table">The table.</param>
   /// <returns>The row count, or null when it is not cheap to learn.</returns>
   public static long? CheapRowCount( OdbcConnection connection, OdbcSelection table )
   {
      if( !IsSqlServer( connection ) )
      {
         return null;
      }

      try
      {
         using var command = new OdbcCommand( SQL_SERVER_COUNT_ONE, connection );
         command.Parameters.Add( new OdbcParameter( "schema", OdbcType.NVarChar, 128 ) { Value = table.Schema } );
         command.Parameters.Add( new OdbcParameter( "name", OdbcType.NVarChar, 128 ) { Value = table.Name } );
         object? result = command.ExecuteScalar();
         return result is null or DBNull ? null : Convert.ToInt64( result, System.Globalization.CultureInfo.InvariantCulture );
      }
      catch( OdbcException )
      {
         return null;
      }
   }

   /// <summary>
   /// True when the connection is to Microsoft SQL Server (or Azure SQL), which has cheap
   /// metadata row counts.
   /// </summary>
   /// <param name="connection">Open connection.</param>
   /// <returns>True for SQL Server.</returns>
   public static bool IsSqlServer( OdbcConnection connection )
   {
      try
      {
         DataTable info = connection.GetSchema( "DataSourceInformation" );
         return info.Rows.Count > 0 && ( info.Rows[0]["DataSourceProductName"] as string ?? string.Empty ).Contains( "SQL Server", StringComparison.OrdinalIgnoreCase );
      }
      catch( Exception ex ) when( ex is OdbcException or ArgumentException or InvalidOperationException )
      {
         return false;
      }
   }

   /// <summary>
   /// The catalog rows that belong to exactly this table. Catalog restrictions are search
   /// patterns ('_' matches any character), so rows are filtered by exact schema and name, and
   /// only when nothing matches exactly is a case-insensitive match accepted.
   /// </summary>
   /// <param name="catalog">Rows from GetSchema with TABLE_SCHEM and TABLE_NAME columns.</param>
   /// <param name="table">The table.</param>
   /// <returns>The matching rows.</returns>
   public static List<DataRow> Matching( DataTable catalog, OdbcSelection table )
   {
      List<DataRow> rows = catalog.Rows.Cast<DataRow>().ToList();
      foreach( StringComparison comparison in new[] { StringComparison.Ordinal, StringComparison.OrdinalIgnoreCase } )
      {
         List<DataRow> exact = rows.Where( r => string.Equals( Text( r, "TABLE_SCHEM" ), table.Schema, comparison )
            && string.Equals( Text( r, "TABLE_NAME" ), table.Name, comparison ) ).ToList();
         if( exact.Count > 0 )
         {
            return exact;
         }
      }

      return new List<DataRow>();
   }

   /// <summary>
   /// A catalog cell as text; DBNull and missing columns are "".
   /// </summary>
   /// <param name="row">Catalog row.</param>
   /// <param name="column">Column name.</param>
   /// <returns>The text.</returns>
   public static string Text( DataRow row, string column )
   {
      return row.Table.Columns.Contains( column ) && row[column] is string text ? text : string.Empty;
   }

   /// <summary>
   /// The schema restriction for a catalog call: null (any schema) when the table has none,
   /// because some drivers reject an empty schema pattern.
   /// </summary>
   /// <param name="table">The table.</param>
   /// <returns>The schema, or null.</returns>
   public static string? SchemaRestriction( OdbcSelection table )
   {
      return table.Schema.Length > 0 ? table.Schema : null;
   }

   #endregion Public Methods

   #region Private Methods

   /// <summary>
   /// Adds the rows of a GetSchema("Tables") or GetSchema("Views") result of the wanted type,
   /// skipping system schemas and anything already found.
   /// </summary>
   /// <param name="found">Tables found so far.</param>
   /// <param name="catalog">The catalog rows.</param>
   /// <param name="type">"TABLE" or "VIEW".</param>
   private static void AddTables( Dictionary<(string Schema, string Name), OdbcTableInfo> found, DataTable catalog, string type )
   {
      foreach( DataRow row in catalog.Rows )
      {
         string rowType = Text( row, "TABLE_TYPE" );
         bool wanted = rowType.Equals( type, StringComparison.OrdinalIgnoreCase )
            || ( type == TABLE && rowType.Equals( "BASE TABLE", StringComparison.OrdinalIgnoreCase ) );
         string schema = Text( row, "TABLE_SCHEM" );
         string name = Text( row, "TABLE_NAME" );
         if( wanted && name.Length > 0 && !SYSTEM_SCHEMAS.Contains( schema ) )
         {
            found.TryAdd( (schema, name), new OdbcTableInfo( schema, name, type, null ) );
         }
      }
   }

   /// <summary>
   /// Adds row counts to base tables: one metadata query on SQL Server, otherwise COUNT(*) per
   /// table with a two-second limit each and ten seconds in total, so a slow database still lists
   /// quickly. A count that is not learned stays null. Views are never counted.
   /// </summary>
   /// <param name="connection">Open connection.</param>
   /// <param name="tables">The tables.</param>
   /// <param name="ct">Cancellation.</param>
   /// <returns>The tables with counts where known.</returns>
   private static List<OdbcTableInfo> AddRowCounts( OdbcConnection connection, List<OdbcTableInfo> tables, CancellationToken ct )
   {
      Dictionary<(string Schema, string Name), long>? metadata = IsSqlServer( connection ) ? SqlServerCounts( connection ) : null;
      if( metadata != null )
      {
         return tables.Select( t => t.Type == TABLE && metadata.TryGetValue( (t.Schema, t.Name), out long n ) ? t with { RowCount = n } : t ).ToList();
      }

      string? quote = OdbcNaming.QuoteCharOf( connection );
      var budget = Stopwatch.StartNew();
      return tables.Select( t => t.Type == TABLE && budget.Elapsed < COUNT_BUDGET ? t with { RowCount = CountRows( connection, t, quote, ct ) } : t ).ToList();
   }

   /// <summary>
   /// Every user table's row count from SQL Server's partition metadata.
   /// </summary>
   /// <param name="connection">Open connection.</param>
   /// <returns>Counts by schema and name, or null when the query is not allowed.</returns>
   private static Dictionary<(string Schema, string Name), long>? SqlServerCounts( OdbcConnection connection )
   {
      try
      {
         var counts = new Dictionary<(string Schema, string Name), long>();
         using var command = new OdbcCommand( SQL_SERVER_COUNTS, connection );
         using OdbcDataReader reader = command.ExecuteReader();
         while( reader.Read() )
         {
            counts[(reader.GetString( 0 ), reader.GetString( 1 ))] = Convert.ToInt64( reader.GetValue( 2 ), System.Globalization.CultureInfo.InvariantCulture );
         }

         return counts;
      }
      catch( OdbcException )
      {
         return null;
      }
   }

   /// <summary>
   /// COUNT(*) of one table with a short time limit.
   /// </summary>
   /// <param name="connection">Open connection.</param>
   /// <param name="table">The table.</param>
   /// <param name="quote">The driver's quote character.</param>
   /// <param name="ct">Cancellation.</param>
   /// <returns>The count, or null when it failed or took too long.</returns>
   private static long? CountRows( OdbcConnection connection, OdbcTableInfo table, string? quote, CancellationToken ct )
   {
      ct.ThrowIfCancellationRequested();
      try
      {
         string name = OdbcNaming.QualifiedName( new OdbcSelection( table.Schema, table.Name ), quote );
         using var command = new OdbcCommand( $"SELECT COUNT(*) FROM {name}", connection ) { CommandTimeout = COUNT_TIMEOUT_SECONDS };
         object? result = command.ExecuteScalar();
         return result is null or DBNull ? null : Convert.ToInt64( result, System.Globalization.CultureInfo.InvariantCulture );
      }
      catch( Exception ex ) when( ex is OdbcException or InvalidOperationException or InvalidCastException or OverflowException )
      {
         return null;
      }
   }

   /// <summary>
   /// Describes a table's columns by running "SELECT * ... WHERE 1=0". Columns whose type the
   /// reader cannot even name are marked binary, which skips them.
   /// </summary>
   /// <param name="connection">Open connection.</param>
   /// <param name="table">The table.</param>
   /// <param name="quote">The driver's quote character.</param>
   /// <returns>The columns.</returns>
   /// <exception cref="OdbcException">The table cannot be queried; the database's reason is in the exception.</exception>
   private static List<OdbcColumn> DescribeByQuery( OdbcConnection connection, OdbcSelection table, string? quote )
   {
      using var command = new OdbcCommand( $"SELECT * FROM {OdbcNaming.QualifiedName( table, quote )} WHERE 1=0", connection );
      using OdbcDataReader reader = command.ExecuteReader();
      var columns = new List<OdbcColumn>( reader.FieldCount );
      for( int i = 0; i < reader.FieldCount; i++ )
      {
         Type? type;
         try
         {
            type = reader.GetFieldType( i );
         }
         catch( ArgumentException )
         {
            type = null;
         }

         columns.Add( new OdbcColumn( reader.GetName( i ), OdbcValues.SqlTypeOf( type ), type?.Name ?? "unknown", true ) );
      }

      return columns;
   }

   #endregion Private Methods
}
