using System.Globalization;
using System.Net;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using GenericVectorBuilder.Web.BenchPages;
using GenericVectorBuilder.Web.Endpoints;

namespace GenericVectorBuilder.Tests.Unit;

/// <summary>
/// The header at the top of the /bench-results page: what the numbers are of (the data set, its size, the queries), then the engines in order of searches
/// per second with eight searchers at once, each with its ms per search worked out from that figure and its difference from the row above.
/// What these tests hold the header to: the order is the order of the file's qps8 medians, every figure is the file's or worked out from it (ms is one second
/// over the searches per second; a difference is from the row above), a row the report's test does not separate from the row above carries a marker exactly
/// where the file's notSeparatedFrom names that row, an engine row the file gives a note (Redis holds its data in memory) carries that note as a bold
/// label, a row the tool recorded as not held is last, labelled and given no figure, no sentence is typed into the header that a legend or the file does not
/// hold, and the header sits above everything the page printed before.
/// Why built from the v8 fixture and a copy of the real set: the fixture lets a test change one field and see the header follow; the real set (when this
/// machine holds it) shows the header reads what the consolidate command actually wrote.
/// </summary>
public sealed class BenchHeaderTests : IDisposable
{
   #region Data Members

   private const string FOLDER = "published-2026-10-08";
   private const string DATASET = "eshoponweb";
   private const string QUESTION_FILE = "/home/dan/ForClaude/evalkit/questions_golden.json";
   private const string RECORDED = "golden: 20 labelled questions from " + QUESTION_FILE + ", embedded with qwen3-emb-0.6b (cached in /home/dan/gvb-data/bench-cache/golden-eshoponweb-68df42ca69efa079.json, no embedding calls)";
   private const string COMMIT_V8 = "9b924200abc3";
   private const string REAL_NOTE = "redis holds its data in memory: its saved docs page says 'Redis is an in-memory but persistent on disk database', and its compose file sets save \"300 1\" and appendonly no.";
   private const double NOT_HELD_MEDIAN = 377.54598230412046;
   private const string REAL_SET = "/home/dan/ForClaude/GenericVectorBuilder/bench-results/published-2026-10-08/consolidated.json";

   private static readonly Regex NUMBER = new( @"\d+(?:[.,]\d+)*", RegexOptions.Compiled );
   private static readonly Regex TIE_WORDS = new( @"\b(tie|ties|tied|equal|equals|equally|same speed|identical|fastest|slowest|winner|bands?)\b", RegexOptions.IgnoreCase | RegexOptions.Compiled );

   private readonly string _root;

   /// <summary>One row as the test expects to see it, worked out from the file's own text.</summary>
   private sealed record Expected( string Target, string Display, double Qps, string[] NotSeparatedFrom, string? Note );

   /// <summary>One row of the header as rendered.</summary>
   private sealed record Shown( string Engine, string State, string Html, List<string> Cells );

   #endregion Data Members

   #region Constructor

   /// <summary>
   /// Creates an empty results root for this test under the test output folder.
   /// </summary>
   public BenchHeaderTests()
   {
      _root = Path.Combine( AppContext.BaseDirectory, "bench-header-tests", Guid.NewGuid().ToString( "N" ) );
      Directory.CreateDirectory( _root );
   }

   #endregion Constructor

   #region Public Methods

   /// <summary>
   /// The engines come in the order of the file's qps8 medians, most searches per second first. The fixture's p50 and one-searcher tables put Redis first, so a
   /// header that took the first table, or the one-searcher figures, would put the engines in another order.
   /// </summary>
   [Fact]
   public void TheOrder_IsTheOrderOfTheQps8Medians_MostSearchesPerSecondFirst()
   {
      string json = Json();
      List<Expected> expected = RankedOf( json );
      List<Shown> shown = RankedRows( Header( json ) );

      Assert.Equal( expected.Select( e => e.Target ), shown.Select( s => s.Engine ) );
      Assert.Equal( new[] { "mariadb", "redis" }, shown.Take( 2 ).Select( s => s.Engine ) );
      double[] qps = shown.Select( s => double.Parse( s.Cells[1], NumberStyles.AllowThousands, CultureInfo.InvariantCulture ) ).ToArray();
      Assert.Equal( qps.OrderByDescending( q => q ), qps );
   }

   /// <summary>
   /// Each row gives the searches per second as the file's median (whole, with thousands separators) and the ms per search as one second over that median,
   /// worked out here from the file's text and not from the header's own figures.
   /// </summary>
   [Fact]
   public void MsPerSearch_IsOneSecondOverTheSearchesPerSecond()
   {
      string json = Json();
      List<Expected> expected = RankedOf( json );
      List<Shown> shown = RankedRows( Header( json ) );

      Assert.Equal( expected.Count, shown.Count );
      for( int i = 0; i < expected.Count; i++ )
      {
         Assert.Equal( Math.Round( expected[i].Qps, MidpointRounding.AwayFromZero ).ToString( "N0", CultureInfo.InvariantCulture ), shown[i].Cells[1] );
         Assert.Equal( ( 1000.0 / expected[i].Qps ).ToString( "0.000", CultureInfo.InvariantCulture ), shown[i].Cells[2] );
      }
   }

   /// <summary>
   /// A difference is the row's ms per search less the ms per search of the row above it, with a plus sign; the first row has no row above and says so.
   /// </summary>
   [Fact]
   public void TheDifference_IsFromTheRowAbove_AndTheFirstRowHasNone()
   {
      string json = Json();
      List<Expected> expected = RankedOf( json );
      List<Shown> shown = RankedRows( Header( json ) );

      Assert.Equal( BenchLegends.L_HEADER_FIRST, shown[0].Cells[3] );
      for( int i = 1; i < expected.Count; i++ )
      {
         double difference = 1000.0 / expected[i].Qps - 1000.0 / expected[i - 1].Qps;
         Assert.True( difference >= 0, $"{expected[i].Target} is listed after {expected[i - 1].Target} and its ms per search is smaller" );
         Assert.Equal( "+" + difference.ToString( "0.000", CultureInfo.InvariantCulture ), shown[i].Cells[3] );
         double shownMs = double.Parse( shown[i].Cells[2], CultureInfo.InvariantCulture ) - double.Parse( shown[i - 1].Cells[2], CultureInfo.InvariantCulture );
         Assert.True( Math.Abs( shownMs - double.Parse( shown[i].Cells[3], CultureInfo.InvariantCulture ) ) <= 0.0011, $"{expected[i].Target}: the difference is not the two printed ms per search less each other, give or take the last digit" );
      }
   }

   /// <summary>
   /// The note the file gives an engine's row (Redis holds its data in memory) is the row's bold label, in the report's own words and with its sources, and no
   /// other row carries a label.
   /// </summary>
   [Fact]
   public void TheNoteOfARow_IsItsBoldLabel_InTheReportsOwnWords()
   {
      string json = Json();
      List<Shown> shown = RankedRows( Header( json ) );
      Shown redis = shown.Single( s => s.Engine == "redis" );

      Assert.Contains( "<strong class=\"bench-header-memory\">Holds its data in memory.</strong>", redis.Html );
      Assert.Contains( BenchLegends.L_HEADER_NOTE_KEEP, redis.Html );
      Assert.Contains( "<details class=\"bench-sources muted\">", redis.Html );
      Assert.Contains( "deploy/engines/redis.compose.yaml#--appendonly no", redis.Html );
      Assert.All( shown.Where( s => s.Engine != "redis" ), s => Assert.DoesNotContain( "bench-header-memory", s.Html ) );
   }

   /// <summary>
   /// A note in the shape the real set gives ("redis holds its data in memory: its saved docs page says ...") has the words before the first colon as the bold
   /// label and the rest as small text, so the label is short and the evidence is still printed whole; the label and the rest together are the note.
   /// </summary>
   [Fact]
   public void ANoteWithItsEvidence_IsSplitAtTheFirstColon_AndNothingIsLost()
   {
      string json = Json( root => RowOf( root, "qps8", "redis" )["note"] = REAL_NOTE );
      Shown redis = RankedRows( Header( json ) ).Single( s => s.Engine == "redis" );
      string label = Regex.Match( redis.Html, "<strong class=\"bench-header-memory\">(.*?)</strong>" ).Groups[1].Value;
      string rest = Regex.Match( redis.Html, "<small class=\"muted bench-header-evidence\">(.*?)</small>", RegexOptions.Singleline ).Groups[1].Value;

      Assert.Equal( "redis holds its data in memory", label );
      Assert.Equal( REAL_NOTE, WebUtility.HtmlDecode( label ) + ": " + WebUtility.HtmlDecode( rest ) );
   }

   /// <summary>
   /// The label is bold and large in the app's stylesheet, so "bold" is a rule that is in the file and not only a word in a test.
   /// </summary>
   [Fact]
   public void TheMemoryLabel_IsBoldInTheStylesheet()
   {
      string css = File.ReadAllText( SourcePath( "src", "GenericVectorBuilder.Web", "wwwroot", "style.css" ) );
      Match rule = Regex.Match( css, @"\.bench-header-memory\s*\{([^}]*)\}" );

      Assert.True( rule.Success, "no .bench-header-memory rule in style.css" );
      Assert.Matches( @"font-weight:\s*(700|800|900)", rule.Groups[1].Value );
      Assert.Matches( @"font-size:\s*\d", rule.Groups[1].Value );
   }

   /// <summary>
   /// A row the tool recorded as not held is last, in a group that says it is not ranked, labelled with the report's own words, and has no searches per
   /// second, no ms per search and no difference: its figure is not one to quote.
   /// </summary>
   [Fact]
   public void ARowThatIsNotHeld_IsLast_Labelled_AndGivenNoFigure()
   {
      string json = Json( AddNotHeldRow );
      string header = Header( json );
      List<Shown> all = AllRows( header );
      Shown last = all[^1];

      Assert.Equal( "clickhouse", last.Engine );
      Assert.Equal( "not-held", last.State );
      Assert.Equal( BenchLegends.L_NOT_HELD, last.Cells[1] );
      Assert.Equal( 2, last.Cells.Count );
      Assert.DoesNotContain( "378", Visible( last.Html ) );
      Assert.DoesNotContain( ( 1000.0 / NOT_HELD_MEDIAN ).ToString( "0.00", CultureInfo.InvariantCulture ), Visible( last.Html ) );
      Assert.DoesNotContain( "+", Visible( last.Html ) );
      Assert.Equal( RankedOf( json ).Select( e => e.Target ), all.Take( all.Count - 1 ).Select( s => s.Engine ) );
      Assert.True( header.IndexOf( BenchLegends.H_NOT_RANKED, StringComparison.Ordinal ) > header.IndexOf( "data-engine=\"sql\"", StringComparison.Ordinal ) );
      Assert.Contains( BenchLegends.NOT_HELD, WebUtility.HtmlDecode( header ) );
      Assert.Contains( BenchLegends.L_HEADER_FIGURES_BELOW, header );
   }

   /// <summary>
   /// A row the file marks not held by its flag, whatever its status says, is not ranked either: the page's tables treat the two marks as one, and the header
   /// must not order by a figure the tool says not to quote.
   /// </summary>
   [Fact]
   public void ARowThatCarriesTheNotHeldFlag_IsNotRanked_WhateverItsStatus()
   {
      string json = Json( root => RowOf( root, "qps8", "sql" )["flags"] = new JsonArray( new JsonObject { ["code"] = "not-held", ["text"] = "Run x: recorded as NOT HELD." } ) );
      List<Shown> all = AllRows( Header( json ) );

      Assert.Equal( "sql", all[^1].Engine );
      Assert.Equal( BenchLegends.L_NOT_HELD, all[^1].Cells[1] );
      Assert.Equal( new[] { "mariadb", "redis", "pgvector", "qdrant", "sqlitevec" }, all.Take( all.Count - 1 ).Select( s => s.Engine ) );
   }

   /// <summary>
   /// A row carries the marker exactly where the file's notSeparatedFrom for that row names the row above it, and the first row never carries one. The
   /// fixture has rows of both kinds, so the check cannot pass by marking all or none.
   /// </summary>
   [Fact]
   public void TheMarker_AppearsExactlyWhereTheFileNamesTheRowAbove()
   {
      string json = Json( AddNotHeldRow );
      List<Expected> expected = RankedOf( json );
      List<Shown> shown = RankedRows( Header( json ) );
      bool[] marked = shown.Select( s => s.Html.Contains( "data-mark=\"within\"", StringComparison.Ordinal ) ).ToArray();
      bool[] named = expected.Select( ( e, i ) => i > 0 && e.NotSeparatedFrom.Contains( expected[i - 1].Target ) ).ToArray();

      Assert.Equal( named, marked );
      Assert.Contains( true, named );
      Assert.Contains( false, named.Skip( 1 ) );
      Assert.False( marked[0] );
      Assert.All( shown.Where( s => s.Html.Contains( "data-mark=\"within\"", StringComparison.Ordinal ) ), s => Assert.Contains( BenchLegends.L_HEADER_WITHIN, s.Html ) );
      Assert.Contains( BenchLegends.L_HEADER_WITHIN_LEGEND, WebUtility.HtmlDecode( Header( json ) ) );
   }

   /// <summary>
   /// A row that the file separates from the row above it carries no marker even when it is not separated from a row further up: the marker is about the
   /// row above, as the legend says.
   /// </summary>
   [Fact]
   public void TheMarker_IsAboutTheRowAbove_NotAboutAnyRowAboveIt()
   {
      string json = Json( root =>
      {
         JsonObject sqlitevec = RowOf( root, "qps8", "sqlitevec" );
         sqlitevec["notSeparatedFrom"] = new JsonArray( JsonValue.Create( "pgvector" ) );
         JsonObject pgvector = RowOf( root, "qps8", "pgvector" );
         ( (JsonArray)pgvector["notSeparatedFrom"]! ).Add( JsonValue.Create( "sqlitevec" ) );
      } );
      List<Shown> shown = RankedRows( Header( json ) );
      Shown sqlitevecRow = shown.Single( s => s.Engine == "sqlitevec" );

      Assert.Equal( "qdrant", shown[shown.IndexOf( sqlitevecRow ) - 1].Engine );
      Assert.DoesNotContain( "data-mark=\"within\"", sqlitevecRow.Html );
   }

   /// <summary>
   /// The header never says two engines tied, were equal, or ran at the same speed, and uses none of the words the page's tests ban elsewhere (fastest,
   /// slowest, winner, band): rows that the test does not separate are marked as such and nothing more is claimed.
   /// </summary>
   [Fact]
   public void TheHeader_NeverSaysTieOrEqualOrSameSpeed()
   {
      string json = Json( root =>
      {
         AddNotHeldRow( root );
         RowOf( root, "qps8", "redis" )["note"] = REAL_NOTE;
      } );
      string text = Visible( Header( json ) );

      Assert.DoesNotMatch( TIE_WORDS, text );
      Assert.Contains( BenchLegends.L_HEADER_WITHIN, text );
   }

   /// <summary>
   /// The first sentence names the data set (with the page's spelling), its size and the number and kind of the queries, each as the file's data line
   /// records it, and the queries' file as the file's recorded query source gives it. It does not call the questions anything the file does not say.
   /// </summary>
   [Fact]
   public void TheFirstSentence_SaysWhatTheNumbersAreOf_AsTheFileRecordsIt()
   {
      string header = Header( Json() );
      string lead = Flat( Regex.Match( header, "<p class=\"bench-header-lead\">(.*?)</p>", RegexOptions.Singleline ).Groups[1].Value );

      Assert.Equal( "These benchmarks were taken from the eShopOnWeb data set (524 vectors of 1024 dimensions) and from the 20 golden questions, which ran the speed test.", lead );
      Assert.Contains( $"<code>{QUESTION_FILE}</code>", header );
      Assert.Contains( "queries are read from", header );
      Assert.DoesNotMatch( new Regex( "search log|real questions|real user", RegexOptions.IgnoreCase ), Visible( header ) );
   }

   /// <summary>
   /// A set whose queries the file records as random stored vectors is not called golden questions.
   /// </summary>
   [Fact]
   public void TheQueryKind_FollowsTheFile()
   {
      string json = Json( root =>
      {
         root["queries"] = new JsonObject { ["recorded"] = "random: 200 random stored vectors from " + QUESTION_FILE };
         SubtitleData( root )["sources"] = DataSources( DATASET, "524", "1024", "200" );
      } );
      string lead = Flat( Regex.Match( Header( json ), "<p class=\"bench-header-lead\">(.*?)</p>", RegexOptions.Singleline ).Groups[1].Value );

      Assert.Contains( "and from the 200 random stored vectors, which ran the speed test.", lead );
      Assert.DoesNotContain( "golden", lead );
   }

   /// <summary>
   /// The line under the heading says how many runs the searches per second are the median of (the file's sessions count them), that the ms per search is
   /// worked out and is not a timed latency, and where each difference is taken from; each of these once.
   /// </summary>
   [Fact]
   public void TheNote_SaysWhatTheFiguresAre_Once()
   {
      string json = Json();
      string header = Header( json );
      string note = Flat( Regex.Match( header, "<p class=\"bench-header-note muted\">(.*?)</p>", RegexOptions.Singleline ).Groups[1].Value );

      Assert.Equal( $"{BenchLegends.L_HEADER_ORDER} {BenchLegends.L_HEADER_MEDIAN_A} 6 {BenchLegends.L_HEADER_MEDIAN_B} {BenchLegends.L_HEADER_MS} {BenchLegends.L_HEADER_DIFF}", note );
      Assert.Single( Regex.Matches( header, Regex.Escape( BenchLegends.L_HEADER_MS ) ) );
      Assert.Contains( $"{BenchLegends.L_HEADER_MULTI_A} <span class=\"bench-header-searchers\">8</span> {BenchLegends.L_HEADER_MULTI_B}", header );
   }

   /// <summary>
   /// Every number on the header, outside the closed lists of sources, is the file's or worked out from it: the sizes and the query count are in the file
   /// as numbers, the searchers come from the table's name, the runs are the sessions' runs, and the figures of a row are the ones this test works out. The
   /// strings that carry digits and are copied from the file or the folder name (the build, the folder, the query file) are taken out first.
   /// </summary>
   [Fact]
   public void EveryNumberOnTheHeader_IsInTheFile_OrWorkedOutFromIt()
   {
      string json = Json( root =>
      {
         AddNotHeldRow( root );
         RowOf( root, "qps8", "redis" )["note"] = REAL_NOTE;
      } );
      string header = Header( json );
      Assert.Contains( "bench-header-lead", header );
      Assert.Equal( RankedOf( json ).Count + 1, AllRows( header ).Count );
      string text = Visible( Regex.Replace( header, "<details.*?</details>", string.Empty, RegexOptions.Singleline ) );
      List<Expected> expected = RankedOf( json );
      string[] copiedFromTheFile = new[] { FOLDER, COMMIT_V8, QUESTION_FILE, "redis holds its data in memory", REAL_NOTE["redis holds its data in memory: ".Length..] }.Concat( expected.Select( e => e.Display ) ).ToArray();
      foreach( string copied in copiedFromTheFile.OrderByDescending( c => c.Length ) )
      {
         text = text.Replace( copied, " " );
      }

      text = Regex.Replace( text, @"\bv\d+\b", " " );
      var allowed = new HashSet<string>( StringComparer.Ordinal ) { "524", "1024", "20", "8", "6" };
      for( int i = 0; i < expected.Count; i++ )
      {
         allowed.Add( Math.Round( expected[i].Qps, MidpointRounding.AwayFromZero ).ToString( "N0", CultureInfo.InvariantCulture ) );
         allowed.Add( ( 1000.0 / expected[i].Qps ).ToString( "0.000", CultureInfo.InvariantCulture ) );
         if( i > 0 )
         {
            allowed.Add( ( 1000.0 / expected[i].Qps - 1000.0 / expected[i - 1].Qps ).ToString( "0.000", CultureInfo.InvariantCulture ) );
         }
      }

      string[] stray = NUMBER.Matches( text ).Select( m => m.Value ).Where( n => !allowed.Contains( n ) ).ToArray();
      Assert.Empty( stray );
      foreach( string fromFile in new[] { "524", "1024", "20" } )
      {
         Assert.Contains( fromFile, json );
      }

      Assert.Equal( 6, JsonNode.Parse( json )!["sessions"]!.AsArray().Sum( s => s!["runs"]!.AsArray().Count ) );
      Assert.Contains( "\"qps8\"", json );
   }

   /// <summary>
   /// No sentence of seven words or more on the header is typed into the page: each is a fixed legend (all of which the legend tests walk), a string of the
   /// file, or a legend followed or preceded by a string of the file. The numbers and names sit in elements of their own, so a sentence is a legend and a
   /// value side by side and not a new claim.
   /// </summary>
   [Fact]
   public void NoLongSentenceOfTheHeader_IsTyped_EachIsALegendOrTheFiles()
   {
      string json = Json( root =>
      {
         AddNotHeldRow( root );
         RowOf( root, "qps8", "redis" )["note"] = REAL_NOTE;
      } );

      Assert.Contains( "bench-header-lead", Header( json ) );
      Assert.Contains( "bench-header-row", Header( json ) );
      Assert.Empty( Unexplained( Header( json ), json ) );
      string typed = "Redis is the best choice for every search workload there is";
      Assert.Equal( new[] { typed }, Unexplained( Header( json ) + $"<p>{typed}</p>", json ) );
   }

   /// <summary>
   /// The header's fixed strings are in the legend list that the legend tests walk (no number, no engine name, no banned word, short, plain), so none of them
   /// can go unwalked.
   /// </summary>
   [Fact]
   public void EveryFixedStringOfTheHeader_IsInTheLegendList()
   {
      string[] names = typeof( BenchLegends ).GetFields( BindingFlags.Public | BindingFlags.Static ).Where( f => f.IsLiteral && f.Name.StartsWith( "L_HEADER_", StringComparison.Ordinal ) ).Select( f => f.Name ).ToArray();

      Assert.True( names.Length >= 30, $"only {names.Length} header strings" );
      Assert.All( names, n => Assert.Contains( n, BenchLegends.All.Keys ) );
   }

   /// <summary>
   /// The build that measured the runs and the set are named in small text: each session's commit as the file records it, "not recorded" for a session whose
   /// runs record none, and the folder the numbers come from.
   /// </summary>
   [Fact]
   public void TheBuildAndTheSet_AreNamedInSmallText()
   {
      string header = Header( Json() );
      string small = Flat( Regex.Match( header, "<p class=\"bench-header-build muted\"><small>(.*?)</small></p>", RegexOptions.Singleline ).Groups[1].Value );

      Assert.Equal( $"{BenchLegends.L_HEADER_BUILD} v7 {BenchLegends.L_HEADER_NOT_RECORDED}, v8 {COMMIT_V8}. {BenchLegends.L_HEADER_SET} {FOLDER}.", small );
   }

   /// <summary>
   /// One line links down to the full results that are already on the page, and the anchor it points at is on the page once, after the header and before the
   /// page's own heading.
   /// </summary>
   [Fact]
   public void OneLink_GoesDownToTheFullResults()
   {
      string json = Json();
      string page = Page( json );
      string header = HeaderOf( page );

      Assert.Single( Regex.Matches( header, "<a href=\"#bench-full-results\">" ) );
      Assert.Contains( $"<a href=\"#bench-full-results\">{BenchLegends.L_HEADER_FULL}</a>", header );
      Assert.Single( Regex.Matches( page, "id=\"bench-full-results\"" ) );
      Assert.True( page.IndexOf( "id=\"bench-full-results\"", StringComparison.Ordinal ) < page.IndexOf( "<h1>Vector search benchmark</h1>", StringComparison.Ordinal ) );
   }

   /// <summary>
   /// The header is the first thing in the page's main block, above the page's own heading, and what was on the page before it follows unchanged: the heading,
   /// the line naming the folder, the headline, the first table and the list of all runs, in that order.
   /// </summary>
   [Fact]
   public void TheHeader_IsAboveEverythingTheSummaryPageHad_AndTheRestFollowsUnchanged()
   {
      WriteSet( FOLDER, Json() );
      string page = BenchResultsEndpoints.ListPageHtml( _root );
      string header = HeaderOf( page );
      string rest = page.Replace( header, string.Empty );

      Assert.Contains( "<main class=\"card bench\"><h1>Vector search benchmark</h1><p class=\"bench-source muted\">Numbers from <a href=\"/bench-results/published-2026-10-08\">published-2026-10-08</a>.</p>", rest );
      Assert.True( page.IndexOf( "<main class=\"card bench\"><section class=\"bench-header\"", StringComparison.Ordinal ) > 0 );
      int lead = rest.IndexOf( "<p class=\"bench-lead\">", StringComparison.Ordinal );
      int table = rest.IndexOf( "<table class=\"bench-table\">", StringComparison.Ordinal );
      int runs = rest.IndexOf( "<h2>All runs</h2>", StringComparison.Ordinal );
      Assert.True( lead > 0 && table > lead && runs > table );
   }

   /// <summary>
   /// A page with no published folder has no header.
   /// </summary>
   [Fact]
   public void ThereIsNoHeader_WithoutAPublishedFolder()
   {
      Assert.DoesNotContain( "bench-header", BenchResultsEndpoints.ListPageHtml( _root ) );
   }

   /// <summary>
   /// A published file that cannot be read shows the error where the tables would be, and no header: bad JSON, a file the reader refuses, and a shape nobody
   /// knows.
   /// </summary>
   [Theory]
   [InlineData( "{ not json" )]
   [InlineData( "{\"a\": 1}" )]
   [InlineData( "{\"metrics\": [], \"sessions\": [], \"sentences\": []}" )]
   public void ThereIsNoHeader_WhenTheFileCannotBeRead( string json )
   {
      WriteSet( FOLDER, json );
      string page = BenchResultsEndpoints.ListPageHtml( _root );

      Assert.Contains( $"<p class=\"errors\">{BenchLegends.UNREADABLE}:", page );
      Assert.DoesNotContain( "bench-header", page );
   }

   /// <summary>
   /// A set the file marks stopped gets a header that says so and shows no order and no figure.
   /// </summary>
   [Fact]
   public void AStoppedSet_GetsTheStoppedNotice_AndNoOrder()
   {
      string json = Json( root =>
      {
         root["guards"] = new JsonObject { ["g2"] = new JsonObject { ["stopped"] = true, ["pairs"] = new JsonArray( "mariadb over pgvector on p50" ) }, ["g3"] = new JsonObject { ["targets"] = new JsonArray() } };
         ( (JsonArray)root["sentences"]! ).RemoveAt( 0 );
      } );
      string header = Header( json );

      Assert.Contains( BenchLegends.L_HEADER_STOPPED, header );
      Assert.DoesNotContain( "bench-header-row", header );
      Assert.DoesNotContain( "<table", header );
   }

   /// <summary>
   /// A file with no qps8 table gets a notice that names the table, and no order.
   /// </summary>
   [Fact]
   public void AFileWithoutTheTable_GetsANotice_AndNoOrder()
   {
      string json = Json( root =>
      {
         JsonArray metrics = (JsonArray)root["metrics"]!;
         metrics.Remove( metrics.First( m => m!["metric"]!.GetValue<string>() == "qps8" ) );
      } );
      string header = Header( json );

      Assert.Contains( $"{BenchLegends.L_HEADER_NO_TABLE_A} <code>qps8</code> {BenchLegends.L_HEADER_NO_TABLE_B}", header );
      Assert.DoesNotContain( "bench-header-row", header );
   }

   /// <summary>
   /// A table whose rows are all unranked gets the no-ranked-row notice, and the rows are listed under the not-ranked group with their label; a row of one
   /// session says so.
   /// </summary>
   [Fact]
   public void ATableWithNoRankedRow_GetsANotice_AndListsTheRowsAsNotRanked()
   {
      string json = Json( root =>
      {
         foreach( JsonNode? row in (JsonArray)( (JsonArray)root["metrics"]! ).First( m => m!["metric"]!.GetValue<string>() == "qps8" )!["rows"]! )
         {
            row!["status"] = "one-session";
         }
      } );
      string header = Header( json );
      List<Shown> rows = AllRows( header );

      Assert.Contains( BenchLegends.L_HEADER_NO_RANKED, header );
      Assert.Equal( 6, rows.Count );
      Assert.All( rows, r => Assert.Equal( BenchLegends.L_HEADER_ONE_SESSION, r.Cells[1] ) );
   }

   /// <summary>
   /// A file with no data line still gets its order, and the header says the data set and the queries are not named, where it would have named them.
   /// </summary>
   [Fact]
   public void AFileWithoutADataLine_StillGetsTheOrder_AndSaysItNamesNoDataSet()
   {
      string json = Json( root => ( (JsonArray)root["sentences"]! ).Remove( ( (JsonArray)root["sentences"]! ).First( s => s!["slot"]!.GetValue<string>() == "subtitle.data" ) ) );
      string header = Header( json );

      Assert.Contains( BenchLegends.L_HEADER_NO_DATA, header );
      Assert.DoesNotContain( "These benchmarks were taken from", header );
      Assert.Equal( RankedOf( json ).Count, RankedRows( header ).Count );
   }

   /// <summary>
   /// A median that is not a positive number cannot give a ms per search; the header says so, with the engine and the table, instead of printing infinity.
   /// </summary>
   [Fact]
   public void ANonPositiveMedian_IsReportedLoudly_NotPrintedAsInfinity()
   {
      string json = Json( root => RowOf( root, "qps8", "sql" )["median"] = 0 );
      string header = BenchHeader.Html( json, FOLDER );

      Assert.Contains( BenchLegends.L_HEADER_ERROR, header );
      Assert.Contains( "sql", header );
      Assert.Contains( "qps8", header );
      Assert.DoesNotContain( "Infinity", header );
      Assert.DoesNotContain( "bench-header-row", header );
   }

   /// <summary>
   /// Names and notes from the file are encoded, so a name with markup in it cannot add markup to the page.
   /// </summary>
   [Fact]
   public void NamesAndNotes_AreEncoded()
   {
      string json = Json( root =>
      {
         RowOf( root, "qps8", "mariadb" )["display"] = "<b>x</b> & y";
         RowOf( root, "qps8", "redis" )["note"] = "<script>alert(1)</script> holds its data in memory";
      } );
      string header = Header( json );

      Assert.DoesNotContain( "<b>x</b>", header );
      Assert.DoesNotContain( "<script>", header );
      Assert.Contains( "&lt;b&gt;x&lt;/b&gt; &amp; y", header );
      Assert.Contains( "&lt;script&gt;alert(1)&lt;/script&gt; holds its data in memory", header );
   }

   /// <summary>
   /// The data set's name is spelled as the run list spells it, and a name the table does not know is printed as it is recorded.
   /// </summary>
   [Theory]
   [InlineData( "eshoponweb", "eShopOnWeb" )]
   [InlineData( "ESHOPONWEB", "eShopOnWeb" )]
   [InlineData( "adventureworks", "AdventureWorks" )]
   [InlineData( "somethingelse", "somethingelse" )]
   public void ThePipelineName_IsSpelledOneWay( string recorded, string expected )
   {
      Assert.Equal( expected, BenchRunList.PipelineName( recorded ) );
   }

   /// <summary>
   /// The real set (when this machine holds it): the header reads what the consolidate command wrote. The engines are in the order of the file's qps8 medians
   /// with ClickHouse last and labelled, Redis carries the file's own note as a bold label with its sources, the figures are worked out from the file, and the
   /// markers are where the file's notSeparatedFrom names the row above.
   /// </summary>
   [FactIfPathExists( REAL_SET )]
   public void TheRealSet_GivesTheOrderTheFileHolds()
   {
      string json = File.ReadAllText( REAL_SET );
      List<Expected> expected = RankedOf( json );
      string header = BenchHeader.Html( json, FOLDER );
      List<Shown> shown = RankedRows( header );
      List<Shown> all = AllRows( header );

      Assert.Equal( 18, expected.Count );
      Assert.Equal( expected.Select( e => e.Target ), shown.Select( s => s.Engine ) );
      Assert.Equal( "clickhouse", all[^1].Engine );
      Assert.Equal( BenchLegends.L_NOT_HELD, all[^1].Cells[1] );
      Assert.Equal( 2, all[^1].Cells.Count );
      for( int i = 0; i < expected.Count; i++ )
      {
         Assert.Equal( Math.Round( expected[i].Qps, MidpointRounding.AwayFromZero ).ToString( "N0", CultureInfo.InvariantCulture ), shown[i].Cells[1] );
         Assert.Equal( ( 1000.0 / expected[i].Qps ).ToString( "0.000", CultureInfo.InvariantCulture ), shown[i].Cells[2] );
         Assert.Equal( i > 0 && expected[i].NotSeparatedFrom.Contains( expected[i - 1].Target ), shown[i].Html.Contains( "data-mark=\"within\"", StringComparison.Ordinal ) );
      }

      Shown redis = shown.Single( s => s.Engine == "redis" );
      Assert.Contains( "<strong class=\"bench-header-memory\">redis holds its data in memory</strong>", redis.Html );
      Assert.Contains( "redis-faq-2026-10-07.html", redis.Html );
      Assert.Single( Regex.Matches( header, "bench-header-memory\">" ) );
      Assert.Contains( "<span class=\"bench-header-searchers\">8</span>", header );
      Assert.Contains( ">20</span>", header );
      Assert.Contains( QUESTION_FILE, header );
      Assert.Contains( "9b924200abc3", header );
      Assert.DoesNotMatch( TIE_WORDS, Visible( header ) );
      Assert.Empty( Unexplained( header, json ) );
   }

   #endregion Public Methods

   #region Private Methods

   /// <summary>
   /// The v8 fixture with the facts the header reads that the fixture lacks (a data line, the recorded queries, the builds), after an optional change.
   /// </summary>
   /// <param name="change">Edits the root after the facts are added, or null.</param>
   /// <returns>The file text.</returns>
   private static string Json( Action<JsonObject>? change = null )
   {
      return BenchResultsV8Fixture.Json( root =>
      {
         ( (JsonArray)root["sentences"]! ).Add( new JsonObject { ["slot"] = "subtitle.data", ["text"] = $"Data: {DATASET}, 524 vectors of 1024 dimensions; 20 queries, top 10 hits each.", ["sources"] = DataSources( DATASET, "524", "1024", "20" ) } );
         root["queries"] = new JsonObject { ["recorded"] = RECORDED, ["copyPath"] = "design/bench-inputs/questions_golden.json" };
         root["build"] = new JsonObject
         {
            ["measured"] = new JsonArray(
               new JsonObject { ["session"] = "v7", ["commit"] = null, ["commitShort"] = null },
               new JsonObject { ["session"] = "v8", ["commit"] = COMMIT_V8 + "0000000000000000000000000000", ["commitShort"] = COMMIT_V8 } ),
         };
         change?.Invoke( root );
      } );
   }

   /// <summary>
   /// The sources of a data line, in the shape the consolidate command writes them.
   /// </summary>
   /// <param name="pipeline">The data set.</param>
   /// <param name="rows">The vector count.</param>
   /// <param name="dimension">The dimension count.</param>
   /// <param name="queries">The query count.</param>
   /// <returns>The array.</returns>
   private static JsonArray DataSources( string pipeline, string rows, string dimension, string queries )
   {
      return new JsonArray(
         new JsonObject { ["kind"] = "results", ["ref"] = "results:pipeline", ["value"] = pipeline },
         new JsonObject { ["kind"] = "results", ["ref"] = "results:rows", ["value"] = rows },
         new JsonObject { ["kind"] = "results", ["ref"] = "results:dimension", ["value"] = dimension },
         new JsonObject { ["kind"] = "results", ["ref"] = "results:queryCount", ["value"] = queries },
         new JsonObject { ["kind"] = "results", ["ref"] = "results:top", ["value"] = "10" } );
   }

   /// <summary>
   /// The data-line sentence of a file.
   /// </summary>
   private static JsonObject SubtitleData( JsonObject root )
   {
      return (JsonObject)( (JsonArray)root["sentences"]! ).First( s => s!["slot"]!.GetValue<string>() == "subtitle.data" )!;
   }

   /// <summary>
   /// Adds a row for an engine whose timed eight-searcher pass the tool recorded as not held, to the qps8 table, as the real set has for ClickHouse.
   /// </summary>
   private static void AddNotHeldRow( JsonObject root )
   {
      JsonObject Range( double v ) => new() { ["min"] = v * 0.9, ["max"] = v * 1.1, ["runs"] = new JsonArray( v * 0.9, v, v * 1.1 ) };
      var row = new JsonObject
      {
         ["target"] = "clickhouse",
         ["display"] = "clickhouse",
         ["status"] = "not-held",
         ["perSession"] = new JsonObject { ["v7"] = Range( NOT_HELD_MEDIAN ), ["v8"] = Range( NOT_HELD_MEDIAN ) },
         ["median"] = NOT_HELD_MEDIAN,
         ["notSeparatedFrom"] = new JsonArray(),
         ["flags"] = new JsonArray( new JsonObject { ["code"] = "not-held", ["text"] = "Run x: the timed eight-searcher pass of clickhouse read 336 QPS and the run recorded it as NOT HELD." } ),
         ["searchMode"] = "approximate",
      };
      ( (JsonArray)MetricOf( root, "qps8" )["rows"]! ).Add( row );
   }

   /// <summary>
   /// One metric table of the file.
   /// </summary>
   private static JsonObject MetricOf( JsonObject root, string metric )
   {
      return (JsonObject)( (JsonArray)root["metrics"]! ).First( m => m!["metric"]!.GetValue<string>() == metric )!;
   }

   /// <summary>
   /// One engine's row of one metric table.
   /// </summary>
   private static JsonObject RowOf( JsonObject root, string metric, string target )
   {
      return (JsonObject)( (JsonArray)MetricOf( root, metric )["rows"]! ).First( r => r!["target"]!.GetValue<string>() == target )!;
   }

   /// <summary>
   /// The ranked rows of the qps8 table as the file's text gives them, most searches per second first. Read from the text, not from the page's model, so the
   /// header's order is held to the file and not to itself.
   /// </summary>
   private static List<Expected> RankedOf( string json )
   {
      JsonNode root = JsonNode.Parse( json )!;
      JsonNode table = ( (JsonArray)root["metrics"]! ).First( m => m!["metric"]!.GetValue<string>() == "qps8" )!;
      return ( (JsonArray)table["rows"]! ).Select( r => r!.AsObject() ).Where( r => r["status"]!.GetValue<string>() == "ranked" )
         .Select( r => new Expected( r["target"]!.GetValue<string>(), r["display"]!.GetValue<string>(), r["median"]!.GetValue<double>(),
            ( (JsonArray)r["notSeparatedFrom"]! ).Select( n => n!.GetValue<string>() ).ToArray(), r["note"]?.GetValue<string>() ) )
         .OrderByDescending( e => e.Qps ).ToList();
   }

   /// <summary>
   /// The header for a file, as the page builds it.
   /// </summary>
   private static string Header( string json )
   {
      return BenchHeader.Html( json, FOLDER );
   }

   /// <summary>
   /// The page for a file, served as the summary of a published folder.
   /// </summary>
   private string Page( string json )
   {
      WriteSet( FOLDER, json );
      return BenchResultsEndpoints.ListPageHtml( _root );
   }

   /// <summary>
   /// The header section of a page.
   /// </summary>
   private static string HeaderOf( string page )
   {
      Match match = Regex.Match( page, "<section class=\"bench-header\".*?</section>", RegexOptions.Singleline );
      Assert.True( match.Success, "no header section on the page" );
      return match.Value;
   }

   /// <summary>
   /// Writes a consolidated folder holding a file.
   /// </summary>
   private void WriteSet( string name, string json )
   {
      string folder = Path.Combine( _root, name );
      Directory.CreateDirectory( folder );
      File.WriteAllText( Path.Combine( folder, "consolidated.json" ), json );
   }

   /// <summary>
   /// The rows of the header's table that carry a figure, in page order.
   /// </summary>
   private static List<Shown> RankedRows( string header )
   {
      return AllRows( header ).Where( s => s.State.Length == 0 ).ToList();
   }

   /// <summary>
   /// Every engine row of the header's table, in page order: the ranked rows, then the rows that are not ranked. A row's html includes the note row under it.
   /// </summary>
   private static List<Shown> AllRows( string header )
   {
      return Regex.Matches( header, "<tr class=\"bench-header-row\" data-engine=\"([^\"]*)\"(?: data-state=\"([^\"]*)\")?>(.*?)</tr>(<tr class=\"bench-header-note-row\"[^>]*>.*?</tr>)?", RegexOptions.Singleline )
         .Select( m => new Shown( WebUtility.HtmlDecode( m.Groups[1].Value ), m.Groups[2].Value, m.Groups[3].Value + m.Groups[4].Value,
            Regex.Matches( m.Groups[3].Value, "<td[^>]*>(.*?)</td>", RegexOptions.Singleline ).Select( c => Visible( c.Groups[1].Value ) ).ToList() ) ).ToList();
   }

   /// <summary>
   /// The text a reader sees: tags removed, entities decoded, white space collapsed.
   /// </summary>
   private static string Visible( string html )
   {
      return Regex.Replace( WebUtility.HtmlDecode( Regex.Replace( html, "<[^>]+>", " " ) ), @"\s+", " " ).Trim();
   }

   /// <summary>
   /// The text of an element as one run: markup removed without putting a space where a tag was, entities decoded.
   /// </summary>
   private static string Flat( string html )
   {
      return WebUtility.HtmlDecode( Regex.Replace( html, "<[^>]+>", string.Empty ) );
   }

   /// <summary>
   /// The path of a file of the repository, found from this source file's own place.
   /// </summary>
   private static string SourcePath( string first, string second, string third, string fourth, [CallerFilePath] string here = "" )
   {
      return Path.GetFullPath( Path.Combine( Path.GetDirectoryName( here )!, "..", "..", "..", first, second, third, fourth ) );
   }

   /// <summary>
   /// The sentences of a page of seven words or more that no source explains: a text node is cut into sentences, and a sentence is explained when it is a part
   /// of a string of the file, a part of a fixed legend, or a legend followed or preceded by a part of a string of the file.
   /// </summary>
   private static List<string> Unexplained( string html, string json )
   {
      var strings = new List<string>();
      Collect( JsonDocument.Parse( json ).RootElement, strings );
      string corpus = string.Join( "\n", strings.Select( t => Regex.Replace( t, @"\s+", " " ) ) );
      string[] legends = BenchLegends.All.Values.Where( l => l.Length >= 12 ).ToArray();
      var found = new List<string>();
      foreach( Match match in Regex.Matches( html, ">([^<>]+)<" ) )
      {
         string node = Regex.Replace( WebUtility.HtmlDecode( match.Groups[1].Value ), @"\s+", " " ).Trim();
         foreach( string sentence in Regex.Split( node, @"(?<=[.!?]) (?=[A-Z])" ) )
         {
            if( !found.Contains( sentence ) && !Explained( sentence, corpus, legends ) )
            {
               found.Add( sentence );
            }
         }
      }

      return found;
   }

   /// <summary>
   /// True when a sentence is short, or the file or a legend explains it.
   /// </summary>
   private static bool Explained( string sentence, string corpus, string[] legends )
   {
      string core = Regex.Replace( sentence.Trim(), "^(= |; )", string.Empty ).Trim();
      if( core.Split( ' ', StringSplitOptions.RemoveEmptyEntries ).Length < 7 || corpus.Contains( core, StringComparison.Ordinal ) || legends.Any( l => l.Contains( core, StringComparison.Ordinal ) ) )
      {
         return true;
      }

      foreach( string legend in legends )
      {
         if( ( core.StartsWith( legend, StringComparison.Ordinal ) && Explained( core[legend.Length..].Trim(), corpus, legends ) )
            || ( core.EndsWith( legend, StringComparison.Ordinal ) && Explained( core[..^legend.Length].Trim(), corpus, legends ) ) )
         {
            return true;
         }
      }

      return false;
   }

   /// <summary>
   /// Adds every property name and string of a JSON value to a list.
   /// </summary>
   private static void Collect( JsonElement value, List<string> into )
   {
      switch( value.ValueKind )
      {
         case JsonValueKind.String: into.Add( value.GetString()! ); break;
         case JsonValueKind.Array: value.EnumerateArray().ToList().ForEach( e => Collect( e, into ) ); break;
         case JsonValueKind.Object: value.EnumerateObject().ToList().ForEach( p => { into.Add( p.Name ); Collect( p.Value, into ); } ); break;
      }
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
