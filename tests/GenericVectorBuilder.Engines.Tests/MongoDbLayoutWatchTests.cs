using System.Reflection;
using GenericVectorBuilder.Engines.Sinks;
using MongoDB.Bson;

namespace GenericVectorBuilder.Engines.Tests;

/// <summary>
/// The rule that decides when mongot's segment layout has stopped changing, and the reading of its
/// explain output, without a server. The explain text is the shape mongot 1.76.0 (the
/// mongodb-atlas-local:8.0.32 image) answered on 2026-10-05: stages[0].$vectorSearch.explain with
/// metadata.lucene.totalDocs and luceneVectorSegmentStats, one entry per segment with its executionType
/// and docCount.
/// Why these tests: the v5 runs ended MongoDB with 4 segments in two runs and 2 in the third, and the
/// wait that was meant to prove the index final only checked that the index was ready. The live proof
/// against the container is in <see cref="MongoDbLayoutLiveTests"/>.
/// </summary>
public class MongoDbLayoutWatchTests
{
   #region Data Members

   private const string TWO_SEGMENTS = """{ "stages": [ { "$vectorSearch": { "explain": { "metadata": { "lucene": { "totalSegments": 2, "totalDocs": 524 } }, "luceneVectorSegmentStats": [ { "id": "_1", "executionType": "Approximate", "docCount": 252, "visitedDocCount": 267 }, { "id": "_0", "executionType": "Approximate", "docCount": 272, "visitedDocCount": 278 } ] } } } ] }""";

   #endregion Data Members

   #region Public Methods

   /// <summary>
   /// The layout is steady only after the same layout has been seen, ready, for the whole time and for
   /// the number of readings asked for; a time that has passed with too few readings, or readings
   /// with too little time, is not enough.
   /// </summary>
   [Fact]
   public void Steady_NeedsTheWholeTimeAndEnoughReadings()
   {
      var now = TimeSpan.Zero;
      var watch = new MongoDbLayoutWatch( TimeSpan.FromSeconds( 10 ), 2, () => now );

      Assert.False( watch.Observe( true, "2 segments" ) );
      now = TimeSpan.FromSeconds( 9 );
      Assert.False( watch.Observe( true, "2 segments" ) );
      now = TimeSpan.FromSeconds( 10 );
      Assert.True( watch.Observe( true, "2 segments" ) );
      Assert.Equal( 3, watch.Reads );
      Assert.Equal( TimeSpan.FromSeconds( 10 ), watch.SteadyFor );

      var slow = new MongoDbLayoutWatch( TimeSpan.FromSeconds( 1 ), 5, () => now );
      now = TimeSpan.Zero;
      Assert.False( slow.Observe( true, "x" ) );
      now = TimeSpan.FromMinutes( 5 );
      Assert.False( slow.Observe( true, "x" ) );
      Assert.False( slow.Observe( true, "x" ) );
      Assert.False( slow.Observe( true, "x" ) );
      Assert.True( slow.Observe( true, "x" ) );
   }

   /// <summary>
   /// A different layout starts the count again, from the reading that showed it, so a layout that
   /// moved at second 9 is not steady at second 10.
   /// </summary>
   [Fact]
   public void ALayoutChange_StartsTheCountAgain()
   {
      var now = TimeSpan.Zero;
      var watch = new MongoDbLayoutWatch( TimeSpan.FromSeconds( 10 ), 2, () => now );
      watch.Observe( true, "4 segments" );
      now = TimeSpan.FromSeconds( 9 );
      Assert.False( watch.Observe( true, "2 segments" ) );
      Assert.Equal( 1, watch.Reads );
      Assert.Equal( "2 segments", watch.Layout );
      now = TimeSpan.FromSeconds( 10 );
      Assert.False( watch.Observe( true, "2 segments" ) );
      now = TimeSpan.FromSeconds( 19 );
      Assert.True( watch.Observe( true, "2 segments" ) );
   }

   /// <summary>
   /// A reading where the index is not ready forgets everything: the layout of a half-built index
   /// says nothing about the finished one.
   /// </summary>
   [Fact]
   public void ANotReadyReading_StartsTheCountAgain()
   {
      var now = TimeSpan.Zero;
      var watch = new MongoDbLayoutWatch( TimeSpan.FromSeconds( 10 ), 2, () => now );
      watch.Observe( true, "2 segments" );
      now = TimeSpan.FromSeconds( 8 );
      Assert.False( watch.Observe( false, "2 segments" ) );
      Assert.Null( watch.Layout );
      Assert.Equal( 0, watch.Reads );
      Assert.Equal( TimeSpan.Zero, watch.SteadyFor );
      Assert.Equal( "segment layout not read as ready", watch.Describe() );
      now = TimeSpan.FromSeconds( 12 );
      Assert.False( watch.Observe( true, "2 segments" ) );
      now = TimeSpan.FromSeconds( 22 );
      Assert.True( watch.Observe( true, "2 segments" ) );
   }

   /// <summary>
   /// With no time asked for, two ready readings are enough (the wait that was there before); at
   /// least one reading is always needed.
   /// </summary>
   [Fact]
   public void ZeroTime_NeedsOnlyTheReadings()
   {
      var watch = new MongoDbLayoutWatch( TimeSpan.Zero, 2, () => TimeSpan.Zero );
      Assert.False( watch.Observe( true, "a" ) );
      Assert.True( watch.Observe( true, "a" ) );
      var none = new MongoDbLayoutWatch( TimeSpan.Zero, 0, () => TimeSpan.Zero );
      Assert.True( none.Observe( true, "a" ) );
   }

   /// <summary>
   /// The layout is written as one text with the count and each segment's documents, and the note on
   /// how steady it was says how long and over how many readings.
   /// </summary>
   [Fact]
   public void Signature_AndDescribe_SayWhatTheyShow()
   {
      Assert.Equal( "2 segments holding 272 + 252 documents", MongoDbLayoutWatch.Signature( 2, new long[] { 272, 252 } ) );
      Assert.Equal( "1 segment holding 524 documents", MongoDbLayoutWatch.Signature( 1, new long[] { 524 } ) );
      Assert.Equal( "3 segments", MongoDbLayoutWatch.Signature( 3, Array.Empty<long>() ) );

      var now = TimeSpan.Zero;
      var watch = new MongoDbLayoutWatch( TimeSpan.FromSeconds( 10 ), 2, () => now );
      watch.Observe( true, "x" );
      Assert.Equal( "segment layout unchanged for 0.0 s over 1 reading", watch.Describe() );
      now = TimeSpan.FromSeconds( 10.1 );
      watch.Observe( true, "x" );
      Assert.Equal( "segment layout unchanged for 10.1 s over 2 readings", watch.Describe() );
   }

   /// <summary>
   /// The reading of mongot's explain: ready only when the status is READY, the index is queryable,
   /// mongot holds every stored document and every segment is searched through the graph; the
   /// evidence text names the segment count first (the consolidate command reads it from there) and
   /// then each segment's documents, and the layout carries the same counts.
   /// </summary>
   [Fact]
   public void ToReadout_ReadsMongotsExplain_IntoReadinessEvidenceAndLayout()
   {
      ( bool ready, string detail, string layout ) = Read( TWO_SEGMENTS, "READY", true, 524 );

      Assert.True( ready );
      Assert.Equal( "index status READY, queryable True; mongot holds 524 of 524 documents in 2 segment(s) (272 + 252 documents), 2 searched through the HNSW graph (Approximate)", detail );
      Assert.Equal( "2 segments holding 272 + 252 documents", layout );

      Assert.False( Read( TWO_SEGMENTS, "READY", true, 600 ).Ready );
      Assert.False( Read( TWO_SEGMENTS, "PENDING", true, 524 ).Ready );
      Assert.False( Read( TWO_SEGMENTS, "READY", false, 524 ).Ready );
      Assert.False( Read( TWO_SEGMENTS.Replace( "\"executionType\": \"Approximate\", \"docCount\": 252", "\"executionType\": \"Exact\", \"docCount\": 252" ), "READY", true, 524 ).Ready );
      Assert.False( Read( """{ "stages": [ { "$vectorSearch": { "explain": { "metadata": { "lucene": { "totalDocs": 0 } } } } } ] }""", "READY", true, 524 ).Ready );
      Assert.True( Read( """{ "stages": [ { "$vectorSearch": { "explain": { "metadata": { "lucene": { "totalDocs": 0 } } } } } ] }""", "READY", true, 0 ).Ready );
   }

   /// <summary>
   /// A segment that states no document count leaves the layout as the count alone, and the evidence
   /// text without a documents clause.
   /// </summary>
   [Fact]
   public void ToReadout_WithoutDocumentCounts_UsesTheSegmentCountAlone()
   {
      string text = TWO_SEGMENTS.Replace( "\"docCount\": 252, ", string.Empty );

      ( bool ready, string detail, string layout ) = Read( text, "READY", true, 524 );

      Assert.True( ready );
      Assert.Contains( "documents in 2 segment(s), 2 searched through the HNSW graph (Approximate)", detail );
      Assert.Equal( "2 segments", layout );
   }

   /// <summary>
   /// The sink's default holds the layout steady for ten seconds; zero turns the hold off.
   /// </summary>
   [Fact]
   public void Options_HoldTheLayoutForTenSecondsByDefault()
   {
      Assert.Equal( 10, new MongoDbSinkOptions().LayoutSteadySeconds );
   }

   #endregion Public Methods

   #region Private Methods

   /// <summary>
   /// Calls the sink's private reading of an explain document.
   /// </summary>
   /// <param name="explainJson">The explain document as JSON text.</param>
   /// <param name="status">Index status.</param>
   /// <param name="queryable">Queryable flag.</param>
   /// <param name="stored">Documents in the collection.</param>
   /// <returns>Whether the reading says ready, its evidence text and its layout text.</returns>
   private static ( bool Ready, string Detail, string Layout ) Read( string explainJson, string status, bool queryable, long stored )
   {
      MethodInfo method = typeof( MongoDbSink ).GetMethod( "ToReadout", BindingFlags.NonPublic | BindingFlags.Static )!;
      object readout = method.Invoke( null, new object[] { BsonDocument.Parse( explainJson ), status, queryable, stored } )!;
      object state = readout.GetType().GetProperty( "State" )!.GetValue( readout )!;
      return ( (bool)state.GetType().GetProperty( "Ready" )!.GetValue( state )!, (string)state.GetType().GetProperty( "Detail" )!.GetValue( state )!, (string)readout.GetType().GetProperty( "Layout" )!.GetValue( readout )! );
   }

   #endregion Private Methods
}
