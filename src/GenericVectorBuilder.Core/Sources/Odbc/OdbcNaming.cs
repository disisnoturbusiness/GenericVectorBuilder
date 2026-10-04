using System.Data.Odbc;
using System.Text;
using System.Text.RegularExpressions;
using GenericVectorBuilder.Core.Mapping;

namespace GenericVectorBuilder.Core.Sources.Odbc;

/// <summary>
/// Every name the ODBC source derives: quoted SQL identifiers, origins, display table names,
/// stable table ids and the stable name of a raw connection string.
/// Why one place: the catalog preview and the source must agree exactly. A table id that the
/// preview computes one way and the source another would key mappings to nothing, and an origin
/// that changes between runs would make every stored row look removed.
/// </summary>
internal static class OdbcNaming
{
   #region Data Members

   private const int TABLE_ID_HEX = 16;
   private const int CONNECTION_HASH_HEX = 10;
   private const string RAW_CONNECTION_PREFIX = "odbc_";
   private const char SEPARATOR = '\u001F';

   /// <summary>Connection string keys that hold a login; never part of a derived name.</summary>
   private static readonly HashSet<string> LOGIN_KEYS = new( StringComparer.OrdinalIgnoreCase ) { "UID", "PWD", "User ID", "User", "Username", "Password" };

   /// <summary>A name that needs no quoting in any SQL dialect.</summary>
   private static readonly Regex PLAIN_IDENTIFIER = new( "^[A-Za-z_][A-Za-z0-9_]*$", RegexOptions.Compiled );

   /// <summary>
   /// Column names that usually identify a row: "id", "key", "order_id", "Order Key", "ProductID",
   /// "CustomerKey". Case matters for the run-together form so "Paid" or "Monkey" do not match.
   /// </summary>
   private static readonly Regex ID_LIKE = new( @"(^(?i:id|key)$)|([_\s\-.](?i:id|key)$)|([a-z0-9](Id|ID|Key|KEY)$)", RegexOptions.Compiled );

   #endregion Data Members

   #region Public Methods

   /// <summary>
   /// Quotes one identifier with the driver's quote character, doubling any quote character
   /// inside it, the way SQL escapes it. When the driver has no quote character, only a plain
   /// name (letters, digits, underscore) is allowed through unquoted; anything else is refused
   /// rather than pasted raw into SQL.
   /// </summary>
   /// <param name="identifier">The name as the catalog reports it.</param>
   /// <param name="quote">The driver's quote character, or null or " " when it has none.</param>
   /// <returns>The quoted identifier.</returns>
   /// <exception cref="InvalidOperationException">The driver cannot quote and the name needs quoting.</exception>
   public static string Quote( string identifier, string? quote )
   {
      if( string.IsNullOrWhiteSpace( quote ) )
      {
         return PLAIN_IDENTIFIER.IsMatch( identifier )
            ? identifier
            : throw new InvalidOperationException( $"This ODBC driver cannot quote names, so '{identifier}' cannot be read safely." );
      }

      return quote + identifier.Replace( quote, quote + quote, StringComparison.Ordinal ) + quote;
   }

   /// <summary>
   /// The quoted schema-qualified name of a table, or just the quoted name when there is no schema.
   /// </summary>
   /// <param name="table">The table.</param>
   /// <param name="quote">The driver's quote character.</param>
   /// <returns>E.g. "Production"."Product".</returns>
   public static string QualifiedName( OdbcSelection table, string? quote )
   {
      return table.Schema.Length > 0 ? $"{Quote( table.Schema, quote )}.{Quote( table.Name, quote )}" : Quote( table.Name, quote );
   }

   /// <summary>
   /// Asks the driver for its identifier quote character (SQLGetInfo SQL_IDENTIFIER_QUOTE_CHAR,
   /// through OdbcCommandBuilder, which reads it from the open connection).
   /// </summary>
   /// <param name="connection">An open connection.</param>
   /// <returns>The quote character, or null when the driver does not support quoting.</returns>
   public static string? QuoteCharOf( OdbcConnection connection )
   {
      const string PROBE = "x";
      using var builder = new OdbcCommandBuilder();
      string quoted = builder.QuoteIdentifier( PROBE, connection );
      if( quoted == PROBE || quoted.Length < PROBE.Length + 2 )
      {
         return null;
      }

      string prefix = quoted[..( ( quoted.Length - PROBE.Length ) / 2 )];
      return string.IsNullOrWhiteSpace( prefix ) ? null : prefix;
   }

   /// <summary>
   /// The name a connection is known by in origins and table ids. A configured or DSN name is
   /// used as is. A raw connection string becomes "odbc_" plus a hash of its settings WITHOUT the
   /// login, so the name is stable across runs and different users, and a password can never end
   /// up in an origin that is stored in every destination.
   /// </summary>
   /// <param name="connection">Configured name, DSN name, or raw connection string.</param>
   /// <returns>The stable connection name.</returns>
   public static string ConnectionName( string connection )
   {
      string trimmed = connection.Trim();
      if( !trimmed.Contains( '=' ) )
      {
         return trimmed;
      }

      var settings = new SortedDictionary<string, string>( StringComparer.Ordinal );
      try
      {
         var builder = new OdbcConnectionStringBuilder( trimmed );
         foreach( string key in builder.Keys.Cast<string>().Where( k => !LOGIN_KEYS.Contains( k ) ) )
         {
            settings[key.ToLowerInvariant()] = Convert.ToString( builder[key], System.Globalization.CultureInfo.InvariantCulture ) ?? string.Empty;
         }
      }
      catch( ArgumentException )
      {
         // Unparseable: hash a placeholder so nothing of the string, login included, is used.
         settings["unparsed"] = RowDocumentMapper.Hash( trimmed );
      }

      string normalized = string.Join( ';', settings.Select( s => $"{s.Key}={s.Value}" ) );
      return RAW_CONNECTION_PREFIX + RowDocumentMapper.Hash( normalized )[..CONNECTION_HASH_HEX];
   }

   /// <summary>
   /// The origin of a table: "&lt;connection&gt;/&lt;schema&gt;.&lt;name&gt;", or "&lt;connection&gt;/&lt;name&gt;" without a schema.
   /// '/' and '#' in the connection name become '_', because the read report treats them as
   /// container separators.
   /// </summary>
   /// <param name="connectionName">Connection name.</param>
   /// <param name="table">The table.</param>
   /// <returns>The origin.</returns>
   public static string Origin( string connectionName, OdbcSelection table )
   {
      return OriginPrefix( connectionName ) + ( table.Schema.Length > 0 ? $"{table.Schema}.{table.Name}" : table.Name );
   }

   /// <summary>
   /// The part every origin of a connection starts with, ending in '/'.
   /// </summary>
   /// <param name="connectionName">Connection name.</param>
   /// <returns>The prefix.</returns>
   public static string OriginPrefix( string connectionName )
   {
      return ConnectionName( connectionName ).Replace( '/', '_' ).Replace( '#', '_' ) + "/";
   }

   /// <summary>
   /// Every table an origin's table part could mean. "a.b.c" could be schema "a" table "b.c",
   /// schema "a.b" table "c", or a table "a.b.c" with no schema; all are returned so a gone check
   /// can require every reading to be missing.
   /// </summary>
   /// <param name="tablePart">The origin after the connection prefix.</param>
   /// <returns>Candidate tables.</returns>
   public static IEnumerable<OdbcSelection> SelectionsFromOrigin( string tablePart )
   {
      for( int dot = tablePart.IndexOf( '.' ); dot >= 0; dot = tablePart.IndexOf( '.', dot + 1 ) )
      {
         if( dot > 0 && dot < tablePart.Length - 1 )
         {
            yield return new OdbcSelection( tablePart[..dot], tablePart[( dot + 1 )..] );
         }
      }

      yield return new OdbcSelection( string.Empty, tablePart );
   }

   /// <summary>
   /// The display name of a table: schema and name joined by '_', lower-cased, with anything that
   /// is not a letter or digit turned into '_'. "Production.Product" becomes "production_product".
   /// </summary>
   /// <param name="table">The table.</param>
   /// <returns>The display name, never empty.</returns>
   public static string TableName( OdbcSelection table )
   {
      string raw = table.Schema.Length > 0 ? $"{table.Schema}_{table.Name}" : table.Name;
      var name = new StringBuilder( raw.Length );
      foreach( char c in raw.ToLowerInvariant() )
      {
         bool keep = char.IsLetterOrDigit( c );
         if( keep || ( name.Length > 0 && name[^1] != '_' ) )
         {
            name.Append( keep ? c : '_' );
         }
      }

      string result = name.ToString().TrimEnd( '_' );
      return result.Length > 0 ? result : "table";
   }

   /// <summary>
   /// The stable id of a table: "t" plus 16 hex digits of a SHA-256 over the connection name,
   /// schema and table name, exactly as given (no case folding, since some databases treat
   /// "Orders" and "orders" as two tables). It never depends on the columns, so two tables with
   /// the same columns stay separate, and adding a column does not re-key a table.
   /// </summary>
   /// <param name="connectionName">Connection name (a raw connection string is reduced to its stable name first).</param>
   /// <param name="table">The table.</param>
   /// <returns>E.g. "t3f9a2c41d0e7b6a5".</returns>
   public static string TableId( string connectionName, OdbcSelection table )
   {
      string identity = string.Join( SEPARATOR, "odbc", ConnectionName( connectionName ), table.Schema, table.Name );
      return "t" + RowDocumentMapper.Hash( identity )[..TABLE_ID_HEX];
   }

   /// <summary>
   /// True for a column name that usually identifies a row ("id", "order_id", "ProductID").
   /// </summary>
   /// <param name="column">Column name.</param>
   /// <returns>True when it looks like an id or key column.</returns>
   public static bool LooksLikeKey( string column )
   {
      return ID_LIKE.IsMatch( column.Trim() );
   }

   /// <summary>
   /// True for exactly "id" or "key", in any case: the strongest naming hint.
   /// </summary>
   /// <param name="column">Column name.</param>
   /// <returns>True for a bare id or key column.</returns>
   public static bool IsBareKeyName( string column )
   {
      string trimmed = column.Trim();
      return trimmed.Equals( "id", StringComparison.OrdinalIgnoreCase ) || trimmed.Equals( "key", StringComparison.OrdinalIgnoreCase );
   }

   #endregion Public Methods
}
