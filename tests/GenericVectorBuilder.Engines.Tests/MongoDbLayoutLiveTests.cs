using System.Text.RegularExpressions;
using GenericVectorBuilder.Core.Contracts;
using GenericVectorBuilder.Engines.Common;
using GenericVectorBuilder.Engines.Sinks;
using Xunit.Abstractions;

namespace GenericVectorBuilder.Engines.Tests;

/// <summary>
/// Proves against the real MongoDB container (deploy/engines/mongodb.compose.yaml must be up) that
/// the sink's wait for the index also waits for the segment layout to stop changing, records the
/// layout in its note, and that the layout it reports then holds.
/// Why live: the layout is what mongot does with the writes, and only the container shows that the
/// wait really holds the layout for the time asked and that a layout read once is still the layout
/// later. Measured 2026-10-05 the same load ended with 4, 2 and 3 segments, so the test does not
/// require a particular count; it requires that what is reported is steady and is the same on every
/// reading.
/// </summary>
[Trait( "Category", "Live" )]
public sealed class MongoDbLayoutLiveTests
{
   #region Data Members

   private const int DIMENSION = 1024;
   private const int COUNT = 524;
   private const int LOADS = 3;
   private const int WATCH_SECONDS = 15;
   private static readonly Regex SEGMENTS = new( @"in (\d+) segment\(s\)", RegexOptions.Compiled );

   private readonly ITestOutputHelper _output;

   #endregion Data Members

   #region Constructor

   /// <summary>
   /// Receives the xunit output writer.
   /// </summary>
   /// <param name="output">Where the evidence is printed.</param>
   public MongoDbLayoutLiveTests( ITestOutputHelper output )
   {
      _output = output;
   }

   #endregion Constructor

   #region Public Methods

   /// <summary>
   /// Three loads of 524 vectors: each wait takes at least the steady time, says how long the layout
   /// was unchanged, reports a segment layout that is the same on every reading for the next 15
   /// seconds, and covers every document.
   /// </summary>
   [Fact]
   public async Task FinishLoad_HoldsForASteadyLayout_AndTheLayoutThenStays()
   {
      var sink = new MongoDbSink( MongoDbSinkOptions.LocalDefaults() );
      var layouts = new List<string>();
      for( int load = 0; load < LOADS; load++ )
      {
         string collection = EnginesAReadinessData.BenchCollection();
         try
         {
            await sink.EnsureCollectionAsync( collection, DIMENSION, CancellationToken.None );
            await sink.UpsertAsync( collection, EnginesAReadinessData.Records( COUNT, DIMENSION, seed: 700 + load ), CancellationToken.None );
            await sink.CountAsync( collection, CancellationToken.None );
            string note = await sink.FinishLoadAsync( collection, CancellationToken.None );
            _output.WriteLine( $"load {load}: {note}" );

            Match unchanged = Regex.Match( note, @"segment layout unchanged for ([0-9.]+) s over (\d+) readings; wait took ([0-9.]+) s" );
            Assert.True( unchanged.Success, note );
            Assert.True( double.Parse( unchanged.Groups[1].Value, System.Globalization.CultureInfo.InvariantCulture ) >= 10.0, note );
            Assert.True( int.Parse( unchanged.Groups[2].Value ) >= 2, note );
            Assert.Contains( $"mongot holds {COUNT} of {COUNT} documents", note );

            IndexState first = await sink.GetIndexStateAsync( collection, CancellationToken.None );
            string firstLayout = SEGMENTS.Match( first.Detail ).Value;
            Assert.True( first.Ready, first.Detail );
            Assert.NotEqual( string.Empty, firstLayout );
            for( int second = 0; second < WATCH_SECONDS; second++ )
            {
               await Task.Delay( 1000 );
               IndexState again = await sink.GetIndexStateAsync( collection, CancellationToken.None );
               Assert.True( again.Ready, again.Detail );
               Assert.Equal( first.Detail, again.Detail );
            }

            layouts.Add( firstLayout );
         }
         finally
         {
            await sink.DropCollectionAsync( collection, CancellationToken.None );
         }
      }

      _output.WriteLine( $"layouts after {LOADS} loads: {string.Join( "; ", layouts )}" );
   }

   #endregion Public Methods
}
