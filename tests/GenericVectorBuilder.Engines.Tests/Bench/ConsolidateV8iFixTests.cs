using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace GenericVectorBuilder.Engines.Tests.Bench;

/// <summary>
/// The report-layer fixes the v8i verdict (BLOCK) asked for, proven on the real v7 and v8 runs: B1 (a recorded statement the run-page notes list marks misleading is never printed bare in
/// the summary), B2 (the framing line does not call every engine's client a .NET client), B3 (the MariaDB clause the summary drops is marked on the run pages), and the fix-with items 4 to 6
/// (drift wording and the excluded QPS@8 cell, the observer's two CPU figures, "no v7 run", the label of a clause the settings table also read back, the clock claim the observer's
/// readings contradict, the unsettled flag on the pass that was not settled, and the blank cells). Each test fails on the tree the verdict judged.
/// </summary>
public sealed class ConsolidateV8iFixTests : IClassFixture<RealV8Fixture>
{
   #region Data Members

   private const string CLIENT_STATEMENT = "Measured end to end through each engine's .NET client";
   private const string MARIADB_STATEMENT = "an earlier run gave about 0.09 at 100,000";
   private const string NOTES_FILE = "deploy/bench/run-page-notes.json";

   private static readonly string[] HTTP_ENGINES = { "elasticsearch", "vespa", "opensearch", "chroma", "milvus", "typesense", "clickhouse", "weaviate" };

   private static readonly string[] ALL_RUNS = ConsolidateRealRunsTests.V5.Concat( ConsolidateRealRunsTests.V6 ).Concat( ConsolidateRealRunsTests.V7 ).Concat( RealV8Fixture.V8 ).ToArray();

   private readonly RealV8Fixture _fixture;

   #endregion Data Members

   #region Constructor

   /// <summary>
   /// Takes the shared consolidation.
   /// </summary>
   /// <param name="fixture">The fixture.</param>
   public ConsolidateV8iFixTests( RealV8Fixture fixture )
   {
      _fixture = fixture;
   }

   #endregion Constructor

   #region Public Methods

   /// <summary>
   /// B1: the three ClickHouse disk notes of the v7 runs ("4.21 GiB added by this load" and the two after it) are marked misleading in the run-page notes list, and the summary printed them bare.
   /// It now prints the folder sizes the line records and says the run's page prints a correction; none of the three strings is in the page or in any sentence.
   /// </summary>
   [Fact]
   public void TheMarkedClickHouseDiskNotes_AreNotPrintedBare_AndThePageSaysTheRunPageCorrectsThem()
   {
      string[] bare = { "4.21 GiB added by this load", "2.57 GiB added by this load", "0 B added by this load" };
      List<string> sentences = _fixture.Both.Root.GetProperty( "sentences" ).EnumerateArray().Select( s => s.GetProperty( "text" ).GetString()! ).ToList();
      foreach( string text in bare )
      {
         Assert.DoesNotContain( text, _fixture.Both.Md );
         Assert.DoesNotContain( sentences, s => s.Contains( text, StringComparison.Ordinal ) );
      }

      Assert.Equal( "Run 20261006-130619-eshoponweb: clickhouse's whole engine data folder was recorded as 4.21 GiB, against 99.17 KiB before; the run's page prints a correction to its recorded \"added by this load\" figure.",
         _fixture.Both.Text( "disclosure.disk.clickhouse.20261006-130619-eshoponweb" ) );
      Assert.Equal( "Run 20261006-142724-eshoponweb: clickhouse's whole engine data folder was recorded as 6.79 GiB, against 4.22 GiB before; the run's page prints a correction to its recorded \"added by this load\" figure.",
         _fixture.Both.Text( "disclosure.disk.clickhouse.20261006-142724-eshoponweb" ) );
      Assert.Equal( "Run 20261006-154837-eshoponweb: clickhouse's whole engine data folder was recorded as 6.79 GiB, against 6.79 GiB before; the run's page prints a correction to its recorded \"added by this load\" figure.",
         _fixture.Both.Text( "disclosure.disk.clickhouse.20261006-154837-eshoponweb" ) );
      foreach( string run in new[] { "20261006-130619-eshoponweb", "20261006-142724-eshoponweb", "20261006-154837-eshoponweb" } )
      {
         string sources = SourceValues( "disclosure.disk.clickhouse." + run );
         Assert.Contains( $"{NOTES_FILE}#results@{run}:targets[clickhouse].disk.text", sources );
         Assert.DoesNotContain( "added by this load", sources );
      }
   }

   /// <summary>
   /// B1, the rule behind it: a sentence that prints a statement the run-page notes list marks is refused by the consolidation, naming the sentence and the statement. The list is the repository's own file
   /// with one entry added, whose statement is words the page's rule sentence prints.
   /// </summary>
   [Fact]
   public void ASentenceThatPrintsAMarkedStatement_StopsTheConsolidation()
   {
      string repo = _fixture.CopyRepo( "marked" );
      string file = Path.Combine( repo, "deploy", "bench", "run-page-notes.json" );
      JsonNode root = JsonNode.Parse( File.ReadAllText( file ) )!;
      root["notes"]!.AsArray().Add( JsonNode.Parse( "{\"runs\":[\"" + RealV8Fixture.V8[0] + "\"],\"target\":\"\",\"field\":\"\",\"statement\":\"every other pair is not separated by this test\",\"kind\":\"misleading\",\"note\":\"A test note.\",\"sources\":[{\"kind\":\"file\",\"ref\":\"GenericVectorBuilder.slnx#Solution\",\"value\":\"Solution\"}]}" ) );
      File.WriteAllText( file, root.ToJsonString() );

      ( int exit, string log, ConsolidateOutcome? written ) = _fixture.AttemptInRepo( "marked", _ => { }, repo, "--observer", _fixture.ObserverV8, "--observer", _fixture.ObserverV7 );

      Assert.Equal( 1, exit );
      Assert.Null( written );
      Assert.Contains( "every other pair is not separated by this test", log );
      Assert.Contains( "rule.claim", log );
      Assert.Contains( "run-page notes", log );
   }

   /// <summary>
   /// B1, a stale list: the list marks Weaviate's clause false because the compose file sets a persistence variable. With a compose file that no longer sets one the clause would be printed, and the
   /// list still marks it, so the consolidation is refused and names the clause: the list has to be regenerated first.
   /// </summary>
   [Fact]
   public void AListThatStillMarksAClauseTheRecordedTextNowPrints_StopsTheConsolidation()
   {
      string repo = _fixture.CopyRepo( "stale" );
      string compose = Path.Combine( repo, "deploy", "engines", "weaviate.compose.yaml" );
      File.WriteAllLines( compose, File.ReadAllLines( compose ).Where( l => !l.Contains( "PERSISTENCE_DATA_PATH", StringComparison.Ordinal ) ) );

      ( int exit, string log, ConsolidateOutcome? written ) = _fixture.AttemptInRepo( "stale", _ => { }, repo, "--observer", _fixture.ObserverV8, "--observer", _fixture.ObserverV7 );

      Assert.Equal( 1, exit );
      Assert.Null( written );
      Assert.Contains( "weaviate.compose.yaml sets no persistence variable", log );
      Assert.Contains( "the run-page notes list marks as false", log );
   }

   /// <summary>
   /// B1, the list is a source the report stands on: a repository with no run-page notes list is refused, naming the file. A list that cannot be read is no list of nothing.
   /// </summary>
   [Fact]
   public void ARepositoryWithoutTheRunPageNotesList_IsRefused_AndTheListIsRecordedInTheAudit()
   {
      string repo = _fixture.CopyRepo( "no-notes" );
      File.Delete( Path.Combine( repo, "deploy", "bench", "run-page-notes.json" ) );

      ( int exit, string log, ConsolidateOutcome? written ) = _fixture.AttemptInRepo( "no-notes", _ => { }, repo, "--observer", _fixture.ObserverV8, "--observer", _fixture.ObserverV7 );

      Assert.Equal( 1, exit );
      Assert.Null( written );
      Assert.Contains( "run-page notes", log );
      Assert.Contains( NOTES_FILE, log );
      Assert.Equal( 64, _fixture.Both.Root.GetProperty( "audit" ).GetProperty( "runPageNotesSha256" ).GetString()!.Length );
   }

   /// <summary>
   /// B2: the framing line that heads every run's results.md calls each engine's client a .NET client. Eight of the 19 engines are reached through the benchmark's own HttpClient REST code, so the line
   /// now says the client is the one the benchmark uses for each engine, a vendor library for some and its own REST code for others, in all three of its forms.
   /// </summary>
   /// <param name="name">The constant.</param>
   [Theory]
   [InlineData( "LINE_SMALL" )]
   [InlineData( "LINE_LARGE" )]
   [InlineData( "LINE_UNKNOWN" )]
   public void TheFramingLine_DoesNotCallEveryClientANetClient( string name )
   {
      string line = (string)ConsolidateHarness.Call( "Framing", name )!;

      Assert.DoesNotContain( ".NET client", line );
      Assert.Contains( "the client code the benchmark uses for each engine", line );
      Assert.Contains( "a vendor library for some engines, the benchmark's own HttpClient REST code for others", line );
      Assert.DoesNotContain( "\u2014", line );
   }

   /// <summary>
   /// B2: line 12 of the results.md of every run that holds the old framing line is marked false by the run-page notes list, once per run, with the eight engines the benchmark reaches with its own
   /// HttpClient REST code named and the code line of each as a source. The statement is in the run's results.md, and the engines named are the ones the fact sheet's protocol rows give.
   /// </summary>
   [Fact]
   public void TheOldFramingLine_IsMarkedFalseOnEveryRunPageThatHoldsIt_NamingTheEightEngines()
   {
      List<JsonElement> notes = NotesOf( CLIENT_STATEMENT ).ToList();
      List<string> factEngines = HttpEnginesFromFactSheet();
      Assert.Equal( HTTP_ENGINES.OrderBy( e => e, StringComparer.Ordinal ), factEngines.OrderBy( e => e, StringComparer.Ordinal ) );

      foreach( string run in ALL_RUNS )
      {
         string md = File.ReadAllText( Path.Combine( ConsolidateHarness.BenchResults(), run, "results.md" ) );
         JsonElement note = Assert.Single( notes, n => RunsOf( n ).Contains( run ) );
         Assert.Contains( CLIENT_STATEMENT, md );
         Assert.Equal( "false", note.GetProperty( "kind" ).GetString() );
         string text = note.GetProperty( "note" ).GetString()!;
         Assert.All( HTTP_ENGINES, engine => Assert.Contains( engine, text ) );
         Assert.Contains( "HttpClient REST code", text );
         List<string> refs = note.GetProperty( "sources" ).EnumerateArray().Select( s => s.GetProperty( "ref" ).GetString()! ).ToList();
         Assert.Equal( HTTP_ENGINES.Length, refs.Count( r => r.StartsWith( "src/GenericVectorBuilder.Engines/Sinks/", StringComparison.Ordinal ) ) );
      }

      Assert.Equal( ALL_RUNS.Length, notes.Sum( n => RunsOf( n ).Count ) );
   }

   /// <summary>
   /// B3: the MariaDB clause "an earlier run gave about 0.09 at 100,000" is dropped by the summary (no log was saved) and is in the results.md of every run. Each run page now carries a note of kind
   /// "unbacked" that cites the saved re-run, gives its range at 524 and at 2,000 vectors as the saved log shows them, and says the 100,000 figure is dropped.
   /// </summary>
   [Fact]
   public void TheUnbackedMariaDbClause_IsMarkedOnEveryRunPage_WithTheSavedRerun()
   {
      string log = File.ReadAllText( Path.Combine( ConsolidateHarness.RepoRoot(), "design", "bench-inputs", "mariadb-effort-2026-10-07", "run.log" ) );
      ( string low524, string high524 ) = Range( log, 524 );
      ( string low2000, string high2000 ) = Range( log, 2000 );
      Assert.Equal( ( "0.988", "0.996" ), ( low524, high524 ) );
      Assert.Equal( ( "0.894", "0.932" ), ( low2000, high2000 ) );
      List<JsonElement> notes = NotesOf( MARIADB_STATEMENT ).ToList();

      foreach( string run in ALL_RUNS )
      {
         string md = File.ReadAllText( Path.Combine( ConsolidateHarness.BenchResults(), run, "results.md" ) );
         Assert.Contains( MARIADB_STATEMENT, md );
         JsonElement note = Assert.Single( notes, n => RunsOf( n ).Contains( run ) );
         Assert.Equal( "unbacked", note.GetProperty( "kind" ).GetString() );
         string text = note.GetProperty( "note" ).GetString()!;
         Assert.Contains( $"{low524} to {high524} at 524", text );
         Assert.Contains( $"{low2000} to {high2000} at 2,000", text );
         Assert.Contains( "100,000", text );
         Assert.Contains( "dropped", text );
         Assert.Contains( note.GetProperty( "sources" ).EnumerateArray(), s => s.GetProperty( "ref" ).GetString()!.StartsWith( "design/bench-inputs/mariadb-effort-2026-10-07/SOURCES.txt#", StringComparison.Ordinal ) );
         Assert.Contains( note.GetProperty( "sources" ).EnumerateArray(), s => s.GetProperty( "ref" ).GetString()!.StartsWith( "design/bench-inputs/mariadb-effort-2026-10-07/run.log#", StringComparison.Ordinal ) );
      }
   }

   /// <summary>
   /// The general rule behind B1 to B3: a string the run-page notes list marks false, misleading or unbacked is in no sentence, no source of a sentence, no string of the consolidated file the page is
   /// drawn from and no line of the summary unless the same paragraph carries the correction, and every run whose results.md holds such a string (under the engine's heading when the note names one) is named by a note with that statement. A new run, or a new string in the
   /// summary, with no note fails here.
   /// </summary>
   [Fact]
   public void NoMarkedStatement_IsPrintedInTheSummaryOrHeldByARunWithoutItsNote()
   {
      JsonElement[] notes = Notes().ToArray();
      Assert.NotEmpty( notes );
      Assert.All( notes, n => Assert.Contains( n.GetProperty( "kind" ).GetString(), new[] { "false", "misleading", "unbacked" } ) );
      List<string> everyString = new();
      foreach( JsonProperty property in _fixture.Both.Root.EnumerateObject().Where( p => p.Name != "runPageNotes" ) )
      {
         // the corrections list is where each marked statement is quoted together with its correction, so it is the one place the rule does not read
         Strings( property.Value, everyString );
      }
      string[] paragraphs = _fixture.Both.Md.Split( "\n\n", StringSplitOptions.RemoveEmptyEntries );
      List<string> sentences = _fixture.Both.Root.GetProperty( "sentences" ).EnumerateArray().Select( s => s.GetProperty( "text" ).GetString()! ).ToList();

      foreach( IGrouping<(string Statement, string Target), JsonElement> byStatement in notes.GroupBy( n => ( n.GetProperty( "statement" ).GetString()!, n.GetProperty( "target" ).GetString() ?? string.Empty ) ) )
      {
         string statement = byStatement.Key.Statement;
         string[] corrections = byStatement.Select( n => n.GetProperty( "note" ).GetString()! ).ToArray();
         Assert.All( paragraphs.Where( p => Whole( p, statement ) ), p => Assert.Contains( corrections, c => p.Contains( c, StringComparison.Ordinal ) ) );
         Assert.All( sentences.Where( s => Whole( s, statement ) ), s => Assert.Contains( corrections, c => s.Contains( c, StringComparison.Ordinal ) ) );
         Assert.All( everyString.Where( s => Whole( s, statement ) ), s => Assert.Contains( corrections, c => s.Contains( c, StringComparison.Ordinal ) ) );
         foreach( string run in Directory.GetDirectories( ConsolidateHarness.BenchResults(), "2026*-eshoponweb" ).Select( Path.GetFileName ).Cast<string>() )
         {
            string file = Path.Combine( ConsolidateHarness.BenchResults(), run, "results.md" );
            string[] lines = File.Exists( file ) ? File.ReadAllLines( file ) : Array.Empty<string>();
            bool holds = HoldsUnder( lines, statement, byStatement.Key.Target );
            bool named = byStatement.Any( n => RunsOf( n ).Contains( run ) );
            Assert.True( !holds || named, $"{run}: the report holds \"{statement}\" and no note of the run-page notes list names this run" );
         }
      }
   }

   /// <summary>
   /// Fix-with 4: the drift sentence says "figure", as the middle case and the largest move are over ranked cells and not engines, and a sentence names the QPS@8 cell it leaves out (ClickHouse, whose
   /// pass was recorded NOT HELD) with the move of its session median (13.36%) and its runs' range in each session.
   /// </summary>
   [Fact]
   public void TheDriftSentence_SaysFigure_AndNamesTheExcludedClickHouseCell()
   {
      Assert.Equal( "From v7 to v8, a ranked figure's session median moved 1.43% in the middle case and 4.66% at most (opensearch, exact p50).", _fixture.Both.Text( "drift.median" ) );
      Assert.Equal( "The NOT HELD clickhouse QPS@8 cell is left out of these figures; its session median moved 13.36%, with v7 runs at 375 to 428 and v8 runs at 336 to 380.", _fixture.Both.Text( "drift.excluded.clickhouse.qps8" ) );
      JsonElement excluded = Assert.Single( _fixture.Both.Root.GetProperty( "drift" ).GetProperty( "excluded" ).EnumerateArray() );
      Assert.Equal( ( "clickhouse", "qps8", -1336 ), ( excluded.GetProperty( "target" ).GetString(), excluded.GetProperty( "metric" ).GetString(), excluded.GetProperty( "moveBp" ).GetInt32() ) );
      Assert.DoesNotContain( "a ranked engine's", _fixture.Both.Md );
   }

   /// <summary>
   /// Fix-with 5, the observer's two CPU figures: 0.138 is the largest CPU the observer used in any one pass, and 0.0755 is the largest of the runs' averages by cgroup; each sentence says which it is.
   /// </summary>
   [Fact]
   public void TheObserverSentences_LabelThePassMaximum_AndTheRunAverage()
   {
      string pass = _fixture.Both.Text( "disclosure.observer" )!;
      Assert.Matches( @"^An observer process ran beside the v8 runs; its own CPU, at most \d\.\d{3} CPUs in any one pass, counts as outside load, and its summary is observer/summary\.json beside this report\.$", pass );
      Assert.Equal( "The observer was not pinned: in the v8 runs 34.8, 33.8 and 33.5 percent of its resident-thread ticks were seen on engine CPUs 2-3,6-7.", _fixture.Both.Text( "disclosure.observer.placement" ) );
      Assert.Equal( "Averaged over a whole run, the observer's CPU by cgroup was at most 0.0755 CPUs in any of the v8 runs.", _fixture.Both.Text( "disclosure.observer.cgroup" ) );
      Assert.DoesNotContain( "CPUs in a pass,", _fixture.Both.Md );
      Assert.Null( _fixture.NoObserver.Text( "disclosure.observer.cgroup" ) );
   }

   /// <summary>
   /// Fix-with 5, "no v7 run": the observer summary of v7 covers systemd timers for none of the three runs, and the sentence says so; with one run covered it says it does not cover every run.
   /// </summary>
   [Fact]
   public void TheTimersSentence_SaysNoRun_WhenNoRunIsCovered_AndNotEveryRun_WhenSomeAre()
   {
      Assert.Equal( "The observer summary covers systemd timers for no v7 run.", _fixture.Both.Text( "disclosure.timers.v7" ) );
      Assert.DoesNotContain( "not cover systemd timers for every v7 run", _fixture.Both.Md );

      string some = Path.Combine( _fixture.Scratch, "summary-v7-some.json" );
      JsonNode summary = JsonNode.Parse( File.ReadAllText( _fixture.ObserverV7 ) )!;
      summary["runs"]![0]!["checks"]!["timers"]!["covered"] = true;
      File.WriteAllText( some, summary.ToJsonString() );
      ( int exit, string log, ConsolidateOutcome? written ) = _fixture.Attempt( "timers-some", _ => { }, "--observer", _fixture.ObserverV8, "--observer", some );
      Assert.True( exit == 0, log );
      Assert.Equal( "The observer summary does not cover systemd timers for every v7 run.", written!.Text( "disclosure.timers.v7" ) );
   }

   /// <summary>
   /// Fix-with 5, the label of a clause: the settings the v8 runs read back from the MariaDB container include innodb_flush_log_at_trx_commit, and the clause of the recorded text that states it no
   /// longer sits under "not read back from the engine in these runs"; its label names the settings table, as does that of a clause that states a setting of SQLite or DuckDB the settings list as
   /// read. A clause no setting covers keeps the old label.
   /// </summary>
   [Fact]
   public void AClauseTheSettingsTableAlsoReadBack_IsNotLabelledNotReadBack()
   {
      string[] lines = _fixture.Both.Md.Split( '\n' );
      Assert.Equal( "Backed by a saved log or measurement; the v8 engine settings also list innodb_flush_log_at_trx_commit, read from the running engine:", LabelOf( lines, "> innodb_flush_log_at_trx_commit=2 (set in mariadb-bench.compose.yaml" ) );
      Assert.Equal( "Set by a line of code or a compose file saved in this repository; the v8 engine settings also list journal_mode, read from the running engine:", LabelOf( lines, "> PRAGMA journal_mode=WAL and PRAGMA synchronous=NORMAL" ) );
      Assert.Equal( "Set by a line of code or a compose file saved in this repository; the v8 engine settings also list checkpoint_threshold, read from the running engine:", LabelOf( lines, "> (checkpoint_threshold=256MB" ) );
      Assert.Equal( "Backed by a saved log or measurement, not read back from the engine in these runs:", LabelOf( lines, "> innodb_doublewrite is on" ) );
   }

   /// <summary>
   /// Fix-with 5, the clock claim: the run note says every CPU's clock is held at its ceiling "whatever the engine runs", and the observer's per-CPU readings put oracle's eight-searcher pass under the pin
   /// in every run. The clause is not printed, in the quotes or in the table of clauses, and a sentence says why with the readings.
   /// </summary>
   [Fact]
   public void TheClockClaimTheObserverContradicts_IsNotPrinted_AndASentenceSaysWhy()
   {
      Assert.DoesNotContain( "whatever the engine runs", _fixture.Both.Md );
      Assert.DoesNotContain( _fixture.Both.Root.GetProperty( "sentences" ).EnumerateArray(), s => s.GetProperty( "text" ).GetString()!.Contains( "whatever the engine runs", StringComparison.Ordinal ) );
      string notice = Assert.Single( _fixture.Both.Under( "method.dropped" ), d => d.Text.Contains( "machine control note", StringComparison.Ordinal ) ).Text;
      Assert.Equal( "A clause of the machine control note, that every CPU's clock is held at its ceiling for any engine, is not printed: the observer found oracle's eight-searcher pass 0.27% to 0.66% under the pin.", notice );
      JsonElement clause = _fixture.Both.Root.GetProperty( "recordedTexts" ).EnumerateArray().Where( r => r.GetProperty( "field" ).GetString() == "notes" ).SelectMany( r => r.GetProperty( "clauses" ).EnumerateArray() )
         .Single( c => c.GetProperty( "text" ).GetString()!.StartsWith( ", so every CPU's clock", StringComparison.Ordinal ) );
      Assert.Equal( ( false, "clock-held" ), ( clause.GetProperty( "printed" ).GetBoolean(), clause.GetProperty( "notPrinted" ).GetString() ) );

      string without = _fixture.NoObserver.Under( "method.dropped" ).Single( d => d.Text.Contains( "machine control note", StringComparison.Ordinal ) ).Text;
      Assert.Equal( "A clause of the machine control note, that every CPU's clock is held at its ceiling for any engine, is not printed: no clock reading by APERF and MPERF was given to check it.", without );
      Assert.DoesNotContain( "whatever the engine runs", _fixture.NoObserver.Md );
   }

   /// <summary>
   /// Fix-with 6, the unsettled flag: ClickHouse's runs recorded the eight-searcher pass as not settled and said so for "default@8" alone. The flag sits on the QPS@8 row, with the pass named, and on no
   /// other ClickHouse row; the NOT HELD flag stays on QPS@8 only.
   /// </summary>
   [Fact]
   public void TheUnsettledFlag_SitsOnlyOnThePassThatWasNotSettled()
   {
      foreach( JsonElement metric in _fixture.Both.Root.GetProperty( "metrics" ).EnumerateArray() )
      {
         JsonElement row = metric.GetProperty( "rows" ).EnumerateArray().First( r => r.GetProperty( "target" ).GetString() == "clickhouse" );
         List<JsonElement> unsettled = row.GetProperty( "flags" ).EnumerateArray().Where( f => f.GetProperty( "code" ).GetString() == "unsettled" ).ToList();
         if( metric.GetProperty( "metric" ).GetString() == "qps8" )
         {
            Assert.Equal( 3, unsettled.Count );
            Assert.All( unsettled, f => Assert.EndsWith( "was recorded as not settled before its timed eight-searcher pass.", f.GetProperty( "text" ).GetString() ) );
         }
         else
         {
            Assert.Empty( unsettled );
         }
      }

      Assert.DoesNotContain( "unsettled", Regex.Matches( _fixture.Both.Md, @"^\| clickhouse \| [^\n]*$", RegexOptions.Multiline ).Select( m => m.Value ).Where( l => l.Contains( "NOT HELD", StringComparison.Ordinal ) || !l.Contains( "336-380", StringComparison.Ordinal ) ) );
   }

   /// <summary>
   /// Fix-with 6, a settle warning that names no pass (a run before the pass list) leaves the flag on every metric of the target, as before; a warning that names two passes puts it on those two.
   /// </summary>
   [Fact]
   public void TheUnsettledPassList_IsReadFromTheWarning_AndAWarningWithNoListFlagsEveryPass()
   {
      Assert.Equal( new[] { "default@8" }, (string[])ConsolidateHarness.Call( "UnsettledPasses", "latency had NOT settled when timing began for default@8 (each by its own warm-up). default@8: warm-up 15.0 s" )! );
      Assert.Equal( new[] { "default@1", "default@8" }, (string[])ConsolidateHarness.Call( "UnsettledPasses", "latency had NOT settled when timing began for default@1, default@8 (each by its own warm-up)." )! );
      Assert.Equal( new[] { "exact" }, (string[])ConsolidateHarness.Call( "UnsettledPasses", "latency had NOT settled when timing began for exact (each by its own warm-up)." )! );
      Assert.Empty( (string[])ConsolidateHarness.Call( "UnsettledPasses", "no settle ran: the engine was not checked" )! );
      Assert.Empty( (string[])ConsolidateHarness.Call( "UnsettledPasses", (string?)null! )! );
   }

   /// <summary>
   /// Fix-with 6, the blank cells: a ranked row that is separated from every other ranked row says "none" in the not-separated column (redis at p50, weaviate at QPS@8), and the row the tool did not rank
   /// says "not held, not ranked" (ClickHouse at QPS@8); no row of any table leaves the cell empty.
   /// </summary>
   [Fact]
   public void TheNotSeparatedCell_IsNeverBlank()
   {
      string[] lines = _fixture.Both.Md.Split( '\n' );
      Assert.Contains( lines, l => l.StartsWith( "| redis | 0.393-0.399 |", StringComparison.Ordinal ) && l.Contains( "| approximate; recorded, documented | none |", StringComparison.Ordinal ) );
      Assert.Contains( lines, l => l.StartsWith( "| weaviate | 305-305 |", StringComparison.Ordinal ) && l.Contains( "| approximate; recorded, unverified | none |", StringComparison.Ordinal ) );
      Assert.Contains( lines, l => l.StartsWith( "| clickhouse | 375-428 |", StringComparison.Ordinal ) && l.Contains( "| approximate; measured | not held, not ranked | unsettled, not-held |", StringComparison.Ordinal ) );
      List<string> blank = TableRows( lines, "| engine |" ).Where( cells => cells.Count > 5 && cells[^2].Length == 0 ).Select( cells => cells[0] ).ToList();
      Assert.Empty( blank );
   }

   /// <summary>
   /// The files of these fixes hold no em or en dash and no name of the former employer: the owner strips both by hand, and the notes list and the sheet are printed on the pages.
   /// </summary>
   [Fact]
   public void TheFilesOfTheseFixes_HoldNoDashesOrForbiddenNames()
   {
      string root = ConsolidateHarness.RepoRoot();
      string src = "src/GenericVectorBuilder.Bench/";
      string web = "src/GenericVectorBuilder.Web/BenchPages/";
      string[] files =
      {
         src + "Stats/ConsolidateNotes.cs", src + "Stats/ConsolidateFraming.cs", src + "Stats/ConsolidateAudit.cs", src + "Stats/ConsolidateInput.cs", src + "Stats/ConsolidateLoader.cs",
         src + "Stats/ConsolidateText.cs", src + "Stats/ConsolidateTextParts.cs", src + "Stats/ConsolidateTextDisclosures.cs", src + "Stats/ConsolidateTextClauses.cs", src + "Stats/ConsolidateTextFlags.cs",
         src + "Stats/ConsolidateFlags.cs", src + "Stats/ConsolidateClasses.cs", src + "Stats/ClaimDrift.cs", src + "Stats/ConsolidatedReport.cs", src + "Stats/Consolidator.cs",
         src + "Report/SettleWarning.cs", src + "Report/RunResult.cs", src + "Report/ConsolidatedMarkdown.cs", src + "Report/BenchReport.cs",
         web + "BenchRunNotes.cs", web + "BenchLegends.cs", web + "BenchConsolidatedFields.cs",
         "deploy/bench/run-page-notes.json", "deploy/bench/recorded-text-classes.json", "design/bench-inputs/recorded-text-classes-source/classes_spec.py",
         "tests/GenericVectorBuilder.Engines.Tests/Bench/ConsolidateV8iFixTests.cs", "tests/GenericVectorBuilder.Tests/Unit/BenchResultsV8iFixTests.cs",
      };
      var names = new Regex( @"(?i)\b" + "bs" + "&?a\\b|" + "fal" + "con|" + "dwe" + "aver" );
      foreach( string file in files )
      {
         string text = File.ReadAllText( Path.Combine( root, file ) );
         Assert.False( text.Contains( '\u2014' ) || text.Contains( '\u2013' ), $"{file} contains an em or en dash" );
         Assert.False( names.IsMatch( text ), $"{file} names the former employer" );
      }
   }

   #endregion Public Methods

   #region Private Methods

   /// <summary>
   /// Every string value of a JSON document, at any depth.
   /// </summary>
   /// <param name="element">The element.</param>
   /// <param name="into">Receives the strings.</param>
   private static void Strings( JsonElement element, List<string> into )
   {
      switch( element.ValueKind )
      {
         case JsonValueKind.String:
            into.Add( element.GetString()! );
            break;
         case JsonValueKind.Array:
            element.EnumerateArray().ToList().ForEach( e => Strings( e, into ) );
            break;
         case JsonValueKind.Object:
            element.EnumerateObject().ToList().ForEach( p => Strings( p.Value, into ) );
            break;
      }
   }

   /// <summary>
   /// The sources of a sentence of the consolidated file, as one text.
   /// </summary>
   /// <param name="slot">The sentence's slot.</param>
   /// <returns>The sources' references and values joined.</returns>
   private string SourceValues( string slot )
   {
      JsonElement sentence = _fixture.Both.Root.GetProperty( "sentences" ).EnumerateArray().First( s => s.GetProperty( "slot" ).GetString() == slot );
      return string.Join( "\n", sentence.GetProperty( "sources" ).EnumerateArray().Select( s => s.GetProperty( "ref" ).GetString() + " " + s.GetProperty( "value" ).GetString() ) );
   }

   /// <summary>
   /// The label line above the quote that starts with some words.
   /// </summary>
   /// <param name="lines">The page's lines.</param>
   /// <param name="quote">The start of the quote line.</param>
   /// <returns>The label line (the last non-empty line before the quote).</returns>
   private static string LabelOf( string[] lines, string quote )
   {
      int at = Array.FindIndex( lines, l => l.StartsWith( quote, StringComparison.Ordinal ) );
      Assert.True( at > 0, $"no line starts with {quote}" );
      for( int i = at - 1; i >= 0; i-- )
      {
         if( lines[i].Length > 0 )
         {
            return lines[i];
         }
      }

      throw new InvalidOperationException( "no label" );
   }

   /// <summary>
   /// The rows of every markdown table whose header starts with the given text, split into trimmed cells.
   /// </summary>
   /// <param name="lines">The page's lines.</param>
   /// <param name="header">The start of the header line.</param>
   /// <returns>One list of cells per table row.</returns>
   private static IEnumerable<List<string>> TableRows( string[] lines, string header )
   {
      bool inside = false;
      foreach( string line in lines )
      {
         if( line.StartsWith( header, StringComparison.Ordinal ) && line.Contains( "not separated from", StringComparison.Ordinal ) )
         {
            inside = true;
            continue;
         }

         if( inside && line.StartsWith( "|---", StringComparison.Ordinal ) )
         {
            continue;
         }

         if( inside && line.StartsWith( '|' ) )
         {
            yield return line.Trim( '|' ).Split( '|' ).Select( c => c.Trim() ).ToList();
            continue;
         }

         inside = false;
      }
   }

   /// <summary>
   /// The notes of the repository's run-page notes list.
   /// </summary>
   /// <returns>The entries.</returns>
   private static IEnumerable<JsonElement> Notes()
   {
      using JsonDocument doc = JsonDocument.Parse( File.ReadAllText( Path.Combine( ConsolidateHarness.RepoRoot(), "deploy", "bench", "run-page-notes.json" ) ) );
      return doc.RootElement.GetProperty( "notes" ).EnumerateArray().Select( n => n.Clone() ).ToList();
   }

   /// <summary>
   /// The notes with a statement.
   /// </summary>
   /// <param name="statement">The statement.</param>
   /// <returns>The entries.</returns>
   private static IEnumerable<JsonElement> NotesOf( string statement )
   {
      return Notes().Where( n => n.GetProperty( "statement" ).GetString() == statement );
   }

   /// <summary>
   /// The run folders a note names.
   /// </summary>
   /// <param name="note">The note.</param>
   /// <returns>The folders.</returns>
   private static List<string> RunsOf( JsonElement note )
   {
      return note.GetProperty( "runs" ).EnumerateArray().Select( r => r.GetString()! ).ToList();
   }

   /// <summary>
   /// The engines the fact sheet gives an HttpClient protocol row.
   /// </summary>
   /// <returns>Target names.</returns>
   private static List<string> HttpEnginesFromFactSheet()
   {
      string path = Path.Combine( ConsolidateHarness.RepoRoot(), "src", "GenericVectorBuilder.Bench", "Report", "engine-facts.json" );
      return File.ReadAllLines( path ).Where( l => l.Contains( "\"kind\": \"protocol\"", StringComparison.Ordinal ) && l.Contains( "\"text\": \"HttpClient", StringComparison.Ordinal ) )
         .Select( l => Regex.Match( l, "\"target\": \"([^\"]+)\"" ).Groups[1].Value ).Distinct( StringComparer.Ordinal ).ToList();
   }

   /// <summary>
   /// The lowest and highest recall@10 at ef 100 the saved MariaDB re-run read on some number of random vectors.
   /// </summary>
   /// <param name="log">The saved run.log.</param>
   /// <param name="vectors">524 or 2000.</param>
   /// <returns>The two figures as written in the log.</returns>
   private static ( string Low, string High ) Range( string log, int vectors )
   {
      List<string> values = Regex.Matches( log, $@"RECALL@10 on {vectors} random 1024-dim vectors, 50 queries: ef 20 [\d.]+, ef 100 ([\d.]+)," ).Select( m => m.Groups[1].Value ).ToList();
      Assert.Equal( 10, values.Count );
      return ( values.OrderBy( v => double.Parse( v, System.Globalization.CultureInfo.InvariantCulture ) ).First(), values.OrderBy( v => double.Parse( v, System.Globalization.CultureInfo.InvariantCulture ) ).Last() );
   }

   /// <summary>
   /// Whether a text holds a statement as a whole figure or phrase: not inside a longer word or number. A dot continues a figure only when a letter or digit sits beyond it, so the full stop that
   /// ends a sentence does not hide a statement.
   /// </summary>
   /// <param name="text">The text.</param>
   /// <param name="statement">The statement.</param>
   /// <returns>True when it does.</returns>
   private static bool Whole( string text, string statement )
   {
      for( int at = text.IndexOf( statement, StringComparison.Ordinal ); at >= 0; at = text.IndexOf( statement, at + 1, StringComparison.Ordinal ) )
      {
         int end = at + statement.Length;
         bool before = at == 0 || !Continues( text, at - 1, -1 ) || !IsPart( statement[0] );
         bool after = end == text.Length || !Continues( text, end, 1 ) || !IsPart( statement[^1] );
         if( before && after )
         {
            return true;
         }
      }

      return false;
   }

   /// <summary>
   /// Whether a report holds a statement, in a line under the engine's heading when the note names one.
   /// </summary>
   /// <param name="lines">The report's lines.</param>
   /// <param name="statement">The statement.</param>
   /// <param name="target">The engine, or empty for any line.</param>
   /// <returns>True when a line holds it.</returns>
   private static bool HoldsUnder( string[] lines, string statement, string target )
   {
      string section = string.Empty;
      foreach( string line in lines )
      {
         Match heading = Regex.Match( line, @"^#{2,4} (.+?)\s*$" );
         section = heading.Success ? heading.Groups[1].Value : section;
         if( ( target.Length == 0 || string.Equals( section, target, StringComparison.OrdinalIgnoreCase ) ) && Whole( line, statement ) )
         {
            return true;
         }
      }

      return false;
   }

   /// <summary>
   /// Whether a character continues a word or a figure by itself.
   /// </summary>
   /// <param name="c">The character.</param>
   /// <returns>True for a letter, a digit or an underscore.</returns>
   private static bool IsPart( char c )
   {
      return char.IsLetterOrDigit( c ) || c == '_';
   }

   /// <summary>
   /// Whether the character at a position continues a word or a figure on the side that faces a statement.
   /// </summary>
   /// <param name="text">The text.</param>
   /// <param name="index">The position.</param>
   /// <param name="step">+1 after the statement, -1 before it.</param>
   /// <returns>True when it does.</returns>
   private static bool Continues( string text, int index, int step )
   {
      int beyond = index + step;
      return IsPart( text[index] ) || ( text[index] == '.' && beyond >= 0 && beyond < text.Length && char.IsLetterOrDigit( text[beyond] ) );
   }

   #endregion Private Methods
}
