using System.Data.Odbc;
using System.Text.RegularExpressions;

namespace GenericVectorBuilder.Core.Sources.Odbc;

/// <summary>
/// Turns ODBC failures into short plain-English reasons for the reject list and error messages,
/// and keeps passwords out of every one of them.
/// Why the SQLSTATE drives the wording: the driver text alone ("ERROR [42000] [Microsoft][ODBC
/// Driver 18 for SQL Server][SQL Server]...") is noisy, and the state code is what reliably
/// separates "the table is gone" from "you may not read it" from "the network dropped".
/// Why redaction is applied to every message: drivers do not promise to keep the connection
/// string out of their errors, and a reason is shown in the browser and kept in run history.
/// </summary>
internal static class OdbcErrors
{
   #region Data Members

   /// <summary>SQLSTATE for "base table or view not found".</summary>
   public const string NOT_FOUND_STATE = "42S02";

   private const string MASK = "***";

   /// <summary>The "ERROR [42000] [vendor][driver][server]" prefix drivers put in front of the real text.</summary>
   private static readonly Regex VENDOR_PREFIX = new( @"^\s*(ERROR\s*\[[^\]]*\]\s*)?(\[[^\]]*\]\s*)*", RegexOptions.Compiled );

   /// <summary>Connection string keys whose value is a secret.</summary>
   private static readonly string[] SECRET_KEYS = { "PWD", "Password" };

   /// <summary>SQLSTATEs that only say a time limit passed (ODBC 3 and ODBC 2 spellings).</summary>
   private static readonly HashSet<string> TIMEOUT_STATES = new( StringComparer.Ordinal ) { "HYT00", "HYT01", "S1T00" };

   #endregion Data Members

   #region Public Methods

   /// <summary>
   /// A plain reason for any failure while reading or listing, with the secret masked.
   /// </summary>
   /// <param name="ex">The failure.</param>
   /// <param name="secret">The password in use, or null.</param>
   /// <returns>The reason.</returns>
   public static string Describe( Exception ex, string? secret )
   {
      if( ex is OdbcException odbc && odbc.Errors.Count > 0 )
      {
         int index = MostSpecific( odbc.Errors.Cast<OdbcError>().Select( e => e.SQLState ).ToList() );
         return Describe( odbc.Errors[index].SQLState, OwnText( odbc.Errors, index ), secret );
      }

      return Redact( ex.Message, secret );
   }

   /// <summary>
   /// Which of a failure's diagnostic records to describe. Normally the first. But when the first
   /// only says the login timed out and a later record says the connection failed (SQLSTATE 08xxx),
   /// the last such record is used, because it names the cause.
   /// Why: the SQL Server driver reports a server name that does not resolve, a closed port and a
   /// host that never answers all as "Login timeout expired" first, with "Server is not found or
   /// not accessible" only in the records after it (measured 2026-10-03). "Did not answer in
   /// time" would send the user to wait for a server that does not exist.
   /// </summary>
   /// <param name="sqlStates">The SQLSTATE of each record, in the driver's order.</param>
   /// <returns>The index of the record to describe; 0 for an empty list.</returns>
   public static int MostSpecific( IReadOnlyList<string?> sqlStates )
   {
      if( sqlStates.Count == 0 || !TIMEOUT_STATES.Contains( sqlStates[0] ?? string.Empty ) )
      {
         return 0;
      }

      for( int i = sqlStates.Count - 1; i > 0; i-- )
      {
         if( sqlStates[i]?.StartsWith( "08", StringComparison.Ordinal ) == true )
         {
            return i;
         }
      }

      return 0;
   }

   /// <summary>
   /// A plain reason from a SQLSTATE and the driver's message: a short summary, then the
   /// driver's own words without the vendor tags, with the secret masked.
   /// </summary>
   /// <param name="sqlState">Five-character SQLSTATE, or null.</param>
   /// <param name="message">The driver's message.</param>
   /// <param name="secret">The password in use, or null.</param>
   /// <returns>The reason.</returns>
   public static string Describe( string? sqlState, string? message, string? secret )
   {
      string detail = Redact( VENDOR_PREFIX.Replace( message ?? string.Empty, string.Empty ).Trim(), secret );
      string summary = Summary( sqlState ?? string.Empty, detail );
      return detail.Length == 0 || detail.Equals( summary, StringComparison.OrdinalIgnoreCase ) ? summary : $"{summary} ({detail})";
   }

   /// <summary>
   /// Masks every occurrence of the secret in a text.
   /// </summary>
   /// <param name="text">Text that may contain the secret.</param>
   /// <param name="secret">The secret, or null or empty for nothing to mask.</param>
   /// <returns>The text with the secret replaced by "***".</returns>
   public static string Redact( string text, string? secret )
   {
      return string.IsNullOrEmpty( secret ) ? text : text.Replace( secret, MASK, StringComparison.Ordinal );
   }

   /// <summary>
   /// The password in a connection string (PWD or Password), so it can be masked in messages.
   /// </summary>
   /// <param name="connectionString">The connection string.</param>
   /// <returns>The password, or null when there is none or the string cannot be parsed.</returns>
   public static string? SecretOf( string connectionString )
   {
      try
      {
         var builder = new OdbcConnectionStringBuilder( connectionString );
         foreach( string key in SECRET_KEYS )
         {
            if( builder.TryGetValue( key, out object? value ) && value is string text && text.Length > 0 )
            {
               return text;
            }
         }

         return null;
      }
      catch( ArgumentException )
      {
         return null;
      }
   }

   /// <summary>
   /// A connection string safe to show: every password value replaced by "***".
   /// </summary>
   /// <param name="connectionString">The connection string.</param>
   /// <returns>The masked string, or "(unreadable connection string)" when it cannot be parsed.</returns>
   public static string RedactConnectionString( string connectionString )
   {
      try
      {
         var builder = new OdbcConnectionStringBuilder( connectionString );
         foreach( string key in builder.Keys.Cast<string>().Where( k => SECRET_KEYS.Contains( k, StringComparer.OrdinalIgnoreCase ) ).ToList() )
         {
            builder[key] = MASK;
         }

         return builder.ConnectionString;
      }
      catch( ArgumentException )
      {
         return "(unreadable connection string)";
      }
   }

   /// <summary>
   /// True when the failure says that this table or view does not exist: SQLSTATE 42S02 with a
   /// message that names it.
   /// Why the name must be in the message: 42S02 also comes back for a view that still exists
   /// when a table INSIDE the view is missing ("Invalid object name 'dbo.Base'" for a query on
   /// dbo.BaseView). For a view the login may not see, which SQL Server leaves out of the catalog,
   /// that would confirm the view gone and delete its vectors (measured 2026-10-03).
   /// </summary>
   /// <param name="ex">The failure.</param>
   /// <param name="table">The table that was queried.</param>
   /// <returns>True for "not found" about this table.</returns>
   public static bool IsNotFound( Exception ex, OdbcSelection table )
   {
      return ex is OdbcException odbc
         && Enumerable.Range( 0, odbc.Errors.Count ).Any( i => odbc.Errors[i].SQLState == NOT_FOUND_STATE && NamesTable( OwnText( odbc.Errors, i ), table ) );
   }

   /// <summary>
   /// The text of one diagnostic record on its own, without the records before it.
   /// Why: System.Data.Odbc builds each record's message on top of the earlier ones (measured
   /// 2026-10-03: the third record of a failed connect starts with the text of the first two), so
   /// a later record would repeat "Login timeout expired" and could name an object it is not about.
   /// </summary>
   /// <param name="message">The record's message as System.Data.Odbc reports it.</param>
   /// <param name="previous">The message of the record before it, or null for the first record.</param>
   /// <returns>The record's own text.</returns>
   public static string OwnText( string? message, string? previous )
   {
      string text = message ?? string.Empty;
      return !string.IsNullOrEmpty( previous ) && text.Length > previous.Length && text.StartsWith( previous, StringComparison.Ordinal )
         ? text[previous.Length..]
         : text;
   }

   /// <summary>
   /// True when a driver message names a table: "schema.name" (or just the name when there is no
   /// schema), compared without case, and not as part of a longer name ("dbo.Base" is not named
   /// by "dbo.BaseView" or "dbo.Base2").
   /// </summary>
   /// <param name="message">The driver's message.</param>
   /// <param name="table">The table.</param>
   /// <returns>True when the message names the table.</returns>
   public static bool NamesTable( string? message, OdbcSelection table )
   {
      string text = message ?? string.Empty;
      string name = table.Schema.Length > 0 ? $"{table.Schema}.{table.Name}" : table.Name;
      for( int at = text.IndexOf( name, StringComparison.OrdinalIgnoreCase ); at >= 0 && name.Length > 0;
         at = text.IndexOf( name, at + 1, StringComparison.OrdinalIgnoreCase ) )
      {
         int end = at + name.Length;
         if( ( at == 0 || !IsNameChar( text[at - 1] ) ) && ( end == text.Length || !IsNameChar( text[end] ) ) )
         {
            return true;
         }
      }

      return false;
   }

   #endregion Public Methods

   #region Private Methods

   /// <summary>
   /// The text of one record of a failure on its own (see <see cref="OwnText(string, string)"/>).
   /// </summary>
   /// <param name="errors">The failure's records.</param>
   /// <param name="index">Which record.</param>
   /// <returns>The record's own text.</returns>
   private static string OwnText( OdbcErrorCollection errors, int index )
   {
      return OwnText( errors[index].Message, index > 0 ? errors[index - 1].Message : null );
   }

   /// <summary>
   /// True for a character that can continue an unquoted name: a letter, a digit or '_'.
   /// </summary>
   /// <param name="c">The character.</param>
   /// <returns>True when it would make the name longer.</returns>
   private static bool IsNameChar( char c )
   {
      return char.IsLetterOrDigit( c ) || c == '_';
   }

   /// <summary>
   /// The short summary for a SQLSTATE.
   /// </summary>
   /// <param name="sqlState">SQLSTATE.</param>
   /// <param name="detail">Driver text, used to spot permission errors inside the generic 42000 class.</param>
   /// <returns>The summary sentence.</returns>
   private static string Summary( string sqlState, string detail )
   {
      return sqlState switch
      {
         NOT_FOUND_STATE => "The table or view was not found; it may have been dropped or renamed.",
         "28000" => "The database refused the login.",
         "08S01" => "The connection to the database was lost.",
         "08001" => "Could not connect to the database.",
         "HYT00" or "HYT01" => "The database did not answer in time.",
         "IM002" => "No ODBC data source or driver by that name.",
         "HY008" => "The read was cancelled.",
         "42000" when detail.Contains( "permission", StringComparison.OrdinalIgnoreCase ) => "Permission denied.",
         _ when sqlState.StartsWith( "08", StringComparison.Ordinal ) => "The connection to the database failed.",
         _ when sqlState.StartsWith( "IM", StringComparison.Ordinal ) => "The ODBC driver could not be loaded or used.",
         _ => "The database reported an error.",
      };
   }

   #endregion Private Methods
}
