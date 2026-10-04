using System.Collections.Concurrent;
using System.Globalization;
using System.Text;
using System.Text.Json;
using GenericVectorBuilder.Core.Contracts;
using GenericVectorBuilder.Engines.Common;

namespace GenericVectorBuilder.Engines.Sinks;

/// <summary>
/// Writes vectors to Milvus over its REST API v2 (/v2/vectordb/...): one collection per
/// pipeline, named gvb_{pipeline}, with the chunk id as a VarChar primary key, a FloatVector
/// field, and the chunk text, document key, table, origin and ordinal as scalar fields.
/// Metadata (meta_ fields) goes to Milvus's dynamic field, so any key is accepted.
/// Index: HNSW with M 16 and efConstruction 128, metric COSINE (Milvus returns the cosine
/// similarity itself, higher is better). Query ef is 100 unless set in the options (see
/// <see cref="MilvusSinkOptions"/> for why not Milvus's own default). Milvus has no per-query
/// exact mode, so this sink does not implement <see cref="IExactSearchSink"/>.
/// Why CountAsync flushes first and asks for Strong consistency: Milvus keeps new rows in growing
/// segments and only a flush makes the stored row count final, and under its default (Bounded)
/// consistency a count can lag five seconds behind writes and deletes. The count itself is a
/// count(*) query, which respects deletes.
/// Why searches use Strong consistency by default: see <see cref="MilvusSinkOptions"/>.
/// Why <see cref="WaitForIndexAsync"/> exists: Milvus builds the HNSW index in the background
/// after a segment is sealed, and searches over a segment without its index fall back to a
/// slower scan. A benchmark must wait for the index before it times searches.
/// Why errors are read from the body: Milvus answers HTTP 200 and reports failure in a "code"
/// field, so a successful status alone proves nothing.
/// </summary>
public sealed class MilvusSink : ISink, IEngineDescription, IDisposable
{
   #region Data Members

   private const int UPSERT_BATCH = 500;
   private const int DELETE_BATCH = 500;
   private const int MAX_ATTEMPTS = 3;
   private const int ID_CHARS = 36;
   private const int KEY_CHARS = 1024;
   private const int ORIGIN_CHARS = 4096;
   private const int TEXT_BYTES = 65000;
   private const string VECTOR_FIELD = "vector";
   private const string INDEX_NAME = "vector_hnsw";
   private const int NOT_LOADED_CODE = 106;
   private const int RATE_LIMITED_CODE = 1807;
   private static readonly TimeSpan FLUSH_INTERVAL = TimeSpan.FromSeconds( 10 );

   private readonly MilvusSinkOptions _options;
   private readonly HttpClient _http;
   private readonly ConcurrentDictionary<string, bool> _unflushed = new( StringComparer.Ordinal );

   #endregion Data Members

   #region Constructor

   /// <summary>
   /// Creates the sink with the local container defaults (http://127.0.0.1:19530).
   /// </summary>
   public MilvusSink() : this( new MilvusSinkOptions() )
   {
   }

   /// <summary>
   /// Creates the sink.
   /// </summary>
   /// <param name="options">Server address and index settings.</param>
   public MilvusSink( MilvusSinkOptions options )
   {
      _options = options;
      _http = new HttpClient { BaseAddress = new Uri( options.BaseUrl.TrimEnd( '/' ) + "/" ), Timeout = TimeSpan.FromMinutes( 5 ) };
   }

   #endregion Constructor

   #region Public Methods

   /// <inheritdoc />
   public string Name => "milvus";

   /// <inheritdoc />
   public string Engine => "Milvus 2.6.25 (standalone, embedded etcd, local storage, REST API v2)";

   /// <inheritdoc />
   public string IndexDescription =>
      $"HNSW M={_options.M} efConstruction={_options.EfConstruction}, ef={( _options.Ef.HasValue ? _options.Ef.Value.ToString( CultureInfo.InvariantCulture ) : "Milvus default" )}, metric COSINE, {_options.SearchConsistency} consistency searches; approximate only (no exact mode)";

   /// <inheritdoc />
   public string ComposeFile => "milvus.compose.yaml";

   /// <inheritdoc />
   public async Task<bool> EnsureCollectionAsync( string collection, int dimension, CancellationToken ct )
   {
      string name = CollectionName( collection );
      if( await HasCollectionAsync( name, ct ) )
      {
         int stored = await StoredDimensionAsync( name, ct );
         if( stored != dimension )
         {
            throw new InvalidOperationException( $"Milvus collection {name} holds {stored}-dim vectors but the embedder produces {dimension}. Reset the pipeline or use a new name." );
         }

         await LoadAsync( name, ct );
         return false;
      }

      await PostAsync( "v2/vectordb/collections/create", BuildCreateBody( name, dimension ), ct );
      await LoadAsync( name, ct );
      return true;
   }

   /// <inheritdoc />
   public async Task UpsertAsync( string collection, IReadOnlyList<VectorRecord> records, CancellationToken ct )
   {
      string name = CollectionName( collection );
      foreach( VectorRecord[] batch in records.Chunk( UPSERT_BATCH ) )
      {
         await PostAsync( "v2/vectordb/entities/upsert", BuildUpsertBody( name, batch ), ct );
         _unflushed[name] = true;
      }
   }

   /// <inheritdoc />
   public async Task DeleteAsync( string collection, IReadOnlyList<Guid> chunkIds, CancellationToken ct )
   {
      string name = CollectionName( collection );
      foreach( Guid[] batch in chunkIds.Chunk( DELETE_BATCH ) )
      {
         string filter = "id in [" + string.Join( ",", batch.Select( g => $"\"{g}\"" ) ) + "]";
         await PostAsync( "v2/vectordb/entities/delete", JsonSerializer.SerializeToUtf8Bytes( new { collectionName = name, filter } ), ct );
      }
   }

   /// <inheritdoc />
   public async Task<long> CountAsync( string collection, CancellationToken ct )
   {
      string name = CollectionName( collection );
      if( _unflushed.ContainsKey( name ) )
      {
         await FlushAsync( name, waitForTurn: false, ct );
      }

      byte[] body = JsonSerializer.SerializeToUtf8Bytes( new { collectionName = name, filter = "", outputFields = new[] { "count(*)" }, consistencyLevel = "Strong" } );
      using JsonDocument result = await PostAsync( "v2/vectordb/entities/query", body, ct );
      return result.RootElement.GetProperty( "data" )[0].GetProperty( "count(*)" ).GetInt64();
   }

   /// <inheritdoc />
   public async Task<IReadOnlyList<SearchHit>> SearchAsync( string collection, float[] vector, int top, CancellationToken ct )
   {
      string name = CollectionName( collection );
      byte[] body = BuildSearchBody( name, vector, top );
      JsonDocument result;
      try
      {
         result = await PostAsync( "v2/vectordb/entities/search", body, ct );
      }
      catch( MilvusException ex ) when( ex.Code == NOT_LOADED_CODE )
      {
         await LoadAsync( name, ct );
         result = await PostAsync( "v2/vectordb/entities/search", body, ct );
      }

      using( result )
      {
         var hits = new List<SearchHit>();
         foreach( JsonElement row in result.RootElement.GetProperty( "data" ).EnumerateArray() )
         {
            hits.Add( new SearchHit( Guid.Parse( row.GetProperty( "id" ).GetString()! ), Text( row, "doc_key" ), Text( row, "table" ), Text( row, "text" ),
               row.GetProperty( "distance" ).GetDouble() ) );
         }

         return hits;
      }
   }

   /// <inheritdoc />
   public async Task DropCollectionAsync( string collection, CancellationToken ct )
   {
      string name = CollectionName( collection );
      if( await HasCollectionAsync( name, ct ) )
      {
         await PostAsync( "v2/vectordb/collections/drop", JsonSerializer.SerializeToUtf8Bytes( new { collectionName = name } ), ct );
      }
   }

   /// <summary>
   /// Waits until Milvus has built the HNSW index over every row, so timed searches measure the
   /// index and not a scan of segments that have none yet. Not part of <see cref="ISink"/>:
   /// only a benchmark needs it. Flushes first so that the last rows are sealed and indexed too.
   /// </summary>
   /// <param name="collection">Collection name.</param>
   /// <param name="timeout">How long to wait before giving up.</param>
   /// <param name="ct">Cancellation.</param>
   /// <returns>True when the index covers every row, false on timeout.</returns>
   public async Task<bool> WaitForIndexAsync( string collection, TimeSpan timeout, CancellationToken ct )
   {
      string name = CollectionName( collection );
      DateTime deadline = DateTime.UtcNow + timeout;
      await FlushAsync( name, waitForTurn: true, ct );
      while( DateTime.UtcNow < deadline )
      {
         using JsonDocument result = await PostAsync( "v2/vectordb/indexes/describe", JsonSerializer.SerializeToUtf8Bytes( new { collectionName = name, indexName = INDEX_NAME } ), ct );
         JsonElement index = result.RootElement.GetProperty( "data" )[0];
         long total = index.TryGetProperty( "totalRows", out JsonElement t ) ? t.GetInt64() : -1;
         long indexed = index.TryGetProperty( "indexedRows", out JsonElement i ) ? i.GetInt64() : -1;
         long pending = index.TryGetProperty( "pendingRows", out JsonElement p ) ? p.GetInt64() : -1;
         if( total >= 0 && indexed >= total && pending == 0 )
         {
            return true;
         }

         await Task.Delay( 1000, ct );
      }

      return false;
   }

   #endregion Public Methods

   #region Private Methods

   /// <summary>
   /// Builds the collection definition: schema, HNSW index on the vector field, cosine metric.
   /// </summary>
   /// <param name="name">Milvus collection name.</param>
   /// <param name="dimension">Embedding dimension.</param>
   /// <returns>UTF-8 JSON.</returns>
   private byte[] BuildCreateBody( string name, int dimension )
   {
      return JsonSerializer.SerializeToUtf8Bytes( new Dictionary<string, object>
      {
         ["collectionName"] = name,
         ["schema"] = new Dictionary<string, object>
         {
            ["autoId"] = false,
            ["enableDynamicField"] = true,
            ["fields"] = new object[]
            {
               Field( "id", "VarChar", isPrimary: true, ( "max_length", ID_CHARS ) ),
               Field( VECTOR_FIELD, "FloatVector", isPrimary: false, ( "dim", dimension ) ),
               Field( "doc_key", "VarChar", isPrimary: false, ( "max_length", KEY_CHARS ) ),
               Field( "table", "VarChar", isPrimary: false, ( "max_length", KEY_CHARS ) ),
               Field( "origin", "VarChar", isPrimary: false, ( "max_length", ORIGIN_CHARS ) ),
               Field( "ordinal", "Int32", isPrimary: false ),
               Field( "text", "VarChar", isPrimary: false, ( "max_length", 65535 ) ),
            },
         },
         ["indexParams"] = new object[]
         {
            new Dictionary<string, object>
            {
               ["fieldName"] = VECTOR_FIELD,
               ["indexName"] = INDEX_NAME,
               ["metricType"] = "COSINE",
               ["indexType"] = "HNSW",
               ["params"] = new Dictionary<string, object> { ["M"] = _options.M, ["efConstruction"] = _options.EfConstruction },
            },
         },
      } );
   }

   /// <summary>
   /// One schema field definition.
   /// </summary>
   /// <param name="name">Field name.</param>
   /// <param name="dataType">Milvus data type.</param>
   /// <param name="isPrimary">True for the primary key.</param>
   /// <param name="typeParams">Type parameters such as max_length or dim.</param>
   /// <returns>An object ready for JSON serialization.</returns>
   private static object Field( string name, string dataType, bool isPrimary, params ( string Key, int Value )[] typeParams )
   {
      var field = new Dictionary<string, object> { ["fieldName"] = name, ["dataType"] = dataType };
      if( isPrimary )
      {
         field["isPrimary"] = true;
      }

      if( typeParams.Length > 0 )
      {
         field["elementTypeParams"] = typeParams.ToDictionary( p => p.Key, p => (object)p.Value.ToString( CultureInfo.InvariantCulture ) );
      }

      return field;
   }

   /// <summary>
   /// Builds one upsert body: a row object per record.
   /// </summary>
   /// <param name="name">Milvus collection name.</param>
   /// <param name="batch">Records for this call.</param>
   /// <returns>UTF-8 JSON.</returns>
   private static byte[] BuildUpsertBody( string name, VectorRecord[] batch )
   {
      using var stream = new MemoryStream();
      using( var writer = new Utf8JsonWriter( stream ) )
      {
         writer.WriteStartObject();
         writer.WriteString( "collectionName", name );
         writer.WriteStartArray( "data" );
         foreach( VectorRecord r in batch )
         {
            writer.WriteStartObject();
            writer.WriteString( "id", r.Chunk.ChunkId.ToString() );
            writer.WriteStartArray( VECTOR_FIELD );
            foreach( float f in r.Vector )
            {
               writer.WriteNumberValue( f );
            }

            writer.WriteEndArray();
            writer.WriteString( "doc_key", Clamp( r.Document.DocKey, KEY_CHARS ) );
            writer.WriteString( "table", Clamp( r.Document.Table, KEY_CHARS ) );
            writer.WriteString( "origin", Clamp( r.Document.Origin, ORIGIN_CHARS ) );
            writer.WriteNumber( "ordinal", r.Chunk.Ordinal );
            writer.WriteString( "text", ClampBytes( r.Chunk.Text, TEXT_BYTES ) );
            foreach( KeyValuePair<string, string> pair in r.Document.Metadata )
            {
               writer.WriteString( $"meta_{pair.Key}", pair.Value );
            }

            writer.WriteEndObject();
         }

         writer.WriteEndArray();
         writer.WriteEndObject();
      }

      return stream.ToArray();
   }

   /// <summary>
   /// Builds one search body: the query vector, the result limit, the fields to return and the
   /// ef (only when one is configured).
   /// </summary>
   /// <param name="name">Milvus collection name.</param>
   /// <param name="vector">Query vector.</param>
   /// <param name="top">How many hits.</param>
   /// <returns>UTF-8 JSON.</returns>
   private byte[] BuildSearchBody( string name, float[] vector, int top )
   {
      using var stream = new MemoryStream();
      using( var writer = new Utf8JsonWriter( stream ) )
      {
         writer.WriteStartObject();
         writer.WriteString( "collectionName", name );
         writer.WriteStartArray( "data" );
         writer.WriteStartArray();
         foreach( float f in vector )
         {
            writer.WriteNumberValue( f );
         }

         writer.WriteEndArray();
         writer.WriteEndArray();
         writer.WriteString( "annsField", VECTOR_FIELD );
         writer.WriteString( "consistencyLevel", _options.SearchConsistency );
         writer.WriteNumber( "limit", top );
         writer.WriteStartArray( "outputFields" );
         foreach( string field in new[] { "doc_key", "table", "text" } )
         {
            writer.WriteStringValue( field );
         }

         writer.WriteEndArray();
         if( _options.Ef.HasValue )
         {
            writer.WriteStartObject( "searchParams" );
            writer.WriteStartObject( "params" );
            writer.WriteNumber( "ef", _options.Ef.Value );
            writer.WriteEndObject();
            writer.WriteEndObject();
         }

         writer.WriteEndObject();
      }

      return stream.ToArray();
   }

   /// <summary>
   /// Seals the collection's growing segments so they are final and get indexed. Milvus allows
   /// one flush per collection every ten seconds and answers "rate limit exceeded" to more.
   /// Why that is tolerated for a count: a Strong count(*) already includes unsealed rows, so a
   /// refused flush costs nothing. Why <paramref name="waitForTurn"/> exists: waiting for the
   /// index only makes sense once the last rows are sealed, so that caller waits its turn.
   /// </summary>
   /// <param name="name">Milvus collection name.</param>
   /// <param name="waitForTurn">True to retry until the flush is accepted, false to give up quietly when refused.</param>
   /// <param name="ct">Cancellation.</param>
   private async Task FlushAsync( string name, bool waitForTurn, CancellationToken ct )
   {
      byte[] body = JsonSerializer.SerializeToUtf8Bytes( new { collectionName = name } );
      for( int attempt = 1; ; attempt++ )
      {
         try
         {
            using( await PostAsync( "v2/vectordb/collections/flush", body, ct ) )
            {
            }

            _unflushed.TryRemove( name, out _ );
            return;
         }
         catch( MilvusException ex ) when( ex.Code == RATE_LIMITED_CODE )
         {
            if( !waitForTurn || attempt >= MAX_ATTEMPTS * 2 )
            {
               return;
            }

            await Task.Delay( FLUSH_INTERVAL, ct );
         }
      }
   }

   /// <summary>
   /// Asks Milvus whether a collection exists.
   /// </summary>
   /// <param name="name">Milvus collection name.</param>
   /// <param name="ct">Cancellation.</param>
   /// <returns>True when it exists.</returns>
   private async Task<bool> HasCollectionAsync( string name, CancellationToken ct )
   {
      using JsonDocument result = await PostAsync( "v2/vectordb/collections/has", JsonSerializer.SerializeToUtf8Bytes( new { collectionName = name } ), ct );
      return result.RootElement.GetProperty( "data" ).GetProperty( "has" ).GetBoolean();
   }

   /// <summary>
   /// Reads the vector dimension out of a collection's schema.
   /// </summary>
   /// <param name="name">Milvus collection name.</param>
   /// <param name="ct">Cancellation.</param>
   /// <returns>The stored dimension.</returns>
   private async Task<int> StoredDimensionAsync( string name, CancellationToken ct )
   {
      using JsonDocument result = await PostAsync( "v2/vectordb/collections/describe", JsonSerializer.SerializeToUtf8Bytes( new { collectionName = name } ), ct );
      foreach( JsonElement field in result.RootElement.GetProperty( "data" ).GetProperty( "fields" ).EnumerateArray() )
      {
         if( field.GetProperty( "name" ).GetString() != VECTOR_FIELD || !field.TryGetProperty( "params", out JsonElement typeParams ) )
         {
            continue;
         }

         foreach( JsonElement p in typeParams.EnumerateArray() )
         {
            if( p.GetProperty( "key" ).GetString() == "dim" )
            {
               return int.Parse( p.GetProperty( "value" ).GetString()!, CultureInfo.InvariantCulture );
            }
         }
      }

      throw new InvalidOperationException( $"Milvus collection {name} has no '{VECTOR_FIELD}' field, so it was not created by this tool. Use a new name." );
   }

   /// <summary>
   /// Loads the collection into memory for searching and waits until Milvus says it is loaded.
   /// </summary>
   /// <param name="name">Milvus collection name.</param>
   /// <param name="ct">Cancellation.</param>
   private async Task LoadAsync( string name, CancellationToken ct )
   {
      byte[] body = JsonSerializer.SerializeToUtf8Bytes( new { collectionName = name } );
      using( await PostAsync( "v2/vectordb/collections/load", body, ct ) )
      {
      }

      for( int i = 0; i < 120; i++ )
      {
         using JsonDocument state = await PostAsync( "v2/vectordb/collections/get_load_state", body, ct );
         if( state.RootElement.GetProperty( "data" ).GetProperty( "loadState" ).GetString() == "LoadStateLoaded" )
         {
            return;
         }

         await Task.Delay( 500, ct );
      }

      throw new InvalidOperationException( $"Milvus did not finish loading collection {name} within a minute." );
   }

   /// <summary>
   /// Posts a JSON body and checks Milvus's own result code, with a short retry on connection
   /// failures and 5xx answers. Every call this sink makes is safe to repeat (upsert by id,
   /// delete by filter, queries, load).
   /// </summary>
   /// <param name="path">Path below the base address.</param>
   /// <param name="body">UTF-8 JSON body.</param>
   /// <param name="ct">Cancellation.</param>
   /// <returns>The parsed response; the caller disposes it.</returns>
   private async Task<JsonDocument> PostAsync( string path, byte[] body, CancellationToken ct )
   {
      for( int attempt = 1; ; attempt++ )
      {
         try
         {
            using var request = new HttpRequestMessage( HttpMethod.Post, path ) { Content = new ByteArrayContent( body ) };
            request.Content.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue( "application/json" );
            using HttpResponseMessage response = await _http.SendAsync( request, ct );
            string text = await response.Content.ReadAsStringAsync( ct );
            if( (int)response.StatusCode >= 500 && attempt < MAX_ATTEMPTS )
            {
               await Task.Delay( TimeSpan.FromSeconds( attempt ), ct );
               continue;
            }

            if( !response.IsSuccessStatusCode )
            {
               throw new InvalidOperationException( $"Milvus answered {(int)response.StatusCode} to POST {path}: {Shorten( text )}" );
            }

            JsonDocument doc = JsonDocument.Parse( text );
            int code = doc.RootElement.TryGetProperty( "code", out JsonElement c ) ? c.GetInt32() : 0;
            if( code != 0 )
            {
               string message = doc.RootElement.TryGetProperty( "message", out JsonElement m ) ? m.GetString() ?? string.Empty : string.Empty;
               doc.Dispose();
               throw new MilvusException( code, $"Milvus refused POST {path} (code {code}): {Shorten( message )}" );
            }

            return doc;
         }
         catch( HttpRequestException ) when( attempt < MAX_ATTEMPTS )
         {
            await Task.Delay( TimeSpan.FromSeconds( attempt ), ct );
         }
      }
   }

   /// <summary>
   /// Reads a string field from a search row, or empty when it is missing or null.
   /// </summary>
   /// <param name="row">One result row.</param>
   /// <param name="key">Field name.</param>
   /// <returns>The value.</returns>
   private static string Text( JsonElement row, string key )
   {
      return row.TryGetProperty( key, out JsonElement value ) && value.ValueKind == JsonValueKind.String ? value.GetString()! : string.Empty;
   }

   /// <summary>
   /// Shortens a string to at most the given number of characters (a VarChar limit counts
   /// characters). Only a search result's display text is affected.
   /// </summary>
   /// <param name="value">The text.</param>
   /// <param name="maxChars">Column length.</param>
   /// <returns>The text, cut if needed.</returns>
   private static string Clamp( string value, int maxChars )
   {
      return value.Length <= maxChars ? value : value[..maxChars];
   }

   /// <summary>
   /// Shortens a string so its UTF-8 form fits in the given number of bytes, never cutting a
   /// character in half. Why bytes: Milvus measures VarChar length in bytes for the text field.
   /// </summary>
   /// <param name="value">The text.</param>
   /// <param name="maxBytes">Column length in bytes.</param>
   /// <returns>The text, cut if needed.</returns>
   private static string ClampBytes( string value, int maxBytes )
   {
      if( Encoding.UTF8.GetByteCount( value ) <= maxBytes )
      {
         return value;
      }

      int length = Math.Min( value.Length, maxBytes / 3 );
      while( length < value.Length && Encoding.UTF8.GetByteCount( value.AsSpan( 0, length + 1 ) ) <= maxBytes )
      {
         length++;
      }

      return char.IsHighSurrogate( value[length - 1] ) ? value[..( length - 1 )] : value[..length];
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
   /// Collection name for a pipeline. Pipeline names are already sanitized to [a-z0-9_].
   /// </summary>
   /// <param name="collection">Pipeline collection name.</param>
   /// <returns>The Milvus collection name.</returns>
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

/// <summary>
/// A failure Milvus reported in the body of an HTTP 200 answer.
/// Why a type: the sink reacts to one specific code (collection not loaded) and rethrows the rest.
/// </summary>
public sealed class MilvusException : InvalidOperationException
{
   /// <summary>
   /// Creates the exception.
   /// </summary>
   /// <param name="code">Milvus result code.</param>
   /// <param name="message">Plain-English message.</param>
   public MilvusException( int code, string message ) : base( message )
   {
      Code = code;
   }

   /// <summary>
   /// Milvus result code, e.g. 106 for "collection not loaded".
   /// </summary>
   public int Code { get; }
}
