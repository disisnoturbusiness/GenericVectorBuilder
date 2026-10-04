using System.Diagnostics;
using GenericVectorBuilder.Core.Contracts;
using GenericVectorBuilder.Engines.Common;
using GenericVectorBuilder.Engines.Sinks;
using Xunit.Abstractions;

namespace GenericVectorBuilder.Engines.Tests;

/// <summary>
/// Pins the search effort of the MariaDB sink. The benchmark compares engines at the same
/// effort (ef 100), so MariaDB must not quietly search with a different number, and the report
/// must print the number it used.
/// The two live tests also measure what that effort buys: recall@10 against an in-memory brute
/// force on 524 and on 2,000 random 1024-dimension vectors (524 is the size of the benchmark's
/// own dataset). Only tables named gvb_gvbbench_* are created, and they are dropped again.
/// </summary>
public sealed class MariaDbEffortTests
{
   #region Data Members

   private const int DIMENSION = GroupCReadinessData.DIMENSION;
   private const int QUERIES = 50;
   private const int TOP = 10;

   private readonly ITestOutputHelper _output;

   #endregion Data Members

   #region Constructor

   /// <summary>
   /// Creates the test with xunit's output sink.
   /// </summary>
   /// <param name="output">Where the recall table is printed.</param>
   public MariaDbEffortTests( ITestOutputHelper output )
   {
      _output = output;
   }

   #endregion Constructor

   #region Public Methods

   /// <summary>
   /// The default effort is 100 and the description the report prints says so.
   /// </summary>
   [Fact]
   public void Default_IsEf100_AndTheReportNamesIt()
   {
      using var sink = new MariaDbSink( new MariaDbSinkOptions() );
      Assert.Equal( 100, new MariaDbSinkOptions().HnswEfSearch );
      Assert.Contains( "mhnsw_ef_search=100", sink.IndexDescription );
      Assert.Contains( "MariaDB's own default is 20", sink.IndexDescription );
   }

   /// <summary>
   /// An explicit effort shows up in the description, so a run with another number is labelled.
   /// </summary>
   [Fact]
   public void ExplicitEffort_IsPrintedInTheDescription()
   {
      using var sink = new MariaDbSink( new MariaDbSinkOptions { HnswEfSearch = 3200 } );
      Assert.Contains( "mhnsw_ef_search=3200", sink.IndexDescription );
   }

   /// <summary>
   /// The server accepts 1 to 10,000 and silently clamps the rest, so the sink refuses anything
   /// outside that range at construction.
   /// </summary>
   /// <param name="ef">A value the server would clamp or reject.</param>
   [Theory]
   [InlineData( 0 )]
   [InlineData( -5 )]
   [InlineData( 10001 )]
   public void EffortOutsideTheServerRange_IsRefused( int ef )
   {
      var options = new MariaDbSinkOptions { HnswEfSearch = ef };
      var error = Assert.Throws<ArgumentOutOfRangeException>( () => new MariaDbSink( options ) );
      Assert.Contains( "mhnsw_ef_search", error.Message );
   }

   /// <summary>
   /// Loads random vectors, then measures recall@10 at the default effort (ef 100) and, for
   /// context, at MariaDB's own default (20) and at the old setting (3200). The default effort
   /// must be the one the server applied (read back by the readiness report). The recall numbers
   /// are printed; the assertions only require that the index works at all (the high-effort
   /// search finds most true neighbours) and that more effort never loses recall by a wide margin.
   /// </summary>
   /// <param name="count">How many vectors to load: 524 (the benchmark's dataset size) or 2,000.</param>
   [Theory]
   [Trait( "Category", "Live" )]
   [InlineData( 524 )]
   [InlineData( 2000 )]
   public async Task Recall_AtTheDefaultEffort_IsMeasured( int count )
   {
      using var deadline = new CancellationTokenSource( GroupCReadinessData.TEST_DEADLINE );
      CancellationToken ct = deadline.Token;
      string collection = GroupCReadinessData.NewCollection( "mdbef" );
      MariaDbSinkOptions baseline = MariaDbSinkOptions.LocalDefaults();
      using var sink = new MariaDbSink( baseline );
      try
      {
         await sink.EnsureCollectionAsync( collection, DIMENSION, ct );
         List<VectorRecord> records = GroupCReadinessData.MakeRecords( count, 31 );
         await GroupCReadinessData.LoadAsync( sink, collection, records, ct );
         string note = await sink.FinishLoadAsync( collection, ct );
         _output.WriteLine( $"{count} vectors, effort in use: {baseline.HnswEfSearch}. {note}" );
         Assert.Equal( 100, baseline.HnswEfSearch );
         Assert.Contains( "applied mhnsw_ef_search 100", note );

         float[][] queries = Enumerable.Range( 0, QUERIES ).Select( i => RandomUnit( new Random( 9000 + i ) ) ).ToArray();
         HashSet<Guid>[] truth = queries.Select( q => ExactTop( records, q ) ).ToArray();
         double atDefault = await MeasureAsync( sink, collection, queries, truth, "ef 100 (the default)" );
         double atMariaDefault = await MeasureAtAsync( baseline, 20, collection, queries, truth );
         double atOld = await MeasureAtAsync( baseline, 3200, collection, queries, truth );
         _output.WriteLine( $"RECALL@{TOP} on {count} random {DIMENSION}-dim vectors, {QUERIES} queries: ef 20 {atMariaDefault:F3}, ef 100 {atDefault:F3}, ef 3200 {atOld:F3}" );
         Assert.True( atOld >= 0.8, $"even ef 3200 found only {atOld:F3} of the true neighbours, so the index is not working" );
         Assert.True( atOld >= atDefault - 0.05, $"ef 3200 recall {atOld:F3} is below ef 100 recall {atDefault:F3}" );
      }
      finally
      {
         await sink.DropCollectionAsync( collection, CancellationToken.None );
      }
   }

   #endregion Public Methods

   #region Private Methods

   /// <summary>
   /// Measures a sink built with another effort against the same table.
   /// </summary>
   /// <param name="template">Connection settings to copy.</param>
   /// <param name="ef">The effort to use.</param>
   /// <param name="collection">Collection name.</param>
   /// <param name="queries">Query vectors.</param>
   /// <param name="truth">True top ten per query.</param>
   /// <returns>Mean recall.</returns>
   private async Task<double> MeasureAtAsync( MariaDbSinkOptions template, int ef, string collection, float[][] queries, HashSet<Guid>[] truth )
   {
      var options = new MariaDbSinkOptions
      {
         Host = template.Host,
         Port = template.Port,
         User = template.User,
         Password = template.Password,
         Database = template.Database,
         HnswM = template.HnswM,
         HnswEfSearch = ef,
      };
      using var other = new MariaDbSink( options );
      return await MeasureAsync( other, collection, queries, truth, $"ef {ef}" );
   }

   /// <summary>
   /// Runs the queries one at a time and prints mean recall and median latency.
   /// </summary>
   /// <param name="sink">The sink.</param>
   /// <param name="collection">Collection name.</param>
   /// <param name="queries">Query vectors.</param>
   /// <param name="truth">True top ten per query.</param>
   /// <param name="label">What this row is.</param>
   /// <returns>Mean recall.</returns>
   private async Task<double> MeasureAsync( MariaDbSink sink, string collection, float[][] queries, HashSet<Guid>[] truth, string label )
   {
      var times = new List<double>();
      double recall = 0;
      for( int q = 0; q < queries.Length; q++ )
      {
         var timer = Stopwatch.StartNew();
         IReadOnlyList<SearchHit> hits = await sink.SearchAsync( collection, queries[q], TOP, CancellationToken.None );
         timer.Stop();
         times.Add( timer.Elapsed.TotalMilliseconds );
         Assert.Equal( TOP, hits.Count );
         recall += hits.Count( h => truth[q].Contains( h.ChunkId ) ) / (double)TOP;
      }

      times.Sort();
      _output.WriteLine( $"  {label}: recall@{TOP} {recall / queries.Length:F3}, p50 {times[times.Count / 2]:F2} ms" );
      return recall / queries.Length;
   }

   /// <summary>
   /// The true ten nearest records by dot product (the vectors are unit length, so this is the
   /// cosine order), computed in memory.
   /// </summary>
   /// <param name="records">Every stored record.</param>
   /// <param name="query">The query vector.</param>
   /// <returns>Chunk ids of the ten best.</returns>
   private static HashSet<Guid> ExactTop( List<VectorRecord> records, float[] query )
   {
      var scores = new float[records.Count];
      for( int i = 0; i < records.Count; i++ )
      {
         float sum = 0;
         float[] v = records[i].Vector;
         for( int d = 0; d < DIMENSION; d++ )
         {
            sum += v[d] * query[d];
         }

         scores[i] = sum;
      }

      return Enumerable.Range( 0, records.Count ).OrderByDescending( i => scores[i] ).Take( TOP ).Select( i => records[i].Chunk.ChunkId ).ToHashSet();
   }

   /// <summary>
   /// A random vector scaled to length 1.
   /// </summary>
   /// <param name="random">Random source.</param>
   /// <returns>The vector.</returns>
   private static float[] RandomUnit( Random random )
   {
      float[] v = new float[DIMENSION];
      double sum = 0;
      for( int i = 0; i < DIMENSION; i++ )
      {
         v[i] = random.NextSingle() * 2f - 1f;
         sum += v[i] * v[i];
      }

      float norm = (float)Math.Sqrt( sum );
      for( int i = 0; i < DIMENSION; i++ )
      {
         v[i] /= norm;
      }

      return v;
   }

   #endregion Private Methods
}
