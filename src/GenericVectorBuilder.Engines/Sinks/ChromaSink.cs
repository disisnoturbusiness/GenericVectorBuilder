using System.Collections.Concurrent;
using System.Globalization;
using System.Net;
using System.Text.Json;
using GenericVectorBuilder.Core.Contracts;
using GenericVectorBuilder.Engines.Common;

namespace GenericVectorBuilder.Engines.Sinks;

/// <summary>
/// Writes vectors to Chroma over its REST API v2: one collection per pipeline, named
/// gvb_{pipeline}, cosine space, with the chunk text stored as the Chroma "document" and the
/// document key, table, origin, ordinal and meta_ fields stored as Chroma metadata.
/// Index: HNSW with M (max_neighbors) 16, ef_construction 128 and ef_search 100 (Chroma's own
/// default). Chroma has no per-query exact mode, so this sink does not implement
/// <see cref="IExactSearchSink"/>.
/// Why the dimension is stored in collection metadata (gvb_dimension): Chroma only learns a
/// collection's dimension from its first insert, so an empty collection cannot say what it was
/// created for, and the pipeline needs to refuse a mismatched embedder before writing anything.
/// Why upsert: it is idempotent by id, so a retried batch never duplicates.
/// Why batches are capped at 500: the server allows 5,461 per call, but 1024-dim JSON batches
/// that large are tens of megabytes; 500 keeps each request near 5 MB.
/// Quirk to know when reading benchmark numbers: Chroma indexes the document text and every
/// metadata field by default (full-text and inverted indexes), which costs write time that the
/// other engines do not pay.
/// </summary>
public sealed class ChromaSink : ISink, IEngineDescription, IDisposable
{
   #region Data Members

   private const int UPSERT_BATCH = 500;
   private const int DELETE_BATCH = 1000;
   private const int MAX_ATTEMPTS = 3;
   private const string DIMENSION_KEY = "gvb_dimension";

   private readonly ChromaSinkOptions _options;
   private readonly HttpClient _http;
   private readonly ConcurrentDictionary<string, string> _ids = new( StringComparer.Ordinal );

   #endregion Data Members

   #region Constructor

   /// <summary>
   /// Creates the sink with the local container defaults (http://127.0.0.1:8000).
   /// </summary>
   public ChromaSink() : this( new ChromaSinkOptions() )
   {
   }

   /// <summary>
   /// Creates the sink.
   /// </summary>
   /// <param name="options">Server address and index settings.</param>
   public ChromaSink( ChromaSinkOptions options )
   {
      _options = options;
      _http = new HttpClient { BaseAddress = new Uri( options.BaseUrl.TrimEnd( '/' ) + "/" ), Timeout = TimeSpan.FromMinutes( 5 ) };
   }

   #endregion Constructor

   #region Public Methods

   /// <inheritdoc />
   public string Name => "chroma";

   /// <inheritdoc />
   public string Engine => "Chroma 1.4.4 (single node, REST API v2)";

   /// <inheritdoc />
   public string IndexDescription =>
      $"HNSW M={_options.MaxNeighbors} ef_construction={_options.EfConstruction}, ef_search={_options.EfSearch} (Chroma default), cosine; approximate only (no exact mode)";

   /// <inheritdoc />
   public string ComposeFile => "chroma.compose.yaml";

   /// <inheritdoc />
   public async Task<bool> EnsureCollectionAsync( string collection, int dimension, CancellationToken ct )
   {
      string name = CollectionName( collection );
      using JsonDocument? existing = await GetCollectionAsync( name, ct );
      if( existing != null )
      {
         VerifyDimension( name, existing.RootElement, dimension );
         _ids[name] = existing.RootElement.GetProperty( "id" ).GetString()!;
         return false;
      }

      byte[] body = JsonSerializer.SerializeToUtf8Bytes( new Dictionary<string, object>
      {
         ["name"] = name,
         ["get_or_create"] = false,
         ["metadata"] = new Dictionary<string, object> { [DIMENSION_KEY] = dimension },
         ["configuration"] = new Dictionary<string, object>
         {
            ["hnsw"] = new Dictionary<string, object>
            {
               ["space"] = "cosine",
               ["max_neighbors"] = _options.MaxNeighbors,
               ["ef_construction"] = _options.EfConstruction,
               ["ef_search"] = _options.EfSearch,
            },
         },
      } );

      ( HttpStatusCode status, string text ) = await SendAsync( HttpMethod.Post, CollectionsPath(), body, ct, allowConflict: true );
      if( status == HttpStatusCode.Conflict )
      {
         // Another writer created it between our look and our create; check theirs instead.
         using JsonDocument? raced = await GetCollectionAsync( name, ct );
         VerifyDimension( name, raced!.RootElement, dimension );
         return false;
      }

      using JsonDocument created = JsonDocument.Parse( text );
      _ids[name] = created.RootElement.GetProperty( "id" ).GetString()!;
      return true;
   }

   /// <inheritdoc />
   public async Task UpsertAsync( string collection, IReadOnlyList<VectorRecord> records, CancellationToken ct )
   {
      string id = await CollectionIdAsync( collection, ct );
      foreach( VectorRecord[] batch in records.Chunk( UPSERT_BATCH ) )
      {
         byte[] body = BuildUpsertBody( batch );
         await SendAsync( HttpMethod.Post, $"{CollectionsPath()}/{id}/upsert", body, ct );
      }
   }

   /// <inheritdoc />
   public async Task DeleteAsync( string collection, IReadOnlyList<Guid> chunkIds, CancellationToken ct )
   {
      string id = await CollectionIdAsync( collection, ct );
      foreach( Guid[] batch in chunkIds.Chunk( DELETE_BATCH ) )
      {
         byte[] body = JsonSerializer.SerializeToUtf8Bytes( new { ids = batch.Select( g => g.ToString() ) } );
         await SendAsync( HttpMethod.Post, $"{CollectionsPath()}/{id}/delete", body, ct );
      }
   }

   /// <inheritdoc />
   public async Task<long> CountAsync( string collection, CancellationToken ct )
   {
      string id = await CollectionIdAsync( collection, ct );
      ( _, string text ) = await SendAsync( HttpMethod.Get, $"{CollectionsPath()}/{id}/count", null, ct );
      return long.Parse( text.Trim(), CultureInfo.InvariantCulture );
   }

   /// <inheritdoc />
   public async Task<IReadOnlyList<SearchHit>> SearchAsync( string collection, float[] vector, int top, CancellationToken ct )
   {
      string id = await CollectionIdAsync( collection, ct );
      using var stream = new MemoryStream();
      using( var writer = new Utf8JsonWriter( stream ) )
      {
         writer.WriteStartObject();
         writer.WritePropertyName( "query_embeddings" );
         writer.WriteStartArray();
         WriteVector( writer, vector );
         writer.WriteEndArray();
         writer.WriteNumber( "n_results", top );
         writer.WriteStartArray( "include" );
         writer.WriteStringValue( "metadatas" );
         writer.WriteStringValue( "documents" );
         writer.WriteStringValue( "distances" );
         writer.WriteEndArray();
         writer.WriteEndObject();
      }

      ( _, string text ) = await SendAsync( HttpMethod.Post, $"{CollectionsPath()}/{id}/query", stream.ToArray(), ct );
      return ParseHits( text );
   }

   /// <inheritdoc />
   public async Task DropCollectionAsync( string collection, CancellationToken ct )
   {
      string name = CollectionName( collection );
      _ids.TryRemove( name, out _ );
      await SendAsync( HttpMethod.Delete, $"{CollectionsPath()}/{Uri.EscapeDataString( name )}", null, ct, allowNotFound: true );
   }

   #endregion Public Methods

   #region Private Methods

   /// <summary>
   /// Builds the JSON body of one upsert call: ids, embeddings, documents (chunk text) and metadata.
   /// </summary>
   /// <param name="batch">Records for this call.</param>
   /// <returns>UTF-8 JSON.</returns>
   private static byte[] BuildUpsertBody( VectorRecord[] batch )
   {
      using var stream = new MemoryStream();
      using( var writer = new Utf8JsonWriter( stream ) )
      {
         writer.WriteStartObject();
         writer.WriteStartArray( "ids" );
         foreach( VectorRecord r in batch )
         {
            writer.WriteStringValue( r.Chunk.ChunkId.ToString() );
         }

         writer.WriteEndArray();
         writer.WriteStartArray( "embeddings" );
         foreach( VectorRecord r in batch )
         {
            WriteVector( writer, r.Vector );
         }

         writer.WriteEndArray();
         writer.WriteStartArray( "documents" );
         foreach( VectorRecord r in batch )
         {
            writer.WriteStringValue( r.Chunk.Text );
         }

         writer.WriteEndArray();
         writer.WriteStartArray( "metadatas" );
         foreach( VectorRecord r in batch )
         {
            WriteMetadata( writer, r );
         }

         writer.WriteEndArray();
         writer.WriteEndObject();
      }

      return stream.ToArray();
   }

   /// <summary>
   /// Writes one record's metadata object: doc_key, table, origin, ordinal and meta_{key} fields.
   /// </summary>
   /// <param name="writer">Open JSON writer.</param>
   /// <param name="record">The record.</param>
   private static void WriteMetadata( Utf8JsonWriter writer, VectorRecord record )
   {
      writer.WriteStartObject();
      writer.WriteString( "doc_key", record.Document.DocKey );
      writer.WriteString( "table", record.Document.Table );
      writer.WriteString( "origin", record.Document.Origin );
      writer.WriteNumber( "ordinal", record.Chunk.Ordinal );
      foreach( KeyValuePair<string, string> pair in record.Document.Metadata )
      {
         writer.WriteString( $"meta_{pair.Key}", pair.Value );
      }

      writer.WriteEndObject();
   }

   /// <summary>
   /// Writes a vector as a JSON number array.
   /// </summary>
   /// <param name="writer">Open JSON writer.</param>
   /// <param name="vector">The vector.</param>
   private static void WriteVector( Utf8JsonWriter writer, float[] vector )
   {
      writer.WriteStartArray();
      foreach( float f in vector )
      {
         writer.WriteNumberValue( f );
      }

      writer.WriteEndArray();
   }

   /// <summary>
   /// Turns a Chroma query response into hits. Chroma returns cosine DISTANCE (1 - similarity),
   /// so the score is 1 - distance.
   /// </summary>
   /// <param name="json">Response body.</param>
   /// <returns>Hits, best first.</returns>
   private static List<SearchHit> ParseHits( string json )
   {
      using JsonDocument doc = JsonDocument.Parse( json );
      JsonElement root = doc.RootElement;
      JsonElement ids = root.GetProperty( "ids" )[0];
      JsonElement documents = root.GetProperty( "documents" )[0];
      JsonElement metadatas = root.GetProperty( "metadatas" )[0];
      JsonElement distances = root.GetProperty( "distances" )[0];
      var hits = new List<SearchHit>( ids.GetArrayLength() );
      for( int i = 0; i < ids.GetArrayLength(); i++ )
      {
         JsonElement meta = metadatas[i];
         hits.Add( new SearchHit( Guid.Parse( ids[i].GetString()! ), MetaText( meta, "doc_key" ), MetaText( meta, "table" ),
            documents[i].GetString() ?? string.Empty, 1.0 - distances[i].GetDouble() ) );
      }

      return hits;
   }

   /// <summary>
   /// Reads a string metadata field, or empty when it is missing.
   /// </summary>
   /// <param name="meta">Metadata object.</param>
   /// <param name="key">Field name.</param>
   /// <returns>The value.</returns>
   private static string MetaText( JsonElement meta, string key )
   {
      return meta.ValueKind == JsonValueKind.Object && meta.TryGetProperty( key, out JsonElement value ) ? value.GetString() ?? string.Empty : string.Empty;
   }

   /// <summary>
   /// Refuses a collection whose stored dimension differs from the embedder's.
   /// </summary>
   /// <param name="name">Chroma collection name.</param>
   /// <param name="collection">The collection JSON returned by the server.</param>
   /// <param name="dimension">Dimension the embedder produces.</param>
   private static void VerifyDimension( string name, JsonElement collection, int dimension )
   {
      int stored = 0;
      if( collection.TryGetProperty( "metadata", out JsonElement meta ) && meta.ValueKind == JsonValueKind.Object
         && meta.TryGetProperty( DIMENSION_KEY, out JsonElement d ) && d.ValueKind == JsonValueKind.Number )
      {
         stored = d.GetInt32();
      }
      else if( collection.TryGetProperty( "dimension", out JsonElement real ) && real.ValueKind == JsonValueKind.Number )
      {
         stored = real.GetInt32();
      }

      if( stored != 0 && stored != dimension )
      {
         throw new InvalidOperationException( $"Chroma collection {name} holds {stored}-dim vectors but the embedder produces {dimension}. Reset the pipeline or use a new name." );
      }
   }

   /// <summary>
   /// Looks a collection up by name.
   /// </summary>
   /// <param name="name">Chroma collection name.</param>
   /// <param name="ct">Cancellation.</param>
   /// <returns>The collection JSON, or null when it does not exist.</returns>
   private async Task<JsonDocument?> GetCollectionAsync( string name, CancellationToken ct )
   {
      ( HttpStatusCode status, string text ) = await SendAsync( HttpMethod.Get, $"{CollectionsPath()}/{Uri.EscapeDataString( name )}", null, ct, allowNotFound: true );
      return status == HttpStatusCode.NotFound ? null : JsonDocument.Parse( text );
   }

   /// <summary>
   /// Chroma addresses a collection by id; this finds it from the pipeline name and remembers it.
   /// </summary>
   /// <param name="collection">Pipeline collection name.</param>
   /// <param name="ct">Cancellation.</param>
   /// <returns>The collection id.</returns>
   private async Task<string> CollectionIdAsync( string collection, CancellationToken ct )
   {
      string name = CollectionName( collection );
      if( _ids.TryGetValue( name, out string? known ) )
      {
         return known;
      }

      using JsonDocument? found = await GetCollectionAsync( name, ct )
         ?? throw new InvalidOperationException( $"Chroma collection {name} does not exist. Run the pipeline once to create it." );
      string id = found.RootElement.GetProperty( "id" ).GetString()!;
      _ids[name] = id;
      return id;
   }

   /// <summary>
   /// Sends one request with a short retry on connection failures and 5xx answers.
   /// Why retry here: the server can briefly refuse while it compacts its write-ahead log, and
   /// every call this sink makes is safe to repeat (upsert, delete, count, query).
   /// </summary>
   /// <param name="method">HTTP method.</param>
   /// <param name="path">Path below the base address.</param>
   /// <param name="body">UTF-8 JSON body, or null.</param>
   /// <param name="ct">Cancellation.</param>
   /// <param name="allowNotFound">Return 404 instead of throwing.</param>
   /// <param name="allowConflict">Return 409 instead of throwing.</param>
   /// <returns>Status and response text.</returns>
   private async Task<( HttpStatusCode Status, string Text )> SendAsync( HttpMethod method, string path, byte[]? body, CancellationToken ct,
      bool allowNotFound = false, bool allowConflict = false )
   {
      for( int attempt = 1; ; attempt++ )
      {
         try
         {
            using var request = new HttpRequestMessage( method, path );
            if( body != null )
            {
               request.Content = new ByteArrayContent( body );
               request.Content.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue( "application/json" );
            }

            using HttpResponseMessage response = await _http.SendAsync( request, ct );
            string text = await response.Content.ReadAsStringAsync( ct );
            if( response.IsSuccessStatusCode || ( allowNotFound && response.StatusCode == HttpStatusCode.NotFound ) || ( allowConflict && response.StatusCode == HttpStatusCode.Conflict ) )
            {
               return ( response.StatusCode, text );
            }

            if( (int)response.StatusCode >= 500 && attempt < MAX_ATTEMPTS )
            {
               await Task.Delay( TimeSpan.FromSeconds( attempt ), ct );
               continue;
            }

            throw new InvalidOperationException( $"Chroma answered {(int)response.StatusCode} to {method} {path}: {Shorten( text )}" );
         }
         catch( HttpRequestException ) when( attempt < MAX_ATTEMPTS )
         {
            await Task.Delay( TimeSpan.FromSeconds( attempt ), ct );
         }
      }
   }

   /// <summary>
   /// Cuts a server error message down so an exception stays readable.
   /// </summary>
   /// <param name="text">Response text.</param>
   /// <returns>At most 300 characters.</returns>
   private static string Shorten( string text )
   {
      return text.Length <= 300 ? text : text[..300] + "...";
   }

   /// <summary>
   /// Base path of this sink's tenant and database collections.
   /// </summary>
   /// <returns>The relative URL.</returns>
   private string CollectionsPath()
   {
      return $"api/v2/tenants/{Uri.EscapeDataString( _options.Tenant )}/databases/{Uri.EscapeDataString( _options.Database )}/collections";
   }

   /// <summary>
   /// Collection name for a pipeline. Pipeline names are already sanitized to [a-z0-9_] and never
   /// start or end with an underscore, which satisfies Chroma's naming rule once prefixed.
   /// </summary>
   /// <param name="collection">Pipeline collection name.</param>
   /// <returns>The Chroma collection name.</returns>
   private static string CollectionName( string collection )
   {
      return $"gvb_{collection}";
   }

   #endregion Private Methods

   #region IDisposable

   /// <summary>
   /// Releases the HTTP client.
   /// </summary>
   public void Dispose()
   {
      _http.Dispose();
   }

   #endregion IDisposable
}
