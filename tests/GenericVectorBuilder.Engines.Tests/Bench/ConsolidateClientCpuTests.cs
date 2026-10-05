using System.Text.Json;
using System.Text.Json.Nodes;

namespace GenericVectorBuilder.Engines.Tests.Bench;

/// <summary>
/// The client's CPU per search in the consolidate command: read from a run's search section
/// (search.clientCpuMsPerSearch, an object keyed by concurrency level), summarized over the runs,
/// and printed beside the latency and the QPS it belongs to, in consolidated.json and
/// consolidated.md. Results that do not carry it show nothing: no column, no line, no empty
/// numbers.
/// Why shown at all: for the fastest engines the client's own CPU per search (0.49 to 0.87 ms in
/// the v4 review) is about as large as the latency, so the speed order partly reflects each
/// engine's .NET client library.
/// </summary>
public sealed partial class ConsolidateTests
{
   #region Data Members

   private const string CLIENT_CPU_LINE = "Client CPU per search is the CPU time the test's .NET client itself used for each search, measured in the same pass as the figure beside it. Where it is close to the latency, the client library is a large part of what is measured.";

   #endregion Data Members

   #region Public Methods

   /// <summary>
   /// A target whose runs carry the figure gets a median, min, max and per-run values at each level
   /// it was recorded for; a target that does not carry it has none, and a level one run left out
   /// is summarized from the runs that have it. Both spellings of a level ("8" and "default@8") are
   /// read.
   /// </summary>
   [Fact]
   public void ClientCpu_IsSummarizedPerLevel_AndAbsentWhereNotRecorded()
   {
      WriteCpuRuns();

      JsonElement json = Consolidate( "--targets", "fast,plain", Path.Combine( _root, "r?" ) );

      JsonElement fast = json.GetProperty( "targetSummaries" )[0].GetProperty( "clientCpuMsPerSearch" );
      JsonElement one = fast.GetProperty( "1" );
      Assert.Equal( 0.5, one.GetProperty( "median" ).GetDouble(), 9 );
      Assert.Equal( 0.45, one.GetProperty( "min" ).GetDouble(), 9 );
      Assert.Equal( 0.55, one.GetProperty( "max" ).GetDouble(), 9 );
      Assert.Equal( 3, one.GetProperty( "n" ).GetInt32() );
      JsonElement eight = fast.GetProperty( "8" );
      Assert.Equal( 2, eight.GetProperty( "n" ).GetInt32() );
      Assert.Equal( 0.35, eight.GetProperty( "median" ).GetDouble(), 9 );
      Assert.Equal( JsonValueKind.Null, eight.GetProperty( "perRun" )[2].ValueKind );
      Assert.Empty( json.GetProperty( "targetSummaries" )[1].GetProperty( "clientCpuMsPerSearch" ).EnumerateObject() );
   }

   /// <summary>
   /// The markdown shows the figure in the per-target table, in a per-run table for each level, and
   /// as a column of the ranking table it belongs to (the one-searcher figure beside p50 and QPS@1,
   /// the 8-searcher figure beside QPS@8), with "-" for a target without it and the one definition
   /// line printed once under the ranking.
   /// </summary>
   [Fact]
   public void ClientCpu_IsShownBesideLatencyAndQps_WhenTheResultsCarryIt()
   {
      WriteCpuRuns();

      Consolidate( "--targets", "fast,plain", Path.Combine( _root, "r?" ) );

      string md = File.ReadAllText( Path.Combine( _root, "out", "consolidated.md" ) );
      Assert.Contains( "client CPU ms/search@1 | client CPU ms/search@8 |", md );
      Assert.Contains( "| fast | 3 | 1.00 [1.00, 1.00] |", md );
      Assert.Contains( "0.50 [0.45, 0.55] | 0.35 [0.30, 0.40] |", md );
      Assert.Contains( "## Per run: client CPU ms per search@1", md );
      Assert.Contains( "## Per run: client CPU ms per search@8", md );
      Assert.Contains( "| band | target | median [min, max] | client CPU ms/search | runs | engine notes |", md );
      string ranking = md[md.IndexOf( "## Request speed", StringComparison.Ordinal )..md.IndexOf( "## Per-engine notes", StringComparison.Ordinal )];
      Assert.Contains( "| fast | 1.00 [1.00, 1.00] | 0.50 [0.45, 0.55] | 3 | - |", ranking );
      Assert.Contains( "| plain | 1.00 [1.00, 1.00] | - | 3 | - |", ranking );
      Assert.Contains( "| fast | 900.0 [900.0, 900.0] | 0.35 [0.30, 0.40] | 3 | - |", ranking );
      Assert.Equal( 1, ranking.Split( CLIENT_CPU_LINE ).Length - 1 );
   }

   /// <summary>
   /// Results that carry no client CPU produce no column, no line and no per-run table: the band
   /// case's markdown has the word "client CPU" nowhere, and every target's figure is an empty object.
   /// </summary>
   [Fact]
   public void ClientCpu_IsNotShown_WhenNoRunCarriesIt()
   {
      WriteBandCase();

      JsonElement json = Consolidate( "--targets", "a,b,c,d,e", Path.Combine( _root, "r?" ) );

      string md = File.ReadAllText( Path.Combine( _root, "out", "consolidated.md" ) );
      Assert.DoesNotContain( "client CPU", md );
      Assert.DoesNotContain( "Client CPU", md );
      Assert.All( json.GetProperty( "targetSummaries" ).EnumerateArray(), t => Assert.Empty( t.GetProperty( "clientCpuMsPerSearch" ).EnumerateObject() ) );
   }

   /// <summary>
   /// A negative or non-numeric figure is not a measurement and is skipped, so a damaged field reads
   /// as not recorded instead of showing a nonsense number.
   /// </summary>
   [Fact]
   public void ClientCpu_IgnoresNegativeAndNonNumericValues()
   {
      JsonObject Damaged( object cpu )
      {
         JsonObject target = Target( "t", 1, 900, 100 );
         ( (JsonObject)target["search"]! )["clientCpuMsPerSearch"] = JsonSerializer.SerializeToNode( cpu );
         return target;
      }

      WriteRun( "r1", "2026-10-05T10:00:00Z", Damaged( new Dictionary<string, object> { ["1"] = -0.5, ["8"] = "fast" } ) );

      JsonElement json = Consolidate( "--targets", "t", Path.Combine( _root, "r1" ) );

      Assert.Empty( json.GetProperty( "targetSummaries" )[0].GetProperty( "clientCpuMsPerSearch" ).EnumerateObject() );
   }

   #endregion Public Methods

   #region Private Methods

   /// <summary>
   /// Three runs of "fast" (client CPU 0.45, 0.50 and 0.55 ms per search with one searcher; with 8
   /// searchers 0.30 and 0.40 in the first two runs, spelled "default@8", and nothing in the third)
   /// and of "plain", which records none.
   /// </summary>
   private void WriteCpuRuns()
   {
      double[] one = { 0.45, 0.50, 0.55 };
      double?[] eight = { 0.30, 0.40, null };
      for( int run = 0; run < 3; run++ )
      {
         JsonObject fast = Target( "fast", 1.0, 900, 100 );
         var cpu = new JsonObject { ["1"] = one[run] };
         if( eight[run] is double e )
         {
            cpu["default@8"] = e;
         }

         ( (JsonObject)fast["search"]! )["clientCpuMsPerSearch"] = cpu;
         WriteRun( $"r{run + 1}", $"2026-10-05T1{run}:00:00Z", fast, Target( "plain", 1.0, 800, 90 ) );
      }
   }

   #endregion Private Methods
}
