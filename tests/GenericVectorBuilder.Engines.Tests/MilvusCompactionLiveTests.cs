using System.Diagnostics;
using System.Text.Json;
using System.Text.RegularExpressions;
using GenericVectorBuilder.Core.Contracts;
using GenericVectorBuilder.Engines.Common;
using GenericVectorBuilder.Engines.Sinks;
using Xunit.Abstractions;

namespace GenericVectorBuilder.Engines.Tests;

/// <summary>
/// Proves against a real Milvus (the container of deploy/engines/milvus.compose.yaml, or another
/// one named by GVB_MILVUS_URL and GVB_MILVUS_MANAGEMENT_URL) that after FinishLoadAsync the segment
/// layout is final: a collection loaded the way the benchmark loads it (upserts) is searched for
/// three minutes, one search after another, and neither the sink's ledger nor an independent read
/// of the data coordinator and the query node shows a compaction in that time.
/// Why three minutes: measured 2026-10-04, Milvus compacted a freshly loaded collection 80 to 100
/// seconds after the load (a level-zero compaction at 48 s, a mix compaction at 98 s), so any
/// window shorter than that after a premature "ready" can pass by luck.
/// </summary>
[Collection( "MilvusNode" )]
[Trait( "Category", "Live" )]
public sealed class MilvusCompactionLiveTests
{
   #region Data Members

   private const int DIMENSION = 1024;
   private const int COUNT = 524;
   private const int LOOP_SECONDS = 180;
   private const int PACE_MILLISECONDS = 10;
   private static readonly Regex SENT = new( @"the sink sent (\d+) searches", RegexOptions.Compiled | RegexOptions.CultureInvariant );

   private readonly ITestOutputHelper _output;

   #endregion Data Members

   #region Constructor

   /// <summary>
   /// Receives the xunit output writer.
   /// </summary>
   /// <param name="output">Where timings and the engine's evidence are printed.</param>
   public MilvusCompactionLiveTests( ITestOutputHelper output )
   {
      _output = output;
   }

   #endregion Constructor

   #region Public Methods

   /// <summary>
   /// Loads 524 vectors with upserts (500 then 24, as the benchmark's loader batches), runs
   /// FinishLoadAsync, then searches for three minutes and requires: the state still ready with
   /// covered rows equal to stored rows, the ledger reporting no compaction and every search sent,
   /// and an independent read (the test's own HTTP calls, parsed here) showing the same live
   /// segments and the same compacted-away count before and after.
   /// </summary>
   [Fact]
   public async Task FinishLoad_LeavesALayoutThatStaysFinal_DuringAThreeMinuteSearchLoop()
   {
      MilvusSinkOptions options = LiveOptions();
      string collection = EnginesAReadinessData.BenchCollection();
      using var sink = new MilvusSink( options ) { Progress = line => _output.WriteLine( line ) };
      List<VectorRecord> records = EnginesAReadinessData.Records( COUNT, DIMENSION, seed: 51 );
      try
      {
         await sink.EnsureCollectionAsync( collection, DIMENSION, CancellationToken.None );
         await sink.UpsertAsync( collection, records, CancellationToken.None );
         var clock = Stopwatch.StartNew();
         string note = await sink.FinishLoadAsync( collection, CancellationToken.None );
         _output.WriteLine( $"FinishLoadAsync took {clock.Elapsed.TotalSeconds:F1} s. Note: {note}" );

         RawLayout before = await ReadRawLayoutAsync( options, "gvb_" + collection );
         _output.WriteLine( $"Independent read after FinishLoadAsync: {before}" );
         IndexState ready = await sink.GetIndexStateAsync( collection, CancellationToken.None );
         Assert.True( ready.Ready, ready.Detail );
         Assert.Equal( COUNT, ready.IndexedVectors );
         Assert.Equal( COUNT, ready.TotalVectors );

         ( int sent, int selfFirst ) = await SearchLoopAsync( sink, collection, records );
         _output.WriteLine( $"Search loop of {LOOP_SECONDS} s: {sent} searches, stored vector first in {selfFirst}" );

         RawLayout after = await ReadRawLayoutAsync( options, "gvb_" + collection );
         _output.WriteLine( $"Independent read after the loop: {after}" );
         IndexState state = await sink.GetIndexStateAsync( collection, CancellationToken.None );
         _output.WriteLine( $"Index state after the loop: Ready={state.Ready}, indexed {state.IndexedVectors} of {state.TotalVectors}, {state.Detail}" );

         Assert.Equal( before.LiveSegments, after.LiveSegments );
         Assert.Equal( before.CompactedAway, after.CompactedAway );
         Assert.Equal( before.ServedSegments, after.ServedSegments );
         Assert.True( state.Ready, state.Detail );
         Assert.Equal( COUNT, state.IndexedVectors );
         Assert.Contains( "segments compacted away +0", state.Detail );
         Assert.Contains( "Growing +0", state.Detail );
         Assert.Equal( sent, int.Parse( SENT.Match( state.Detail ).Groups[1].Value ) );
         Assert.True( selfFirst >= sent * 0.99, $"only {selfFirst} of {sent} stored vectors found themselves first" );
      }
      finally
      {
         await sink.DropCollectionAsync( collection, CancellationToken.None );
      }
   }

   #endregion Public Methods

   #region Private Methods

   /// <summary>
   /// The options of the Milvus under test: the local container, or the addresses in
   /// GVB_MILVUS_URL and GVB_MILVUS_MANAGEMENT_URL.
   /// </summary>
   /// <returns>The options.</returns>
   private static MilvusSinkOptions LiveOptions()
   {
      var defaults = new MilvusSinkOptions();
      return defaults with
      {
         BaseUrl = Environment.GetEnvironmentVariable( "GVB_MILVUS_URL" ) ?? defaults.BaseUrl,
         ManagementUrl = Environment.GetEnvironmentVariable( "GVB_MILVUS_MANAGEMENT_URL" ) ?? defaults.ManagementUrl,
      };
   }

   /// <summary>
   /// Searches one stored vector after another for the length of the loop.
   /// </summary>
   /// <param name="sink">Sink under test.</param>
   /// <param name="collection">Loaded collection.</param>
   /// <param name="records">What was loaded.</param>
   /// <returns>How many searches ran and how many returned the stored vector first.</returns>
   private static async Task<( int Sent, int SelfFirst )> SearchLoopAsync( MilvusSink sink, string collection, List<VectorRecord> records )
   {
      var clock = Stopwatch.StartNew();
      int sent = 0;
      int selfFirst = 0;
      while( clock.Elapsed < TimeSpan.FromSeconds( LOOP_SECONDS ) )
      {
         VectorRecord query = records[sent % records.Count];
         IReadOnlyList<SearchHit> hits = await sink.SearchAsync( collection, query.Vector, 10, CancellationToken.None );
         sent++;
         selfFirst += hits.Count > 0 && hits[0].ChunkId == query.Chunk.ChunkId ? 1 : 0;
         await Task.Delay( PACE_MILLISECONDS );
      }

      return ( sent, selfFirst );
   }

   /// <summary>
   /// What the independent read saw.
   /// </summary>
   /// <param name="LiveSegments">The data coordinator's segments that are not dropped, sorted, as id:state:level.</param>
   /// <param name="CompactedAway">How many segments the data coordinator lists as compacted.</param>
   /// <param name="ServedSegments">The query node's segments with their state and loaded index builds, sorted.</param>
   private sealed record RawLayout( string LiveSegments, int CompactedAway, string ServedSegments );

   /// <summary>
   /// Reads the data coordinator and the query node with the test's own HTTP calls and its own
   /// parsing, apart from the sink's.
   /// </summary>
   /// <param name="options">Where Milvus is.</param>
   /// <param name="collectionName">Milvus collection name.</param>
   /// <returns>The raw layout.</returns>
   private static async Task<RawLayout> ReadRawLayoutAsync( MilvusSinkOptions options, string collectionName )
   {
      using var http = new HttpClient { Timeout = TimeSpan.FromSeconds( 30 ) };
      using var body = new StringContent( JsonSerializer.Serialize( new { collectionName } ), System.Text.Encoding.UTF8, "application/json" );
      using HttpResponseMessage described = await http.PostAsync( options.BaseUrl.TrimEnd( '/' ) + "/v2/vectordb/collections/describe", body );
      using JsonDocument describedJson = JsonDocument.Parse( await described.Content.ReadAsStringAsync() );
      long id = describedJson.RootElement.GetProperty( "data" ).GetProperty( "collectionID" ).GetInt64();
      string management = options.ManagementUrl.TrimEnd( '/' );

      using JsonDocument dc = JsonDocument.Parse( await http.GetStringAsync( $"{management}/api/v1/_dc/segments?collection_id={id}&in=dc" ) );
      var live = new List<string>();
      int compacted = 0;
      foreach( JsonElement s in dc.RootElement.ValueKind == JsonValueKind.Array ? dc.RootElement.EnumerateArray() : Enumerable.Empty<JsonElement>() )
      {
         compacted += s.TryGetProperty( "compacted", out JsonElement c ) && c.GetBoolean() ? 1 : 0;
         if( s.GetProperty( "state" ).GetString() != "Dropped" )
         {
            live.Add( $"{s.GetProperty( "segment_id" ).GetString()}:{s.GetProperty( "state" ).GetString()}:{( s.TryGetProperty( "level", out JsonElement l ) ? l.GetString() : "?" )}" );
         }
      }

      using JsonDocument qn = JsonDocument.Parse( await http.GetStringAsync( $"{management}/api/v1/_qn/segments?collection_id={id}&in=qn" ) );
      var served = qn.RootElement.EnumerateArray().Select( s =>
         $"{s.GetProperty( "segment_id" ).GetString()}:{s.GetProperty( "state" ).GetString()}:{( s.TryGetProperty( "index_fields", out JsonElement f ) ? string.Join( "+", f.EnumerateArray().Select( x => x.GetProperty( "build_id" ).GetString() ) ) : "no index" )}" ).ToList();
      live.Sort( StringComparer.Ordinal );
      served.Sort( StringComparer.Ordinal );
      return new RawLayout( string.Join( ",", live ), compacted, string.Join( ",", served ) );
   }

   #endregion Private Methods
}

/// <summary>
/// Loads 100,000 random 1024-dimension vectors into Milvus the way the benchmark loads them (500-row
/// upserts), runs FinishLoadAsync, and requires the layout to be final: ready with covered rows equal
/// to stored rows, no delete-log segment left, and no compaction in the searches that follow.
/// Why a scale test: at 524 rows there is one segment, and the claim "the delete records are applied
/// by Milvus on its own and one compaction then makes the layout final" has to hold for a load that
/// seals several segments and writes a delete log for every batch. Run with:
/// dotnet test --filter "Category=Scale&amp;FullyQualifiedName~MilvusCompactionScaleTests"
/// It also carries the Live trait, so a run that leaves out the live tests (Category!=Live) leaves
/// this ten-minute test out too.
/// </summary>
[Collection( "MilvusNode" )]
[Trait( "Category", "Live" )]
[Trait( "Category", "Scale" )]
public sealed class MilvusCompactionScaleTests
{
   #region Data Members

   private const int DIMENSION = 1024;
   private const int COUNT = 100_000;
   private const int SEARCHES = 200;

   private readonly ITestOutputHelper _output;

   #endregion Data Members

   #region Constructor

   /// <summary>
   /// Receives the xunit output writer.
   /// </summary>
   /// <param name="output">Where timings and the engine's evidence are printed.</param>
   public MilvusCompactionScaleTests( ITestOutputHelper output )
   {
      _output = output;
   }

   #endregion Constructor

   #region Public Methods

   /// <summary>
   /// Loads 100k vectors, runs FinishLoadAsync, prints the timings and the engine's evidence, then
   /// runs 200 searches and requires the state to stay ready with no compaction in the ledger.
   /// </summary>
   [Fact]
   public async Task FinishLoad_At100kRows_LeavesAFinalLayout()
   {
      var defaults = new MilvusSinkOptions();
      var options = defaults with
      {
         BaseUrl = Environment.GetEnvironmentVariable( "GVB_MILVUS_URL" ) ?? defaults.BaseUrl,
         ManagementUrl = Environment.GetEnvironmentVariable( "GVB_MILVUS_MANAGEMENT_URL" ) ?? defaults.ManagementUrl,
      };
      string collection = EnginesAReadinessData.BenchCollection();
      using var sink = new MilvusSink( options ) { Progress = line => _output.WriteLine( line ) };
      try
      {
         await sink.EnsureCollectionAsync( collection, DIMENSION, CancellationToken.None );
         var clock = Stopwatch.StartNew();
         List<VectorRecord> sample = new();
         for( int from = 0; from < COUNT; from += 5000 )
         {
            List<VectorRecord> chunk = EnginesAReadinessData.Records( 5000, DIMENSION, seed: 100 + from / 5000 );
            await sink.UpsertAsync( collection, chunk, CancellationToken.None );
            sample.Add( chunk[0] );
         }

         _output.WriteLine( $"Loaded {COUNT} x {DIMENSION} in {clock.Elapsed.TotalSeconds:F1} s" );
         clock.Restart();
         string note = await sink.FinishLoadAsync( collection, CancellationToken.None );
         _output.WriteLine( $"FinishLoadAsync took {clock.Elapsed.TotalSeconds:F1} s. Note: {note}" );

         int found = 0;
         for( int i = 0; i < SEARCHES; i++ )
         {
            VectorRecord query = sample[i % sample.Count];
            IReadOnlyList<SearchHit> hits = await sink.SearchAsync( collection, query.Vector, 10, CancellationToken.None );
            found += hits.Count > 0 && hits[0].ChunkId == query.Chunk.ChunkId ? 1 : 0;
         }

         IndexState state = await sink.GetIndexStateAsync( collection, CancellationToken.None );
         _output.WriteLine( $"After {SEARCHES} searches ({found} found themselves first): Ready={state.Ready}, indexed {state.IndexedVectors} of {state.TotalVectors}, {state.Detail}" );
         Assert.True( state.Ready, state.Detail );
         Assert.Equal( COUNT, state.IndexedVectors );
         Assert.Equal( COUNT, state.TotalVectors );
         Assert.Contains( "segments compacted away +0", state.Detail );
         Assert.Contains( "0 delete-log (L0) segment(s)", state.Detail );
      }
      finally
      {
         await sink.DropCollectionAsync( collection, CancellationToken.None );
      }
   }

   #endregion Public Methods
}
