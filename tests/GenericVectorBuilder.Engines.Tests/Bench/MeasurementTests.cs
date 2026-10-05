using System.Globalization;
using System.Reflection;
using System.Runtime.Loader;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;

namespace GenericVectorBuilder.Engines.Tests.Bench;

/// <summary>
/// The benchmark's measurement rules, checked against the real runner code with a fake sink
/// whose latency follows a script: p50/p95/p99 and QPS@1 come from one window that holds every
/// timed default@1 search, the exact mode is a time window that cycles the queries, every pass
/// type is rehearsed at its own concurrency before any timed pass, every timed pass follows a
/// time-based warm-up at its own concurrency and (after a preparation) a settle check that
/// compares a trial of that same pass with the warm-up's settled figure and extends the warm-up
/// once when they differ by more than 10%, each pass is recorded with its window, its searches
/// and the pass before it, and lopsided or thin windows are flagged.
/// Why these rules get tests: each one closes a hole a review found (a p50 from a separate
/// burst, an exact p50 from 20 searches, a client compiled during the first timed pass, JVM
/// engines timed cold when their 8-searcher pass ran first, a settle at one searcher that said
/// nothing about eight), and each is easy to undo by accident.
/// Why the sources are compiled here with Roslyn: the benchmark is a console project this test
/// project does not reference. Compiling the repository's Bench sources together with
/// MeasurementScenarios.cs (fakes, built only in this compilation) tests the code the benchmark runs.
/// </summary>
public class MeasurementTests
{
   #region Data Members

   private const string SCENARIOS_TYPE = "GenericVectorBuilder.Bench.MeasurementUnderTest.MeasurementScenarios";
   private const string IMPLICIT_USINGS = "global using System; global using System.Collections.Generic; global using System.IO; global using System.Linq; "
      + "global using System.Net.Http; global using System.Threading; global using System.Threading.Tasks;";
   private static readonly Regex WARMUP_START = new( @"warm-up before (\S+), \d+ searches", RegexOptions.Compiled );
   private static readonly Regex MACHINE_CONTROL_WARMUP = new( @"^\s*(?<target>[^\s:]+): warm-up before (?<pass>[^\s,]+), \d+ searches", RegexOptions.Compiled );
   private static readonly Regex REHEARSED = new( @"(\S+) [\d,]+ searches in ([\d.]+) s", RegexOptions.Compiled );
   private static readonly Lazy<Assembly> COMPILED = new( Compile );

   #endregion Data Members

   #region Public Methods

   /// <summary>
   /// The benchmark's own lengths: a 30 s rehearsal per pass type, a 15 s warm-up before every
   /// pass read in windows of 2 s, a warm-up cap of 120 s, a 3 s trial within 10%, and one
   /// extension of 30 s to 120 s; a runner built with no settings carries exactly these.
   /// </summary>
   [Fact]
   public void Defaults_ThirtySecondRehearsal_FifteenSecondWarmup_TenPercentTrial()
   {
      double[] defaults = (double[])Invoke( "Defaults" );
      Assert.Equal( new[] { 30.0, 15, 2, 120, 3, 0.10, 30, 120, 30, 15, 2, 120, 3, 30, 120 }, defaults );
   }

   /// <summary>
   /// The settled figure is the median of the last three window figures (of fewer when fewer ran,
   /// none when none), and a trial agrees when the settled figure is within 10% of the trial's.
   /// </summary>
   [Fact]
   public void SettleCheck_Rules()
   {
      Assert.Equal( 2.0, Rules( new[] { 5.0, 1, 3, 2 }, 0, 0 )[0] );
      Assert.Equal( 4.0, Rules( new[] { 4.0 }, 0, 0 )[0] );
      Assert.Equal( 1.5, Rules( new[] { 1.0, 2 }, 0, 0 )[0] );
      Assert.True( double.IsNaN( Rules( Array.Empty<double>(), 0, 0 )[0] ) );
      Assert.Equal( 1.0, Rules( Array.Empty<double>(), 1.099, 1.0 )[1] );
      Assert.Equal( 1.0, Rules( Array.Empty<double>(), 0.901, 1.0 )[1] );
      Assert.Equal( 0.0, Rules( Array.Empty<double>(), 1.101, 1.0 )[1] );
      Assert.Equal( 0.0, Rules( Array.Empty<double>(), 0.899, 1.0 )[1] );
      Assert.Equal( 0.0, Rules( Array.Empty<double>(), 1550, 914 )[1] );
      Assert.Equal( 0.0, Rules( Array.Empty<double>(), 1.0, 0 )[1] );
   }

   /// <summary>
   /// Every timed pass follows a warm-up that lasts at least its set time (here 0.6 s), sends
   /// far more than the --warmup count, uses only the pass's own search, and keeps exactly the
   /// pass's number of searchers busy (4 for default@4, 1 for default@1 and exact); its progress
   /// line still matches the line machine control holds a pass at.
   /// </summary>
   [Fact]
   public async Task Warmup_IsTimeBasedAtThePassConcurrency()
   {
      dynamic r = await ScenarioAsync( "PassesAsync", Args( "--seconds", "1", "--concurrency", "1,4", "--queries", "random:5", "--exact-seconds", "1" ), "fixed:2", "fixed:2", false, 0.6 );
      string[] events = r.Events;
      long[] ticks = r.Ticks;
      int[] inFlight = r.InFlight;
      double frequency = (long)r.Frequency;
      foreach( string pass in (string[])r.PassOrder )
      {
         int line = Array.FindIndex( events, e => e.Contains( $": warm-up before {pass}, ", StringComparison.Ordinal ) );
         int timing = Array.FindIndex( events, line + 1, e => e.EndsWith( $"timing {pass}", StringComparison.Ordinal ) );
         Assert.True( line >= 0 && timing > line, string.Join( "\n", events.Where( e => e.StartsWith( "LOG ", StringComparison.Ordinal ) ) ) );
         Assert.Matches( MACHINE_CONTROL_WARMUP, events[line]["LOG ".Length..] );
         int[] searches = Enumerable.Range( line + 1, timing - line - 1 ).Where( i => !events[i].StartsWith( "LOG ", StringComparison.Ordinal ) ).ToArray();
         Assert.All( searches, i => Assert.Equal( pass == "exact" ? "X" : "S", events[i] ) );
         Assert.True( searches.Length > 3 * 10, $"{pass}: warm-up sent only {searches.Length} searches" );
         double seconds = ( ticks[searches[^1]] - ticks[line] ) / frequency;
         Assert.True( seconds >= 0.55, $"{pass}: warm-up lasted {seconds:0.000} s, less than its 0.6 s" );
         Assert.Equal( pass == "default@4" ? 4 : 1, searches.Max( i => inFlight[i] ) );
      }
   }

   /// <summary>
   /// Before every timed pass (not just the first) a settle check runs at that pass's own
   /// concurrency: its trial uses the pass's searchers (4 in flight for default@4), the warm-up it
   /// reads ran at the same concurrency, it sits between the pass's warm-up and its timing, and
   /// the one settle note names every pass and reads as settled exactly when every check was
   /// confirmed. Why the verdict itself is not asserted: on a loaded test box a steady 5 ms fake
   /// can fairly wander past 5% between windows; the verdicts have tests with latencies far apart.
   /// </summary>
   [Fact]
   public async Task SettleCheck_BeforeEveryPassAtItsOwnConcurrency()
   {
      dynamic r = await ScenarioAsync( "CheckAsync", "fixed:5", 8, 0.5, 3.0 );
      string[] order = r.PassOrder;
      string[][] checks = ( (string[])r.Checks ).Select( c => c.Split( '|' ) ).ToArray();
      Assert.Equal( order, checks.Select( c => c[0] ) );
      string[] events = r.Events;
      int[] inFlight = r.InFlight;
      foreach( string[] check in checks )
      {
         int searchers = check[0] == "default@4" ? 4 : 1;
         Assert.Equal( searchers.ToString( CultureInfo.InvariantCulture ), check[1] );
         Assert.Equal( searchers.ToString( CultureInfo.InvariantCulture ), check[11] );
         Assert.Contains( $"{check[0]}: warm-up ", (string)r.Note );
         int warmup = Array.FindIndex( events, e => e.Contains( $": warm-up before {check[0]}, ", StringComparison.Ordinal ) );
         int trial = Array.FindIndex( events, e => e.Contains( $": settle check before {check[0]}: trial, ", StringComparison.Ordinal ) );
         int timing = Array.FindIndex( events, e => e.EndsWith( $"timing {check[0]}", StringComparison.Ordinal ) );
         Assert.True( warmup < trial && trial < timing, string.Join( "\n", events.Where( e => e.StartsWith( "LOG ", StringComparison.Ordinal ) ) ) );
         int end = Array.FindIndex( events, trial + 1, e => e.StartsWith( "LOG ", StringComparison.Ordinal ) );
         Assert.Equal( searchers, Enumerable.Range( trial + 1, end - trial - 1 ).Max( i => inFlight[i] ) );
      }

      Assert.Equal( "default@4", order[0] );
      Assert.Equal( checks.All( c => c[10] == "True" ), (bool?)r.ReadSettled );
   }

   /// <summary>
   /// The v5 Vespa case at 4 searchers: 12 ms per search through the warm-up, 4 ms from the trial
   /// on. The warm-up's settled QPS is well below the trial's, so the check extends the warm-up
   /// once (for at least its minimum, at the pass's concurrency), the second trial agrees, the
   /// warm-up is announced and run again before the timing (so the quiet-box check and the
   /// warm-up still come right before the timed pass), and the note says settled, with the
   /// extension named. The next pass, already fast, is not extended.
   /// </summary>
   [Fact]
   public async Task SettleCheck_LateSpeedUpExtendsOnce()
   {
      dynamic r = await ScenarioAsync( "CheckAsync", "drop:12:4", 8, 0.5, 3.0 );
      string[] order = r.PassOrder;
      Assert.Equal( "default@4", order[0] );
      string[][] checks = ( (string[])r.Checks ).Select( c => c.Split( '|' ) ).ToArray();
      string[] first = checks[0];
      double settled = double.Parse( first[3], CultureInfo.InvariantCulture );
      double trial = double.Parse( first[4], CultureInfo.InvariantCulture );
      Assert.True( settled < 0.7 * trial, $"settled {settled} QPS, trial {trial} QPS" );
      Assert.Equal( "False", first[5] );
      Assert.Equal( "True", first[6] );
      Assert.InRange( double.Parse( first[7], CultureInfo.InvariantCulture ), 0.5, 3.5 );
      Assert.Equal( "True", first[9] );
      Assert.Equal( "True", first[10] );
      Assert.Equal( "False", checks[1][6] );
      string note = r.Note;
      Assert.StartsWith( "Settle: settled before every timed pass", note );
      Assert.Contains( "EXTENDED once", note );
      Assert.DoesNotContain( "NOT settled", note );
      Assert.True( (bool?)r.ReadSettled );
      string[] log = ( (string[])r.Events ).Where( e => e.StartsWith( "LOG ", StringComparison.Ordinal ) ).ToArray();
      int extending = Array.FindIndex( log, l => l.Contains( "before default@4: extending the warm-up once", StringComparison.Ordinal ) );
      int again = Array.FindIndex( log, l => l.Contains( "warm-up before default@4, 3 searches and 1.5 s at least, 4 searchers (again, after the settle extension)", StringComparison.Ordinal ) );
      int timing = Array.FindIndex( log, l => l.EndsWith( "timing default@4", StringComparison.Ordinal ) );
      Assert.True( extending > 0 && extending < again && again < timing, string.Join( "\n", log ) );
      Assert.Equal( 2, log.Take( timing ).Count( l => l.Contains( ": warm-up before default@4, ", StringComparison.Ordinal ) ) );
   }

   /// <summary>
   /// An engine whose latency keeps climbing: the warm-up's windows never agree, the one
   /// extension hits its cap, and the note is the WARNING the consolidation reads as not settled,
   /// naming the pass; the passes are still timed.
   /// </summary>
   [Fact]
   public async Task SettleCheck_NeverAgreeingIsFlagged()
   {
      dynamic r = await ScenarioAsync( "CheckAsync", "climb", 8, 0.3, 0.8 );
      string[][] checks = ( (string[])r.Checks ).Select( c => c.Split( '|' ) ).ToArray();
      Assert.Equal( "True", checks[0][6] );
      Assert.Equal( "False", checks[0][10] );
      Assert.StartsWith( "WARNING: latency had NOT settled when timing began for default@4", (string)r.Note );
      Assert.False( (bool?)r.ReadSettled );
      Assert.Equal( 2, ( (string[])r.PassOrder ).Length );
   }

   /// <summary>
   /// An engine that warms up only under concurrent load (9 ms per search until it has served
   /// 300 searches with others in flight, 3 ms after), the way the JVM engines did: whether
   /// default@4 runs first (seed 8) or last (seed 1), the engine has already served more than
   /// those 300 concurrent searches when default@4's timing begins, so the pass is timed warm,
   /// and QPS@4 is well above the cold ceiling of 4 / 9 ms = 444. Why the served count and not
   /// the two QPS against each other: on a loaded test box a 3 ms timer wait wanders by tens of
   /// percent between runs, while the count says exactly whether the warm-up did its job.
   /// </summary>
   [Fact]
   public async Task JvmLikeEngine_TimedWarmWhetherFirstOrLast()
   {
      dynamic first = await ScenarioAsync( "OrderAsync", 8, "jit:9:3:300" );
      dynamic last = await ScenarioAsync( "OrderAsync", 1, "jit:9:3:300" );
      Assert.Equal( "default@4", ( (string[])first.PassOrder )[0] );
      Assert.Equal( "default@4", ( (string[])last.PassOrder )[^1] );
      Assert.True( (int)first.ServedAtTiming > 300, $"default@4 first: {first.ServedAtTiming} concurrent searches served when its timing began" );
      Assert.True( (int)last.ServedAtTiming > 300, $"default@4 last: {last.ServedAtTiming} concurrent searches served when its timing began" );
      double a = ( (Dictionary<int, double>)first.Qps )[4];
      double b = ( (Dictionary<int, double>)last.Qps )[4];
      Assert.True( a > 1.5 * 444 && b > 1.5 * 444, $"QPS@4 {a:0} first, {b:0} last; cold is about 444" );
   }

   /// <summary>
   /// Every settle note reads right in the consolidation: settled when every check confirmed its
   /// warm-up (with or without the extension); not settled when the extension did not help, when
   /// the searches kept failing (then nothing is extended), or when no check ran.
   /// </summary>
   [Fact]
   public void SettleNote_ReadByConsolidation()
   {
      foreach( (string mode, bool settled, string start) in new[] { ( "agreed", true, "Settle: settled before every timed pass" ), ( "extended-ok", true, "Settle: settled before every timed pass" ),
         ( "extended-bad", false, "WARNING: latency had NOT settled when timing began for default@8" ), ( "gave-up", false, "WARNING: latency had NOT settled when timing began for default@8" ),
         ( "none", false, "WARNING: no settle ran" ) } )
      {
         dynamic n = Invoke( "SettleNote", mode );
         Assert.StartsWith( start, (string)n.Note );
         Assert.Equal( settled, (bool?)n.ReadSettled );
         Assert.DoesNotContain( "\u2014", (string)n.Note );
      }

      string ok = ( (dynamic)Invoke( "SettleNote", "extended-ok" ) ).Note;
      Assert.Contains( "trial of 8,700 searches in 3.0 s at 8 searchers: 2,900 QPS against the settled 1,524 QPS, 47% apart (limit 10%)", ok );
      Assert.Contains( "EXTENDED once", ok );
      Assert.Contains( "p50 1.871 ms against the settled p50 1.870 ms", ok );
      Assert.Contains( "not extended, because the searches kept failing", (string)( (dynamic)Invoke( "SettleNote", "gave-up" ) ).Note );
   }

   /// <summary>
   /// The run's method notes quote the runner's own lengths and limits, and none of the words
   /// the consolidation reads as a target flag.
   /// </summary>
   [Fact]
   public void MethodNotes_QuoteTheRunnerLengths()
   {
      string[] notes = (string[])Invoke( "MethodNotes", 20 );
      string all = string.Join( " ", notes );
      Assert.Contains( "for 30 s each", all );
      Assert.Contains( "for at least 15 s and at least 20 searches", all );
      Assert.Contains( "within 10%", all );
      Assert.Contains( "at least 30 s, until its windows agree, at most 120 s", all );
      Assert.DoesNotContain( "NOT settled", all );
      Assert.DoesNotContain( "\u2014", all );
   }

   /// <summary>
   /// Every timed default@1 search is a latency sample: the searches sent between "timing
   /// default@1" and the next warm-up equal the samples, QPS@1 is those samples over the same
   /// window's seconds, and no search is sent outside a warm-up or a timed window (no separate
   /// latency burst). With no warm-up time the warm-up is exactly the --warmup count.
   /// </summary>
   [Fact]
   public async Task LatencyAndQps1_ComeFromOneWindow()
   {
      dynamic r = await ScenarioAsync( "PassesAsync", Args( "--seconds", "2", "--concurrency", "1,4", "--queries", "random:5", "--exact-seconds", "0" ), "fixed:1", null, false, 0.0 );
      string[] events = r.Events;
      string[] one = Record( r, "default@1" );
      string[] four = Record( r, "default@4" );
      int samples = r.LatencySamples;
      Assert.Equal( samples, TimedSearches( events, "default@1" ) );
      Assert.Equal( samples, int.Parse( one[5], CultureInfo.InvariantCulture ) );
      Assert.True( samples >= 200, $"only {samples} samples in a 2 s window of 1 ms searches" );
      double seconds = double.Parse( one[4], CultureInfo.InvariantCulture );
      Assert.True( seconds >= 2.0, $"default@1 window lasted {seconds} s" );
      Dictionary<int, double> qps = r.Qps;
      Assert.Equal( samples / seconds, qps[1], 6 );
      Assert.True( (double)r.P50 <= 1000 / qps[1] * 1.05, $"p50 {r.P50} ms against {1000 / qps[1]} ms per search in the same window" );
      Assert.Equal( 1.0, (double)r.Recall );
      int warmups = 2 * 3;
      Assert.Equal( warmups + samples + int.Parse( four[5], CultureInfo.InvariantCulture ), events.Count( e => e == "S" ) );
      Assert.DoesNotContain( (string[])r.Flags, f => f.Contains( "default@1", StringComparison.Ordinal ) );
   }

   /// <summary>
   /// QPS@1 is only written when 1 is a concurrency level, but the latency window still runs.
   /// </summary>
   [Fact]
   public async Task Qps1_OnlyWhenOneIsALevel()
   {
      dynamic r = await ScenarioAsync( "PassesAsync", Args( "--seconds", "1", "--concurrency", "4", "--queries", "random:5", "--exact-seconds", "0" ), "fixed:1", null, false, 0.0 );
      Dictionary<int, double> qps = r.Qps;
      Assert.False( qps.ContainsKey( 1 ) );
      Assert.True( qps.ContainsKey( 4 ) );
      Assert.True( (int)r.LatencySamples > 0 );
   }

   /// <summary>
   /// The exact mode runs for --exact-seconds, cycling the queries: with 5 queries and 1 ms
   /// searches it times hundreds of searches, every exact search in the window is a sample, and
   /// its recall is still scored over every query.
   /// </summary>
   [Fact]
   public async Task Exact_IsATimeWindowCyclingTheQueries()
   {
      dynamic r = await ScenarioAsync( "PassesAsync", Args( "--seconds", "1", "--concurrency", "1", "--queries", "random:5", "--exact-seconds", "2" ), "fixed:1", "fixed:1", false, 0.0 );
      string[] exact = Record( r, "exact" );
      int samples = r.ExactQueries;
      Assert.True( samples >= 200, $"exact timed {samples} searches in 2 s of 1 ms searches" );
      Assert.Equal( samples, TimedSearches( (string[])r.Events, "exact" ) );
      Assert.True( double.Parse( exact[4], CultureInfo.InvariantCulture ) >= 2.0 );
      Assert.Equal( 1.0, (double)r.ExactRecall );
      Assert.DoesNotContain( (string[])r.Flags, f => f.Contains( "exact", StringComparison.Ordinal ) );
   }

   /// <summary>
   /// A slow exact mode that cannot reach 200 samples, or answer every query, within its window
   /// (at most twice the exact seconds) is flagged for both.
   /// </summary>
   [Fact]
   public async Task Exact_ThinWindowFlagged()
   {
      dynamic r = await ScenarioAsync( "PassesAsync", Args( "--seconds", "1", "--concurrency", "1", "--queries", "random:20", "--exact-seconds", "1" ), "fixed:1", "fixed:150", false, 0.0 );
      string[] flags = r.Flags;
      Assert.Contains( flags, f => f.StartsWith( "WARNING: exact timed only", StringComparison.Ordinal ) );
      Assert.Contains( flags, f => f.StartsWith( "WARNING: exact answered", StringComparison.Ordinal ) && f.Contains( "of 20 queries", StringComparison.Ordinal ) );
      Assert.True( double.Parse( Record( r, "exact" )[4], CultureInfo.InvariantCulture ) < 3.0, "the exact window ran past twice its length" );
   }

   /// <summary>
   /// A default@1 window with two speeds (2 of 5 searches instant, the rest 20 ms) puts p50 well
   /// above the mean of the same window, and is flagged.
   /// </summary>
   [Fact]
   public async Task TwoSpeedWindow_Flagged()
   {
      dynamic r = await ScenarioAsync( "PassesAsync", Args( "--seconds", "2", "--concurrency", "1", "--queries", "random:5", "--exact-seconds", "0" ), "bimodal:20", null, false, 0.0 );
      Assert.Contains( (string[])r.Flags, f => f.StartsWith( "WARNING: default@1 p50", StringComparison.Ordinal ) && f.Contains( "x its mean", StringComparison.Ordinal ) );
   }

   /// <summary>
   /// The shape rules on known samples: a clean window has no flag; fewer than 200 samples, a p50
   /// above 1.25 x the mean, and a mean above the p99 each raise their own flag.
   /// </summary>
   [Fact]
   public void ShapeFlags_Rules()
   {
      Assert.Empty( Flags( Enumerable.Repeat( 1.0, 300 ) ) );
      Assert.Single( Flags( Enumerable.Repeat( 1.0, 150 ) ), f => f.Contains( "fewer than 200", StringComparison.Ordinal ) );
      Assert.Single( Flags( Enumerable.Repeat( 1.0, 0 ) ) );
      string[] skew = Flags( Enumerable.Repeat( 10.0, 300 ).Concat( Enumerable.Repeat( 0.1, 200 ) ) );
      Assert.Single( skew );
      Assert.Contains( "x its mean", skew[0] );
      string[] stalls = Flags( Enumerable.Repeat( 1.0, 1000 ).Concat( Enumerable.Repeat( 10000.0, 5 ) ) );
      Assert.Single( stalls );
      Assert.Contains( "above its p99", stalls[0] );
      Assert.Empty( Flags( Enumerable.Repeat( 1.25, 200 ).Concat( Enumerable.Repeat( 1.0, 199 ) ) ) );
   }

   /// <summary>
   /// Settled means the figures of the last three windows lie within 5% of the lowest; two
   /// windows are not enough, and a steady 4% drift per window is not settled.
   /// </summary>
   [Fact]
   public void IsSettled_Rules()
   {
      Assert.True( IsSettled( 5, 5.1, 5.2 ) );
      Assert.True( IsSettled( 10, 7, 5, 5.1, 5.2 ) );
      Assert.True( IsSettled( 5.2, 5.0, 5.24 ) );
      Assert.False( IsSettled( 5, 5.1 ) );
      Assert.False( IsSettled( 5, 5.2, 5.3 ) );
      Assert.False( IsSettled( 5, 5.2, 5.408 ) );
      Assert.False( IsSettled( 0, 0, 0 ) );
   }

   /// <summary>
   /// The rehearsal covers every pass type in the target's order, each for at least its length and
   /// at its own concurrency (4 searchers in flight for default@4); a target whose default search
   /// fails every time is not timed at all.
   /// </summary>
   [Fact]
   public async Task Rehearsal_EveryPassTypeAtItsConcurrencyAndFailsLoud()
   {
      dynamic ok = await ScenarioAsync( "PrepareAsync", Args( "--concurrency", "1,4", "--queries", "random:5", "--exact-seconds", "1" ), "fixed:1", 0.4 );
      string[] rehearsals = ok.Rehearsals;
      Assert.Equal( new[] { "default@1", "default@4", "exact" }, rehearsals.Select( x => x.Split( '|' )[0] ).OrderBy( p => p ) );
      Assert.All( rehearsals, x => Assert.True( double.Parse( x.Split( '|' )[1], CultureInfo.InvariantCulture ) >= 0.4 && int.Parse( x.Split( '|' )[2], CultureInfo.InvariantCulture ) > 0, x ) );
      string[] events = ok.Events;
      int[] inFlight = ok.InFlight;
      foreach( string pass in new[] { "default@1", "default@4", "exact" } )
      {
         int line = Array.FindIndex( events, e => e.Contains( $": rehearsal of {pass}, ", StringComparison.Ordinal ) );
         int end = Array.FindIndex( events, line + 1, e => e.StartsWith( "LOG ", StringComparison.Ordinal ) );
         end = end < 0 ? events.Length : end;
         Assert.Equal( pass == "default@4" ? 4 : 1, Enumerable.Range( line + 1, end - line - 1 ).Max( i => inFlight[i] ) );
      }

      dynamic failed = await ScenarioAsync( "PrepareAsync", Args( "--concurrency", "1,4", "--queries", "random:5", "--exact-seconds", "1" ), "fail", 0.4 );
      Assert.Contains( "every rehearsal search failed", (string)failed.Error );
   }

   /// <summary>
   /// Pass records chain: the first timed pass ran after the rehearsal, every later one after the
   /// pass before it in the run order, and each window starts after the previous one ended.
   /// </summary>
   [Fact]
   public async Task PassRecords_ChainInRunOrder()
   {
      dynamic r = await ScenarioAsync( "PassesAsync", Args( "--seconds", "1", "--concurrency", "1,2", "--queries", "random:5", "--exact-seconds", "1" ), "fixed:1", "fixed:1", true, 0.3 );
      string[] order = r.PassOrder;
      string[][] records = ( (string[])r.Passes ).Select( p => p.Split( '|' ) ).ToArray();
      Assert.Equal( order, records.Select( p => p[0] ) );
      Assert.Equal( new[] { "rehearsal" }.Concat( order.Take( order.Length - 1 ) ), records.Select( p => p[1] ) );
      for( int i = 0; i < records.Length; i++ )
      {
         long start = long.Parse( records[i][2], CultureInfo.InvariantCulture );
         long end = long.Parse( records[i][3], CultureInfo.InvariantCulture );
         Assert.True( end > start, $"{records[i][0]} ended before it started" );
         string before = i == 0 ? "rehearsal" : records[i - 1][0];
         Assert.True( i == 0 || start >= long.Parse( records[i - 1][3], CultureInfo.InvariantCulture ), $"{records[i][0]} started before {before} ended" );
         Assert.True( int.Parse( records[i][5], CultureInfo.InvariantCulture ) > 0, $"{records[i][0]} timed no searches" );
      }
   }

   /// <summary>
   /// The whole flow through TargetRunner: every pass type is rehearsed for at least its length,
   /// every timed pass follows its own warm-up and its own settle check, and results.json carries
   /// the rehearsal, one settle note naming every pass, and the per-pass notes. Why the verdict
   /// is not asserted here: 1 ms fake searches on a loaded test box can fairly fail the check;
   /// the verdicts have their own tests with latencies far apart.
   /// </summary>
   [Fact]
   public async Task Target_RehearsesThenWarmsAndChecksEveryPass()
   {
      string folder = Path.Combine( AppContext.BaseDirectory, "measurement-tests", Guid.NewGuid().ToString( "N" ) );
      try
      {
         dynamic r = await ScenarioAsync( "MeasureAsync", "fixed:1", folder );
         Assert.Null( (string?)r.Error );
         string[] events = r.Events;
         string[] order = r.PassOrder;
         int firstTiming = Array.FindIndex( events, e => e.Contains( ": timing ", StringComparison.Ordinal ) );
         Assert.True( Array.FindLastIndex( events, e => e.Contains( ": rehearsal of ", StringComparison.Ordinal ) ) < firstTiming );
         Assert.Equal( order.Length, events.Count( e => WARMUP_START.IsMatch( e ) && !e.Contains( "(again", StringComparison.Ordinal ) ) );
         Assert.All( order, pass => Assert.True(
            Array.FindIndex( events, e => e.Contains( $": settle check before {pass}: trial, ", StringComparison.Ordinal ) ) < Array.FindIndex( events, e => e.EndsWith( $"timing {pass}", StringComparison.Ordinal ) ), pass ) );
         string[] notes = r.Notes;
         string rehearsal = Assert.Single( notes, n => n.StartsWith( "Rehearsal before any timed pass", StringComparison.Ordinal ) );
         Dictionary<string, double> rehearsed = REHEARSED.Matches( rehearsal ).ToDictionary( m => m.Groups[1].Value, m => double.Parse( m.Groups[2].Value, CultureInfo.InvariantCulture ) );
         Assert.Equal( order.OrderBy( p => p ), rehearsed.Keys.OrderBy( p => p ) );
         Assert.All( rehearsed, p => Assert.True( p.Value >= 0.4, $"{p.Key} rehearsed {p.Value} s" ) );
         string settle = Assert.Single( notes, n => n.StartsWith( "Settle: ", StringComparison.Ordinal ) || n.Contains( "NOT settled", StringComparison.Ordinal ) );
         Assert.True( settle.StartsWith( "Settle: settled before every timed pass", StringComparison.Ordinal )
            || settle.StartsWith( "WARNING: latency had NOT settled when timing began for ", StringComparison.Ordinal ), settle );
         Assert.All( order, pass => Assert.Contains( $"{pass}: warm-up ", settle ) );
         Assert.Equal( order, notes.Where( n => n.StartsWith( "Pass ", StringComparison.Ordinal ) ).Select( n => n.Split( ' ' )[1] ) );
         Assert.StartsWith( $"Pass {order[0]} after rehearsal:", notes.First( n => n.StartsWith( "Pass ", StringComparison.Ordinal ) ) );
         Assert.Equal( 0, (int?)r.WarmupErrors );
         using JsonDocument json = JsonDocument.Parse( (string)r.Json );
         string[] written = json.RootElement.GetProperty( "targets" )[0].GetProperty( "notes" ).EnumerateArray().Select( n => n.GetString()! ).ToArray();
         Assert.Equal( notes, written );
      }
      finally
      {
         if( Directory.Exists( folder ) )
         {
            Directory.Delete( folder, true );
         }
      }
   }

   #endregion Public Methods

   #region Private Methods

   /// <summary>
   /// Arguments as an array, for the scenario calls.
   /// </summary>
   /// <param name="args">Arguments.</param>
   /// <returns>The array.</returns>
   private static string[] Args( params string[] args )
   {
      return args;
   }

   /// <summary>
   /// The searches sent between "timing PASS" and the next warm-up (or the end): the timed
   /// window's searches.
   /// </summary>
   /// <param name="events">Trace.</param>
   /// <param name="pass">Pass name.</param>
   /// <returns>Searches in the window.</returns>
   private static int TimedSearches( string[] events, string pass )
   {
      int start = Array.FindIndex( events, e => e.EndsWith( $"timing {pass}", StringComparison.Ordinal ) );
      Assert.True( start >= 0, $"no timing line for {pass}" );
      int end = Array.FindIndex( events, start + 1, e => WARMUP_START.IsMatch( e ) );
      return events.Skip( start + 1 ).Take( ( end < 0 ? events.Length : end ) - start - 1 ).Count( e => !e.StartsWith( "LOG ", StringComparison.Ordinal ) );
   }

   /// <summary>
   /// The pass record of one pass, split into its fields.
   /// </summary>
   /// <param name="result">Scenario result.</param>
   /// <param name="pass">Pass name.</param>
   /// <returns>name|previous|startTicks|endTicks|seconds|searches|errors|p50|mean|p99|qps, split.</returns>
   private static string[] Record( dynamic result, string pass )
   {
      string[] passes = result.Passes;
      return Assert.Single( passes, p => p.StartsWith( pass + "|", StringComparison.Ordinal ) ).Split( '|' );
   }

   /// <summary>MeasurementScenarios.ShapeFlags.</summary>
   /// <param name="latencies">Latencies, ms.</param>
   /// <returns>Flags.</returns>
   private static string[] Flags( IEnumerable<double> latencies )
   {
      return (string[])Invoke( "ShapeFlags", (object)latencies.ToArray() );
   }

   /// <summary>MeasurementScenarios.TrialRules.</summary>
   /// <param name="figures">Window figures.</param>
   /// <param name="settled">Settled figure.</param>
   /// <param name="trial">Trial figure.</param>
   /// <returns>Settled figure and 1 when they agree.</returns>
   private static double[] Rules( double[] figures, double settled, double trial )
   {
      return (double[])Invoke( "TrialRules", figures, settled, trial );
   }

   /// <summary>MeasurementScenarios.IsSettled.</summary>
   /// <param name="figures">Window figures.</param>
   /// <returns>True when settled.</returns>
   private static bool IsSettled( params double[] figures )
   {
      return (bool)Invoke( "IsSettled", (object)figures );
   }

   /// <summary>
   /// Calls a synchronous scenario method.
   /// </summary>
   /// <param name="method">Method name.</param>
   /// <param name="arguments">Arguments.</param>
   /// <returns>Its result.</returns>
   private static object Invoke( string method, params object[] arguments )
   {
      return COMPILED.Value.GetType( SCENARIOS_TYPE )!.GetMethod( method )!.Invoke( null, arguments )!;
   }

   /// <summary>
   /// Calls an async scenario and returns its result, failing after 3 minutes.
   /// </summary>
   /// <param name="method">Scenario method name.</param>
   /// <param name="arguments">Arguments.</param>
   /// <returns>The result record.</returns>
   private static async Task<dynamic> ScenarioAsync( string method, params object?[] arguments )
   {
      var task = (Task)COMPILED.Value.GetType( SCENARIOS_TYPE )!.GetMethod( method )!.Invoke( null, arguments )!;
      Task finished = await Task.WhenAny( task, Task.Delay( TimeSpan.FromMinutes( 3 ) ) );
      Assert.True( finished == task, $"{method} did not finish within 3 minutes" );
      await task;
      return task.GetType().GetProperty( "Result" )!.GetValue( task )!;
   }

   /// <summary>
   /// Compiles every benchmark source file plus MeasurementScenarios.cs into one in-memory
   /// assembly. Packages only the benchmark uses are found through the benchmark's own restore
   /// output and loaded from the NuGet folder when first needed.
   /// </summary>
   /// <returns>The assembly.</returns>
   private static Assembly Compile()
   {
      string root = RepoRoot();
      string bench = Path.Combine( root, "src", "GenericVectorBuilder.Bench" );
      var parse = new CSharpParseOptions( LanguageVersion.Latest );
      List<SyntaxTree> trees = Directory.EnumerateFiles( bench, "*.cs", SearchOption.AllDirectories )
         .Where( f => Path.GetRelativePath( bench, f ).Split( Path.DirectorySeparatorChar )[0] is not ( "bin" or "obj" ) )
         .Select( f => CSharpSyntaxTree.ParseText( File.ReadAllText( f ), parse, f ) ).ToList();
      string scenarios = Path.Combine( root, "tests", "GenericVectorBuilder.Engines.Tests", "Bench", "MeasurementScenarios.cs" );
      trees.Add( CSharpSyntaxTree.ParseText( File.ReadAllText( scenarios ), parse.WithPreprocessorSymbols( "BENCH_UNDER_TEST" ), scenarios ) );
      trees.Add( CSharpSyntaxTree.ParseText( IMPLICIT_USINGS, parse ) );
      List<string> platform = ( (string)AppContext.GetData( "TRUSTED_PLATFORM_ASSEMBLIES" )! ).Split( Path.PathSeparator ).ToList();
      List<string> extra = BenchOnlyPackages( bench, platform.Select( Path.GetFileName ).ToHashSet( StringComparer.OrdinalIgnoreCase ) );
      AssemblyLoadContext.Default.Resolving += ( context, name ) =>
         extra.FirstOrDefault( p => Path.GetFileNameWithoutExtension( p ).Equals( name.Name, StringComparison.OrdinalIgnoreCase ) ) is string path ? context.LoadFromAssemblyPath( path ) : null;
      var options = new CSharpCompilationOptions( OutputKind.DynamicallyLinkedLibrary, nullableContextOptions: NullableContextOptions.Enable );
      CSharpCompilation compilation = CSharpCompilation.Create( "BenchMeasurementUnderTest", trees, platform.Concat( extra ).Select( p => MetadataReference.CreateFromFile( p ) ), options );
      using var image = new MemoryStream();
      Microsoft.CodeAnalysis.Emit.EmitResult result = compilation.Emit( image );
      Assert.True( result.Success, string.Join( Environment.NewLine, result.Diagnostics.Where( d => d.Severity == DiagnosticSeverity.Error ) ) );
      return Assembly.Load( image.ToArray() );
   }

   /// <summary>
   /// Runtime files of the packages the benchmark restored that this test host does not
   /// already have, read from the benchmark's project.assets.json.
   /// </summary>
   /// <param name="bench">Benchmark project folder.</param>
   /// <param name="loaded">File names already on the test host's platform list.</param>
   /// <returns>Full paths of the extra assemblies.</returns>
   private static List<string> BenchOnlyPackages( string bench, HashSet<string?> loaded )
   {
      string assets = Path.Combine( bench, "obj", "project.assets.json" );
      Assert.True( File.Exists( assets ), $"Restore the benchmark project first (no {assets})." );
      using JsonDocument doc = JsonDocument.Parse( File.ReadAllText( assets ) );
      string packages = doc.RootElement.GetProperty( "packageFolders" ).EnumerateObject().First().Name;
      JsonElement libraries = doc.RootElement.GetProperty( "libraries" );
      var extra = new List<string>();
      foreach( JsonProperty library in doc.RootElement.GetProperty( "targets" ).EnumerateObject().First().Value.EnumerateObject() )
      {
         if( !library.Value.TryGetProperty( "runtime", out JsonElement runtime ) || library.Value.GetProperty( "type" ).GetString() != "package" )
         {
            continue;
         }

         string folder = libraries.GetProperty( library.Name ).GetProperty( "path" ).GetString()!;
         extra.AddRange( runtime.EnumerateObject().Select( r => r.Name ).Where( r => r.EndsWith( ".dll", StringComparison.OrdinalIgnoreCase ) && !loaded.Contains( Path.GetFileName( r ) ) )
            .Select( r => Path.Combine( packages, folder, r ) ) );
      }

      return extra;
   }

   /// <summary>
   /// Walks up from the test binary to the folder holding the solution file.
   /// </summary>
   /// <returns>The repository root.</returns>
   private static string RepoRoot()
   {
      var dir = new DirectoryInfo( AppContext.BaseDirectory );
      while( dir != null && !File.Exists( Path.Combine( dir.FullName, "GenericVectorBuilder.slnx" ) ) )
      {
         dir = dir.Parent;
      }

      return dir?.FullName ?? throw new InvalidOperationException( "Repository root not found." );
   }

   #endregion Private Methods
}
