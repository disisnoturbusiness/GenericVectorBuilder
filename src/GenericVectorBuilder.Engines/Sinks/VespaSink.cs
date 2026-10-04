using System.Text.Json;
using GenericVectorBuilder.Core.Contracts;
using GenericVectorBuilder.Engines.Common;

namespace GenericVectorBuilder.Engines.Sinks;

/// <summary>
/// Writes vectors to Vespa 8: one schema (document type) per pipeline, named gvb_{pipeline}, with a
/// tensor&lt;float&gt;(x[n]) attribute carrying an HNSW index (see <see cref="VespaApplicationPackage.Schema"/>)
/// and the chunk text, document key, table, origin and ordinal as summary fields. The chunk id is the
/// document id, so a repeated write replaces the old document.
/// How a collection is created: Vespa only learns about document types through a deployed application
/// package. EnsureCollectionAsync reads the schemas the config server holds, adds one, deploys the
/// package and waits until the container knows the new type (a few seconds). Dropping does the reverse;
/// Vespa deletes the schema's data, which the package must explicitly allow. Deployments in this
/// process are serialized, so two collections created at once do not overwrite each other.
/// Search: nearestNeighbor with targetHits = top and hnsw.exploreAdditionalHits chosen so the graph
/// walk keeps 100 candidates (see <see cref="VespaSinkOptions.EfSearch"/>). The rank profile returns
/// cosine similarity directly as 1 - distance.
/// Exact search (<see cref="IExactSearchSink"/>): the same query with approximate:false, which scans
/// every vector.
/// Why the sink checks vector length: prenormalized-angular assumes unit-length vectors (the
/// pipeline's embedders return them) and ranks wrongly for anything else, so a vector that is not unit
/// length is refused instead of silently corrupting the ranking.
/// Why metadata is one JSON string field: schema fields are fixed at deploy time, so arbitrary meta_*
/// keys cannot become fields of their own.
/// </summary>
public sealed class VespaSink : ISink, IExactSearchSink, IEngineDescription, IDisposable
{
   #region Data Members

   private const int HNSW_M = 16;
   private const int HNSW_EF_CONSTRUCTION = 128;
   private const double UNIT_TOLERANCE = 1e-3;
   private const string SUMMARY_FIELDS = "documentid,doc_key,table,text";
   private static readonly SemaphoreSlim DEPLOY_LOCK = new( 1, 1 );

   private readonly VespaSinkOptions _options;
   private readonly VespaRest _rest;

   #endregion Data Members

   #region Constructor

   /// <summary>
   /// Creates the sink against the local Docker container.
   /// </summary>
   public VespaSink()
      : this( new VespaSinkOptions() )
   {
   }

   /// <summary>
   /// Creates the sink with explicit settings.
   /// </summary>
   /// <param name="options">Addresses and query settings.</param>
   public VespaSink( VespaSinkOptions options )
   {
      _options = options;
      _rest = new VespaRest( options );
   }

   #endregion Constructor

   #region Public Methods

   /// <inheritdoc />
   public string Name => "vespa";

   /// <inheritdoc />
   public string Engine => "Vespa 8.754.14 (tensor attribute + HNSW)";

   /// <inheritdoc />
   public string IndexDescription =>
      $"HNSW float32 tensor, prenormalized-angular (cosine), max-links-per-node={HNSW_M}, neighbors-to-explore-at-insert={HNSW_EF_CONSTRUCTION}; search targetHits=top, ef={_options.EfSearch} via exploreAdditionalHits; exact mode = approximate:false; vectors held in memory";

   /// <inheritdoc />
   public string ComposeFile => "vespa.compose.yaml";

   /// <inheritdoc />
   public async Task<bool> EnsureCollectionAsync( string collection, int dimension, CancellationToken ct )
   {
      string type = TypeName( collection );
      Dictionary<string, string> schemas = await _rest.ReadSchemasAsync( ct );
      if( !schemas.ContainsKey( type ) )
      {
         await DEPLOY_LOCK.WaitAsync( ct );
         try
         {
            schemas = await _rest.ReadSchemasAsync( ct );
            if( !schemas.ContainsKey( type ) )
            {
               schemas[type] = VespaApplicationPackage.Schema( type, dimension );
               await _rest.DeployAsync( VespaApplicationPackage.Build( schemas, false ), ct );
               await _rest.WaitForTypeAsync( type, dimension, ct );
               return true;
            }
         }
         finally
         {
            DEPLOY_LOCK.Release();
         }
      }

      VerifyDimension( type, schemas[type], dimension );
      await _rest.WaitForTypeAsync( type, dimension, ct );
      return false;
   }

   /// <inheritdoc />
   public async Task UpsertAsync( string collection, IReadOnlyList<VectorRecord> records, CancellationToken ct )
   {
      string type = TypeName( collection );
      await FeedAsync( records, record => _rest.PutAsync( type, record.Chunk.ChunkId.ToString( "D" ), WriteDocument( record ), ct ), ct );
   }

   /// <inheritdoc />
   public async Task DeleteAsync( string collection, IReadOnlyList<Guid> chunkIds, CancellationToken ct )
   {
      string type = TypeName( collection );
      await FeedAsync( chunkIds, id => _rest.RemoveAsync( type, id.ToString( "D" ), ct ), ct );
   }

   /// <inheritdoc />
   public async Task<long> CountAsync( string collection, CancellationToken ct )
   {
      var query = new Dictionary<string, object>
      {
         ["yql"] = $"select * from {TypeName( collection )} where true",
         ["hits"] = 0,
         ["ranking"] = "unranked",
         ["timeout"] = "60s",
      };

      using JsonDocument answer = await _rest.QueryAsync( query, ct );
      return answer.RootElement.GetProperty( "root" ).GetProperty( "fields" ).GetProperty( "totalCount" ).GetInt64();
   }

   /// <inheritdoc />
   public Task<IReadOnlyList<SearchHit>> SearchAsync( string collection, float[] vector, int top, CancellationToken ct )
   {
      int extra = Math.Max( 0, _options.EfSearch - top );
      return QueryAsync( collection, vector, top, $"targetHits:{top},hnsw.exploreAdditionalHits:{extra}", ct );
   }

   /// <inheritdoc />
   public Task<IReadOnlyList<SearchHit>> SearchExactAsync( string collection, float[] vector, int top, CancellationToken ct )
   {
      return QueryAsync( collection, vector, top, $"targetHits:{top},approximate:false", ct );
   }

   /// <inheritdoc />
   public async Task DropCollectionAsync( string collection, CancellationToken ct )
   {
      string type = TypeName( collection );
      if( !( await _rest.ReadSchemasAsync( ct ) ).ContainsKey( type ) )
      {
         return;
      }

      await DEPLOY_LOCK.WaitAsync( ct );
      try
      {
         Dictionary<string, string> schemas = await _rest.ReadSchemasAsync( ct );
         if( schemas.Remove( type ) )
         {
            await _rest.DeployAsync( VespaApplicationPackage.Build( schemas, true ), ct );
         }
      }
      finally
      {
         DEPLOY_LOCK.Release();
      }
   }

   #endregion Public Methods

   #region Private Methods

   /// <summary>
   /// Document type (schema) name for a pipeline. Pipeline names are already sanitized to [a-z0-9_].
   /// </summary>
   /// <param name="collection">Pipeline collection name.</param>
   /// <returns>The Vespa document type name.</returns>
   private static string TypeName( string collection )
   {
      return $"gvb_{collection}";
   }

   /// <summary>
   /// Checks a deployed schema holds vectors of the expected length.
   /// </summary>
   /// <param name="type">Document type name.</param>
   /// <param name="schemaText">The deployed .sd text.</param>
   /// <param name="dimension">Expected dimension.</param>
   /// <exception cref="InvalidOperationException">The schema has a different dimension.</exception>
   private static void VerifyDimension( string type, string schemaText, int dimension )
   {
      int? existing = VespaApplicationPackage.Dimension( schemaText );
      if( existing != dimension )
      {
         throw new InvalidOperationException( $"Vespa schema {type} holds {existing}-dim vectors but the embedder produces {dimension}. Reset the pipeline or use a new name." );
      }
   }

   /// <summary>
   /// Runs one write per item with up to FeedConcurrency in flight, and stops at the first failure.
   /// Why concurrent: Vespa takes one document per request, so a loop that waits for each answer would
   /// spend its time on round trips rather than on Vespa.
   /// </summary>
   /// <typeparam name="T">Item type.</typeparam>
   /// <param name="items">What to write.</param>
   /// <param name="write">Writes one item.</param>
   /// <param name="ct">Cancellation.</param>
   private async Task FeedAsync<T>( IReadOnlyList<T> items, Func<T, Task> write, CancellationToken ct )
   {
      using var gate = new SemaphoreSlim( _options.FeedConcurrency );
      using var failure = CancellationTokenSource.CreateLinkedTokenSource( ct );
      var running = new List<Task>( items.Count );
      foreach( T item in items )
      {
         try
         {
            await gate.WaitAsync( failure.Token );
         }
         catch( OperationCanceledException ) when( failure.IsCancellationRequested && !ct.IsCancellationRequested )
         {
            break;
         }

         running.Add( WriteOneAsync( item, write, gate, failure ) );
      }

      await Task.WhenAll( running );
   }

   /// <summary>
   /// Runs one write, frees its slot, and cancels the rest of the batch if it fails.
   /// </summary>
   /// <typeparam name="T">Item type.</typeparam>
   /// <param name="item">The item.</param>
   /// <param name="write">Writes one item.</param>
   /// <param name="gate">Concurrency limit to release.</param>
   /// <param name="failure">Cancelled on failure.</param>
   private static async Task WriteOneAsync<T>( T item, Func<T, Task> write, SemaphoreSlim gate, CancellationTokenSource failure )
   {
      try
      {
         await write( item );
      }
      catch
      {
         failure.Cancel();
         throw;
      }
      finally
      {
         gate.Release();
      }
   }

   /// <summary>
   /// Runs a nearestNeighbor query and converts the answer into hits.
   /// </summary>
   /// <param name="collection">Pipeline collection name.</param>
   /// <param name="vector">Query vector.</param>
   /// <param name="top">How many hits to return.</param>
   /// <param name="annotation">The nearestNeighbor annotation, e.g. "targetHits:10,approximate:false".</param>
   /// <param name="ct">Cancellation.</param>
   /// <returns>Hits, best first, with cosine similarity scores.</returns>
   private async Task<IReadOnlyList<SearchHit>> QueryAsync( string collection, float[] vector, int top, string annotation, CancellationToken ct )
   {
      string input = VespaApplicationPackage.QueryInput( vector.Length );
      var query = new Dictionary<string, object>
      {
         ["yql"] = $"select {SUMMARY_FIELDS} from {TypeName( collection )} where {{{annotation}}}nearestNeighbor(embedding,{input})",
         [$"input.query({input})"] = vector,
         ["hits"] = top,
         ["ranking"] = "default",
         ["timeout"] = "60s",
      };

      using JsonDocument answer = await _rest.QueryAsync( query, ct );
      var hits = new List<SearchHit>( top );
      if( answer.RootElement.GetProperty( "root" ).TryGetProperty( "children", out JsonElement children ) )
      {
         foreach( JsonElement hit in children.EnumerateArray() )
         {
            JsonElement fields = hit.GetProperty( "fields" );
            string id = fields.GetProperty( "documentid" ).GetString()!;
            hits.Add( new SearchHit( Guid.Parse( id[( id.LastIndexOf( "::", StringComparison.Ordinal ) + 2 )..] ), Text( fields, "doc_key" ), Text( fields, "table" ),
               Text( fields, "text" ), hit.GetProperty( "relevance" ).GetDouble() ) );
         }
      }

      return hits;
   }

   /// <summary>
   /// Reads a string field, or empty when it is missing.
   /// </summary>
   /// <param name="fields">The hit's fields object.</param>
   /// <param name="key">Field name.</param>
   /// <returns>The value.</returns>
   private static string Text( JsonElement fields, string key )
   {
      return fields.TryGetProperty( key, out JsonElement value ) ? value.GetString() ?? string.Empty : string.Empty;
   }

   /// <summary>
   /// Builds the {"fields": ...} body of one document, after checking the vector is unit length.
   /// </summary>
   /// <param name="record">The record.</param>
   /// <returns>JSON bytes.</returns>
   /// <exception cref="InvalidOperationException">The vector is not unit length.</exception>
   private static byte[] WriteDocument( VectorRecord record )
   {
      double norm = Math.Sqrt( record.Vector.Sum( x => (double)x * x ) );
      if( Math.Abs( norm - 1.0 ) > UNIT_TOLERANCE )
      {
         throw new InvalidOperationException( $"Vespa is set to prenormalized-angular, which needs unit-length vectors, but chunk {record.Chunk.ChunkId} has length {norm:F4}." );
      }

      using var stream = new MemoryStream( 16 * 1024 );
      using( var w = new Utf8JsonWriter( stream ) )
      {
         w.WriteStartObject();
         w.WriteStartObject( "fields" );
         w.WriteString( "doc_key", record.Document.DocKey );
         w.WriteString( "table", record.Document.Table );
         w.WriteString( "origin", record.Document.Origin );
         w.WriteNumber( "ordinal", record.Chunk.Ordinal );
         w.WriteString( "text", record.Chunk.Text );
         w.WriteString( "meta", JsonSerializer.Serialize( record.Document.Metadata ) );
         w.WriteStartObject( "embedding" );
         w.WriteStartArray( "values" );
         foreach( float x in record.Vector )
         {
            w.WriteNumberValue( x );
         }

         w.WriteEndArray();
         w.WriteEndObject();
         w.WriteEndObject();
         w.WriteEndObject();
      }

      return stream.ToArray();
   }

   #endregion Private Methods

   #region IDisposable

   /// <summary>
   /// Disposes the HTTP clients.
   /// </summary>
   public void Dispose()
   {
      _rest.Dispose();
   }

   #endregion IDisposable
}
