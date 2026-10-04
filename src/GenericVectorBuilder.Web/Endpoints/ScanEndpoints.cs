using GenericVectorBuilder.Core.Configuration;
using GenericVectorBuilder.Core.Embedding;
using GenericVectorBuilder.Core.Sources.Files;
using GenericVectorBuilder.Web.Destinations;

namespace GenericVectorBuilder.Web.Endpoints;

/// <summary>
/// Folder scanning, browser uploads and the settings the page needs to start.
/// Why uploads land in a folder and then get scanned like any other folder: the run path is
/// identical whether the files came from the Samba share or a browser drag, so there is one
/// code path to trust.
/// </summary>
public static class ScanEndpoints
{
   #region Data Members

   private const long MAX_UPLOAD_REQUEST_BYTES = 1L * 1024 * 1024 * 1024;
   private const int UPLOAD_ID_LENGTH = 12;

   #endregion Data Members

   #region Public Methods

   /// <summary>
   /// Maps the endpoints.
   /// </summary>
   /// <param name="app">The app.</param>
   public static void Map( WebApplication app )
   {
      app.MapGet( "/api/config", ( GvbServices services, DestinationCatalog destinations ) => Results.Ok( new
      {
         roots = services.Settings.DataRoots.Select( GvbSettings.Expand ),
         sinks = destinations.All.Select( d => d.Name ),
         destinations = destinations.All.Select( d => new { name = d.Name, displayName = d.DisplayName, optional = d.Optional, defaultOn = d.DefaultOn } ),
         models = EmbedderProfile.All.Select( p => new { key = p.ModelKey, measured = p.Measured } ),
         defaultModel = services.Settings.DefaultEmbedModel,
      } ) );

      app.MapGet( "/api/health", HealthAsync );

      app.MapPost( "/api/scan", ( ScanRequest request, GvbServices services, CancellationToken ct ) => Scan( request, services, ct ) );

      app.MapPost( "/api/uploads", ( GvbServices services ) =>
      {
         string id = Guid.NewGuid().ToString( "N" )[..UPLOAD_ID_LENGTH];
         string path = services.Settings.UploadFolder( id );
         Directory.CreateDirectory( path );
         return Results.Ok( new { id, path } );
      } );

      app.MapPost( "/api/uploads/{id}/files", SaveUploadAsync ).DisableAntiforgery()
         .WithMetadata( new Microsoft.AspNetCore.Mvc.RequestSizeLimitAttribute( MAX_UPLOAD_REQUEST_BYTES ) );
   }

   #endregion Public Methods

   #region Private Methods

   /// <summary>
   /// Checks every dependency for the page: the embedding service, SQL Server and Qdrant (the
   /// services' own checks), and every engine destination within two seconds each, all at once.
   /// Nothing is started; a stopped engine is simply "not running".
   /// </summary>
   /// <param name="services">Services.</param>
   /// <param name="destinations">Destination catalog.</param>
   /// <param name="ct">Cancellation.</param>
   /// <returns>"ok" or the reason, by name: "embedder", "sql", "qdrant", then each engine.</returns>
   private static async Task<IResult> HealthAsync( GvbServices services, DestinationCatalog destinations, CancellationToken ct )
   {
      Task<Dictionary<string, string>> core = services.HealthAsync( ct );
      Task<IReadOnlyDictionary<string, string>> engines = destinations.ProbeOptionalAsync( ct );
      Dictionary<string, string> health = await core;
      foreach( KeyValuePair<string, string> engine in await engines )
      {
         health[engine.Key] = engine.Value;
      }

      return Results.Ok( health );
   }

   /// <summary>
   /// Scans an allowed folder and returns tables, previews, rejects and ignored files.
   /// </summary>
   /// <param name="request">Folder to scan.</param>
   /// <param name="services">Services (for the allowed roots).</param>
   /// <param name="ct">Cancellation.</param>
   /// <returns>The scan as JSON, or a 400 with the reason.</returns>
   private static IResult Scan( ScanRequest request, GvbServices services, CancellationToken ct )
   {
      if( string.IsNullOrWhiteSpace( request.Path ) )
      {
         return Results.BadRequest( new { error = "Pick a folder first." } );
      }

      if( !services.Settings.IsAllowedDataPath( request.Path ) )
      {
         return Results.BadRequest( new { error = "That folder is outside the allowed data folders." } );
      }

      try
      {
         FolderScanResult scan = new FolderScanner().Scan( GvbSettings.Expand( request.Path ), ct );
         return Results.Ok( ToDto( scan ) );
      }
      catch( Exception ex ) when( ex is DirectoryNotFoundException or UnauthorizedAccessException )
      {
         return Results.BadRequest( new { error = ex.Message } );
      }
   }

   /// <summary>
   /// Saves a batch of uploaded files under the upload folder, keeping each file's relative
   /// path (sent as the multipart file name) so sub-folders survive. The whole batch is
   /// checked before anything is written: one refused name (".." climbs, absolute paths, NUL
   /// characters, a name that is a folder) refuses the batch and leaves nothing behind. If
   /// writing fails part way, the files this batch created are removed again, so a batch is
   /// saved completely or not at all.
   /// </summary>
   /// <param name="id">Upload id from POST /api/uploads.</param>
   /// <param name="request">The HTTP request.</param>
   /// <param name="services">Services (for the upload root).</param>
   /// <param name="ct">Cancellation.</param>
   /// <returns>How many files were saved, or a JSON error (400 bad request, 413 too big, 500 disk trouble).</returns>
   private static async Task<IResult> SaveUploadAsync( string id, HttpRequest request, GvbServices services, CancellationToken ct )
   {
      string root = services.Settings.UploadFolder( id );
      if( id.Length != UPLOAD_ID_LENGTH || !id.All( Uri.IsHexDigit ) || !Directory.Exists( root ) )
      {
         return Results.BadRequest( new { error = "Unknown upload. Choose the folder again." } );
      }

      if( !request.HasFormContentType )
      {
         return Results.BadRequest( new { error = "The files must be sent as a multipart form." } );
      }

      IFormCollection form;
      try
      {
         form = await request.ReadFormAsync( ct );
      }
      catch( Exception ex ) when( ex is InvalidDataException or BadHttpRequestException )
      {
         int status = ex is BadHttpRequestException bad ? bad.StatusCode : StatusCodes.Status400BadRequest;
         return Results.Json( new { error = $"The upload could not be read: {ex.Message}" }, statusCode: status );
      }

      var plan = new List<( IFormFile File, string Target )>();
      foreach( IFormFile file in form.Files )
      {
         if( !GvbSettings.TryResolveUploadTarget( root, file.FileName, out string target ) )
         {
            return Results.BadRequest( new { error = $"Refused path '{file.FileName.Replace( '\0', '?' )}'. Nothing from this batch was saved." } );
         }

         plan.Add( ( file, target ) );
      }

      return await WriteBatchAsync( plan, ct );
   }

   /// <summary>
   /// Writes an already checked batch, removing the files this batch created if anything
   /// fails part way.
   /// </summary>
   /// <param name="plan">Each file with the path it will be written to.</param>
   /// <param name="ct">Cancellation.</param>
   /// <returns>How many files were saved, or a 500 with the reason.</returns>
   private static async Task<IResult> WriteBatchAsync( List<( IFormFile File, string Target )> plan, CancellationToken ct )
   {
      var created = new List<string>();
      try
      {
         foreach( ( IFormFile file, string target ) in plan )
         {
            Directory.CreateDirectory( Path.GetDirectoryName( target )! );
            if( !File.Exists( target ) )
            {
               created.Add( target );
            }

            await using FileStream output = File.Create( target );
            await file.CopyToAsync( output, ct );
         }

         return Results.Ok( new { saved = plan.Count } );
      }
      catch( Exception ex ) when( ex is IOException or UnauthorizedAccessException )
      {
         DeleteQuietly( created );
         return Results.Json( new { error = $"Could not save the files: {ex.Message} Nothing from this batch was kept." }, statusCode: StatusCodes.Status500InternalServerError );
      }
      catch
      {
         DeleteQuietly( created );
         throw;
      }
   }

   /// <summary>
   /// Deletes files, ignoring any that cannot be removed.
   /// </summary>
   /// <param name="paths">Files to delete.</param>
   private static void DeleteQuietly( IEnumerable<string> paths )
   {
      foreach( string path in paths )
      {
         try
         {
            File.Delete( path );
         }
         catch( Exception ex ) when( ex is IOException or UnauthorizedAccessException )
         {
            // Best effort; a leftover partial file is harmless and the next upload overwrites it.
         }
      }
   }

   /// <summary>
   /// Flattens a scan into JSON-friendly shapes (encodings as names, delimiters as labels).
   /// </summary>
   /// <param name="scan">The scan.</param>
   /// <returns>An anonymous object for serialization.</returns>
   private static object ToDto( FolderScanResult scan )
   {
      return new
      {
         root = scan.Root,
         totalRows = scan.TotalRows,
         tables = scan.Tables.Select( t => new
         {
            name = t.Name,
            columns = t.Columns,
            rows = t.DataRows,
            suggestedKey = t.SuggestedKey,
            sample = t.Sample,
            files = t.Origins.Select( o => new
            {
               origin = o.Spec.Origin,
               rows = o.DataRows,
               badRows = o.BadRows,
               encoding = o.Spec.Kind == OriginKind.Delimited ? o.Spec.Profile.Encoding.WebName : "excel",
               delimiter = DelimiterLabel( o.Spec ),
               header = o.Spec.Profile.HasHeader,
            } ),
         } ),
         rejected = scan.Rejected,
         ignored = scan.Ignored,
      };
   }

   /// <summary>
   /// Human label for a delimiter.
   /// </summary>
   /// <param name="spec">Origin.</param>
   /// <returns>"comma", "pipe", "tab", "semicolon" or "sheet".</returns>
   private static string DelimiterLabel( OriginSpec spec )
   {
      return spec.Kind == OriginKind.ExcelSheet ? "sheet" : spec.Profile.Delimiter switch
      {
         ',' => "comma",
         '|' => "pipe",
         '\t' => "tab",
         ';' => "semicolon",
         var c => c.ToString(),
      };
   }

   #endregion Private Methods
}

/// <summary>
/// Body of POST /api/scan.
/// </summary>
/// <param name="Path">Folder to scan.</param>
public sealed record ScanRequest( string Path );
