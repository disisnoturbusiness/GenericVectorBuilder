using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace GenericVectorBuilder.Engines.Tests.Bench;

/// <summary>
/// The report-layer fixes the v8j verdict (BLOCK) asked for, proven on the real v7 and v8 runs: the clock clause the summary drops is marked on every run page that prints it (false for the v8 runs,
/// whose observer read oracle's pass under the pin, unbacked for the v7 runs), the general rule that every clause the summary drops as unverified has a note on every run that holds it, the drift
/// median taken once over the exact moves, and the corrections list carried into the consolidated file so the summary can draw all of it. Each test fails on the tree the verdict judged.
/// </summary>
public sealed class ConsolidateV8kFixTests : IClassFixture<RealV8Fixture>
{
   #region Data Members

   private const string CLOCK_STATEMENT = "so every CPU's clock is held at its ceiling of 3500 MHz whatever the engine runs";
   private const string NOTES_FILE = "deploy/bench/run-page-notes.json";
   private const string CLASSES_FILE = "deploy/bench/recorded-text-classes.json";
   private const string OBSERVER_DIR = "design/bench-inputs/observer-v8";

   private static readonly Regex WORST_READING = new( @"X02: .*?worst deviation (?<bp>[\d.]+) bp \((?<target>[\w-]+)/(?<pass>\S+) cpu(?<cpu>\d+) (?<mhz>[\d.]+) MHz\)", RegexOptions.CultureInvariant, TimeSpan.FromSeconds( 5 ) );

   private readonly RealV8Fixture _fixture;

   #endregion Data Members

   #region Constructor

   /// <summary>
   /// Takes the shared consolidation.
   /// </summary>
   /// <param name="fixture">The fixture.</param>
   public ConsolidateV8kFixTests( RealV8Fixture fixture )
   {
      _fixture = fixture;
   }

   #endregion Constructor

   #region Public Methods

   /// <summary>
   /// The block reason: the clause "so every CPU's clock is held at its ceiling of 3500 MHz whatever the engine runs" is in the results.md of all six runs, the summary drops it, and the run pages printed
   /// it with no mark. Each of the six runs now has one note for it. The v8 notes are false and quote the run's own observer reading (the worst CPU of the pass, read from the saved analysis); the v7 notes
   /// are unbacked, because no saved reading of those runs is kept.
   /// </summary>
   [Fact]
   public void TheClockClause_HasOneNoteOnEachOfTheSixRuns_FalseForV8AndUnbackedForV7()
   {
      List<JsonElement> notes = NotesOf( CLOCK_STATEMENT ).ToList();
      Assert.Equal( 6, notes.Sum( n => RunsOf( n ).Count ) );

      foreach( string run in ConsolidateRealRunsTests.V7 )
      {
         Assert.Contains( CLOCK_STATEMENT, File.ReadAllText( Path.Combine( ConsolidateHarness.BenchResults(), run, "results.md" ) ) );
         JsonElement note = Assert.Single( notes, n => RunsOf( n ).Contains( run ) );
         Assert.Equal( "unbacked", note.GetProperty( "kind" ).GetString() );
         string text = note.GetProperty( "note" ).GetString()!;
         Assert.Contains( "No saved reading backs the clause for this run", text );
         Assert.Contains( OBSERVER_DIR, text );
         Assert.Contains( note.GetProperty( "sources" ).EnumerateArray(), s => s.GetProperty( "ref" ).GetString()!.StartsWith( CLASSES_FILE + "#", StringComparison.Ordinal ) );
      }

      for( int i = 0; i < RealV8Fixture.V8.Length; i++ )
      {
         string run = RealV8Fixture.V8[i];
         string analysis = $"analysis-{801 + i}.txt";
         Assert.Contains( CLOCK_STATEMENT, File.ReadAllText( Path.Combine( ConsolidateHarness.BenchResults(), run, "results.md" ) ) );
         Match worst = WORST_READING.Match( File.ReadAllText( Path.Combine( ConsolidateHarness.RepoRoot(), OBSERVER_DIR, analysis ) ) );
         Assert.True( worst.Success, $"{analysis} has no X02 worst reading" );
         JsonElement note = Assert.Single( notes, n => RunsOf( n ).Contains( run ) );
         Assert.Equal( "false", note.GetProperty( "kind" ).GetString() );
         string text = note.GetProperty( "note" ).GetString()!;
         Assert.Contains( $"CPU {worst.Groups["cpu"].Value} read {worst.Groups["mhz"].Value} MHz against 3500 MHz", text );
         Assert.Contains( $"{worst.Groups["bp"].Value} basis points under it", text );
         Assert.Contains( $"{worst.Groups["target"].Value}'s {worst.Groups["pass"].Value} pass", text );
         List<string> refs = note.GetProperty( "sources" ).EnumerateArray().Select( s => s.GetProperty( "ref" ).GetString()! ).ToList();
         Assert.Contains( refs, r => r.StartsWith( $"{OBSERVER_DIR}/{analysis}#", StringComparison.Ordinal ) );
         Assert.Contains( refs, r => r.StartsWith( CLASSES_FILE + "#", StringComparison.Ordinal ) );
      }
   }

   /// <summary>
   /// The general rule behind the block (the same pattern as v8i's B3): a clause the summary does not print because it is unverified (a clause with a drop code in the classes list, and a clause a
   /// repository file contradicts) and that a run's results.md holds has a note naming that run whose statement lies inside the clause. A new dropped clause, or a new run that holds one, with no note fails here.
   /// The clauses dropped only because their engine is not in the report ("absent-targets") are not claims, so they are not in this rule.
   /// </summary>
   [Fact]
   public void EveryClauseTheSummaryDropsAsUnverified_HasANoteOnEveryRunThatHoldsIt()
   {
      List<( string Label, Regex Clause )> dropped = DroppedClauses();
      Assert.Contains( dropped, d => d.Label == "clock-held" );
      Assert.Contains( dropped, d => d.Label == "mariadb-recall" );
      Assert.Contains( dropped, d => d.Label == "contradicted" );
      List<JsonElement> notes = Notes().ToList();
      int held = 0;

      foreach( string run in Directory.GetDirectories( ConsolidateHarness.BenchResults(), "2026*-eshoponweb" ).Select( Path.GetFileName ).Cast<string>() )
      {
         string file = Path.Combine( ConsolidateHarness.BenchResults(), run, "results.md" );
         string md = File.Exists( file ) ? File.ReadAllText( file ) : string.Empty;
         foreach( ( string label, Regex clause ) in dropped )
         {
            foreach( Match place in clause.Matches( md ) )
            {
               held++;
               Assert.True( notes.Any( n => RunsOf( n ).Contains( run ) && place.Value.Contains( n.GetProperty( "statement" ).GetString()!, StringComparison.Ordinal ) ),
                  $"{run}: the report holds the {label} clause \"{Shorten( place.Value )}\" that the summary drops, and no note of {NOTES_FILE} names this run for a statement inside it" );
            }
         }
      }

      Assert.True( held >= 6 + 12 + 12, $"only {held} places of dropped clauses were found; the rule is not looking at the reports" );
   }

   /// <summary>
   /// Fix-with 2, the drift median: "1.44%" was the median of moves that were each rounded to a basis point, rounded again (the middle two of 70 moves are 140 and 147 bp, whose mean is 143.5); the exact
   /// median of the 70 session-median moves is 143.3 bp. The figure is now taken over the exact moves and rounded once, so the sentence says 1.43%, and the test recomputes it from the runs in the file.
   /// </summary>
   [Fact]
   public void TheDriftMedian_IsTakenOverTheExactMoves_AndRoundedOnce()
   {
      var exact = new List<double>();
      foreach( JsonElement metric in _fixture.Both.Root.GetProperty( "metrics" ).EnumerateArray() )
      {
         foreach( JsonElement row in metric.GetProperty( "rows" ).EnumerateArray().Where( r => r.GetProperty( "status" ).GetString() == "ranked" ) )
         {
            double v7 = Median( row.GetProperty( "perSession" ).GetProperty( "v7" ).GetProperty( "runs" ) );
            double v8 = Median( row.GetProperty( "perSession" ).GetProperty( "v8" ).GetProperty( "runs" ) );
            exact.Add( Math.Abs( ( v8 / v7 - 1 ) * 10000 ) );
         }
      }

      exact.Sort();
      double median = ( exact[exact.Count / 2 - 1] + exact[exact.Count / 2] ) / 2;
      Assert.Equal( 70, exact.Count );
      Assert.InRange( median, 143.2, 143.45 );
      Assert.Equal( 143, _fixture.Both.Root.GetProperty( "drift" ).GetProperty( "medianAbsMoveBp" ).GetInt32() );
      Assert.Equal( "From v7 to v8, a ranked figure's session median moved 1.43% in the middle case and 4.66% at most (opensearch, exact p50).", _fixture.Both.Text( "drift.median" ) );
   }

   /// <summary>
   /// Fix-with 4: the consolidated file carries the corrections list ("runPageNotes"), in list order with the sources of each. A note keeps only the runs the set uses (the page refuses a correction that names
   /// a run the set does not link to), a note that names none of them is left out, and the six older runs the set leaves out of its basis are the ones the filter removes. The summary page draws its table of
   /// all corrections from the field.
   /// </summary>
   [Fact]
   public void TheConsolidatedFile_CarriesTheRunPageNotes_ForTheRunsTheSetUses()
   {
      HashSet<string> used = _fixture.Both.Root.GetProperty( "sessions" ).EnumerateArray().SelectMany( s => s.GetProperty( "runs" ).EnumerateArray() ).Select( r => r.GetProperty( "folder" ).GetString()! )
         .Concat( _fixture.Both.Root.GetProperty( "basis" ).GetProperty( "runs" ).EnumerateArray().Select( r => r.GetProperty( "folder" ).GetString()! ) ).ToHashSet( StringComparer.Ordinal );
      Assert.Equal( 12, used.Count );
      List<JsonElement> list = Notes().ToList();
      Assert.True( list.Count >= 74, "the list should hold the 68 notes of freeze 4 and the six clock notes" );
      Assert.Contains( list, n => RunsOf( n ).Any( r => !used.Contains( r ) ) );
      List<( JsonElement Note, List<string> Runs )> expected = list.Select( n => ( Note: n, Runs: RunsOf( n ).Where( used.Contains ).ToList() ) ).Where( e => e.Runs.Count > 0 ).ToList();
      List<JsonElement> emitted = _fixture.Both.Root.GetProperty( "runPageNotes" ).EnumerateArray().ToList();
      Assert.Equal( expected.Count, emitted.Count );
      Assert.True( emitted.Count >= 70, "the set's runs hold nearly every note of the list" );

      for( int i = 0; i < expected.Count; i++ )
      {
         foreach( string field in new[] { "target", "field", "statement", "kind", "note" } )
         {
            Assert.Equal( Text( expected[i].Note, field ), Text( emitted[i], field ) );
         }

         Assert.Equal( expected[i].Runs, RunsOf( emitted[i] ) );
         Assert.Equal( expected[i].Note.GetProperty( "sources" ).GetRawText().Replace( " ", string.Empty ), emitted[i].GetProperty( "sources" ).GetRawText().Replace( " ", string.Empty ) );
      }
   }

   /// <summary>
   /// Fix-with 4, a note the page cannot draw: the page refuses a correction with no source, so the consolidation refuses a list that holds one, naming the entry, and writes nothing.
   /// </summary>
   [Fact]
   public void AListNoteWithNoSource_StopsTheConsolidation()
   {
      string repo = _fixture.CopyRepo( "no-source" );
      string file = Path.Combine( repo, "deploy", "bench", "run-page-notes.json" );
      JsonNode root = JsonNode.Parse( File.ReadAllText( file ) )!;
      root["notes"]!.AsArray()[0]!["sources"] = new JsonArray();
      File.WriteAllText( file, root.ToJsonString() );

      ( int exit, string log, ConsolidateOutcome? written ) = _fixture.AttemptInRepo( "no-source", _ => { }, repo, "--observer", _fixture.ObserverV8, "--observer", _fixture.ObserverV7 );

      Assert.Equal( 1, exit );
      Assert.Null( written );
      Assert.Contains( "notes[0].sources", log );
   }

   /// <summary>
   /// The files of these fixes hold no em or en dash and no name of the former employer.
   /// </summary>
   [Fact]
   public void TheFilesOfTheseFixes_HoldNoDashesOrForbiddenNames()
   {
      string root = ConsolidateHarness.RepoRoot();
      string[] files =
      {
         "deploy/bench/make-run-page-notes.py", NOTES_FILE, "src/GenericVectorBuilder.Bench/Stats/ConsolidateNotes.cs", "src/GenericVectorBuilder.Bench/Stats/ClaimDrift.cs",
         "src/GenericVectorBuilder.Web/BenchPages/BenchRunNotes.cs", "src/GenericVectorBuilder.Web/BenchPages/BenchLegends.cs", "src/GenericVectorBuilder.Web/BenchPages/BenchRunTable.cs",
         "tests/GenericVectorBuilder.Engines.Tests/Bench/ConsolidateV8kFixTests.cs", "tests/GenericVectorBuilder.Tests/Unit/BenchResultsV8kFixTests.cs",
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
   /// The clauses the summary does not print as unverified claims: every span of the classes list that has a drop code, and every span a repository file contradicts. Each as a label and a regex that finds it in a report.
   /// </summary>
   /// <returns>The clauses.</returns>
   private static List<( string Label, Regex Clause )> DroppedClauses()
   {
      using JsonDocument doc = JsonDocument.Parse( File.ReadAllText( Path.Combine( ConsolidateHarness.RepoRoot(), CLASSES_FILE ) ) );
      var found = new List<( string, Regex )>();
      foreach( JsonElement span in doc.RootElement.GetProperty( "texts" ).EnumerateArray().Concat( doc.RootElement.GetProperty( "notes" ).EnumerateArray() ).SelectMany( t => t.GetProperty( "spans" ).EnumerateArray() ) )
      {
         if( span.GetProperty( "class" ).GetString() != "unverified" )
         {
            continue;
         }

         string? label = span.TryGetProperty( "drop", out JsonElement drop ) ? drop.GetString() : span.TryGetProperty( "contradictedBy", out _ ) ? "contradicted" : null;
         if( label != null )
         {
            string pattern = span.TryGetProperty( "pattern", out JsonElement p ) ? p.GetString()! : Regex.Escape( span.GetProperty( "text" ).GetString()! );
            found.Add( ( label, new Regex( pattern, RegexOptions.Singleline | RegexOptions.CultureInvariant, TimeSpan.FromSeconds( 5 ) ) ) );
         }
      }

      return found;
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
   /// A string property, or empty.
   /// </summary>
   /// <param name="element">The object.</param>
   /// <param name="name">The property.</param>
   /// <returns>The text.</returns>
   private static string Text( JsonElement element, string name )
   {
      return element.TryGetProperty( name, out JsonElement value ) && value.ValueKind == JsonValueKind.String ? value.GetString()! : string.Empty;
   }

   /// <summary>
   /// The median of an array of numbers.
   /// </summary>
   /// <param name="runs">The numbers.</param>
   /// <returns>The median.</returns>
   private static double Median( JsonElement runs )
   {
      double[] sorted = runs.EnumerateArray().Select( r => r.GetDouble() ).OrderBy( r => r ).ToArray();
      return sorted.Length % 2 == 1 ? sorted[sorted.Length / 2] : ( sorted[sorted.Length / 2 - 1] + sorted[sorted.Length / 2] ) / 2;
   }

   /// <summary>
   /// A clause cut to a length a failure message can carry.
   /// </summary>
   /// <param name="text">The clause.</param>
   /// <returns>At most 90 characters.</returns>
   private static string Shorten( string text )
   {
      return text.Length <= 90 ? text : text[..90] + "...";
   }

   #endregion Private Methods
}
