using System.Data;
using System.Data.Odbc;
using System.Globalization;

namespace GenericVectorBuilder.Core.Sources.Odbc;

/// <summary>
/// Suggests the column that identifies a row, so the page can pre-select a row id.
/// The order of trust, highest first:
/// 1. A one-column primary key (SQLPrimaryKeys, through System.Data.Odbc's key info), tables only.
/// 2. A one-column unique index on a NOT NULL column (SQLStatistics), tables and indexed views.
/// 3. A column named like an id ("id", "order_id", "ProductID") that a query proves unique and
///    never NULL: COUNT(*) = COUNT(col) = COUNT(DISTINCT col), only on tables small enough
///    (five million rows) or, for views, within a time limit.
/// Why views skip step 1: a view's columns report the BASE table they come from, so a join view
/// would inherit the base table's primary key even when the join repeats it.
/// Why a guess must be proven: a key column that is not unique makes rows overwrite each other
/// (the mapper adds #2 suffixes in read order, which shift when rows move). No suggestion is
/// far better than a wrong one, so every failure here quietly means "no suggestion".
/// </summary>
internal static class OdbcKeyFinder
{
   #region Data Members

   private const long KEY_CHECK_MAX_ROWS = 5_000_000;
   private const int KEY_CHECK_TIMEOUT_SECONDS = 15;
   private const int MAX_NAME_CANDIDATES = 3;
   private const short CLUSTERED_INDEX = 1;

   #endregion Data Members

   #region Public Methods

   /// <summary>
   /// Suggests the row id column of a table or view.
   /// </summary>
   /// <param name="connection">Open connection.</param>
   /// <param name="table">The table.</param>
   /// <param name="shape">Its readable columns; only these can be suggested.</param>
   /// <param name="quote">The driver's quote character.</param>
   /// <param name="isView">True for a view.</param>
   /// <param name="rowCount">Cheap row count when known, used to skip the uniqueness query on huge tables.</param>
   /// <param name="ct">Cancellation.</param>
   /// <returns>The column name, or null.</returns>
   public static string? Suggest( OdbcConnection connection, OdbcSelection table, OdbcTableShape shape, string? quote, bool isView, long? rowCount, CancellationToken ct )
   {
      string? key = isView ? null : PrimaryKey( connection, table, shape, quote );
      key ??= UniqueIndex( connection, table, shape );
      ct.ThrowIfCancellationRequested();
      if( key != null || ( !isView && rowCount > KEY_CHECK_MAX_ROWS ) )
      {
         return key;
      }

      return ProvenIdColumn( connection, table, shape, quote, ct );
   }

   #endregion Public Methods

   #region Private Methods

   /// <summary>
   /// The single NOT NULL primary key column, from the driver's key info. Composite keys give
   /// null, because the mapper keys a row by one column.
   /// </summary>
   /// <param name="connection">Open connection.</param>
   /// <param name="table">The table.</param>
   /// <param name="shape">Readable columns.</param>
   /// <param name="quote">The driver's quote character.</param>
   /// <returns>The column, or null.</returns>
   private static string? PrimaryKey( OdbcConnection connection, OdbcSelection table, OdbcTableShape shape, string? quote )
   {
      // Key info needs every selected column's type to be one System.Data.Odbc can map.
      var mappable = new OdbcTableShape( shape.Columns.Where( c => !OdbcValues.ReadsAsText( c.SqlType ) ).ToList(), Array.Empty<string>() );
      if( mappable.Columns.Count == 0 )
      {
         return null;
      }

      try
      {
         using var command = new OdbcCommand( OdbcMetadata.BuildSelect( mappable, table, quote ), connection );
         using OdbcDataReader reader = command.ExecuteReader( CommandBehavior.KeyInfo | CommandBehavior.SchemaOnly );
         DataTable? schema = reader.GetSchemaTable();
         List<DataRow> keys = schema?.Rows.Cast<DataRow>().Where( r => r["IsKey"] is true ).ToList() ?? new List<DataRow>();
         if( keys.Count != 1 )
         {
            return null;
         }

         string name = OdbcMetadata.Text( keys[0], "ColumnName" );
         string baseTable = OdbcMetadata.Text( keys[0], "BaseTableName" );
         OdbcColumn? column = shape.Columns.FirstOrDefault( c => c.Name == name );
         bool sameTable = baseTable.Length == 0 || baseTable.Equals( table.Name, StringComparison.OrdinalIgnoreCase );
         return column != null && !column.Nullable && sameTable ? column.Name : null;
      }
      catch( Exception ex ) when( ex is OdbcException or ArgumentException or InvalidOperationException )
      {
         return null;
      }
   }

   /// <summary>
   /// A one-column unique index on a NOT NULL readable column, clustered first. Filtered indexes
   /// are ignored because they only promise uniqueness for part of the table.
   /// </summary>
   /// <param name="connection">Open connection.</param>
   /// <param name="table">The table.</param>
   /// <param name="shape">Readable columns.</param>
   /// <returns>The column, or null.</returns>
   private static string? UniqueIndex( OdbcConnection connection, OdbcSelection table, OdbcTableShape shape )
   {
      try
      {
         DataTable indexes = connection.GetSchema( "Indexes", new[] { null, OdbcMetadata.SchemaRestriction( table ), table.Name, null } );
         var candidates = OdbcMetadata.Matching( indexes, table )
            .Where( r => IsUnique( r ) && OdbcMetadata.Text( r, "INDEX_NAME" ).Length > 0 && OdbcMetadata.Text( r, "FILTER_CONDITION" ).Length == 0 )
            .GroupBy( r => OdbcMetadata.Text( r, "INDEX_NAME" ), StringComparer.Ordinal )
            .Where( g => g.Count() == 1 )
            .Select( g => g.Single() )
            .OrderBy( r => IndexType( r ) == CLUSTERED_INDEX ? 0 : 1 );
         foreach( DataRow row in candidates )
         {
            OdbcColumn? column = shape.Columns.FirstOrDefault( c => c.Name == OdbcMetadata.Text( row, "COLUMN_NAME" ) );
            if( column != null && !column.Nullable && !OdbcValues.IsLongText( column.SqlType ) )
            {
               return column.Name;
            }
         }

         return null;
      }
      catch( Exception ex ) when( ex is OdbcException or ArgumentException or InvalidOperationException or FormatException or InvalidCastException )
      {
         return null;
      }
   }

   /// <summary>
   /// The first id-like column a query proves unique and never NULL. Bare "id"/"key" columns are
   /// tried first, then the rest in table order, at most three.
   /// </summary>
   /// <param name="connection">Open connection.</param>
   /// <param name="table">The table.</param>
   /// <param name="shape">Readable columns.</param>
   /// <param name="quote">The driver's quote character.</param>
   /// <param name="ct">Cancellation.</param>
   /// <returns>The column, or null.</returns>
   private static string? ProvenIdColumn( OdbcConnection connection, OdbcSelection table, OdbcTableShape shape, string? quote, CancellationToken ct )
   {
      IEnumerable<OdbcColumn> candidates = shape.Columns
         .Where( c => OdbcNaming.LooksLikeKey( c.Name ) && !OdbcValues.IsLongText( c.SqlType ) )
         .OrderBy( c => OdbcNaming.IsBareKeyName( c.Name ) ? 0 : 1 )
         .Take( MAX_NAME_CANDIDATES );
      foreach( OdbcColumn column in candidates )
      {
         ct.ThrowIfCancellationRequested();
         if( IsProvenUnique( connection, table, column, quote, ct ) )
         {
            return column.Name;
         }
      }

      return null;
   }

   /// <summary>
   /// Runs COUNT(*), COUNT(col), COUNT(DISTINCT col) in one statement with a time limit.
   /// </summary>
   /// <param name="connection">Open connection.</param>
   /// <param name="table">The table.</param>
   /// <param name="column">The candidate column.</param>
   /// <param name="quote">The driver's quote character.</param>
   /// <param name="ct">Cancellation.</param>
   /// <returns>True when the table has rows and the column is unique and never NULL.</returns>
   private static bool IsProvenUnique( OdbcConnection connection, OdbcSelection table, OdbcColumn column, string? quote, CancellationToken ct )
   {
      try
      {
         string name = OdbcNaming.Quote( column.Name, quote );
         string sql = $"SELECT COUNT(*), COUNT({name}), COUNT(DISTINCT {name}) FROM {OdbcNaming.QualifiedName( table, quote )}";
         using var command = new OdbcCommand( sql, connection ) { CommandTimeout = KEY_CHECK_TIMEOUT_SECONDS };
         using CancellationTokenRegistration registration = ct.Register( command.Cancel );
         using OdbcDataReader reader = command.ExecuteReader();
         if( !reader.Read() )
         {
            return false;
         }

         long total = Convert.ToInt64( reader.GetValue( 0 ), CultureInfo.InvariantCulture );
         long present = Convert.ToInt64( reader.GetValue( 1 ), CultureInfo.InvariantCulture );
         long distinct = Convert.ToInt64( reader.GetValue( 2 ), CultureInfo.InvariantCulture );
         return total > 0 && present == total && distinct == total;
      }
      catch( Exception ex ) when( ex is OdbcException or InvalidOperationException or InvalidCastException or OverflowException or FormatException )
      {
         return false;
      }
   }

   /// <summary>
   /// True for a statistics row of a unique index (NON_UNIQUE = 0). The table-statistics row the
   /// driver may add has NULL there and is not unique.
   /// </summary>
   /// <param name="row">SQLStatistics row.</param>
   /// <returns>True for a unique index row.</returns>
   private static bool IsUnique( DataRow row )
   {
      return row.Table.Columns.Contains( "NON_UNIQUE" ) && row["NON_UNIQUE"] is not DBNull
         && Convert.ToInt32( row["NON_UNIQUE"], CultureInfo.InvariantCulture ) == 0;
   }

   /// <summary>
   /// The index type of a statistics row (1 clustered, 2 hashed, 3 other), or 0 when unknown.
   /// </summary>
   /// <param name="row">SQLStatistics row.</param>
   /// <returns>The type.</returns>
   private static int IndexType( DataRow row )
   {
      return row.Table.Columns.Contains( "TYPE" ) && row["TYPE"] is not DBNull ? Convert.ToInt32( row["TYPE"], CultureInfo.InvariantCulture ) : 0;
   }

   #endregion Private Methods
}
