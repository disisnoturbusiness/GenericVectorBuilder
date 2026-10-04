using System.Collections.Concurrent;
using System.Globalization;
using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using GenericVectorBuilder.Core.Contracts;
using GenericVectorBuilder.Engines.Common;

namespace GenericVectorBuilder.Engines.Sinks;

/// <summary>
/// Writes vectors to Weaviate over REST (schema, batch import, batch delete) and searches with a
/// GraphQL nearVector query. One class per pipeline, named Gvb_{pipeline} (Weaviate class names
/// must start with a capital letter), holding the chunk text, document key, table, origin,
/// ordinal and meta_ fields as properties. The chunk id is the object id, so a re-import of an
/// unchanged chunk replaces itself.
/// Index: HNSW with maxConnections (M) 16, efConstruction 128 and ef -1 (dynamic: limit x 8
/// clamped to 100..500, so 100 for a top 10), cosine distance, no quantization. Weaviate has no
/// way to run an exact scan over an HNSW collection, so this sink does not implement
/// <see cref="IExactSearchSink"/>.
/// Why skipDefaultQuantization is set: newer Weaviate versions may compress vectors by default;
/// the benchmark compares full-precision indexes, so compression is switched off explicitly.
/// Why payload properties are declared with indexing off: by default Weaviate builds a keyword
/// index and a filter index for every text property. The benchmark only does vector search, and
/// leaving them on would slow Weaviate's writes for nothing the benchmark uses. Properties found
/// in document metadata are declared the same way before their first batch.
/// Why the dimension is kept in the class description: Weaviate learns a dimension from the
/// first object, so an empty class cannot say what it was created for.
/// Why batch results are checked one by one: Weaviate answers 200 to a batch even when some
/// objects failed, and the failures are only visible inside the response.
/// </summary>
public sealed class WeaviateSink : ISink, IEngineDescription, IDisposable
{
   #region Data Members

   private const int UPSERT_BATCH = 200;
   private const int DELETE_BATCH = 1000;
   private const int MAX_ATTEMPTS = 3;
   private const string DESCRIPTION_PREFIX = "gvb dimension=";
   private static readonly Regex UNSAFE_NAME = new( "[^A-Za-z0-9_]", RegexOptions.Compiled );

   private readonly WeaviateSinkOptions _options;
   private readonly HttpClient _http;
   private readonly ConcurrentDictionary<string, HashSet<string>> _properties = new( StringComparer.Ordinal );

   #endregion Data Members

   #region Constructor

   /// <summary>
   /// Creates the sink with the local container defaults (http://127.0.0.1:8085).
   /// </summary>
   public WeaviateSink() : this( new WeaviateSinkOptions() )
   {
   }

   /// <summary>
   /// Creates the sink.
   /// </summary>
   /// <param name="options">Server address and index settings.</param>
   public WeaviateSink( WeaviateSinkOptions options )
   {
      _options = options;
      _http = new HttpClient { BaseAddress = new Uri( options.BaseUrl.TrimEnd( '/' ) + "/" ), Timeout = TimeSpan.FromMinutes( 5 ) };
   }

   #endregion Constructor

   #region Public Methods

   /// <inheritdoc />
   public string Name => "weaviate";

   /// <inheritdoc />
   public string Engine => "Weaviate 1.39.8 (single node, REST + GraphQL)";

   /// <inheritdoc />
   public string IndexDescription =>
      $"HNSW maxConnections(M)={_options.MaxConnections} efConstruction={_options.EfConstruction}, ef={_options.Ef} (dynamic: limit x 8 clamped 100..500), cosine, no quantization; approximate only (no exact mode)";

   /// <inheritdoc />
   public string ComposeFile => "weaviate.compose.yaml";

   /// <inheritdoc />
   public async Task<bool> EnsureCollectionAsync( string collection, int dimension, CancellationToken ct )
   {
      string name = ClassName( collection );
      using JsonDocument? existing = await GetClassAsync( name, ct );
      if( existing != null )
      {
         VerifyDimension( name, existing.RootElement, dimension );
         _properties[name] = PropertyNames( existing.RootElement );
         return false;
      }

      ( HttpStatusCode status, string text ) = await SendAsync( HttpMethod.Post, "v1/schema", BuildClassBody( name, dimension ), ct, allowUnprocessable: true );
      if( status == HttpStatusCode.UnprocessableEntity )
      {
         // Another writer created it between our look and our create; check theirs instead.
         using JsonDocument? raced = await GetClassAsync( name, ct );
         if( raced == null )
         {
            throw new InvalidOperationException( $"Weaviate refused to create class {name}: {Shorten( text )}" );
         }

         VerifyDimension( name, raced.RootElement, dimension );
         _properties[name] = PropertyNames( raced.RootElement );
         return false;
      }

      _properties[name] = new HashSet<string>( new[] { "doc_key", "table", "origin", "ordinal", "text" }, StringComparer.Ordinal );
      return true;
   }

   /// <inheritdoc />
   public async Task UpsertAsync( string collection, IReadOnlyList<VectorRecord> records, CancellationToken ct )
   {
      string name = ClassName( collection );
      foreach( VectorRecord[] batch in records.Chunk( UPSERT_BATCH ) )
      {
         await EnsurePropertiesAsync( name, batch, ct );
         ( _, string text ) = await SendAsync( HttpMethod.Post, "v1/batch/objects", BuildBatchBody( name, batch ), ct );
         ThrowOnFailedObjects( text );
      }
   }

   /// <inheritdoc />
   public async Task DeleteAsync( string collection, IReadOnlyList<Guid> chunkIds, CancellationToken ct )
   {
      string name = ClassName( collection );
      foreach( Guid[] batch in chunkIds.Chunk( DELETE_BATCH ) )
      {
         byte[] body = JsonSerializer.SerializeToUtf8Bytes( new
         {
            match = new { @class = name, where = new { path = new[] { "id" }, @operator = "ContainsAny", valueTextArray = batch.Select( g => g.ToString() ) } },
            output = "minimal",
         } );
         await SendAsync( HttpMethod.Delete, "v1/batch/objects", body, ct );
      }
   }

   /// <inheritdoc />
   public async Task<long> CountAsync( string collection, CancellationToken ct )
   {
      string name = ClassName( collection );
      using JsonDocument result = await GraphQlAsync( $"{{ Aggregate {{ {name} {{ meta {{ count }} }} }} }}", ct );
      return result.RootElement.GetProperty( "data" ).GetProperty( "Aggregate" ).GetProperty( name )[0].GetProperty( "meta" ).GetProperty( "count" ).GetInt64();
   }

   /// <inheritdoc />
   public async Task<IReadOnlyList<SearchHit>> SearchAsync( string collection, float[] vector, int top, CancellationToken ct )
   {
      string name = ClassName( collection );
      var query = new StringBuilder();
      query.Append( "{ Get { " ).Append( name ).Append( "( nearVector: { vector: [" );
      for( int i = 0; i < vector.Length; i++ )
      {
         query.Append( i == 0 ? "" : "," ).Append( vector[i].ToString( "R", CultureInfo.InvariantCulture ) );
      }

      query.Append( "] }, limit: " ).Append( top ).Append( " ) { doc_key table text _additional { id distance } } } }" );
      using JsonDocument result = await GraphQlAsync( query.ToString(), ct );
      JsonElement rows = result.RootElement.GetProperty( "data" ).GetProperty( "Get" ).GetProperty( name );
      var hits = new List<SearchHit>( rows.GetArrayLength() );
      foreach( JsonElement row in rows.EnumerateArray() )
      {
         JsonElement extra = row.GetProperty( "_additional" );
         hits.Add( new SearchHit( Guid.Parse( extra.GetProperty( "id" ).GetString()! ), Text( row, "doc_key" ), Text( row, "table" ), Text( row, "text" ),
            1.0 - extra.GetProperty( "distance" ).GetDouble() ) );
      }

      return hits;
   }

   /// <inheritdoc />
   public async Task DropCollectionAsync( string collection, CancellationToken ct )
   {
      string name = ClassName( collection );
      _properties.TryRemove( name, out _ );
      await SendAsync( HttpMethod.Delete, $"v1/schema/{name}", null, ct, allowNotFound: true );
   }

   #endregion Public Methods

   #region Private Methods

   /// <summary>
   /// Builds the class definition: HNSW settings, cosine distance, no vectorizer, and payload
   /// properties with all keyword and filter indexing switched off.
   /// </summary>
   /// <param name="name">Weaviate class name.</param>
   /// <param name="dimension">Embedding dimension, recorded in the description.</param>
   /// <returns>UTF-8 JSON.</returns>
   private byte[] BuildClassBody( string name, int dimension )
   {
      var properties = new List<object>
      {
         PropertyDefinition( "doc_key", "text" ),
         PropertyDefinition( "table", "text" ),
         PropertyDefinition( "origin", "text" ),
         PropertyDefinition( "ordinal", "int" ),
         PropertyDefinition( "text", "text" ),
      };

      return JsonSerializer.SerializeToUtf8Bytes( new Dictionary<string, object>
      {
         ["class"] = name,
         ["description"] = DESCRIPTION_PREFIX + dimension.ToString( CultureInfo.InvariantCulture ),
         ["vectorizer"] = "none",
         ["vectorIndexType"] = "hnsw",
         ["vectorIndexConfig"] = new Dictionary<string, object>
         {
            ["distance"] = "cosine",
            ["maxConnections"] = _options.MaxConnections,
            ["efConstruction"] = _options.EfConstruction,
            ["ef"] = _options.Ef,
            ["skipDefaultQuantization"] = true,
         },
         ["invertedIndexConfig"] = new Dictionary<string, object> { ["indexNullState"] = false, ["indexPropertyLength"] = false, ["indexTimestamps"] = false },
         ["properties"] = properties,
      } );
   }

   /// <summary>
   /// One payload property definition with every index off.
   /// </summary>
   /// <param name="name">Property name.</param>
   /// <param name="dataType">Weaviate data type, "text" or "int".</param>
   /// <returns>An object ready for JSON serialization.</returns>
   private static object PropertyDefinition( string name, string dataType )
   {
      var definition = new Dictionary<string, object> { ["name"] = name, ["dataType"] = new[] { dataType }, ["indexFilterable"] = false };
      if( dataType == "text" )
      {
         definition["indexSearchable"] = false;
      }

      return definition;
   }

   /// <summary>
   /// Declares any metadata property in this batch that the class does not have yet, with
   /// indexing off, so Weaviate's automatic schema never creates an indexed one.
   /// </summary>
   /// <param name="name">Weaviate class name.</param>
   /// <param name="batch">The batch about to be written.</param>
   /// <param name="ct">Cancellation.</param>
   private async Task EnsurePropertiesAsync( string name, VectorRecord[] batch, CancellationToken ct )
   {
      if( !_properties.TryGetValue( name, out HashSet<string>? known ) )
      {
         using JsonDocument? existing = await GetClassAsync( name, ct )
            ?? throw new InvalidOperationException( $"Weaviate class {name} does not exist. Run the pipeline once to create it." );
         known = PropertyNames( existing.RootElement );
         _properties[name] = known;
      }

      foreach( string property in batch.SelectMany( r => r.Document.Metadata.Keys ).Select( MetaProperty ).Distinct() )
      {
         if( known.Add( property ) )
         {
            byte[] body = JsonSerializer.SerializeToUtf8Bytes( PropertyDefinition( property, "text" ) );
            await SendAsync( HttpMethod.Post, $"v1/schema/{name}/properties", body, ct, allowUnprocessable: true );
         }
      }
   }

   /// <summary>
   /// Builds one batch import body: class, id, properties and vector for each record.
   /// </summary>
   /// <param name="name">Weaviate class name.</param>
   /// <param name="batch">Records for this call.</param>
   /// <returns>UTF-8 JSON.</returns>
   private static byte[] BuildBatchBody( string name, VectorRecord[] batch )
   {
      using var stream = new MemoryStream();
      using( var writer = new Utf8JsonWriter( stream ) )
      {
         writer.WriteStartObject();
         writer.WriteStartArray( "objects" );
         foreach( VectorRecord r in batch )
         {
            writer.WriteStartObject();
            writer.WriteString( "class", name );
            writer.WriteString( "id", r.Chunk.ChunkId.ToString() );
            writer.WriteStartObject( "properties" );
            writer.WriteString( "doc_key", r.Document.DocKey );
            writer.WriteString( "table", r.Document.Table );
            writer.WriteString( "origin", r.Document.Origin );
            writer.WriteNumber( "ordinal", r.Chunk.Ordinal );
            writer.WriteString( "text", r.Chunk.Text );
            foreach( KeyValuePair<string, string> pair in r.Document.Metadata )
            {
               writer.WriteString( MetaProperty( pair.Key ), pair.Value );
            }

            writer.WriteEndObject();
            writer.WriteStartArray( "vector" );
            foreach( float f in r.Vector )
            {
               writer.WriteNumberValue( f );
            }

            writer.WriteEndArray();
            writer.WriteEndObject();
         }

         writer.WriteEndArray();
         writer.WriteEndObject();
      }

      return stream.ToArray();
   }

   /// <summary>
   /// Throws when any object in a batch response failed (the HTTP status alone is still 200).
   /// </summary>
   /// <param name="json">Batch response body.</param>
   private static void ThrowOnFailedObjects( string json )
   {
      using JsonDocument doc = JsonDocument.Parse( json );
      foreach( JsonElement item in doc.RootElement.EnumerateArray() )
      {
         if( item.TryGetProperty( "result", out JsonElement result ) && result.TryGetProperty( "status", out JsonElement status ) && status.GetString() == "FAILED" )
         {
            string id = item.TryGetProperty( "id", out JsonElement idValue ) ? idValue.GetString() ?? "?" : "?";
            string message = result.TryGetProperty( "errors", out JsonElement errors ) ? Shorten( errors.ToString() ) : "no reason given";
            throw new InvalidOperationException( $"Weaviate failed to store object {id}: {message}" );
         }
      }
   }

   /// <summary>
   /// Runs a GraphQL query and throws if the server reports errors.
   /// </summary>
   /// <param name="query">GraphQL text.</param>
   /// <param name="ct">Cancellation.</param>
   /// <returns>The parsed response; the caller disposes it.</returns>
   private async Task<JsonDocument> GraphQlAsync( string query, CancellationToken ct )
   {
      byte[] body = JsonSerializer.SerializeToUtf8Bytes( new { query } );
      ( _, string text ) = await SendAsync( HttpMethod.Post, "v1/graphql", body, ct );
      JsonDocument doc = JsonDocument.Parse( text );
      if( doc.RootElement.TryGetProperty( "errors", out JsonElement errors ) && errors.ValueKind == JsonValueKind.Array && errors.GetArrayLength() > 0 )
      {
         string message = Shorten( errors.ToString() );
         doc.Dispose();
         throw new InvalidOperationException( $"Weaviate rejected the query: {message}" );
      }

      return doc;
   }

   /// <summary>
   /// Looks a class up by name.
   /// </summary>
   /// <param name="name">Weaviate class name.</param>
   /// <param name="ct">Cancellation.</param>
   /// <returns>The class JSON, or null when it does not exist.</returns>
   private async Task<JsonDocument?> GetClassAsync( string name, CancellationToken ct )
   {
      ( HttpStatusCode status, string text ) = await SendAsync( HttpMethod.Get, $"v1/schema/{name}", null, ct, allowNotFound: true );
      return status == HttpStatusCode.NotFound ? null : JsonDocument.Parse( text );
   }

   /// <summary>
   /// Refuses a class whose recorded dimension differs from the embedder's.
   /// </summary>
   /// <param name="name">Weaviate class name.</param>
   /// <param name="schema">The class JSON returned by the server.</param>
   /// <param name="dimension">Dimension the embedder produces.</param>
   private static void VerifyDimension( string name, JsonElement schema, int dimension )
   {
      string description = schema.TryGetProperty( "description", out JsonElement d ) ? d.GetString() ?? string.Empty : string.Empty;
      if( description.StartsWith( DESCRIPTION_PREFIX, StringComparison.Ordinal )
         && int.TryParse( description[DESCRIPTION_PREFIX.Length..], NumberStyles.Integer, CultureInfo.InvariantCulture, out int stored ) && stored != dimension )
      {
         throw new InvalidOperationException( $"Weaviate class {name} holds {stored}-dim vectors but the embedder produces {dimension}. Reset the pipeline or use a new name." );
      }
   }

   /// <summary>
   /// Lists the property names a class already has.
   /// </summary>
   /// <param name="schema">The class JSON.</param>
   /// <returns>Property names.</returns>
   private static HashSet<string> PropertyNames( JsonElement schema )
   {
      var names = new HashSet<string>( StringComparer.Ordinal );
      if( schema.TryGetProperty( "properties", out JsonElement properties ) && properties.ValueKind == JsonValueKind.Array )
      {
         foreach( JsonElement p in properties.EnumerateArray() )
         {
            names.Add( p.GetProperty( "name" ).GetString()! );
         }
      }

      return names;
   }

   /// <summary>
   /// Reads a string property from a GraphQL row, or empty when it is missing or null.
   /// </summary>
   /// <param name="row">One result row.</param>
   /// <param name="key">Property name.</param>
   /// <returns>The value.</returns>
   private static string Text( JsonElement row, string key )
   {
      return row.TryGetProperty( key, out JsonElement value ) && value.ValueKind == JsonValueKind.String ? value.GetString()! : string.Empty;
   }

   /// <summary>
   /// Sends one request with a short retry on connection failures and 5xx answers.
   /// Why retry here: every call this sink makes is safe to repeat (import by id, delete by
   /// filter, count, query).
   /// </summary>
   /// <param name="method">HTTP method.</param>
   /// <param name="path">Path below the base address.</param>
   /// <param name="body">UTF-8 JSON body, or null.</param>
   /// <param name="ct">Cancellation.</param>
   /// <param name="allowNotFound">Return 404 instead of throwing.</param>
   /// <param name="allowUnprocessable">Return 422 instead of throwing (Weaviate's answer to "already exists").</param>
   /// <returns>Status and response text.</returns>
   private async Task<( HttpStatusCode Status, string Text )> SendAsync( HttpMethod method, string path, byte[]? body, CancellationToken ct,
      bool allowNotFound = false, bool allowUnprocessable = false )
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
            if( response.IsSuccessStatusCode || ( allowNotFound && response.StatusCode == HttpStatusCode.NotFound )
               || ( allowUnprocessable && response.StatusCode == HttpStatusCode.UnprocessableEntity ) )
            {
               return ( response.StatusCode, text );
            }

            if( (int)response.StatusCode >= 500 && attempt < MAX_ATTEMPTS )
            {
               await Task.Delay( TimeSpan.FromSeconds( attempt ), ct );
               continue;
            }

            throw new InvalidOperationException( $"Weaviate answered {(int)response.StatusCode} to {method} {path}: {Shorten( text )}" );
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
   /// Weaviate property name for a document metadata key: meta_ plus the key with every
   /// character Weaviate does not allow in a name replaced by an underscore.
   /// </summary>
   /// <param name="key">Metadata key.</param>
   /// <returns>The property name.</returns>
   private static string MetaProperty( string key )
   {
      return "meta_" + UNSAFE_NAME.Replace( key, "_" );
   }

   /// <summary>
   /// Class name for a pipeline. Pipeline names are already sanitized to [a-z0-9_]; Weaviate
   /// needs the first letter capitalized.
   /// </summary>
   /// <param name="collection">Pipeline collection name.</param>
   /// <returns>The Weaviate class name.</returns>
   private static string ClassName( string collection )
   {
      return $"Gvb_{collection}";
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
