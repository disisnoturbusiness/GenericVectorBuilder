using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace GenericVectorBuilder.Engines.Tests.Bench;

/// <summary>
/// The sweep of the dry check's second round (verdict v8e): the clauses of every recorded text the report prints are classified, each is printed
/// with how it is backed, and the five clauses the second dry check found hard-coded or mislabelled are fixed (Weaviate's effort clause,
/// MariaDB's recall clause, MongoDB's graph label, the undefined "median rule", the CPUs of embedded engines), with the fix-with items (the observer
/// beside v7, the readings with no saved log, the exact claim ratio, the line after the 1e-5 tie quote, Weaviate's durability corrected once).
/// Every figure a test expects is read again from a raw results.json, a saved file or a repository file, never from the report under test.
/// Every test fails on the tree the second dry check blocked.
/// </summary>
public sealed class ConsolidateSweepTests : IClassFixture<DryFixFixture>, IDisposable
{
   #region Data Members

   private static readonly Regex SPACE = new( @"\s+", RegexOptions.Compiled );
   private static readonly string[] CLASSES = { "read-back", "measured", "documented", "unverified" };

   private readonly DryFixFixture _fixture;
   private readonly string _scratch = Path.Combine( AppContext.BaseDirectory, "consolidate-tests", "sweep-" + Guid.NewGuid().ToString( "N" ) );

   #endregion Data Members

   #region Constructor

   /// <summary>
   /// Takes the shared consolidation of the real runs.
   /// </summary>
   /// <param name="fixture">The fixture.</param>
   public ConsolidateSweepTests( DryFixFixture fixture )
   {
      _fixture = fixture;
      Directory.CreateDirectory( _scratch );
   }

   #endregion Constructor

   #region Public Methods

   /// <summary>
   /// Every durability and index text of every reported engine and every run note is printed as clauses that together are the recorded text, each with a class and, unless it
   /// is unverified, what backs it; no clause is left out of the list. The recorded text is read again from the raw results.json.
   /// </summary>
   /// <returns>A task.</returns>
   [Fact]
   public async Task Sweep_EveryPrintedClause_HasAClass_AndTheClausesAreTheRecordedText()
   {
      await ConsolidateHarness.Timed( () =>
      {
         ConsolidateOutcome two = _fixture.Two;
         JsonElement texts = two.Root.GetProperty( "recordedTexts" );
         Assert.Equal( 0, two.Root.GetProperty( "audit" ).GetProperty( "unlistedClauses" ).GetArrayLength() );
         Assert.Equal( 38, texts.EnumerateArray().Count( t => t.GetProperty( "target" ).GetString()!.Length > 0 ) );
         Assert.True( texts.EnumerateArray().Count( t => t.GetProperty( "field" ).GetString() == "notes" ) >= 14 );
         foreach( JsonElement text in texts.EnumerateArray() )
         {
            string target = text.GetProperty( "target" ).GetString()!;
            string field = text.GetProperty( "field" ).GetString()!;
            string recorded = Collapse( Raw( text.GetProperty( "run" ).GetString()!, target, field, text.TryGetProperty( "note", out JsonElement n ) && n.ValueKind == JsonValueKind.Number ? n.GetInt32() : -1 ) );
            AssertClausesAreTheText( recorded, text, $"{target}.{field}" );
         }
      } );
   }

   /// <summary>
   /// Every clause is printed as a verbatim quote that follows a sentence naming how it is backed, and that sentence agrees with the clause's class: an unverified clause
   /// follows the unverified label, a documented one a documented label, a read-back one a read-back label.
   /// </summary>
   /// <returns>A task.</returns>
   [Fact]
   public async Task Sweep_EveryQuotedClause_FollowsALabelThatAgreesWithItsClass()
   {
      await ConsolidateHarness.Timed( () =>
      {
         ConsolidateOutcome two = _fixture.Two;
         Dictionary<string, string> labels = Labels();
         var cls = new Dictionary<string, List<(string Text, string Class)>>( StringComparer.Ordinal );
         foreach( JsonElement text in two.Root.GetProperty( "recordedTexts" ).EnumerateArray() )
         {
            string prefix = text.GetProperty( "field" ).GetString() switch
            {
               "durability" => $"disclosure.durability.{text.GetProperty( "target" ).GetString()}",
               "index" => $"why.effort.{text.GetProperty( "target" ).GetString()}",
               _ => "method",
            };
            if( !cls.TryGetValue( prefix, out List<(string Text, string Class)>? list ) )
            {
               cls[prefix] = list = new List<(string Text, string Class)>();
            }

            list.AddRange( text.GetProperty( "clauses" ).EnumerateArray().Where( c => c.GetProperty( "printed" ).GetBoolean() ).Select( c => ( c.GetProperty( "text" ).GetString()!, c.GetProperty( "class" ).GetString()! ) ) );
         }

         var label = new Dictionary<string, string>( StringComparer.Ordinal );
         int quotes = 0;
         foreach( JsonElement s in two.Root.GetProperty( "sentences" ).EnumerateArray() )
         {
            Match m = Regex.Match( s.GetProperty( "slot" ).GetString()!, @"^(?<prefix>.+)\.g(?<g>\d+)(?<q>\.q\d+)?$" );
            if( !m.Success )
            {
               continue;
            }

            string text = s.GetProperty( "text" ).GetString()!;
            if( !m.Groups["q"].Success )
            {
               Assert.Contains( text, labels.Values );
               label[m.Groups["prefix"].Value] = text;
               continue;
            }

            quotes++;
            Assert.True( label.TryGetValue( m.Groups["prefix"].Value, out string? current ), $"{s.GetProperty( "slot" ).GetString()} has no label before it" );
            string? clauseClass = cls[m.Groups["prefix"].Value].FirstOrDefault( c => c.Text == text ).Class ?? cls[m.Groups["prefix"].Value].FirstOrDefault( c => c.Text.Contains( text, StringComparison.Ordinal ) ).Class;
            Assert.True( clauseClass != null, $"the quote '{text}' is not a classified clause" );
            Assert.Contains( clauseClass, CLASSES );
            Assert.Contains( current!, AllowedLabels( clauseClass!, labels ) );
         }

         Assert.True( quotes > 100, $"{quotes} clause quotes" );
      } );
   }

   /// <summary>
   /// A clause the list does not cover stops the report when it is in an engine's recorded text (the sinks are frozen, so a changed text is a change to find), and is printed
   /// whole as unverified, labelled and counted, when it is in a run note.
   /// </summary>
   /// <returns>A task.</returns>
   [Fact]
   public async Task Sweep_AnUnlistedClause_StopsTheReport_ForAnEngineText_AndIsLabelledUnverified_ForANote()
   {
      await ConsolidateHarness.Timed( () =>
      {
         string results = CopyResults( "unlisted" );
         foreach( string folder in ConsolidateRealRunsTests.V7.Concat( DryFixFixture.STAND_IN ) )
         {
            Change( results, folder, run => Target( run, "pgvector" )["durability"] = Target( run, "pgvector" )["durability"]!.GetValue<string>() + " A new clause nobody classified." );
         }

         ConsolidateOutcomeOrLog stopped = Try( results, "out-unlisted-1" );
         Assert.Equal( 1, stopped.Exit );
         Assert.Contains( "A new clause nobody classified.", stopped.Log );
         Assert.Contains( "pgvector.durability", stopped.Log );

         string notes = CopyResults( "unlisted-note" );
         foreach( string folder in ConsolidateRealRunsTests.V7.Concat( DryFixFixture.STAND_IN ) )
         {
            Change( notes, folder, run => run["notes"]!.AsArray().Add( "A method note nobody classified." ) );
         }

         ConsolidateOutcomeOrLog printed = Try( notes, "out-unlisted-2" );
         Assert.Equal( 0, printed.Exit );
         JsonElement root = printed.Root!.Value;
         Assert.Contains( root.GetProperty( "audit" ).GetProperty( "unlistedClauses" ).EnumerateArray().Select( c => c.GetString()! ), c => c.EndsWith( "A method note nobody classified.", StringComparison.Ordinal ) );
         string[] sentences = root.GetProperty( "sentences" ).EnumerateArray().Select( s => s.GetProperty( "text" ).GetString()! ).ToArray();
         int at = Array.IndexOf( sentences, "A method note nobody classified." );
         Assert.True( at > 0, "the unlisted note is printed" );
         Assert.Equal( Labels()["CLAUSE_UNLISTED"], sentences[at - 1] );
      } );
   }

   /// <summary>
   /// Every fact of the why table carries the class that says how it is backed; a recorded fact says so in its label (recorded, then the class of the clause of the recorded
   /// text it rests on), and the facts that rest on a typed clause say unverified.
   /// </summary>
   /// <returns>A task.</returns>
   [Fact]
   public async Task Sweep_EveryFact_CarriesItsClass_AndARecordedFactSaysHowItsClauseIsBacked()
   {
      await ConsolidateHarness.Timed( () =>
      {
         JsonElement why = _fixture.Two.Root.GetProperty( "why" );
         var facts = why.EnumerateArray().SelectMany( w => w.GetProperty( "facts" ).EnumerateArray().Select( f => ( Target: w.GetProperty( "target" ).GetString()!, Fact: f ) ) ).ToList();
         Assert.Equal( 116, facts.Count );
         foreach( ( string target, JsonElement fact ) in facts )
         {
            string confidence = fact.GetProperty( "confidence" ).GetString()!;
            Assert.Contains( fact.GetProperty( "class" ).GetString(), CLASSES );
            bool fromTheIndexText = fact.GetProperty( "source" ).GetString()!.Contains( "].index#", StringComparison.Ordinal );
            Assert.True( confidence.StartsWith( "recorded, ", StringComparison.Ordinal ) == fromTheIndexText, $"{target}: {confidence}" );
            if( confidence.StartsWith( "recorded, ", StringComparison.Ordinal ) )
            {
               Assert.Equal( confidence, "recorded, " + fact.GetProperty( "class" ).GetString() );
            }
         }

         string Of( string target, string kind ) => facts.First( f => f.Target == target && f.Fact.GetProperty( "kind" ).GetString() == kind ).Fact.GetProperty( "confidence" ).GetString()!;
         Assert.Equal( "recorded, unverified", Of( "chroma", "search" ) );
         Assert.Equal( "recorded, unverified", Of( "weaviate", "search" ) );
         Assert.Equal( "recorded, read-back", Of( "pgvector", "index" ) );
         Assert.Equal( "recorded, documented", Of( "redis", "index" ) );
      } );
   }

   /// <summary>
   /// Reason 1: Weaviate's effort clause "(dynamic: limit x 8 clamped 100..500)" is neither set nor read back, so it is printed as documented on saved pages, and the label says it
   /// was not read back; the pages are saved with the commit they were taken at, hashed and listed; the three numbers are rows of the saved reference table.
   /// V8 fix-with 13: its "ef=-1" is the value the saved reference table lists for ef, so it is labelled documented on a saved page and not read back, and not as a value the runs read.
   /// </summary>
   /// <returns>A task.</returns>
   [Fact]
   public async Task Weaviate_DynamicEffortClause_IsLabelledDocumentedNotReadBack_WithTheSavedPages()
   {
      await ConsolidateHarness.Timed( () =>
      {
         ConsolidateOutcome two = _fixture.Two;
         List<JsonElement> under = two.Root.GetProperty( "sentences" ).EnumerateArray().Where( s => s.GetProperty( "slot" ).GetString()!.StartsWith( "why.effort.weaviate.", StringComparison.Ordinal ) ).ToList();
         int at = under.FindIndex( s => s.GetProperty( "text" ).GetString() == "(dynamic: limit x 8 clamped 100..500)," || s.GetProperty( "text" ).GetString() == "(dynamic: limit x 8 clamped 100..500)" );
         Assert.True( at > 0, string.Join( " | ", under.Select( s => s.GetProperty( "text" ).GetString() ) ) );
         int ef = under.FindIndex( s => s.GetProperty( "text" ).GetString() == "ef=-1" );
         Assert.Equal( ef + 1, at );
         JsonElement label = under[ef - 1];
         Assert.Equal( "Documented on a saved page, not read back from the engine:", label.GetProperty( "text" ).GetString() );
         string refs = string.Join( "\n", label.GetProperty( "sources" ).EnumerateArray().Select( s => s.GetProperty( "ref" ).GetString() ) );
         Assert.Contains( "weaviate-vector-index-reference-2026-10-07.mdx", refs );
         Assert.Contains( "weaviate-vector-index-concepts-2026-10-07.md", refs );
         string docs = Path.Combine( ConsolidateHarness.RepoRoot(), "design", "engine-docs" );
         string reference = File.ReadAllText( Path.Combine( docs, "weaviate-vector-index-reference-2026-10-07.mdx" ) );
         foreach( ( string name, string value ) in new[] { ( "dynamicEfFactor", "8" ), ( "dynamicEfMin", "100" ), ( "dynamicEfMax", "500" ) } )
         {
            Assert.Matches( @"(?m)^\| `" + name + @"`.*\|\s*" + value + @"\s*\|\s*Yes\s*\|$", reference );
         }

         Assert.Contains( "The dynamic list size will be set as the query limit multiplied by `dynamicEfFactor`, modified by a minimum of `dynamicEfMin` and a maximum of `dynamicEfMax`.", File.ReadAllText( Path.Combine( docs, "weaviate-vector-index-concepts-2026-10-07.md" ) ) );
         string listed = File.ReadAllText( Path.Combine( docs, "SOURCES.txt" ) );
         Assert.Contains( "weaviate-vector-index-reference-2026-10-07.mdx  https://raw.githubusercontent.com/weaviate/docs/", listed );
         Assert.Contains( "weaviate-vector-index-concepts-2026-10-07.md  https://raw.githubusercontent.com/weaviate/docs/", listed );
         Assert.Contains( "weaviate-vector-index-reference-2026-10-07.mdx#`ef`", refs );
         Assert.Matches( @"(?m)^\| `ef`.*\|\s*-1\s*\|\s*Yes\s*\|$", reference );
      } );
   }

   /// <summary>
   /// A documentation row the list quotes must hold in the saved page: with the factor changed in the saved reference table, the report stops and names the row.
   /// </summary>
   /// <returns>A task.</returns>
   [Fact]
   public async Task Weaviate_ADocRowThatDoesNotHold_StopsTheReport()
   {
      await ConsolidateHarness.Timed( () =>
      {
         string repo = CopyRepo( "repo-docrow" );
         string page = Path.Combine( repo, "design", "engine-docs", "weaviate-vector-index-reference-2026-10-07.mdx" );
         File.WriteAllText( page, Regex.Replace( File.ReadAllText( page ), @"(?m)^(\| `dynamicEfFactor`.*\|\s*)8(\s*\|\s*Yes\s*\|)$", "${1}9${2}" ) );
         ConsolidateOutcomeOrLog stopped = Try( _fixture.Results, "out-docrow", repo );
         Assert.Equal( 1, stopped.Exit );
         Assert.Contains( "holds no row dynamicEfFactor with the value 8", stopped.Log );
      } );
   }

   /// <summary>
   /// Reason 2: the recorded MariaDB clause with its recall figures (0.99, 0.89 to 0.92, about 0.09 at 100,000) is not printed anywhere; the report gives the recall of the saved re-run
   /// of the effort test, per collection size, as the lowest and highest of its repeats, which this test reads again from the saved log.
   /// </summary>
   /// <returns>A task.</returns>
   [Fact]
   public async Task MariaDb_RecallClause_IsReplacedByTheSavedReRun_AndTheOldFiguresAreNotPrinted()
   {
      await ConsolidateHarness.Timed( () =>
      {
         ConsolidateOutcome two = _fixture.Two;
         string all = string.Join( "\n", two.Root.GetProperty( "sentences" ).EnumerateArray().Select( s => s.GetProperty( "text" ).GetString() ) );
         Assert.DoesNotContain( "0.89 to 0.92", all );
         Assert.DoesNotContain( "100,000", all );
         Assert.DoesNotContain( "0.89 to 0.92", two.Md );
         Assert.DoesNotContain( "about 0.09", two.Md );
         Dictionary<string, (string Low, string High)> saved = SavedRecall();
         Assert.Equal( new[] { "2000", "524" }, saved.Keys.OrderBy( k => k, StringComparer.Ordinal ).ToArray() );
         foreach( ( string size, ( string low, string high ) ) in saved )
         {
            string sentence = two.Text( $"why.effort.mariadb.recall.{size}" ) ?? string.Empty;
            Assert.Equal( $"In the saved re-run of the mariadb effort test, recall@10 at ef 100 on {size} random 1024-dim vectors read {low} to {high}.", sentence );
         }

         Assert.Contains( "A clause of the mariadb text is not printed: it cites figures that no saved source backs.", two.Under( "why.effort.mariadb" ).Select( s => s.Text ) );
         JsonElement clause = two.Root.GetProperty( "recordedTexts" ).EnumerateArray().First( t => t.GetProperty( "target" ).GetString() == "mariadb" && t.GetProperty( "field" ).GetString() == "index" )
            .GetProperty( "clauses" ).EnumerateArray().First( c => !c.GetProperty( "printed" ).GetBoolean() );
         Assert.Equal( "mariadb-recall", clause.GetProperty( "notPrinted" ).GetString() );
      } );
   }

   /// <summary>
   /// The saved re-run is a record, not a number typed in a sink: it is the repository's effort test run against the benchmark's own container on the engine CPUs, the daily container
   /// untouched before and after, no table left behind, every file hashed, and the one change to the test (the port) in a saved diff.
   /// </summary>
   /// <returns>A task.</returns>
   [Fact]
   public async Task MariaDb_ReRun_IsSavedWithItsEnvironment_AndHashed()
   {
      await ConsolidateHarness.Timed( () =>
      {
         string folder = Path.Combine( ConsolidateHarness.RepoRoot(), "design", "bench-inputs", "mariadb-effort-2026-10-07" );
         string log = File.ReadAllText( Path.Combine( folder, "run.log" ) );
         Assert.Contains( "cpuset=2-3,6-7", log );
         Assert.Contains( "image=sha256:", log );
         Assert.Contains( "daily container unchanged: yes", log );
         Assert.Contains( "leftover tables in the bench container: 0", log );
         Assert.Contains( "SETTING innodb_flush_log_at_trx_commit = 2", log );
         Assert.Contains( "SETTING mhnsw_ef_search = 20", log );
         Assert.True( Regex.Matches( log, @"RECALL@10 on 524 random" ).Count >= 10 && Regex.Matches( log, @"RECALL@10 on 2000 random" ).Count >= 10 );
         Assert.Equal( 10, Directory.GetFiles( folder, "test-repeat-*.txt" ).Length );
         string diff = File.ReadAllText( Path.Combine( folder, "MariaDbEffortTests.port.diff" ) );
         Assert.Single( diff.Split( '\n' ), l => l.StartsWith( "+ ", StringComparison.Ordinal ) );
         Assert.Contains( "GVB_BENCH_MARIADB_PORT", diff );
         foreach( string line in File.ReadAllLines( Path.Combine( folder, "SHA256SUMS" ) ).Where( l => l.Trim().Length > 0 ) )
         {
            string[] part = line.Split( "  ", 2 );
            Assert.Equal( part[0], Convert.ToHexString( SHA256.HashData( File.ReadAllBytes( Path.Combine( folder, part[1] ) ) ) ).ToLowerInvariant() );
         }

         string[] listed = File.ReadAllLines( Path.Combine( folder, "SHA256SUMS" ) ).Where( l => l.Trim().Length > 0 ).Select( l => l.Split( "  ", 2 )[1] ).OrderBy( n => n, StringComparer.Ordinal ).ToArray();
         Assert.Equal( Directory.GetFiles( folder ).Select( f => Path.GetFileName( f )! ).Where( n => n != "SHA256SUMS" ).OrderBy( n => n, StringComparer.Ordinal ).ToArray(), listed );
      } );
   }

   /// <summary>
   /// When the saved re-run holds no line at the effort the runs recorded, the report says no figure is given, never a figure from another effort.
   /// </summary>
   /// <returns>A task.</returns>
   [Fact]
   public async Task MariaDb_ASavedReRunAtAnotherEffort_GivesNoFigure()
   {
      await ConsolidateHarness.Timed( () =>
      {
         string repo = CopyRepo( "repo-recall" );
         string log = Path.Combine( repo, "design", "bench-inputs", "mariadb-effort-2026-10-07", "run.log" );
         File.WriteAllText( log, File.ReadAllText( log ).Replace( ", ef 100 ", ", ef 101 ", StringComparison.Ordinal ) );
         ConsolidateOutcomeOrLog outcome = Try( _fixture.Results, "out-recall", repo );
         Assert.Equal( 0, outcome.Exit );
         JsonElement root = outcome.Root!.Value;
         string[] texts = root.GetProperty( "sentences" ).EnumerateArray().Select( s => s.GetProperty( "text" ).GetString()! ).ToArray();
         Assert.Contains( "No saved re-run of the mariadb effort test matches the ef 100 its runs recorded, so no recall figure is given.", texts );
         Assert.DoesNotContain( texts, t => t.StartsWith( "In the saved re-run", StringComparison.Ordinal ) );
      } );
   }

   /// <summary>
   /// Reason 3: MongoDB's "searched through the HNSW graph" is the engine's own report, and says so wherever it shows: the fact sheet, the why table, the search column of the p50,
   /// QPS@1 and QPS@8 tables, and the page's JSON. No MongoDB fact is labelled measured.
   /// </summary>
   /// <returns>A task.</returns>
   [Fact]
   public async Task MongoDb_GraphFact_IsLabelledTheEnginesOwnReport_EverywhereItShows()
   {
      await ConsolidateHarness.Timed( () =>
      {
         const string OWN = "recorded (engine's own report)";
         ConsolidateOutcome two = _fixture.Two;
         JsonElement mongo = two.Root.GetProperty( "why" ).EnumerateArray().First( w => w.GetProperty( "target" ).GetString() == "mongodb" );
         JsonElement search = mongo.GetProperty( "facts" ).EnumerateArray().First( f => f.GetProperty( "kind" ).GetString() == "search" );
         Assert.Equal( OWN, search.GetProperty( "confidence" ).GetString() );
         Assert.DoesNotContain( mongo.GetProperty( "facts" ).EnumerateArray(), f => f.GetProperty( "confidence" ).GetString() == "measured" );
         Assert.Contains( $"| mongodb | search (approximate) | searched through the HNSW graph (Approximate) | {OWN} | read-back |", two.Md );
         foreach( JsonElement table in two.Root.GetProperty( "metrics" ).EnumerateArray().Where( t => t.GetProperty( "metric" ).GetString() != "exactP50Ms" ) )
         {
            Assert.Equal( OWN, table.GetProperty( "rows" ).EnumerateArray().First( r => r.GetProperty( "target" ).GetString() == "mongodb" ).GetProperty( "searchConfidence" ).GetString() );
         }

         Assert.Equal( 3, two.Md.Split( '\n' ).Count( l => l.StartsWith( "| mongodb |", StringComparison.Ordinal ) && l.Contains( $"| approximate; {OWN} |", StringComparison.Ordinal ) ) );
         Assert.Contains( two.Under( "disclosure.graph" ), s => s.Text.StartsWith( "mongodb's search fact is the engine's own report", StringComparison.Ordinal ) );
         using JsonDocument sheet = JsonDocument.Parse( File.ReadAllText( Path.Combine( ConsolidateHarness.RepoRoot(), "src", "GenericVectorBuilder.Bench", "Report", "engine-facts.json" ) ) );
         Assert.Equal( OWN, sheet.RootElement.EnumerateArray().First( f => f.GetProperty( "target" ).GetString() == "mongodb" && f.GetProperty( "kind" ).GetString() == "search" ).GetProperty( "confidence" ).GetString() );
      } );
   }

   /// <summary>
   /// Reason 4: "the median rule" is defined, with the tolerance and the pinned clock it uses, in the sentence before the first sentence that uses it.
   /// The figures come from the raw runs' own record of the clock.
   /// </summary>
   /// <returns>A task.</returns>
   [Fact]
   public async Task MedianRule_IsDefinedInOneSentence_BeforeItsFirstUse()
   {
      await ConsolidateHarness.Timed( () =>
      {
         ConsolidateOutcome two = _fixture.Two;
         string notes = string.Join( "\n", JsonDocument.Parse( File.ReadAllText( Path.Combine( _fixture.Results, DryFixFixture.STAND_IN[0], "results.json" ) ) ).RootElement.GetProperty( "notes" ).EnumerateArray().Select( n => n.GetString() ) );
         Match recorded = Regex.Match( notes, @"more than (\d+)% off the pinned (\d+) MHz" );
         Assert.True( recorded.Success, "the run's own note states the tolerance and the pinned clock" );
         int tolerance = int.Parse( recorded.Groups[1].Value );
         int pinned = int.Parse( recorded.Groups[2].Value );
         string[] slots = two.Root.GetProperty( "sentences" ).EnumerateArray().Select( s => s.GetProperty( "slot" ).GetString()! ).ToArray();
         string[] texts = two.Root.GetProperty( "sentences" ).EnumerateArray().Select( s => s.GetProperty( "text" ).GetString()! ).ToArray();
         int rule = Array.IndexOf( slots, "disclosure.clock.rule.v7" );
         int firstUse = Array.FindIndex( texts, t => t.Contains( "median rule", StringComparison.Ordinal ) && !t.StartsWith( "The median rule:", StringComparison.Ordinal ) );
         Assert.True( rule >= 0 && firstUse > rule, $"rule at {rule}, first use at {firstUse}" );
         Assert.Equal( $"The median rule: a pass is off when the median, over its engine CPUs or over its client CPUs, of each CPU's median MHz is more than {tolerance}% from the pinned {pinned} MHz.", texts[rule] );
         Assert.Single( texts, t => t.StartsWith( "The median rule:", StringComparison.Ordinal ) );
         Assert.Contains( texts[rule], two.Md );
      } );
   }

   /// <summary>
   /// Reason 5: the sentence about where the engines ran says "container engines" and names them with their CPUs, and names the embedded engines with theirs, from each run's own record of
   /// where each engine ran; no sentence says every engine ran on the engine CPUs.
   /// </summary>
   /// <returns>A task.</returns>
   [Fact]
   public async Task EngineCpus_AreStatedPerHosting_ContainerEnginesAndEmbeddedEngines()
   {
      await ConsolidateHarness.Timed( () =>
      {
         ConsolidateOutcome two = _fixture.Two;
         JsonElement engines = JsonDocument.Parse( File.ReadAllText( Path.Combine( _fixture.Results, DryFixFixture.STAND_IN[0], "results.json" ) ) ).RootElement.GetProperty( "conditions" ).GetProperty( "engines" );
         string[] container = engines.EnumerateArray().Where( e => e.GetProperty( "hosting" ).GetString() == "compose" ).Select( e => e.GetProperty( "target" ).GetString()! ).ToArray();
         string[] embedded = engines.EnumerateArray().Where( e => e.GetProperty( "hosting" ).GetString() == "embedded" ).Select( e => e.GetProperty( "target" ).GetString()! ).ToArray();
         Assert.Equal( new[] { "duckdb", "sqlitevec" }, embedded.OrderBy( e => e, StringComparer.Ordinal ).ToArray() );
         List<(string Slot, string Text)> sentences = two.Under( "disclosure.engines" );
         Assert.Equal( 2, sentences.Count );
         string containerText = sentences.First( s => s.Text.StartsWith( "The container engines ", StringComparison.Ordinal ) ).Text;
         Assert.EndsWith( " ran on CPUs 2-3,6-7.", containerText );
         Assert.Equal( container.OrderBy( e => e, StringComparer.Ordinal ).ToArray(), Regex.Split( containerText["The container engines ".Length..^" ran on CPUs 2-3,6-7.".Length].Replace( " and ", ", ", StringComparison.Ordinal ), ", " ).OrderBy( e => e, StringComparer.Ordinal ).ToArray() );
         Assert.Equal( "The embedded engines sqlitevec and duckdb ran inside the client process on CPUs 0-1,4-5.", sentences.First( s => s.Text.StartsWith( "The embedded engines ", StringComparison.Ordinal ) ).Text );
         Assert.Equal( "Every claim run used the performance governor, with the client on CPUs 0-1,4-5.", two.Text( "disclosure.governor" ) );
         Assert.DoesNotContain( "engines on CPUs", string.Join( "\n", two.Root.GetProperty( "sentences" ).EnumerateArray().Select( s => s.GetProperty( "text" ).GetString() ) ) );
      } );
   }

   /// <summary>
   /// The observer that ran beside v7 is cited too: given its summary as well as v8's, the report writes the observer, clock and timer sentences of both sessions, copies the newest
   /// summary to observer/summary.json and the older to observer/summary-v7.json, and refuses a summary that covers none of the claim runs.
   /// </summary>
   /// <returns>A task.</returns>
   [Fact]
   public async Task Observer_BesideV7_IsCitedAsWellAsV8()
   {
      await ConsolidateHarness.Timed( () =>
      {
         string v8 = ObserverSummary( "obs-v8.json", DryFixFixture.STAND_IN, 0.123 );
         string v7 = ObserverSummary( "obs-v7.json", ConsolidateRealRunsTests.V7, 0.081 );
         string none = ObserverSummary( "obs-none.json", new[] { "20260101-000000-eshoponweb" }, 0.1 );
         string output = Path.Combine( _scratch, "out-observer" );
         ConsolidateOutcome both = DryFixFixture.Run( _fixture.Results, output, "--session", "v7=" + string.Join( ",", ConsolidateRealRunsTests.V7 ), "--session", "v8=" + string.Join( ",", DryFixFixture.STAND_IN ), "--observer", v8, "--observer", v7 );
         Assert.Equal( 0, both.Exit );
         Assert.Equal( "An observer process ran beside the v8 runs; its own CPU, at most 0.123 CPUs in a pass, counts as outside load, and its summary is observer/summary.json beside this report.", both.Text( "disclosure.observer" ) );
         Assert.Equal( "An observer process ran beside the v7 runs; its own CPU, at most 0.081 CPUs in a pass, counts as outside load, and its summary is observer/summary-v7.json beside this report.", both.Text( "disclosure.observer.v7" ) );
         Assert.NotNull( both.Text( "disclosure.observer.clock.v7" ) );
         Assert.NotNull( both.Text( "disclosure.timers.v7" ) );
         Assert.Equal( File.ReadAllText( v8 ), File.ReadAllText( Path.Combine( output, "observer", "summary.json" ) ) );
         Assert.Equal( File.ReadAllText( v7 ), File.ReadAllText( Path.Combine( output, "observer", "summary-v7.json" ) ) );
         Assert.Single( both.Root.GetProperty( "observerOthers" ).EnumerateArray() );
         Assert.Equal( 64, both.Root.GetProperty( "audit" ).GetProperty( "observerSha256Others" ).GetProperty( "v7" ).GetString()!.Length );
         string[] args = { "--session", "v7=" + string.Join( ",", ConsolidateRealRunsTests.V7 ), "--session", "v8=" + string.Join( ",", DryFixFixture.STAND_IN ), "--observer", v8, "--observer", none };
         ConsolidateOutcomeOrLog refused = Try( _fixture.Results, "out-observer-none", ConsolidateHarness.RepoRoot(), args );
         Assert.Equal( 1, refused.Exit );
         Assert.Contains( "covers no run of any claim session", refused.Log );
      } );
   }

   /// <summary>
   /// Fix-with: the durability texts that cite a measurement or a reading that no saved file holds are named in the one sentence that says so, and the sentence is made from the classes,
   /// not from the word "measured": Typesense (a flag page read on 2026-10-04) is named though its text does not say measured.
   /// </summary>
   /// <returns>A task.</returns>
   [Fact]
   public async Task NoSavedLog_NamesTypesense_AndAnyEngineWhoseClassesSayItCitesAReading()
   {
      await ConsolidateHarness.Timed( () =>
      {
         string none = _fixture.Two.Text( "disclosure.durability.nologs" ) ?? string.Empty;
         Assert.Contains( "typesense", none );
         Assert.DoesNotContain( "mariadb", none );
         string typesense = Raw( DryFixFixture.STAND_IN[0], "typesense", "durability", -1 );
         Assert.DoesNotContain( "measured", typesense, StringComparison.OrdinalIgnoreCase );
         Assert.Contains( "read from the running container's brpc /flags page on 2026-10-04", typesense );

         using var bench = new SyntheticBench();
         string runs = bench.WriteSession( "2026-10-06", 701, _ => new[] { SyntheticBench.Target( "alpha", 1.0, 1000, 4000 ), SyntheticBench.Target( "beta", 2.0, 500, 2000 ) } );
         WriteSheet( bench, new JsonObject
         {
            ["policy"] = new JsonObject { ["durability"] = "refuse", ["index"] = "label", ["notes"] = "label" },
            ["texts"] = new JsonArray( Entry( "alpha", "durability", "alpha fsyncs every commit.", "unverified", cites: true ), Entry( "beta", "durability", "beta fsyncs every commit.", "unverified", cites: false ) ),
         } );
         ( int exit, JsonElement? json, _ ) = bench.Consolidate( "--session", "v7=" + runs );
         Assert.True( exit == 0, bench.LogText() );
         Assert.Equal( "No log is saved for the measurements cited in the durability texts of alpha.", json!.Value.GetProperty( "sentences" ).EnumerateArray().First( s => s.GetProperty( "slot" ).GetString() == "disclosure.durability.nologs" ).GetProperty( "text" ).GetString() );
      } );
   }

   /// <summary>
   /// Fix-with: the claim ratio is floored exactly on the two numbers as stored. A pair whose ratio is exactly the threshold in decimal but a hair below it in binary stays unordered,
   /// where 10000.0 x a / b in doubles rounded it up onto the line; a move is rounded up exactly the same way.
   /// </summary>
   /// <returns>A task.</returns>
   [Fact]
   public async Task ClaimRatio_IsFlooredExactly_SoRoundingNeverCreatesAClaim()
   {
      await ConsolidateHarness.Timed( () =>
      {
         Assert.Equal( 13999, (int)ConsolidateHarness.Call( "RatioBp", 1.4, 1.0 )! );
         Assert.Equal( 13999, (int)ConsolidateHarness.Call( "RatioBp", 0.7, 0.5 )! );
         Assert.Equal( 14000, (int)ConsolidateHarness.Call( "RatioBp", 14.0, 10.0 )! );
         Assert.Equal( 15000, (int)ConsolidateHarness.Call( "RatioBp", 3.0, 2.0 )! );
         Assert.Equal( 13500, (int)ConsolidateHarness.Call( "RatioBp", 1.35, 1.0 )! );
         Assert.Equal( 4000, (int)ConsolidateHarness.Call( "MoveBp", 1.4, 1.0 )! );
         Assert.Equal( 5000, (int)ConsolidateHarness.Call( "MoveBp", 1.5, 1.0 )! );
         Assert.Throws<ArgumentOutOfRangeException>( () => ConsolidateHarness.Call( "RatioBp", 1.0, 0.0 ) );
      } );
   }

   /// <summary>
   /// Fix-with: the 1e-5 tie quote is followed by a sentence from the saved check of this data (no pair of rows within the tolerance, no query with a row the rule would count), read
   /// again here from the saved JSON; and the clauses about sql-native and qdrant-native, which are not targets of this report, are not printed. A check made on other data gives no such sentence.
   /// </summary>
   /// <returns>A task.</returns>
   [Fact]
   public async Task TieQuote_IsFollowedByTheSavedCheck_AndClausesAboutAbsentTargetsAreNotPrinted()
   {
      await ConsolidateHarness.Timed( () =>
      {
         ConsolidateOutcome two = _fixture.Two;
         using JsonDocument check = JsonDocument.Parse( File.ReadAllText( Path.Combine( ConsolidateHarness.RepoRoot(), "design", "bench-inputs", "tie-check-eshoponweb-2026-10-07.json" ) ) );
         JsonElement c = check.RootElement;
         Assert.Equal( 524, c.GetProperty( "rows" ).GetInt32() );
         Assert.Equal( $"A saved check of this data found {c.GetProperty( "duplicatePairs" ).GetInt32()} pairs of rows within 1e-5 of identical, and {c.GetProperty( "queriesWithRowsOutsideTopKWithinEpsilon" ).GetInt32()} of {c.GetProperty( "queries" ).GetInt32()} queries with a row outside the exact top {c.GetProperty( "top" ).GetInt32()} that the tie rule would count.", two.Text( "method.tie" ) );
         string[] texts = two.Root.GetProperty( "sentences" ).EnumerateArray().Select( s => s.GetProperty( "text" ).GetString()! ).ToArray();
         int tie = Array.IndexOf( texts, two.Text( "method.tie" ) );
         Assert.Contains( "(within 1e-5) also counts", texts[tie - 1] );
         Assert.DoesNotContain( texts, t => t.Contains( "native comparison targets", StringComparison.Ordinal ) );
         Assert.Equal( 2, two.Under( "method" ).Count( s => s.Slot.Contains( ".dropped.", StringComparison.Ordinal ) ) );
         Assert.All( two.Under( "method" ).Where( s => s.Slot.Contains( ".dropped.", StringComparison.Ordinal ) ), s => Assert.Equal( "A clause of this note names sql-native and qdrant-native, which are not targets of this report, so it is not printed.", s.Text ) );

         string repo = CopyRepo( "repo-tie" );
         string file = Path.Combine( repo, "design", "bench-inputs", "tie-check-eshoponweb-2026-10-07.json" );
         File.WriteAllText( file, File.ReadAllText( file ).Replace( "\"rows\": 524", "\"rows\": 523", StringComparison.Ordinal ) );
         ConsolidateOutcomeOrLog other = Try( _fixture.Results, "out-tie", repo );
         Assert.Equal( 0, other.Exit );
         string none = other.Root!.Value.GetProperty( "sentences" ).EnumerateArray().First( s => s.GetProperty( "slot" ).GetString() == "method.tie" ).GetProperty( "text" ).GetString()!;
         Assert.Equal( "No saved check of the tie rule matches this data, so the reason given for the rule is not shown to apply to it.", none );
      } );
   }

   /// <summary>
   /// The recorded durability text of an engine is printed clause by clause and the clause a file contradicts is not printed: Weaviate's text says its compose file sets no persistence
   /// variable, and the one sentence that says it is wrong is generated from the file; the false clause appears once, as the words the sentence quotes.
   /// </summary>
   /// <returns>A task.</returns>
   [Fact]
   public async Task Weaviate_FalseClause_IsShownCorrectedOnce_NotQuotedThenCorrected()
   {
      await ConsolidateHarness.Timed( () =>
      {
         ConsolidateOutcome two = _fixture.Two;
         Assert.Single( Regex.Matches( two.Md, "sets no persistence variable" ) );
         Assert.Equal( "The recorded weaviate text says 'sets no persistence variable'; deploy/engines/weaviate.compose.yaml sets PERSISTENCE_DATA_PATH. That clause is not printed.", two.Text( "disclosure.durability.weaviate.correction" ) );
         JsonElement dropped = two.Root.GetProperty( "recordedTexts" ).EnumerateArray().First( t => t.GetProperty( "target" ).GetString() == "weaviate" && t.GetProperty( "field" ).GetString() == "durability" )
            .GetProperty( "clauses" ).EnumerateArray().First( c => !c.GetProperty( "printed" ).GetBoolean() );
         Assert.Equal( "contradicted", dropped.GetProperty( "notPrinted" ).GetString() );
         Assert.DoesNotContain( "persistence variable", dropped.GetProperty( "text" ).GetString()! );
      } );
   }

   /// <summary>
   /// The list is checked before it is used: a span with the wrong shape, a class with no basis, an unverified span with one, a basis that does not resolve, a missing list and a
   /// policy that is neither refuse nor label each stop the report and say what is wrong.
   /// </summary>
   /// <returns>A task.</returns>
   [Fact]
   public async Task TheList_IsCheckedBeforeUse_AndEveryMistakeStopsTheReport()
   {
      await ConsolidateHarness.Timed( () =>
      {
         ( string Label, Action<JsonObject> Damage, string Expected )[] cases =
         {
            ( "read-back without a basis", s => s["class"] = "read-back", "is read-back with 0 basis source(s)" ),
            ( "unverified with a basis", s => s["basis"] = new JsonArray( "src/Alpha.cs#using" ), "is unverified with 1 basis source(s)" ),
            ( "unknown class", s => s["class"] = "guessed", "has class 'guessed'" ),
            ( "text and pattern", s => s["pattern"] = "x", "needs exactly one of text and pattern" ),
            ( "unknown field", s => s["why"] = "because", "unknown field 'why'" ),
            ( "basis that does not resolve", s => { s["class"] = "documented"; s["basis"] = new JsonArray( "src/AlphaSink.cs#this token is nowhere" ); }, "does not resolve" ),
         };
         foreach( ( string label, Action<JsonObject> damage, string expected ) in cases )
         {
            using var bench = new SyntheticBench();
            string runs = bench.WriteSession( "2026-10-06", 701, _ => new[] { SyntheticBench.Target( "alpha", 1.0, 1000, 4000 ) } );
            JsonObject entry = Entry( "alpha", "durability", "alpha fsyncs every commit.", "unverified", cites: false );
            damage( entry["spans"]!.AsArray()[0]!.AsObject() );
            WriteSheet( bench, new JsonObject { ["policy"] = new JsonObject { ["durability"] = "refuse", ["index"] = "label", ["notes"] = "label" }, ["texts"] = new JsonArray( entry ) } );
            Assert.Equal( 1, bench.Consolidate( "--session", "v7=" + runs ).Exit );
            Assert.True( bench.LogText().Contains( expected, StringComparison.Ordinal ), $"{label}: {bench.LogText()}" );
         }

         using var missing = new SyntheticBench();
         string session = missing.WriteSession( "2026-10-06", 701, _ => new[] { SyntheticBench.Target( "alpha", 1.0, 1000, 4000 ) } );
         File.Delete( Path.Combine( missing.Repo, "deploy", "bench", "recorded-text-classes.json" ) );
         Assert.Equal( 1, missing.Consolidate( "--session", "v7=" + session ).Exit );
         Assert.Contains( "recorded-text classes", missing.LogText() );

         using var badPolicy = new SyntheticBench();
         string again = badPolicy.WriteSession( "2026-10-06", 701, _ => new[] { SyntheticBench.Target( "alpha", 1.0, 1000, 4000 ) } );
         WriteSheet( badPolicy, new JsonObject { ["policy"] = new JsonObject { ["durability"] = "ignore" } } );
         Assert.Equal( 1, badPolicy.Consolidate( "--session", "v7=" + again ).Exit );
         Assert.Contains( "must be refuse or label", badPolicy.LogText() );
      } );
   }

   /// <summary>
   /// The list in the repository is strict where the sinks are frozen: a text of an engine that no entry covers stops the report; every class is used; the files it names exist.
   /// </summary>
   /// <returns>A task.</returns>
   [Fact]
   public async Task TheRealList_RefusesAnUnlistedEngineText_AndUsesEveryClass()
   {
      await ConsolidateHarness.Timed( () =>
      {
         string path = Path.Combine( ConsolidateHarness.RepoRoot(), "deploy", "bench", "recorded-text-classes.json" );
         using JsonDocument doc = JsonDocument.Parse( File.ReadAllText( path ) );
         JsonElement root = doc.RootElement;
         Assert.Equal( "refuse", root.GetProperty( "policy" ).GetProperty( "durability" ).GetString() );
         Assert.Equal( "refuse", root.GetProperty( "policy" ).GetProperty( "index" ).GetString() );
         var classes = root.GetProperty( "texts" ).EnumerateArray().SelectMany( t => t.GetProperty( "spans" ).EnumerateArray() ).Concat( root.GetProperty( "notes" ).EnumerateArray().SelectMany( t => t.GetProperty( "spans" ).EnumerateArray() ) )
            .Select( s => s.GetProperty( "class" ).GetString()! ).ToList();
         Assert.Equal( new[] { "documented", "read-back", "unverified" }, classes.Distinct().OrderBy( c => c, StringComparer.Ordinal ).ToArray() );
         foreach( JsonProperty evidence in root.GetProperty( "evidence" ).EnumerateObject() )
         {
            Assert.True( File.Exists( Path.Combine( ConsolidateHarness.RepoRoot(), evidence.Value.GetString()! ) ), evidence.Value.GetString() );
         }

         Assert.Equal( 19, root.GetProperty( "texts" ).EnumerateArray().Where( t => t.GetProperty( "field" ).GetString() == "durability" ).Select( t => t.GetProperty( "target" ).GetString() ).Distinct().Count() );
         Assert.Equal( 19, root.GetProperty( "texts" ).EnumerateArray().Where( t => t.GetProperty( "field" ).GetString() == "index" ).Select( t => t.GetProperty( "target" ).GetString() ).Distinct().Count() );
      } );
   }

   #endregion Public Methods

   #region Private Methods

   /// <summary>The result of a consolidation that may have been refused.</summary>
   /// <param name="Exit">Exit code.</param>
   /// <param name="Log">The log.</param>
   /// <param name="Root">consolidated.json, or null when nothing was written.</param>
   private sealed record ConsolidateOutcomeOrLog( int Exit, string Log, JsonElement? Root );

   /// <summary>
   /// Consolidates the real runs of a results folder with the repository (or a copy of it), keeping the log when the command refuses.
   /// </summary>
   /// <param name="results">The results folder.</param>
   /// <param name="name">The output folder's name.</param>
   /// <param name="repo">The repository root; the real one when null.</param>
   /// <param name="extra">Arguments after the sessions; the stand-in two-session arguments when none.</param>
   /// <returns>The outcome.</returns>
   private ConsolidateOutcomeOrLog Try( string results, string name, string? repo = null, string[]? extra = null )
   {
      string root = repo ?? ConsolidateHarness.RepoRoot();
      string output = Path.Combine( _scratch, name );
      string[] sessions = extra ?? new[] { "--session", "v7=" + string.Join( ",", ConsolidateRealRunsTests.V7 ), "--session", "v8=" + string.Join( ",", DryFixFixture.STAND_IN ) };
      string[] full = sessions.Concat( new[]
      {
         "--basis-session", "v5=" + string.Join( ",", ConsolidateRealRunsTests.V5 ), "--basis-session", "v6=" + string.Join( ",", ConsolidateRealRunsTests.V6 ), "--results-dir", results, "--repo", root,
         "--facts", Path.Combine( root, "src", "GenericVectorBuilder.Bench", "Report", "engine-facts.json" ), "--exclusions", Path.Combine( root, "deploy", "bench", "basis-exclusions.json" ), "--out", output,
      } ).ToArray();
      var result = (string[])ConsolidateHarness.Call( "Consolidate", (object)full )!;
      string json = Path.Combine( output, "consolidated.json" );
      return new ConsolidateOutcomeOrLog( int.Parse( result[0] ), string.Join( Environment.NewLine, result.Skip( 1 ) ), File.Exists( json ) ? JsonDocument.Parse( File.ReadAllText( json ) ).RootElement.Clone() : null );
   }

   /// <summary>
   /// The label sentences the report can print, by constant name.
   /// </summary>
   /// <returns>Name to text.</returns>
   private static Dictionary<string, string> Labels()
   {
      return ( (string[])ConsolidateHarness.Call( "Templates" )! ).Where( t => t.StartsWith( "CLAUSE_", StringComparison.Ordinal ) ).ToDictionary( t => t[..t.IndexOf( '=' )], t => t[( t.IndexOf( '=' ) + 1 )..] );
   }

   /// <summary>
   /// The labels a clause of a class may follow.
   /// </summary>
   /// <param name="clauseClass">The class.</param>
   /// <param name="labels">The label sentences by name.</param>
   /// <returns>The allowed texts.</returns>
   private static string[] AllowedLabels( string clauseClass, Dictionary<string, string> labels )
   {
      return clauseClass switch
      {
         "unverified" => new[] { labels["CLAUSE_UNVERIFIED"], labels["CLAUSE_UNVERIFIED_CITES"], labels["CLAUSE_UNLISTED"] },
         "documented" => new[] { labels["CLAUSE_DOC_PAGE"], labels["CLAUSE_DOC_LOG"], labels["CLAUSE_DOC_CODE"], labels["CLAUSE_DOC_TEST"] },
         "measured" => new[] { labels["CLAUSE_MEASURED"] },
         _ => new[] { labels["CLAUSE_READ_BACK"], labels["CLAUSE_READ_BY_CODE"] },
      };
   }

   /// <summary>
   /// Checks that the clauses of a recorded text are, in order and with only white space between them, the recorded text, and that each has a class and the right amount of basis.
   /// </summary>
   /// <param name="recorded">The recorded text, white space collapsed.</param>
   /// <param name="text">The record.</param>
   /// <param name="where">For messages.</param>
   private static void AssertClausesAreTheText( string recorded, JsonElement text, string where )
   {
      int pos = 0;
      foreach( JsonElement clause in text.GetProperty( "clauses" ).EnumerateArray() )
      {
         string cls = clause.GetProperty( "class" ).GetString()!;
         Assert.Contains( cls, CLASSES );
         Assert.Equal( cls == "unverified", clause.GetProperty( "basis" ).GetArrayLength() == 0 );
         while( pos < recorded.Length && recorded[pos] == ' ' )
         {
            pos++;
         }

         string shown = clause.GetProperty( "text" ).GetString()!;
         bool printed = clause.GetProperty( "printed" ).GetBoolean();
         string expected = printed ? shown : shown.TrimEnd( '.' );
         Assert.True( string.CompareOrdinal( recorded, pos, expected, 0, expected.Length ) == 0, $"{where}: the clause '{expected}' is not at {pos} of the recorded text" );
         pos += clause.GetProperty( "length" ).GetInt32();
      }

      Assert.True( recorded[pos..].Trim().Length == 0, $"{where}: the clauses stop at {pos} of {recorded.Length}" );
   }

   /// <summary>
   /// The recorded text of a target field, or run note, read from the raw results.json of a run.
   /// </summary>
   /// <param name="run">The run folder.</param>
   /// <param name="target">The target, or empty for a note.</param>
   /// <param name="field">durability, index or notes.</param>
   /// <param name="note">The note's position, or -1.</param>
   /// <returns>The text.</returns>
   private string Raw( string run, string target, string field, int note )
   {
      JsonElement root = JsonDocument.Parse( File.ReadAllText( Path.Combine( _fixture.Results, run, "results.json" ) ) ).RootElement;
      return field == "notes" ? root.GetProperty( "notes" )[note].GetString()! : root.GetProperty( "targets" ).EnumerateArray().First( t => t.GetProperty( "name" ).GetString() == target ).GetProperty( field ).GetString()!;
   }

   /// <summary>
   /// The lowest and highest recall at ef 100 per collection size in the saved log, read with an expression of the test's own.
   /// </summary>
   /// <returns>Size to lowest and highest, as the log prints them.</returns>
   private static Dictionary<string, (string Low, string High)> SavedRecall()
   {
      string log = File.ReadAllText( Path.Combine( ConsolidateHarness.RepoRoot(), "design", "bench-inputs", "mariadb-effort-2026-10-07", "run.log" ) );
      return Regex.Matches( log, @"RECALL@10 on (\d+) random 1024-dim vectors, 50 queries: ef 20 [\d.]+, ef 100 ([\d.]+)," ).GroupBy( m => m.Groups[1].Value )
         .ToDictionary( g => g.Key, g => ( g.Select( m => m.Groups[2].Value ).OrderBy( v => double.Parse( v, System.Globalization.CultureInfo.InvariantCulture ) ).First(), g.Select( m => m.Groups[2].Value ).OrderBy( v => double.Parse( v, System.Globalization.CultureInfo.InvariantCulture ) ).Last() ) );
   }

   /// <summary>
   /// Collapses white space the way the audit compares a quote with its field.
   /// </summary>
   /// <param name="text">The text.</param>
   /// <returns>The collapsed text.</returns>
   private static string Collapse( string text )
   {
      return SPACE.Replace( text, " " ).Trim();
   }

   /// <summary>
   /// A copy of the results folder of the shared fixture, so a test can change a run.
   /// </summary>
   /// <param name="name">The copy's name.</param>
   /// <returns>The folder.</returns>
   private string CopyResults( string name )
   {
      string destination = Path.Combine( _scratch, name );
      foreach( string file in Directory.GetFiles( _fixture.Results, "*", SearchOption.AllDirectories ) )
      {
         string target = Path.Combine( destination, Path.GetRelativePath( _fixture.Results, file ) );
         Directory.CreateDirectory( Path.GetDirectoryName( target )! );
         File.Copy( file, target );
      }

      foreach( string folder in Directory.GetDirectories( _fixture.Results ) )
      {
         Directory.CreateDirectory( Path.Combine( destination, Path.GetFileName( folder ) ) );
      }

      return destination;
   }

   /// <summary>
   /// Changes the results.json of one run in a copy of the results folder.
   /// </summary>
   /// <param name="results">The copy.</param>
   /// <param name="folder">The run's folder.</param>
   /// <param name="change">The change.</param>
   private static void Change( string results, string folder, Action<JsonObject> change )
   {
      string file = Path.Combine( results, folder, "results.json" );
      JsonObject run = JsonNode.Parse( File.ReadAllText( file ) )!.AsObject();
      change( run );
      File.WriteAllText( file, run.ToJsonString() );
   }

   /// <summary>
   /// A target of a run object.
   /// </summary>
   /// <param name="run">The run.</param>
   /// <param name="name">The target's name.</param>
   /// <returns>The target.</returns>
   private static JsonObject Target( JsonObject run, string name )
   {
      return run["targets"]!.AsArray().Select( t => t!.AsObject() ).First( t => t["name"]!.GetValue<string>() == name );
   }

   /// <summary>
   /// A copy of the repository's files the consolidation reads, in the scratch folder.
   /// </summary>
   /// <param name="name">The copy's name.</param>
   /// <returns>The repository root of the copy.</returns>
   private string CopyRepo( string name )
   {
      string root = ConsolidateHarness.RepoRoot();
      string destination = Path.Combine( _scratch, name );
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

      return destination;
   }

   /// <summary>
   /// An observer summary of the shape the run tooling writes, covering the given run folders.
   /// </summary>
   /// <param name="name">The file name.</param>
   /// <param name="folders">The run folders.</param>
   /// <param name="cpu">The highest CPU the observer used in a pass.</param>
   /// <returns>The path.</returns>
   private string ObserverSummary( string name, IEnumerable<string> folders, double cpu )
   {
      var runs = new JsonArray( folders.Select( f => (JsonNode)new JsonObject
      {
         ["folder"] = f,
         ["checks"] = new JsonObject
         {
            ["observerCpu"] = new JsonArray( 0.05, cpu ), ["aperf"] = new JsonObject { ["worstDeviationBp"] = 40.0 },
            ["msr620"] = new JsonObject { ["valuesSeen"] = new JsonArray( "1e1e" ) }, ["timers"] = new JsonObject { ["covered"] = true, ["passesWithTimerFired"] = new JsonArray() },
         },
      } ).ToArray() );
      string path = Path.Combine( _scratch, name );
      File.WriteAllText( path, new JsonObject { ["schema"] = "v8-observer-summary-1", ["runs"] = runs }.ToJsonString() );
      return path;
   }

   /// <summary>
   /// A list entry whose one span is the whole text.
   /// </summary>
   /// <param name="target">The target.</param>
   /// <param name="field">The field.</param>
   /// <param name="text">The text.</param>
   /// <param name="cls">The class.</param>
   /// <param name="cites">True when the span cites a measurement.</param>
   /// <returns>The entry.</returns>
   private static JsonObject Entry( string target, string field, string text, string cls, bool cites )
   {
      var span = new JsonObject { ["text"] = text, ["class"] = cls, ["basis"] = new JsonArray() };
      if( cites )
      {
         span["cites"] = "measurement";
      }

      return new JsonObject { ["target"] = target, ["field"] = field, ["spans"] = new JsonArray( span ) };
   }

   /// <summary>
   /// Writes a list into a synthetic bench's repository.
   /// </summary>
   /// <param name="bench">The bench.</param>
   /// <param name="sheet">The list.</param>
   private static void WriteSheet( SyntheticBench bench, JsonObject sheet )
   {
      File.WriteAllText( Path.Combine( bench.Repo, "deploy", "bench", "recorded-text-classes.json" ), sheet.ToJsonString() );
   }

   #endregion Private Methods

   #region IDisposable

   /// <summary>
   /// Removes the scratch folder.
   /// </summary>
   public void Dispose()
   {
      if( Directory.Exists( _scratch ) )
      {
         Directory.Delete( _scratch, recursive: true );
      }
   }

   #endregion IDisposable
}
