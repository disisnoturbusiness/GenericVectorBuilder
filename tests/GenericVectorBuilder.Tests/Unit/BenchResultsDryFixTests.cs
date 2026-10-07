using System.Net;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using GenericVectorBuilder.Web.BenchPages;
using GenericVectorBuilder.Web.Endpoints;

namespace GenericVectorBuilder.Tests.Unit;

/// <summary>
/// The pages the dry check of the v8 pipeline blocked, proven through the rendered HTML only: the run pages describe the warm-up from the run's own
/// notes and never print a count as "searches"; a run page says what its notes record about the clock; the reuse sentence of a set is on the pages of the
/// runs of the session it is about and on no other, worded for the part those runs play; the facts and costs table shows the search effort column; and
/// the withdrawn state of the 4 Oct 2026 set renders as a record kept under a banner and not as a result.
/// Why the HTML and not the model: the dry check found these on the served pages, so each test reads what a reader sees, and each strings a test uses
/// that is new in this change is written out here and not read from <see cref="BenchLegends"/>, so the same tests can be run against the tree before
/// the change and shown to fail there.
/// The real notes of a v5, a v6 and a v7 run and the real files of the 4 Oct set come from the BenchResultsFixtures folder (see
/// <see cref="BenchResultsFixtureFiles"/>).
/// </summary>
public class BenchResultsDryFixTests : IDisposable
{
   #region Data Members

   private const string V5_RUN = "20261005-023459-eshoponweb";
   private const string V6_RUN = "20261005-073329-eshoponweb";
   private const string V7_RUN = "20261006-130619-eshoponweb";
   private const string V8_RUN = "20261008-130000-eshoponweb";
   private const string SET = "published-2026-10-07";
   private const string WITHDRAWN = "withdrawn-2026-10-04";
   private const string REUSE_V5 = "The runs of v5 were also used by the blocked set blocked-2026-10-05-v5.";
   private const string REUSE_V6 = "The runs of v6 were also used by the blocked set blocked-2026-10-05-v6.";
   private const string REUSE_V7 = "The runs of v7 were also used by a report that was not published.";
   private const string RECORD_HEADING = "Report text as it was written, kept for the record";

   private readonly string _root;

   #endregion Data Members

   #region Constructor

   /// <summary>
   /// Creates an empty results root for this test under the test output folder.
   /// </summary>
   public BenchResultsDryFixTests()
   {
      _root = Path.Combine( AppContext.BaseDirectory, "bench-page-tests", Guid.NewGuid().ToString( "N" ) );
      Directory.CreateDirectory( _root );
   }

   #endregion Constructor

   #region Public Methods

   /// <summary>
   /// The run page of a v7 run and of a v6 run describes the warm-up in the run's own words (at least a time and at least a count), and prints no
   /// "Warm-up searches 20" line, which says a method the run did not use. The dry check found that line on every v6, v7 and v8 run page.
   /// </summary>
   [Theory]
   [InlineData( "v7" )]
   [InlineData( "v6" )]
   public void ARunPage_DescribesTheTimedWarmupFromTheRunsOwnNotes( string session )
   {
      string text = Visible( RunPage( V7_RUN, BenchResultsFixtureFiles.LegacyRun( session ) ) );

      Assert.Contains( "Warm-up, as this run's notes record it: Warm-up and settle check, untimed, right before every timed pass", text );
      Assert.Contains( "for at least 15 s and at least 20 searches (at most 120 s)", text );
      Assert.DoesNotContain( "Warm-up searches", text );
      Assert.DoesNotMatch( new Regex( @"Warm-up searches\s*\d" ), text );
      Assert.DoesNotContain( "The trial's figure", text );
   }

   /// <summary>
   /// A v5 run's warm-up really was a count of searches, and its page says so by printing the run's own note, not a label of the page's own.
   /// </summary>
   [Fact]
   public void ARunPage_OfAV5Run_PrintsItsOwnCountedWarmupNote()
   {
      string text = Visible( RunPage( V5_RUN, BenchResultsFixtureFiles.LegacyRun( "v5" ) ) );

      Assert.Contains( "Warm-up, as this run's notes record it: Warm-up: every timed pass started with its own untimed warm-up of 20 searches", text );
      Assert.Contains( "stopped early after 60 s", text );
      Assert.DoesNotContain( "Warm-up searches 20", text );
   }

   /// <summary>
   /// A run that recorded a warmupSearches field and no note on the warm-up gets the value beside the statement that no note describes the method: the
   /// page never turns the field into a count of searches.
   /// </summary>
   [Fact]
   public void ARunPage_WithOnlyTheCountField_SaysNoNoteDescribesTheMethod()
   {
      string json = Edit( BenchResultsFixtures.RUN_RESULTS_V8_JSON, root => root.AsObject().Remove( "notes" ) );

      string text = Visible( RunPage( V7_RUN, json ) );

      Assert.Contains( "Warm-up: the run recorded the field warmupSearches with the value 20 and no note that describes the method.", text );
      Assert.DoesNotContain( "Warm-up searches", text );
   }

   /// <summary>
   /// A run that recorded neither a note nor a field prints no warm-up line, and a note's own text is encoded and cut when it is very long.
   /// </summary>
   [Fact]
   public void ARunPage_PrintsNoWarmupLineWithoutARecord_AndEncodesAndCapsTheNote()
   {
      string none = Edit( BenchResultsFixtures.RUN_RESULTS_V8_JSON, root => root["conditions"]!.AsObject().Remove( "warmupSearches" ) );
      Assert.DoesNotContain( "data-line=\"warmup\"", RunPage( V7_RUN, none ) );

      string loud = Edit( BenchResultsFixtures.RUN_RESULTS_V8_JSON, root => root["notes"] = new JsonArray( "Warm-up of <b>x</b> & more. " + new string( 'q', 50 ) + ". Second sentence." ) );
      string html = RunPage( V7_RUN, loud );
      Assert.Contains( "Warm-up of &lt;b&gt;x&lt;/b&gt; &amp; more.", html );
      Assert.DoesNotContain( "<b>x</b>", html );
      Assert.DoesNotContain( "Second sentence", html );

      string longNote = Edit( BenchResultsFixtures.RUN_RESULTS_V8_JSON, root => root["notes"] = new JsonArray( "Warm-up " + new string( 'w', 2000 ) + "." ) );
      Match line = Regex.Match( RunPage( V7_RUN, longNote ), "data-line=\"warmup\">(.*?)</p>" );
      Assert.True( line.Success );
      Assert.EndsWith( "...", line.Groups[1].Value );
      Assert.True( line.Groups[1].Value.Length < 800, $"the warm-up line is {line.Groups[1].Value.Length} characters long" );
   }

   /// <summary>
   /// A run measured with the clock pinned says so in its notes' own words on its page; a run measured with machine control on and no pin in its notes says
   /// that none is recorded; a run with a clock block prints the block and neither line; a run with no machine control says nothing about its clock.
   /// The dry check found v5, v6 and v7 run pages reading alike.
   /// </summary>
   [Fact]
   public void ARunPage_SaysWhatItsNotesRecordAboutTheClock()
   {
      string pinned = Visible( RunPage( V7_RUN, BenchResultsFixtureFiles.LegacyRun( "v7" ) ) );
      Assert.Contains( "Clock, as this run's notes record it: CPU clock pinned for the run: turbo off (intel_pstate/no_turbo 0 -> 1), so every CPU's clock is held at its ceiling of 3500 MHz whatever the engine runs", pinned );
      Assert.DoesNotContain( "No CPU clock pin is recorded", pinned );

      foreach( string session in new[] { "v5", "v6" } )
      {
         string free = Visible( RunPage( V5_RUN, BenchResultsFixtureFiles.LegacyRun( session ) ) );
         Assert.Contains( "No CPU clock pin is recorded in this run's notes.", free );
         Assert.DoesNotContain( "CPU clock pinned for the run", free );
      }

      string block = Visible( RunPage( V7_RUN, BenchResultsFixtures.RUN_RESULTS_V8_JSON ) );
      Assert.Contains( "Clock: pinned true; pinnedMhz 3500", block );
      Assert.DoesNotContain( "No CPU clock pin is recorded", block );
      Assert.DoesNotContain( "Clock, as this run's notes record it", block );

      string plain = Edit( BenchResultsFixtures.RUN_RESULTS_V8_JSON, root => root["conditions"]!.AsObject().Remove( "clock" ) );
      Assert.DoesNotContain( "data-line=\"clock\"", RunPage( V7_RUN, plain ) );
   }

   /// <summary>
   /// A run that recorded the commit its binary was built from prints it on its page, and a build block is never read as the build configuration.
   /// </summary>
   [Fact]
   public void ARunPage_PrintsTheCodeCommitTheRunRecorded()
   {
      string json = Edit( BenchResultsFixtures.RUN_RESULTS_V8_JSON, root => root["conditions"]!.AsObject()["build"] = new JsonObject { ["informationalVersion"] = "1.0.0+abc1234", ["commit"] = "abc1234def5678" } );
      string html = RunPage( V7_RUN, json );
      Assert.Contains( "Release build; Code commit abc1234def5678;", html );
      Assert.DoesNotContain( "bench-no", html );

      string noConfiguration = Edit( json, root => root["conditions"]!.AsObject().Remove( "buildConfiguration" ) );
      string bare = RunPage( V7_RUN, noConfiguration );
      Assert.Contains( "Build not recorded; Code commit abc1234def5678;", bare );
      Assert.DoesNotContain( "commit=abc", bare );
   }

   /// <summary>
   /// The reuse sentence is on the page of a run of the session it is about and on no other run page: the v5 and v6 basis runs carry their own session's
   /// sentence, the v7 run carries the v7 one, and a v8 run, which no blocked report used, carries none. The dry check found the v7 sentence on all twelve.
   /// </summary>
   [Fact]
   public void ARunPage_PrintsOnlyTheReuseSentenceOfItsOwnSession()
   {
      WriteReuseSet( null );
      foreach( string run in new[] { V5_RUN, V6_RUN, V7_RUN, V8_RUN } )
      {
         WriteRunFolder( run, BenchResultsFixtures.RUN_RESULTS_V8_JSON );
      }

      string v5 = Visible( BenchResultsEndpoints.RunPageHtml( _root, V5_RUN )! );
      string v6 = Visible( BenchResultsEndpoints.RunPageHtml( _root, V6_RUN )! );
      string v7 = Visible( BenchResultsEndpoints.RunPageHtml( _root, V7_RUN )! );
      string v8 = Visible( BenchResultsEndpoints.RunPageHtml( _root, V8_RUN )! );

      Assert.Contains( REUSE_V5, v5 );
      Assert.DoesNotContain( REUSE_V6, v5 );
      Assert.DoesNotContain( REUSE_V7, v5 );
      Assert.Contains( REUSE_V6, v6 );
      Assert.DoesNotContain( REUSE_V5, v6 );
      Assert.DoesNotContain( REUSE_V7, v6 );
      Assert.Contains( REUSE_V7, v7 );
      Assert.DoesNotContain( REUSE_V5, v7 );
      Assert.DoesNotContain( REUSE_V6, v7 );
      Assert.DoesNotContain( "were also used by", v8 );
      Assert.Single( Regex.Matches( v7, Regex.Escape( REUSE_V7 ) ) );
   }

   /// <summary>
   /// Two folders that use a run and carry the same reuse sentence give one sentence on the run's page, not one for each folder.
   /// </summary>
   [Fact]
   public void ARunPage_PrintsASentenceTwoFoldersShare_Once()
   {
      WriteReuseSet( null );
      File.Copy( Path.Combine( _root, SET, "consolidated.json" ), Path.Combine( Directory.CreateDirectory( Path.Combine( _root, "candidate-v8" ) ).FullName, "consolidated.json" ) );
      WriteRunFolder( V7_RUN, BenchResultsFixtures.RUN_RESULTS_V8_JSON );

      string text = Visible( BenchResultsEndpoints.RunPageHtml( _root, V7_RUN )! );

      Assert.Single( Regex.Matches( text, Regex.Escape( REUSE_V7 ) ) );
      Assert.Contains( "candidate-v8", text );
   }

   /// <summary>
   /// A run no folder uses carries no reuse sentence and no used-by line, and a run that only a blocked folder in the older shape names carries none either.
   /// </summary>
   [Fact]
   public void ARunPage_OfARunNoSetUses_CarriesNoReuseSentence()
   {
      WriteReuseSet( null );
      WriteRunFolder( "20261001-010101-eshoponweb", BenchResultsFixtures.RUN_RESULTS_V8_JSON );

      string text = Visible( BenchResultsEndpoints.RunPageHtml( _root, "20261001-010101-eshoponweb" )! );

      Assert.DoesNotContain( "were also used by", text );
      Assert.DoesNotContain( "Used by", text );
   }

   /// <summary>
   /// A sentence written for one part is printed on the page of a run only when the run plays that part: a run that feeds the threshold and is not a run of a
   /// compared session gets the basis sentence and not the claim one, and a run that is both gets both. The part a sentence is worded for is read from its slot.
   /// </summary>
   [Fact]
   public void ARunPage_PrintsTheSentenceWordedForThePartItPlays()
   {
      WriteReuseSet( sentences =>
      {
         sentences.Add( BenchResultsV8Fixture.Sentence( "runs.reuse.claim.v5", "CLAIM-V5 words." ) );
         sentences.Add( BenchResultsV8Fixture.Sentence( "runs.reuse.basis.v5", "BASIS-V5 words." ) );
         sentences.Add( BenchResultsV8Fixture.Sentence( "runs.reuse.claim.v7", "CLAIM-V7 words." ) );
         sentences.Add( BenchResultsV8Fixture.Sentence( "runs.reuse.basis.v7", "BASIS-V7 words." ) );
      } );
      WriteRunFolder( V5_RUN, BenchResultsFixtures.RUN_RESULTS_V8_JSON );
      WriteRunFolder( V7_RUN, BenchResultsFixtures.RUN_RESULTS_V8_JSON );

      string v5 = Visible( BenchResultsEndpoints.RunPageHtml( _root, V5_RUN )! );
      string v7 = Visible( BenchResultsEndpoints.RunPageHtml( _root, V7_RUN )! );

      Assert.Contains( "BASIS-V5 words.", v5 );
      Assert.DoesNotContain( "CLAIM-V5 words.", v5 );
      Assert.Contains( "BASIS-V7 words.", v7 );
      Assert.Contains( "CLAIM-V7 words.", v7 );
   }

   /// <summary>
   /// A run page names the part its run plays, with the session of a basis run: "(v7, basis)" for a run that is both, "(basis v5)" for an older run that only
   /// feeds the threshold, and the list marks the same way.
   /// </summary>
   [Fact]
   public void TheMarks_NameTheSessionOfABasisRun()
   {
      WriteReuseSet( null );
      WriteRunFolder( V5_RUN, BenchResultsFixtures.RUN_RESULTS_V8_JSON );
      WriteRunFolder( V7_RUN, BenchResultsFixtures.RUN_RESULTS_V8_JSON );

      string v5 = BenchResultsEndpoints.RunPageHtml( _root, V5_RUN )!;
      string v7 = BenchResultsEndpoints.RunPageHtml( _root, V7_RUN )!;
      string list = BenchResultsEndpoints.ListPageHtml( _root );

      Assert.Contains( $"<a href=\"/bench-results/{SET}\">{SET}</a> (basis v5)", v5 );
      Assert.Contains( $"<a href=\"/bench-results/{SET}\">{SET}</a> (v7, basis)", v7 );
      Assert.Contains( $"<a href=\"/bench-results/{SET}\">{SET}</a> (basis v5)", list );
   }

   /// <summary>
   /// The set's own page prints every reuse sentence once, in the runs section, whichever session each is about.
   /// </summary>
   [Fact]
   public void TheSetPage_PrintsEveryReuseSentenceOnce()
   {
      WriteReuseSet( null );

      string text = Visible( BenchResultsEndpoints.RunPageHtml( _root, SET )! );

      foreach( string sentence in new[] { REUSE_V5, REUSE_V6, REUSE_V7 } )
      {
         Assert.Single( Regex.Matches( text, Regex.Escape( sentence ) ) );
      }
   }

   /// <summary>
   /// A reuse sentence that the page could not place is refused: its slot must name a session the file holds, in one of the three written forms, so a
   /// sentence never goes missing from the run pages without a word.
   /// </summary>
   [Theory]
   [InlineData( "runs.reuse.v9", "v9" )]
   [InlineData( "runs.reuse.claim.v9", "v9" )]
   [InlineData( "runs.reuse", "runs.reuse" )]
   [InlineData( "runs.reuse.claim", "runs.reuse.claim" )]
   [InlineData( "runs.reuse.later.v7", "runs.reuse.later.v7" )]
   [InlineData( "runs.reuse.v7.extra", "runs.reuse.v7.extra" )]
   public void AReuseSlotThatNamesNoSession_IsRefused( string slot, string named )
   {
      string json = BenchResultsV8Fixture.Json( root => root["sentences"]!.AsArray().Add( BenchResultsV8Fixture.Sentence( slot, "A reuse sentence." ) ) );

      InvalidDataException failure = Assert.Throws<InvalidDataException>( () => BenchConsolidatedReader.Read( json ) );

      Assert.Contains( named, failure.Message );
      Assert.Contains( "reuse", failure.Message );
   }

   /// <summary>
   /// The facts and costs table has a search effort column, and it prints the settings each engine's file entry carries: settings written as names and
   /// values, as a list of strings, as objects with a text and a source, and in the file's top-level list; an engine with none shows a dash.
   /// </summary>
   [Fact]
   public void TheFactsAndCostsTable_PrintsTheSearchEffortColumn()
   {
      string json = BenchResultsV8Fixture.Json( root =>
      {
         JsonArray why = root["why"]!.AsArray();
         WhyRow( why, "redis" )["searchEffort"] = new JsonObject { ["EF_RUNTIME"] = "100", ["M"] = "16" };
         WhyRow( why, "mariadb" )["searchEffort"] = new JsonArray( "mhnsw_ef_search=100", "ef_search is raised from 20" );
         WhyRow( why, "pgvector" )["searchEffort"] = new JsonArray( new JsonObject { ["text"] = "hnsw.ef_search=100", ["source"] = "results:targets[pgvector].searchSettings#hnsw.ef_search" } );
         root["searchEffort"] = new JsonArray( new JsonObject { ["target"] = "sqlitevec", ["settings"] = new JsonObject { ["chunk_size"] = "1024" } } );
         JsonArray sentences = root["sentences"]!.AsArray();
         sentences.Add( BenchResultsV8Fixture.Sentence( "why.settings.redis", "Redis searched with EF_RUNTIME=100 and M=16." ) );
         sentences.Add( BenchResultsV8Fixture.Sentence( "why.effort.mariadb", "The MariaDB figure used a raised ef_search, and its recall falls as the set grows." ) );
         sentences.Add( BenchResultsV8Fixture.Sentence( "why.settings.sql", "SQL Server compared every vector and has no search setting." ) );
      } );
      WriteFolder( SET, json );

      string html = BenchResultsEndpoints.RunPageHtml( _root, SET )!;
      string why = html[html.IndexOf( "<h2>Facts and costs side by side</h2>", StringComparison.Ordinal )..];
      string mariadb = Regex.Match( why, "<tr data-target=\"mariadb\">.*?</tr>", RegexOptions.Singleline ).Value;
      string sql = Regex.Match( why, "<tr data-target=\"sql\">.*?</tr>", RegexOptions.Singleline ).Value;

      Assert.Contains( "<th>Search effort per query</th>", html );
      Assert.Contains( "<td data-column=\"effort\"><ul class=\"bench-facts\"><li><code>EF_RUNTIME=100</code></li><li><code>M=16</code></li></ul></td>", html );
      Assert.Contains( "<code>mhnsw_ef_search=100</code>", html );
      Assert.Contains( "<code>ef_search is raised from 20</code>", html );
      Assert.Contains( "<code>hnsw.ef_search=100</code> <span class=\"muted\">(<code>results:targets[pgvector].searchSettings#hnsw.ef_search</code>)</span>", html );
      Assert.Contains( "<code>chunk_size=1024</code>", html );
      Assert.Contains( "<td data-column=\"effort\">-</td>", html );
      Assert.Equal( 6, Regex.Matches( html, "data-column=\"effort\"" ).Count );

      Assert.Contains( "The MariaDB figure used a raised ef_search, and its recall falls as the set grows.", mariadb );
      Assert.Contains( "SQL Server compared every vector and has no search setting.", sql );
      Assert.Contains( "<details class=\"bench-target\"><summary>Statements behind the search effort column</summary>", html );
      Assert.Single( Regex.Matches( html, Regex.Escape( "Redis searched with EF_RUNTIME=100 and M=16." ) ) );
      Assert.Single( Regex.Matches( html, Regex.Escape( "The MariaDB figure used a raised ef_search" ) ) );
      Assert.Single( Regex.Matches( html, Regex.Escape( "SQL Server compared every vector" ) ) );
      Assert.DoesNotContain( "Statements with no place on this page", html );
   }

   /// <summary>
   /// A search effort the page cannot read is refused with its path, and so is a top-level entry for an engine the facts and costs table does not list.
   /// </summary>
   [Fact]
   public void ASearchEffortThePageCannotRead_IsRefused()
   {
      string number = BenchResultsV8Fixture.Json( root => WhyRow( root["why"]!.AsArray(), "redis" )["searchEffort"] = 5 );
      string stranger = BenchResultsV8Fixture.Json( root => root["searchEffort"] = new JsonArray( new JsonObject { ["target"] = "nonesuch", ["settings"] = new JsonObject { ["a"] = "1" } } ) );

      Assert.Contains( "searchEffort", Assert.Throws<InvalidDataException>( () => BenchConsolidatedReader.Read( number ) ).Message );
      Assert.Contains( "nonesuch", Assert.Throws<InvalidDataException>( () => BenchConsolidatedReader.Read( stranger ) ).Message );
   }

   /// <summary>
   /// The withdrawn state of the 4 Oct 2026 set, as the page will serve it once its folder is renamed: its real file in the first published shape and its
   /// real report, which says "Not publishable as is". The page carries the withdrawn banner, says the file is in an older format, keeps the report in a
   /// closed block headed as a record kept as written, and does not say it "does not show it as a result" over a page that holds a ranked table. The run list
   /// says withdrawn and not the current result, and the summary page is the newest published set.
   /// </summary>
   [Fact]
   public void TheWithdrawnSetOf4Oct_IsServedAsARecordUnderABanner_AndNeverAsAResult()
   {
      WriteWithdrawnSet();
      WriteSet( SET );

      string list = BenchResultsEndpoints.ListPageHtml( _root );
      string page = BenchResultsEndpoints.RunPageHtml( _root, WITHDRAWN )!;
      string text = Visible( page );

      Assert.Contains( "<td>Withdrawn medians, older format (not the current result)</td>", list );
      Assert.Contains( $"Numbers from <a href=\"/bench-results/{SET}\">{SET}</a>.", list );
      Assert.DoesNotContain( "<th>rank</th>", list );
      Assert.DoesNotContain( "Not publishable as is", list );

      int banner = page.IndexOf( "class=\"errors bench-banner\">This set is marked withdrawn by its folder name. It is not the current result", StringComparison.Ordinal );
      int notice = page.IndexOf( "This file is in an older format.", StringComparison.Ordinal );
      int record = page.IndexOf( $"<details class=\"bench-target\"><summary>{RECORD_HEADING}</summary>", StringComparison.Ordinal );
      Assert.True( banner >= 0 && notice > banner && record > notice, "the banner, the older-format notice and the record block must come in that order" );
      Assert.DoesNotContain( "<details open", page );
      Assert.DoesNotContain( ">Report text<", page );
      Assert.Contains( "Not publishable as is.", text );
      Assert.Contains( "<th>rank</th>", page );
      Assert.DoesNotContain( "does not show it as a result", text );
      Assert.DoesNotContain( "bench-lead", page );
      Assert.DoesNotContain( "data-metric=", page );
      Assert.DoesNotMatch( new Regex( @">\s*Published" ), page );
      Assert.Contains( $"href=\"/bench-results/{WITHDRAWN}/consolidated.json\"", page );
      Assert.Contains( $"href=\"/bench-results/{WITHDRAWN}/consolidated.md\"", page );
   }

   /// <summary>
   /// With only the withdrawn set in the results root, the results page is the run list alone: no headline, no table, no "Numbers from" line, and the
   /// withdrawn folder is listed as withdrawn. A withdrawn set is never the summary.
   /// </summary>
   [Fact]
   public void WithOnlyTheWithdrawnSet_TheResultsPageHasNoSummary()
   {
      WriteWithdrawnSet();
      WriteRunFolder( V7_RUN, BenchResultsFixtureFiles.LegacyRun( "v7" ) );

      string list = BenchResultsEndpoints.ListPageHtml( _root );

      Assert.Contains( "<h1>Benchmark results</h1>", list );
      Assert.DoesNotContain( "Numbers from", list );
      Assert.DoesNotContain( "bench-lead", list );
      Assert.DoesNotContain( "data-metric=", list );
      Assert.DoesNotContain( "<th>rank</th>", list );
      Assert.DoesNotContain( "Not publishable as is", list );
      Assert.Contains( "Withdrawn medians, older format (not the current result)", list );
      Assert.Empty( BenchResultsEndpoints.PublishedFolders( _root ) );
   }

   /// <summary>
   /// Every set whose report is a record (withdrawn, blocked, or a file in an older shape) carries the record heading; a candidate in the current shape and
   /// a published set keep the plain heading. A ranked table in an old report is therefore never under a plain "Report text".
   /// </summary>
   [Fact]
   public void TheReportOfASetThatIsNotACurrentResult_CarriesTheRecordHeading()
   {
      WriteWithdrawnSet();
      WriteFolder( "blocked-2026-10-05-v6", BenchResultsFixtures.REPORT_SHAPE_JSON );
      WriteFolder( "published-2026-10-03", BenchResultsFixtures.OLD_PUBLISHED_JSON );
      File.WriteAllText( Path.Combine( _root, "published-2026-10-03", "consolidated.md" ), BenchResultsV8Fixture.REPORT_MD );
      File.WriteAllText( Path.Combine( _root, "blocked-2026-10-05-v6", "consolidated.md" ), BenchResultsV8Fixture.REPORT_MD );
      WriteSet( "candidate-v8" );
      WriteSet( SET );

      foreach( string name in new[] { WITHDRAWN, "blocked-2026-10-05-v6", "published-2026-10-03" } )
      {
         string page = BenchResultsEndpoints.RunPageHtml( _root, name )!;
         Assert.Contains( $"<summary>{RECORD_HEADING}</summary>", page );
         Assert.DoesNotContain( "<summary>Report text</summary>", page );
      }

      foreach( string name in new[] { "candidate-v8", SET } )
      {
         string page = BenchResultsEndpoints.RunPageHtml( _root, name )!;
         Assert.Contains( "<summary>Report text</summary>", page );
         Assert.DoesNotContain( RECORD_HEADING, page );
      }
   }

   /// <summary>
   /// The first published shape and the shape before v8 are told they are older, in words that hold when the report beside them carries a table: the page
   /// draws no headline and no table from the file.
   /// </summary>
   [Fact]
   public void TheOlderFormatNotice_DoesNotClaimTheReportBesideItIsHidden()
   {
      WriteWithdrawnSet();

      string text = Visible( BenchResultsEndpoints.RunPageHtml( _root, WITHDRAWN )! );

      Assert.Contains( "This file is in an older format. The page draws no headline and no table from it.", text );
   }

   /// <summary>
   /// The results list puts a published set first and a withdrawn or blocked set after it, then the runs, newest name first in each group: by name alone
   /// "withdrawn-" sorts after "published-" and a withdrawn set led the list as if it were the newest result.
   /// </summary>
   [Fact]
   public void TheResultsList_DoesNotLeadWithAWithdrawnSet()
   {
      WriteWithdrawnSet();
      WriteSet( SET );
      WriteSet( "candidate-v8" );
      WriteFolder( "blocked-2026-10-05-v6", BenchResultsFixtures.REPORT_SHAPE_JSON );
      WriteRunFolder( V7_RUN, BenchResultsFixtureFiles.LegacyRun( "v7" ) );
      WriteRunFolder( V8_RUN, Edit( BenchResultsFixtureFiles.LegacyRun( "v7" ), root => root["startedUtc"] = "2026-10-08T13:00:00Z" ) );

      string list = BenchResultsEndpoints.ListPageHtml( _root );
      string table = list[list.IndexOf( "<h2>All runs</h2>", StringComparison.Ordinal )..];

      int[] at = new[] { "Published medians of", "Candidate medians of", "Withdrawn medians", "Blocked medians", "8 Oct 2026, 13:00", "6 Oct 2026, 13:06" }
         .Select( t => table.IndexOf( t, StringComparison.Ordinal ) ).ToArray();
      Assert.All( at, i => Assert.True( i >= 0, $"a row is missing from the list: {string.Join( ",", at )}" ) );
      Assert.Equal( at.OrderBy( i => i ).ToArray(), at );
   }

   /// <summary>
   /// Before its folder is renamed, the 4 Oct set (a folder named "published-" that holds the first published shape and a report that says "Not publishable as
   /// is") is not listed as published medians: the page draws no result from the file and the list says so. The dry check listed it as published.
   /// </summary>
   [Fact]
   public void APublishedNamedFolderHoldingAnOlderFile_IsNotListedAsPublished()
   {
      WriteFolder( "published-2026-10-04", BenchResultsFixtureFiles.Text( "withdrawn-2026-10-04.consolidated.json" ) );
      File.WriteAllText( Path.Combine( _root, "published-2026-10-04", "consolidated.md" ), BenchResultsFixtureFiles.Text( "withdrawn-2026-10-04.consolidated.md" ) );

      string list = BenchResultsEndpoints.ListPageHtml( _root );

      Assert.Contains( "<td>Medians, older format (not shown as a result)</td>", list );
      Assert.DoesNotContain( "Published medians", list );
      Assert.DoesNotContain( "bench-lead", list );
      Assert.DoesNotContain( "<th>rank</th>", list );
   }

   /// <summary>
   /// The engine settings table the disclosure sentence points to is on the page: one row per setting with its engine, session, value and how it was read or
   /// set, in a closed block; text is encoded; and a file with no engine settings prints no such block.
   /// </summary>
   [Fact]
   public void TheEngineSettingsTable_IsOnThePage_WhenTheRunsRecordedSettings()
   {
      string json = BenchResultsV8Fixture.Json( root =>
      {
         root["engineSettings"] = new JsonArray(
            new JsonObject { ["target"] = "redis", ["session"] = "v8", ["settings"] = new JsonArray(
               new JsonObject { ["key"] = "maxmemory", ["value"] = "0", ["how"] = "read: redis-cli CONFIG GET maxmemory" },
               new JsonObject { ["key"] = "save", ["value"] = "300 1", ["how"] = "set: deploy/engines/redis.compose.yaml#--save \"300 1\"" } ) },
            new JsonObject { ["target"] = "qdrant", ["session"] = "v8", ["settings"] = new JsonArray(
               new JsonObject { ["key"] = "<hnsw>&", ["value"] = "", ["how"] = "read: GET /collections" } ) } );
         root["sentences"]!.AsArray().Add( BenchResultsV8Fixture.Sentence( "disclosure.settings", "The engine settings table lists what run 20261008-130000-eshoponweb read from each running engine or set from a repository file." ) );
      } );
      WriteFolder( SET, json );

      string html = BenchResultsEndpoints.RunPageHtml( _root, SET )!;

      Assert.Contains( "<summary>Engine settings as recorded</summary>", html );
      Assert.Contains( "<tr data-target=\"redis\"><td>Redis</td><td>v8</td><td><code>maxmemory</code></td><td>0</td><td>read: redis-cli CONFIG GET maxmemory</td></tr>", html );
      Assert.Contains( "<td><code>save</code></td><td>300 1</td><td>set: deploy/engines/redis.compose.yaml#--save &quot;300 1&quot;</td>", html );
      Assert.Contains( "<code>&lt;hnsw&gt;&amp;</code></td><td>-</td>", html );
      Assert.Equal( 3, Regex.Matches( html, "<td>v8</td><td><code>" ).Count );
      Assert.DoesNotContain( "Statements with no place on this page", html );

      WriteFolder( "candidate-none", BenchResultsV8Fixture.Json() );
      Assert.DoesNotContain( "Engine settings as recorded", BenchResultsEndpoints.RunPageHtml( _root, "candidate-none" )! );
   }

   /// <summary>
   /// A row that is not ranked because its setup changed between sessions says its median is of one session's runs, and a clock field that says the clock was
   /// read from the runs' notes is explained where it is printed. The dry check found a one-session row under a header that read "median of all runs", and
   /// "legacyParsed true" that nobody could decode.
   /// </summary>
   [Fact]
   public void TheSetPage_ExplainsAOneSessionMedian_AndTheLegacyClockField()
   {
      string json = BenchResultsV8Fixture.Json( root =>
      {
         JsonObject row = root["metrics"]![2]!["rows"]!.AsArray().Select( r => r!.AsObject() ).First( r => r["target"]!.GetValue<string>() == "sqlitevec" );
         row["status"] = "one-session";
         row["notSeparatedFrom"] = new JsonArray();
         row["perSession"] = new JsonObject { ["v8"] = row["perSession"]!["v8"]!.DeepClone() };
      } );
      WriteFolder( SET, json );

      string text = Visible( BenchResultsEndpoints.RunPageHtml( _root, SET )! );

      Assert.Contains( "A row marked one session has figures from one session, its median is of that session's runs, and it is not ranked.", text );
      Assert.Contains( "legacyParsed true (the clock was read from the runs' notes, not from a clock block)", text );
      Assert.Contains( "legacyParsed false", text );
      Assert.DoesNotContain( "legacyParsed false (the clock", text );
   }

   /// <summary>
   /// The heading "Targets not in this report" is printed only when the file carries a sentence for it: a set that leaves no target out has no empty heading.
   /// The dry check found an empty heading in the Markdown report.
   /// </summary>
   [Fact]
   public void TheSetPage_HasNoEmptyTargetsNotInThisReportHeading()
   {
      string without = BenchResultsV8Fixture.Json( root =>
      {
         JsonArray sentences = root["sentences"]!.AsArray();
         foreach( JsonNode? gone in sentences.Where( x => x!["slot"]!.GetValue<string>().StartsWith( "notinreport", StringComparison.Ordinal ) ).ToList() )
         {
            sentences.Remove( gone );
         }
      } );
      WriteFolder( "candidate-none", without );
      WriteFolder( "candidate-some", BenchResultsV8Fixture.Json() );

      Assert.DoesNotContain( "Targets not in this report", BenchResultsEndpoints.RunPageHtml( _root, "candidate-none" )! );
      Assert.Contains( "<h2>Targets not in this report</h2>", BenchResultsEndpoints.RunPageHtml( _root, "candidate-some" )! );
   }

   /// <summary>
   /// The real run files, when this machine has them: the pages of the v5, v6 and v7 runs describe their warm-up in their own words and their clock as
   /// their notes record it. Skipped, with the reason named, on a machine without the files.
   /// </summary>
   [FactIfPathExists( "/home/dan/ForClaude/GenericVectorBuilder/bench-results/20261006-130619-eshoponweb/results.json" )]
   public void TheRealRuns_DescribeTheirWarmupAndClockInTheirOwnWords()
   {
      const string results = "/home/dan/ForClaude/GenericVectorBuilder/bench-results";
      foreach( ( string run, string warmup, string clock ) in new[]
      {
         ( V5_RUN, "untimed warm-up of 20 searches", "No CPU clock pin is recorded" ),
         ( V6_RUN, "for at least 15 s and at least 20 searches", "No CPU clock pin is recorded" ),
         ( V7_RUN, "for at least 15 s and at least 20 searches", "CPU clock pinned for the run: turbo off" ),
      } )
      {
         if( !File.Exists( Path.Combine( results, run, "results.json" ) ) )
         {
            continue;
         }

         string text = Visible( RunPage( run, File.ReadAllText( Path.Combine( results, run, "results.json" ) ) ) );
         Assert.Contains( warmup, text );
         Assert.Contains( clock, text );
         Assert.DoesNotMatch( new Regex( @"Warm-up searches\s*\d" ), text );
      }
   }

   #endregion Public Methods

   #region Private Methods

   /// <summary>
   /// Writes one run folder with the given results.json and returns the page of that run.
   /// </summary>
   /// <param name="run">The run's folder name.</param>
   /// <param name="json">The results.json text.</param>
   /// <returns>The run page's HTML.</returns>
   private string RunPage( string run, string json )
   {
      WriteRunFolder( run, json );
      return BenchResultsEndpoints.RunPageHtml( _root, run )!;
   }

   /// <summary>
   /// Writes a run folder.
   /// </summary>
   /// <param name="run">The run's folder name.</param>
   /// <param name="json">The results.json text.</param>
   private void WriteRunFolder( string run, string json )
   {
      string folder = Path.Combine( _root, run );
      Directory.CreateDirectory( folder );
      File.WriteAllText( Path.Combine( folder, "results.json" ), json );
   }

   /// <summary>
   /// Writes a folder holding a consolidated.json.
   /// </summary>
   /// <param name="name">Folder name.</param>
   /// <param name="json">File text.</param>
   private void WriteFolder( string name, string json )
   {
      string folder = Path.Combine( _root, name );
      Directory.CreateDirectory( folder );
      File.WriteAllText( Path.Combine( folder, "consolidated.json" ), json );
   }

   /// <summary>
   /// Writes a folder holding the v8 fixture and its report.
   /// </summary>
   /// <param name="name">Folder name.</param>
   private void WriteSet( string name )
   {
      WriteFolder( name, BenchResultsV8Fixture.Json() );
      File.WriteAllText( Path.Combine( _root, name, "consolidated.md" ), BenchResultsV8Fixture.REPORT_MD );
   }

   /// <summary>
   /// Writes the withdrawn set of 4 Oct 2026 from the real copies of its two files.
   /// </summary>
   private void WriteWithdrawnSet()
   {
      WriteFolder( WITHDRAWN, BenchResultsFixtureFiles.Text( "withdrawn-2026-10-04.consolidated.json" ) );
      File.WriteAllText( Path.Combine( _root, WITHDRAWN, "consolidated.md" ), BenchResultsFixtureFiles.Text( "withdrawn-2026-10-04.consolidated.md" ) );
   }

   /// <summary>
   /// Writes the published set the reuse tests read: two compared sessions (v7 and v8) and a basis of v5, v6, v7 and v8 runs, each basis run naming its
   /// session, with one reuse sentence for each of v5, v6 and v7 and none for v8.
   /// </summary>
   /// <param name="more">Adds sentences to the file's list, or null.</param>
   private void WriteReuseSet( Action<JsonArray>? more )
   {
      string json = BenchResultsV8Fixture.Json( root =>
      {
         JsonArray basis = root["basis"]!["runs"]!.AsArray();
         basis.Clear();
         foreach( ( string folder, int seed, string session ) in new[] { ( V5_RUN, 501, "v5" ), ( V6_RUN, 601, "v6" ), ( V7_RUN, 701, "v7" ), ( V8_RUN, 801, "v8" ) } )
         {
            basis.Add( new JsonObject { ["folder"] = folder, ["seed"] = seed, ["session"] = session, ["conditions"] = new JsonObject { ["turbo"] = "off" } } );
         }

         JsonArray sentences = root["sentences"]!.AsArray();
         foreach( JsonNode? old in sentences.Where( s => s!["slot"]!.GetValue<string>().StartsWith( "runs.reuse", StringComparison.Ordinal ) ).ToList() )
         {
            sentences.Remove( old );
         }

         sentences.Add( BenchResultsV8Fixture.Sentence( "runs.reuse.v7", REUSE_V7 ) );
         sentences.Add( BenchResultsV8Fixture.Sentence( "runs.reuse.v6", REUSE_V6 ) );
         sentences.Add( BenchResultsV8Fixture.Sentence( "runs.reuse.v5", REUSE_V5 ) );
         more?.Invoke( sentences );
      } );
      WriteFolder( SET, json );
      File.WriteAllText( Path.Combine( _root, SET, "consolidated.md" ), BenchResultsV8Fixture.REPORT_MD );
   }

   /// <summary>
   /// The why row of a target.
   /// </summary>
   /// <param name="why">The why array.</param>
   /// <param name="target">The target.</param>
   /// <returns>The row's object.</returns>
   private static JsonObject WhyRow( JsonArray why, string target )
   {
      return why.Select( r => r!.AsObject() ).First( r => r["target"]!.GetValue<string>() == target );
   }

   /// <summary>
   /// A JSON text after an edit of its root.
   /// </summary>
   /// <param name="json">The text.</param>
   /// <param name="edit">The edit.</param>
   /// <returns>The edited text.</returns>
   private static string Edit( string json, Action<JsonNode> edit )
   {
      JsonNode root = JsonNode.Parse( json )!;
      edit( root );
      return root.ToJsonString();
   }

   /// <summary>
   /// The text a reader sees: tags removed, entities decoded, white space collapsed.
   /// </summary>
   /// <param name="html">The page.</param>
   /// <returns>The visible text.</returns>
   private static string Visible( string html )
   {
      return Regex.Replace( WebUtility.HtmlDecode( Regex.Replace( html, "<[^>]+>", " " ) ), @"\s+", " " );
   }

   #endregion Private Methods

   #region IDisposable

   /// <summary>
   /// Removes this test's folder.
   /// </summary>
   public void Dispose()
   {
      if( Directory.Exists( _root ) )
      {
         Directory.Delete( _root, true );
      }

      GC.SuppressFinalize( this );
   }

   #endregion IDisposable
}
