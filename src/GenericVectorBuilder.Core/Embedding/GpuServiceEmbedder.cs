using System.Net.Http.Json;
using System.Text.Json;
using GenericVectorBuilder.Core.Contracts;

namespace GenericVectorBuilder.Core.Embedding;

/// <summary>
/// The document and query conventions for one model on the GPU service. These are not
/// interchangeable between models, and getting them wrong gives plausible-looking but bad
/// results, so each model's rules live in exactly one place.
/// </summary>
/// <param name="ModelKey">Model key the service knows ("bge-code-v1", "qwen3-emb-0.6b").</param>
/// <param name="DocumentSuffix">Appended to every document chunk.</param>
/// <param name="QueryFormat">Format string for queries; {0} is the user's text.</param>
/// <param name="Measured">Plain-English note on what this profile was measured on, shown in the UI.</param>
public sealed record EmbedderProfile( string ModelKey, string DocumentSuffix, string QueryFormat, string Measured )
{
   /// <summary>
   /// bge-code-v1 with the end-of-text token appended client-side to documents AND queries.
   /// The model pools the last token and was trained with that token there, but its fast
   /// tokenizer never appends it. The service's own bge profile omits it on purpose to match
   /// the hosted demo, which is why it is added here instead.
   /// Measured 2026-10-02 on the quality-gate set (45 rows, 15 questions): top-1 11/15 with the
   /// token, 2/15 without. An earlier 60-row, 14-question check gave 14/14 vs 5/14. It is a code
   /// model; on general table rows it trails Qwen3-Embedding.
   /// </summary>
   public static readonly EmbedderProfile BgeCodeV1 = new( "bge-code-v1", "<|endoftext|>", "{0}<|endoftext|>",
      "code model; table-row gate 11/15 (below the 13/15 bar)" );

   /// <summary>
   /// Qwen3-Embedding-0.6B with a generic retrieval instruction on queries. The service's own
   /// query prefix is code-search specific, so queries are sent in document mode with this
   /// instruction instead. The default: measured 2026-10-02 on the quality-gate set, top-1
   /// 13/15 in both SQL Server and Qdrant (bge-code-v1: 11/15), and it also beat bge-code-v1 on
   /// the code golden set (nDCG 0.594 vs 0.431). 15 questions is a small set; treat the margin
   /// as directional.
   /// </summary>
   public static readonly EmbedderProfile Qwen3Emb06B = new( "qwen3-emb-0.6b", string.Empty,
      "Instruct: Given a search query, retrieve the records that answer it\nQuery:{0}", "general model; table-row gate 13/15 (default)" );

   /// <summary>
   /// All profiles, for the UI's model picker.
   /// </summary>
   public static IReadOnlyList<EmbedderProfile> All { get; } = new[] { Qwen3Emb06B, BgeCodeV1 };

   /// <summary>
   /// Finds a profile by model key.
   /// </summary>
   /// <param name="modelKey">Model key.</param>
   /// <returns>The profile.</returns>
   /// <exception cref="ArgumentException">Unknown model key.</exception>
   public static EmbedderProfile Get( string modelKey )
   {
      return All.FirstOrDefault( p => p.ModelKey == modelKey ) ?? throw new ArgumentException( $"Unknown embedding model '{modelKey}'." );
   }
}

/// <summary>
/// Client for the FastAPI embedding service on linus7795's RTX 3060 (POST /embed).
/// Why this is the default embedder: it is local and free, where the hosted endpoints cost
/// money per wake. The service holds one embedding model in memory at a time, so asking for a
/// different model evicts whatever another job was using.
/// Every response is checked: count, dimension, no NaN, no all-zero vector. The service maps
/// blank text to a space rather than failing, and a silent zero vector would poison search.
/// </summary>
public sealed class GpuServiceEmbedder : IEmbedder
{
   #region Data Members

   private const int BATCH_SIZE = 32;
   private const int MAX_ATTEMPTS = 3;
   private const string PROBE_TEXT = "connectivity probe";
   private static readonly TimeSpan DEFAULT_BACKOFF = TimeSpan.FromSeconds( 2 );

   private readonly HttpClient _http;
   private readonly EmbedderProfile _profile;
   private readonly TimeSpan _backoff;

   #endregion Data Members

   #region Constructor

   /// <summary>
   /// Creates the client.
   /// </summary>
   /// <param name="http">HTTP client whose BaseAddress is the service root, e.g. http://localhost:8080/.
   /// Give it a long timeout: the first call after a model swap loads weights onto the GPU.</param>
   /// <param name="profile">Model conventions.</param>
   public GpuServiceEmbedder( HttpClient http, EmbedderProfile profile )
      : this( http, profile, DEFAULT_BACKOFF )
   {
   }

   /// <summary>
   /// Creates the client with its own retry pause, so tests do not wait seconds per retry.
   /// </summary>
   /// <param name="http">HTTP client whose BaseAddress is the service root.</param>
   /// <param name="profile">Model conventions.</param>
   /// <param name="backoff">Pause before the first retry; the second retry waits twice as long.</param>
   public GpuServiceEmbedder( HttpClient http, EmbedderProfile profile, TimeSpan backoff )
   {
      _http = http;
      _profile = profile;
      _backoff = backoff;
   }

   #endregion Constructor

   #region Public Methods

   /// <inheritdoc />
   public int Dimension { get; private set; }

   /// <inheritdoc />
   public string Fingerprint => $"gpu-service|{_profile.ModelKey}|dims={Dimension}|doc+{_profile.DocumentSuffix}|query={_profile.QueryFormat}";

   /// <inheritdoc />
   public async Task PreflightAsync( CancellationToken ct )
   {
      float[][] probe = await PostAsync( new[] { PROBE_TEXT + _profile.DocumentSuffix }, ct );
      int dimension = probe.Length == 1 && probe[0] != null ? probe[0].Length : 0;
      Validate( probe, 1, dimension );
      Dimension = dimension;
   }

   /// <inheritdoc />
   public async Task<float[][]> EmbedDocumentsAsync( IReadOnlyList<string> texts, CancellationToken ct )
   {
      var result = new List<float[]>( texts.Count );
      for( int i = 0; i < texts.Count; i += BATCH_SIZE )
      {
         string[] batch = texts.Skip( i ).Take( BATCH_SIZE ).Select( t => t + _profile.DocumentSuffix ).ToArray();
         float[][] vectors = await PostAsync( batch, ct );
         Validate( vectors, batch.Length, Dimension );
         result.AddRange( vectors );
      }

      return result.ToArray();
   }

   /// <inheritdoc />
   public async Task<float[]> EmbedQueryAsync( string query, CancellationToken ct )
   {
      float[][] vectors = await PostAsync( new[] { string.Format( _profile.QueryFormat, query ) }, ct );
      Validate( vectors, 1, Dimension );
      return vectors[0];
   }

   #endregion Public Methods

   #region Private Methods

   /// <summary>
   /// Posts a batch, retrying transient failures (connection errors, 5xx) with a short backoff.
   /// A request the service rejects (4xx) is not retried, since the same request would be
   /// rejected again. When the service stays unreachable, answers an error status or times
   /// out, the failure is rethrown as <see cref="EmbedderUnavailableException"/> with a
   /// plain-English message.
   /// Everything is sent in document mode; query formatting is done client-side by the profile.
   /// </summary>
   /// <param name="texts">Fully formatted texts.</param>
   /// <param name="ct">Cancellation.</param>
   /// <returns>Raw vectors.</returns>
   /// <exception cref="EmbedderUnavailableException">The service could not give a usable answer.</exception>
   private async Task<float[][]> PostAsync( IReadOnlyList<string> texts, CancellationToken ct )
   {
      var body = new { inputs = texts, truncate = true, model = _profile.ModelKey, is_query = false };
      for( int attempt = 1; ; attempt++ )
      {
         try
         {
            using HttpResponseMessage response = await _http.PostAsJsonAsync( "embed", body, ct );
            if( (int)response.StatusCode >= 500 && attempt < MAX_ATTEMPTS )
            {
               await Task.Delay( _backoff * attempt, ct );
               continue;
            }

            if( !response.IsSuccessStatusCode )
            {
               throw new EmbedderUnavailableException( $"The embedding service answered {(int)response.StatusCode} {response.ReasonPhrase}. Check its log on the GPU box." );
            }

            return await ReadVectorsAsync( response, ct );
         }
         catch( HttpRequestException ) when( attempt < MAX_ATTEMPTS )
         {
            await Task.Delay( _backoff * attempt, ct );
         }
         catch( HttpRequestException ex )
         {
            throw new EmbedderUnavailableException( $"The embedding service at {_http.BaseAddress} is not answering ({ex.Message}). Check that it is running.", ex );
         }
         catch( OperationCanceledException ex ) when( !ct.IsCancellationRequested )
         {
            throw new EmbedderUnavailableException( $"The embedding service did not answer within {_http.Timeout.TotalMinutes:0.#} minutes.", ex );
         }
      }
   }

   /// <summary>
   /// Reads the vectors out of a successful response.
   /// </summary>
   /// <param name="response">The response.</param>
   /// <param name="ct">Cancellation.</param>
   /// <returns>The vectors.</returns>
   /// <exception cref="InvalidOperationException">The body is empty or not a list of vectors.</exception>
   private static async Task<float[][]> ReadVectorsAsync( HttpResponseMessage response, CancellationToken ct )
   {
      try
      {
         return await response.Content.ReadFromJsonAsync<float[][]>( ct )
            ?? throw new InvalidOperationException( "Embedding service returned an empty body." );
      }
      catch( JsonException ex )
      {
         throw new InvalidOperationException( "Embedding service returned something that is not a list of vectors.", ex );
      }
   }

   /// <summary>
   /// Rejects malformed responses instead of writing them to a sink. The count is checked
   /// before anything is read, so a short or empty answer gets the count message and not an
   /// index error.
   /// </summary>
   /// <param name="vectors">Vectors returned.</param>
   /// <param name="expected">How many were requested.</param>
   /// <param name="dimension">The vector length every vector must have.</param>
   private static void Validate( float[][] vectors, int expected, int dimension )
   {
      if( vectors.Length != expected )
      {
         throw new InvalidOperationException( $"Embedding service returned {vectors.Length} vectors for {expected} inputs." );
      }

      foreach( float[]? v in vectors )
      {
         if( v == null || v.Length == 0 )
         {
            throw new InvalidOperationException( "Embedding service returned an empty vector." );
         }

         if( v.Length != dimension )
         {
            throw new InvalidOperationException( $"Embedding service returned a {v.Length}-dim vector, expected {dimension}." );
         }

         if( v.Any( float.IsNaN ) || v.All( x => x == 0f ) )
         {
            throw new InvalidOperationException( "Embedding service returned a NaN or all-zero vector." );
         }
      }
   }

   #endregion Private Methods
}
