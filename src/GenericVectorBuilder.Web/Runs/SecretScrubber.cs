using System.Data.Odbc;

namespace GenericVectorBuilder.Web.Runs;

/// <summary>
/// Masks passwords and connection strings in text that is about to be shown or logged.
/// Why a second line of defence: the ODBC layer already masks the password it knows about, but a
/// driver's own wording is not under our control, and a message that reaches the browser or the
/// run history must never carry a secret. This runs on every ODBC message the web layer passes
/// on, using what the web layer knows (the typed password, the connection as typed, the string
/// that was actually opened).
/// </summary>
public static class SecretScrubber
{
   #region Data Members

   /// <summary>What a secret is replaced by.</summary>
   public const string MASK = "***";

   private static readonly string[] PASSWORD_KEYS = { "PWD", "Password" };

   #endregion Data Members

   #region Public Methods

   /// <summary>
   /// Replaces every occurrence of every secret with "***". Longer secrets go first, so a whole
   /// connection string is masked as one piece before the password inside it is looked at.
   /// </summary>
   /// <param name="text">The text, or null.</param>
   /// <param name="secrets">Secrets to mask; null and empty entries are ignored.</param>
   /// <returns>The masked text; empty when the text was null.</returns>
   public static string Scrub( string? text, IEnumerable<string?> secrets )
   {
      string result = text ?? string.Empty;
      foreach( string secret in secrets.Where( s => !string.IsNullOrEmpty( s ) ).Select( s => s! ).Distinct( StringComparer.Ordinal ).OrderByDescending( s => s.Length ) )
      {
         result = result.Replace( secret, MASK, StringComparison.Ordinal );
      }

      return result;
   }

   /// <summary>
   /// The secrets a request can put into an error message: the typed password, and for a raw
   /// connection string the whole string and any password inside it.
   /// </summary>
   /// <param name="connection">The connection as typed: a name or a raw ODBC connection string.</param>
   /// <param name="password">The typed password, or null.</param>
   /// <returns>The secrets to mask.</returns>
   public static IReadOnlyList<string> SecretsOf( string? connection, string? password )
   {
      var secrets = new List<string>();
      if( !string.IsNullOrEmpty( password ) )
      {
         secrets.Add( password );
      }

      if( connection != null && connection.Contains( '=' ) )
      {
         secrets.Add( connection.Trim() );
         secrets.AddRange( PasswordsIn( connection ) );
      }

      return secrets;
   }

   /// <summary>
   /// The password values inside a connection string (PWD or Password).
   /// </summary>
   /// <param name="connectionString">The connection string.</param>
   /// <returns>The passwords; empty when there are none or the string cannot be parsed.</returns>
   public static IReadOnlyList<string> PasswordsIn( string connectionString )
   {
      try
      {
         var builder = new OdbcConnectionStringBuilder( connectionString );
         return PASSWORD_KEYS
            .Select( k => builder.TryGetValue( k, out object? value ) ? value as string : null )
            .Where( v => !string.IsNullOrEmpty( v ) )
            .Select( v => v! )
            .ToList();
      }
      catch( ArgumentException )
      {
         return Array.Empty<string>();
      }
   }

   #endregion Public Methods
}
