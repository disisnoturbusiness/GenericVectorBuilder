using System.Net;
using System.Text.Json;

namespace GenericVectorBuilder.Engines.Sinks;

/// <summary>
/// The error Typesense answered with: its HTTP status and its plain message.
/// Why a type of its own: callers need the status (404 for a missing collection, 409 for one that
/// already exists) to tell a harmless race from a real failure.
/// </summary>
internal sealed class TypesenseRestException : Exception
{
   #region Constructor

   /// <summary>
   /// Creates the exception.
   /// </summary>
   /// <param name="status">HTTP status code.</param>
   /// <param name="message">Plain-English description.</param>
   public TypesenseRestException( int status, string message )
      : base( message )
   {
      Status = status;
   }

   #endregion Constructor

   #region Public Methods

   /// <summary>HTTP status code of the failed call.</summary>
   public int Status { get; }

   #endregion Public Methods
}

/// <summary>
/// A small HTTP client for the Typesense REST API that adds the API key to every call.
/// The key is read from the shared secrets file on the first call, not at construction, so
/// building the sink (which the engine catalog does for every engine) never touches the file.
/// Transient failures (connection errors, 429 and 5xx) are retried with a short backoff; a request
/// Typesense rejects with another 4xx is not, because the same request would fail again.
/// </summary>
internal sealed class TypesenseRest : IDisposable
{
   #region Data Members

   private const int MAX_ATTEMPTS = 3;
   private const string KEY_NAME = "TYPESENSE_API_KEY";

   private readonly HttpClient _http;
   private readonly string _baseUrl;
   private readonly string? _configuredKey;
   private readonly string _secretsFile;
   private string? _apiKey;

   #endregion Data Members

   #region Constructor

   /// <summary>
   /// Creates the client without contacting the server or reading any secret.
   /// </summary>
   /// <param name="options">Address, key source and time limit.</param>
   public TypesenseRest( TypesenseSinkOptions options )
   {
      _baseUrl = options.BaseUrl.TrimEnd( '/' );
      _configuredKey = options.ApiKey;
      _secretsFile = options.SecretsFile;
      _http = new HttpClient { BaseAddress = new Uri( _baseUrl + "/" ), Timeout = options.EffectiveTimeout };
   }

   #endregion Constructor

   #region Public Methods

   /// <summary>
   /// Sends one request and returns the raw answer bytes.
   /// </summary>
   /// <param name="method">HTTP method.</param>
   /// <param name="path">Path and query, without a leading slash.</param>
   /// <param name="body">Request body bytes, or null.</param>
   /// <param name="contentType">Content type of the body.</param>
   /// <param name="allowMissing">When true a 404 answer returns null instead of throwing.</param>
   /// <param name="ct">Cancellation.</param>
   /// <returns>The answer bytes, or null for an allowed 404.</returns>
   public async Task<byte[]?> SendAsync( HttpMethod method, string path, byte[]? body, string contentType, bool allowMissing, CancellationToken ct )
   {
      string key = ApiKey();
      for( int attempt = 1; ; attempt++ )
      {
         try
         {
            using var request = new HttpRequestMessage( method, path );
            request.Headers.Add( "X-TYPESENSE-API-KEY", key );
            if( body != null )
            {
               request.Content = new ByteArrayContent( body );
               request.Content.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue( contentType );
            }

            using HttpResponseMessage response = await _http.SendAsync( request, ct );
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
            throw new InvalidOperationException( $"Typesense at {_baseUrl} is not answering ({ex.Message}). Start it with: sudo docker compose -f deploy/engines/typesense.compose.yaml up -d", ex );
         }
      }
   }

   /// <summary>
   /// Sends a JSON request and parses the JSON answer.
   /// </summary>
   /// <param name="method">HTTP method.</param>
   /// <param name="path">Path and query, without a leading slash.</param>
   /// <param name="body">Object to serialize as the JSON body, or null.</param>
   /// <param name="allowMissing">When true a 404 answer returns null instead of throwing.</param>
   /// <param name="ct">Cancellation.</param>
   /// <returns>The parsed answer, which the caller disposes, or null for an allowed 404.</returns>
   public async Task<JsonDocument?> SendJsonAsync( HttpMethod method, string path, object? body, bool allowMissing, CancellationToken ct )
   {
      byte[]? payload = body == null ? null : JsonSerializer.SerializeToUtf8Bytes( body );
      byte[]? answer = await SendAsync( method, path, payload, "application/json", allowMissing, ct );
      return answer == null || answer.Length == 0 ? null : JsonDocument.Parse( answer );
   }

   #endregion Public Methods

   #region Private Methods

   /// <summary>
   /// Returns the API key, reading it from the secrets file the first time.
   /// The file is KEY=VALUE lines; blank lines and # comments are skipped, a Windows line ending
   /// is stripped, and when a key appears twice the last one wins. The value is never logged and
   /// never placed in an error message.
   /// </summary>
   /// <returns>The API key.</returns>
   /// <exception cref="InvalidOperationException">No key was configured and the file has none.</exception>
   private string ApiKey()
   {
      if( _apiKey != null )
      {
         return _apiKey;
      }

      string? found = _configuredKey;
      if( string.IsNullOrEmpty( found ) && File.Exists( _secretsFile ) )
      {
         foreach( string raw in File.ReadLines( _secretsFile ) )
         {
            string line = raw.TrimEnd( '\r' );
            if( line.StartsWith( KEY_NAME + "=", StringComparison.Ordinal ) )
            {
               found = line[( KEY_NAME.Length + 1 )..];
            }
         }
      }

      if( string.IsNullOrEmpty( found ) )
      {
         throw new InvalidOperationException( $"No Typesense API key. Add a line {KEY_NAME}=... to {_secretsFile} and restart the container." );
      }

      _apiKey = found;
      return found;
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
   /// Builds a plain-English exception from a Typesense error body, which is {"message": "..."}.
   /// </summary>
   /// <param name="status">HTTP status code.</param>
   /// <param name="body">Response bytes.</param>
   /// <returns>The exception to throw.</returns>
   private static TypesenseRestException ToException( int status, byte[] body )
   {
      string reason;
      try
      {
         using JsonDocument doc = JsonDocument.Parse( body );
         reason = doc.RootElement.TryGetProperty( "message", out JsonElement m ) ? m.GetString() ?? string.Empty : doc.RootElement.GetRawText();
      }
      catch( JsonException )
      {
         reason = System.Text.Encoding.UTF8.GetString( body );
      }

      if( status == 503 )
      {
         reason += ". The node is still starting up: after a restart Typesense replays its whole write log, which takes minutes after a big load. Wait until GET /collections answers, then retry.";
      }

      return new TypesenseRestException( status, $"Typesense answered {status}: {reason}" );
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
