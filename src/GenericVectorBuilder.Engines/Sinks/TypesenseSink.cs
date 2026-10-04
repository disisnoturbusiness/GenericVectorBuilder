using System.Buffers;
using System.Globalization;
using System.Text;
using System.Text.Json;
using GenericVectorBuilder.Core.Contracts;
using GenericVectorBuilder.Engines.Common;

namespace GenericVectorBuilder.Engines.Sinks;

/// <summary>
/// Writes vectors to Typesense 30 over its REST API: one collection per pipeline, named gvb_{pipeline},
/// with a float[] field "vec" (cosine, HNSW m = 16, ef_construction = 128) and the chunk text, document
/// key, table, origin and meta_* values stored beside it. The chunk id is the Typesense document id,
/// so an import with action=upsert replaces an earlier version.
/// What is in the schema: only "vec" and "ordinal". Typesense stores fields that are not in the
/// schema without indexing them, which is what payload needs; "ordinal" is indexed as a number because
/// exact search needs a filter that matches every document (see below).
/// Search: the vector_query "vec:([...], k:top, ef:100)" sent as a POST to /multi_search, because 1024
/// numbers do not fit in a GET query string. Typesense reports cosine distance (1 - cosine); the
/// sink returns the similarity 1 - distance like every other engine.
/// Exact search (<see cref="IExactSearchSink"/>): Typesense switches from the graph to a brute-force
/// scan when a filter matches fewer documents than flat_search_cutoff. The sink filters on
/// ordinal:>=0 (every document) with a cutoff above any collection size, so every vector is compared.
/// A cutoff without a filter does nothing: measured, recall stayed at the graph's level.
/// Why the vector field keeps store=true: Typesense rebuilds its in-memory HNSW graph from the stored
/// documents on every start, so the stored vector is the only copy it can rebuild from.
/// </summary>
public sealed class TypesenseSink : ISink, IExactSearchSink, IEngineDescription, IDisposable
{
   #region Data Members

   private const int UPSERT_BATCH = 500;
   private const int DELETE_BATCH = 100;
   private const int MAX_PER_PAGE = 250;
   private const int HNSW_M = 16;
   private const int HNSW_EF_CONSTRUCTION = 128;
   private const int FLAT_SEARCH_CUTOFF = 2_000_000_000;
   private const string INCLUDE_FIELDS = "id,doc_key,table,text";

   private readonly TypesenseSinkOptions _options;
   private readonly TypesenseRest _rest;

   #endregion Data Members

   #region Constructor

   /// <summary>
   /// Creates the sink against the local Docker container, reading the API key lazily from the
   /// shared secrets file.
   /// </summary>
   public TypesenseSink()
      : this( new TypesenseSinkOptions() )
   {
   }

   /// <summary>
   /// Creates the sink with explicit settings.
   /// </summary>
   /// <param name="options">Address, key source and query settings.</param>
   public TypesenseSink( TypesenseSinkOptions options )
   {
      _options = options;
      _rest = new TypesenseRest( options );
   }

   #endregion Constructor

   #region Public Methods

   /// <inheritdoc />
   public string Name => "typesense";

   /// <inheritdoc />
   public string Engine => "Typesense 30.2 (vector search)";

   /// <inheritdoc />
   public string IndexDescription =>
      $"HNSW float32 (hnswlib), m={HNSW_M}, ef_construction={HNSW_EF_CONSTRUCTION}, cosine; search k=top, ef={_options.Ef}; exact mode = filter ordinal:>=0 with flat_search_cutoff; index held in memory";

   /// <inheritdoc />
   public string ComposeFile => "typesense.compose.yaml";

   /// <inheritdoc />
   public async Task<bool> EnsureCollectionAsync( string collection, int dimension, CancellationToken ct )
   {
      string name = CollectionName( collection );
      if( await VerifyDimensionAsync( name, dimension, ct ) )
      {
         return false;
      }

      try
      {
         using JsonDocument? created = await _rest.SendJsonAsync( HttpMethod.Post, "collections", Schema( name, dimension ), false, ct );
         return true;
      }
      catch( TypesenseRestException ex ) when( ex.Status == 409 )
      {
         await VerifyDimensionAsync( name, dimension, ct );
         return false;
      }
   }

   /// <inheritdoc />
   public async Task UpsertAsync( string collection, IReadOnlyList<VectorRecord> records, CancellationToken ct )
   {
      string path = $"collections/{CollectionName( collection )}/documents/import?action=upsert&batch_size={UPSERT_BATCH}";
      foreach( VectorRecord[] batch in records.Chunk( UPSERT_BATCH ) )
      {
         byte[]? answer = await _rest.SendAsync( HttpMethod.Post, path, WriteDocuments( batch ), "text/plain", false, ct );
         ThrowOnFailedLines( answer, collection );
      }
   }

   /// <inheritdoc />
   public async Task DeleteAsync( string collection, IReadOnlyList<Guid> chunkIds, CancellationToken ct )
   {
      foreach( Guid[] batch in chunkIds.Chunk( DELETE_BATCH ) )
      {
         string filter = "id:[" + string.Join( ",", batch.Select( id => $"`{id:D}`" ) ) + "]";
         string path = $"collections/{CollectionName( collection )}/documents?batch_size={DELETE_BATCH}&filter_by={Uri.EscapeDataString( filter )}";
         await _rest.SendAsync( HttpMethod.Delete, path, null, "application/json", false, ct );
      }
   }

   /// <inheritdoc />
   public async Task<long> CountAsync( string collection, CancellationToken ct )
   {
      using JsonDocument? info = await _rest.SendJsonAsync( HttpMethod.Get, $"collections/{CollectionName( collection )}", null, false, ct );
      return info!.RootElement.GetProperty( "num_documents" ).GetInt64();
   }

   /// <inheritdoc />
   public Task<IReadOnlyList<SearchHit>> SearchAsync( string collection, float[] vector, int top, CancellationToken ct )
   {
      string options = $"k:{top}, ef:{Math.Max( _options.Ef, top )}";
      return QueryAsync( collection, vector, top, options, null, ct );
   }

   /// <inheritdoc />
   public Task<IReadOnlyList<SearchHit>> SearchExactAsync( string collection, float[] vector, int top, CancellationToken ct )
   {
      string options = $"k:{top}, flat_search_cutoff:{FLAT_SEARCH_CUTOFF}";
      return QueryAsync( collection, vector, top, options, "ordinal:>=0", ct );
   }

   /// <inheritdoc />
   public async Task DropCollectionAsync( string collection, CancellationToken ct )
   {
      await _rest.SendAsync( HttpMethod.Delete, $"collections/{CollectionName( collection )}", null, "application/json", true, ct );
   }

   #endregion Public Methods

   #region Private Methods

   /// <summary>
   /// Collection name for a pipeline. Pipeline names are already sanitized to [a-z0-9_].
   /// </summary>
   /// <param name="collection">Pipeline collection name.</param>
   /// <returns>The Typesense collection name.</returns>
   private static string CollectionName( string collection )
   {
      return $"gvb_{collection}";
   }

   /// <summary>
   /// Builds the collection schema for a vector of the given length.
   /// </summary>
   /// <param name="name">Collection name.</param>
   /// <param name="dimension">Embedding dimension.</param>
   /// <returns>The request body.</returns>
   private static object Schema( string name, int dimension )
   {
      return new
      {
         name,
         fields = new object[]
         {
            new { name = "vec", type = "float[]", num_dim = dimension, vec_dist = "cosine", hnsw_params = new { M = HNSW_M, ef_construction = HNSW_EF_CONSTRUCTION } },
            new { name = "ordinal", type = "int32" },
         },
      };
   }

   /// <summary>
   /// Checks an existing collection holds vectors of the expected length.
   /// </summary>
   /// <param name="name">Collection name.</param>
   /// <param name="dimension">Expected dimension.</param>
   /// <param name="ct">Cancellation.</param>
   /// <returns>True when the collection exists with that dimension; false when it does not exist.</returns>
   /// <exception cref="InvalidOperationException">The collection exists with a different dimension.</exception>
   private async Task<bool> VerifyDimensionAsync( string name, int dimension, CancellationToken ct )
   {
      using JsonDocument? info = await _rest.SendJsonAsync( HttpMethod.Get, $"collections/{name}", null, true, ct );
      if( info == null )
      {
         return false;
      }

      int existing = info.RootElement.GetProperty( "fields" ).EnumerateArray()
         .Where( f => f.GetProperty( "name" ).GetString() == "vec" ).Select( f => f.GetProperty( "num_dim" ).GetInt32() ).FirstOrDefault();
      if( existing != dimension )
      {
         throw new InvalidOperationException( $"Typesense collection {name} holds {existing}-dim vectors but the embedder produces {dimension}. Reset the pipeline or use a new name." );
      }

      return true;
   }

   /// <summary>
   /// Runs one vector query through /multi_search and pages through the hits when more than 250
   /// are wanted (Typesense's page size limit).
   /// </summary>
   /// <param name="collection">Pipeline collection name.</param>
   /// <param name="vector">Query vector.</param>
   /// <param name="top">How many hits to return.</param>
   /// <param name="options">The vector_query options after the vector, e.g. "k:10, ef:100".</param>
   /// <param name="filter">filter_by expression, or null.</param>
   /// <param name="ct">Cancellation.</param>
   /// <returns>Hits, best first, with cosine similarity scores.</returns>
   private async Task<IReadOnlyList<SearchHit>> QueryAsync( string collection, float[] vector, int top, string options, string? filter, CancellationToken ct )
   {
      string vectorQuery = $"vec:([{string.Join( ",", vector.Select( x => x.ToString( "R", CultureInfo.InvariantCulture ) ) )}], {options})";
      var hits = new List<SearchHit>( top );
      for( int page = 1; hits.Count < top; page++ )
      {
         var search = new Dictionary<string, object>
         {
            ["collection"] = CollectionName( collection ),
            ["q"] = "*",
            ["vector_query"] = vectorQuery,
            ["per_page"] = Math.Min( MAX_PER_PAGE, top ),
            ["page"] = page,
            ["include_fields"] = INCLUDE_FIELDS,
         };
         if( filter != null )
         {
            search["filter_by"] = filter;
         }

         using JsonDocument? answer = await _rest.SendJsonAsync( HttpMethod.Post, "multi_search", new { searches = new[] { search } }, false, ct );
         int before = hits.Count;
         ReadHits( answer!, hits, top );
         if( hits.Count == before )
         {
            break;
         }
      }

      return hits;
   }

   /// <summary>
   /// Appends the hits of one /multi_search answer, turning cosine distance into similarity.
   /// </summary>
   /// <param name="answer">Parsed answer.</param>
   /// <param name="hits">List to append to.</param>
   /// <param name="top">Stop once the list holds this many.</param>
   /// <exception cref="InvalidOperationException">Typesense reported an error for the search.</exception>
   private static void ReadHits( JsonDocument answer, List<SearchHit> hits, int top )
   {
      JsonElement result = answer.RootElement.GetProperty( "results" )[0];
      if( result.TryGetProperty( "error", out JsonElement error ) )
      {
         throw new InvalidOperationException( $"Typesense search failed: {error.GetString()}" );
      }

      foreach( JsonElement hit in result.GetProperty( "hits" ).EnumerateArray() )
      {
         if( hits.Count >= top )
         {
            return;
         }

         JsonElement doc = hit.GetProperty( "document" );
         hits.Add( new SearchHit( Guid.Parse( doc.GetProperty( "id" ).GetString()! ), Text( doc, "doc_key" ), Text( doc, "table" ), Text( doc, "text" ),
            1.0 - hit.GetProperty( "vector_distance" ).GetDouble() ) );
      }
   }

   /// <summary>
   /// Reads a string field, or empty when it is missing.
   /// </summary>
   /// <param name="doc">The hit's document object.</param>
   /// <param name="key">Field name.</param>
   /// <returns>The value.</returns>
   private static string Text( JsonElement doc, string key )
   {
      return doc.TryGetProperty( key, out JsonElement value ) ? value.GetString() ?? string.Empty : string.Empty;
   }

   /// <summary>
   /// Fails on the first line of an import answer that did not succeed. An import answers 200 even
   /// when some documents were rejected, one JSON line per document.
   /// </summary>
   /// <param name="answer">Raw import answer.</param>
   /// <param name="collection">Collection name for the message.</param>
   /// <exception cref="InvalidOperationException">At least one document was rejected.</exception>
   private static void ThrowOnFailedLines( byte[]? answer, string collection )
   {
      if( answer == null )
      {
         return;
      }

      foreach( string line in Encoding.UTF8.GetString( answer ).Split( '\n', StringSplitOptions.RemoveEmptyEntries ) )
      {
         if( line.Contains( "\"success\":false", StringComparison.Ordinal ) )
         {
            int cut = Math.Min( line.Length, 300 );
            throw new InvalidOperationException( $"Typesense rejected a document in collection {CollectionName( collection )}: {line[..cut]}" );
         }
      }
   }

   /// <summary>
   /// Builds the newline-delimited import body, one JSON document per record.
   /// </summary>
   /// <param name="batch">Records to write.</param>
   /// <returns>JSON lines as bytes.</returns>
   private static byte[] WriteDocuments( VectorRecord[] batch )
   {
      var buffer = new ArrayBufferWriter<byte>( 1 << 20 );
      using var writer = new Utf8JsonWriter( buffer );
      foreach( VectorRecord record in batch )
      {
         writer.Reset();
         WriteDocument( writer, record );
         writer.Flush();
         buffer.Write( "\n"u8 );
      }

      return buffer.WrittenSpan.ToArray();
   }

   /// <summary>
   /// Writes one record as a Typesense document.
   /// </summary>
   /// <param name="w">JSON writer.</param>
   /// <param name="record">The record.</param>
   private static void WriteDocument( Utf8JsonWriter w, VectorRecord record )
   {
      w.WriteStartObject();
      w.WriteString( "id", record.Chunk.ChunkId.ToString( "D" ) );
      w.WriteStartArray( "vec" );
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
