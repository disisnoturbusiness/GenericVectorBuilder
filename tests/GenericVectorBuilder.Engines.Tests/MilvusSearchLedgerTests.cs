using System.Globalization;
using System.Text.RegularExpressions;
using GenericVectorBuilder.Core.Contracts;
using GenericVectorBuilder.Engines.Common;
using GenericVectorBuilder.Engines.Sinks;
using Xunit.Abstractions;

namespace GenericVectorBuilder.Engines.Tests;

/// <summary>
/// The arithmetic and wording of the Milvus search ledger, without a server. The metric lines are
/// copied from a real Milvus 2.6.25 /metrics page (label order and all).
/// </summary>
public sealed class MilvusSearchLedgerTests
{
   #region Data Members

   private const string METRICS = @"# HELP milvus_querynode_sq_req_count count of search / query request
# TYPE milvus_querynode_sq_req_count counter
milvus_querynode_sq_req_count{collection_id=""469537245757016024"",node_id=""13"",query_type=""search"",scope=""FromLeader"",status=""success""} 5
milvus_querynode_sq_req_count{collection_id=""469537245757016024"",node_id=""13"",query_type=""search"",scope=""OnLeader"",status=""fail""} 12
milvus_querynode_sq_req_count{collection_id=""469537245757016024"",node_id=""13"",query_type=""search"",scope=""OnLeader"",status=""success""} 7
milvus_querynode_sq_req_count{collection_id=""469537245757016024"",node_id=""13"",query_type=""query"",scope=""OnLeader"",status=""success""} 40
milvus_querynode_sq_req_count{collection_id=""999"",node_id=""13"",query_type=""search"",scope=""OnLeader"",status=""success""} 1000
# TYPE milvus_querynode_sq_segment_latency_count counter
milvus_querynode_sq_segment_latency_count{node_id=""13"",query_type=""query"",segment_state=""Sealed""} 348
milvus_querynode_sq_segment_latency_count{node_id=""13"",query_type=""search"",segment_state=""Growing""} 1
milvus_querynode_sq_segment_latency_count{node_id=""13"",query_type=""search"",segment_state=""Sealed""} 18
milvus_other_metric{query_type=""search""} 77
";

   #endregion Data Members

   #region Public Methods

   /// <summary>
   /// Only search counters count: not query counters, not another collection's requests, not the
   /// FromLeader copy of a request, not failed requests.
   /// </summary>
   [Fact]
   public void Parse_ReadsOnlyTheSearchCountersOfThisCollection()
   {
      MilvusSearchCounters counters = MilvusSearchLedger.Parse( METRICS, 469537245757016024 );
      Assert.Equal( 18, counters.SealedSegmentSearches );
      Assert.Equal( 1, counters.GrowingSegmentSearches );
      Assert.Equal( 7, counters.CollectionRequests );
   }

   /// <summary>
   /// A counter appears only after its first event, so a page without them reads as zero.
   /// </summary>
   [Fact]
   public void Parse_MissingCountersAreZero()
   {
      MilvusSearchCounters counters = MilvusSearchLedger.Parse( "# HELP nothing\nmilvus_proxy_other 3\n", 1 );
      Assert.Equal( new MilvusSearchCounters( 0, 0, 0 ), counters );
   }

   /// <summary>
   /// Searches that all ran on the sealed segment, with the same segments at both readings, are clean.
   /// </summary>
   [Fact]
   public void Evaluate_CleanWindow_HasNoProblems()
   {
      MilvusLedgerReading reading = MilvusSearchLedger.Evaluate( Start(), new MilvusSearchCounters( 130, 0, 120 ), 200, "seg1", "searchParams.params.ef=100" );
      Assert.Empty( reading.Problems );
      Assert.Contains( "the sink sent 100 searches (searchParams.params.ef=100)", reading.Detail );
      Assert.Contains( "counted 100 search requests", reading.Detail );
      Assert.Contains( "Sealed +100 and Growing +0", reading.Detail );
      Assert.Contains( "the same sealed segments", reading.Detail );
   }

   /// <summary>
   /// A window with no searches is clean: there is nothing to account for yet.
   /// </summary>
   [Fact]
   public void Evaluate_NoSearches_IsClean()
   {
      MilvusLedgerReading reading = MilvusSearchLedger.Evaluate( Start(), new MilvusSearchCounters( 30, 0, 20 ), 100, "seg1", "p" );
      Assert.Empty( reading.Problems );
      Assert.Contains( "the sink sent 0 searches", reading.Detail );
   }

   /// <summary>
   /// Any segment search on a growing segment since the start is a problem.
   /// </summary>
   [Fact]
   public void Evaluate_GrowingSearches_AreAProblem()
   {
      MilvusLedgerReading reading = MilvusSearchLedger.Evaluate( Start(), new MilvusSearchCounters( 130, 4, 120 ), 200, "seg1", "p" );
      Assert.Contains( reading.Problems, p => p.Contains( "4 segment searches ran on growing" ) );
      Assert.Contains( "PROBLEM", reading.Detail );
   }

   /// <summary>
   /// Concurrent searches are merged by the query node, so fewer Sealed segment searches than
   /// searches sent is normal as long as every request was counted (measured: 170 to 175 of 200
   /// from 8 threads).
   /// </summary>
   [Fact]
   public void Evaluate_MergedConcurrentSearches_AreNotAProblem()
   {
      MilvusLedgerReading reading = MilvusSearchLedger.Evaluate( Start(), new MilvusSearchCounters( 105, 0, 120 ), 200, "seg1", "p" );
      Assert.Empty( reading.Problems );
      Assert.Contains( "Sealed +75", reading.Detail );
   }

   /// <summary>
   /// Searches were sent but no segment search at all was counted on a sealed segment: they ran
   /// somewhere else (or nowhere), so the index is not shown to be used.
   /// </summary>
   [Fact]
   public void Evaluate_NoSealedSearchesAtAll_IsAProblem()
   {
      MilvusLedgerReading reading = MilvusSearchLedger.Evaluate( Start(), new MilvusSearchCounters( 30, 0, 120 ), 200, "seg1", "p" );
      Assert.Contains( reading.Problems, p => p.Contains( "counted no segment search on a sealed segment" ) );
   }

   /// <summary>
   /// Fewer search requests for the collection than the sink sent means searches did not reach it.
   /// </summary>
   [Fact]
   public void Evaluate_FewerRequestsThanSent_IsAProblem()
   {
      MilvusLedgerReading reading = MilvusSearchLedger.Evaluate( Start(), new MilvusSearchCounters( 130, 0, 90 ), 200, "seg1", "p" );
      Assert.Contains( reading.Problems, p => p.Contains( "counted only 70 search requests for this collection" ) );
   }

   /// <summary>
   /// A different segment set at the end than at the start leaves the window unproven.
   /// </summary>
   [Fact]
   public void Evaluate_ChangedSegments_AreAProblem()
   {
      MilvusLedgerReading reading = MilvusSearchLedger.Evaluate( Start(), new MilvusSearchCounters( 130, 0, 120 ), 200, "seg2", "p" );
      Assert.Contains( reading.Problems, p => p.Contains( "changed since the index finished" ) );
      Assert.Contains( "differ between the readings", reading.Detail );
   }

   /// <summary>
   /// A counter that went down means Milvus restarted, which the ledger cannot account for.
   /// </summary>
   [Fact]
   public void Evaluate_CounterWentDown_IsAProblem()
   {
      MilvusLedgerReading reading = MilvusSearchLedger.Evaluate( Start(), new MilvusSearchCounters( 2, 0, 1 ), 200, "seg1", "p" );
      Assert.Contains( reading.Problems, p => p.Contains( "counter went down" ) );
   }

   /// <summary>
   /// The fingerprint ignores the order segments and builds are listed in.
   /// </summary>
   [Fact]
   public void Fingerprint_IsOrderIndependent()
   {
      string a = MilvusSearchLedger.Fingerprint( [( "2", "Sealed", ["b", "a"] ), ( "1", "Growing", [] )] );
      string b = MilvusSearchLedger.Fingerprint( [( "1", "Growing", [] ), ( "2", "Sealed", ["a", "b"] )] );
      Assert.Equal( a, b );
      Assert.Equal( "1:Growing:[],2:Sealed:[a+b]", a );
      Assert.Equal( "none", MilvusSearchLedger.Fingerprint( [] ) );
   }

   #endregion Public Methods

   #region Private Methods

   /// <summary>
   /// A ledger start with 30 sealed searches, 20 requests, 100 searches sent, one segment.
   /// </summary>
   /// <returns>The start.</returns>
   private static MilvusLedgerStart Start()
   {
      return new MilvusLedgerStart( new MilvusSearchCounters( 30, 0, 20 ), 100, "seg1" );
   }

   #endregion Private Methods
}

/// <summary>
/// Proves against the real Milvus container (deploy/engines/milvus.compose.yaml must be up) that a
/// state read taken after the searches shows they ran on the sealed, indexed segment, and that it
/// says so when they did not. The numbers the ledger reports are checked against the query node's
/// own /metrics page read separately.
/// Why one class and a named collection: the segment counters are node-wide, so searches from
/// another test running at the same moment would land in this test's window. Every Milvus test
/// class is in the same xunit collection, which runs them one after another.
/// </summary>
[Collection( "MilvusNode" )]
[Trait( "Category", "Live" )]
public sealed class MilvusSearchLedgerLiveTests
{
   #region Data Members

   private const int DIMENSION = 1024;
   private const int SEARCHERS = 8;
   private const int SEARCHES_EACH = 25;
   private const string MANAGEMENT = "http://127.0.0.1:9091/";
   private static readonly Regex SENT = new( @"the sink sent (\d+) searches", RegexOptions.Compiled | RegexOptions.CultureInvariant );
   private static readonly Regex REQUESTS = new( @"counted (\d+) search requests", RegexOptions.Compiled | RegexOptions.CultureInvariant );
   private static readonly Regex SEALED = new( @"Sealed \+(\d+)", RegexOptions.Compiled | RegexOptions.CultureInvariant );
   private static readonly Regex GROWING = new( @"Growing \+(\d+)", RegexOptions.Compiled | RegexOptions.CultureInvariant );

   private readonly ITestOutputHelper _output;

   #endregion Data Members

   #region Constructor

   /// <summary>
   /// Receives the xunit output writer.
   /// </summary>
   /// <param name="output">Where the evidence is printed.</param>
   public MilvusSearchLedgerLiveTests( ITestOutputHelper output )
   {
      _output = output;
   }

   #endregion Constructor

   #region Public Methods

   /// <summary>
   /// The benchmark's shape: finish the load, search from 8 concurrent searchers, read the state.
   /// The read must be Ready, name type HNSW, count every search as a request for the collection,
   /// show segment searches on the sealed segment and none on a growing one, and the query node's
   /// own counters, read separately, must agree with what the ledger printed.
   /// </summary>
   [Fact]
   public async Task TimedSearches_AreProvenToUseTheHnswIndex()
   {
      string collection = EnginesAReadinessData.BenchCollection();
      using var sink = new MilvusSink();
      List<VectorRecord> records = EnginesAReadinessData.Records( 2000, DIMENSION, seed: 41 );
      try
      {
         await sink.EnsureCollectionAsync( collection, DIMENSION, CancellationToken.None );
         await sink.UpsertAsync( collection, records, CancellationToken.None );
         string note = await sink.FinishLoadAsync( collection, CancellationToken.None );
         _output.WriteLine( $"FinishLoadAsync: {note}" );
         Assert.Contains( "search ledger started", note );
         Assert.Contains( "type HNSW", note );

         IndexState before = await sink.GetIndexStateAsync( collection, CancellationToken.None );
         _output.WriteLine( $"before the searches: Ready={before.Ready}, {before.Detail}" );
         Assert.True( before.Ready, before.Detail );
         Assert.Contains( "the sink sent 0 searches", before.Detail );

         double sealedBefore = await NodeSearchesAsync( "Sealed" );
         double growingBefore = await NodeSearchesAsync( "Growing" );
         await Task.WhenAll( Enumerable.Range( 0, SEARCHERS ).Select( worker => Task.Run( async () =>
         {
            for( int i = 0; i < SEARCHES_EACH; i++ )
            {
               IReadOnlyList<SearchHit> hits = await sink.SearchAsync( collection, records[( worker * SEARCHES_EACH + i ) % records.Count].Vector, 10, CancellationToken.None );
               Assert.Equal( 10, hits.Count );
            }
         } ) ) );
         int total = SEARCHERS * SEARCHES_EACH;

         IndexState after = await sink.GetIndexStateAsync( collection, CancellationToken.None );
         _output.WriteLine( $"after {total} searches from {SEARCHERS} searchers: Ready={after.Ready}, {after.Detail}" );
         Assert.True( after.Ready, after.Detail );
         Assert.Equal( total, Number( SENT, after.Detail ) );
         Assert.Equal( total, Number( REQUESTS, after.Detail ) );
         Assert.True( Number( SEALED, after.Detail ) > 0, "the searches must show up as segment searches on the sealed segment" );
         Assert.Equal( 0, Number( GROWING, after.Detail ) );
         Assert.Contains( "searchParams.params.ef=100", after.Detail );
         Assert.Contains( "the same sealed segments with the same loaded index builds", after.Detail );

         double sealedDelta = await NodeSearchesAsync( "Sealed" ) - sealedBefore;
         double growingDelta = await NodeSearchesAsync( "Growing" ) - growingBefore;
         _output.WriteLine( $"query node /metrics read separately: Sealed +{sealedDelta}, Growing +{growingDelta}" );
         Assert.Equal( Number( SEALED, after.Detail ), sealedDelta );
         Assert.Equal( 0, growingDelta );
      }
      finally
      {
         await sink.DropCollectionAsync( collection, CancellationToken.None );
      }
   }

   /// <summary>
   /// The bench command never loads, so a fresh sink's first read starts the ledger and the next
   /// read, after searches, accounts for them.
   /// </summary>
   [Fact]
   public async Task FreshSink_StartsTheLedgerOnItsFirstReadyRead()
   {
      string collection = EnginesAReadinessData.BenchCollection();
      using var loader = new MilvusSink();
      using var reader = new MilvusSink();
      List<VectorRecord> records = EnginesAReadinessData.Records( 500, DIMENSION, seed: 42 );
      try
      {
         await loader.EnsureCollectionAsync( collection, DIMENSION, CancellationToken.None );
         await loader.UpsertAsync( collection, records, CancellationToken.None );
         await loader.FinishLoadAsync( collection, CancellationToken.None );

         IndexState first = await reader.GetIndexStateAsync( collection, CancellationToken.None );
         _output.WriteLine( $"first read: Ready={first.Ready}, {first.Detail}" );
         Assert.True( first.Ready, first.Detail );
         Assert.Contains( "search ledger started by this read", first.Detail );

         for( int i = 0; i < 12; i++ )
         {
            await reader.SearchAsync( collection, records[i].Vector, 10, CancellationToken.None );
         }

         IndexState second = await reader.GetIndexStateAsync( collection, CancellationToken.None );
         _output.WriteLine( $"second read: Ready={second.Ready}, {second.Detail}" );
         Assert.True( second.Ready, second.Detail );
         Assert.Equal( 12, Number( SENT, second.Detail ) );
      }
      finally
      {
         await loader.DropCollectionAsync( collection, CancellationToken.None );
      }
   }

   /// <summary>
   /// Control for the failure path: after the index is ready, new rows land in a growing segment and
   /// searches run on it. The state read must say not ready and must show the growing searches.
   /// </summary>
   [Fact]
   public async Task SearchesThatReachGrowingRows_AreFlagged()
   {
      string collection = EnginesAReadinessData.BenchCollection();
      using var sink = new MilvusSink();
      List<VectorRecord> records = EnginesAReadinessData.Records( 600, DIMENSION, seed: 43 );
      try
      {
         await sink.EnsureCollectionAsync( collection, DIMENSION, CancellationToken.None );
         await sink.UpsertAsync( collection, records.Take( 500 ).ToList(), CancellationToken.None );
         await sink.FinishLoadAsync( collection, CancellationToken.None );

         await sink.UpsertAsync( collection, records.Skip( 500 ).ToList(), CancellationToken.None );
         for( int i = 0; i < 6; i++ )
         {
            await sink.SearchAsync( collection, records[500 + i].Vector, 10, CancellationToken.None );
         }

         IndexState state = await sink.GetIndexStateAsync( collection, CancellationToken.None );
         _output.WriteLine( $"after searching over unsealed rows: Ready={state.Ready}, {state.Detail}" );
         Assert.False( state.Ready, "searches over a growing segment must not be reported as using the index" );
         Assert.True( Number( GROWING, state.Detail ) > 0, state.Detail );
         Assert.Contains( "PROBLEM", state.Detail );
      }
      finally
      {
         await sink.DropCollectionAsync( collection, CancellationToken.None );
      }
   }

   /// <summary>
   /// A management port nobody listens on makes the state not ready with a message that names the
   /// setting; it never reports the searches as proven.
   /// </summary>
   [Fact]
   public async Task UnreadableManagementPort_IsNeverReady()
   {
      string collection = EnginesAReadinessData.BenchCollection();
      using var loader = new MilvusSink();
      using var blind = new MilvusSink( new MilvusSinkOptions( ManagementUrl: "http://127.0.0.1:1" ) );
      try
      {
         await loader.EnsureCollectionAsync( collection, DIMENSION, CancellationToken.None );
         await loader.UpsertAsync( collection, EnginesAReadinessData.Records( 50, DIMENSION, seed: 44 ), CancellationToken.None );
         IndexState state = await blind.GetIndexStateAsync( collection, CancellationToken.None );
         _output.WriteLine( $"state: Ready={state.Ready}, {state.Detail}" );
         Assert.False( state.Ready );
         Assert.Contains( "ManagementUrl", state.Detail );
      }
      finally
      {
         await loader.DropCollectionAsync( collection, CancellationToken.None );
      }
   }

   #endregion Public Methods

   #region Private Methods

   /// <summary>
   /// Reads the query node's node-wide search counter for one segment state straight from the
   /// management port, without going through the sink.
   /// </summary>
   /// <param name="state">Sealed or Growing.</param>
   /// <returns>The counter value (0 when it does not exist yet).</returns>
   private static async Task<double> NodeSearchesAsync( string state )
   {
      using var http = new HttpClient { Timeout = TimeSpan.FromSeconds( 30 ) };
      string metrics = await http.GetStringAsync( MANAGEMENT + "metrics" );
      double sum = 0;
      foreach( Match match in Regex.Matches( metrics, "^milvus_querynode_sq_segment_latency_count\\{[^}]*query_type=\"search\"[^}]*segment_state=\"" + state + "\"[^}]*\\} (\\S+)", RegexOptions.Multiline ) )
      {
         sum += double.Parse( match.Groups[1].Value, CultureInfo.InvariantCulture );
      }

      return sum;
   }

   /// <summary>
   /// The first number a pattern captures from a text.
   /// </summary>
   /// <param name="pattern">A pattern with one capture group of digits.</param>
   /// <param name="text">The text.</param>
   /// <returns>The number.</returns>
   private static long Number( Regex pattern, string text )
   {
      Match match = pattern.Match( text );
      Assert.True( match.Success, $"{pattern} not found in: {text}" );
      return long.Parse( match.Groups[1].Value, CultureInfo.InvariantCulture );
   }

   #endregion Private Methods
}
