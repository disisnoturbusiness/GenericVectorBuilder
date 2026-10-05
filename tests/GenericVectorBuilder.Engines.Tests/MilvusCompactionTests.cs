using System.Text.RegularExpressions;
using GenericVectorBuilder.Engines.Common;
using GenericVectorBuilder.Engines.Sinks;

namespace GenericVectorBuilder.Engines.Tests;

/// <summary>
/// Drives the Milvus sink's compaction step and its readiness judgement against a scripted fake
/// server (<see cref="MilvusFakeServer"/>), so no container is needed and no real compaction has to
/// be waited for. The scripts reproduce what was measured on Milvus 2.6.25 on 2026-10-04: delete
/// records from upserts waiting as a level-zero segment, a compaction that hands over from the old
/// segment to a new one with both served for a while, a new segment served without its index until
/// the build is done, and a manual compaction that rewrites a segment even when there is nothing
/// to purge.
/// </summary>
public sealed class MilvusCompactionTests
{
   #region Data Members

   private const string COLLECTION = "gvbbench_fake";

   #endregion Data Members

   #region Public Methods

   /// <summary>
   /// The baseline: one sealed segment with its index loaded, covering exactly the stored rows.
   /// </summary>
   [Fact]
   public async Task GetIndexState_IsReady_WhenCoveredRowsEqualStoredRows()
   {
      using MilvusFakeServer server = OneSegmentServer();
      using var sink = new MilvusSink( FastOptions( server ) );
      IndexState state = await sink.GetIndexStateAsync( COLLECTION, CancellationToken.None );
      Assert.True( state.Ready, state.Detail );
      Assert.Equal( 524, state.IndexedVectors );
      Assert.Equal( 524, state.TotalVectors );
      Assert.Contains( "1 sealed segment(s) with the index loaded covering 524 rows against 524 stored rows", state.Detail );
   }

   /// <summary>
   /// The consolidated report reads the layout out of this text with the first match of
   /// "N [sealed ]segment(s) [with the index loaded] [covering R rows]" (Bench.Stats.SegmentLayout), so
   /// that phrase must stay the first segment count in the detail and must keep "covering R rows"
   /// whole, with the stored rows after it. The pattern below is a copy of that reader's.
   /// </summary>
   [Fact]
   public async Task GetIndexState_Detail_KeepsTheFirstSegmentPhraseTheConsolidatedReportReads()
   {
      using MilvusFakeServer server = OneSegmentServer();
      using var sink = new MilvusSink( FastOptions( server ) );
      IndexState state = await sink.GetIndexStateAsync( COLLECTION, CancellationToken.None );
      var reader = new Regex( @"(?<n>\d[\d,]*)\s+(?<kind>sealed\s+|growing\s+)?segment(?:s|\(s\))?(?!\w)(?!\s+search)(?:\s+with the index loaded)?(?:\s+covering\s+(?<rows>\d[\d,]*)\s+rows)?", RegexOptions.IgnoreCase );
      Match first = reader.Match( state.Detail );
      Assert.True( first.Success, state.Detail );
      Assert.Equal( "1", first.Groups["n"].Value );
      Assert.Equal( "sealed", first.Groups["kind"].Value.Trim() );
      Assert.Equal( "524", first.Groups["rows"].Value );
   }

   /// <summary>
   /// During a compaction the query node serves the old and the new segment together: 2 sealed
   /// segments covering 1,048 rows for 524 stored (the reading of the 2026-10-04 smoke run). The old
   /// check "covered at least stored" called that ready. It must not, and the detail must show the
   /// two numbers.
   /// </summary>
   [Fact]
   public async Task GetIndexState_IsNotReady_WhenTwoGenerationsOfASegmentAreServedTogether()
   {
      using MilvusFakeServer server = OneSegmentServer();
      server.Update( s =>
      {
         s.QueryNode.Add( new FakeQueryNodeSegment( "S2", "Sealed", 524, true ) );
         s.Datacoord[0] = new FakeDatacoordSegment( "S1", "Dropped", "L1", 524, true, true );
         s.Datacoord.Add( new FakeDatacoordSegment( "S2", "Flushed", "L1", 524, true, false ) );
      } );
      using var sink = new MilvusSink( FastOptions( server ) );
      IndexState state = await sink.GetIndexStateAsync( COLLECTION, CancellationToken.None );
      Assert.False( state.Ready, state.Detail );
      Assert.Equal( 1048, state.IndexedVectors );
      Assert.Equal( 524, state.TotalVectors );
      Assert.Contains( "2 sealed segment(s) with the index loaded covering 1048 rows against 524 stored rows", state.Detail );
      Assert.Contains( "compaction is handing over", state.Detail );
   }

   /// <summary>
   /// Fewer covered rows than stored is not ready either (the index loaded on part of the rows).
   /// </summary>
   [Fact]
   public async Task GetIndexState_IsNotReady_WhenCoveredRowsAreFewerThanStored()
   {
      using MilvusFakeServer server = OneSegmentServer();
      server.Update( s => s.Stored = 600 );
      using var sink = new MilvusSink( FastOptions( server ) );
      IndexState state = await sink.GetIndexStateAsync( COLLECTION, CancellationToken.None );
      Assert.False( state.Ready, state.Detail );
      Assert.Contains( "covering 524 rows against 600 stored rows", state.Detail );
   }

   /// <summary>
   /// The full sequence: Milvus applies the delete records by itself (the level-zero segment goes
   /// away), the sink then asks for one compaction, waits for the job, and does not return while the
   /// new segment is unindexed, while old and new are served together, or until the old one is
   /// released. Asserts the request came after the delete-log segment was gone and that exactly one
   /// was made.
   /// </summary>
   [Fact]
   public async Task FinishLoad_TriggersOneCompaction_AndWaitsForTheLayoutToBeFinal()
   {
      using MilvusFakeServer server = OneSegmentServer();
      int readsAtCompact = -1;
      int mark = 0;
      server.Update( s =>
      {
         s.Datacoord.Add( new FakeDatacoordSegment( "L0a", "Flushed", "L0", 0, false, false ) );
         s.CompactionState = call => call <= 2 ? "Executing" : "Completed";
         s.OnCompact = x =>
         {
            readsAtCompact = x.ManagementReads;
            mark = x.ManagementReads;
            x.Datacoord[0] = new FakeDatacoordSegment( "S1", "Dropped", "L1", 524, true, true );
            x.Datacoord.Add( new FakeDatacoordSegment( "S2", "Flushed", "L1", 524, false, false ) );
            return 7;
         };
         s.OnManagementRead = x => Script( x, mark );
      } );
      using var sink = new MilvusSink( FastOptions( server ) );
      string note = await sink.FinishLoadAsync( COLLECTION, CancellationToken.None );

      Assert.Equal( 1, server.CompactCalls );
      Assert.True( readsAtCompact >= 8, $"the compaction was asked for at query node read {readsAtCompact}, before the delete-log segment was applied (read 8)" );
      Assert.Contains( "1 job(s) accepted by Milvus", note );
      Assert.Contains( "1 sealed segment(s) with the index loaded covering 524 rows against 524 stored rows", note );
      IndexState after = await sink.GetIndexStateAsync( COLLECTION, CancellationToken.None );
      Assert.True( after.Ready, after.Detail );
      Assert.Contains( "S2", server.QueryNode.Select( q => q.Id ) );
      Assert.DoesNotContain( "S1", server.QueryNode.Select( q => q.Id ) );
   }

   /// <summary>
   /// A manual compaction rewrites a finished segment under a new id even when nothing needs
   /// purging (measured 2026-10-04), so asking again after every layout change would never end. The
   /// sink must ask once and finish.
   /// </summary>
   [Fact]
   public async Task FinishLoad_AsksOnlyOnce_WhenEveryRequestRewritesTheSegment()
   {
      using MilvusFakeServer server = OneSegmentServer();
      int generation = 1;
      server.Update( s => s.OnCompact = x =>
      {
         string old = $"S{generation}";
         generation++;
         string next = $"S{generation}";
         x.QueryNode[0] = new FakeQueryNodeSegment( next, "Sealed", 524, true );
         x.Datacoord[0] = new FakeDatacoordSegment( old, "Dropped", "L1", 524, true, true );
         x.Datacoord.Add( new FakeDatacoordSegment( next, "Flushed", "L1", 524, true, false ) );
         return 9;
      } );
      using var sink = new MilvusSink( FastOptions( server ) );
      string note = await sink.FinishLoadAsync( COLLECTION, CancellationToken.None );
      Assert.Equal( 1, server.CompactCalls );
      Assert.Contains( "1 job(s) accepted by Milvus", note );
   }

   /// <summary>
   /// When Milvus has nothing to compact (job id -1) the sink does not wait for a job and still
   /// requires the quiet window.
   /// </summary>
   [Fact]
   public async Task FinishLoad_Finishes_WhenMilvusHasNothingToCompact()
   {
      using MilvusFakeServer server = OneSegmentServer();
      using var sink = new MilvusSink( FastOptions( server ) );
      string note = await sink.FinishLoadAsync( COLLECTION, CancellationToken.None );
      Assert.Equal( 1, server.CompactCalls );
      Assert.Contains( "0 job(s) accepted by Milvus", note );
   }

   /// <summary>
   /// A compaction job that never completes must end in a plain failure at the deadline that names
   /// the job, not hang.
   /// </summary>
   [Fact]
   public async Task FinishLoad_Fails_WithAPlainMessage_WhenTheCompactionNeverCompletes()
   {
      using MilvusFakeServer server = OneSegmentServer();
      server.Update( s =>
      {
         s.OnCompact = _ => 7;
         s.CompactionState = _ => "Executing";
      } );
      using var sink = new MilvusSink( FastOptions( server ) with { CompactionWaitSeconds = 2 } );
      var clock = System.Diagnostics.Stopwatch.StartNew();
      InvalidOperationException error = await Assert.ThrowsAsync<InvalidOperationException>( () => sink.FinishLoadAsync( COLLECTION, CancellationToken.None ) );
      Assert.Contains( "did not finish compacting", error.Message );
      Assert.Contains( "compaction job 7", error.Message );
      Assert.True( clock.Elapsed < TimeSpan.FromSeconds( 20 ), $"took {clock.Elapsed.TotalSeconds:F0} s to fail" );
   }

   /// <summary>
   /// Delete-log segments that Milvus never applies end in a plain failure that says so.
   /// </summary>
   [Fact]
   public async Task FinishLoad_Fails_WithAPlainMessage_WhenDeleteLogsAreNeverApplied()
   {
      using MilvusFakeServer server = OneSegmentServer();
      server.Update( s => s.Datacoord.Add( new FakeDatacoordSegment( "L0a", "Flushed", "L0", 0, false, false ) ) );
      using var sink = new MilvusSink( FastOptions( server ) with { CompactionWaitSeconds = 2 } );
      InvalidOperationException error = await Assert.ThrowsAsync<InvalidOperationException>( () => sink.FinishLoadAsync( COLLECTION, CancellationToken.None ) );
      Assert.Contains( "did not finish compacting", error.Message );
      Assert.Contains( "delete-log (L0)", error.Message );
      Assert.Equal( 0, server.CompactCalls );
   }

   /// <summary>
   /// A compaction after FinishLoadAsync (here: the segment is replaced and one more segment is
   /// listed as compacted away) must show in the next state read as a problem, even with no search
   /// sent.
   /// </summary>
   [Fact]
   public async Task GetIndexState_FlagsACompactionThatRanAfterFinishLoad()
   {
      using MilvusFakeServer server = OneSegmentServer();
      using var sink = new MilvusSink( FastOptions( server ) );
      await sink.FinishLoadAsync( COLLECTION, CancellationToken.None );
      IndexState clean = await sink.GetIndexStateAsync( COLLECTION, CancellationToken.None );
      Assert.True( clean.Ready, clean.Detail );
      Assert.Contains( "segments compacted away +0", clean.Detail );

      server.Update( s =>
      {
         s.QueryNode[0] = new FakeQueryNodeSegment( "S2", "Sealed", 524, true );
         s.Datacoord[0] = new FakeDatacoordSegment( "S1", "Dropped", "L1", 524, true, true );
         s.Datacoord.Add( new FakeDatacoordSegment( "S2", "Flushed", "L1", 524, true, false ) );
      } );
      IndexState later = await sink.GetIndexStateAsync( COLLECTION, CancellationToken.None );
      Assert.False( later.Ready, later.Detail );
      Assert.Contains( "1 segment(s) of this collection were compacted away", later.Detail );
      Assert.Contains( "PROBLEM", later.Detail );
   }

   #endregion Public Methods

   #region Private Methods

   /// <summary>
   /// The scripted passing of time after the compaction request: the new segment shows up in the
   /// query node without an index, then with one next to the old segment, then alone.
   /// </summary>
   /// <param name="server">The fake.</param>
   /// <param name="mark">The query node read count at which the compaction was requested.</param>
   private static void Script( MilvusFakeServer server, int mark )
   {
      if( server.ManagementReads == 8 )
      {
         server.Datacoord[server.Datacoord.FindIndex( d => d.Level == "L0" )] = new FakeDatacoordSegment( "L0a", "Dropped", "L0", 0, false, true );
      }

      if( server.CompactCalls == 0 )
      {
         return;
      }

      int since = server.ManagementReads - mark;
      if( since == 2 )
      {
         server.QueryNode.Add( new FakeQueryNodeSegment( "S2", "Sealed", 524, false ) );
      }
      else if( since == 4 )
      {
         server.QueryNode[1] = new FakeQueryNodeSegment( "S2", "Sealed", 524, true );
         server.Datacoord[server.Datacoord.FindIndex( d => d.Id == "S2" )] = new FakeDatacoordSegment( "S2", "Flushed", "L1", 524, true, false );
      }
      else if( since == 6 )
      {
         server.QueryNode.RemoveAt( 0 );
      }
   }

   /// <summary>
   /// A server with one finished segment: sealed and indexed in the query node, flushed and indexed
   /// in the data coordinator, 524 rows stored.
   /// </summary>
   /// <returns>The fake.</returns>
   private static MilvusFakeServer OneSegmentServer()
   {
      var server = new MilvusFakeServer();
      server.Update( s =>
      {
         s.QueryNode.Add( new FakeQueryNodeSegment( "S1", "Sealed", 524, true ) );
         s.Datacoord.Add( new FakeDatacoordSegment( "S1", "Flushed", "L1", 524, true, false ) );
      } );
      return server;
   }

   /// <summary>
   /// Options that point at the fake and keep every wait short.
   /// </summary>
   /// <param name="server">The fake.</param>
   /// <returns>The options.</returns>
   private static MilvusSinkOptions FastOptions( MilvusFakeServer server )
   {
      return new MilvusSinkOptions( BaseUrl: server.Url, ManagementUrl: server.Url, IndexWaitMinutes: 1, CompactionWaitSeconds: 60, LayoutQuietSeconds: 1, PollMilliseconds: 20 );
   }

   #endregion Private Methods
}
