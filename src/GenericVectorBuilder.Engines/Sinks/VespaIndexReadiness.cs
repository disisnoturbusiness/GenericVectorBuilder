using System.Diagnostics;
using System.Text.Json;
using GenericVectorBuilder.Engines.Common;

namespace GenericVectorBuilder.Engines.Sinks;

/// <summary>
/// Checks, and waits for, the HNSW index of one Vespa document type (used by <see cref="VespaSink"/>).
/// How Vespa builds the index: the content node inserts each vector into the attribute's HNSW graph as it
/// applies the write, and the feed answer comes after that (the live proton config has
/// documentdb.visibilitydelay 0: a document is visible in the index as soon as it is received). There is no
/// separate build step, so the finish step only confirms the state.
/// What proves readiness (all read from the engine):
///   1. A query "where true" with ranking unranked: totalCount is the number of searchable documents, and
///      coverage.full says every content node answered.
///   2. A nearestNeighbor query with approximate:true, targetHits above the document count and hits 0, with
///      the query trace on (trace.level 1, trace.explainLevel 1). The trace holds the content node's own
///      description of the search, a NearestNeighborBlueprint with has_index true, algorithm "index top k"
///      (the HNSW search; a scan says "exact") and top_k_hits, the number of vectors the graph returned.
///      top_k_hits equal to the document count means the graph holds every stored vector.
/// This is the "EXPLAIN plan" of Vespa: the engine says which algorithm it used and what it found.
/// Cost: one graph walk over every vector, a few milliseconds per thousand vectors.
/// </summary>
internal sealed class VespaIndexReadiness
{
   #region Data Members

   private const string BLUEPRINT = "search::queryeval::NearestNeighborBlueprint";
   private const string HNSW_ALGORITHM = "index top k";
   private static readonly TimeSpan POLL_FIRST = TimeSpan.FromMilliseconds( 100 );
   private static readonly TimeSpan POLL_MAX = TimeSpan.FromSeconds( 1 );
   private static readonly TimeSpan PROGRESS_EVERY = TimeSpan.FromSeconds( 10 );
   private static readonly TimeSpan STALL_AFTER = TimeSpan.FromSeconds( 30 );

   private readonly VespaRest _rest;
   private readonly VespaSinkOptions _options;

   #endregion Data Members

   #region Constructor

   /// <summary>
   /// Creates the helper over the sink's HTTP clients.
   /// </summary>
   /// <param name="rest">The sink's Vespa client.</param>
   /// <param name="options">Sink settings (index step time limit and progress output).</param>
   public VespaIndexReadiness( VespaRest rest, VespaSinkOptions options )
   {
      _rest = rest;
      _options = options;
   }

   #endregion Constructor

   #region Public Methods

   /// <summary>
   /// Waits until every fed document is searchable and the HNSW index returns all of them.
   /// </summary>
   /// <param name="documentType">Vespa document type name.</param>
   /// <param name="ct">Cancellation.</param>
   /// <returns>What was found and how long it took.</returns>
   /// <exception cref="InvalidOperationException">The type is missing, has no HNSW index, the index stalled
   /// short of the documents, or the finish time limit passed.</exception>
   public async Task<string> FinishAsync( string documentType, CancellationToken ct )
   {
      var clock = Stopwatch.StartNew();
      TimeSpan limit = _options.EffectiveFinishTimeout;
      string last = "no state read yet";
      using var deadline = CancellationTokenSource.CreateLinkedTokenSource( ct );
      deadline.CancelAfter( limit );
      try
      {
         IndexState state = await WaitForIndexAsync( documentType, deadline.Token, seen => last = seen );
         return $"nothing to build, Vespa inserts into the HNSW graph while writing; ready after {clock.Elapsed.TotalSeconds:F1} s: {state.Detail}";
      }
      catch( OperationCanceledException ) when( !ct.IsCancellationRequested )
      {
         throw new InvalidOperationException( $"Vespa document type {documentType} was not ready within {limit.TotalSeconds:0.###} s (the engine did not answer in time). Last state: {last}" );
      }
   }

   /// <summary>
   /// Reads the index state: searchable documents, and what the engine says its nearest-neighbour search
   /// does. Nothing is written; both calls are searches.
   /// </summary>
   /// <param name="documentType">Vespa document type name.</param>
   /// <param name="ct">Cancellation.</param>
   /// <returns>The state, with the evidence in its detail.</returns>
   public async Task<IndexState> ReadStateAsync( string documentType, CancellationToken ct )
   {
      IndexFacts? facts = await ReadFactsAsync( documentType, ct );
      return facts == null ? new IndexState( false, null, null, $"document type {documentType} is not deployed" ) : Interpret( facts );
   }

   #endregion Public Methods

   #region Private Methods

   /// <summary>
   /// Polls the engine until the state is ready, passing each state's detail to <paramref name="seen"/> so
   /// the caller can quote the last one if the deadline passes. Fails at once when the engine says it has no
   /// HNSW index, and fails when the index count has stayed short and unchanged for STALL_AFTER.
   /// </summary>
   /// <param name="documentType">Document type name.</param>
   /// <param name="ct">Cancellation, which carries the finish deadline.</param>
   /// <param name="seen">Called with the detail of each state read.</param>
   /// <returns>The first ready state.</returns>
   /// <exception cref="InvalidOperationException">The type is missing, has no HNSW index, or has stalled short.</exception>
   private async Task<IndexState> WaitForIndexAsync( string documentType, CancellationToken ct, Action<string> seen )
   {
      var clock = Stopwatch.StartNew();
      var unchanged = Stopwatch.StartNew();
      TimeSpan nextReport = PROGRESS_EVERY;
      TimeSpan pause = POLL_FIRST;
      IndexFacts? previous = null;
      while( true )
      {
         IndexFacts facts = await ReadFactsAsync( documentType, ct ) ?? throw new InvalidOperationException( $"Vespa document type {documentType} is not deployed, so there is nothing to finish. Create and load it first." );
         IndexState state = Interpret( facts );
         seen( state.Detail );
         if( state.Ready )
         {
            return state;
         }

         if( facts.Blueprints.Count > 0 && facts.Blueprints.Any( b => !b.HasIndex || b.Algorithm != HNSW_ALGORITHM ) )
         {
            throw new InvalidOperationException( state.Detail );
         }

         if( previous == null || previous.Documents != facts.Documents || previous.TopKHits != facts.TopKHits )
         {
            unchanged.Restart();
         }
         else if( unchanged.Elapsed >= STALL_AFTER )
         {
            throw new InvalidOperationException( $"Vespa document type {documentType} has stalled: {state.Detail}" );
         }

         previous = facts;
         nextReport = ReportProgress( $"Vespa {documentType}: waiting for the index for {clock.Elapsed.TotalSeconds:F0} s ({state.Detail})", clock, nextReport );
         await Task.Delay( pause, ct );
         pause = Slower( pause );
      }
   }

   /// <summary>
   /// Sends a progress line when the next report time has passed.
   /// </summary>
   /// <param name="line">The line.</param>
   /// <param name="clock">Time since the step started.</param>
   /// <param name="nextReport">When the next line is due.</param>
   /// <returns>When the following line is due.</returns>
   private TimeSpan ReportProgress( string line, Stopwatch clock, TimeSpan nextReport )
   {
      if( clock.Elapsed < nextReport )
      {
         return nextReport;
      }

      if( _options.Progress != null )
      {
         _options.Progress( line );
      }
      else
      {
         Console.Error.WriteLine( line );
      }

      return nextReport + PROGRESS_EVERY;
   }

   /// <summary>
   /// The next polling pause: double the last one, up to POLL_MAX.
   /// </summary>
   /// <param name="pause">The pause just used.</param>
   /// <returns>The pause to use next.</returns>
   private static TimeSpan Slower( TimeSpan pause )
   {
      return TimeSpan.FromTicks( Math.Min( pause.Ticks * 2, POLL_MAX.Ticks ) );
   }

   /// <summary>
   /// Reads the numbers the state is built from.
   /// </summary>
   /// <param name="documentType">Document type name.</param>
   /// <param name="ct">Cancellation.</param>
   /// <returns>The facts, or null when the type is not deployed.</returns>
   private async Task<IndexFacts?> ReadFactsAsync( string documentType, CancellationToken ct )
   {
      string? schema = await _rest.ReadSchemaAsync( documentType, ct );
      int? dimension = schema == null ? null : VespaApplicationPackage.Dimension( schema );
      if( dimension == null )
      {
         return null;
      }

      var count = new Dictionary<string, object>
      {
         ["yql"] = $"select * from {documentType} where true",
         ["hits"] = 0,
         ["ranking"] = "unranked",
         ["timeout"] = "60s",
      };

      using JsonDocument counted = await _rest.QueryAsync( count, ct );
      JsonElement root = counted.RootElement.GetProperty( "root" );
      long documents = root.GetProperty( "fields" ).GetProperty( "totalCount" ).GetInt64();
      bool full = root.TryGetProperty( "coverage", out JsonElement coverage ) && coverage.GetProperty( "full" ).GetBoolean();
      if( documents == 0 )
      {
         return new IndexFacts( 0, full, 0, new List<Blueprint>() );
      }

      using JsonDocument probed = await _rest.QueryAsync( ProbeQuery( documentType, dimension.Value, documents ), ct );
      var blueprints = new List<Blueprint>();
      CollectBlueprints( probed.RootElement, blueprints );
      return new IndexFacts( documents, full, blueprints.Sum( b => b.TopKHits ), blueprints );
   }

   /// <summary>
   /// The probe query: approximate nearestNeighbor asking for more hits than there are documents, no hits
   /// returned, with the query trace that carries the content node's description of the search.
   /// The query vector is the first unit axis, a valid cosine query; only the trace is used.
   /// </summary>
   /// <param name="documentType">Document type name.</param>
   /// <param name="dimension">Vector length.</param>
   /// <param name="documents">Searchable documents.</param>
   /// <returns>The query parameters.</returns>
   private static Dictionary<string, object> ProbeQuery( string documentType, int dimension, long documents )
   {
      string input = VespaApplicationPackage.QueryInput( dimension );
      var axis = new float[dimension];
      axis[0] = 1f;
      return new Dictionary<string, object>
      {
         ["yql"] = $"select documentid from {documentType} where {{targetHits:{documents + 1},approximate:true}}nearestNeighbor(embedding,{input})",
         [$"input.query({input})"] = axis,
         ["hits"] = 0,
         ["ranking"] = "default",
         ["timeout"] = "60s",
         ["trace.level"] = 1,
         ["trace.explainLevel"] = 1,
      };
   }

   /// <summary>
   /// Walks the answer's trace and collects every nearest-neighbour blueprint (one per content node).
   /// </summary>
   /// <param name="node">A JSON value of the trace.</param>
   /// <param name="found">Collects the blueprints.</param>
   private static void CollectBlueprints( JsonElement node, List<Blueprint> found )
   {
      if( node.ValueKind == JsonValueKind.Object )
      {
         if( node.TryGetProperty( "[type]", out JsonElement type ) && type.ValueKind == JsonValueKind.String && type.GetString() == BLUEPRINT )
         {
            found.Add( ReadBlueprint( node ) );
            return;
         }

         foreach( JsonProperty property in node.EnumerateObject() )
         {
            CollectBlueprints( property.Value, found );
         }
      }
      else if( node.ValueKind == JsonValueKind.Array )
      {
         foreach( JsonElement item in node.EnumerateArray() )
         {
            CollectBlueprints( item, found );
         }
      }
   }

   /// <summary>
   /// Reads the fields of one blueprint that say how the search ran.
   /// </summary>
   /// <param name="node">The blueprint object.</param>
   /// <returns>The values; missing fields read as false, empty or zero.</returns>
   private static Blueprint ReadBlueprint( JsonElement node )
   {
      bool Flag( string name ) => node.TryGetProperty( name, out JsonElement v ) && v.ValueKind == JsonValueKind.True;
      long topK = node.TryGetProperty( "top_k_hits", out JsonElement hits ) && hits.ValueKind == JsonValueKind.Number ? hits.GetInt64() : 0;
      string algorithm = node.TryGetProperty( "algorithm", out JsonElement a ) && a.ValueKind == JsonValueKind.String ? a.GetString()! : "none reported";
      return new Blueprint( Flag( "has_index" ), algorithm, topK, Flag( "timeout_hit" ), Flag( "terminated_early" ) );
   }

   /// <summary>
   /// Turns the facts into the index state. Ready needs full coverage, the HNSW algorithm on every content
   /// node, no timeout or early stop, and the graph returning every searchable document. An empty type is
   /// ready: nothing is waiting to be indexed.
   /// </summary>
   /// <param name="facts">What the engine reported.</param>
   /// <returns>The state, with the evidence in its detail.</returns>
   private static IndexState Interpret( IndexFacts facts )
   {
      if( facts.Documents == 0 )
      {
         return new IndexState( true, 0, 0, "no documents stored, nothing to index (totalCount 0)" );
      }

      string seen = $"totalCount {facts.Documents:N0}, coverage full {facts.CoverageFull}, nearestNeighbor approximate:true with targetHits above the count: {Describe( facts.Blueprints )}";
      if( facts.Blueprints.Count == 0 )
      {
         return new IndexState( false, null, facts.Documents, $"the query trace has no NearestNeighborBlueprint, so the search algorithm cannot be confirmed: {seen}" );
      }

      if( facts.Blueprints.Any( b => !b.HasIndex || b.Algorithm != HNSW_ALGORITHM ) )
      {
         return new IndexState( false, facts.TopKHits, facts.Documents, $"NO HNSW INDEX USED, searches scan all {facts.Documents:N0} vectors: {seen}" );
      }

      if( !facts.CoverageFull || facts.Blueprints.Any( b => b.TimeoutHit || b.TerminatedEarly ) )
      {
         return new IndexState( false, facts.TopKHits, facts.Documents, $"the probe was cut short or did not reach every content node: {seen}" );
      }

      if( facts.TopKHits != facts.Documents )
      {
         return new IndexState( false, facts.TopKHits, facts.Documents, $"the HNSW graph returns {facts.TopKHits:N0} of {facts.Documents:N0} vectors: {seen}" );
      }

      return new IndexState( true, facts.TopKHits, facts.Documents, $"HNSW graph returns {facts.TopKHits:N0} of {facts.Documents:N0} vectors: {seen}; the graph is updated while each write is applied, so there is no later build step" );
   }

   /// <summary>
   /// Short text for the blueprints of an answer.
   /// </summary>
   /// <param name="blueprints">The blueprints.</param>
   /// <returns>The text.</returns>
   private static string Describe( List<Blueprint> blueprints )
   {
      return blueprints.Count == 0 ? "no blueprint in the trace" : string.Join( "; ", blueprints.Select( b => $"has_index {b.HasIndex}, algorithm \"{b.Algorithm}\", top_k_hits {b.TopKHits:N0}" ) );
   }

   /// <summary>
   /// One content node's account of its nearest-neighbour search.
   /// </summary>
   /// <param name="HasIndex">The field has an HNSW index (has_index).</param>
   /// <param name="Algorithm">"index top k" for the HNSW search, "exact" for a scan.</param>
   /// <param name="TopKHits">Vectors the search returned (top_k_hits).</param>
   /// <param name="TimeoutHit">The search ran out of time (timeout_hit).</param>
   /// <param name="TerminatedEarly">The search stopped early (terminated_early).</param>
   private sealed record Blueprint( bool HasIndex, string Algorithm, long TopKHits, bool TimeoutHit, bool TerminatedEarly );

   /// <summary>
   /// The numbers read from Vespa that the index state is built from.
   /// </summary>
   /// <param name="Documents">Searchable documents (totalCount).</param>
   /// <param name="CoverageFull">Every content node answered (coverage.full).</param>
   /// <param name="TopKHits">Vectors the HNSW search returned, summed over content nodes.</param>
   /// <param name="Blueprints">The nearest-neighbour blueprints of the probe's trace.</param>
   private sealed record IndexFacts( long Documents, bool CoverageFull, long TopKHits, List<Blueprint> Blueprints );

   #endregion Private Methods
}
