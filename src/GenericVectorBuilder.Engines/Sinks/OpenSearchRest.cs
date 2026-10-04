using System.Net;
using System.Text.Json;

namespace GenericVectorBuilder.Engines.Sinks;

/// <summary>
/// The error OpenSearch answered with, reduced to its status, type and reason.
/// Why a type of its own: callers need the error type (for example "resource_already_exists_exception")
/// to tell a harmless race from a real failure.
/// </summary>
internal sealed class OpenSearchRestException : Exception
{
   #region Constructor

   /// <summary>
   /// Creates the exception.
   /// </summary>
   /// <param name="status">HTTP status code.</param>
   /// <param name="errorType">OpenSearch error type, or empty.</param>
   /// <param name="message">Plain-English description.</param>
   public OpenSearchRestException( int status, string errorType, string message )
      : base( message )
   {
      Status = status;
      ErrorType = errorType;
   }

   #endregion Constructor

   #region Public Methods

   /// <summary>HTTP status code of the failed call.</summary>
   public int Status { get; }

   /// <summary>OpenSearch error type, or empty when the body had none.</summary>
   public string ErrorType { get; }

   #endregion Public Methods
}

/// <summary>
/// A small HTTP and JSON client for the OpenSearch REST API.
/// Why hand-written instead of the official client: the benchmark only needs about eight calls,
/// and a plain HttpClient keeps the measured time free of client-library overhead.
/// Transient failures (connection errors, 429 and 5xx) are retried with a short backoff; a request
/// OpenSearch rejects with another 4xx is not, because the same request would fail again.
/// </summary>
internal sealed class OpenSearchRest : IDisposable
{
   #region Data Members

   private const int MAX_ATTEMPTS = 3;
   private const string JSON = "application/json";

   private readonly HttpClient _http;
   private readonly string _baseUrl;

   #endregion Data Members

   #region Constructor

   /// <summary>
   /// Creates the client.
   /// </summary>
   /// <param name="baseUrl">Root URL of the node.</param>
   /// <param name="timeout">Time limit for one HTTP call.</param>
   public OpenSearchRest( string baseUrl, TimeSpan timeout )
   {
      _baseUrl = baseUrl.TrimEnd( '/' );
      _http = new HttpClient { BaseAddress = new Uri( _baseUrl + "/" ), Timeout = timeout };
   }

   #endregion Constructor

   #region Public Methods

   /// <summary>
   /// Sends one request and returns the parsed JSON answer.
   /// </summary>
   /// <param name="method">HTTP method.</param>
   /// <param name="path">Path and query, without a leading slash.</param>
   /// <param name="body">Request body bytes, or null.</param>
   /// <param name="contentType">Content type of the body: JSON, or newline-delimited JSON for bulk.</param>
   /// <param name="allowMissing">When true a 404 answer returns null instead of throwing.</param>
   /// <param name="ct">Cancellation.</param>
   /// <returns>The answer, which the caller disposes, or null for an allowed 404.</returns>
   public async Task<JsonDocument?> SendAsync( HttpMethod method, string path, byte[]? body, string contentType, bool allowMissing, CancellationToken ct )
   {
      for( int attempt = 1; ; attempt++ )
      {
         try
         {
            using var request = new HttpRequestMessage( method, path );
            if( body != null )
            {
               request.Content = new ByteArrayContent( body );
               request.Content.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue( contentType );
            }

            using HttpResponseMessage response = await _http.SendAsync( request, ct );
            byte[] bytes = await response.Content.ReadAsByteArrayAsync( ct );
            if( response.IsSuccessStatusCode )
            {
               return bytes.Length == 0 ? null : JsonDocument.Parse( bytes );
            }

            if( response.StatusCode == HttpStatusCode.NotFound && allowMissing )
            {
               return null;
            }

            if( IsTransient( response.StatusCode ) && attempt < MAX_ATTEMPTS )
            {
               await Task.Delay( TimeSpan.FromSeconds( attempt * 2 ), ct );
               continue;
            }

            throw ToException( (int)response.StatusCode, bytes );
         }
         catch( HttpRequestException ) when( attempt < MAX_ATTEMPTS )
         {
            await Task.Delay( TimeSpan.FromSeconds( attempt * 2 ), ct );
         }
         catch( HttpRequestException ex )
         {
            throw new InvalidOperationException( $"OpenSearch at {_baseUrl} is not answering ({ex.Message}). Start it with: sudo docker compose -f deploy/engines/opensearch.compose.yaml up -d", ex );
         }
      }
   }

   /// <summary>
   /// Sends a JSON request body built from an object.
   /// </summary>
   /// <param name="method">HTTP method.</param>
   /// <param name="path">Path and query, without a leading slash.</param>
   /// <param name="body">Object to serialize as the JSON body.</param>
   /// <param name="allowMissing">When true a 404 answer returns null instead of throwing.</param>
   /// <param name="ct">Cancellation.</param>
   /// <returns>The answer, which the caller disposes, or null for an allowed 404.</returns>
   public Task<JsonDocument?> SendJsonAsync( HttpMethod method, string path, object? body, bool allowMissing, CancellationToken ct )
   {
      byte[]? bytes = body == null ? null : JsonSerializer.SerializeToUtf8Bytes( body );
      return SendAsync( method, path, bytes, JSON, allowMissing, ct );
   }

   #endregion Public Methods

   #region Private Methods

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
   /// Builds a plain-English exception from an OpenSearch error body.
   /// </summary>
   /// <param name="status">HTTP status code.</param>
   /// <param name="body">Response bytes.</param>
   /// <returns>The exception to throw.</returns>
   private static OpenSearchRestException ToException( int status, byte[] body )
   {
      string type = string.Empty;
      string reason = string.Empty;
      try
      {
         using JsonDocument doc = JsonDocument.Parse( body );
         if( doc.RootElement.TryGetProperty( "error", out JsonElement error ) && error.ValueKind == JsonValueKind.Object )
         {
            type = error.TryGetProperty( "type", out JsonElement t ) ? t.GetString() ?? string.Empty : string.Empty;
            reason = error.TryGetProperty( "reason", out JsonElement r ) ? r.GetString() ?? string.Empty : string.Empty;
         }
      }
      catch( JsonException )
      {
         reason = System.Text.Encoding.UTF8.GetString( body );
      }

      return new OpenSearchRestException( status, type, $"OpenSearch answered {status} {type}: {reason}".TrimEnd( ' ', ':' ) );
   }

   #endregion Private Methods

   #region IDisposable

   /// <summary>
   /// Disposes the HTTP client.
   /// </summary>
   public void Dispose()
   {
      _http.Dispose();
   }

   #endregion IDisposable
}
