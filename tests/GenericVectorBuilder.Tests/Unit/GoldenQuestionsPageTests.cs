using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using GenericVectorBuilder.Web.BenchPages;
using GenericVectorBuilder.Web.Endpoints;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace GenericVectorBuilder.Tests.Unit;

/// <summary>
/// The Golden Questions page at /benchmark/golden-questions, and the link to it from the words "golden questions" on the Vector search benchmark page.
/// What these tests hold the page to: it lists exactly the questions of the file the published set's runs recorded, in the file's order, each with its type
/// and the files a correct answer should return; it carries the same top bar and stylesheet as the benchmark page and links back to it; it prints the file's
/// path in small text; it prints a note only when the note is short and not the labelling record; and it refuses with an error notice, never with an empty
/// table, when the file is missing, unreadable, malformed, empty, not the file the runs read (by its hash) or holds another number of questions than the runs
/// ran. The benchmark page links its words "golden questions" to the page, and the link is served.
/// Why the questions file is written by the test: the page reads whatever file the set names, so a test names a file of its own and can change one thing in it.
/// Why one test reads the real file as well: the written file shows the page follows the file, and only the real file shows that the page reads the file the
/// real set records.
/// </summary>
public sealed class GoldenQuestionsPageTests : IDisposable
{
   #region Data Members

   private const string FOLDER = "published-2026-10-08";
   private const string ADDRESS = "/benchmark/golden-questions";
   private const string REAL_SET = "/home/dan/ForClaude/GenericVectorBuilder/bench-results/published-2026-10-08/consolidated.json";
   private const string REAL_QUESTIONS = "/home/dan/ForClaude/evalkit/questions_golden.json";
   private const string INTERNAL_NOTE = "Labelled 2026-10-02: blind code reads (sonnet, opus) + judge over 252 pooled results from 20 stored runs + author's recorded answer. Grade = median of graders; only grades 2-3 listed.";
   private const string PLAIN_NOTE = "Asked by the support desk.";
   private const int QUESTION_COUNT = 20;
   private const int SERVER_SECONDS = 60;
   private const int REQUEST_SECONDS = 15;

   private static readonly Regex ROW = new( "<tr class=\"bench-golden-row\" data-id=\"([^\"]*)\"><td class=\"n\">(\\d+)</td><td class=\"bench-golden-question\">(.*?)</td><td class=\"bench-golden-type\" data-label=\"Type\">(.*?)</td><td class=\"bench-golden-filecell\" data-label=\"Files a correct answer should return\"><ul class=\"bench-golden-files\">(.*?)</ul></td></tr>", RegexOptions.Singleline | RegexOptions.Compiled );
   private static readonly Regex FILE_ITEM = new( "<li><code>(.*?)</code></li>", RegexOptions.Singleline | RegexOptions.Compiled );
   private static readonly Regex TOP_BAR = new( "<header class=\"topbar\">.*?</header>", RegexOptions.Singleline | RegexOptions.Compiled );
   private static readonly Regex TITLE = new( "<title>.*?</title>", RegexOptions.Singleline | RegexOptions.Compiled );

   private readonly string _root;
   private readonly string _questions;

   /// <summary>One question as a test expects to see it.</summary>
   private sealed record Expected( string Id, string Question, string Category, string[] Files );

   #endregion Data Members

   #region Constructor

   /// <summary>
   /// Creates an empty results root and a path for the questions file under the test output folder.
   /// </summary>
   public GoldenQuestionsPageTests()
   {
      string folder = Path.Combine( AppContext.BaseDirectory, "golden-questions-tests", Guid.NewGuid().ToString( "N" ) );
      _root = Path.Combine( folder, "results" );
      _questions = Path.Combine( folder, "questions_golden.json" );
      Directory.CreateDirectory( _root );
   }

   #endregion Constructor

   #region Public Methods

   /// <summary>
   /// The route is the address the benchmark page links to, and the page's name is "Golden Questions".
   /// </summary>
   [Fact]
   public void TheRoute_IsTheAddressTheBenchmarkPageLinksTo()
   {
      Assert.Equal( "/benchmark/golden-questions", BenchRoutes.GOLDEN_QUESTIONS );
      Assert.Equal( "Golden Questions", BenchRoutes.GOLDEN_QUESTIONS_NAME );
   }

   /// <summary>
   /// The page answers 200 with an HTML page titled and headed "Golden Questions", at the address with and without a trailing slash, and the page the route
   /// serves is the page the builder draws.
   /// </summary>
   [Fact]
   public async Task TheRoute_ServesThePage_WithAndWithoutATrailingSlash()
   {
      WriteQuestions( Questions() );
      WriteSet( withHash: true );
      await WithServerAsync( async client =>
      {
         foreach( string address in new[] { ADDRESS, ADDRESS + "/" } )
         {
            using HttpResponseMessage response = await client.GetAsync( address );
            string body = await response.Content.ReadAsStringAsync();

            Assert.Equal( HttpStatusCode.OK, response.StatusCode );
            Assert.Equal( "text/html; charset=utf-8", response.Content.Headers.ContentType?.ToString() );
            Assert.Contains( "<title>Golden Questions</title>", body );
            Assert.Contains( "<h1>Golden Questions</h1>", body );
            Assert.Equal( BenchResultsEndpoints.GoldenQuestionsPageHtml( _root ), body );
         }
      } );
   }

   /// <summary>
   /// The page lists exactly the file's questions, in the file's order, each with its number, its text, its type (the file's Category) and the files a correct
   /// answer should return (the file's Relevant, in the file's order), and the first sentence gives the number of questions read.
   /// </summary>
   [Fact]
   public void ThePage_ListsExactlyTheFilesQuestions_InFileOrder()
   {
      JsonArray items = Questions();
      WriteQuestions( items );
      WriteSet( withHash: true );
      string page = BenchResultsEndpoints.GoldenQuestionsPageHtml( _root );

      List<Expected> expected = ExpectedOf( items );
      List<( string Id, int Number, string Question, string Category, string[] Files )> shown = Rows( page );

      Assert.Equal( QUESTION_COUNT, expected.Count );
      Assert.Equal( QUESTION_COUNT, shown.Count );
      for( int i = 0; i < expected.Count; i++ )
      {
         Assert.Equal( expected[i].Id, shown[i].Id );
         Assert.Equal( i + 1, shown[i].Number );
         Assert.Equal( expected[i].Question, shown[i].Question );
         Assert.Equal( expected[i].Category, shown[i].Category );
         Assert.Equal( expected[i].Files, shown[i].Files );
      }

      Assert.Contains( $"<p class=\"bench-golden-lead\">These are the <span class=\"bench-golden-count\">{QUESTION_COUNT}</span> questions every engine answered in the speed test.</p>", page );
      Assert.Contains( "<th class=\"n\">#</th><th>Question</th><th>Type</th><th>Files a correct answer should return</th>", page );
      Assert.Single( Regex.Matches( page, "<table" ) );
   }

   /// <summary>
   /// The page follows the file and not a list of its own: the same set with a file of three other questions shows those three, in that order.
   /// </summary>
   [Fact]
   public void ThePage_FollowsTheFile_NotAListOfItsOwn()
   {
      var items = new JsonArray(
         Item( "z-last", "zebra crossing", "identifier", INTERNAL_NOTE, "src/Z.cs" ),
         Item( "a-first", "how does apple pie get made", "conceptual", INTERNAL_NOTE, "src/A.cs", "src/B.cs" ),
         Item( "m-mid", "Middle", "identifier", INTERNAL_NOTE, "src/M.cs" ) );
      WriteQuestions( items );
      WriteSet( withHash: true, count: "3" );
      List<( string Id, int Number, string Question, string Category, string[] Files )> shown = Rows( BenchResultsEndpoints.GoldenQuestionsPageHtml( _root ) );

      Assert.Equal( new[] { "z-last", "a-first", "m-mid" }, shown.Select( s => s.Id ) );
      Assert.Equal( new[] { "zebra crossing", "how does apple pie get made", "Middle" }, shown.Select( s => s.Question ) );
      Assert.Equal( new[] { "src/A.cs", "src/B.cs" }, shown[1].Files );
   }

   /// <summary>
   /// The text of the file reaches the page encoded: a question and a path with markup characters are printed as text, never as markup.
   /// </summary>
   [Fact]
   public void ThePage_EncodesTheFilesText()
   {
      var items = new JsonArray( Item( "x", "what does <b>bold</b> & \"quoted\" do", "conceptual", INTERNAL_NOTE, "src/A&B/<x>.cs" ) );
      WriteQuestions( items );
      WriteSet( withHash: true, count: "1" );
      string page = BenchResultsEndpoints.GoldenQuestionsPageHtml( _root );

      Assert.DoesNotContain( "<b>bold</b>", page );
      Assert.DoesNotContain( "<x>", page );
      Assert.Contains( "what does &lt;b&gt;bold&lt;/b&gt; &amp; &quot;quoted&quot; do", page );
      Assert.Equal( "what does <b>bold</b> & \"quoted\" do", Rows( page ).Single().Question );
   }

   /// <summary>
   /// A file that starts with a byte order mark is read like any other.
   /// </summary>
   [Fact]
   public void AFileWithAByteOrderMark_IsRead()
   {
      byte[] json = new UTF8Encoding( true ).GetPreamble().Concat( Encoding.UTF8.GetBytes( Questions().ToJsonString() ) ).ToArray();
      File.WriteAllBytes( _questions, json );
      WriteSet( withHash: true );

      Assert.Equal( QUESTION_COUNT, Rows( BenchResultsEndpoints.GoldenQuestionsPageHtml( _root ) ).Count );
   }

   /// <summary>
   /// The path of the file is printed in small text, in the paragraph the benchmark page uses for the same line, and when the file's hash is the one the runs
   /// recorded the line says so; a set that records no hash prints the path and says nothing of a hash.
   /// </summary>
   [Fact]
   public void ThePage_PrintsTheSourcePath_InSmallText()
   {
      WriteQuestions( Questions() );
      WriteSet( withHash: true );
      string checkedPage = BenchResultsEndpoints.GoldenQuestionsPageHtml( _root );
      WriteSet( withHash: false );
      string uncheckedPage = BenchResultsEndpoints.GoldenQuestionsPageHtml( _root );

      Assert.Contains( $"<p class=\"bench-header-from muted\">The questions are read from <code>{_questions}</code>. Its hash is the one the runs recorded.</p>", checkedPage );
      Assert.Contains( $"<p class=\"bench-header-from muted\">The questions are read from <code>{_questions}</code>.</p>", uncheckedPage );
      Assert.DoesNotContain( "hash", uncheckedPage );
      Assert.Equal( QUESTION_COUNT, Rows( uncheckedPage ).Count );
   }

   /// <summary>
   /// The page has the same top bar and the same head (viewport, colour scheme and stylesheet) as the benchmark page, and links back to it once in the body.
   /// </summary>
   [Fact]
   public void ThePage_HasTheBenchmarkPagesTopBarAndStyles_AndLinksBack()
   {
      WriteQuestions( Questions() );
      WriteSet( withHash: true );
      string golden = BenchResultsEndpoints.GoldenQuestionsPageHtml( _root );
      string benchmark = BenchResultsEndpoints.BenchmarkPageHtml( _root );

      Assert.Equal( TOP_BAR.Match( benchmark ).Value, TOP_BAR.Match( golden ).Value );
      Assert.Contains( "<a href=\"/benchmark\">Benchmark</a></header>", golden );
      Assert.Equal( HeadOf( benchmark ), HeadOf( golden ) );
      Assert.Contains( "<link rel=\"stylesheet\" href=\"/style.css\">", golden );
      Assert.Contains( "<main class=\"card bench\"><p class=\"bench-golden-back\"><a href=\"/benchmark\">Back to the Vector search benchmark</a></p><h1>Golden Questions</h1>", golden );
      Assert.Single( Regex.Matches( golden, "<a href=\"/benchmark\">Back to the Vector search benchmark</a>" ) );
   }

   /// <summary>
   /// On the benchmark page the words "golden questions" are a link to the Golden Questions page, once, and nothing else in the first sentence changes: the
   /// sentence reads as it did, the link is the only one in it, and the link is served by the route table.
   /// </summary>
   [Fact]
   public async Task TheBenchmarkPage_LinksTheWordsGoldenQuestions_ToThePage()
   {
      WriteQuestions( Questions() );
      WriteSet( withHash: true );
      string benchmark = BenchResultsEndpoints.BenchmarkPageHtml( _root );
      string lead = Regex.Match( benchmark, "<p class=\"bench-header-lead\">(.*?)</p>", RegexOptions.Singleline ).Groups[1].Value;

      Assert.Single( Regex.Matches( benchmark, Regex.Escape( "<strong><a href=\"/benchmark/golden-questions\">golden questions</a></strong>" ) ) );
      Assert.Single( Regex.Matches( lead, "<a " ) );
      Assert.Equal( "These benchmarks were taken from the eShopOnWeb data set (524 vectors of 1024 dimensions) and from the 20 golden questions, which ran the speed test.", WebUtility.HtmlDecode( Regex.Replace( lead, "<[^>]+>", string.Empty ) ) );
      Assert.Equal( 2, Regex.Matches( Regex.Match( benchmark, "<section class=\"bench-header\".*?</section>", RegexOptions.Singleline ).Value, "href=" ).Count );
      Assert.Single( Regex.Matches( benchmark, "href=\"/benchmark/golden-questions\"" ) );
      await WithServerAsync( async client =>
      {
         string page = await client.GetStringAsync( "/benchmark" );
         string href = Regex.Match( page, "<strong><a href=\"([^\"]*)\">golden questions</a></strong>" ).Groups[1].Value;
         string target = await client.GetStringAsync( href );

         Assert.Equal( ADDRESS, href );
         Assert.Contains( "<title>Golden Questions</title>", target );
         Assert.Equal( QUESTION_COUNT, Rows( target ).Count );
      } );
   }

   /// <summary>
   /// A set whose queries are not golden questions links nothing: the words that name them are plain text, and the Golden Questions page says the set records
   /// no file of questions.
   /// </summary>
   [Fact]
   public void ASetOfRandomQueries_HasNoLink_AndThePageSaysThereAreNoQuestions()
   {
      WriteQuestions( Questions() );
      WriteSet( withHash: true, change: root => ( (JsonObject)root["queries"]! )["recorded"] = $"random: 200 random stored vectors from {_questions}" );
      string benchmark = BenchResultsEndpoints.BenchmarkPageHtml( _root );
      string golden = BenchResultsEndpoints.GoldenQuestionsPageHtml( _root );

      Assert.DoesNotContain( "href=\"/benchmark/golden-questions\"", benchmark );
      Assert.Contains( "<strong>random stored vectors</strong>", benchmark );
      Assert.Contains( BenchLegends.L_GOLDEN_NO_FILE, golden );
      Assert.DoesNotContain( "<table", golden );
   }

   /// <summary>
   /// A note is printed only when it is short and not the labelling record: the file's real notes (who graded, which model, what was overridden) are not
   /// printed, a short plain note is, and a note over the length limit or a short one that carries the labelling words is not.
   /// </summary>
   [Fact]
   public void ANote_IsPrintedOnlyWhenShortAndNotInternal()
   {
      var items = new JsonArray(
         Item( "real", "one", "identifier", INTERNAL_NOTE, "src/One.cs" ),
         Item( "plain", "two", "conceptual", PLAIN_NOTE, "src/Two.cs" ),
         Item( "long", "three", "conceptual", new string( 'x', BenchGoldenQuestions.MAX_NOTE_CHARS + 1 ), "src/Three.cs" ),
         Item( "short-internal", "four", "conceptual", "Labelled by hand.", "src/Four.cs" ),
         Item( "edge", "five", "conceptual", new string( 'y', BenchGoldenQuestions.MAX_NOTE_CHARS ), "src/Five.cs" ),
         Item( "none", "six", "conceptual", null, "src/Six.cs" ) );
      WriteQuestions( items );
      WriteSet( withHash: true, count: "6" );
      string page = BenchResultsEndpoints.GoldenQuestionsPageHtml( _root );
      List<( string Id, int Number, string Question, string Category, string[] Files )> shown = Rows( page );

      Assert.Equal( 6, shown.Count );
      Assert.Equal( new[] { "one", "two", "three", "four", "five", "six" }, shown.Select( s => s.Question ) );
      Assert.Equal( new[] { PLAIN_NOTE, new string( 'y', BenchGoldenQuestions.MAX_NOTE_CHARS ) }, Regex.Matches( page, "<small class=\"muted bench-golden-note\">(.*?)</small>" ).Select( m => m.Groups[1].Value ) );
      Assert.DoesNotContain( "blind code reads", page );
      Assert.DoesNotContain( "Labelled", page );
      Assert.Null( BenchGoldenQuestions.NoteToShow( INTERNAL_NOTE ) );
      Assert.Null( BenchGoldenQuestions.NoteToShow( "AUTHOR OVERRIDDEN: named another file." ) );
      Assert.Null( BenchGoldenQuestions.NoteToShow( "   " ) );
      Assert.Null( BenchGoldenQuestions.NoteToShow( null ) );
      Assert.Equal( PLAIN_NOTE, BenchGoldenQuestions.NoteToShow( "  " + PLAIN_NOTE + " " ) );
   }

   /// <summary>
   /// A file that is missing is a visible error that names the file, and the page holds no table and no row: it is not an empty table.
   /// </summary>
   [Fact]
   public void AMissingFile_IsAnErrorNotice_NotAnEmptyTable()
   {
      WriteSet( withHash: true );
      string page = BenchResultsEndpoints.GoldenQuestionsPageHtml( _root );

      AssertRefused( page, BenchLegends.L_GOLDEN_UNREADABLE );
      Assert.Contains( _questions, WebUtility.HtmlDecode( page ) );
   }

   /// <summary>
   /// A file that is missing is the same error over HTTP: the page answers, with the notice and without a table, and does not fail the request.
   /// </summary>
   [Fact]
   public async Task AMissingFile_AnswersThePageWithTheNotice_OverHttp()
   {
      WriteSet( withHash: true );
      await WithServerAsync( async client =>
      {
         using HttpResponseMessage response = await client.GetAsync( ADDRESS );
         string body = await response.Content.ReadAsStringAsync();

         Assert.Equal( HttpStatusCode.OK, response.StatusCode );
         AssertRefused( body, BenchLegends.L_GOLDEN_UNREADABLE );
      } );
   }

   /// <summary>
   /// A file that cannot be read as the questions is a visible error that says why: text that is not JSON, JSON that is not a list, an item without its
   /// question (named by its place), an item that lists no file, and a path in the set that is not a full path.
   /// </summary>
   [Fact]
   public void AFileThatCannotBeRead_IsAnErrorNotice_AndNamesWhatIsWrong()
   {
      WriteSet( withHash: false, count: null );
      File.WriteAllText( _questions, "this is not json" );
      string notJson = BenchResultsEndpoints.GoldenQuestionsPageHtml( _root );
      File.WriteAllText( _questions, "{\"Question\": \"a\"}" );
      string notList = BenchResultsEndpoints.GoldenQuestionsPageHtml( _root );
      File.WriteAllText( _questions, new JsonArray( Item( "a", "ok", "identifier", null, "src/A.cs" ), new JsonObject { ["Id"] = "b", ["Category"] = "identifier", ["Relevant"] = new JsonArray( new JsonObject { ["FilePath"] = "src/B.cs" } ) } ).ToJsonString() );
      string noQuestion = BenchResultsEndpoints.GoldenQuestionsPageHtml( _root );
      File.WriteAllText( _questions, new JsonArray( new JsonObject { ["Id"] = "c", ["Question"] = "q", ["Category"] = "identifier", ["Relevant"] = new JsonArray() } ).ToJsonString() );
      string noFiles = BenchResultsEndpoints.GoldenQuestionsPageHtml( _root );
      File.WriteAllText( _questions, new JsonArray( new JsonObject { ["Id"] = "d", ["Question"] = "q", ["Category"] = "identifier", ["Relevant"] = new JsonArray( new JsonObject { ["Grade"] = 3 } ) } ).ToJsonString() );
      string noPath = BenchResultsEndpoints.GoldenQuestionsPageHtml( _root );

      AssertRefused( notJson, BenchLegends.L_GOLDEN_UNREADABLE );
      AssertRefused( notList, BenchLegends.L_GOLDEN_UNREADABLE );
      Assert.Contains( "is not a list of questions", notList );
      AssertRefused( noQuestion, BenchLegends.L_GOLDEN_UNREADABLE );
      Assert.Contains( "questions[1].Question is missing", noQuestion );
      AssertRefused( noFiles, BenchLegends.L_GOLDEN_UNREADABLE );
      Assert.Contains( "questions[0].Relevant lists no file", noFiles );
      AssertRefused( noPath, BenchLegends.L_GOLDEN_UNREADABLE );
      Assert.Contains( "questions[0].Relevant[0].FilePath is missing", noPath );
   }

   /// <summary>
   /// A file with no question in it is a visible error and not a table with no rows.
   /// </summary>
   [Fact]
   public void AFileWithNoQuestions_IsAnErrorNotice()
   {
      File.WriteAllText( _questions, "[]" );
      WriteSet( withHash: false, count: null );
      string page = BenchResultsEndpoints.GoldenQuestionsPageHtml( _root );

      AssertRefused( page, BenchLegends.L_GOLDEN_EMPTY );
   }

   /// <summary>
   /// A file whose hash is not the one the runs recorded is not shown: the notice says the file is not the file the runs read and gives the start of both
   /// hashes, and the page holds no table.
   /// </summary>
   [Fact]
   public void AFileThatIsNotTheFileTheRunsRead_IsRefused()
   {
      WriteQuestions( Questions() );
      string recorded = new string( 'a', 64 );
      WriteSet( withHash: true, hash: recorded );
      string page = BenchResultsEndpoints.GoldenQuestionsPageHtml( _root );
      string actual = Convert.ToHexString( SHA256.HashData( File.ReadAllBytes( _questions ) ) ).ToLowerInvariant();

      AssertRefused( page, BenchLegends.L_GOLDEN_CHANGED_A );
      Assert.Contains( actual[..12], page );
      Assert.Contains( recorded[..12], page );
      Assert.DoesNotContain( actual, page );
   }

   /// <summary>
   /// A file that holds another number of questions than the set records the runs ran is not shown, and the notice gives both numbers.
   /// </summary>
   [Fact]
   public void AFileWithAnotherNumberOfQuestionsThanTheRunsRan_IsRefused()
   {
      WriteQuestions( Questions() );
      WriteSet( withHash: true, count: "19" );
      string page = BenchResultsEndpoints.GoldenQuestionsPageHtml( _root );

      AssertRefused( page, BenchLegends.L_GOLDEN_COUNT_A );
      Assert.Contains( $"{BenchLegends.L_GOLDEN_COUNT_A} {QUESTION_COUNT} {BenchLegends.L_GOLDEN_COUNT_B} 19 {BenchLegends.L_GOLDEN_COUNT_C}", page );
   }

   /// <summary>
   /// With no published set the page is an error notice; with a set in an older format it says so; with a newer folder that cannot be read it names the
   /// folder above the questions of the older one, as the benchmark page does.
   /// </summary>
   [Fact]
   public void WithoutAReadableSet_ThePageSaysSo()
   {
      string none = BenchResultsEndpoints.GoldenQuestionsPageHtml( _root );
      AssertRefused( none, BenchLegends.L_BENCHMARK_NONE );

      Directory.CreateDirectory( Path.Combine( _root, FOLDER ) );
      File.WriteAllText( Path.Combine( _root, FOLDER, "consolidated.json" ), "{\"targetSummaries\": []}" );
      AssertRefused( BenchResultsEndpoints.GoldenQuestionsPageHtml( _root ), BenchLegends.OLDER_FORMAT );

      WriteQuestions( Questions() );
      WriteSet( withHash: true );
      Directory.CreateDirectory( Path.Combine( _root, "published-2026-10-09" ) );
      File.WriteAllText( Path.Combine( _root, "published-2026-10-09", "consolidated.json" ), "{ not json" );
      string fallback = BenchResultsEndpoints.GoldenQuestionsPageHtml( _root );

      Assert.Contains( "The newest published results, published-2026-10-09, could not be read", fallback );
      Assert.Equal( QUESTION_COUNT, Rows( fallback ).Count );
   }

   /// <summary>
   /// The words the page prints itself are the fixed legends the legend tests walk: the first sentence, the column headings, the link back and the notices
   /// are all in <see cref="BenchLegends"/>.
   /// </summary>
   [Fact]
   public void TheFixedWordsOfThePage_AreLegends()
   {
      foreach( string key in new[] { "L_GOLDEN_INTRO_A", "L_GOLDEN_INTRO_B", "L_GOLDEN_COL_NUMBER", "L_GOLDEN_COL_QUESTION", "L_GOLDEN_COL_TYPE", "L_GOLDEN_COL_FILES", "L_GOLDEN_FROM", "L_GOLDEN_HASH_MATCH", "L_GOLDEN_BACK", "L_GOLDEN_UNREADABLE", "L_GOLDEN_NO_FILE", "L_GOLDEN_CHANGED_A", "L_GOLDEN_CHANGED_B", "L_GOLDEN_COUNT_A", "L_GOLDEN_COUNT_B", "L_GOLDEN_COUNT_C", "L_GOLDEN_EMPTY" } )
      {
         Assert.Contains( key, BenchLegends.All.Keys );
      }
   }

   /// <summary>
   /// Against the real set and the real questions file on this machine: the page lists the real file's questions, in its order, with its categories and
   /// relevant files, reads the file the set records, and the file is the file whose hash the set records.
   /// </summary>
   [FactIfPathExists( REAL_QUESTIONS )]
   public void TheRealFile_IsListedInFull_AsTheRealSetRecordsIt()
   {
      if( !File.Exists( REAL_SET ) )
      {
         return;
      }

      Directory.CreateDirectory( Path.Combine( _root, FOLDER ) );
      File.Copy( REAL_SET, Path.Combine( _root, FOLDER, "consolidated.json" ) );
      JsonArray items = JsonNode.Parse( File.ReadAllText( REAL_QUESTIONS ) )!.AsArray();
      string page = BenchResultsEndpoints.GoldenQuestionsPageHtml( _root );
      List<Expected> expected = ExpectedOf( items );
      List<( string Id, int Number, string Question, string Category, string[] Files )> shown = Rows( page );

      Assert.Equal( 20, expected.Count );
      Assert.Equal( expected.Select( e => e.Id ), shown.Select( s => s.Id ) );
      Assert.Equal( expected.Select( e => e.Question ), shown.Select( s => s.Question ) );
      Assert.Equal( expected.Select( e => e.Category ), shown.Select( s => s.Category ) );
      Assert.Equal( expected.Select( e => string.Join( "|", e.Files ) ), shown.Select( s => string.Join( "|", s.Files ) ) );
      Assert.Contains( $"<code>{REAL_QUESTIONS}</code>. Its hash is the one the runs recorded.", page );
      Assert.DoesNotContain( "bench-golden-note", page );
      Assert.DoesNotContain( "blind code reads", page );
   }

   #endregion Public Methods

   #region Private Methods

   /// <summary>
   /// Twenty questions in the file's shape: identifiers and conceptual questions, one to four relevant files each, each with the labelling record as Notes.
   /// </summary>
   /// <returns>The array.</returns>
   private static JsonArray Questions()
   {
      var items = new JsonArray();
      for( int i = 0; i < QUESTION_COUNT; i++ )
      {
         bool identifier = i % 3 == 0;
         string[] files = Enumerable.Range( 0, 1 + ( i % 4 ) ).Select( j => $"src/Area{i}/Part{j}.cs" ).ToArray();
         items.Add( Item( $"question-{i + 1}", identifier ? $"CamelCaseName{i}" : $"how does thing {i + 1} work", identifier ? "identifier" : "conceptual", INTERNAL_NOTE, files ) );
      }

      return items;
   }

   /// <summary>
   /// One item of the questions file.
   /// </summary>
   /// <param name="id">Id.</param>
   /// <param name="question">Question.</param>
   /// <param name="category">Category.</param>
   /// <param name="notes">Notes, or null for none.</param>
   /// <param name="files">The relevant files, best first.</param>
   /// <returns>The item.</returns>
   private static JsonObject Item( string id, string question, string category, string? notes, params string[] files )
   {
      var item = new JsonObject { ["Id"] = id, ["Question"] = question, ["Category"] = category };
      if( notes != null )
      {
         item["Notes"] = notes;
      }

      item["Relevant"] = new JsonArray( files.Select( ( f, i ) => (JsonNode)new JsonObject { ["FilePath"] = f, ["Grade"] = i == 0 ? 3 : 2 } ).ToArray() );
      return item;
   }

   /// <summary>
   /// What the page should show for the items of a questions file, read from the items themselves.
   /// </summary>
   /// <param name="items">The array.</param>
   /// <returns>One entry per item, in order.</returns>
   private static List<Expected> ExpectedOf( JsonArray items )
   {
      return items.Select( n => new Expected( n!["Id"]!.GetValue<string>(), n["Question"]!.GetValue<string>(), n["Category"]!.GetValue<string>(), n["Relevant"]!.AsArray().Select( r => r!["FilePath"]!.GetValue<string>() ).ToArray() ) ).ToList();
   }

   /// <summary>
   /// The rows of the table of a page, each decoded: id, number, question, type and files.
   /// </summary>
   /// <param name="page">The page.</param>
   /// <returns>The rows in page order.</returns>
   private static List<( string Id, int Number, string Question, string Category, string[] Files )> Rows( string page )
   {
      return ROW.Matches( page ).Select( m => (
         WebUtility.HtmlDecode( m.Groups[1].Value ),
         int.Parse( m.Groups[2].Value ),
         WebUtility.HtmlDecode( Regex.Replace( m.Groups[3].Value, "<small.*?</small>", string.Empty ).Trim() ),
         WebUtility.HtmlDecode( m.Groups[4].Value ),
         FILE_ITEM.Matches( m.Groups[5].Value ).Select( f => WebUtility.HtmlDecode( f.Groups[1].Value ) ).ToArray() ) ).ToList();
   }

   /// <summary>
   /// The head of a page without its title.
   /// </summary>
   /// <param name="page">The page.</param>
   /// <returns>The head's HTML.</returns>
   private static string HeadOf( string page )
   {
      Match head = Regex.Match( page, "<head>.*?</head>", RegexOptions.Singleline );
      Assert.True( head.Success, "no head on the page" );
      return TITLE.Replace( head.Value, string.Empty );
   }

   /// <summary>
   /// Holds a page to the refusal: an error notice with the given words, no table and no row.
   /// </summary>
   /// <param name="page">The page.</param>
   /// <param name="words">Words the notice must hold.</param>
   private static void AssertRefused( string page, string words )
   {
      Match notice = Regex.Match( page, "<p class=\"errors\" data-notice=\"golden-questions\">(.*?)</p>", RegexOptions.Singleline );

      Assert.True( notice.Success, "no error notice on the page" );
      Assert.Contains( words, WebUtility.HtmlDecode( notice.Groups[1].Value ) );
      Assert.DoesNotContain( "<table", page );
      Assert.DoesNotContain( "bench-golden-row", page );
      Assert.DoesNotContain( "bench-golden-lead", page );
   }

   /// <summary>
   /// Writes the questions file at this test's path.
   /// </summary>
   /// <param name="items">The array.</param>
   private void WriteQuestions( JsonArray items )
   {
      File.WriteAllText( _questions, items.ToJsonString( new JsonSerializerOptions { WriteIndented = true } ) );
   }

   /// <summary>
   /// Writes the published set: the v8 fixture with a data line, and queries recorded as golden questions read from this test's file.
   /// </summary>
   /// <param name="withHash">True to record the hash of the file; false to record none.</param>
   /// <param name="hash">The hash to record, or null for the hash of the file on disk.</param>
   /// <param name="count">The number of queries the data line records, or null for none.</param>
   /// <param name="change">Edits the root last, or null.</param>
   private void WriteSet( bool withHash, string? hash = null, string? count = "20", Action<JsonObject>? change = null )
   {
      string json = BenchResultsV8Fixture.Json( root =>
      {
         if( count != null )
         {
            ( (JsonArray)root["sentences"]! ).Add( new JsonObject { ["slot"] = "subtitle.data", ["text"] = $"Data: eshoponweb, 524 vectors of 1024 dimensions; {count} queries, top 10 hits each.", ["sources"] = DataSources( count ) } );
         }

         var queries = new JsonObject { ["recorded"] = $"golden: {count ?? "20"} labelled questions from {_questions}, embedded with qwen3-emb-0.6b (cached in /home/dan/gvb-data/bench-cache/golden.json, no embedding calls)", ["copyPath"] = "design/bench-inputs/questions_golden.json" };
         if( withHash )
         {
            queries["copySha256"] = hash ?? ( File.Exists( _questions ) ? Convert.ToHexString( SHA256.HashData( File.ReadAllBytes( _questions ) ) ).ToLowerInvariant() : new string( '0', 64 ) );
         }

         root["queries"] = queries;
         change?.Invoke( root );
      } );
      Directory.CreateDirectory( Path.Combine( _root, FOLDER ) );
      File.WriteAllText( Path.Combine( _root, FOLDER, "consolidated.json" ), json );
   }

   /// <summary>
   /// The sources of a data line, in the shape the consolidate command writes them.
   /// </summary>
   /// <param name="queries">The query count.</param>
   /// <returns>The array.</returns>
   private static JsonArray DataSources( string queries )
   {
      return new JsonArray(
         new JsonObject { ["kind"] = "results", ["ref"] = "results:pipeline", ["value"] = "eshoponweb" },
         new JsonObject { ["kind"] = "results", ["ref"] = "results:rows", ["value"] = "524" },
         new JsonObject { ["kind"] = "results", ["ref"] = "results:dimension", ["value"] = "1024" },
         new JsonObject { ["kind"] = "results", ["ref"] = "results:queryCount", ["value"] = queries },
         new JsonObject { ["kind"] = "results", ["ref"] = "results:top", ["value"] = "10" } );
   }

   /// <summary>
   /// Runs the routes in a real web host on a free loopback port, with the results root of this test, and hands a client to the body. The host and the
   /// client have timeouts, and the host is stopped when the body ends, whether it passes or fails.
   /// </summary>
   /// <param name="body">What to do with the client.</param>
   private async Task WithServerAsync( Func<HttpClient, Task> body )
   {
      WebApplicationBuilder builder = WebApplication.CreateBuilder();
      builder.WebHost.UseUrls( "http://127.0.0.1:0" );
      builder.Logging.ClearProviders();
      builder.Configuration["Gvb:BenchResultsPath"] = _root;
      await using WebApplication app = builder.Build();
      BenchResultsEndpoints.Map( app );
      using var timeout = new CancellationTokenSource( TimeSpan.FromSeconds( SERVER_SECONDS ) );
      await app.StartAsync( timeout.Token );
      try
      {
         string address = app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.First();
         using var client = new HttpClient { BaseAddress = new Uri( address ), Timeout = TimeSpan.FromSeconds( REQUEST_SECONDS ) };
         await body( client );
      }
      finally
      {
         await app.StopAsync( timeout.Token );
      }
   }

   #endregion Private Methods

   #region IDisposable

   /// <summary>
   /// Removes this test's folder.
   /// </summary>
   public void Dispose()
   {
      string? folder = Path.GetDirectoryName( _questions );
      if( folder != null && Directory.Exists( folder ) )
      {
         Directory.Delete( folder, true );
      }

      GC.SuppressFinalize( this );
   }

   #endregion IDisposable
}
