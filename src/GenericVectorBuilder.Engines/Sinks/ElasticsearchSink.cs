using System.Buffers;
using System.Text.Json;
using GenericVectorBuilder.Core.Contracts;
using GenericVectorBuilder.Engines.Common;

namespace GenericVectorBuilder.Engines.Sinks;

/// <summary>
/// Writes vectors to Elasticsearch 9 over its REST API: one index per pipeline, named gvb_{pipeline},
/// with a dense_vector field and the chunk text, document key, table, origin, ordinal and
/// meta_* values as ordinary fields. The chunk id is the Elasticsearch document id, so a repeated
/// write replaces the old document.
/// Index settings: one shard, no replicas (a benchmark box has one node), and a dense_vector with
/// index_options type "hnsw", m = 16, ef_construction = 128, similarity cosine.
/// Why type "hnsw" is written out: from version 9 the default for 384 or more dimensions is bbq_hnsw,
/// which compresses vectors to one bit each. That is fast but lossy, and the benchmark compares
/// engines on full float32 vectors, so the default is overridden.
/// Why the vector is excluded from _source: the stored copy is only needed to rebuild the index,
/// and leaving it out halves the disk used.
/// Search: the kNN search with k = top and num_candidates = 100 (see
/// <see cref="ElasticsearchSinkOptions.NumCandidates"/>). Elasticsearch scores cosine as (1 + cosine) / 2,
/// which the sink converts back to the plain cosine similarity every other engine reports.
/// Exact search (<see cref="IExactSearchSink"/>): a script_score query that computes
/// cosineSimilarity against every document, so it scans the whole index.
/// Counting refreshes first, because Elasticsearch makes new documents searchable only after a refresh.
/// </summary>
public sealed class ElasticsearchSink : ISink, IExactSearchSink, IEngineDescription, IDisposable
{
   #region Data Members

   private const int UPSERT_BATCH = 500;
   private const int DELETE_BATCH = 1000;
   private const int HNSW_M = 16;
   private const int HNSW_EF_CONSTRUCTION = 128;
   private const int MAX_NUM_CANDIDATES = 10000;
   private const string NDJSON = "application/x-ndjson";
   private static readonly string[] SOURCE_FIELDS = { "doc_key", "table", "text" };

   private readonly ElasticsearchSinkOptions _options;
   private readonly ElasticsearchRest _rest;

   #endregion Data Members

   #region Constructor

   /// <summary>
   /// Creates the sink against the local Docker container.
   /// </summary>
   public ElasticsearchSink()
      : this( new ElasticsearchSinkOptions() )
   {
   }

   /// <summary>
   /// Creates the sink with explicit settings.
   /// </summary>
   /// <param name="options">Address and query settings.</param>
   public ElasticsearchSink( ElasticsearchSinkOptions options )
   {
      _options = options;
      _rest = new ElasticsearchRest( options.BaseUrl, options.EffectiveTimeout );
   }

   #endregion Constructor

   #region Public Methods

   /// <inheritdoc />
   public string Name => "elasticsearch";

   /// <inheritdoc />
   public string Engine => "Elasticsearch 9.5.3 (dense_vector)";

   /// <inheritdoc />
   public string IndexDescription =>
      $"HNSW float32, no quantization, m={HNSW_M}, ef_construction={HNSW_EF_CONSTRUCTION}, cosine; search k=top, num_candidates={_options.NumCandidates}; 1 shard, 0 replicas";

   /// <inheritdoc />
   public string ComposeFile => "elasticsearch.compose.yaml";

   /// <inheritdoc />
   public async Task<bool> EnsureCollectionAsync( string collection, int dimension, CancellationToken ct )
   {
      string index = IndexName( collection );
      if( await VerifyDimensionAsync( index, dimension, ct ) )
      {
         return false;
      }

      try
      {
         using JsonDocument? created = await _rest.SendJsonAsync( HttpMethod.Put, index, IndexDefinition( dimension ), false, ct );
         return true;
      }
      catch( ElasticsearchRestException ex ) when( ex.ErrorType == "resource_already_exists_exception" )
      {
         await VerifyDimensionAsync( index, dimension, ct );
         return false;
      }
   }

   /// <inheritdoc />
   public async Task UpsertAsync( string collection, IReadOnlyList<VectorRecord> records, CancellationToken ct )
   {
      string index = IndexName( collection );
      foreach( VectorRecord[] batch in records.Chunk( UPSERT_BATCH ) )
      {
         await BulkAsync( index, WriteIndexActions( batch ), ct );
      }
   }

   /// <inheritdoc />
   public async Task DeleteAsync( string collection, IReadOnlyList<Guid> chunkIds, CancellationToken ct )
   {
      string index = IndexName( collection );
      foreach( Guid[] batch in chunkIds.Chunk( DELETE_BATCH ) )
      {
         await BulkAsync( index, WriteDeleteActions( batch ), ct );
      }
   }

   /// <inheritdoc />
   public async Task<long> CountAsync( string collection, CancellationToken ct )
   {
      string index = IndexName( collection );
      using JsonDocument? refreshed = await _rest.SendAsync( HttpMethod.Post, $"{index}/_refresh", null, "application/json", false, ct );
      using JsonDocument? answer = await _rest.SendAsync( HttpMethod.Get, $"{index}/_count", null, "application/json", false, ct );
      return answer!.RootElement.GetProperty( "count" ).GetInt64();
   }

   /// <inheritdoc />
   public async Task<IReadOnlyList<SearchHit>> SearchAsync( string collection, float[] vector, int top, CancellationToken ct )
   {
      int candidates = Math.Clamp( Math.Max( _options.NumCandidates, top ), top, MAX_NUM_CANDIDATES );
      var body = new
      {
         size = top,
         track_total_hits = false,
         _source = SOURCE_FIELDS,
         knn = new { field = "vector", query_vector = vector, k = top, num_candidates = candidates },
      };

      using JsonDocument? answer = await _rest.SendJsonAsync( HttpMethod.Post, $"{IndexName( collection )}/_search", body, false, ct );
      return ReadHits( answer!, score => 2.0 * score - 1.0 );
   }

   /// <inheritdoc />
   public async Task<IReadOnlyList<SearchHit>> SearchExactAsync( string collection, float[] vector, int top, CancellationToken ct )
   {
      var body = new
      {
         size = top,
         track_total_hits = false,
         _source = SOURCE_FIELDS,
         query = new
         {
            script_score = new
            {
               query = new { match_all = new { } },
               script = new { source = "cosineSimilarity(params.q, 'vector') + 1.0", @params = new { q = vector } },
            },
         },
      };

      using JsonDocument? answer = await _rest.SendJsonAsync( HttpMethod.Post, $"{IndexName( collection )}/_search", body, false, ct );
      return ReadHits( answer!, score => score - 1.0 );
   }

   /// <inheritdoc />
   public async Task DropCollectionAsync( string collection, CancellationToken ct )
   {
      using JsonDocument? answer = await _rest.SendJsonAsync( HttpMethod.Delete, IndexName( collection ), null, true, ct );
   }

   #endregion Public Methods

   #region Private Methods

   /// <summary>
   /// Index name for a pipeline. Pipeline names are already sanitized to [a-z0-9_].
   /// </summary>
   /// <param name="collection">Pipeline collection name.</param>
   /// <returns>The Elasticsearch index name.</returns>
   private static string IndexName( string collection )
   {
      return $"gvb_{collection}";
   }

   /// <summary>
   /// Builds the index settings and mapping for a vector of the given length.
   /// Payload fields are keyword or unindexed text because the sink only reads them back; meta_*
   /// values become keywords through a dynamic template so any metadata key is accepted.
   /// </summary>
   /// <param name="dimension">Embedding dimension.</param>
   /// <returns>The request body.</returns>
   private static object IndexDefinition( int dimension )
   {
      return new
      {
         settings = new { number_of_shards = 1, number_of_replicas = 0 },
         mappings = new
         {
            _source = new { excludes = new[] { "vector" } },
            dynamic_templates = new[]
            {
               new { meta_values = new { match = "meta_*", mapping = new { type = "keyword", ignore_above = 1024 } } },
            },
            properties = new Dictionary<string, object>
            {
               ["vector"] = new
               {
                  type = "dense_vector",
                  dims = dimension,
                  index = true,
                  similarity = "cosine",
                  index_options = new { type = "hnsw", m = HNSW_M, ef_construction = HNSW_EF_CONSTRUCTION },
               },
               ["doc_key"] = new { type = "keyword", ignore_above = 1024 },
               ["table"] = new { type = "keyword", ignore_above = 1024 },
               ["origin"] = new { type = "keyword", ignore_above = 4096 },
               ["ordinal"] = new { type = "integer" },
               ["text"] = new { type = "text", index = false },
            },
         },
      };
   }

   /// <summary>
   /// Checks an existing index holds vectors of the expected length.
   /// </summary>
   /// <param name="index">Index name.</param>
   /// <param name="dimension">Expected dimension.</param>
   /// <param name="ct">Cancellation.</param>
   /// <returns>True when the index exists with that dimension; false when it does not exist.</returns>
   /// <exception cref="InvalidOperationException">The index exists with a different dimension.</exception>
   private async Task<bool> VerifyDimensionAsync( string index, int dimension, CancellationToken ct )
   {
      using JsonDocument? mapping = await _rest.SendAsync( HttpMethod.Get, $"{index}/_mapping", null, "application/json", true, ct );
      if( mapping == null )
      {
         return false;
      }

      JsonElement vector = mapping.RootElement.GetProperty( index ).GetProperty( "mappings" ).GetProperty( "properties" ).GetProperty( "vector" );
      int existing = vector.GetProperty( "dims" ).GetInt32();
      if( existing != dimension )
      {
         throw new InvalidOperationException( $"Elasticsearch index {index} holds {existing}-dim vectors but the embedder produces {dimension}. Reset the pipeline or use a new name." );
      }

      return true;
   }

   /// <summary>
   /// Writes one bulk body and fails with the first item error if any item was rejected.
   /// Why the answer is filtered: a bulk answer repeats every document id, which for a batch of
   /// 500 is pure waste; the filter keeps only the error flag and failed items.
   /// </summary>
   /// <param name="index">Index name.</param>
   /// <param name="body">Newline-delimited bulk body.</param>
   /// <param name="ct">Cancellation.</param>
   private async Task BulkAsync( string index, byte[] body, CancellationToken ct )
   {
      string path = $"{index}/_bulk?filter_path=errors,items.*.error,items.*.status";
      using JsonDocument? answer = await _rest.SendAsync( HttpMethod.Post, path, body, NDJSON, false, ct );
      if( answer == null || !answer.RootElement.GetProperty( "errors" ).GetBoolean() )
      {
         return;
      }

      foreach( JsonElement item in answer.RootElement.GetProperty( "items" ).EnumerateArray() )
      {
         JsonElement result = item.EnumerateObject().First().Value;
         if( result.TryGetProperty( "error", out JsonElement error ) )
         {
            throw new InvalidOperationException( $"Elasticsearch rejected a document in index {index}: {error.GetRawText()}" );
         }
      }
   }

   /// <summary>
   /// Builds the bulk body that indexes each record under its chunk id.
   /// </summary>
   /// <param name="batch">Records to write.</param>
   /// <returns>Newline-delimited JSON bytes.</returns>
   private static byte[] WriteIndexActions( VectorRecord[] batch )
   {
      var buffer = new ArrayBufferWriter<byte>( 1 << 20 );
      using var writer = new Utf8JsonWriter( buffer );
      foreach( VectorRecord record in batch )
      {
         WriteLine( writer, buffer, w =>
         {
            w.WriteStartObject();
            w.WriteStartObject( "index" );
            w.WriteString( "_id", record.Chunk.ChunkId.ToString( "D" ) );
            w.WriteEndObject();
            w.WriteEndObject();
         } );
         WriteLine( writer, buffer, w => WriteSource( w, record ) );
      }

      return buffer.WrittenSpan.ToArray();
   }

   /// <summary>
   /// Builds the bulk body that deletes each chunk id.
   /// </summary>
   /// <param name="batch">Chunk ids to remove.</param>
   /// <returns>Newline-delimited JSON bytes.</returns>
   private static byte[] WriteDeleteActions( Guid[] batch )
   {
      var buffer = new ArrayBufferWriter<byte>( 1 << 16 );
      using var writer = new Utf8JsonWriter( buffer );
      foreach( Guid id in batch )
      {
         WriteLine( writer, buffer, w =>
         {
            w.WriteStartObject();
            w.WriteStartObject( "delete" );
            w.WriteString( "_id", id.ToString( "D" ) );
            w.WriteEndObject();
            w.WriteEndObject();
         } );
      }

      return buffer.WrittenSpan.ToArray();
   }

   /// <summary>
   /// Writes one JSON value as one line of a newline-delimited body.
   /// </summary>
   /// <param name="writer">Reusable JSON writer.</param>
   /// <param name="buffer">Output buffer the writer writes into.</param>
   /// <param name="write">Writes the value.</param>
   private static void WriteLine( Utf8JsonWriter writer, ArrayBufferWriter<byte> buffer, Action<Utf8JsonWriter> write )
   {
      writer.Reset();
      write( writer );
      writer.Flush();
      buffer.Write( "\n"u8 );
   }

   /// <summary>
   /// Writes a record's vector and payload as the document source.
   /// </summary>
   /// <param name="w">JSON writer.</param>
   /// <param name="record">The record.</param>
   private static void WriteSource( Utf8JsonWriter w, VectorRecord record )
   {
      w.WriteStartObject();
      w.WriteStartArray( "vector" );
      foreach( float x in record.Vector )
      {
         w.WriteNumberValue( x );
      }

      w.WriteEndArray();
      w.WriteString( "doc_key", record.Document.DocKey );
      w.WriteString( "table", record.Document.Table );
      w.WriteString( "origin", record.Document.Origin );
      w.WriteNumber( "ordinal", record.Chunk.Ordinal );
      w.WriteString( "text", record.Chunk.Text );
      foreach( KeyValuePair<string, string> pair in record.Document.Metadata )
      {
         w.WriteString( $"meta_{pair.Key}", pair.Value );
      }

      w.WriteEndObject();
   }

   /// <summary>
   /// Converts a search answer into hits, turning Elasticsearch's score into cosine similarity.
   /// </summary>
   /// <param name="answer">Parsed search answer.</param>
   /// <param name="toCosine">Converts an Elasticsearch score into cosine similarity.</param>
   /// <returns>Hits in the order Elasticsearch returned them, best first.</returns>
   private static IReadOnlyList<SearchHit> ReadHits( JsonDocument answer, Func<double, double> toCosine )
   {
      var hits = new List<SearchHit>();
      foreach( JsonElement hit in answer.RootElement.GetProperty( "hits" ).GetProperty( "hits" ).EnumerateArray() )
      {
         JsonElement source = hit.GetProperty( "_source" );
         hits.Add( new SearchHit( Guid.Parse( hit.GetProperty( "_id" ).GetString()! ), Text( source, "doc_key" ), Text( source, "table" ), Text( source, "text" ),
            toCosine( hit.GetProperty( "_score" ).GetDouble() ) ) );
      }

      return hits;
   }

   /// <summary>
   /// Reads a string field, or empty when it is missing.
   /// </summary>
   /// <param name="source">The hit's _source object.</param>
   /// <param name="key">Field name.</param>
   /// <returns>The value.</returns>
   private static string Text( JsonElement source, string key )
   {
      return source.TryGetProperty( key, out JsonElement value ) ? value.GetString() ?? string.Empty : string.Empty;
   }

   #endregion Private Methods

   #region IDisposable

   /// <summary>
   /// Disposes the HTTP client.
   /// </summary>
   public void Dispose()
   {
      _rest.Dispose();
   }

   #endregion IDisposable
}
