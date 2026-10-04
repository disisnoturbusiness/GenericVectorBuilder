using System.Net;
using System.Text.Json;

namespace GenericVectorBuilder.Engines.Sinks;

/// <summary>
/// HTTP access to a Vespa node: the config server (deploying the application package and reading
/// back the deployed schemas) and the query and feed container (documents and searches).
/// Why two HTTP clients: the config server is called rarely with big requests and speaks plain
/// HTTP/1.1. The feed port gets thousands of small requests, and Vespa's own feed client uses HTTP/2
/// for that, so the data client sends HTTP/2 without TLS (prior knowledge) to let many documents share
/// a few connections instead of opening one per document.
/// Transient failures (connection errors, 429 and 502 to 504) are retried with a short backoff.
/// A 507 means Vespa has stopped accepting writes because memory or disk is nearly full; that is
/// reported at once with a plain message, since retrying cannot fix it.
/// </summary>
internal sealed class VespaRest : IDisposable
{
   #region Data Members

   private const int MAX_ATTEMPTS = 4;
   private const string CONVERGE_PATH = "application/v2/tenant/default/application/default/environment/prod/region/default/instance/default";
   private static readonly TimeSpan READY_TIMEOUT = TimeSpan.FromMinutes( 3 );

   private readonly HttpClient _config;
   private readonly HttpClient _data;
   private readonly string _configUrl;
   private readonly string _queryUrl;

   #endregion Data Members

   #region Constructor

   /// <summary>
   /// Creates the clients without contacting the server.
   /// </summary>
   /// <param name="options">Addresses and time limit.</param>
   public VespaRest( VespaSinkOptions options )
   {
      _configUrl = options.ConfigUrl.TrimEnd( '/' );
      _queryUrl = options.QueryUrl.TrimEnd( '/' );
      _config = new HttpClient { BaseAddress = new Uri( _configUrl + "/" ), Timeout = options.EffectiveTimeout };
      var handler = new SocketsHttpHandler { EnableMultipleHttp2Connections = true, PooledConnectionIdleTimeout = TimeSpan.FromMinutes( 2 ) };
      _data = new HttpClient( handler )
      {
         BaseAddress = new Uri( _queryUrl + "/" ),
         Timeout = options.EffectiveTimeout,
         DefaultRequestVersion = HttpVersion.Version20,
         DefaultVersionPolicy = HttpVersionPolicy.RequestVersionExact,
      };
   }

   #endregion Constructor

   #region Public Methods

   /// <summary>
   /// Reads every schema file of the active application from the config server.
   /// </summary>
   /// <param name="ct">Cancellation.</param>
   /// <returns>Schema text by schema name; empty when nothing has been deployed yet.</returns>
   public async Task<Dictionary<string, string>> ReadSchemasAsync( CancellationToken ct )
   {
      var schemas = new Dictionary<string, string>( StringComparer.Ordinal );
      byte[]? listing = await SendAsync( _config, HttpMethod.Get, $"{CONVERGE_PATH}/content/schemas/", null, "application/json", true, ct );
      if( listing == null )
      {
         return schemas;
      }

      using JsonDocument urls = JsonDocument.Parse( listing );
      foreach( JsonElement url in urls.RootElement.EnumerateArray() )
      {
         string file = url.GetString()!;
         string name = Path.GetFileNameWithoutExtension( file );
         byte[]? text = await SendAsync( _config, HttpMethod.Get, file, null, "application/json", false, ct );
         schemas[name] = System.Text.Encoding.UTF8.GetString( text! );
      }

      return schemas;
   }

   /// <summary>
   /// Deploys an application package and waits until the node runs it.
   /// </summary>
   /// <param name="package">Zip file bytes.</param>
   /// <param name="ct">Cancellation.</param>
   public async Task DeployAsync( byte[] package, CancellationToken ct )
   {
      await SendAsync( _config, HttpMethod.Post, "application/v2/tenant/default/prepareandactivate", package, "application/zip", false, ct );
      DateTime stop = DateTime.UtcNow + READY_TIMEOUT;
      while( !await ConvergedAsync( ct ) )
      {
         if( DateTime.UtcNow > stop )
         {
            throw new InvalidOperationException( "Vespa accepted the new application but its services did not switch to it within 3 minutes. Check the container log: sudo docker logs gvb-vespa" );
         }

         await Task.Delay( 500, ct );
      }
   }

   /// <summary>
   /// Waits until the container serves a document type end to end: a nearest-neighbour query with a
   /// vector of the right length succeeds, and the document API knows the type.
   /// Why both and why a real query: the services can report the new config as running before the
   /// container has finished loading it, and a plain "match everything" query can pass while the
   /// rank profile's query input is still unknown, which fails the first real search with a 400.
   /// </summary>
   /// <param name="documentType">Document type name.</param>
   /// <param name="dimension">Vector length of the type.</param>
   /// <param name="ct">Cancellation.</param>
   public async Task WaitForTypeAsync( string documentType, int dimension, CancellationToken ct )
   {
      DateTime stop = DateTime.UtcNow + READY_TIMEOUT;
      string input = VespaApplicationPackage.QueryInput( dimension );
      var query = new Dictionary<string, object>
      {
         ["yql"] = $"select * from {documentType} where {{targetHits:1}}nearestNeighbor(embedding,{input})",
         [$"input.query({input})"] = new float[dimension],
         ["hits"] = 1,
      };

      while( true )
      {
         try
         {
            using JsonDocument ignored = await QueryAsync( query, ct );
            await SendAsync( _data, HttpMethod.Get, DocumentPath( documentType, "readiness-probe" ), null, "application/json", true, ct );
            return;
         }
         catch( InvalidOperationException ) when( DateTime.UtcNow < stop )
         {
            await Task.Delay( 500, ct );
         }
      }
   }

   /// <summary>
   /// Runs a search and returns the parsed answer.
   /// </summary>
   /// <param name="body">Query parameters as sent to /search/.</param>
   /// <param name="ct">Cancellation.</param>
   /// <returns>The parsed answer, which the caller disposes.</returns>
   public async Task<JsonDocument> QueryAsync( object body, CancellationToken ct )
   {
      byte[] payload = JsonSerializer.SerializeToUtf8Bytes( body );
      byte[]? answer = await SendAsync( _data, HttpMethod.Post, "search/", payload, "application/json", false, ct );
      return JsonDocument.Parse( answer! );
   }

   /// <summary>
   /// Writes (creates or replaces) one document.
   /// </summary>
   /// <param name="documentType">Document type name.</param>
   /// <param name="id">User part of the document id.</param>
   /// <param name="body">The {"fields": ...} JSON.</param>
   /// <param name="ct">Cancellation.</param>
   public async Task PutAsync( string documentType, string id, byte[] body, CancellationToken ct )
   {
      await SendAsync( _data, HttpMethod.Post, DocumentPath( documentType, id ), body, "application/json", false, ct );
   }

   /// <summary>
   /// Removes one document. Removing a document that is not there succeeds.
   /// </summary>
   /// <param name="documentType">Document type name.</param>
   /// <param name="id">User part of the document id.</param>
   /// <param name="ct">Cancellation.</param>
   public async Task RemoveAsync( string documentType, string id, CancellationToken ct )
   {
      await SendAsync( _data, HttpMethod.Delete, DocumentPath( documentType, id ), null, "application/json", false, ct );
   }

   #endregion Public Methods

   #region Private Methods

   /// <summary>
   /// The /document/v1 path for one document. The namespace "gvb" is arbitrary but fixed.
   /// </summary>
   /// <param name="documentType">Document type name.</param>
   /// <param name="id">User part of the document id.</param>
   /// <returns>Relative path.</returns>
   private static string DocumentPath( string documentType, string id )
   {
      return $"document/v1/gvb/{documentType}/docid/{Uri.EscapeDataString( id )}";
   }

   /// <summary>
   /// True when every service runs the latest deployed generation.
   /// </summary>
   /// <param name="ct">Cancellation.</param>
   /// <returns>True once converged.</returns>
   private async Task<bool> ConvergedAsync( CancellationToken ct )
   {
      byte[]? answer = await SendAsync( _config, HttpMethod.Get, $"{CONVERGE_PATH}/serviceconverge", null, "application/json", true, ct );
      if( answer == null )
      {
         return false;
      }

      using JsonDocument doc = JsonDocument.Parse( answer );
      return doc.RootElement.TryGetProperty( "converged", out JsonElement converged ) && converged.GetBoolean();
   }

   /// <summary>
   /// Sends one request with retries and returns the answer bytes.
   /// </summary>
   /// <param name="client">Config or data client.</param>
   /// <param name="method">HTTP method.</param>
   /// <param name="path">Relative or absolute URL.</param>
   /// <param name="body">Body bytes, or null.</param>
   /// <param name="contentType">Content type of the body.</param>
   /// <param name="allowMissing">When true a 404 answer returns null instead of throwing.</param>
   /// <param name="ct">Cancellation.</param>
   /// <returns>Answer bytes, or null for an allowed 404.</returns>
   private async Task<byte[]?> SendAsync( HttpClient client, HttpMethod method, string path, byte[]? body, string contentType, bool allowMissing, CancellationToken ct )
   {
      for( int attempt = 1; ; attempt++ )
      {
         try
         {
            using var request = new HttpRequestMessage( method, path ) { Version = client.DefaultRequestVersion, VersionPolicy = client.DefaultVersionPolicy };
            if( body != null )
            {
               request.Content = new ByteArrayContent( body );
               request.Content.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue( contentType );
            }

            using HttpResponseMessage response = await client.SendAsync( request, ct );
            byte[] bytes = await response.Content.ReadAsByteArrayAsync( ct );
            if( response.IsSuccessStatusCode )
            {
               return bytes;
            }

            if( response.StatusCode == HttpStatusCode.NotFound && allowMissing )
            {
               return null;
            }

            if( IsTransient( response.StatusCode ) && attempt < MAX_ATTEMPTS )
            {
               await Task.Delay( TimeSpan.FromMilliseconds( 200 * attempt * attempt ), ct );
               continue;
            }

            throw ToException( (int)response.StatusCode, bytes );
         }
         catch( HttpRequestException ) when( attempt < MAX_ATTEMPTS )
         {
            await Task.Delay( TimeSpan.FromMilliseconds( 200 * attempt * attempt ), ct );
         }
         catch( HttpRequestException ex )
         {
            throw new InvalidOperationException( $"Vespa at {_queryUrl} / {_configUrl} is not answering ({ex.Message}). Start it with: sudo docker compose -f deploy/engines/vespa.compose.yaml up -d", ex );
         }
      }
   }

   /// <summary>
   /// True for statuses worth retrying: too many requests and server-side trouble.
   /// </summary>
   /// <param name="status">Response status.</param>
   /// <returns>True when a retry may succeed.</returns>
   private static bool IsTransient( HttpStatusCode status )
   {
      return status == HttpStatusCode.TooManyRequests || (int)status >= 502 && (int)status <= 504;
   }

   /// <summary>
   /// Builds a plain-English exception from a Vespa error body. A search error carries the whole
   /// query vector inside its message, so only the start and the end of long messages are kept.
   /// </summary>
   /// <param name="status">HTTP status code.</param>
   /// <param name="body">Response bytes.</param>
   /// <returns>The exception to throw.</returns>
   private static InvalidOperationException ToException( int status, byte[] body )
   {
      string reason = ErrorText( body );
      if( reason.Length > 700 )
      {
         reason = reason[..200] + " ... " + reason[^450..];
      }

      if( status == 507 )
      {
         return new InvalidOperationException( $"Vespa stopped accepting writes (507): memory or disk is above its limit. Details: {reason}" );
      }

      return new InvalidOperationException( $"Vespa answered {status}: {reason}" );
   }

   /// <summary>
   /// Pulls the error summary and message out of a Vespa answer, or returns the raw text.
   /// </summary>
   /// <param name="body">Response bytes.</param>
   /// <returns>Readable error text.</returns>
   private static string ErrorText( byte[] body )
   {
      string text = System.Text.Encoding.UTF8.GetString( body );
      try
      {
         using JsonDocument doc = JsonDocument.Parse( body );
         JsonElement root = doc.RootElement;
         if( root.TryGetProperty( "root", out JsonElement inner ) && inner.TryGetProperty( "errors", out JsonElement errors ) && errors.GetArrayLength() > 0 )
         {
            JsonElement first = errors[0];
            string summary = first.TryGetProperty( "summary", out JsonElement s ) ? s.GetString() ?? string.Empty : string.Empty;
            string message = first.TryGetProperty( "message", out JsonElement m ) ? m.GetString() ?? string.Empty : string.Empty;
            string detail = first.TryGetProperty( "detailedMessage", out JsonElement d ) ? d.GetString() ?? string.Empty : string.Empty;
            return $"{summary}: {message} {detail}".Trim();
         }

         if( root.TryGetProperty( "message", out JsonElement plain ) )
         {
            return plain.GetString() ?? text;
         }
      }
      catch( JsonException )
      {
         return text;
      }

      return text;
   }

   #endregion Private Methods

   #region IDisposable

   /// <summary>
   /// Disposes both HTTP clients.
   /// </summary>
   public void Dispose()
   {
      _config.Dispose();
      _data.Dispose();
   }

   #endregion IDisposable
}
