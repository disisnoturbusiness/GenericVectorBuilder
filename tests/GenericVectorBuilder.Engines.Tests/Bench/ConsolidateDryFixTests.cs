using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace GenericVectorBuilder.Engines.Tests.Bench;

/// <summary>
/// One consolidation of the real v5, v6 and v7 runs with a stand-in second session (copies of the v7 runs
/// one day later, as the dry check of the v8 pipeline built it), run once for every test of
/// <see cref="ConsolidateDryFixTests"/>, with the scratch results folder holding the blocked folders of the
/// earlier reports by name, and a second consolidation that leaves two targets out.
/// Why a stand-in and not the pseudo-v8 of the real-runs tests: it carries exactly the recorded figures of
/// v7, so the orders that sit on the line are the ones the dry check's lenses recomputed by hand.
/// </summary>
public sealed class DryFixFixture : IDisposable
{
   #region Data Members

   /// <summary>The stand-in session's folder names.</summary>
   public static readonly string[] STAND_IN = { "20261007-130619-eshoponweb", "20261007-142724-eshoponweb", "20261007-154837-eshoponweb" };

   /// <summary>Blocked folders of the earlier reports, created empty beside the runs.</summary>
   public static readonly string[] BLOCKED = { "blocked-2026-10-05-v5", "blocked-2026-10-05-v6", "blocked-2026-10-06-v7" };

   #endregion Data Members

   #region Constructor

   /// <summary>
   /// Builds the scratch results folder and runs the consolidations.
   /// </summary>
   public DryFixFixture()
   {
      Scratch = Path.Combine( AppContext.BaseDirectory, "consolidate-tests", "dryfix-" + Guid.NewGuid().ToString( "N" ) );
      Results = Path.Combine( Scratch, "results" );
      Directory.CreateDirectory( Results );
      string real = ConsolidateHarness.BenchResults();
      foreach( string folder in ConsolidateRealRunsTests.V5.Concat( ConsolidateRealRunsTests.V6 ).Concat( ConsolidateRealRunsTests.V7 ).Concat( ConsolidateBasisTests.OTHER_RUNS ) )
      {
         Directory.CreateDirectory( Path.Combine( Results, folder ) );
         File.Copy( Path.Combine( real, folder, "results.json" ), Path.Combine( Results, folder, "results.json" ) );
      }

      for( int i = 0; i < 3; i++ )
      {
         JsonObject run = JsonNode.Parse( File.ReadAllText( Path.Combine( real, ConsolidateRealRunsTests.V7[i], "results.json" ) ) )!.AsObject();
         run["startedUtc"] = ( (string)run["startedUtc"]! ).Replace( "2026-10-06", "2026-10-07", StringComparison.Ordinal );
         Directory.CreateDirectory( Path.Combine( Results, STAND_IN[i] ) );
         File.WriteAllText( Path.Combine( Results, STAND_IN[i], "results.json" ), run.ToJsonString() );
      }

      foreach( string blocked in BLOCKED )
      {
         Directory.CreateDirectory( Path.Combine( Results, blocked ) );
      }

      Two = Run( Results, Path.Combine( Scratch, "out-two" ), "--session", "v7=" + string.Join( ",", ConsolidateRealRunsTests.V7 ), "--session", "v8=" + string.Join( ",", STAND_IN ) );
      Narrowed = Run( Results, Path.Combine( Scratch, "out-narrowed" ), "--session", "v7=" + string.Join( ",", ConsolidateRealRunsTests.V7 ), "--session", "v8=" + string.Join( ",", STAND_IN ),
         "--targets", string.Join( ",", Two.Root.GetProperty( "targets" ).EnumerateArray().Select( t => t.GetString()! ).Where( t => t != "weaviate" ) ) );
   }

   #endregion Constructor

   #region Public Methods

   /// <summary>The scratch folder.</summary>
   public string Scratch { get; }

   /// <summary>The scratch results folder.</summary>
   public string Results { get; }

   /// <summary>The two-session consolidation.</summary>
   public ConsolidateOutcome Two { get; }

   /// <summary>The same consolidation without weaviate.</summary>
   public ConsolidateOutcome Narrowed { get; }

   /// <summary>
   /// Runs the command with the repository's facts and exclusions and v5 and v6 as basis.
   /// </summary>
   /// <param name="results">Results folder.</param>
   /// <param name="output">Output folder.</param>
   /// <param name="args">The session arguments and any more.</param>
   /// <returns>The outcome.</returns>
   public static ConsolidateOutcome Run( string results, string output, params string[] args )
   {
      return RunIn( results, output, ConsolidateHarness.RepoRoot(), args );
   }

   /// <summary>
   /// Runs the command against a given repository root.
   /// </summary>
   /// <param name="results">Results folder.</param>
   /// <param name="output">Output folder.</param>
   /// <param name="repo">Repository root.</param>
   /// <param name="args">The session arguments and any more.</param>
   /// <returns>The outcome.</returns>
   public static ConsolidateOutcome RunIn( string results, string output, string repo, params string[] args )
   {
      string[] full = args.Concat( new[]
      {
         "--basis-session", "v5=" + string.Join( ",", ConsolidateRealRunsTests.V5 ), "--basis-session", "v6=" + string.Join( ",", ConsolidateRealRunsTests.V6 ), "--results-dir", results, "--repo", repo,
         "--facts", Path.Combine( repo, "src", "GenericVectorBuilder.Bench", "Report", "engine-facts.json" ), "--exclusions", Path.Combine( repo, "deploy", "bench", "basis-exclusions.json" ),
         "--out", output,
      } ).ToArray();
      var result = (string[])ConsolidateHarness.Call( "Consolidate", (object)full )!;
      string log = string.Join( Environment.NewLine, result.Skip( 1 ) );
      string json = Path.Combine( output, "consolidated.json" );
      Assert.True( File.Exists( json ), log );
      return new ConsolidateOutcome( int.Parse( result[0] ), log, JsonDocument.Parse( File.ReadAllText( json ) ).RootElement.Clone(), File.ReadAllText( Path.Combine( output, "consolidated.md" ) ) );
   }

   #endregion Public Methods

   #region IDisposable

   /// <summary>
   /// Removes the scratch folder (copies only).
   /// </summary>
   public void Dispose()
   {
      Directory.Delete( Scratch, recursive: true );
   }

   #endregion IDisposable
}

/// <summary>
/// What one consolidation wrote.
/// </summary>
/// <param name="Exit">The exit code.</param>
/// <param name="Log">The command's log.</param>
/// <param name="Root">consolidated.json.</param>
/// <param name="Md">consolidated.md.</param>
public sealed record ConsolidateOutcome( int Exit, string Log, JsonElement Root, string Md )
{
   /// <summary>
   /// The text of the first sentence with a slot, or null.
   /// </summary>
   /// <param name="slot">The slot.</param>
   /// <returns>The text or null.</returns>
   public string? Text( string slot )
   {
      foreach( JsonElement s in Root.GetProperty( "sentences" ).EnumerateArray() )
      {
         if( s.GetProperty( "slot" ).GetString() == slot )
         {
            return s.GetProperty( "text" ).GetString();
         }
      }

      return null;
   }

   /// <summary>
   /// Every sentence whose slot is the prefix or starts with it and a dot.
   /// </summary>
   /// <param name="prefix">The prefix.</param>
   /// <returns>Slot and text pairs.</returns>
   public List<(string Slot, string Text)> Under( string prefix )
   {
      return Root.GetProperty( "sentences" ).EnumerateArray().Select( s => ( Slot: s.GetProperty( "slot" ).GetString()!, Text: s.GetProperty( "text" ).GetString()! ) )
         .Where( s => s.Slot == prefix || s.Slot.StartsWith( prefix + ".", StringComparison.Ordinal ) ).ToList();
   }
}

/// <summary>
/// The dry check of the v8 pipeline on real v5, v6 and v7 data blocked it for six reasons; these tests hold the
/// fixes of the report layer: the basis text says it compares runs of one recorded setup and lists every split with
/// the field that changed, the move it hides and the threshold it would give; every engine's recorded search settings
/// are in the text and the JSON, MariaDB's with its recorded caveat; Weaviate's durability text is corrected from its
/// compose file; the blocked v5 and v6 sets are named; and the fix-with items (fact rows with no value, the
/// "not recorded, recorded" rows, the duplicate golden line, the empty heading, the v5 searchSettings wording, the
/// seed phrase of an exclusion, the orders on the line, dockerd in the busy-box disclosure, the saved strace logs,
/// the mongodb graph fact and the compose settings beside the Redis note).
/// Every test fails on the tree the dry check blocked.
/// </summary>
public sealed class ConsolidateDryFixTests : IClassFixture<DryFixFixture>
{
   #region Data Members

   private readonly DryFixFixture _fixture;

   #endregion Data Members

   #region Constructor

   /// <summary>
   /// Takes the shared consolidation.
   /// </summary>
   /// <param name="fixture">The fixture.</param>
   public ConsolidateDryFixTests( DryFixFixture fixture )
   {
      _fixture = fixture;
   }

   #endregion Constructor

   #region Public Methods

   /// <summary>
   /// Reason 1: the text says the basis compares runs of one recorded setup, no sentence says "over all basis runs", and the
   /// four splits of the real data (clickhouse v6 to v7, duckdb, mariadb and sqlitevec v5 to v6) are listed with their fields,
   /// the one-engine and pair moves they hide and the threshold each would give, which an independent replay from the raw
   /// files (tools/splits.py) computed first. The threshold itself stays 35%.
   /// </summary>
   /// <returns>A task.</returns>
   [Fact]
   public async Task Basis_SaysItComparesRunsOfOneRecordedSetup_AndListsEverySplit()
   {
      await ConsolidateHarness.Timed( () =>
      {
         ConsolidateOutcome two = _fixture.Two;
         Assert.Equal( 3500, two.Root.GetProperty( "threshold" ).GetProperty( "tBp" ).GetInt32() );
         Assert.Equal( 2908, two.Root.GetProperty( "basis" ).GetProperty( "maxBp" ).GetInt32() );
         Assert.DoesNotContain( "over all basis runs", two.Md );
         Assert.Contains( "recorded the same setup", two.Text( "basis.identity" ) ?? string.Empty );
         JsonElement splits = two.Root.GetProperty( "basis" ).GetProperty( "setupSplits" );
         Dictionary<string, JsonElement> byTarget = splits.EnumerateArray().ToDictionary( s => s.GetProperty( "target" ).GetString()!, s => s );
         Assert.Equal( new[] { "clickhouse", "duckdb", "mariadb", "sqlitevec" }, byTarget.Keys.OrderBy( k => k, StringComparer.Ordinal ).ToArray() );
         Assert.Equal( new[] { "engine" }, Strings( byTarget["clickhouse"].GetProperty( "fields" ) ) );
         Assert.Equal( new[] { "index" }, Strings( byTarget["sqlitevec"].GetProperty( "fields" ) ) );
         Assert.Equal( new[] { "index" }, Strings( byTarget["duckdb"].GetProperty( "fields" ) ) );
         Assert.Equal( new[] { "durability" }, Strings( byTarget["mariadb"].GetProperty( "fields" ) ) );
         Assert.Equal( ( 3229, "qps8", 3794, "clickhouse/mongodb", 4500 ), Hidden( byTarget["clickhouse"] ) );
         Assert.Equal( ( 11936, "qps8", 15508, "oracle/sqlitevec", 16500 ), Hidden( byTarget["sqlitevec"] ) );
         Assert.Equal( ( 11556, "qps8", 14495, "duckdb/oracle", 15000 ), Hidden( byTarget["duckdb"] ) );
         Assert.Equal( ( 409, "exactP50Ms", 1785, "mariadb/oracle", 3500 ), Hidden( byTarget["mariadb"] ) );
         Assert.Equal( new[] { "v6" }, Strings( byTarget["clickhouse"].GetProperty( "before" ).GetProperty( "sessions" ) ) );
         Assert.Equal( new[] { "v7", "v8" }, Strings( byTarget["clickhouse"].GetProperty( "after" ).GetProperty( "sessions" ) ) );
         List<(string Slot, string Text)> sentences = two.Under( "basis.split" );
         Assert.Equal( 4, sentences.Count( x => x.Slot.EndsWith( ".fields", StringComparison.Ordinal ) ) );
         Assert.Equal( 4, sentences.Count( x => x.Slot.EndsWith( ".moves", StringComparison.Ordinal ) ) );
         Assert.Equal( 4, sentences.Count( x => x.Slot.EndsWith( ".conditions", StringComparison.Ordinal ) ) );
         Assert.Contains( sentences, x => x.Text.StartsWith( "The two runs of that one-engine move also differ in turbo, uncore", StringComparison.Ordinal ) );
         Assert.Contains( sentences, s => s.Text.Contains( "clickhouse", StringComparison.Ordinal ) && s.Text.Contains( "engine", StringComparison.Ordinal ) );
         Assert.Contains( sentences, s => s.Text.Contains( "the threshold would be 45%", StringComparison.Ordinal ) );
         Assert.Contains( sentences, s => s.Text.Contains( "the threshold would be 165%", StringComparison.Ordinal ) );
         Assert.Contains( "| setup split |", two.Md );
      } );
   }

   /// <summary>
   /// Fix-with: the largest move's two runs differ in whether searchSettings was recorded (v5 recorded none), and the
   /// sentence does not say the runs "differ in searchSettings".
   /// </summary>
   /// <returns>A task.</returns>
   [Fact]
   public async Task Basis_DifferencesSentence_SaysV5RecordedNoSearchSettings()
   {
      await ConsolidateHarness.Timed( () =>
      {
         string text = _fixture.Two.Text( "basis.max.differences" ) ?? string.Empty;
         Assert.Contains( "whether searchSettings was recorded", text );
         Assert.DoesNotContain( "and searchSettings", text );
      } );
   }

   /// <summary>
   /// Fix-with: an exclusion is cited by the phrase of its verdict that carries its seeds, not by a single word.
   /// </summary>
   /// <returns>A task.</returns>
   [Fact]
   public async Task Exclusions_CiteThePhraseThatCarriesTheSeeds()
   {
      await ConsolidateHarness.Timed( () =>
      {
         string vespa = _fixture.Two.Text( "basis.exclusion.0" ) ?? string.Empty;
         string elastic = _fixture.Two.Text( "basis.exclusion.1" ) ?? string.Empty;
         string clickhouse = _fixture.Two.Text( "basis.exclusion.2" ) ?? string.Empty;
         Assert.Contains( "ran first in runs 502 and 503", vespa );
         Assert.DoesNotContain( "holds the words: cold.", vespa );
         Assert.Contains( "run 503", elastic );
         Assert.Contains( "after the v5 runs ended", clickhouse );
      } );
   }

   /// <summary>
   /// Reason 2: every engine that recorded searchSettings has them in a sentence and in the JSON, and MariaDB's raised ef
   /// comes with its recorded recall caveat; the engines whose recorded effort is not a plain number carry their recorded clause.
   /// </summary>
   /// <returns>A task.</returns>
   [Fact]
   public async Task SearchSettings_AreInTheTextAndTheJson_MariaDbWithItsCaveat()
   {
      await ConsolidateHarness.Timed( () =>
      {
         ConsolidateOutcome two = _fixture.Two;
         JsonElement rows = two.Root.GetProperty( "searchSettings" );
         Assert.Equal( 19, rows.GetArrayLength() );
         Dictionary<string, Dictionary<string, string>> byTarget = rows.EnumerateArray().ToDictionary( r => r.GetProperty( "target" ).GetString()!,
            r => r.GetProperty( "settings" ).EnumerateArray().ToDictionary( s => s.GetProperty( "key" ).GetString()!, s => s.GetProperty( "value" ).GetString()! ) );
         Assert.Equal( "100", byTarget["mariadb"]["mhnsw_ef_search"] );
         Assert.Equal( "256", byTarget["clickhouse"]["hnsw_candidate_list_size_for_search"] );
         Assert.Equal( "20x", byTarget["mongodb"]["numCandidates"] );
         Assert.Equal( "-1", byTarget["weaviate"]["ef"] );
         Assert.Equal( "server", byTarget["qdrant-hnsw"]["hnsw_ef"] );
         Assert.Equal( "100", byTarget["pgvector"]["hnsw.ef_search"] );
         Assert.Contains( "mhnsw_ef_search=100", two.Text( "why.settings.mariadb" ) ?? string.Empty );
         Assert.Contains( "hnsw.ef_search=100", two.Text( "why.settings.pgvector" ) ?? string.Empty );
         Assert.Contains( "hnsw_candidate_list_size_for_search=256", two.Text( "why.settings.clickhouse" ) ?? string.Empty );
         string caveat = Effort( two, "mariadb" );
         Assert.Contains( "MariaDB's own default is 20", caveat );
         Assert.DoesNotContain( "recall@10 at ef 100 falls as the set grows", caveat );
         Assert.Contains( "dynamic: limit x 8 clamped 100..500", Effort( two, "weaviate" ) );
         Assert.Contains( "hnsw_ef=server default", Effort( two, "qdrant-hnsw" ) );
         Assert.Contains( "numCandidates=20x hits (min 100)", Effort( two, "mongodb" ) );
         Assert.Contains( "MariaDB's own default is 20", two.Md );
         Assert.Equal( 19, two.Under( "why.settings" ).Count );
      } );
   }

   /// <summary>
   /// Reason 3: the recorded Weaviate durability text says its compose file sets no persistence variable, and that file sets PERSISTENCE_DATA_PATH. The
   /// false clause is not printed, one sentence generated from the compose file says so and names the variable, and the recorded text is untouched.
   /// </summary>
   /// <returns>A task.</returns>
   [Fact]
   public async Task WeaviateDurability_IsCorrectedFromItsComposeFile_OnceAndWithoutChangingTheRecord()
   {
      await ConsolidateHarness.Timed( () =>
      {
         ConsolidateOutcome two = _fixture.Two;
         string recorded = JsonDocument.Parse( File.ReadAllText( Path.Combine( ConsolidateHarness.BenchResults(), ConsolidateRealRunsTests.V7[0], "results.json" ) ) ).RootElement
            .GetProperty( "targets" ).EnumerateArray().First( t => t.GetProperty( "name" ).GetString() == "weaviate" ).GetProperty( "durability" ).GetString()!;
         Assert.Contains( "weaviate.compose.yaml sets no persistence variable", recorded );
         List<string> printed = two.Under( "disclosure.durability.weaviate" ).Select( s => s.Text ).ToList();
         Assert.DoesNotContain( printed, t => t.Contains( "Weaviate 1.39.8 defaults, weaviate.compose.yaml", StringComparison.Ordinal ) );
         Assert.Single( printed, t => t.Contains( "sets no persistence variable", StringComparison.Ordinal ) );
         string correction = two.Text( "disclosure.durability.weaviate.correction" ) ?? string.Empty;
         Assert.Contains( "PERSISTENCE_DATA_PATH", correction );
         Assert.Contains( "sets no persistence variable", correction );
         Assert.Contains( correction, two.Md );
         Assert.Empty( two.Root.GetProperty( "guards" ).GetProperty( "g3" ).GetProperty( "targets" ).EnumerateArray() );
      } );
   }

   /// <summary>
   /// Reason 3, the other way: a compose file that sets no PERSISTENCE variable makes the recorded clause true, so it is printed and nothing is corrected.
   /// </summary>
   /// <returns>A task.</returns>
   [Fact]
   public async Task WeaviateDurability_NeedsNoCorrection_WhenItsComposeFileSetsNoPersistenceVariable()
   {
      await ConsolidateHarness.Timed( () =>
      {
         string repo = Path.Combine( _fixture.Scratch, "repo-no-persistence" );
         CopyRepo( repo );
         string compose = Path.Combine( repo, "deploy", "engines", "weaviate.compose.yaml" );
         File.WriteAllLines( compose, File.ReadAllLines( compose ).Where( l => !l.Contains( "PERSISTENCE_DATA_PATH", StringComparison.Ordinal ) ) );
         ConsolidateOutcome outcome = DryFixFixture.RunIn( _fixture.Results, Path.Combine( _fixture.Scratch, "out-no-persistence" ), repo, "--session", "v7=" + string.Join( ",", ConsolidateRealRunsTests.V7 ) );
         Assert.Equal( 0, outcome.Exit );
         Assert.Contains( outcome.Under( "disclosure.durability.weaviate" ), s => s.Text.StartsWith( "Weaviate 1.39.8 defaults, weaviate.compose.yaml sets no persistence variable", StringComparison.Ordinal ) );
         Assert.Null( outcome.Text( "disclosure.durability.weaviate.correction" ) );
      } );
   }

   /// <summary>
   /// The history part of reason 5: the blocked v5, v6 and v7 sets are each named, with their folder and their verdict.
   /// </summary>
   /// <returns>A task.</returns>
   [Fact]
   public async Task Reuse_NamesTheBlockedSetsOfV5V6AndV7()
   {
      await ConsolidateHarness.Timed( () =>
      {
         ConsolidateOutcome two = _fixture.Two;
         string v5 = two.Text( "runs.reuse.v5" ) ?? string.Empty;
         string v6 = two.Text( "runs.reuse.v6" ) ?? string.Empty;
         string v7 = two.Text( "runs.reuse.v7" ) ?? string.Empty;
         Assert.Contains( "blocked-2026-10-05-v5", v5 );
         Assert.Contains( "design/verdicts/v5-verdict.txt", v5 );
         Assert.Contains( "blocked-2026-10-05-v6", v6 );
         Assert.Contains( "design/verdicts/v6-verdict.md", v6 );
         Assert.Contains( "blocked-2026-10-06-v7", v7 );
         Assert.Contains( "design/verdicts/v7-verdict.md", v7 );
         JsonElement reuse = two.Root.GetProperty( "reuse" );
         Assert.Equal( new[] { "v5", "v6", "v7" }, reuse.EnumerateArray().Select( r => r.GetProperty( "session" ).GetString()! ).OrderBy( s => s, StringComparer.Ordinal ).ToArray() );
         JsonElement fiveRuns = reuse.EnumerateArray().First( r => r.GetProperty( "session" ).GetString() == "v5" ).GetProperty( "runs" );
         Assert.Equal( ConsolidateRealRunsTests.V5, Strings( fiveRuns ) );
      } );
   }

   /// <summary>
   /// The history part of reason 5, without a blocked folder beside the runs: the note quotes the verdict and names no set.
   /// </summary>
   /// <returns>A task.</returns>
   [Fact]
   public async Task Reuse_WithNoBlockedFolderBesideTheRuns_QuotesTheVerdictOnly()
   {
      await ConsolidateHarness.Timed( () =>
      {
         string results = Path.Combine( _fixture.Scratch, "results-bare" );
         foreach( string folder in ConsolidateRealRunsTests.V5.Concat( ConsolidateRealRunsTests.V6 ).Concat( ConsolidateRealRunsTests.V7 ) )
         {
            Directory.CreateDirectory( Path.Combine( results, folder ) );
            File.Copy( Path.Combine( _fixture.Results, folder, "results.json" ), Path.Combine( results, folder, "results.json" ) );
         }

         ConsolidateOutcome outcome = DryFixFixture.Run( results, Path.Combine( _fixture.Scratch, "out-bare" ), "--session", "v7=" + string.Join( ",", ConsolidateRealRunsTests.V7 ) );
         Assert.Equal( 0, outcome.Exit );
         string v5 = outcome.Text( "runs.reuse.v5" ) ?? string.Empty;
         Assert.Equal( "The runs of v5 were also used by a report that was not published; its verdict, design/verdicts/v5-verdict.txt, holds the word BLOCK.", v5 );
         Assert.Empty( outcome.Root.GetProperty( "reuse" ).EnumerateArray().First().GetProperty( "blockedFolders" ).EnumerateArray() );
      } );
   }

   /// <summary>
   /// Fix-with: no fact row is a bare setting name or holds an unbalanced bracket; DuckDB's two settings carry their values.
   /// </summary>
   /// <returns>A task.</returns>
   [Fact]
   public async Task FactRows_HoldTheirValue_AndBalancedBrackets()
   {
      await ConsolidateHarness.Timed( () =>
      {
         List<(string Target, string Kind, string Text, string Confidence)> facts = FactRows( _fixture.Two );
         Assert.True( facts.Count > 100, $"{facts.Count} fact rows" );
         foreach( ( string target, string kind, string text, _ ) in facts )
         {
            Assert.True( text.Count( c => c == '(' ) == text.Count( c => c == ')' ), $"{target} {kind}: unbalanced '{text}'" );
            Assert.False( text is "SET checkpoint_threshold" or "SET memory_limit", $"{target} {kind}: no value in '{text}'" );
         }

         Assert.Contains( facts, f => f.Target == "duckdb" && f.Text.Contains( "CheckpointThreshold", StringComparison.Ordinal ) && f.Text.Contains( "256MB", StringComparison.Ordinal ) );
         Assert.Contains( facts, f => f.Target == "duckdb" && f.Text.Contains( "MemoryLimit", StringComparison.Ordinal ) && f.Text.Contains( "8GB", StringComparison.Ordinal ) );
         Assert.Contains( facts, f => f.Target == "sql" && f.Text.Contains( "CAST( @q AS VECTOR", StringComparison.Ordinal ) && f.Text.TrimEnd().EndsWith( ")", StringComparison.Ordinal ) );
      } );
   }

   /// <summary>
   /// Fix-with: no row reads "not recorded" with the confidence "recorded"; the absence check has a label of its own.
   /// </summary>
   /// <returns>A task.</returns>
   [Fact]
   public async Task AbsenceRows_AreNotLabelledRecorded()
   {
      await ConsolidateHarness.Timed( () =>
      {
         List<(string Target, string Kind, string Text, string Confidence)> facts = FactRows( _fixture.Two );
         List<(string Target, string Kind, string Text, string Confidence)> absent = facts.Where( f => f.Text == "not recorded" ).ToList();
         Assert.True( absent.Count >= 13, $"{absent.Count} absence rows" );
         Assert.DoesNotContain( absent, f => f.Confidence == "recorded" );
         Assert.DoesNotContain( "| not recorded | recorded |", _fixture.Two.Md );
      } );
   }

   /// <summary>
   /// Fix-with: the golden-queries line is printed once when both sessions recorded the same text.
   /// </summary>
   /// <returns>A task.</returns>
   [Fact]
   public async Task QueriesLine_IsPrintedOnce_WhenBothSessionsRecordedTheSameText()
   {
      await ConsolidateHarness.Timed( () =>
      {
         ConsolidateOutcome two = _fixture.Two;
         Assert.Single( two.Under( "subtitle.queries" ) );
         Assert.Equal( 1, Occurrences( two.Md, "> golden: 20 labelled questions" ) );
      } );
   }

   /// <summary>
   /// Fix-with: the heading "Targets not in this report" is printed when a target is left out and not otherwise.
   /// </summary>
   /// <returns>A task.</returns>
   [Fact]
   public async Task NotInReportHeading_IsPrintedOnlyWhenItHasSentences()
   {
      await ConsolidateHarness.Timed( () =>
      {
         Assert.DoesNotContain( "## Targets not in this report", _fixture.Two.Md );
         Assert.Contains( "## Targets not in this report", _fixture.Narrowed.Md );
         Assert.Contains( "weaviate has no row", _fixture.Narrowed.Md );
      } );
   }

   /// <summary>
   /// Fix-with: the orders that clear the line by 25 bp or less are listed, in the JSON, a sentence and a table; the stand-in's four are
   /// the ones the dry check's lenses found by hand (13508, 13513, 13525 and 13504 bp).
   /// </summary>
   /// <returns>A task.</returns>
   [Fact]
   public async Task OrdersOnTheLine_AreListed()
   {
      await ConsolidateHarness.Timed( () =>
      {
         ConsolidateOutcome two = _fixture.Two;
         JsonElement drift = two.Root.GetProperty( "drift" );
         Assert.Equal( 25, drift.GetProperty( "onLineBp" ).GetInt32() );
         List<(string Metric, string A, string B, int Bp)> onLine = drift.GetProperty( "onLine" ).EnumerateArray()
            .Select( o => ( o.GetProperty( "metric" ).GetString()!, o.GetProperty( "a" ).GetString()!, o.GetProperty( "b" ).GetString()!, o.GetProperty( "minRatioBp" ).GetInt32() ) ).ToList();
         Assert.Equal( 4, onLine.Count );
         Assert.Contains( ( "p50Ms", "oracle", "mongodb", 13508 ), onLine );
         Assert.Contains( ( "p50Ms", "mongodb", "sqlitevec", 13513 ), onLine );
         Assert.Contains( ( "qps1", "oracle", "mongodb", 13525 ), onLine );
         Assert.Contains( ( "exactP50Ms", "sqlitevec", "opensearch", 13504 ), onLine );
         Assert.Equal( 4, drift.GetProperty( "onLineCount" ).GetInt32() );
         Assert.Contains( "4 ordered pairs cleared 1.35 times by 25 bp or less", two.Text( "drift.onLine" ) ?? string.Empty );
         Assert.Contains( "| on the line |", two.Md );
      } );
   }

   /// <summary>
   /// Fix-with: the busy-box disclosure says dockerd's CPU is subtracted from outside load as benchmark work (read from the code lines that do it),
   /// and a busy-box flag prints its outside load to three decimals, so a pass at 0.301 is not shown as 0.30.
   /// </summary>
   /// <returns>A task.</returns>
   [Fact]
   public async Task BusyBoxDisclosure_StatesTheDockerdSubtraction_AndThreeDecimals()
   {
      await ConsolidateHarness.Timed( () =>
      {
         ConsolidateOutcome two = _fixture.Two;
         string busy = two.Text( "disclosure.busy" ) ?? string.Empty;
         Assert.Contains( "dockerd", busy );
         Assert.Contains( "outside load", busy );
         Assert.Contains( "benchmark work", busy );
         JsonElement sources = two.Root.GetProperty( "sentences" ).EnumerateArray().First( s => s.GetProperty( "slot" ).GetString() == "disclosure.busy" ).GetProperty( "sources" );
         Assert.Contains( sources.EnumerateArray(), s => s.GetProperty( "ref" ).GetString()!.StartsWith( "src/GenericVectorBuilder.Bench/Running/MachineControlPinning.cs#", StringComparison.Ordinal ) );
         Assert.Contains( sources.EnumerateArray(), s => s.GetProperty( "ref" ).GetString()!.StartsWith( "src/GenericVectorBuilder.Bench/Running/MachineControlSampler.cs#", StringComparison.Ordinal ) );
         List<string> flags = two.Root.GetProperty( "metrics" ).EnumerateArray().SelectMany( m => m.GetProperty( "rows" ).EnumerateArray() ).SelectMany( r => r.GetProperty( "flags" ).EnumerateArray() )
            .Select( f => f.GetProperty( "text" ).ValueKind == JsonValueKind.String ? f.GetProperty( "text" ).GetString()! : string.Empty ).Where( t => t.Contains( "busy box", StringComparison.Ordinal ) ).ToList();
         Assert.NotEmpty( flags );
         Assert.Contains( flags, t => t.Contains( "0.301 CPUs", StringComparison.Ordinal ) );
         Assert.DoesNotContain( flags, t => t.Contains( "0.30 CPUs", StringComparison.Ordinal ) );
      } );
   }

   /// <summary>
   /// Fix-with: the durability numbers dated 2026-10-04 are cited to saved strace logs in the repository, each listed with its SHA-256,
   /// and an engine whose text cites a measurement with no saved log is named as having none.
   /// </summary>
   /// <returns>A task.</returns>
   [Fact]
   public async Task DurabilityNumbers_CiteTheSavedLogs()
   {
      await ConsolidateHarness.Timed( () =>
      {
         string folder = "design/engine-docs/durability-logs-2026-10-04";
         string full = Path.Combine( ConsolidateHarness.RepoRoot(), folder );
         Assert.True( Directory.Exists( full ), full );
         string[] sums = File.ReadAllLines( Path.Combine( full, "SHA256SUMS" ) ).Where( l => l.Trim().Length > 0 ).ToArray();
         Assert.True( sums.Length >= 8, $"{sums.Length} saved files" );
         foreach( string line in sums )
         {
            string[] part = line.Split( "  ", 2 );
            Assert.Equal( part[0], Convert.ToHexString( SHA256.HashData( File.ReadAllBytes( Path.Combine( full, part[1] ) ) ) ).ToLowerInvariant() );
         }

         LogsReproduceTheCountsTheTextsQuote( full );

         ConsolidateOutcome two = _fixture.Two;
         string intro = two.Text( "disclosure.durability.logs" ) ?? string.Empty;
         Assert.Contains( folder, intro );
         Assert.Contains( "clickhouse", intro );
         Assert.Contains( "qdrant", intro );
         string none = two.Text( "disclosure.durability.nologs" ) ?? string.Empty;
         Assert.Contains( "sqlitevec", none );
      } );
   }

   /// <summary>
   /// The saved logs hold what the sinks' durability texts say about them: ClickHouse made no fsync, fdatasync or sync_file_range call for acknowledged
   /// inserts and 61 completed fdatasync calls for its control table; Milvus synced only the embedded etcd files; MongoDB synced its journal; Qdrant made
   /// 30 msync(MS_SYNC) calls for 30 wait=true upserts, acknowledged 30 wait=false upserts within 0.26 s and made its first WAL msync 2.7 s after the last reply.
   /// </summary>
   /// <param name="folder">The folder of the saved logs.</param>
   private static void LogsReproduceTheCountsTheTextsQuote( string folder )
   {
      string[] Lines( string name ) => File.ReadAllLines( Path.Combine( folder, name ) );
      string[] inserts = Lines( "clickhouse-acknowledged-inserts.strace" );
      Assert.Equal( 0, inserts.Count( l => l.Contains( "fsync(", StringComparison.Ordinal ) || l.Contains( "fdatasync(", StringComparison.Ordinal ) || l.Contains( "sync_file_range(", StringComparison.Ordinal ) ) );
      Assert.Equal( 61, Lines( "clickhouse-control-fsync-after-insert.strace" ).Count( l => l.Contains( "fdatasync", StringComparison.Ordinal ) && l.TrimEnd().EndsWith( "= 0", StringComparison.Ordinal ) ) );
      List<string> milvus = Lines( "milvus-acknowledged-upserts.strace" ).Where( l => l.Contains( "fdatasync(", StringComparison.Ordinal ) && l.Contains( '<', StringComparison.Ordinal ) && !l.Contains( "<unfinished", StringComparison.Ordinal ) ).ToList();
      Assert.NotEmpty( milvus );
      Assert.All( milvus, l => Assert.Contains( "/etcd/member/", l ) );
      Assert.Contains( Lines( "mongodb-acknowledged-inserts.strace" ), l => l.Contains( "fdatasync(", StringComparison.Ordinal ) && l.Contains( "/journal/WiredTigerLog", StringComparison.Ordinal ) );
      Assert.Equal( 30, Lines( "qdrant-wait-true.strace" ).Count( l => l.Contains( "msync(", StringComparison.Ordinal ) && l.Contains( "MS_SYNC) = 0", StringComparison.Ordinal ) ) );
      using JsonDocument marks = JsonDocument.Parse( File.ReadAllText( Path.Combine( folder, "qdrant-wait-false.marks.json" ) ) );
      double start = marks.RootElement.GetProperty( "marks" )[0][0].GetDouble();
      double end = marks.RootElement.GetProperty( "end" ).GetDouble();
      Assert.InRange( end - start, 0.25, 0.27 );
      string first = Lines( "qdrant-wait-false.strace" ).First( l => l.Contains( "msync(", StringComparison.Ordinal ) );
      double firstAt = double.Parse( first.Split( ' ', StringSplitOptions.RemoveEmptyEntries )[1], System.Globalization.CultureInfo.InvariantCulture );
      Assert.InRange( firstAt - end, 2.6, 2.8 );
   }

   /// <summary>
   /// Fix-with: mongodb's search fact rests on mongot's own report, and the report says its segments held at most 278 vectors (the largest of the three v7 runs), fewer than
   /// the 1,043 where the Elasticsearch text records a graph, and that no graph was checked.
   /// </summary>
   /// <returns>A task.</returns>
   [Fact]
   public async Task MongodbGraphFact_CarriesItsCaveat()
   {
      await ConsolidateHarness.Timed( () =>
      {
         string text = _fixture.Two.Text( "disclosure.graph.mongodb" ) ?? string.Empty;
         Assert.Contains( "at most 278 vectors", text );
         Assert.Contains( "1,043", text );
         Assert.Contains( "elasticsearch", text );
         Assert.DoesNotContain( _fixture.Two.Under( "disclosure.graph" ), s => s.Slot != "disclosure.graph.mongodb" );
      } );
   }

   /// <summary>
   /// Fix-with: the Redis note under every table says what the compose file sets beside the docs quote.
   /// </summary>
   /// <returns>A task.</returns>
   [Fact]
   public async Task RedisRowNote_NamesTheComposeSettings()
   {
      await ConsolidateHarness.Timed( () =>
      {
         JsonElement redis = _fixture.Two.Root.GetProperty( "metrics" ).EnumerateArray().First().GetProperty( "rows" ).EnumerateArray().First( r => r.GetProperty( "target" ).GetString() == "redis" );
         string note = redis.GetProperty( "note" ).GetString()!;
         Assert.Contains( "save \"300 1\"", note );
         Assert.Contains( "appendonly no", note );
         Assert.Contains( "in-memory", note );
      } );
   }

   /// <summary>
   /// Lens finding (facts-engines): the p50 and QPS tables carry how each engine searched, so the four scans (qdrant, sql, sqlitevec and elasticsearch, whose
   /// index held no graph at 524 vectors) are not read as graph engines; the exact table has no such column, since every row there is an exact mode.
   /// </summary>
   /// <returns>A task.</returns>
   [Fact]
   public async Task Tables_SayWhichEnginesScanAndWhichUseAGraph()
   {
      await ConsolidateHarness.Timed( () =>
      {
         ConsolidateOutcome two = _fixture.Two;
         foreach( JsonElement table in two.Root.GetProperty( "metrics" ).EnumerateArray().Where( t => t.GetProperty( "metric" ).GetString() != "exactP50Ms" ) )
         {
            Dictionary<string, string?> modes = table.GetProperty( "rows" ).EnumerateArray().ToDictionary( r => r.GetProperty( "target" ).GetString()!, r => r.GetProperty( "searchMode" ).GetString() );
            Assert.Equal( new[] { "elasticsearch", "qdrant", "sql", "sqlitevec" }, modes.Where( m => m.Value == "exact" ).Select( m => m.Key ).OrderBy( k => k, StringComparer.Ordinal ).ToArray() );
            Assert.Equal( 15, modes.Count( m => m.Value == "approximate" ) );
         }

         Assert.Contains( "| median of 6 | search | not separated from | flags |", two.Md );
         Assert.Contains( "| qdrant | 0.881-0.886 | 0.881-0.886 | 0.883 | exact; measured |", two.Md );
         Assert.Contains( "| redis | 0.393-0.399 | 0.393-0.399 | 0.398 | approximate; recorded, documented |", two.Md );
         string exactHeader = two.Md.Split( '\n' ).First( l => l.StartsWith( "| engine |", StringComparison.Ordinal ) && l.Contains( "median of 6", StringComparison.Ordinal ) && !l.Contains( "| search |", StringComparison.Ordinal ) );
         Assert.Contains( "| median of 6 | not separated from | flags |", exactHeader );
      } );
   }

   /// <summary>
   /// Design O14 (the lens found it gone from the text): the subtitle says that at 524 vectors each figure is the cost of one request through the
   /// engine's client and does not show how an index scales.
   /// </summary>
   /// <returns>A task.</returns>
   [Fact]
   public async Task Subtitle_SaysWhatAFigureIsAtThisSize()
   {
      await ConsolidateHarness.Timed( () =>
      {
         Assert.Equal( "At 524 vectors each figure is the cost of one request through the engine's .NET client, and does not show how an index scales.", _fixture.Two.Text( "subtitle.scope" ) );
         Assert.Contains( "does not show how an index scales", _fixture.Two.Md );
      } );
   }

   /// <summary>
   /// Every sentence the fixes add holds the generated-text rules (the audit ran before anything was written, and no sentence outside the quotes
   /// holds a banned, provenance, purpose, default or "only" word, a dash, or more than 35 words).
   /// </summary>
   /// <returns>A task.</returns>
   [Fact]
   public async Task EverySentenceHoldsTheRules()
   {
      await ConsolidateHarness.Timed( () =>
      {
         var forbidden = new System.Text.RegularExpressions.Regex( @"(?<![A-Za-z0-9_@])(only|default|defaults|purpose|in order to|so that|intended|designed to)(?![A-Za-z0-9_@])", System.Text.RegularExpressions.RegexOptions.IgnoreCase );
         foreach( JsonElement s in _fixture.Two.Root.GetProperty( "sentences" ).EnumerateArray() )
         {
            string slot = s.GetProperty( "slot" ).GetString()!;
            string text = s.GetProperty( "text" ).GetString()!;
            Assert.Contains( text, _fixture.Two.Md );
            if( s.GetProperty( "sources" ).EnumerateArray().Any( x => x.GetProperty( "kind" ).GetString() == "quote" ) )
            {
               continue;
            }

            Assert.Empty( (string[])ConsolidateHarness.Call( "Banned", text )! );
            Assert.False( forbidden.IsMatch( text ), $"{slot}: {text}" );
            Assert.True( (int)ConsolidateHarness.Call( "Words", text )! <= ( slot == "headline" ? 34 : 35 ), $"{slot}: {text}" );
         }
      } );
   }

   #endregion Public Methods

   #region Private Methods

   /// <summary>
   /// Every sentence printed under an engine's effort slot, joined: the head, the labels and the clauses of the recorded effort clause.
   /// </summary>
   /// <param name="outcome">The consolidation.</param>
   /// <param name="target">The engine.</param>
   /// <returns>The texts joined by a space.</returns>
   private static string Effort( ConsolidateOutcome outcome, string target )
   {
      return string.Join( " ", outcome.Under( $"why.effort.{target}" ).Select( s => s.Text ) );
   }

   /// <summary>
   /// The strings of a JSON array.
   /// </summary>
   /// <param name="array">The array.</param>
   /// <returns>The strings.</returns>
   private static string[] Strings( JsonElement array )
   {
      return array.EnumerateArray().Select( e => e.GetString()! ).ToArray();
   }

   /// <summary>
   /// What a setup split hides: the largest one-engine move (bp, metric), the largest pair move (bp, pair) and the threshold the largest would give.
   /// </summary>
   /// <param name="split">The split.</param>
   /// <returns>The five values.</returns>
   private static (int OneBp, string OneMetric, int PairBp, string Pair, int TBp) Hidden( JsonElement split )
   {
      JsonElement one = split.GetProperty( "oneEngine" );
      JsonElement pair = split.GetProperty( "pair" );
      return ( one.GetProperty( "moveBp" ).GetInt32(), one.GetProperty( "metric" ).GetString()!, pair.GetProperty( "moveBp" ).GetInt32(), pair.GetProperty( "pair" ).GetString()!, split.GetProperty( "tBpIfCounted" ).GetInt32() );
   }

   /// <summary>
   /// Every fact row of the why table.
   /// </summary>
   /// <param name="outcome">The outcome.</param>
   /// <returns>Target, kind, text and confidence of each row.</returns>
   private static List<(string Target, string Kind, string Text, string Confidence)> FactRows( ConsolidateOutcome outcome )
   {
      return outcome.Root.GetProperty( "why" ).EnumerateArray()
         .SelectMany( w => w.GetProperty( "facts" ).EnumerateArray().Select( f => ( w.GetProperty( "target" ).GetString()!, f.GetProperty( "kind" ).GetString()!, f.GetProperty( "text" ).GetString()!, f.GetProperty( "confidence" ).GetString()! ) ) ).ToList();
   }

   /// <summary>
   /// How many times a text occurs in another.
   /// </summary>
   /// <param name="haystack">The text searched.</param>
   /// <param name="needle">The text looked for.</param>
   /// <returns>The count.</returns>
   private static int Occurrences( string haystack, string needle )
   {
      int count = 0;
      for( int at = haystack.IndexOf( needle, StringComparison.Ordinal ); at >= 0; at = haystack.IndexOf( needle, at + needle.Length, StringComparison.Ordinal ) )
      {
         count++;
      }

      return count;
   }

   /// <summary>
   /// Copies the files of the repository the consolidation reads (deploy, design, src without build output, the solution file) into a scratch folder.
   /// </summary>
   /// <param name="destination">The scratch repository.</param>
   private static void CopyRepo( string destination )
   {
      string root = ConsolidateHarness.RepoRoot();
      Directory.CreateDirectory( destination );
      File.Copy( Path.Combine( root, "GenericVectorBuilder.slnx" ), Path.Combine( destination, "GenericVectorBuilder.slnx" ) );
      foreach( string top in new[] { "deploy", "design", "src", "tests" } )
      {
         foreach( string file in Directory.GetFiles( Path.Combine( root, top ), "*", SearchOption.AllDirectories ) )
         {
            string relative = Path.GetRelativePath( root, file );
            if( relative.Split( Path.DirectorySeparatorChar ).Any( p => p is "bin" or "obj" ) )
            {
               continue;
            }

            string target = Path.Combine( destination, relative );
            Directory.CreateDirectory( Path.GetDirectoryName( target )! );
            File.Copy( file, target );
         }
      }
   }

   #endregion Private Methods
}
