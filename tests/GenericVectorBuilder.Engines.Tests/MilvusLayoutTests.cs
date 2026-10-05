using GenericVectorBuilder.Engines.Sinks;

namespace GenericVectorBuilder.Engines.Tests;

/// <summary>
/// The reading and judging of a Milvus collection's segment layout, without a server. The data
/// coordinator's JSON below is copied from a real Milvus 2.6.25 answer (GET
/// /api/v1/_dc/segments?collection_id=N&amp;in=dc) of 2026-10-04, taken 30 seconds after a
/// 524-row load with upserts: the level-zero delete log still live, one finished segment, and three
/// dropped segments that compactions replaced.
/// </summary>
public sealed class MilvusLayoutTests
{
   #region Data Members

   private const string DATACOORD = @"[
 {""segment_id"":""593265"",""state"":""Dropped"",""compacted"":true,""level"":""L1"",""num_of_rows"":""524"",""node_id"":1},
 {""segment_id"":""594279"",""state"":""Flushed"",""level"":""L1"",""num_of_rows"":""524"",""is_sorted"":true,""is_indexed"":true,""index_fields"":[{""field_id"":""101"",""build_id"":""1""}]},
 {""segment_id"":""593266"",""state"":""Flushed"",""level"":""L0"",""node_id"":1},
 {""segment_id"":""593264"",""state"":""Dropped"",""compacted"":true,""level"":""L0""}
]";

   #endregion Data Members

   #region Public Methods

   /// <summary>
   /// Counts what the data coordinator lists: live segments, the finished one with its rows, the
   /// live delete log, and the segments compacted away.
   /// </summary>
   [Fact]
   public void ParseDatacoord_CountsLiveSegmentsDeleteLogsAndCompactedAway()
   {
      MilvusDatacoordView view = MilvusLayout.ParseDatacoord( DATACOORD );
      Assert.Equal( 2, view.LiveSegments );
      Assert.Equal( 1, view.FlushedIndexedSegments );
      Assert.Equal( 524, view.FlushedRows );
      Assert.Equal( 1, view.DeleteLogSegments );
      Assert.Equal( 0, view.NotYetFlushedSegments );
      Assert.Equal( 2, view.CompactedAway );
      Assert.Equal( "593266:Flushed:L0,594279:Flushed:L1", view.Fingerprint );
   }

   /// <summary>
   /// The order Milvus lists the segments in changes between two reads (measured), so the
   /// fingerprint must not depend on it.
   /// </summary>
   [Fact]
   public void ParseDatacoord_FingerprintIsOrderIndependent()
   {
      string reversed = @"[
 {""segment_id"":""593266"",""state"":""Flushed"",""level"":""L0""},
 {""segment_id"":""594279"",""state"":""Flushed"",""level"":""L1"",""num_of_rows"":""524"",""is_indexed"":true}
]";
      string forward = @"[
 {""segment_id"":""594279"",""state"":""Flushed"",""level"":""L1"",""num_of_rows"":""524"",""is_indexed"":true},
 {""segment_id"":""593266"",""state"":""Flushed"",""level"":""L0""}
]";
      Assert.Equal( MilvusLayout.ParseDatacoord( forward ).Fingerprint, MilvusLayout.ParseDatacoord( reversed ).Fingerprint );
   }

   /// <summary>
   /// Milvus answers the text null for a collection it lists no segments for.
   /// </summary>
   [Fact]
   public void ParseDatacoord_Null_IsEmpty()
   {
      MilvusDatacoordView view = MilvusLayout.ParseDatacoord( "null" );
      Assert.Equal( 0, view.LiveSegments );
      Assert.Equal( "none", view.Fingerprint );
   }

   /// <summary>
   /// A flushed segment whose index build is not finished is a segment not ready yet; a growing
   /// segment is too.
   /// </summary>
   [Fact]
   public void ParseDatacoord_UnindexedAndGrowingSegments_AreNotYetFlushed()
   {
      string json = @"[
 {""segment_id"":""1"",""state"":""Flushed"",""level"":""L1"",""num_of_rows"":""10""},
 {""segment_id"":""2"",""state"":""Growing"",""level"":""L1"",""num_of_rows"":""5""}
]";
      MilvusDatacoordView view = MilvusLayout.ParseDatacoord( json );
      Assert.Equal( 2, view.NotYetFlushedSegments );
      Assert.Equal( 0, view.FlushedIndexedSegments );
   }

   /// <summary>
   /// Everything in place: ready, nothing to wait for.
   /// </summary>
   [Fact]
   public void Judge_AllChecksHold_IsReady()
   {
      MilvusVerdict verdict = MilvusLayout.Judge( "Finished", "HNSW", 524, 524, 0, 524, "LoadStateLoaded", Node( 524 ), Clean() );
      Assert.True( verdict.Ready );
      Assert.False( verdict.Unsealed );
      Assert.Empty( verdict.Waiting );
   }

   /// <summary>
   /// The reading of the 2026-10-04 smoke run: 2 sealed segments covering 1,048 rows for 524 stored.
   /// Equality is required, so this is not ready, and the reason names both numbers.
   /// </summary>
   [Fact]
   public void Judge_MoreCoveredRowsThanStored_IsNotReady()
   {
      var node = new MilvusNodeView( 1048, 0, 2, 0, "a,b" );
      MilvusVerdict verdict = MilvusLayout.Judge( "Finished", "HNSW", 1048, 1048, 0, 524, "LoadStateLoaded", node, Clean() );
      Assert.False( verdict.Ready );
      Assert.Contains( verdict.Waiting, w => w.Contains( "1048 indexed rows for 524 stored" ) );
      Assert.Contains( verdict.Waiting, w => w.Contains( "1048 rows are in sealed segments but 524 are stored" ) );
   }

   /// <summary>
   /// Rows in growing segments need another flush: Unsealed is true.
   /// </summary>
   [Fact]
   public void Judge_GrowingRows_AreUnsealedAndNotReady()
   {
      var node = new MilvusNodeView( 400, 124, 1, 0, "a" );
      MilvusVerdict verdict = MilvusLayout.Judge( "Finished", "HNSW", 400, 400, 0, 524, "LoadStateLoaded", node, Clean() );
      Assert.False( verdict.Ready );
      Assert.True( verdict.Unsealed );
   }

   /// <summary>
   /// A new segment served without its index (the state after a compaction until the build runs)
   /// is not ready.
   /// </summary>
   [Fact]
   public void Judge_SealedSegmentWithoutIndex_IsNotReady()
   {
      var node = new MilvusNodeView( 0, 0, 0, 1, "a" );
      MilvusVerdict verdict = MilvusLayout.Judge( "Finished", "HNSW", 524, 524, 524, 524, "LoadStateLoaded", node, Clean() );
      Assert.False( verdict.Ready );
      Assert.Contains( verdict.Waiting, w => w.Contains( "served without a loaded index" ) );
      Assert.Contains( verdict.Waiting, w => w.Contains( "524 rows wait for an index build" ) );
   }

   /// <summary>
   /// A different index type or an unfinished index state is not ready.
   /// </summary>
   [Fact]
   public void Judge_WrongIndexTypeOrState_IsNotReady()
   {
      MilvusVerdict flat = MilvusLayout.Judge( "Finished", "FLAT", 524, 524, 0, 524, "LoadStateLoaded", Node( 524 ), Clean() );
      Assert.Contains( flat.Waiting, w => w.Contains( "not HNSW" ) );
      MilvusVerdict building = MilvusLayout.Judge( "InProgress", "HNSW", 100, 524, 424, 524, "LoadStateLoaded", Node( 524 ), Clean() );
      Assert.False( building.Ready );
      Assert.Contains( building.Waiting, w => w.Contains( "not Finished" ) );
   }

   /// <summary>
   /// A delete-log segment waiting for its compaction does not change what a search returns, so
   /// it does not make the reading not ready (the compaction step waits for it separately).
   /// </summary>
   [Fact]
   public void Judge_LiveDeleteLog_DoesNotMakeItNotReady()
   {
      MilvusDatacoordView withLog = Clean() with { DeleteLogSegments = 1, LiveSegments = 2 };
      MilvusVerdict verdict = MilvusLayout.Judge( "Finished", "HNSW", 524, 524, 0, 524, "LoadStateLoaded", Node( 524 ), withLog );
      Assert.True( verdict.Ready );
   }

   /// <summary>
   /// A data segment the data coordinator lists as not flushed and indexed yet is not ready.
   /// </summary>
   [Fact]
   public void Judge_DatacoordSegmentNotIndexed_IsNotReady()
   {
      MilvusDatacoordView notYet = Clean() with { NotYetFlushedSegments = 1 };
      MilvusVerdict verdict = MilvusLayout.Judge( "Finished", "HNSW", 524, 524, 0, 524, "LoadStateLoaded", Node( 524 ), notYet );
      Assert.False( verdict.Ready );
      Assert.Contains( verdict.Waiting, w => w.Contains( "not flushed and indexed yet" ) );
   }

   #endregion Public Methods

   #region Private Methods

   /// <summary>
   /// A query node view with one sealed, indexed segment covering the rows.
   /// </summary>
   /// <param name="rows">Rows covered.</param>
   /// <returns>The view.</returns>
   private static MilvusNodeView Node( long rows )
   {
      return new MilvusNodeView( rows, 0, 1, 0, "a" );
   }

   /// <summary>
   /// A data coordinator view with one finished segment and nothing waiting.
   /// </summary>
   /// <returns>The view.</returns>
   private static MilvusDatacoordView Clean()
   {
      return new MilvusDatacoordView( 1, 1, 524, 0, 0, 3, "594279:Flushed:L1" );
   }

   #endregion Private Methods
}
