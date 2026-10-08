using System.Text.Json;
using System.Text.Json.Nodes;

namespace GenericVectorBuilder.Engines.Tests.Bench;

/// <summary>
/// The report-layer fixes the v8 verdict (BLOCK) asked for, proven on the real v7 and v8 runs. The consolidation of the six runs was refused twice (the
/// fact sheet and the ClickHouse sentence), and eight lenses then found sentences that a reader would take for something the data does not say. Each test here
/// fails on the tree the verdict judged.
/// </summary>
public sealed class ConsolidateV8FixerTests : IClassFixture<RealV8Fixture>
{
   #region Data Members

   private readonly RealV8Fixture _fixture;

   #endregion Data Members

   #region Constructor

   /// <summary>
   /// Takes the shared consolidation.
   /// </summary>
   /// <param name="fixture">The fixture.</param>
   public ConsolidateV8FixerTests( RealV8Fixture fixture )
   {
      _fixture = fixture;
   }

   #endregion Constructor

   #region Public Methods

   /// <summary>
   /// Refusal 1: the consolidation of the real v7 and v8 runs is not refused. The storage rows of sqlitevec and duckdb said "in-memory" is absent from the
   /// target's recorded text, and the v8 engine-settings rows say a version was read "on an in-memory connection".
   /// </summary>
   [Fact]
   public void TheRealV7AndV8Runs_AreNotRefused()
   {
      Assert.Equal( 0, _fixture.Both.Exit );
      Assert.Equal( "complete", _fixture.Both.Root.GetProperty( "status" ).GetString() );
      Assert.Empty( _fixture.Both.Root.GetProperty( "audit" ).GetProperty( "failures" ).EnumerateArray() );
   }

   /// <summary>
   /// Refusal 2 and the {4} garble: each v8 ClickHouse folder sentence is built from the byte fields, fits the 35 word limit, names no "steady" folder and
   /// has no raw template slot left in it.
   /// </summary>
   [Fact]
   public void TheClickHouseFolderSentence_IsBuiltFromTheByteFields_AndFitsTheLimit()
   {
      foreach( string run in RealV8Fixture.V8 )
      {
         JsonElement folder = ClickHouse( run ).GetProperty( "dataFolder" );
         string text = _fixture.Both.Text( $"disclosure.dataFolder.clickhouse.{run}" )!;
         Assert.Equal( $"Run {run}: clickhouse's data folder held {folder.GetProperty( "bytesAtStart" ).GetInt64()} bytes at its start, {folder.GetProperty( "bytesAfterReset" ).GetInt64()} bytes after its system log tables were truncated and it stopped changing, and {folder.GetProperty( "bytesAtEnd" ).GetInt64()} bytes at its end.", text );
         Assert.True( (int)ConsolidateHarness.Call( "Words", text )! <= 35, text );
         Assert.DoesNotContain( "steady", text );
         Assert.DoesNotContain( "{", text );
         Assert.DoesNotContain( "tables truncated", text );
      }
   }

   /// <summary>
   /// The second ClickHouse sentence says what the reset truncated and what ClickHouse itself counted in those tables, each figure a token of the recorded text.
   /// </summary>
   [Fact]
   public void TheClickHouseResetSentence_CitesTheRecordedTableCountAndActiveBytes()
   {
      string text = _fixture.Both.Text( "disclosure.dataFolder.clickhouse.20261007-205106-eshoponweb.tables" )!;
      Assert.Equal( "Run 20261007-205106-eshoponweb: the reset truncated 9 MergeTree log tables, whose active bytes by clickhouse's own count were 2.09 GiB before and 0 B after.", text );
      Assert.True( (int)ConsolidateHarness.Call( "Words", text )! <= 35 );
   }

   /// <summary>
   /// The recorded reset text is never printed whole: its "(the folder was steady)" sits beside a folder that fell from 4.25 GiB to 14.01 MiB, and the report
   /// states the size read after the folder stopped changing instead.
   /// </summary>
   [Fact]
   public void TheRecordedResetText_IsNeverQuotedWhole()
   {
      Assert.DoesNotContain( "the folder was steady", _fixture.Both.Md );
      Assert.All( _fixture.Both.Root.GetProperty( "sentences" ).EnumerateArray(), x => Assert.DoesNotContain( "the folder was steady", x.GetProperty( "text" ).GetString() ) );
   }

   /// <summary>
   /// Verdict fix-with 4: the basis section says the largest move rests on run 801's ClickHouse eight-searcher pass, which the run recorded as NOT HELD, and
   /// that the threshold would be 35% with ClickHouse left out of the basis. The threshold itself stays what the pre-set rule computes, 45%.
   /// </summary>
   [Fact]
   public void TheBasis_NamesTheNotHeldPassTheLargestMoveRestsOn_AndTheThresholdWithoutIt()
   {
      JsonElement basis = _fixture.Both.Root.GetProperty( "basis" );
      Assert.Equal( 4500, _fixture.Both.Root.GetProperty( "threshold" ).GetProperty( "tBp" ).GetInt32() );
      Assert.Equal( 3582, basis.GetProperty( "maxBp" ).GetInt32() );
      Assert.Equal( "clickhouse/oracle", basis.GetProperty( "maxMove" ).GetProperty( "pair" ).GetString() );
      Assert.Equal( "Run 20261007-205106-eshoponweb: the timed eight-searcher pass of clickhouse read 336 QPS, 24% from the settled 418 QPS (limit 10%), and the run recorded it as NOT HELD.", _fixture.Both.Text( "basis.max.notHeld.0" ) );
      Assert.Single( _fixture.Both.Under( "basis.max.notHeld" ) );
      JsonElement without = basis.GetProperty( "maxMoveWithout" );
      Assert.Equal( new[] { "clickhouse" }, without.GetProperty( "targets" ).EnumerateArray().Select( t => t.GetString() ).ToArray() );
      Assert.Equal( 3500, without.GetProperty( "tBp" ).GetInt32() );
      Assert.True( without.GetProperty( "maxBp" ).GetInt32() < 3582 );
      string text = _fixture.Both.Text( "basis.max.without" )!;
      Assert.StartsWith( "The largest move includes a NOT HELD pass; with clickhouse left out of the basis, the largest move is ", text );
      Assert.EndsWith( ", and the threshold would be 35%.", text );
      Assert.True( (int)ConsolidateHarness.Call( "Words", text )! <= 35 );
      int notHeld = _fixture.Both.Md.IndexOf( "NOT HELD", StringComparison.Ordinal );
      Assert.True( notHeld > 0 );
   }

   /// <summary>
   /// The brief: ClickHouse QPS@8 is shown with "not held" and is not ranked. Its row carries the not-held flag, one text per run that recorded the pass NOT HELD, and the status "not-held"; no other metric's
   /// row of ClickHouse does; the row stands after every ranked row of its table and no ranked row names it as an engine it is not separated from.
   /// </summary>
   [Fact]
   public void TheClickHouseQps8Row_IsShownNotHeldAndNotRanked_AndNoOtherMetricIs()
   {
      foreach( JsonElement metric in _fixture.Both.Root.GetProperty( "metrics" ).EnumerateArray() )
      {
         List<JsonElement> rows = metric.GetProperty( "rows" ).EnumerateArray().ToList();
         JsonElement row = rows.First( r => r.GetProperty( "target" ).GetString() == "clickhouse" );
         List<JsonElement> held = row.GetProperty( "flags" ).EnumerateArray().Where( f => f.GetProperty( "code" ).GetString() == "not-held" ).ToList();
         if( metric.GetProperty( "metric" ).GetString() == "qps8" )
         {
            Assert.Equal( "not-held", row.GetProperty( "status" ).GetString() );
            Assert.Equal( 3, held.Count );
            Assert.Equal( new[] { 336, 380, 349 }, held.Select( f => int.Parse( System.Text.RegularExpressions.Regex.Match( f.GetProperty( "text" ).GetString()!, @"read (\d+) QPS" ).Groups[1].Value ) ).ToArray() );
            Assert.All( held, f => Assert.EndsWith( "and the run recorded it as NOT HELD.", f.GetProperty( "text" ).GetString() ) );
            Assert.Equal( "clickhouse", rows[^1].GetProperty( "target" ).GetString() );
            Assert.Empty( row.GetProperty( "notSeparatedFrom" ).EnumerateArray() );
            Assert.DoesNotContain( rows, r => r.GetProperty( "notSeparatedFrom" ).EnumerateArray().Any( t => t.GetString() == "clickhouse" ) );
            Assert.All( rows.Take( rows.Count - 1 ), r => Assert.Equal( "ranked", r.GetProperty( "status" ).GetString() ) );
            Assert.Equal( 377.54598230412046, row.GetProperty( "median" ).GetDouble() );
         }
         else
         {
            Assert.Empty( held );
            Assert.Equal( "ranked", row.GetProperty( "status" ).GetString() );
         }
      }
   }

   /// <summary>
   /// The unranked row takes part in no pair: QPS@8 orders 118 of the 153 pairs of its 18 ranked engines, which is the 132 the table ordered with ClickHouse in it less the 14 pairs ClickHouse was behind; the
   /// other tables are as before; the drift lists name ClickHouse at no QPS@8 order; and the page says in a sentence under the table that the row is shown and not ranked.
   /// </summary>
   [Fact]
   public void TheNotHeldRow_TakesPartInNoPair_AndTheSentenceUnderTheTableSaysSo()
   {
      JsonElement[] metrics = _fixture.Both.Root.GetProperty( "metrics" ).EnumerateArray().ToArray();
      Assert.Equal( new[] { 126, 127, 118, 68 }, metrics.Select( m => m.GetProperty( "orderedPairs" ).GetInt32() ).ToArray() );
      Assert.Equal( new[] { 171, 171, 153, 91 }, metrics.Select( m => m.GetProperty( "comparedPairs" ).GetInt32() ).ToArray() );
      JsonElement drift = _fixture.Both.Root.GetProperty( "drift" );
      Assert.DoesNotContain( drift.GetProperty( "unconfirmedOrders" ).EnumerateArray(), u => u.GetProperty( "metric" ).GetString() == "qps8" && ( u.GetProperty( "a" ).GetString() == "clickhouse" || u.GetProperty( "b" ).GetString() == "clickhouse" ) );
      Assert.DoesNotContain( drift.GetProperty( "perTarget" ).EnumerateArray(), d => d.GetProperty( "metric" ).GetString() == "qps8" && d.GetProperty( "target" ).GetString() == "clickhouse" );
      Assert.NotEqual( "clickhouse", drift.GetProperty( "largest" ).GetProperty( "target" ).GetString() );
      Assert.Equal( "clickhouse is shown and not ranked in this table: its timed eight-searcher pass was recorded NOT HELD in 3 of the 6 runs.", _fixture.Both.Text( "table.qps8.notHeld.clickhouse" ) );
      JsonElement unranked = Assert.Single( _fixture.Both.Root.GetProperty( "notHeld" ).GetProperty( "unranked" ).EnumerateArray() );
      Assert.Equal( ( "qps8", "clickhouse", "default@8", 3 ), ( unranked.GetProperty( "metric" ).GetString(), unranked.GetProperty( "target" ).GetString(), unranked.GetProperty( "pass" ).GetString(), unranked.GetProperty( "runs" ).GetInt32() ) );
      Assert.Equal( new[] { 418, 432, 416 }, _fixture.Both.Root.GetProperty( "notHeld" ).GetProperty( "cells" ).EnumerateArray().Select( c => int.Parse( c.GetProperty( "settled" ).GetString()!.Split( ' ' )[0] ) ).ToArray() );
   }

   /// <summary>
   /// A pass timed in p50 is handled the same way: with ElasticSearch's exact pass planted as NOT HELD in one v8 run, its row in the exact table is unranked and the p50 table is not.
   /// </summary>
   [Fact]
   public void ANotHeldExactPass_LeavesThatRowUnrankedInTheExactTableOnly()
   {
      ( int exit, string log, ConsolidateOutcome? written ) = _fixture.Attempt( "exact", results =>
         RealV8Fixture.Change( results, RealV8Fixture.V8[0], "qdrant-hnsw", t => t["notes"]!.AsArray().Add( "WARNING: latency had NOT settled when timing began for exact (each by its own warm-up). exact: warm-up 15.0 s and 2,155 searches (0 failed) at 1 searcher; the timed pass p50 1.000 ms, 12% from the settled p50 0.890 ms (limit 10% or 0.1 ms; 0.110 ms), NOT HELD: the engine was still changing when it was timed." ) ) );
      Assert.True( exit == 0, log );
      JsonElement[] metrics = written!.Root.GetProperty( "metrics" ).EnumerateArray().ToArray();
      JsonElement exact = metrics.First( m => m.GetProperty( "metric" ).GetString() == "exactP50Ms" );
      Assert.Equal( "not-held", exact.GetProperty( "rows" ).EnumerateArray().First( r => r.GetProperty( "target" ).GetString() == "qdrant-hnsw" ).GetProperty( "status" ).GetString() );
      Assert.All( metrics.Where( m => m.GetProperty( "metric" ).GetString() != "exactP50Ms" ), m => Assert.Equal( "ranked", m.GetProperty( "rows" ).EnumerateArray().First( r => r.GetProperty( "target" ).GetString() == "qdrant-hnsw" ).GetProperty( "status" ).GetString() ) );
      Assert.Equal( "qdrant-hnsw is shown and not ranked in this table: its timed exact pass was recorded NOT HELD in 1 of the 6 runs.", written.Text( "table.exactP50Ms.notHeld.qdrant-hnsw" ) );
   }

   /// <summary>
   /// A NOT HELD clause is read from the settle warning as the run wrote it: the pass it belongs to, the figure in QPS or p50, its distance from the settled figure and the margin; two
   /// clauses in one warning are two passes; a clause in a form this report does not read is refused, naming it, and never dropped.
   /// </summary>
   [Fact]
   public void ANotHeldClause_IsReadFromTheSettleWarning_AndAnUnreadableOneIsRefused()
   {
      const string QPS = "default@8: warm-up 15.0 s and 6,523 searches; the timed pass 336 QPS, 24% from the settled 418 QPS (limit 10%), NOT HELD: the engine was still changing when it was timed; ";
      const string P50 = "exact: warm-up 15.0 s and 2,155 searches; the timed pass p50 6.630 ms, 3% from the settled p50 6.837 ms (limit 10% or 0.1 ms; 0.208 ms), NOT HELD: the engine was still changing when it was timed. Its numbers may still include warm-up.";
      string[] found = (string[])ConsolidateHarness.Call( "NotHeld", "latency had NOT settled when timing began for default@8, exact (...). " + QPS + P50 )!;
      Assert.Equal( 2, found.Length );
      Assert.Equal( "default@8|qps|336 QPS|24|418 QPS|10|the timed pass 336 QPS, 24% from the settled 418 QPS (limit 10%), NOT HELD", found[0] );
      Assert.Equal( "exact|p50|p50 6.630 ms|3|p50 6.837 ms|10|the timed pass p50 6.630 ms, 3% from the settled p50 6.837 ms (limit 10% or 0.1 ms; 0.208 ms), NOT HELD", found[1] );
      Assert.Empty( (string[])ConsolidateHarness.Call( "NotHeld", "latency had NOT settled when timing began for default@8. default@8: warm-up 15.0 s; the timed pass 400 QPS, 2% from the settled 410 QPS (limit 10%)." )! );
      Assert.Empty( (string[])ConsolidateHarness.Call( "NotHeld", (string?)null! )! );
      string[] refused = (string[])ConsolidateHarness.Call( "NotHeld", "default@8: warm-up 15.0 s; the timed pass about 336 QPS, far from the settled one, NOT HELD" )!;
      Assert.StartsWith( "refused: the settle warning's NOT HELD clause is not in the form this report reads", refused[0] );
      Assert.Contains( "about 336 QPS", refused[0] );
   }

   /// <summary>
   /// A NOT HELD clause the report cannot read stops the consolidation and names it.
   /// </summary>
   [Fact]
   public void AnUnreadableNotHeldClause_StopsTheConsolidation_AndNamesIt()
   {
      ( int exit, string log, ConsolidateOutcome? written ) = _fixture.Attempt( "unreadable", results =>
         RealV8Fixture.Change( results, RealV8Fixture.V8[0], "clickhouse", t => t["notes"] = new JsonArray( t["notes"]!.AsArray().Select( n => (JsonNode)JsonValue.Create( ((string)n!).Replace( "the timed pass 336 QPS, 24% from", "the timed pass of 336 QPS, 24 percent from", StringComparison.Ordinal ) )! ).ToArray() ) ) );
      Assert.Equal( 1, exit );
      Assert.Null( written );
      Assert.Contains( "NOT HELD clause is not in the form this report reads", log );
   }

   /// <summary>
   /// A reset text that says the folder was still changing at the deadline is stated so, and a reset text in no form this report reads stops the consolidation, naming the run.
   /// </summary>
   [Fact]
   public void AResetTextThatStillChanged_IsStatedSo_AndAnUnreadableOneIsRefused()
   {
      ( int exit, string stillLog, ConsolidateOutcome? still ) = _fixture.Attempt( "still", results =>
         RealV8Fixture.Change( results, RealV8Fixture.V8[1], "clickhouse", t => t["dataFolder"]!["reset"] = ((string)t["dataFolder"]!["reset"]!).Replace( RESET_STABLE, RESET_STILL, StringComparison.Ordinal ) ) );
      Assert.True( exit == 0, stillLog );
      string run = RealV8Fixture.V8[1];
      JsonElement folder = ClickHouse( run ).GetProperty( "dataFolder" );
      Assert.Equal( $"Run {run}: clickhouse's data folder held {folder.GetProperty( "bytesAtStart" ).GetInt64()} bytes at its start, {folder.GetProperty( "bytesAfterReset" ).GetInt64()} bytes after its system log tables were truncated and while it was still changing at the deadline, and {folder.GetProperty( "bytesAtEnd" ).GetInt64()} bytes at its end.", still!.Text( $"disclosure.dataFolder.clickhouse.{run}" ) );
      ( int refused, string log, ConsolidateOutcome? none ) = _fixture.Attempt( "unreadable-reset", results =>
         RealV8Fixture.Change( results, RealV8Fixture.V8[2], "clickhouse", t => t["dataFolder"]!["reset"] = "truncated some tables" ) );
      Assert.Equal( 1, refused );
      Assert.Null( none );
      Assert.Contains( RealV8Fixture.V8[2] + ": the recorded data-folder reset text is not in the form this report reads", log );
   }

   /// <summary>
   /// Verdict fix-with 7: the drift section says the sessions differ in build, day and outside load, and gives each session's outside load as the median of its passes' own record, per run;
   /// the figures are recomputed here from the raw results.json files.
   /// </summary>
   [Fact]
   public void TheDriftSection_SaysTheSessionsDifferInBuildDayAndOutsideLoad_WithTheMeasuredMedians()
   {
      Assert.Equal( "The sessions differ in build, day and outside load, and this test does not separate those from the engines' own drift.", _fixture.Both.Text( "drift.sessions" ) );
      double[] v7 = ConsolidateRealRunsTests.V7.Select( RunMedian ).ToArray();
      double[] v8 = RealV8Fixture.V8.Select( RunMedian ).ToArray();
      Assert.Equal( $"The median outside load of a pass, per run, was {Exact( v7.Min() )} to {Exact( v7.Max() )} CPUs in v7 and {Exact( v8.Min() )} to {Exact( v8.Max() )} in v8.", _fixture.Both.Text( "drift.outsideLoad" ) );
      Assert.Equal( "0.2095", Exact( v7.Min() ) );
      Assert.Equal( "0.1435", Exact( v8.Max() ) );
      JsonElement drift = _fixture.Both.Root.GetProperty( "drift" );
      Assert.Equal( new[] { "build", "day", "outside load" }, drift.GetProperty( "sessionDifferences" ).EnumerateArray().Select( d => d.GetString() ).ToArray() );
      Assert.Equal( v8, drift.GetProperty( "outsideLoad" )[1].GetProperty( "runs" ).EnumerateArray().Select( r => r.GetProperty( "medianCpus" ).GetDouble() ).ToArray() );
      Assert.Equal( "The test OutsideLoad_IsTheV7AccountingBitForBit holds the formula that turns CPU counters into outside load equal to the formula of v7, on fixed counters; it does not make the measured load of two sessions equal.", _fixture.Both.Text( "disclosure.dockerd.test" ) );
      Assert.DoesNotContain( "bit for bit", _fixture.Both.Md );
   }

   /// <summary>
   /// The outside-load difference is claimed only when the two sessions' run medians do not overlap: with v8's outside load raised to v7's, the sentence names build and day alone.
   /// </summary>
   [Fact]
   public void TheSessionsAreSaidToDifferInOutsideLoad_OnlyWhenTheirMediansDoNotOverlap()
   {
      ( int exit, _, ConsolidateOutcome? same ) = _fixture.Attempt( "load", results =>
      {
         foreach( string run in RealV8Fixture.V8 )
         {
            string path = Path.Combine( results, run, "results.json" );
            JsonNode root = JsonNode.Parse( File.ReadAllText( path ) )!;
            foreach( JsonNode? pass in root["conditions"]!["passes"]!.AsArray() )
            {
               pass!["outsideLoadDuring"] = 0.21;
            }

            File.WriteAllText( path, root.ToJsonString() );
         }
      } );
      Assert.Equal( 0, exit );
      Assert.Equal( "The sessions differ in build and day, and this test does not separate those from the engines' own drift.", same!.Text( "drift.sessions" ) );
   }

   /// <summary>
   /// Verdict fix-with 8: the clock sentence says the pin is ratio 35 (nominally 3500 MHz) and that the kernel's pass medians centre on 3492 MHz, and not that every CPU runs at 3500 MHz.
   /// </summary>
   [Fact]
   public void TheClockSentence_SaysRatio35_AndWhatTheKernelReads()
   {
      foreach( string session in new[] { "v7", "v8" } )
      {
         string text = _fixture.Both.Text( $"disclosure.clock.held.{session}" )!;
         Assert.Equal( $"In {session}, every run held the clock: turbo off, MSR 0x620 at 0x1e1e, top ratio 35 (nominally 3500 MHz); the median of the kernel's pass medians is 3492 MHz; the ceiling before was 3600 MHz.", text );
         Assert.True( (int)ConsolidateHarness.Call( "Words", text )! <= 35 );
      }

      Assert.DoesNotContain( "every CPU at 3500 MHz", _fixture.Both.Md );
      JsonElement v8 = _fixture.Both.Root.GetProperty( "clock" ).GetProperty( "perSession" ).GetProperty( "v8" );
      Assert.Equal( 35, v8.GetProperty( "pinnedRatio" ).GetInt32() );
      Assert.Equal( 3492, v8.GetProperty( "kernelMedianMhz" ).GetInt32() );
      Assert.Equal( 3492, v8.GetProperty( "kernelMinMhz" ).GetInt32() );
      Assert.True( v8.GetProperty( "kernelMaxMhz" ).GetInt32() >= 3492 );
   }

   /// <summary>
   /// Verdict fix-with 8: the observer's figure is the worst single CPU of a pass, not the engine-group mean; oracle's eight-searcher pass is said to run under the pin on every CPU in every run, with the
   /// range of how far, how far every other pass stayed, and that why is not known; and the mean dip a mean rule flags is stated for v8, whose median rule does not.
   /// </summary>
   [Fact]
   public void TheObserverSentences_GiveTheWorstSingleCpu_TheOracleDip_AndTheMeanDipTheMedianRuleDoesNotFlag()
   {
      Assert.Equal( "By APERF and MPERF, the largest deviation of one CPU in a pass was 0.66%: oracle's eight-searcher pass on CPU 4, at 3476.9 MHz against the pin of 3500 MHz.", _fixture.Both.Text( "disclosure.observer.clock" ) );
      Assert.Equal( "By APERF and MPERF, the largest deviation of one CPU in a pass was 0.73%: oracle's eight-searcher pass on CPU 3, at 3474.3 MHz against the pin of 3500 MHz.", _fixture.Both.Text( "disclosure.observer.clock.v7" ) );
      Assert.Equal( "The observer read MSR 0x620 as 0x1e1e.", _fixture.Both.Text( "disclosure.observer.msr" ) );
      Assert.Equal( "oracle's eight-searcher pass ran 0.27% to 0.66% under the pin on every CPU in all 3 runs of v8; no other pass was more than 0.01% from it, and why is not known.", _fixture.Both.Text( "disclosure.observer.dip" ) );
      Assert.Equal( "oracle's eight-searcher pass ran 0.41% to 0.73% under the pin on every CPU in all 3 runs of v7; no other pass was more than 0.01% from it, and why is not known.", _fixture.Both.Text( "disclosure.observer.dip.v7" ) );
      for( int i = 0; i < 3; i++ )
      {
         Assert.Equal( $"Run {RealV8Fixture.V8[i]}: oracle's eight-searcher pass averaged 3085.5 MHz on the engine CPUs and 3189.8 MHz on the client CPUs, as in v7; a mean rule flags it and the median rule does not.", _fixture.Both.Text( $"disclosure.observer.meandip.{i}" ) );
      }

      Assert.Null( _fixture.Both.Text( "disclosure.observer.meandip.v7.0" ) );
      Assert.DoesNotContain( "largest clock deviation in a pass", _fixture.Both.Md );
      JsonElement observer = _fixture.Both.Root.GetProperty( "observer" );
      Assert.Equal( 4, observer.GetProperty( "cpuWorst" ).GetProperty( "cpu" ).GetInt32() );
      Assert.True( observer.GetProperty( "cpuWorst" ).GetProperty( "under" ).GetBoolean() );
      Assert.True( observer.GetProperty( "dip" ).GetProperty( "everyCpuUnder" ).GetBoolean() );
   }

   /// <summary>
   /// An observer summary without per-CPU figures still gets a clock sentence, and that sentence says whose figure it is: the largest of a CPU group's mean.
   /// </summary>
   [Fact]
   public void AnObserverSummaryWithoutPerCpuFigures_StatesTheGroupFigureAsSuch()
   {
      string summary = Path.Combine( _fixture.Scratch, "summary-groups.json" );
      JsonNode root = JsonNode.Parse( File.ReadAllText( _fixture.ObserverV8 ) )!;
      foreach( JsonNode? run in root["runs"]!.AsArray() )
      {
         run!.AsObject().Remove( "passes" );
      }

      File.WriteAllText( summary, root.ToJsonString() );
      ( int exit, _, ConsolidateOutcome? groups ) = _fixture.Attempt( "groups", _ => { }, "--observer", summary );
      Assert.Equal( 0, exit );
      Assert.Matches( @"^By APERF and MPERF, the largest deviation of a CPU group's mean in a pass was \d+\.\d\d%, and the observer read MSR 0x620 as 0x1e1e\.$", groups!.Text( "disclosure.observer.clock" ) );
      Assert.Null( groups.Text( "disclosure.observer.dip" ) );
   }

   /// <summary>
   /// Verdict fix-with 11: the page states the rounding of a move (it is rounded up to the next basis point, and the largest is shown unrounded and as printed), says the recall hits of the sessions whose
   /// runs record none are derived, and says "1 ordered pair" and not "1 ordered pairs".
   /// </summary>
   [Fact]
   public void ThePage_StatesTheCeilingRounding_TheDerivedRecallHits_AndTheSingularPair()
   {
      Assert.Equal( "Each move is rounded up to the next basis point before it is printed: the largest, 35.815% unrounded, prints as 35.82%.", _fixture.Both.Text( "basis.max.rounding" ) );
      Assert.Equal( "The recall hits of v7 are the recall of each run times 20 queries times 10 hits, rounded; those runs record no hit count.", _fixture.Both.Text( "recall.derived.v7" ) );
      Assert.Null( _fixture.Both.Text( "recall.derived.v8" ) );
      Assert.Equal( "1 ordered pair cleared 1.45 times by 25 bp or less in its lowest session; it is listed as on the line.", _fixture.Both.Text( "drift.onLine" ) );
      Assert.DoesNotContain( "1 ordered pairs", _fixture.Both.Md );
   }

   /// <summary>
   /// Verdict fix-with 12: the page does not say "the engine's .NET client"; it says which engines the benchmark reaches with its own REST code (eight, from their protocol facts), and that the runs of v7, which
   /// record no question-file hash, read the same questions as shown by one truthNdcg in all six runs.
   /// </summary>
   [Fact]
   public void TheSubtitle_NamesTheBenchmarkClient_TheEightRestEngines_AndTheTruthNdcgOfTheSixRuns()
   {
      Assert.Equal( "At 524 vectors each figure is the cost of one request through the benchmark's client for that engine, and does not show how an index scales.", _fixture.Both.Text( "subtitle.scope" ) );
      Assert.DoesNotContain( "engine's .NET client", _fixture.Both.Md );
      Assert.Equal( "Of the 19 engines, 8 are reached through the benchmark's own HttpClient REST code: elasticsearch, vespa, opensearch, chroma, milvus, typesense, clickhouse and weaviate.", _fixture.Both.Text( "subtitle.clients" ) );
      Assert.Equal( "The runs of v7 record no question-file hash, and truthNdcg reads 0.5181699774768911 in all 6 claim runs.", _fixture.Both.Text( "subtitle.truth" ) );
      Assert.Equal( "The runs of v8 read a question file with the same SHA256 hash as design/bench-inputs/questions_golden.json.", _fixture.Both.Text( "subtitle.questions" ) );
   }

   /// <summary>
   /// Verdict fix-with 14: the page says the observer was not pinned, what share of its resident-thread ticks fell on the engine CPUs in each v8 run and the most CPU it used by cgroup, each figure a quote of the
   /// analysis saved for its run.
   /// </summary>
   [Fact]
   public void TheObserverSentence_SaysItWasNotPinned_WithTheSavedAnalysisFigures()
   {
      Assert.Equal( "The observer was not pinned: in the v8 runs 34.8, 33.8 and 33.5 percent of its resident-thread ticks were seen on engine CPUs 2-3,6-7, and it used at most 0.0755 CPUs by cgroup.", _fixture.Both.Text( "disclosure.observer.placement" ) );
      JsonElement[] sources = _fixture.Both.Root.GetProperty( "sentences" ).EnumerateArray().First( s => s.GetProperty( "slot" ).GetString() == "disclosure.observer.placement" ).GetProperty( "sources" ).EnumerateArray().ToArray();
      Assert.Contains( sources, x => x.GetProperty( "kind" ).GetString() == "doc" && x.GetProperty( "ref" ).GetString()!.StartsWith( "doc:design/bench-inputs/observer-placement/analysis-801.txt#", StringComparison.Ordinal ) );
      Assert.Null( _fixture.Both.Text( "disclosure.observer.placement.v7" ) );
      Assert.Null( _fixture.NoObserver.Text( "disclosure.observer.placement" ) );
   }

   /// <summary>
   /// With no saved analysis for a run the page says nothing about the observer's placement (the summary alone cannot support it), and an analysis that is saved but in no form this report reads stops
   /// the consolidation and names the file.
   /// </summary>
   [Fact]
   public void WithoutASavedAnalysis_NothingIsSaidOfThePlacement_AndAMalformedOneIsRefused()
   {
      string absent = _fixture.CopyRepo( "no-analysis" );
      Directory.Delete( Path.Combine( absent, "design", "bench-inputs", "observer-placement" ), recursive: true );
      ( int exit, string log, ConsolidateOutcome? written ) = _fixture.AttemptInRepo( "no-analysis", _ => { }, absent, "--observer", _fixture.ObserverV8 );
      Assert.True( exit == 0, log );
      Assert.Null( written!.Text( "disclosure.observer.placement" ) );
      Assert.NotNull( written.Text( "disclosure.observer.clock" ) );

      string broken = _fixture.CopyRepo( "bad-analysis" );
      string file = Path.Combine( broken, "design", "bench-inputs", "observer-placement", "analysis-802.txt" );
      File.WriteAllText( file, File.ReadAllText( file ).Replace( "The observer is NOT pinned", "The observer is pinned", StringComparison.Ordinal ) );
      ( int refused, string reason, ConsolidateOutcome? none ) = _fixture.AttemptInRepo( "bad-analysis", _ => { }, broken, "--observer", _fixture.ObserverV8 );
      Assert.Equal( 1, refused );
      Assert.Null( none );
      Assert.Contains( "design/bench-inputs/observer-placement/analysis-802.txt is not in the form this report reads", reason );
   }

   /// <summary>
   /// Verdict fix-with 13, Weaviate: its "ef=-1" is printed under the label that says it is documented on a saved page and not read back, with the saved reference table's row for ef among its sources.
   /// </summary>
   [Fact]
   public void WeaviatesEfOfMinusOne_IsLabelledDocumentedAndNotReadBack()
   {
      List<JsonElement> under = _fixture.Both.Root.GetProperty( "sentences" ).EnumerateArray().Where( s => s.GetProperty( "slot" ).GetString()!.StartsWith( "why.effort.weaviate.", StringComparison.Ordinal ) ).ToList();
      int at = under.FindIndex( s => s.GetProperty( "text" ).GetString() == "ef=-1" );
      Assert.True( at > 0 );
      JsonElement label = under[at - 1];
      Assert.Equal( "Documented on a saved page, not read back from the engine:", label.GetProperty( "text" ).GetString() );
      Assert.Contains( label.GetProperty( "sources" ).EnumerateArray().Select( s => s.GetProperty( "ref" ).GetString() ), r => r!.EndsWith( "weaviate-vector-index-reference-2026-10-07.mdx#`ef`", StringComparison.Ordinal ) );
      Assert.DoesNotContain( "Set by a line of code", label.GetProperty( "text" ).GetString() );
   }

   /// <summary>
   /// Verdict fix-with 15: "all with machine control on" stays true, and the page now also says in which sessions the clock was held.
   /// </summary>
   [Fact]
   public void TheBasis_SaysWhichSessionsHeldTheClock()
   {
      Assert.Equal( "The basis holds the 12 runs of sessions v5, v6, v7 and v8, all with machine control on.", _fixture.Both.Text( "basis.runs" ) );
      Assert.Equal( "The clock was held in the runs of v7 and v8 and not in those of v5 and v6.", _fixture.Both.Text( "basis.clock" ) );
   }

   /// <summary>
   /// Verdict fix-with 5: Chroma's recorded "chroma_version" row is shown as what it is, the version its API reports, beside the how column that names the endpoint; no row is called plain chroma_version.
   /// </summary>
   [Fact]
   public void ChromasVersionRow_IsLabelledTheApiVersionReported()
   {
      JsonElement chroma = _fixture.Both.Root.GetProperty( "engineSettings" ).EnumerateArray().First( r => r.GetProperty( "target" ).GetString() == "chroma" );
      JsonElement row = chroma.GetProperty( "settings" ).EnumerateArray().First( x => x.GetProperty( "key" ).GetString()!.StartsWith( "chroma_version", StringComparison.Ordinal ) );
      Assert.Equal( "chroma_version (API version reported)", row.GetProperty( "key" ).GetString() );
      Assert.Contains( "GET /api/v2/version", row.GetProperty( "how" ).GetString() );
      Assert.DoesNotContain( "| chroma_version |", _fixture.Both.Md );
      Assert.Contains( "| chroma | chroma_version (API version reported) |", _fixture.Both.Md );
   }

   /// <summary>
   /// Verdict fix-with 13: a clause that begins "Measured 2026-10-04" and that nothing saved backs is no longer printed under a label that says it was not measured; its label says it cites a measurement these
   /// runs did not repeat.
   /// </summary>
   [Fact]
   public void AMeasurementClauseNothingBacks_IsLabelledAsCitingAMeasurement()
   {
      List<JsonElement> sentences = _fixture.Both.Root.GetProperty( "sentences" ).EnumerateArray().ToList();
      const string PLAIN = "Typed in the program's own text; not read back, measured or backed by a saved source:";
      const string CITES = "Typed in the program's own text; it cites a measurement that these runs did not repeat, and no saved source backs this clause:";
      var measured = new List<string>();
      for( int i = 1; i < sentences.Count; i++ )
      {
         string text = sentences[i].GetProperty( "text" ).GetString()!;
         string slot = sentences[i].GetProperty( "slot" ).GetString()!;
         if( slot.StartsWith( "disclosure.durability.", StringComparison.Ordinal ) && System.Text.RegularExpressions.Regex.IsMatch( slot, @"\.q\d+$" ) && text.StartsWith( "Measured 2026-10-04", StringComparison.Ordinal ) )
         {
            string group = slot[..slot.LastIndexOf( ".q", StringComparison.Ordinal )];
            string label = sentences.First( x => x.GetProperty( "slot" ).GetString() == group ).GetProperty( "text" ).GetString()!;
            measured.Add( slot );
            Assert.NotEqual( PLAIN, label );
            Assert.True( label == CITES || label.StartsWith( "Backed by a saved log", StringComparison.Ordinal ), $"{slot}: {label}" );
         }
      }

      Assert.True( measured.Count >= 3, string.Join( ", ", measured ) );
      Assert.Contains( CITES, sentences.Select( x => x.GetProperty( "text" ).GetString() ) );
   }

   /// <summary>
   /// Verdict fix-with 9: the tables come first. The first table heading lies before the threshold and basis heading, the recall and why sections lie before it too, and the one sentence that says what "not separated"
   /// means stays above the tables.
   /// </summary>
   [Fact]
   public void TheResultsLead_AndTheThresholdAndBasisComeAfterTheTables()
   {
      string md = _fixture.Both.Md;
      int p50 = md.IndexOf( "\n## p50\n", StringComparison.Ordinal );
      int exact = md.IndexOf( "\n## exact p50\n", StringComparison.Ordinal );
      int recall = md.IndexOf( "\n## Recall\n", StringComparison.Ordinal );
      int why = md.IndexOf( "\n## Why some engines beat others", StringComparison.Ordinal );
      int threshold = md.IndexOf( "\n## Threshold and basis\n", StringComparison.Ordinal );
      int rule = md.IndexOf( _fixture.Both.Text( "rule.claim" )!, StringComparison.Ordinal );
      Assert.True( p50 > 0 && exact > p50 && recall > exact && why > recall && threshold > why, $"{p50} {exact} {recall} {why} {threshold}" );
      Assert.True( rule > 0 && rule < p50, $"the claim rule sentence is at {rule}, the first table at {p50}" );
      Assert.True( md.IndexOf( _fixture.Both.Text( "rule.threshold" )!, StringComparison.Ordinal ) > threshold );
      Assert.True( md.IndexOf( "\n## Drift between the sessions\n", StringComparison.Ordinal ) > threshold );
   }

   /// <summary>
   /// Verdict fix-with 3 and the brief: the page names the build that measured v8 (the commit its runs record) and the build that consolidated the report, and consolidated.json records both with the SHA-256 of
   /// every results.json it read.
   /// </summary>
   [Fact]
   public void ThePage_NamesTheBuildThatMeasuredAndTheBuildThatConsolidated_AndTheHashOfEveryInput()
   {
      Assert.Equal( "The runs of v8 were measured by build 9b924200abc3.", _fixture.Both.Text( "subtitle.build.v8" ) );
      Assert.Null( _fixture.Both.Text( "subtitle.build.v7" ) );
      Assert.Equal( "This report was consolidated by build fedcba987654, not by the build that measured the runs of v8.", _fixture.Both.Text( "subtitle.build.consolidated" ) );
      JsonElement build = _fixture.Both.Root.GetProperty( "build" );
      Assert.Equal( ConsolidateHarness.BUILD_COMMIT, build.GetProperty( "consolidatedBy" ).GetProperty( "commit" ).GetString() );
      Assert.Equal( "9b924200abc3f2c3e41c2a9520e5c7bb16748e6c", build.GetProperty( "measured" )[1].GetProperty( "commit" ).GetString() );
      Assert.Equal( JsonValueKind.Null, build.GetProperty( "measured" )[0].GetProperty( "commit" ).ValueKind );
      foreach( JsonElement run in _fixture.Both.Root.GetProperty( "sessions" ).EnumerateArray().SelectMany( s => s.GetProperty( "runs" ).EnumerateArray() ).Concat( _fixture.Both.Root.GetProperty( "basis" ).GetProperty( "runs" ).EnumerateArray() ) )
      {
         string file = Path.Combine( _fixture.Results, run.GetProperty( "folder" ).GetString()!, "results.json" );
         Assert.Equal( Convert.ToHexString( System.Security.Cryptography.SHA256.HashData( File.ReadAllBytes( file ) ) ).ToLowerInvariant(), run.GetProperty( "resultsSha256" ).GetString() );
      }

      Assert.Equal( "fedcba9", (string)ConsolidateHarness.Call( "CommitOf", "1.0.0+fedcba9" )! );
      Assert.Equal( "null", (string)ConsolidateHarness.Call( "CommitOf", "1.0.0" )! );
      Assert.Equal( "null", (string)ConsolidateHarness.Call( "CommitOf", "1.0.0+not-a-hash" )! );
   }

   /// <summary>
   /// The build sentences cover the other cases too: a consolidating build that is the one that measured the runs says so, and when no session records a commit the page names the consolidating build alone.
   /// </summary>
   [Fact]
   public void TheBuildSentences_CoverAConsolidatorThatAlsoMeasured_AndRunsThatRecordNoCommit()
   {
      ( int exit, string log, ConsolidateOutcome? same ) = _fixture.Attempt( "same-build", results =>
      {
         foreach( string run in RealV8Fixture.V8 )
         {
            string path = Path.Combine( results, run, "results.json" );
            JsonNode root = JsonNode.Parse( File.ReadAllText( path ) )!;
            root["conditions"]!["build"]!["commit"] = ConsolidateHarness.BUILD_COMMIT;
            root["conditions"]!["build"]!["informationalVersion"] = "1.0.0+" + ConsolidateHarness.BUILD_COMMIT;
            File.WriteAllText( path, root.ToJsonString() );
         }
      } );
      Assert.True( exit == 0, log );
      Assert.Equal( "This report was consolidated by build fedcba987654, which also measured the runs of v8.", same!.Text( "subtitle.build.consolidated" ) );

      ( int exitNone, string logNone, ConsolidateOutcome? none ) = _fixture.Attempt( "no-commit", results =>
      {
         foreach( string run in RealV8Fixture.V8 )
         {
            string path = Path.Combine( results, run, "results.json" );
            JsonNode root = JsonNode.Parse( File.ReadAllText( path ) )!;
            root["conditions"]!["build"]!.AsObject().Remove( "commit" );
            File.WriteAllText( path, root.ToJsonString() );
         }
      } );
      Assert.True( exitNone == 0, logNone );
      Assert.Equal( "This report was consolidated by build fedcba987654.", none!.Text( "subtitle.build.consolidated" ) );
      Assert.Null( none.Text( "subtitle.build.v8" ) );
   }

   /// <summary>
   /// Every sentence the fixes add holds the generated-text rules: audited against the raw files (the consolidation would have refused otherwise), printed in the markdown, no banned, provenance, purpose, default
   /// or "only" word, no dash, within its word limit.
   /// </summary>
   [Fact]
   public void EverySentence_HoldsTheGeneratedTextRules()
   {
      JsonElement root = _fixture.Both.Root;
      Assert.Empty( root.GetProperty( "audit" ).GetProperty( "failures" ).EnumerateArray() );
      var forbidden = new System.Text.RegularExpressions.Regex( @"(?<![A-Za-z0-9_@])(only|default|defaults|purpose|in order to|so that|intended|designed to)(?![A-Za-z0-9_@])", System.Text.RegularExpressions.RegexOptions.IgnoreCase );
      int checkedCount = 0;
      foreach( JsonElement s in root.GetProperty( "sentences" ).EnumerateArray() )
      {
         string slot = s.GetProperty( "slot" ).GetString()!;
         string text = s.GetProperty( "text" ).GetString()!;
         Assert.Contains( text, _fixture.Both.Md );
         if( s.GetProperty( "sources" ).EnumerateArray().Any( x => x.GetProperty( "kind" ).GetString() == "quote" ) )
         {
            continue;
         }

         checkedCount++;
         Assert.Empty( (string[])ConsolidateHarness.Call( "Banned", text )! );
         Assert.False( forbidden.IsMatch( text ), $"{slot}: {text}" );
         Assert.True( (int)ConsolidateHarness.Call( "Words", text )! <= ( slot == "headline" ? 34 : 35 ), $"{slot}: {text}" );
         Assert.DoesNotContain( '\u2014', text );
         Assert.DoesNotContain( '\u2013', text );
      }

      Assert.True( checkedCount > 250, $"{checkedCount} sentences checked" );
      Assert.DoesNotContain( '\u2014', _fixture.Both.Md );
   }

   /// <summary>
   /// The saved analyses the observer sentence quotes are listed with their hash in design/bench-inputs/SHA256SUMS and match it, and where the operator's original still exists on this machine it is the same file.
   /// </summary>
   [Fact]
   public void TheSavedObserverAnalyses_MatchTheirListedHashes_AndTheirOriginals()
   {
      string root = ConsolidateHarness.RepoRoot();
      string inputs = Path.Combine( root, "design", "bench-inputs" );
      Dictionary<string, string> listed = File.ReadAllLines( Path.Combine( inputs, "SHA256SUMS" ) ).Where( l => l.Length > 66 ).ToDictionary( l => l[66..].Trim(), l => l[..64] );
      foreach( string seed in new[] { "801", "802", "803" } )
      {
         string relative = $"observer-placement/analysis-{seed}.txt";
         Assert.True( listed.ContainsKey( relative ), relative );
         byte[] bytes = File.ReadAllBytes( Path.Combine( inputs, relative ) );
         Assert.Equal( listed[relative], Convert.ToHexString( System.Security.Cryptography.SHA256.HashData( bytes ) ).ToLowerInvariant() );
         string original = $"/home/dan/gvb-work/lanes/v8-final/observer/analysis-{seed}.txt";
         if( File.Exists( original ) )
         {
            Assert.Equal( File.ReadAllBytes( original ), bytes );
         }
      }

      Assert.Contains( "observer-placement/", File.ReadAllText( Path.Combine( inputs, "SOURCES.txt" ) ) );
   }

   /// <summary>
   /// The files these fixes add or change hold no em or en dash and no name of the former employer.
   /// </summary>
   [Fact]
   public void TheFilesOfTheseFixes_HoldNoDashesOrForbiddenNames()
   {
      string root = ConsolidateHarness.RepoRoot();
      string src = "src/GenericVectorBuilder.Bench/";
      string[] files =
      {
         src + "Report/SettleWarning.cs", src + "Report/Sentence.cs", src + "Report/RunResult.cs", src + "Report/ConsolidatedMarkdown.cs", src + "Report/engine-facts.json",
         src + "Stats/ConsolidateResetText.cs", src + "Stats/ConsolidateNotHeld.cs", src + "Stats/ConsolidateObserverClock.cs", src + "Stats/ConsolidateObserverPlacement.cs", src + "Stats/ConsolidateSessions.cs",
         src + "Stats/ConsolidateBuild.cs", src + "Stats/ConsolidateText.cs", src + "Stats/ConsolidateTextParts.cs", src + "Stats/ConsolidateTextDisclosures.cs", src + "Stats/ConsolidateTextClauses.cs",
         src + "Stats/ConsolidateSettings.cs", src + "Stats/ConsolidateClock.cs", src + "Stats/ConsolidateFlags.cs", src + "Stats/ThresholdBasis.cs", src + "Stats/Consolidator.cs", src + "Stats/ConsolidatedReport.cs",
         "deploy/bench/recorded-text-classes.json", "design/bench-inputs/SOURCES.txt", "design/bench-inputs/SHA256SUMS", "design/bench-inputs/recorded-text-classes-source/classes_spec.py",
         "design/bench-inputs/observer-placement/analysis-801.txt", "design/bench-inputs/observer-placement/analysis-802.txt", "design/bench-inputs/observer-placement/analysis-803.txt",
         "tests/GenericVectorBuilder.Engines.Tests/Bench/ConsolidateV8FixerTests.cs", "tests/GenericVectorBuilder.Engines.Tests/Bench/ConsolidateRealV8Fixture.cs", "tests/GenericVectorBuilder.Engines.Tests/Bench/EngineFactsV8Tests.cs",
      };
      var names = new System.Text.RegularExpressions.Regex( @"(?i)\b" + "bs" + "&?a\\b|" + "fal" + "con|" + "dwe" + "aver" );
      foreach( string file in files )
      {
         string text = File.ReadAllText( Path.Combine( root, file ) );
         Assert.False( text.Contains( '\u2014' ) || text.Contains( '\u2013' ), $"{file} contains an em or en dash" );
         Assert.False( names.IsMatch( text ), $"{file} names the former employer" );
      }
   }

   #endregion Public Methods

   #region Private Methods

   /// <summary>The recorded form of the folder state that a stable reset text carries.</summary>
   private const string RESET_STABLE = "(the folder was steady)";

   /// <summary>The recorded form of the folder state of a reset that had not settled.</summary>
   private const string RESET_STILL = "(the folder was STILL CHANGING at the deadline)";

   /// <summary>
   /// The median over a real run's passes of the load outside the benchmark during the pass, recomputed here from the raw file.
   /// </summary>
   /// <param name="run">The run folder.</param>
   /// <returns>The median, CPUs.</returns>
   private double RunMedian( string run )
   {
      using JsonDocument document = JsonDocument.Parse( File.ReadAllText( Path.Combine( _fixture.Results, run, "results.json" ) ) );
      double[] values = document.RootElement.GetProperty( "conditions" ).GetProperty( "passes" ).EnumerateArray().Select( p => p.GetProperty( "outsideLoadDuring" ).GetDouble() ).OrderBy( v => v ).ToArray();
      int middle = values.Length / 2;
      return values.Length % 2 == 1 ? values[middle] : ( values[middle - 1] + values[middle] ) / 2;
   }

   /// <summary>
   /// A number with the decimals it has, up to four.
   /// </summary>
   /// <param name="value">The number.</param>
   /// <returns>The text.</returns>
   private static string Exact( double value )
   {
      return decimal.Round( (decimal)value, 4, MidpointRounding.AwayFromZero ).ToString( "0.####", System.Globalization.CultureInfo.InvariantCulture );
   }

   /// <summary>
   /// The ClickHouse target of a real v8 run, read from its results.json.
   /// </summary>
   /// <param name="run">The run folder.</param>
   /// <returns>The target object.</returns>
   private JsonElement ClickHouse( string run )
   {
      using JsonDocument document = JsonDocument.Parse( File.ReadAllText( Path.Combine( _fixture.Results, run, "results.json" ) ) );
      return document.RootElement.GetProperty( "targets" ).EnumerateArray().First( t => t.GetProperty( "name" ).GetString() == "clickhouse" ).Clone();
   }

   #endregion Private Methods
}
