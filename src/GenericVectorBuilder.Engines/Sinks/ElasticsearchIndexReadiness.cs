using System.Diagnostics;
using System.Text.Json;
using GenericVectorBuilder.Engines.Common;

namespace GenericVectorBuilder.Engines.Sinks;

/// <summary>
/// Finishes and checks the HNSW index of one Elasticsearch collection (used by <see cref="ElasticsearchSink"/>).
/// What the finish step does: refresh (so every written document is searchable), force merge to one
/// segment, refresh again, then poll the engine's own statistics until they show one segment whose HNSW
/// graph covers every vector.
/// Why Elasticsearch needs a finish step at all: Lucene builds one HNSW graph per segment, and a bulk load
/// leaves many small segments. A segment that holds fewer vectors than Lucene's size cut gets no graph and
/// is scanned instead. Measured on this container (Elasticsearch 9.5.3, Lucene 10, 1,024 dimensions): a segment of 1,042
/// vectors had no graph (total_vex_size_bytes 0) and one of 1,043 vectors had one. So a collection of
/// 524 vectors can never have a graph, and its "HNSW" search is a scan.
/// Force merge choice: yes, always. One graph per shard is the shape the single-graph engines have, a
/// search then walks one graph instead of one per segment, and it gives a definite "finished" state to
/// wait for (one segment) instead of waiting for background merges that have no end signal. The merge
/// time is the cost of finishing the load, so it sits inside this timed step. The state's detail says so.
/// What proves readiness (all read from the engine, nothing is inferred from timing): _count (searchable
/// documents), _stats segments.count, dense_vector.value_count (vectors in the searchable segments) and
/// dense_vector.off_heap.total_vex_size_bytes (bytes of HNSW graph files, the .vex files). With one segment,
/// a non-zero graph size means that segment's graph covers all its vectors.
/// </summary>
internal sealed class ElasticsearchIndexReadiness
{
   #region Data Members

   private const string JSON = "application/json";
   private const string STATS_PATH = "_stats/segments,dense_vector?filter_path=_all.primaries.segments.count,_all.primaries.dense_vector.value_count,_all.primaries.dense_vector.off_heap.total_vex_size_bytes";
   private static readonly TimeSpan POLL_FIRST = TimeSpan.FromMilliseconds( 100 );
   private static readonly TimeSpan POLL_MAX = TimeSpan.FromSeconds( 1 );
   private static readonly TimeSpan PROGRESS_EVERY = TimeSpan.FromSeconds( 10 );

   private readonly ElasticsearchRest _rest;
   private readonly ElasticsearchSinkOptions _options;

   #endregion Data Members

   #region Constructor

   /// <summary>
   /// Creates the helper over the sink's HTTP client.
   /// </summary>
   /// <param name="rest">The sink's Elasticsearch client.</param>
   /// <param name="options">Sink settings (index step time limit and progress output).</param>
   public ElasticsearchIndexReadiness( ElasticsearchRest rest, ElasticsearchSinkOptions options )
   {
      _rest = rest;
      _options = options;
   }

   #endregion Constructor

   #region Public Methods

   /// <summary>
   /// Makes every written document searchable, force merges to one segment and waits until the engine
   /// reports that segment's HNSW graph covering every vector.
   /// </summary>
   /// <param name="index">Elasticsearch index name.</param>
   /// <param name="ct">Cancellation.</param>
   /// <returns>What was built and how long it took.</returns>
   /// <exception cref="InvalidOperationException">The index is missing, cannot have a graph at this size,
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
         IndexState state = await WaitForGraphAsync( index, deadline.Token, seen => last = seen );
         return $"force-merged to one segment, graph ready after {clock.Elapsed.TotalSeconds:F1} s: {state.Detail}";
      }
      catch( OperationCanceledException ) when( !ct.IsCancellationRequested )
      {
         throw new InvalidOperationException( $"Elasticsearch index {index} was not finished within {limit.TotalSeconds:0.###} s (the engine or the merge did not answer in time). Last state: {last}" );
      }
   }

   /// <summary>
   /// Reads the index state without changing anything: what a search would use right now.
   /// </summary>
   /// <param name="index">Elasticsearch index name.</param>
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
         throw new InvalidOperationException( $"Elasticsearch index {index} does not exist, so there is nothing to finish. Create and load it first." );
      }
   }

   /// <summary>
   /// Force merges the index to one segment as a background task and polls the task until it ends.
   /// Why a task and not one long request: a merge of a big collection can outlast one HTTP call, and a
   /// task can be polled under the caller's deadline. When the engine no longer has the task record the
   /// wait ends and the state check that follows decides, because it requires one segment.
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

         nextReport = ReportProgress( $"Elasticsearch {index}: force merge running for {clock.Elapsed.TotalSeconds:F0} s", clock, nextReport );
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
         throw new InvalidOperationException( $"Elasticsearch force merge of {index} failed: {error.GetRawText()}" );
      }

      if( !root.GetProperty( "completed" ).GetBoolean() )
      {
         return false;
      }

      if( root.TryGetProperty( "response", out JsonElement response ) && response.GetProperty( "_shards" ).GetProperty( "failed" ).GetInt32() > 0 )
      {
         throw new InvalidOperationException( $"Elasticsearch force merge of {index} failed on a shard: {response.GetRawText()}" );
      }

      return true;
   }

   /// <summary>
   /// Polls the engine's statistics until the graph covers every vector, passing each state's detail to
   /// <paramref name="seen"/> so the caller can quote the last one if the deadline passes. Stops at once
   /// when the engine shows one finished segment that has no graph, because waiting cannot change that.
   /// </summary>
   /// <param name="index">Index name.</param>
   /// <param name="ct">Cancellation, which carries the finish deadline.</param>
   /// <param name="seen">Called with the detail of each state read.</param>
   /// <returns>The first ready state.</returns>
   /// <exception cref="InvalidOperationException">No graph can exist for this collection.</exception>
   private async Task<IndexState> WaitForGraphAsync( string index, CancellationToken ct, Action<string> seen )
   {
      var clock = Stopwatch.StartNew();
      TimeSpan nextReport = PROGRESS_EVERY;
      TimeSpan pause = POLL_FIRST;
      while( true )
      {
         IndexFacts facts = await ReadFactsAsync( index, ct ) ?? throw new InvalidOperationException( $"Elasticsearch index {index} disappeared while it was being finished." );
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

         nextReport = ReportProgress( $"Elasticsearch {index}: waiting for the graph for {clock.Elapsed.TotalSeconds:F0} s ({state.Detail})", clock, nextReport );
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
   /// Reads the numbers the state is built from: searchable documents (_count), the segment and vector
   /// numbers from the index statistics, and the declared index type from the mapping.
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

      using JsonDocument? stats = await _rest.SendAsync( HttpMethod.Get, $"{index}/{STATS_PATH}", null, JSON, false, ct );
      using JsonDocument? mapping = await _rest.SendAsync( HttpMethod.Get, $"{index}/_mapping?filter_path=*.mappings.properties.vector.index_options", null, JSON, false, ct );
      JsonElement primaries = stats!.RootElement.GetProperty( "_all" ).GetProperty( "primaries" );
      long vectors = 0;
      long graphBytes = 0;
      if( primaries.TryGetProperty( "dense_vector", out JsonElement dense ) )
      {
         vectors = dense.GetProperty( "value_count" ).GetInt64();
         graphBytes = dense.GetProperty( "off_heap" ).GetProperty( "total_vex_size_bytes" ).GetInt64();
      }

      return new IndexFacts( count.RootElement.GetProperty( "count" ).GetInt64(), primaries.GetProperty( "segments" ).GetProperty( "count" ).GetInt64(),
         vectors, graphBytes, DeclaredIndexOptions( mapping, index ) );
   }

   /// <summary>
   /// The index_options the mapping declares for the vector field, as short text such as "hnsw m=16 ef_construction=128".
   /// </summary>
   /// <param name="mapping">The filtered _mapping answer.</param>
   /// <param name="index">Index name.</param>
   /// <returns>The text, or "none declared".</returns>
   private static string DeclaredIndexOptions( JsonDocument? mapping, string index )
   {
      if( mapping != null
         && mapping.RootElement.TryGetProperty( index, out JsonElement root )
         && root.TryGetProperty( "mappings", out JsonElement mappings )
         && mappings.TryGetProperty( "properties", out JsonElement properties )
         && properties.TryGetProperty( "vector", out JsonElement vector )
         && vector.TryGetProperty( "index_options", out JsonElement options )
         && options.TryGetProperty( "type", out JsonElement type ) )
      {
         string m = options.TryGetProperty( "m", out JsonElement mv ) ? $" m={mv.GetInt32()}" : string.Empty;
         string ef = options.TryGetProperty( "ef_construction", out JsonElement ev ) ? $" ef_construction={ev.GetInt32()}" : string.Empty;
         return $"{type.GetString()}{m}{ef}";
      }

      return "none declared";
   }

   /// <summary>
   /// True when the engine shows one finished segment that holds every vector but has no graph: there is
   /// nothing left to wait for, so the collection is below the size at which Lucene builds a graph.
   /// </summary>
   /// <param name="facts">What the engine reported.</param>
   /// <returns>True when no graph can come.</returns>
   private static bool IsFinishedWithoutGraph( IndexFacts facts )
   {
      return facts.Documents > 0 && facts.Segments == 1 && facts.Vectors == facts.Documents && facts.GraphBytes == 0;
   }

   /// <summary>
   /// Turns the facts into the index state. Ready needs one segment, every document's vector in it and a
   /// graph present. Several segments are never ready here, because the statistics do not say which segment
   /// has a graph and which is scanned. An empty index is ready: nothing is waiting to be indexed.
   /// </summary>
   /// <param name="facts">What the engine reported.</param>
   /// <returns>The state, with the evidence in its detail.</returns>
   private static IndexState Interpret( IndexFacts facts )
   {
      if( facts.Documents == 0 )
      {
         return new IndexState( true, 0, 0, "no vectors stored, nothing to index (_count 0)" );
      }

      string seen = $"{facts.Segments} segment(s), {facts.Vectors:N0} vectors, total_vex_size_bytes {facts.GraphBytes:N0} (_stats dense_vector), index_options {facts.IndexOptions}";
      if( facts.Segments != 1 || facts.Vectors != facts.Documents )
      {
         return new IndexState( false, null, facts.Documents, $"not merged to one segment yet: {seen}, {facts.Documents:N0} searchable documents; the statistics do not say which segment has a graph, so graph coverage cannot be counted" );
      }

      if( facts.GraphBytes > 0 )
      {
         return new IndexState( true, facts.Vectors, facts.Documents, $"HNSW graph covers {facts.Vectors:N0} of {facts.Documents:N0} vectors: {seen}; force-merged to one segment on purpose so one graph answers every search" );
      }

      return new IndexState( false, 0, facts.Documents, $"NO HNSW GRAPH, searches scan all {facts.Documents:N0} vectors: {seen}; Lucene builds no graph for a segment this small (measured at 1,024 dimensions: 1,042 vectors none, 1,043 vectors a graph), so default search equals exact search" );
   }

   /// <summary>
   /// The numbers read from Elasticsearch that the index state is built from.
   /// </summary>
   /// <param name="Documents">Searchable documents (_count).</param>
   /// <param name="Segments">Segment count of the primary shard (_stats segments.count).</param>
   /// <param name="Vectors">Vectors in the searchable segments (dense_vector value_count).</param>
   /// <param name="GraphBytes">Bytes of HNSW graph across the segments (total_vex_size_bytes).</param>
   /// <param name="IndexOptions">The index_options the mapping declares for the vector field.</param>
   private sealed record IndexFacts( long Documents, long Segments, long Vectors, long GraphBytes, string IndexOptions );

   #endregion Private Methods
}
