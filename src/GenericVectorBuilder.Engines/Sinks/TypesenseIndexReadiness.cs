using System.Diagnostics;
using System.Globalization;
using System.Text.Json;
using GenericVectorBuilder.Engines.Common;

namespace GenericVectorBuilder.Engines.Sinks;

/// <summary>
/// Checks, and waits for, the HNSW index of one Typesense collection (used by <see cref="TypesenseSink"/>).
/// How Typesense builds the index: it keeps the HNSW graph in memory and inserts each vector while the
/// document is being written, inside the write request. There is no separate build step to run, so the
/// finish step only waits until the engine reports its write queue empty and the graph complete.
/// What proves readiness (all read from the engine):
///   1. GET /collections/{name} num_documents: the stored documents.
///   2. GET /stats.json pending_write_batches: write batches queued but not applied; must be 0.
///   3. GET /health: ok, with no resource_error (out of memory or disk makes Typesense stop indexing).
///   4. A vector query with no filter and k above the document count: Typesense answers it from the HNSW
///      graph and reports "found", the number of vectors the graph returned. found equal to num_documents
///      means the graph holds every stored vector.
/// What the engine does not say: which algorithm answered a query. Typesense switches to a brute-force scan
/// only for a filtered query that matches fewer documents than flat_search_cutoff, so a query without a
/// filter cannot be a scan. TypesenseReadinessTests checks that premise: a default-ef search returns
/// different hits from the exact search, which a scan could not.
/// </summary>
internal sealed class TypesenseIndexReadiness
{
   #region Data Members

   private const string JSON = "application/json";
   private static readonly TimeSpan POLL_FIRST = TimeSpan.FromMilliseconds( 100 );
   private static readonly TimeSpan POLL_MAX = TimeSpan.FromSeconds( 1 );
   private static readonly TimeSpan PROGRESS_EVERY = TimeSpan.FromSeconds( 10 );
   private static readonly TimeSpan STALL_AFTER = TimeSpan.FromSeconds( 30 );

   private readonly TypesenseRest _rest;
   private readonly TypesenseSinkOptions _options;

   #endregion Data Members

   #region Constructor

   /// <summary>
   /// Creates the helper over the sink's HTTP client.
   /// </summary>
   /// <param name="rest">The sink's Typesense client.</param>
   /// <param name="options">Sink settings (index step time limit and progress output).</param>
   public TypesenseIndexReadiness( TypesenseRest rest, TypesenseSinkOptions options )
   {
      _rest = rest;
      _options = options;
   }

   #endregion Constructor

   #region Public Methods

   /// <summary>
   /// Waits until the write queue is empty and the graph holds every stored vector.
   /// </summary>
   /// <param name="collection">Typesense collection name.</param>
   /// <param name="ct">Cancellation.</param>
   /// <returns>What was found and how long it took.</returns>
   /// <exception cref="InvalidOperationException">The collection is missing, the graph stopped short of the
   /// documents with nothing queued, or the finish time limit passed.</exception>
   public async Task<string> FinishAsync( string collection, CancellationToken ct )
   {
      var clock = Stopwatch.StartNew();
      TimeSpan limit = _options.EffectiveFinishTimeout;
      string last = "no state read yet";
      using var deadline = CancellationTokenSource.CreateLinkedTokenSource( ct );
      deadline.CancelAfter( limit );
      try
      {
         IndexState state = await WaitForGraphAsync( collection, deadline.Token, seen => last = seen );
         return $"nothing to build, Typesense inserts into the HNSW graph while writing; ready after {clock.Elapsed.TotalSeconds:F1} s: {state.Detail}";
      }
      catch( OperationCanceledException ) when( !ct.IsCancellationRequested )
      {
         throw new InvalidOperationException( $"Typesense collection {collection} was not ready within {limit.TotalSeconds:0.###} s (the engine did not answer in time). Last state: {last}" );
      }
   }

   /// <summary>
   /// Reads the index state: documents, queued writes, health and how many vectors the graph returns.
   /// Nothing is written; the probe is a search.
   /// </summary>
   /// <param name="collection">Typesense collection name.</param>
   /// <param name="ct">Cancellation.</param>
   /// <returns>The state, with the evidence in its detail.</returns>
   public async Task<IndexState> ReadStateAsync( string collection, CancellationToken ct )
   {
      IndexFacts? facts = await ReadFactsAsync( collection, ct );
      return facts == null ? new IndexState( false, null, null, $"collection {collection} does not exist" ) : Interpret( facts );
   }

   #endregion Public Methods

   #region Private Methods

   /// <summary>
   /// Polls the engine until the state is ready, passing each state's detail to <paramref name="seen"/> so
   /// the caller can quote the last one if the deadline passes. Fails early when the queue is empty and the
   /// graph count has stayed short and unchanged for STALL_AFTER, because then nothing is coming.
   /// </summary>
   /// <param name="collection">Collection name.</param>
   /// <param name="ct">Cancellation, which carries the finish deadline.</param>
   /// <param name="seen">Called with the detail of each state read.</param>
   /// <returns>The first ready state.</returns>
   /// <exception cref="InvalidOperationException">The collection is missing or the graph has stalled short.</exception>
   private async Task<IndexState> WaitForGraphAsync( string collection, CancellationToken ct, Action<string> seen )
   {
      var clock = Stopwatch.StartNew();
      var unchanged = Stopwatch.StartNew();
      TimeSpan nextReport = PROGRESS_EVERY;
      TimeSpan pause = POLL_FIRST;
      IndexFacts? previous = null;
      while( true )
      {
         IndexFacts facts = await ReadFactsAsync( collection, ct ) ?? throw new InvalidOperationException( $"Typesense collection {collection} does not exist, so there is nothing to finish. Create and load it first." );
         IndexState state = Interpret( facts );
         seen( state.Detail );
         if( state.Ready )
         {
            return state;
         }

         if( previous == null || previous.Reachable != facts.Reachable || previous.Documents != facts.Documents || facts.PendingWrites > 0 )
         {
            unchanged.Restart();
         }
         else if( unchanged.Elapsed >= STALL_AFTER )
         {
            throw new InvalidOperationException( $"Typesense collection {collection} has stalled with no writes queued: {state.Detail}" );
         }

         previous = facts;
         nextReport = ReportProgress( $"Typesense {collection}: waiting for the graph for {clock.Elapsed.TotalSeconds:F0} s ({state.Detail})", clock, nextReport );
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
   /// <param name="collection">Collection name.</param>
   /// <param name="ct">Cancellation.</param>
   /// <returns>The facts, or null when the collection does not exist.</returns>
   private async Task<IndexFacts?> ReadFactsAsync( string collection, CancellationToken ct )
   {
      using JsonDocument? info = await _rest.SendJsonAsync( HttpMethod.Get, $"collections/{collection}", null, true, ct );
      if( info == null )
      {
         return null;
      }

      long documents = info.RootElement.GetProperty( "num_documents" ).GetInt64();
      int dimension = info.RootElement.GetProperty( "fields" ).EnumerateArray().Where( f => f.GetProperty( "name" ).GetString() == "vec" ).Select( f => f.GetProperty( "num_dim" ).GetInt32() ).FirstOrDefault();
      using JsonDocument? stats = await _rest.SendJsonAsync( HttpMethod.Get, "stats.json", null, false, ct );
      long pending = stats!.RootElement.TryGetProperty( "pending_write_batches", out JsonElement p ) ? p.GetInt64() : 0;
      string? healthProblem = await HealthProblemAsync( ct );
      long reachable = documents == 0 || dimension == 0 ? 0 : await CountReachableAsync( collection, dimension, documents, ct );
      return new IndexFacts( documents, pending, healthProblem, reachable );
   }

   /// <summary>
   /// Reads /health. Typesense answers 503 with a resource_error when it is out of memory or disk, and then
   /// refuses writes.
   /// </summary>
   /// <param name="ct">Cancellation.</param>
   /// <returns>Null when healthy, else what the engine said.</returns>
   private async Task<string?> HealthProblemAsync( CancellationToken ct )
   {
      try
      {
         using JsonDocument? health = await _rest.SendJsonAsync( HttpMethod.Get, "health", null, false, ct );
         bool ok = health!.RootElement.TryGetProperty( "ok", out JsonElement okValue ) && okValue.GetBoolean();
         return ok && !health.RootElement.TryGetProperty( "resource_error", out _ ) ? null : health.RootElement.GetRawText();
      }
      catch( TypesenseRestException ex ) when( ex.Status == 503 )
      {
         return ex.Message;
      }
   }

   /// <summary>
   /// Asks the graph for more neighbours than there are documents and reports how many it returned.
   /// The query vector is the first unit axis, a valid cosine query; only the count is used.
   /// </summary>
   /// <param name="collection">Collection name.</param>
   /// <param name="dimension">Vector length.</param>
   /// <param name="documents">Documents stored.</param>
   /// <param name="ct">Cancellation.</param>
   /// <returns>The "found" count of the answer.</returns>
   private async Task<long> CountReachableAsync( string collection, int dimension, long documents, CancellationToken ct )
   {
      string axis = string.Join( ",", Enumerable.Range( 0, dimension ).Select( i => i == 0 ? "1" : "0" ) );
      var search = new Dictionary<string, object>
      {
         ["collection"] = collection,
         ["q"] = "*",
         ["vector_query"] = $"vec:([{axis}], k:{( documents + 1 ).ToString( CultureInfo.InvariantCulture )})",
         ["per_page"] = 1,
         ["include_fields"] = "id",
      };

      using JsonDocument? answer = await _rest.SendJsonAsync( HttpMethod.Post, "multi_search", new { searches = new[] { search } }, false, ct );
      JsonElement result = answer!.RootElement.GetProperty( "results" )[0];
      if( result.TryGetProperty( "error", out JsonElement error ) )
      {
         throw new InvalidOperationException( $"Typesense could not run the index probe on {collection}: {error.GetString()}" );
      }

      return result.GetProperty( "found" ).GetInt64();
   }

   /// <summary>
   /// Turns the facts into the index state. Ready needs an empty write queue, a healthy node and a graph
   /// that returns every stored vector. An empty collection is ready: nothing is waiting to be indexed.
   /// </summary>
   /// <param name="facts">What the engine reported.</param>
   /// <returns>The state, with the evidence in its detail.</returns>
   private static IndexState Interpret( IndexFacts facts )
   {
      if( facts.Documents == 0 )
      {
         return new IndexState( true, 0, 0, "no vectors stored, nothing to index (num_documents 0)" );
      }

      string seen = $"num_documents {facts.Documents:N0}, pending_write_batches {facts.PendingWrites}, health {( facts.HealthProblem ?? "ok" )}, unfiltered vector query with k above the count returned {facts.Reachable:N0}";
      if( facts.HealthProblem != null )
      {
         return new IndexState( false, facts.Reachable, facts.Documents, $"the node reports a problem, so indexing may have stopped: {seen}" );
      }

      if( facts.PendingWrites > 0 )
      {
         return new IndexState( false, facts.Reachable, facts.Documents, $"writes are still queued: {seen}" );
      }

      if( facts.Reachable != facts.Documents )
      {
         return new IndexState( false, facts.Reachable, facts.Documents, $"the HNSW graph returns {facts.Reachable:N0} of {facts.Documents:N0} vectors: {seen}" );
      }

      return new IndexState( true, facts.Reachable, facts.Documents, $"HNSW graph returns {facts.Reachable:N0} of {facts.Documents:N0} vectors, no writes queued: {seen}; the graph is built in memory during each write, so there is no later build step" );
   }

   /// <summary>
   /// The numbers read from Typesense that the index state is built from.
   /// </summary>
   /// <param name="Documents">Documents stored (num_documents).</param>
   /// <param name="PendingWrites">Write batches queued, node wide (stats.json pending_write_batches).</param>
   /// <param name="HealthProblem">Null when /health is ok, else what it said.</param>
   /// <param name="Reachable">Vectors the unfiltered probe query returned (found).</param>
   private sealed record IndexFacts( long Documents, long PendingWrites, string? HealthProblem, long Reachable );

   #endregion Private Methods
}
