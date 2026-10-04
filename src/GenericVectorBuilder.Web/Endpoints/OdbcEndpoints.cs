using System.Data.Odbc;
using GenericVectorBuilder.Core.Pipeline;
using GenericVectorBuilder.Core.Sources.Odbc;
using GenericVectorBuilder.Web.Runs;

namespace GenericVectorBuilder.Web.Endpoints;

/// <summary>
/// What the page needs before an ODBC run: the connections it can pick, the tables and views of
/// one, and a preview of one table.
/// Why every database failure comes back as JSON with a plain message: the page shows the text
/// as is. A wrong login, a database that is down and a driver that is not installed are
/// ordinary events for this feature, and each must say what to fix.
/// Why the password and the connection string are masked a second time here: the ODBC layer
/// masks the password it knows about, but a driver's wording is not under our control and these
/// messages go to a browser. The login is read from the request body, used for that one call and
/// dropped; it is never logged and never put in a response.
/// </summary>
public static class OdbcEndpoints
{
   #region Data Members

   private const string NO_DRIVER_MESSAGE = "ODBC is not available on this server. Install unixODBC and the driver for your database, then restart the service.";
   private const string UNEXPECTED_MESSAGE = "The database request failed for a reason the server did not recognise. The details are in the service log.";
   private const string FALLBACK_PIPELINE = "odbc";
   private static readonly string[] PIPELINE_NAME_KEYS = { "DSN", "Database", "Initial Catalog", "DBQ" };

   #endregion Data Members

   #region Public Methods

   /// <summary>
   /// Maps the endpoints.
   /// </summary>
   /// <param name="app">The app.</param>
   public static void Map( WebApplication app )
   {
      app.MapGet( "/api/odbc/connections", ListConnections );
      app.MapPost( "/api/odbc/tables", ListTablesAsync );
      app.MapPost( "/api/odbc/preview", PreviewAsync );
   }

   /// <summary>
   /// The pipeline name to offer for a connection: a configured or DSN name as it is, or for a
   /// raw connection string its DSN, database or file name. Never includes a login.
   /// </summary>
   /// <param name="connection">Configured name, DSN name, or raw connection string.</param>
   /// <returns>A name that is already safe to use as a pipeline name.</returns>
   public static string SuggestPipeline( string connection )
   {
      string trimmed = connection.Trim();
      if( !trimmed.Contains( '=' ) )
      {
         return PipelineNames.Sanitize( trimmed );
      }

      try
      {
         var builder = new OdbcConnectionStringBuilder( trimmed );
         foreach( string key in PIPELINE_NAME_KEYS )
         {
            if( builder.TryGetValue( key, out object? value ) && value is string { Length: > 0 } text )
            {
               return PipelineNames.Sanitize( key == "DBQ" ? Path.GetFileNameWithoutExtension( text ) : text );
            }
         }
      }
      catch( ArgumentException )
      {
         // An unreadable string has no name in it; the fallback below is used.
      }

      return FALLBACK_PIPELINE;
   }

   #endregion Public Methods

   #region Private Methods

   /// <summary>
   /// Lists the configured connections and the DSNs of this server, with the installed drivers.
   /// </summary>
   /// <param name="catalog">The ODBC catalog.</param>
   /// <param name="loggers">Logger factory.</param>
   /// <returns>Connections and driver names, or a JSON error.</returns>
   private static IResult ListConnections( OdbcCatalog catalog, ILoggerFactory loggers )
   {
      try
      {
         return Results.Ok( new { connections = catalog.ListConnections(), drivers = catalog.ListDrivers() } );
      }
      catch( Exception ex ) when( ex is not OperationCanceledException )
      {
         return Failure( ex, Array.Empty<string>(), "listing the connections", loggers );
      }
   }

   /// <summary>
   /// Opens a connection and lists its tables and views with row counts.
   /// </summary>
   /// <param name="request">Connection and login.</param>
   /// <param name="catalog">The ODBC catalog.</param>
   /// <param name="loggers">Logger factory.</param>
   /// <param name="ct">Cancellation.</param>
   /// <returns>The stable connection name, the suggested pipeline name and the tables; a 400 for a bad request; a 502 when the database or driver failed.</returns>
   private static async Task<IResult> ListTablesAsync( OdbcConnectRequest request, OdbcCatalog catalog, ILoggerFactory loggers, CancellationToken ct )
   {
      if( string.IsNullOrWhiteSpace( request.Connection ) )
      {
         return Results.BadRequest( new { error = "Pick an ODBC connection." } );
      }

      var login = new OdbcCredentials( request.User, request.Password );
      try
      {
         IReadOnlyList<OdbcTableInfo> tables = await catalog.ListTablesAsync( request.Connection, login, ct );
         return Results.Ok( new { connection = OdbcCatalog.ConnectionName( request.Connection ), suggestedPipeline = SuggestPipeline( request.Connection ), tables } );
      }
      catch( Exception ex ) when( ex is not OperationCanceledException )
      {
         return Failure( ex, SecretsFor( catalog, request.Connection, login ), "listing the tables", loggers );
      }
   }

   /// <summary>
   /// Previews one table or view: columns, ten sample rows, the suggested row id and the columns left out.
   /// </summary>
   /// <param name="request">Connection, login and the table.</param>
   /// <param name="catalog">The ODBC catalog.</param>
   /// <param name="loggers">Logger factory.</param>
   /// <param name="ct">Cancellation.</param>
   /// <returns>The preview; a 400 for a bad request; a 502 when the database or driver failed.</returns>
   private static async Task<IResult> PreviewAsync( OdbcPreviewRequest request, OdbcCatalog catalog, ILoggerFactory loggers, CancellationToken ct )
   {
      if( string.IsNullOrWhiteSpace( request.Connection ) )
      {
         return Results.BadRequest( new { error = "Pick an ODBC connection." } );
      }

      if( string.IsNullOrWhiteSpace( request.Name ) )
      {
         return Results.BadRequest( new { error = "Pick a table or view to preview." } );
      }

      var login = new OdbcCredentials( request.User, request.Password );
      try
      {
         return Results.Ok( await catalog.PreviewAsync( request.Connection, login, new OdbcSelection( request.Schema ?? string.Empty, request.Name ), ct ) );
      }
      catch( Exception ex ) when( ex is not OperationCanceledException )
      {
         return Failure( ex, SecretsFor( catalog, request.Connection, login ), "previewing a table", loggers );
      }
   }

   /// <summary>
   /// Turns a failure into a JSON answer: 400 when the request itself was wrong (no such
   /// connection, a connection string that cannot be read), 502 when the database, the network
   /// or the driver failed. Every message is masked, and only the kind of exception and the
   /// masked message are logged.
   /// </summary>
   /// <param name="ex">The failure.</param>
   /// <param name="secrets">Secrets to mask out of the message.</param>
   /// <param name="what">What was being done, for the log.</param>
   /// <param name="loggers">Logger factory.</param>
   /// <returns>The JSON answer.</returns>
   private static IResult Failure( Exception ex, IReadOnlyList<string> secrets, string what, ILoggerFactory loggers )
   {
      int status = ex is ArgumentException ? StatusCodes.Status400BadRequest : StatusCodes.Status502BadGateway;
      string message = ex switch
      {
         ArgumentException or InvalidOperationException => SecretScrubber.Scrub( ex.Message, secrets ),
         DllNotFoundException or EntryPointNotFoundException or TypeInitializationException or PlatformNotSupportedException => NO_DRIVER_MESSAGE,
         _ => UNEXPECTED_MESSAGE,
      };

      loggers.CreateLogger( "GenericVectorBuilder.Odbc" ).LogWarning( "ODBC request failed while {What} ({Kind}): {Message}", what, ex.GetType().Name, message );
      return Results.Json( new { error = message }, statusCode: status );
   }

   /// <summary>
   /// Everything a failed request must not repeat: the typed password, a raw connection string
   /// and its password, and the string that would have been opened.
   /// </summary>
   /// <param name="catalog">The ODBC catalog.</param>
   /// <param name="connection">The connection as typed.</param>
   /// <param name="login">The typed login.</param>
   /// <returns>The secrets to mask.</returns>
   private static IReadOnlyList<string> SecretsFor( OdbcCatalog catalog, string connection, OdbcCredentials login )
   {
      List<string> secrets = SecretScrubber.SecretsOf( connection, login.Password ).ToList();
      try
      {
         string resolved = catalog.ResolveConnectionString( connection, login );
         secrets.Add( resolved );
         secrets.AddRange( SecretScrubber.PasswordsIn( resolved ) );
      }
      catch( Exception ex ) when( ex is ArgumentException or InvalidOperationException )
      {
         // The connection could not be resolved, which is the failure being reported; there is no string to mask.
      }

      return secrets;
   }

   #endregion Private Methods
}

/// <summary>
/// Body of POST /api/odbc/tables.
/// Why ToString is overridden: a record prints every member, and one stray log line would write
/// the password.
/// </summary>
/// <param name="Connection">Configured connection name, DSN name, or raw ODBC connection string.</param>
/// <param name="User">User name typed on the page, or null.</param>
/// <param name="Password">Password typed on the page, or null.</param>
public sealed record OdbcConnectRequest( string Connection, string? User, string? Password )
{
   /// <summary>
   /// Describes the request without the password or a raw connection string.
   /// </summary>
   /// <returns>The user and a masked password.</returns>
   public override string ToString()
   {
      return $"OdbcConnectRequest {{ User = {User ?? "(none)"}, Password = {( Password == null ? "(none)" : "***" )} }}";
   }
}

/// <summary>
/// Body of POST /api/odbc/preview.
/// </summary>
/// <param name="Connection">Configured connection name, DSN name, or raw ODBC connection string.</param>
/// <param name="User">User name typed on the page, or null.</param>
/// <param name="Password">Password typed on the page, or null.</param>
/// <param name="Schema">Schema of the table, or null or "" for none.</param>
/// <param name="Name">Table or view name.</param>
public sealed record OdbcPreviewRequest( string Connection, string? User, string? Password, string? Schema, string Name )
{
   /// <summary>
   /// Describes the request without the password or a raw connection string.
   /// </summary>
   /// <returns>The user, a masked password and the table.</returns>
   public override string ToString()
   {
      return $"OdbcPreviewRequest {{ User = {User ?? "(none)"}, Password = {( Password == null ? "(none)" : "***" )}, Table = {Schema}.{Name} }}";
   }
}
