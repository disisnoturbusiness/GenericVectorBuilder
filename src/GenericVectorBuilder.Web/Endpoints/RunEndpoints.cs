using System.Text.Json;
using GenericVectorBuilder.Core.Configuration;
using GenericVectorBuilder.Core.Embedding;
using GenericVectorBuilder.Core.Pipeline;
using GenericVectorBuilder.Core.Sources.Odbc;
using GenericVectorBuilder.Web.Destinations;
using GenericVectorBuilder.Web.Git;
using GenericVectorBuilder.Web.Runs;
using Microsoft.AspNetCore.Http.Json;
using Microsoft.Extensions.Options;

namespace GenericVectorBuilder.Web.Endpoints;

/// <summary>
/// Starting, watching, cancelling and searching runs and pipelines.
/// Progress goes to the browser as Server-Sent Events: one snapshot every half second until
/// the run ends. A reloaded page does not resume the old stream; it fetches the latest
/// snapshot from /api/runs/{id} and opens a new stream, which always carries full state.
/// </summary>
public static class RunEndpoints
{
   #region Data Members

   private static readonly TimeSpan PROGRESS_INTERVAL = TimeSpan.FromMilliseconds( 500 );
   private const int DEFAULT_TOP = 5;
   private const int MAX_TOP = 50;
   private const string RUN_GONE = "That run is no longer on the server. The service may have restarted.";

   #endregion Data Members

   #region Public Methods

   /// <summary>
   /// Maps the endpoints.
   /// </summary>
   /// <param name="app">The app.</param>
   public static void Map( WebApplication app )
   {
      app.MapPost( "/api/runs", StartRunAsync );
      app.MapGet( "/api/runs", ( RunRegistry registry ) => Results.Ok( registry.Recent() ) );
      app.MapGet( "/api/runs/{id}", ( string id, RunRegistry registry ) =>
         registry.Find( id ) is { } job ? Results.Ok( RunRegistry.ConsistentSnapshot( job.Progress ) ) : Results.NotFound( new { error = RUN_GONE } ) );
      app.MapGet( "/api/runs/{id}/events", StreamProgressAsync );
      app.MapPost( "/api/runs/{id}/cancel", ( string id, RunRegistry registry ) =>
         registry.Cancel( id ) ? Results.Ok() : Results.NotFound( new { error = RUN_GONE } ) );

      app.MapGet( "/api/pipelines", async ( GvbServices services, CancellationToken ct ) => Results.Ok( await services.State.ListPipelinesAsync( ct ) ) );
      app.MapPost( "/api/pipelines/{name}/reset", ResetPipelineAsync );
      app.MapPost( "/api/search", SearchAsync );
   }

   #endregion Public Methods

   #region Private Methods

   /// <summary>
   /// Validates and queues a run. A run reads exactly one of a folder, a database and a git
   /// repository: the folder is checked against the allowed data roots, the database request
   /// against the configured connections and DSNs and a non-empty table list, and the repository
   /// address, branch and file types without contacting anything.
   /// </summary>
   /// <param name="request">The run request.</param>
   /// <param name="registry">Run registry.</param>
   /// <param name="services">Services (allowed roots, sink names).</param>
   /// <param name="catalog">ODBC catalog, to check the connection of a database run.</param>
   /// <param name="git">Git workspace, to check the repository of a code run.</param>
   /// <param name="destinations">Destination catalog, to refuse an engine that is not running.</param>
   /// <param name="ct">Cancellation.</param>
   /// <returns>The run id, or a 400 with the reason.</returns>
   internal static async Task<IResult> StartRunAsync( RunRequest request, RunRegistry registry, GvbServices services, OdbcCatalog catalog, GitWorkspace git,
      DestinationCatalog destinations, CancellationToken ct )
   {
      IResult? refusal = CheckSource( request, services, catalog, git );
      if( refusal != null )
      {
         return refusal;
      }

      if( request.Sinks == null || request.Sinks.Count == 0 )
      {
         return Results.BadRequest( new { error = "Pick at least one destination." } );
      }

      try
      {
         services.GetSinks( request.Sinks );
         services.CreateEmbedder( request.Model );
      }
      catch( ArgumentException ex )
      {
         return Results.BadRequest( new { error = ex.Message } );
      }

      IReadOnlyList<( Destination Destination, string Reason )> down = await destinations.UnreachableAsync( request.Sinks, ct );
      if( down.Count > 0 )
      {
         string fix = down.Count == 1 ? "Start it, or untick it, and press Go again." : "Start them, or untick them, and press Go again.";
         return Results.BadRequest( new { error = $"{DestinationCatalog.DescribeDown( down )} {fix}" } );
      }

      RunRequest clean = CleanRequest( request, git );
      EnqueueResult queued = registry.Enqueue( clean );
      return queued.Job switch
      {
         { } job => Results.Ok( new { runId = job.Progress.RunId, pipeline = clean.Pipeline } ),
         _ when queued.Refusal == EnqueueRefusal.PipelineResetting =>
            Results.Json( new { error = $"Pipeline '{clean.Pipeline}' is being reset. Try again in a moment." }, statusCode: StatusCodes.Status409Conflict ),
         _ => Results.Json( new { error = "Too many runs queued. Try again when one finishes." }, statusCode: StatusCodes.Status503ServiceUnavailable ),
      };
   }

   /// <summary>
   /// Checks what the run reads: exactly one of a folder, a database and a git repository.
   /// </summary>
   /// <param name="request">The run request.</param>
   /// <param name="services">Services (allowed roots).</param>
   /// <param name="catalog">ODBC catalog.</param>
   /// <param name="git">Git workspace.</param>
   /// <returns>A 400 (or 502 for a connection that cannot be set up) with the reason, or null when the source is fine.</returns>
   private static IResult? CheckSource( RunRequest request, GvbServices services, OdbcCatalog catalog, GitWorkspace git )
   {
      bool hasFolder = !string.IsNullOrWhiteSpace( request.Path );
      int sources = ( hasFolder ? 1 : 0 ) + ( request.Odbc != null ? 1 : 0 ) + ( request.Git != null ? 1 : 0 );
      if( sources > 1 )
      {
         return Results.BadRequest( new { error = "Send only one of a folder, a database or a git repository." } );
      }

      if( request.Odbc != null )
      {
         return CheckDatabase( request.Odbc, catalog );
      }

      if( request.Git != null )
      {
         return CheckGit( request.Git, git );
      }

      if( !hasFolder )
      {
         return Results.BadRequest( new { error = "Pick a folder, a database or a git repository first." } );
      }

      return services.Settings.IsAllowedDataPath( request.Path )
         ? null
         : Results.BadRequest( new { error = "That folder is outside the allowed data folders." } );
   }

   /// <summary>
   /// Checks a database request: a connection that resolves, and at least one named table. The
   /// connection is resolved here only to fail early with a plain reason; nothing is opened.
   /// </summary>
   /// <param name="odbc">The database request.</param>
   /// <param name="catalog">ODBC catalog.</param>
   /// <returns>A JSON error, or null when the request is fine.</returns>
   private static IResult? CheckDatabase( OdbcRunRequest odbc, OdbcCatalog catalog )
   {
      if( string.IsNullOrWhiteSpace( odbc.Connection ) )
      {
         return Results.BadRequest( new { error = "Pick an ODBC connection." } );
      }

      if( odbc.Tables == null || odbc.Tables.Count == 0 || odbc.Tables.Any( t => t == null || string.IsNullOrWhiteSpace( t.Name ) ) )
      {
         return Results.BadRequest( new { error = "Pick at least one table or view." } );
      }

      try
      {
         catalog.ResolveConnectionString( odbc.Connection, new OdbcCredentials( odbc.User, odbc.Password ) );
         return null;
      }
      catch( ArgumentException ex )
      {
         return Results.BadRequest( new { error = SecretScrubber.Scrub( ex.Message, SecretScrubber.SecretsOf( odbc.Connection, odbc.Password ) ) } );
      }
      catch( InvalidOperationException ex )
      {
         return Results.Json( new { error = SecretScrubber.Scrub( ex.Message, SecretScrubber.SecretsOf( odbc.Connection, odbc.Password ) ) }, statusCode: StatusCodes.Status502BadGateway );
      }
   }

   /// <summary>
   /// Checks a git request without contacting anything: an acceptable address (a local folder
   /// only inside the data folders), branch and file types.
   /// </summary>
   /// <param name="request">The git request.</param>
   /// <param name="git">Git workspace.</param>
   /// <returns>A JSON error, or null when the request is fine.</returns>
   private static IResult? CheckGit( GitRunRequest request, GitWorkspace git )
   {
      try
      {
         git.Open( request.Url, request.Branch );
         GitWorkspace.CleanExtensions( request.Extensions );
         return null;
      }
      catch( ArgumentException ex )
      {
         return Results.BadRequest( new { error = ex.Message } );
      }
   }

   /// <summary>
   /// Cleans the request for the queue: the pipeline name made safe, a folder expanded to a full
   /// path, a database request given an empty folder and no null schemas, and a git request given
   /// an empty folder, clean file types and, when no name was typed, the repository's own name.
   /// </summary>
   /// <param name="request">The checked request.</param>
   /// <param name="git">Git workspace.</param>
   /// <returns>The request to queue.</returns>
   private static RunRequest CleanRequest( RunRequest request, GitWorkspace git )
   {
      if( request.Git != null )
      {
         GitRunRequest code = request.Git;
         string named = string.IsNullOrWhiteSpace( request.Pipeline ) ? GitWorkspace.SuggestPipeline( code.Url ) : request.Pipeline;
         string? branch = string.IsNullOrWhiteSpace( code.Branch ) ? null : code.Branch.Trim();
         GitRunRequest cleanGit = code with { Url = git.Open( code.Url, branch ).Url, Branch = branch, Extensions = GitWorkspace.CleanExtensions( code.Extensions ) };
         return request with { Pipeline = PipelineNames.Sanitize( named ), Path = string.Empty, Git = cleanGit };
      }

      string pipeline = PipelineNames.Sanitize( request.Pipeline );
      if( request.Odbc == null )
      {
         return request with { Pipeline = pipeline, Path = GvbSettings.Expand( request.Path ) };
      }

      IReadOnlyList<OdbcSelection> tables = request.Odbc.Tables.Select( t => new OdbcSelection( t.Schema ?? string.Empty, t.Name ) ).ToList();
      return request with { Pipeline = pipeline, Path = string.Empty, Odbc = request.Odbc with { Connection = request.Odbc.Connection.Trim(), Tables = tables } };
   }

   /// <summary>
   /// Drops a pipeline from every destination. Refused while the pipeline has a queued or
   /// running run, and a run for it cannot start until the reset is done.
   /// </summary>
   /// <param name="name">Pipeline name.</param>
   /// <param name="services">Services.</param>
   /// <param name="registry">Run registry, to keep runs and the reset apart.</param>
   /// <param name="ct">Cancellation.</param>
   /// <returns>200 when everything was removed; 409 while a run is active; 502 when a destination could not be reached.</returns>
   private static async Task<IResult> ResetPipelineAsync( string name, GvbServices services, RunRegistry registry, CancellationToken ct )
   {
      string pipeline = PipelineNames.Sanitize( name );
      if( !registry.TryBeginReset( pipeline ) )
      {
         return Results.Json( new { error = $"Pipeline '{pipeline}' has a run queued or running, or is already being reset. Cancel the run or wait for it to finish first." }, statusCode: StatusCodes.Status409Conflict );
      }

      try
      {
         await services.ResetPipelineAsync( pipeline, ct );
         return Results.Ok();
      }
      catch( ResetIncompleteException ex )
      {
         return Results.Json( new { error = ex.Message }, statusCode: StatusCodes.Status502BadGateway );
      }
      finally
      {
         registry.EndReset( pipeline );
      }
   }

   /// <summary>
   /// Streams progress snapshots as Server-Sent Events until the run ends, the browser leaves,
   /// or the app starts shutting down. Watching ApplicationStopping matters: Kestrel waits for
   /// open streams before it stops, so a stream that only ends with the run would hold a
   /// restart for the full shutdown timeout.
   /// </summary>
   /// <param name="id">Run id.</param>
   /// <param name="context">HTTP context.</param>
   /// <param name="registry">Run registry.</param>
   /// <param name="jsonOptions">App JSON options (enum names as strings).</param>
   /// <param name="lifetime">App lifetime, for the shutdown signal.</param>
   private static async Task StreamProgressAsync( string id, HttpContext context, RunRegistry registry, IOptions<JsonOptions> jsonOptions, IHostApplicationLifetime lifetime )
   {
      RunJob? job = registry.Find( id );
      if( job == null )
      {
         context.Response.StatusCode = StatusCodes.Status404NotFound;
         await context.Response.WriteAsJsonAsync( new { error = RUN_GONE } );
         return;
      }

      context.Response.Headers.ContentType = "text/event-stream";
      context.Response.Headers.CacheControl = "no-cache";
      using var linked = CancellationTokenSource.CreateLinkedTokenSource( context.RequestAborted, lifetime.ApplicationStopping );
      CancellationToken ct = linked.Token;
      try
      {
         while( !ct.IsCancellationRequested )
         {
            RunSnapshot snapshot = RunRegistry.ConsistentSnapshot( job.Progress );
            string json = JsonSerializer.Serialize( snapshot, jsonOptions.Value.SerializerOptions );
            await context.Response.WriteAsync( $"data: {json}\n\n", ct );
            await context.Response.Body.FlushAsync( ct );
            if( snapshot.FinishedUtc.HasValue )
            {
               return;
            }

            await Task.Delay( PROGRESS_INTERVAL, ct );
         }
      }
      catch( OperationCanceledException )
      {
         // The browser left or the app is stopping; either way the stream simply ends.
      }
   }

   /// <summary>
   /// Runs a search against a built pipeline in each requested destination (SQL Server and
   /// Qdrant when none are named). An engine that is not running is not asked; its entry says
   /// so, and when none of the chosen destinations is running the search is a 400. Every
   /// failure comes back as JSON with a plain-English reason: 400 for a bad question, unknown
   /// or stale pipeline, 503 when the embedding service is down or too slow, 500 for anything
   /// else (also written to the service log).
   /// </summary>
   /// <param name="request">Search request.</param>
   /// <param name="services">Services.</param>
   /// <param name="destinations">Destination catalog, for the default list and the running check.</param>
   /// <param name="loggers">Logger factory, for failures that are not the caller's fault.</param>
   /// <param name="ct">Cancellation.</param>
   /// <returns>Hits per destination.</returns>
   internal static async Task<IResult> SearchAsync( SearchRequest request, GvbServices services, DestinationCatalog destinations, ILoggerFactory loggers, CancellationToken ct )
   {
      if( string.IsNullOrWhiteSpace( request.Query ) )
      {
         return Results.BadRequest( new { error = "Type a question first." } );
      }

      if( string.IsNullOrWhiteSpace( request.Pipeline ) )
      {
         return Results.BadRequest( new { error = "Pick a pipeline to search. Build one first if the list is empty." } );
      }

      try
      {
         int top = Math.Clamp( request.Top ?? DEFAULT_TOP, 1, MAX_TOP );
         List<string> sinks = ( request.Sinks is { Count: > 0 } ? request.Sinks : destinations.DefaultNames ).Distinct( StringComparer.OrdinalIgnoreCase ).ToList();
         services.GetSinks( sinks );
         IReadOnlyList<( Destination Destination, string Reason )> down = await destinations.UnreachableAsync( sinks, ct );
         if( down.Count == sinks.Count )
         {
            return Results.BadRequest( new { error = $"None of the chosen destinations is running. {DestinationCatalog.DescribeDown( down )}" } );
         }

         HashSet<string> skipped = down.Select( d => d.Destination.Name ).ToHashSet( StringComparer.OrdinalIgnoreCase );
         Dictionary<string, object> hits = await services.SearchAsync( PipelineNames.Sanitize( request.Pipeline ), request.Query, sinks.Where( s => !skipped.Contains( s ) ), top, ct );
         return Results.Ok( InRequestOrder( sinks, hits, down ) );
      }
      catch( Exception ex ) when( ex is InvalidOperationException or ArgumentException )
      {
         return Results.BadRequest( new { error = ex.Message } );
      }
      catch( EmbedderUnavailableException ex )
      {
         return Results.Json( new { error = ex.Message }, statusCode: StatusCodes.Status503ServiceUnavailable );
      }
      catch( Exception ex ) when( ex is not OperationCanceledException )
      {
         loggers.CreateLogger( "GenericVectorBuilder.Search" ).LogError( ex, "Search failed for pipeline {Pipeline}", request.Pipeline );
         return Results.Json( new { error = $"The search failed: {ex.Message}" }, statusCode: StatusCodes.Status500InternalServerError );
      }
   }

   /// <summary>
   /// Puts search answers in the order the destinations were asked for, with an entry for each
   /// one that was skipped because it is not running.
   /// </summary>
   /// <param name="sinks">The destinations asked for, in order.</param>
   /// <param name="hits">Answers from the destinations that were searched.</param>
   /// <param name="down">The destinations that were skipped, with the reason.</param>
   /// <returns>One entry per destination.</returns>
   private static Dictionary<string, object> InRequestOrder( IReadOnlyList<string> sinks, Dictionary<string, object> hits, IReadOnlyList<( Destination Destination, string Reason )> down )
   {
      var ordered = new Dictionary<string, object>( StringComparer.Ordinal );
      foreach( string sink in sinks )
      {
         ( Destination Destination, string Reason ) skipped = down.FirstOrDefault( d => string.Equals( d.Destination.Name, sink, StringComparison.OrdinalIgnoreCase ) );
         if( skipped.Destination != null )
         {
            ordered[skipped.Destination.Name] = new { error = $"{skipped.Destination.DisplayName} is {skipped.Reason}." };
         }
         else if( hits.FirstOrDefault( h => string.Equals( h.Key, sink, StringComparison.OrdinalIgnoreCase ) ) is { Key: not null } found )
         {
            ordered[found.Key] = found.Value;
         }
      }

      return ordered;
   }

   #endregion Private Methods
}

/// <summary>
/// Body of POST /api/search.
/// </summary>
/// <param name="Pipeline">Pipeline to search.</param>
/// <param name="Query">The question.</param>
/// <param name="Sinks">Destinations to search; SQL Server and Qdrant when empty.</param>
/// <param name="Top">Hits per destination.</param>
public sealed record SearchRequest( string Pipeline, string Query, IReadOnlyList<string>? Sinks, int? Top );
