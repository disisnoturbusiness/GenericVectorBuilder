using System.Diagnostics;
using System.Text.Json;
using GenericVectorBuilder.Engines.Common;

namespace GenericVectorBuilder.Engines.Sinks;

/// <summary>
/// Finishes and checks the k-NN (faiss HNSW) index of one OpenSearch collection (used by <see cref="OpenSearchSink"/>).
/// What the finish step does: refresh (so every written document is searchable), force merge to one
/// segment, wait until no merge is running, refresh again, then poll the engine until one profiled probe
/// search shows every segment answered from its graph.
/// Why OpenSearch needs a finish step: the k-NN plugin builds one faiss graph per segment when the segment
/// is written or merged, and a bulk load leaves many small segments. A search walks every segment's graph, so
/// many small segments cost more than one, and background merges rebuild graphs while the benchmark runs.
/// Force merge choice: yes, always, for the same reasons as <see cref="ElasticsearchIndexReadiness"/>: one
/// graph per shard, and a definite "finished" state to wait for. The merge time is the cost of finishing
/// the load, so it sits inside this timed step. The state's detail says so.
/// What proves readiness (read from the engine, nothing inferred from timing): _count (searchable
/// documents), _cat/segments (segments and their live documents), _stats merges.current (merges running)
/// and a probe kNN search run with "profile": true, whose KNNQuery breakdown counts ann_search_count
/// (segments the search tried through the graph) and exact_search_count (the segments among them that fell
/// back to a scan). Measured on this container: one segment above the size cut gives ann 1, exact 0; one
/// segment below it gives ann 1, exact 1; a 1,500-document segment plus a 400-document one with a cut of
/// 1,000 gives ann 2, exact 1. So graph-searched segments are ann minus exact. Ready means no merge is
/// running and ann_search_count equals the number of segments that hold documents, with exact_search_count 0.
/// Why a probe search: file sizes and the plugin's cache statistics do not show which segments have a
/// graph (measured: compound segments hide the .faiss file from _stats), but the profile says how the
/// engine actually searched, which is what readiness means. The probe also loads the graphs into the
/// plugin's native memory, as the first search of any run would; the benchmark's warm-up covers it.
/// </summary>
internal sealed class OpenSearchIndexReadiness
{
   #region Data Members

   private const string JSON = "application/json";
   private static readonly TimeSpan POLL_FIRST = TimeSpan.FromMilliseconds( 100 );
   private static readonly TimeSpan POLL_MAX = TimeSpan.FromSeconds( 1 );
   private static readonly TimeSpan PROGRESS_EVERY = TimeSpan.FromSeconds( 10 );

   private readonly OpenSearchRest _rest;
   private readonly OpenSearchSinkOptions _options;

   #endregion Data Members

   #region Constructor

   /// <summary>
   /// Creates the helper over the sink's HTTP client.
   /// </summary>
   /// <param name="rest">The sink's OpenSearch client.</param>
   /// <param name="options">Sink settings (index step time limit and progress output).</param>
   public OpenSearchIndexReadiness( OpenSearchRest rest, OpenSearchSinkOptions options )
   {
      _rest = rest;
      _options = options;
   }

   #endregion Constructor

   #region Public Methods

   /// <summary>
   /// Makes every written document searchable, force merges to one segment and waits until the engine
   /// shows every segment searched through its graph.
   /// </summary>
   /// <param name="index">OpenSearch index name.</param>
   /// <param name="ct">Cancellation.</param>
   /// <returns>What was built and how long it took.</returns>
   /// <exception cref="InvalidOperationException">The index is missing, cannot have a graph with its settings,
   /// or was not ready within the finish time limit.</exception>
   public async Task<string> FinishAsync( string index, CancellationToken ct )
   {
      var clock = Stopwatch.StartNew();
      TimeSpan limit = _options.EffectiveFinishTimeout;
      string last = "no state read yet";
      using var deadline = CancellationTokenSource.CreateLinkedTokenSource( ct );
      deadline.CancelAfter( limit );
      try
      {
         await RefreshAsync( index, deadline.Token );
         await ForceMergeAsync( index, deadline.Token );
         await RefreshAsync( index, deadline.Token );
         IndexState state = await WaitForGraphsAsync( index, deadline.Token, seen => last = seen );
         return $"force-merged to one segment, graph ready after {clock.Elapsed.TotalSeconds:F1} s: {state.Detail}";
      }
      catch( OperationCanceledException ) when( !ct.IsCancellationRequested )
      {
         throw new InvalidOperationException( $"OpenSearch index {index} was not finished within {limit.TotalSeconds:0.###} s (the engine or the merge did not answer in time). Last state: {last}" );
      }
   }

   /// <summary>
   /// Reads the index state: what the engine's own profile says a search does right now. Nothing is
   /// written; the probe search is a read.
   /// </summary>
   /// <param name="index">OpenSearch index name.</param>
   /// <param name="ct">Cancellation.</param>
   /// <returns>The state, with the evidence in its detail.</returns>
   public async Task<IndexState> ReadStateAsync( string index, CancellationToken ct )
   {
      IndexFacts? facts = await ReadFactsAsync( index, ct );
      return facts == null ? new IndexState( false, null, null, $"index {index} does not exist" ) : Interpret( facts );
   }

   #endregion Public Methods

   #region Private Methods

   /// <summary>
   /// Makes everything written so far searchable.
   /// </summary>
   /// <param name="index">Index name.</param>
   /// <param name="ct">Cancellation.</param>
   private async Task RefreshAsync( string index, CancellationToken ct )
   {
      using JsonDocument? refreshed = await _rest.SendAsync( HttpMethod.Post, $"{index}/_refresh", null, JSON, true, ct );
      if( refreshed == null )
      {
         throw new InvalidOperationException( $"OpenSearch index {index} does not exist, so there is nothing to finish. Create and load it first." );
      }
   }

   /// <summary>
   /// Force merges the index to one segment as a background task and polls the task until it ends.
   /// Why a task and not one long request: a merge of a big collection can outlast one HTTP call, and a
   /// task can be polled under the caller's deadline. When the engine no longer has the task record the
   /// wait ends and the state check that follows decides, because it requires no merge and every segment
   /// answered from its graph.
   /// </summary>
   /// <param name="index">Index name.</param>
   /// <param name="ct">Cancellation, which carries the finish deadline.</param>
   /// <exception cref="InvalidOperationException">The merge task reported a failure.</exception>
   private async Task ForceMergeAsync( string index, CancellationToken ct )
   {
      using JsonDocument? started = await _rest.SendAsync( HttpMethod.Post, $"{index}/_forcemerge?max_num_segments=1&wait_for_completion=false", null, JSON, false, ct );
      string task = started!.RootElement.GetProperty( "task" ).GetString()!;
      var clock = Stopwatch.StartNew();
      TimeSpan nextReport = PROGRESS_EVERY;
      TimeSpan pause = POLL_FIRST;
      while( true )
      {
         using JsonDocument? status = await _rest.SendAsync( HttpMethod.Get, $"_tasks/{Uri.EscapeDataString( task )}", null, JSON, true, ct );
         if( status == null || ReadTaskDone( status, index ) )
         {
            return;
         }

         nextReport = ReportProgress( $"OpenSearch {index}: force merge running for {clock.Elapsed.TotalSeconds:F0} s", clock, nextReport );
         await Task.Delay( pause, ct );
         pause = Slower( pause );
      }
   }

   /// <summary>
   /// Reads a task answer: true when the task is complete, and throws when it completed with a failure.
   /// </summary>
   /// <param name="status">The _tasks answer.</param>
   /// <param name="index">Index name for the message.</param>
   /// <returns>True when the task has finished.</returns>
   /// <exception cref="InvalidOperationException">The task failed or a shard failed to merge.</exception>
   private static bool ReadTaskDone( JsonDocument status, string index )
   {
      JsonElement root = status.RootElement;
      if( root.TryGetProperty( "error", out JsonElement error ) )
      {
         throw new InvalidOperationException( $"OpenSearch force merge of {index} failed: {error.GetRawText()}" );
      }

      if( !root.GetProperty( "completed" ).GetBoolean() )
      {
         return false;
      }

      if( root.TryGetProperty( "response", out JsonElement response ) && response.GetProperty( "_shards" ).GetProperty( "failed" ).GetInt32() > 0 )
      {
         throw new InvalidOperationException( $"OpenSearch force merge of {index} failed on a shard: {response.GetRawText()}" );
      }

      return true;
   }

   /// <summary>
   /// Polls the engine until every segment is searched through its graph, passing each state's detail to
   /// <paramref name="seen"/> so the caller can quote the last one if the deadline passes. Stops at once
   /// when one finished segment is scanned although the merge is done, because waiting cannot change that.
   /// </summary>
   /// <param name="index">Index name.</param>
   /// <param name="ct">Cancellation, which carries the finish deadline.</param>
   /// <param name="seen">Called with the detail of each state read.</param>
   /// <returns>The first ready state.</returns>
   /// <exception cref="InvalidOperationException">No graph can exist for this collection with its settings.</exception>
   private async Task<IndexState> WaitForGraphsAsync( string index, CancellationToken ct, Action<string> seen )
   {
      var clock = Stopwatch.StartNew();
      TimeSpan nextReport = PROGRESS_EVERY;
      TimeSpan pause = POLL_FIRST;
      while( true )
      {
         IndexFacts facts = await ReadFactsAsync( index, ct ) ?? throw new InvalidOperationException( $"OpenSearch index {index} disappeared while it was being finished." );
         IndexState state = Interpret( facts );
         seen( state.Detail );
         if( state.Ready )
         {
            return state;
         }

         if( IsFinishedWithoutGraph( facts ) )
         {
            throw new InvalidOperationException( state.Detail );
         }

         nextReport = ReportProgress( $"OpenSearch {index}: waiting for the graphs for {clock.Elapsed.TotalSeconds:F0} s ({state.Detail})", clock, nextReport );
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
   /// The next polling pause: double the last one, up to POLL_MAX. Why: a small collection finishes in
   /// well under a second and should not wait a whole second to find out, while a long merge should not
   /// be polled ten times a second.
   /// </summary>
   /// <param name="pause">The pause just used.</param>
   /// <returns>The pause to use next.</returns>
   private static TimeSpan Slower( TimeSpan pause )
   {
      return TimeSpan.FromTicks( Math.Min( pause.Ticks * 2, POLL_MAX.Ticks ) );
   }

   /// <summary>
   /// Reads the numbers the state is built from: searchable documents, segments, running merges, and how a
   /// profiled probe search was answered.
   /// </summary>
   /// <param name="index">Index name.</param>
   /// <param name="ct">Cancellation.</param>
   /// <returns>The facts, or null when the index does not exist.</returns>
   private async Task<IndexFacts?> ReadFactsAsync( string index, CancellationToken ct )
   {
      using JsonDocument? count = await _rest.SendAsync( HttpMethod.Get, $"{index}/_count", null, JSON, true, ct );
      if( count == null )
      {
         return null;
      }

      using JsonDocument? segments = await _rest.SendAsync( HttpMethod.Get, $"_cat/segments/{index}?format=json&h=segment,docs.count", null, JSON, false, ct );
      using JsonDocument? merges = await _rest.SendAsync( HttpMethod.Get, $"{index}/_stats/merge?filter_path=_all.primaries.merges.current", null, JSON, false, ct );
      long documents = count.RootElement.GetProperty( "count" ).GetInt64();
      int withDocs = segments!.RootElement.EnumerateArray().Count( s => long.Parse( s.GetProperty( "docs.count" ).GetString()! ) > 0 );
      long running = merges!.RootElement.GetProperty( "_all" ).GetProperty( "primaries" ).GetProperty( "merges" ).GetProperty( "current" ).GetInt64();
      if( documents == 0 || withDocs == 0 )
      {
         return new IndexFacts( documents, withDocs, running, 0, 0, string.Empty );
      }

      ( long ann, long exact ) = await ProbeAsync( index, ct );
      string threshold = await ThresholdAsync( index, ct );
      return new IndexFacts( documents, withDocs, running, ann, exact, threshold );
   }

   /// <summary>
   /// Runs one profiled kNN search and returns how many segments the engine answered from the graph and
   /// how many it scanned. The probe vector is the first unit axis, which is a valid cosine query.
   /// </summary>
   /// <param name="index">Index name.</param>
   /// <param name="ct">Cancellation.</param>
   /// <returns>The profile's ann_search_count and exact_search_count, summed over shards.</returns>
   private async Task<(long Ann, long Exact)> ProbeAsync( string index, CancellationToken ct )
   {
      using JsonDocument? mapping = await _rest.SendAsync( HttpMethod.Get, $"{index}/_mapping?filter_path=*.mappings.properties.vector.dimension", null, JSON, false, ct );
      int dimension = mapping!.RootElement.GetProperty( index ).GetProperty( "mappings" ).GetProperty( "properties" ).GetProperty( "vector" ).GetProperty( "dimension" ).GetInt32();
      var probe = new float[dimension];
      probe[0] = 1f;
      var body = new
      {
         size = 1,
         profile = true,
         track_total_hits = false,
         _source = false,
         query = new { knn = new Dictionary<string, object> { ["vector"] = new { vector = probe, k = 1 } } },
      };

      using JsonDocument? answer = await _rest.SendJsonAsync( HttpMethod.Post, $"{index}/_search", body, false, ct );
      long ann = 0;
      long exact = 0;
      foreach( JsonElement shard in answer!.RootElement.GetProperty( "profile" ).GetProperty( "shards" ).EnumerateArray() )
      {
         foreach( JsonElement search in shard.GetProperty( "searches" ).EnumerateArray() )
         {
            foreach( JsonElement query in search.GetProperty( "query" ).EnumerateArray() )
            {
               JsonElement breakdown = query.GetProperty( "breakdown" );
               ann += breakdown.GetProperty( "ann_search_count" ).GetInt64();
               exact += breakdown.GetProperty( "exact_search_count" ).GetInt64();
            }
         }
      }

      return ( ann, exact );
   }

   /// <summary>
   /// Reads index.knn.advanced.approximate_threshold (the segment size below which the plugin builds no
   /// graph and scans instead) as the engine reports it, defaults included.
   /// </summary>
   /// <param name="index">Index name.</param>
   /// <param name="ct">Cancellation.</param>
   /// <returns>The setting's value as text.</returns>
   private async Task<string> ThresholdAsync( string index, CancellationToken ct )
   {
      const string KEY = "index.knn.advanced.approximate_threshold";
      using JsonDocument? settings = await _rest.SendAsync( HttpMethod.Get, $"{index}/_settings/{KEY}?include_defaults=true&flat_settings=true", null, JSON, false, ct );
      JsonElement root = settings!.RootElement.GetProperty( index );
      JsonElement source = root.GetProperty( "settings" ).TryGetProperty( KEY, out JsonElement set ) ? set : root.GetProperty( "defaults" ).GetProperty( KEY );
      return source.GetString() ?? "?";
   }

   /// <summary>
   /// True when merging is over and the engine still scans a segment: one segment, no merge running, and
   /// the probe was answered by a scan. Nothing is left to wait for.
   /// </summary>
   /// <param name="facts">What the engine reported.</param>
   /// <returns>True when no graph can come.</returns>
   private static bool IsFinishedWithoutGraph( IndexFacts facts )
   {
      return facts.Documents > 0 && facts.Segments == 1 && facts.RunningMerges == 0 && facts.ExactSearches > 0;
   }

   /// <summary>
   /// Turns the facts into the index state. Ready needs no running merge and every segment that holds
   /// documents answered from its graph (ann_search_count equal to the segment count, exact_search_count 0).
   /// An empty index is ready: nothing is waiting to be indexed.
   /// </summary>
   /// <param name="facts">What the engine reported.</param>
   /// <returns>The state, with the evidence in its detail.</returns>
   private static IndexState Interpret( IndexFacts facts )
   {
      if( facts.Documents == 0 || facts.Segments == 0 )
      {
         return new IndexState( true, 0, facts.Documents, "no vectors stored, nothing to index (_count 0)" );
      }

      string seen = $"{facts.Segments} segment(s), {facts.RunningMerges} merge(s) running, profiled probe search: ann_search_count {facts.AnnSearches} (segments tried through a graph), exact_search_count {facts.ExactSearches} (of those, scanned instead), approximate_threshold {facts.Threshold}";
      if( facts.RunningMerges > 0 )
      {
         return new IndexState( false, null, facts.Documents, $"a merge is still running, so graphs are being rebuilt: {seen}" );
      }

      if( facts.ExactSearches == 0 && facts.AnnSearches == facts.Segments )
      {
         string shape = facts.Segments == 1 ? "force-merged to one segment on purpose, so one graph answers every search" : "several segments, each searched through its own graph";
         return new IndexState( true, facts.Documents, facts.Documents, $"every segment searched through its HNSW graph, covering {facts.Documents:N0} of {facts.Documents:N0} vectors: {seen}; {shape}" );
      }

      if( facts.AnnSearches - facts.ExactSearches <= 0 )
      {
         return new IndexState( false, 0, facts.Documents, $"NO HNSW GRAPH USED, searches scan all {facts.Documents:N0} vectors: {seen}; the plugin builds no graph for a segment under approximate_threshold documents" );
      }

      return new IndexState( false, null, facts.Documents, $"graphs cover only some segments: {seen}; the profile does not say which segments, so graph coverage cannot be counted" );
   }

   /// <summary>
   /// The numbers read from OpenSearch that the index state is built from.
   /// </summary>
   /// <param name="Documents">Searchable documents (_count).</param>
   /// <param name="Segments">Segments that hold documents (_cat/segments).</param>
   /// <param name="RunningMerges">Merges running now (_stats merges.current).</param>
   /// <param name="AnnSearches">Segments the probe search answered from a graph (profile ann_search_count).</param>
   /// <param name="ExactSearches">Segments the probe search scanned (profile exact_search_count).</param>
   /// <param name="Threshold">index.knn.advanced.approximate_threshold as the engine reports it.</param>
   private sealed record IndexFacts( long Documents, long Segments, long RunningMerges, long AnnSearches, long ExactSearches, string Threshold );

   #endregion Private Methods
}
