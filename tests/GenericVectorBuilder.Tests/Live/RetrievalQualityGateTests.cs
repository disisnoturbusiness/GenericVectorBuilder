using GenericVectorBuilder.Core.Chunking;
using GenericVectorBuilder.Core.Configuration;
using GenericVectorBuilder.Core.Contracts;
using GenericVectorBuilder.Core.Embedding;
using GenericVectorBuilder.Core.Mapping;
using GenericVectorBuilder.Core.Pipeline;
using GenericVectorBuilder.Core.Sources.Files;
using GenericVectorBuilder.Tests.Fakes;
using Microsoft.Data.SqlClient;
using Xunit.Abstractions;

namespace GenericVectorBuilder.Tests.Live;

/// <summary>
/// End-to-end gate against the REAL services on linus7795: GPU embedding service, SQL Server
/// 2025 and Qdrant. Builds a small labelled folder (products, tickets, staff in three file
/// formats), runs the actual pipeline into both sinks, then asks paraphrased questions that
/// share almost no words with the right row and checks the right row comes back first.
/// Why this test exists: every other test can pass while search quality is garbage. Measured
/// 2026-10-02: bge-code-v1 without its end-of-text token put the right row first 5 times in 14;
/// with it, 14 in 14. A pipeline that "works" but returns the wrong rows must fail the build.
/// These tests need the services up and FAIL (not skip) when they are not.
/// Run only these: dotnet test --filter Category=Live. Run everything else: --filter Category!=Live.
/// </summary>
[Trait( "Category", "Live" )]
public class RetrievalQualityGateTests : IDisposable
{
   #region Data Members

   private const string TEST_DATABASE = "GenericVectorBuilder_Test";
   private const int MIN_TOP1 = 13;

   private static readonly (string Query, string Expected)[] QUESTIONS =
   {
      ( "something to keep my dog under control on walks", "Retractable Dog Leash" ),
      ( "tool for pulling out nails", "Claw Hammer" ),
      ( "a pan I can put in the oven", "Cast Iron Skillet" ),
      ( "make a latte at home", "Espresso Machine" ),
      ( "keeps me warm at night below freezing outdoors", "Sleeping Bag" ),
      ( "protect my head while cycling", "Bicycle Helmet" ),
      ( "the website security cert ran out", "SSL certificate expired" ),
      ( "employees did not get paid because the deposit job broke", "Payroll run failed" ),
      ( "customer still waiting for their money after sending the item back", "Refund not processed" ),
      ( "iPhone app quits when signing in", "Mobile app crashes on login" ),
      ( "storage space running out on a server", "Disk almost full on file server" ),
      ( "who do I talk to about signing up for health insurance", "Sofia Rossi" ),
      ( "staff member who can operate a forklift", "Tom Becker" ),
      ( "who interviews job candidates", "Hannah Muller" ),
      ( "who can renew an expiring web certificate", "Liam O'Brien" ),
   };

   private readonly ITestOutputHelper _output;
   private readonly TempFolder _data = new();
   private readonly TempFolder _stateDir = new();
   private readonly GvbSettings _settings;
   private readonly GvbServices _services;

   #endregion Data Members

   #region Constructor

   /// <summary>
   /// Points the services at a throwaway SQL database and state file, and writes the folder.
   /// </summary>
   /// <param name="output">xUnit output, for the per-question report.</param>
   public RetrievalQualityGateTests( ITestOutputHelper output )
   {
      _output = output;
      _settings = new GvbSettings { SqlDatabase = TEST_DATABASE, StatePath = Path.Combine( _stateDir.Path, "state.db" ) };
      _services = new GvbServices( _settings );
      WriteDataset( _data );
   }

   #endregion Constructor

   #region Public Methods

   /// <summary>
   /// Measures each available model on the same 15 questions and reports top-1 per sink.
   /// Does not gate; it exists so the default is chosen by measurement, not by assumption.
   /// </summary>
   /// <param name="modelKey">Model to measure.</param>
   [Theory]
   [InlineData( "bge-code-v1" )]
   [InlineData( "qwen3-emb-0.6b" )]
   public async Task Measure_EachModel( string modelKey )
   {
      Dictionary<string, int> top1 = await BuildAndScoreAsync( $"gvbgate_{modelKey.Replace( '-', '_' ).Replace( '.', '_' )}", EmbedderProfile.Get( modelKey ) );
      Assert.Equal( 2, top1.Count );
   }

   /// <summary>
   /// The shipped default model must put the right row first for at least 13 of 15
   /// questions, in BOTH SQL Server and Qdrant.
   /// </summary>
   [Fact]
   public async Task ShippedDefault_FindsTheRightRowFirst_InBothSinks()
   {
      Dictionary<string, int> top1 = await BuildAndScoreAsync( "gvbgate_default", EmbedderProfile.Get( _settings.DefaultEmbedModel ) );
      Assert.All( top1, kv => Assert.True( kv.Value >= MIN_TOP1, $"{kv.Key}: right row first for {kv.Value}/{QUESTIONS.Length}, gate is {MIN_TOP1}" ) );
   }

   /// <summary>
   /// The gate must have teeth: the same model WITHOUT the end-of-text token (the known
   /// failure) has to score worse than the shipped profile, or the dataset is too easy to
   /// catch a real regression.
   /// </summary>
   [Fact]
   public async Task Gate_CatchesTheKnownMissingTokenRegression()
   {
      var broken = new EmbedderProfile( "bge-code-v1", string.Empty, "{0}", "control: end-of-text token missing" );
      Dictionary<string, int> good = await BuildAndScoreAsync( "gvbgate_good", EmbedderProfile.BgeCodeV1 );
      Dictionary<string, int> bad = await BuildAndScoreAsync( "gvbgate_control", broken );
      Assert.True( bad["sql"] < good["sql"], $"control {bad["sql"]} vs shipped {good["sql"]}: the gate cannot tell them apart" );
   }

   /// <summary>
   /// Drops everything the test created: pipeline tables, collections, the test database.
   /// </summary>
   public void Dispose()
   {
      foreach( string pipeline in new[] { "gvbgate_default", "gvbgate_good", "gvbgate_control", "gvbgate_bge_code_v1", "gvbgate_qwen3_emb_0_6b" } )
      {
         _services.ResetPipelineAsync( pipeline, CancellationToken.None ).GetAwaiter().GetResult();
      }

      DropTestDatabase();
      _services.Dispose();
      _data.Dispose();
      _stateDir.Dispose();
   }

   #endregion Public Methods

   #region Private Methods

   /// <summary>
   /// Runs the real pipeline into SQL and Qdrant with the given embedder profile, then asks
   /// every question and counts how often the expected row is the top hit, per sink.
   /// </summary>
   /// <param name="pipeline">Pipeline name.</param>
   /// <param name="profile">Embedder profile.</param>
   /// <returns>Top-1 hits per sink name.</returns>
   private async Task<Dictionary<string, int>> BuildAndScoreAsync( string pipeline, EmbedderProfile profile )
   {
      await _services.ResetPipelineAsync( pipeline, CancellationToken.None );
      var http = new HttpClient { BaseAddress = new Uri( _settings.EmbedUrl ), Timeout = TimeSpan.FromMinutes( 10 ) };
      var embedder = new GpuServiceEmbedder( http, profile );
      IReadOnlyList<ISink> sinks = _services.GetSinks( new[] { "sql", "qdrant" } );
      FolderScanResult scan = new FolderScanner().Scan( _data.Path, CancellationToken.None );
      Assert.Equal( 3, scan.Tables.Count );

      var progress = new RunProgress( "gate", pipeline );
      await new PipelineRunner( embedder, sinks, _services.State, new TextChunker() )
         .RunAsync( pipeline, new FolderSource( scan ), new RowDocumentMapper( new Dictionary<string, TableMapping>() ), progress, CancellationToken.None );
      RunSnapshot run = progress.Snapshot();
      Assert.True( run.Status == RunStatus.Completed, $"run ended {run.Status}: {run.Message} {string.Join( "; ", run.Errors )}" );

      var top1 = sinks.ToDictionary( s => s.Name, _ => 0 );
      foreach( (string query, string expected) in QUESTIONS )
      {
         float[] vector = await embedder.EmbedQueryAsync( query, CancellationToken.None );
         foreach( ISink sink in sinks )
         {
            IReadOnlyList<SearchHit> hits = await sink.SearchAsync( pipeline, vector, 3, CancellationToken.None );
            bool hit = hits.Count > 0 && hits[0].Text.Contains( expected, StringComparison.Ordinal );
            top1[sink.Name] += hit ? 1 : 0;
            _output.WriteLine( $"[{profile.ModelKey}{( profile.DocumentSuffix.Length == 0 && profile.ModelKey == "bge-code-v1" ? " NO-EOS" : "" )}] {sink.Name} {( hit ? "HIT " : "MISS" )} {query} -> {FirstLine( hits )}" );
         }
      }

      _output.WriteLine( $"[{profile.ModelKey}{( profile.DocumentSuffix.Length == 0 && profile.ModelKey == "bge-code-v1" ? " NO-EOS" : "" )}] top-1: {string.Join( ", ", top1.Select( kv => $"{kv.Key} {kv.Value}/{QUESTIONS.Length}" ) )}" );
      return top1;
   }

   /// <summary>
   /// Second line of the top hit's text (the name or subject column), for the report.
   /// </summary>
   /// <param name="hits">Hits.</param>
   /// <returns>A short label.</returns>
   private static string FirstLine( IReadOnlyList<SearchHit> hits )
   {
      return hits.Count == 0 ? "(nothing)" : hits[0].Text.Split( '\n' ).Skip( 1 ).FirstOrDefault() ?? hits[0].Text;
   }

   /// <summary>
   /// Drops the throwaway database. The name is a constant, never user input.
   /// </summary>
   private void DropTestDatabase()
   {
      using var connection = new SqlConnection( _settings.BuildSqlConnectionString() );
      connection.Open();
      using var command = new SqlCommand( $"IF DB_ID( '{TEST_DATABASE}' ) IS NOT NULL BEGIN ALTER DATABASE [{TEST_DATABASE}] SET SINGLE_USER WITH ROLLBACK IMMEDIATE; DROP DATABASE [{TEST_DATABASE}]; END", connection );
      command.ExecuteNonQuery();
   }

   /// <summary>
   /// Writes the labelled dataset: products as CSV, support tickets pipe-delimited, staff as
   /// tab-delimited. Every question above has exactly one right row.
   /// </summary>
   /// <param name="folder">Target folder.</param>
   private static void WriteDataset( TempFolder folder )
   {
      folder.Write( "products.csv", string.Join( "\n", new[]
      {
         "sku,name,category,description,price",
         "P001,Retractable Dog Leash,Pets,5 metre nylon cord with brake button,24.99",
         "P002,Claw Hammer,Tools,16 oz steel head with curved claw,18.50",
         "P003,Cordless Drill,Tools,18V battery drill driver with two speed gearbox,89.00",
         "P004,Cast Iron Skillet,Kitchen,pre-seasoned 12 inch frying pan for stovetop and broiler,39.95",
         "P005,Espresso Machine,Kitchen,15 bar pump coffee maker with milk frother,249.00",
         "P006,Yoga Mat,Fitness,6mm non-slip exercise mat with carry strap,29.00",
         "P007,Adjustable Dumbbells,Fitness,pair of weights from 5 to 52 lb with dial selector,299.00",
         "P008,Tent,Outdoors,4 person waterproof dome shelter,159.00",
         "P009,Sleeping Bag,Outdoors,mummy bag rated to minus 10 C,119.00",
         "P010,LED Desk Lamp,Office,dimmable lamp with USB charging port,34.99",
         "P011,Ergonomic Office Chair,Office,mesh back chair with lumbar support,219.00",
         "P012,Cat Litter Box,Pets,covered self cleaning litter tray,129.00",
         "P013,Garden Hose,Garden,50 ft expandable water hose with spray nozzle,32.00",
         "P014,Lawn Mower,Garden,electric push mower with grass catcher,279.00",
         "P015,Bluetooth Headphones,Electronics,over-ear wireless headphones with noise cancelling,149.00",
         "P016,Smoke Detector,Safety,battery powered smoke alarm with 10 year life,22.00",
         "P017,Fire Extinguisher,Safety,5 lb ABC dry chemical extinguisher,45.00",
         "P018,Baby Stroller,Baby,lightweight folding pushchair with canopy,189.00",
         "P019,Bicycle Helmet,Sports,ventilated shell with adjustable fit dial,59.00",
         "P020,Umbrella,Accessories,windproof compact umbrella with auto open,19.00",
      } ) + "\n" );

      folder.Write( "tickets.psv", string.Join( "\n", new[]
      {
         "ticket_id|subject|body|status",
         "T100|Wrong tax charged on invoice|Customer in Ohio was billed sales tax at the wrong rate on invoice 5521|open",
         "T101|SSL certificate expired|The HTTPS certificate on the customer portal lapsed last night and browsers show a warning|open",
         "T102|Password reset email not arriving|User requested a reset link three times but nothing reached their inbox|pending",
         "T103|Payroll run failed|Biweekly payroll job stopped with a database timeout before direct deposits went out|open",
         "T104|Printer offline|Front desk printer shows offline after the network switch was replaced|closed",
         "T105|Duplicate customer records|Same customer appears twice after the CRM import with different spellings|open",
         "T106|Slow report generation|Monthly sales report takes 40 minutes to render|pending",
         "T107|VPN disconnects|Remote staff lose VPN connection every 15 minutes|open",
         "T108|Refund not processed|Customer returned an order two weeks ago and has not been reimbursed|open",
         "T109|Shipping label wrong address|Labels print with the warehouse address instead of the customer address|closed",
         "T110|Mobile app crashes on login|iOS app closes immediately after entering credentials|open",
         "T111|Data export missing columns|CSV export of orders no longer includes the discount column|pending",
         "T112|Calendar invites in wrong time zone|Meeting invites show times shifted by three hours|open",
         "T113|Disk almost full on file server|File server has 2 percent free space left|open",
         "T114|Account locked out|Employee locked out after too many failed sign-in attempts|closed",
      } ) + "\n" );

      folder.Write( "staff.tsv", string.Join( "\n", new[]
      {
         "employee_id\tname\ttitle\tdepartment\tskills",
         "E01\tMaria Lopez\tPayroll Specialist\tFinance\tgarnishments, tax filings, quarterly 941 returns",
         "E02\tJames Chen\tNetwork Engineer\tIT\tfirewalls, VPN, switches and routing",
         "E03\tAisha Patel\tData Analyst\tAnalytics\tSQL, dashboards, forecasting",
         "E04\tTom Becker\tWarehouse Lead\tOperations\tforklift certified, inventory counts, shipping",
         "E05\tSofia Rossi\tHR Generalist\tHuman Resources\tonboarding, benefits enrollment, leave of absence",
         "E06\tDaniel Kim\tiOS Developer\tEngineering\tSwift, mobile apps, App Store releases",
         "E07\tGrace Okafor\tAccounts Receivable Clerk\tFinance\tcollections, invoicing, payment plans",
         "E08\tLiam O'Brien\tSecurity Officer\tIT\tTLS certificates, identity management, account lockouts",
         "E09\tHannah Muller\tRecruiter\tHuman Resources\tscreening calls, job postings, offer letters",
         "E10\tCarlos Diaz\tFacilities Manager\tOperations\tbuilding maintenance, HVAC, fire safety inspections",
      } ) + "\n" );
   }

   #endregion Private Methods
}
