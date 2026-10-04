using System.Data;
using ClosedXML.Excel;
using GenericVectorBuilder.Core.Chunking;
using GenericVectorBuilder.Core.Contracts;
using GenericVectorBuilder.Core.Mapping;
using GenericVectorBuilder.Core.Pipeline;
using GenericVectorBuilder.Core.Sinks;
using GenericVectorBuilder.Core.Sources.Files;
using GenericVectorBuilder.Core.State;
using GenericVectorBuilder.Tests.Fakes;

namespace GenericVectorBuilder.Tests.Unit;

/// <summary>
/// The rules that make re-runs cheap and safe, tested with a fake embedder, in-memory sinks
/// and the real SQLite state store.
/// </summary>
public class PipelineRunnerTests : IDisposable
{
   #region Data Members

   private const string PIPELINE = "unit";

   private readonly TempFolder _data = new();
   private readonly TempFolder _stateDir = new();
   private readonly SqliteStateStore _state;
   private readonly FakeEmbedder _embedder = new();
   private readonly MemorySink _a = new( "a" );
   private readonly MemorySink _b = new( "b" );

   #endregion Data Members

   #region Constructor

   /// <summary>
   /// Sets up a fresh state store per test.
   /// </summary>
   public PipelineRunnerTests()
   {
      _state = new SqliteStateStore( Path.Combine( _stateDir.Path, "state.db" ) );
   }

   #endregion Constructor

   #region Public Methods

   /// <summary>
   /// A second run over unchanged data embeds nothing.
   /// </summary>
   [Fact]
   public async Task Rerun_UnchangedData_EmbedsNothing()
   {
      _data.Write( "p.csv", "id,name\n1,Hammer\n2,Drill\n" );
      RunSnapshot first = await RunAsync();
      Assert.Equal( RunStatus.Completed, first.Status );
      Assert.Equal( 2, first.DocsEmbedded );

      _embedder.Embedded.Clear();
      RunSnapshot second = await RunAsync();
      Assert.Equal( 2, second.DocsUnchanged );
      Assert.Empty( _embedder.Embedded );
      Assert.Equal( 2, _a.Records.Count );
   }

   /// <summary>
   /// A changed row is re-embedded and its old chunk is deleted; untouched rows are not.
   /// </summary>
   [Fact]
   public async Task ChangedRow_IsReplacedNotDuplicated()
   {
      _data.Write( "p.csv", "id,name\n1,Hammer\n2,Drill\n" );
      await RunAsync();
      _data.Write( "p.csv", "id,name\n1,Claw Hammer\n2,Drill\n" );
      _embedder.Embedded.Clear();

      RunSnapshot run = await RunAsync();

      Assert.Single( _embedder.Embedded );
      Assert.Equal( new[] { "id: 1\nname: Claw Hammer", "id: 2\nname: Drill" }, _a.Texts() );
      Assert.Single( _a.Deleted );
      Assert.Equal( 1, run.DocsUnchanged );
   }

   /// <summary>
   /// Rows removed from a cleanly read file are deleted from the sinks.
   /// </summary>
   [Fact]
   public async Task RemovedRow_IsDeleted()
   {
      _data.Write( "p.csv", "id,name\n1,Hammer\n2,Drill\n" );
      await RunAsync();
      _data.Write( "p.csv", "id,name\n1,Hammer\n" );

      RunSnapshot run = await RunAsync();

      Assert.Equal( 1, run.DocsDeleted );
      Assert.Equal( new[] { "id: 1\nname: Hammer" }, _a.Texts() );
   }

   /// <summary>
   /// A file that breaks between runs keeps its old vectors: rejected is not the same as empty.
   /// </summary>
   [Fact]
   public async Task RejectedFile_KeepsItsOldVectors()
   {
      _data.Write( "p.csv", "id,name\n1,Hammer\n" );
      _data.Write( "q.csv", "id,name\n9,Saw\n" );
      await RunAsync();
      _data.Write( "q.csv", "id,name\n9,\"Saw never closed\n" );

      RunSnapshot run = await RunAsync();

      Assert.Equal( 0, run.DocsDeleted );
      Assert.Contains( run.Rejected, r => r.Origin == "q.csv" );
      Assert.Contains( "id: 9\nname: Saw", _a.Texts() );
   }

   /// <summary>
   /// When one sink fails, the other still gets everything and the run ends Partial; the
   /// failed sink catches up on the next run.
   /// </summary>
   [Fact]
   public async Task FailingSink_DoesNotBlockTheOther_AndCatchesUpLater()
   {
      _data.Write( "p.csv", "id,name\n1,Hammer\n2,Drill\n" );
      _b.FailUpserts = true;

      RunSnapshot run = await RunAsync();
      Assert.Equal( RunStatus.Partial, run.Status );
      Assert.Equal( 2, _a.Records.Count );
      Assert.Empty( _b.Records );

      _b.FailUpserts = false;
      _embedder.Embedded.Clear();
      RunSnapshot retry = await RunAsync();
      Assert.Equal( RunStatus.Completed, retry.Status );
      Assert.Equal( 2, _b.Records.Count );
      Assert.Equal( 2, _embedder.Embedded.Count );
   }

   /// <summary>
   /// A pipeline refuses a different embedder instead of mixing incompatible vectors.
   /// </summary>
   [Fact]
   public async Task DifferentEmbedder_IsRefused()
   {
      _data.Write( "p.csv", "id,name\n1,Hammer\n" );
      await RunAsync();
      _embedder.Fingerprint = "fake|v2";

      RunSnapshot run = await RunAsync();

      Assert.Equal( RunStatus.Failed, run.Status );
      Assert.Contains( "different embedder", run.Message );
   }

   /// <summary>
   /// Chunk ids are deterministic and long text splits under the size cap.
   /// </summary>
   [Fact]
   public void Chunker_IsDeterministicAndRespectsCap()
   {
      var doc = new Document( "t|1", "t", "f.csv", string.Join( "\n", Enumerable.Range( 0, 400 ).Select( i => $"line {i} with some words" ) ), "h",
         new Dictionary<string, string>() );
      var chunker = new TextChunker();
      IReadOnlyList<Chunk> first = chunker.Split( "p", doc );
      IReadOnlyList<Chunk> second = chunker.Split( "p", doc );

      Assert.True( first.Count > 1 );
      Assert.All( first, c => Assert.True( c.Text.Length <= TextChunker.MAX_CHUNK_CHARS ) );
      Assert.Equal( first.Select( c => c.ChunkId ), second.Select( c => c.ChunkId ) );
      Assert.Equal( first.Count, first.Select( c => c.ChunkId ).Distinct().Count() );
   }

   /// <summary>
   /// Without a key column, duplicate rows get distinct keys rather than overwriting each other.
   /// </summary>
   [Fact]
   public void Mapper_SuffixesDuplicateKeys()
   {
      var mapper = new RowDocumentMapper( new Dictionary<string, TableMapping>() );
      var columns = new[] { "a" };
      Document? one = mapper.Map( new SourceRecord( "t", "f", 1, columns, new string?[] { "same" } ) );
      Document? two = mapper.Map( new SourceRecord( "t", "f", 2, columns, new string?[] { "same" } ) );
      Assert.NotEqual( one!.DocKey, two!.DocKey );
      Assert.EndsWith( "#2", two.DocKey );
   }

   /// <summary>
   /// The whitelist still deletes what really left: a file removed from the folder takes its
   /// rows with it.
   /// </summary>
   [Fact]
   public async Task RemovedFile_IsDeleted()
   {
      _data.Write( "p.csv", "id,name\n1,Hammer\n2,Drill\n3,Saw\n" );
      string q = _data.Write( "q.csv", "id,name\n9,Level\n" );
      await RunAsync();
      File.Delete( q );

      RunSnapshot run = await RunAsync();

      Assert.Equal( RunStatus.Completed, run.Status );
      Assert.Equal( 1, run.DocsDeleted );
      Assert.DoesNotContain( "id: 9\nname: Level", _a.Texts() );
   }

   /// <summary>
   /// A sheet removed from a workbook that still opens is confirmed gone and its rows deleted.
   /// </summary>
   [Fact]
   public async Task RemovedSheet_IsDeleted()
   {
      _data.Write( "p.csv", "id,name\n1,Hammer\n2,Drill\n3,Saw\n" );
      WriteWorkbook( withOldSheet: true );
      await RunAsync();
      WriteWorkbook( withOldSheet: false );

      RunSnapshot run = await RunAsync();

      Assert.Equal( 1, run.DocsDeleted );
      Assert.DoesNotContain( "id: 8\nname: Retired", _a.Texts() );
      Assert.Contains( "id: 7\nname: Clamp", _a.Texts() );
   }

   /// <summary>
   /// The run summary carries rows skipped for extra cells, rows with an empty key, and stored
   /// rows kept because their file was not read completely.
   /// </summary>
   [Fact]
   public async Task Snapshot_ReportsSkippedMissingKeyAndKeptRows()
   {
      string rows = "id,name\n" + string.Concat( Enumerable.Range( 1, 30 ).Select( i => $"{i},Name {i}\n" ) );
      _data.Write( "p.csv", rows );
      _data.Write( "q.csv", "id,name\n99,Saw\n" );
      await RunAsync();
      _data.Write( "p.csv", rows.Replace( "\n7,Name 7\n", "\n7,Smith, John\n", StringComparison.Ordinal ).Replace( "\n8,Name 8\n", "\n,Name 8\n", StringComparison.Ordinal ) );
      _data.Write( "q.csv", "id,name\n99,\"Saw never closed\n" );

      RunSnapshot run = await RunAsync();

      Assert.Equal( 1, run.SkippedRows );
      Assert.Equal( 1, run.MissingKeyRows );
      Assert.Equal( 3, run.DocsKept );
      Assert.Equal( 0, run.DocsDeleted );
   }

   /// <summary>
   /// With large deletes allowed, a removal of most rows goes through.
   /// </summary>
   [Fact]
   public async Task AllowLargeDeletes_LetsAnExpectedRemovalThrough()
   {
      _data.Write( "p.csv", "id,name\n" + string.Concat( Enumerable.Range( 1, 10 ).Select( i => $"{i},Tool {i}\n" ) ) );
      await RunAsync();
      _data.Write( "p.csv", "id,name\n1,Tool 1\n" );

      RunSnapshot run = await RunAsync( allowLargeDeletes: true );

      Assert.Equal( RunStatus.Completed, run.Status );
      Assert.Equal( 9, run.DocsDeleted );
      Assert.Single( _a.Records );
   }

   /// <summary>
   /// Mappings are found by the table's stable id as well as its name, keys use the stable id,
   /// and a setting that matched nothing is reported.
   /// </summary>
   [Fact]
   public void Mapper_UsesStableTableIdAndReportsUnusedSettings()
   {
      var mapper = new RowDocumentMapper( new Dictionary<string, TableMapping>
      {
         ["t123"] = new TableMapping( "id", null ),
         ["old_name"] = new TableMapping( "id", null ),
         ["plain"] = new TableMapping( null, null ),
      } );

      Document? document = mapper.Map( new SourceRecord( "orders", "f.csv", 1, new[] { "id", "name" }, new string?[] { "5", "Hammer" }, "t123" ) );

      Assert.Equal( "t123|5", document!.DocKey );
      Assert.Equal( new[] { "old_name" }, mapper.UnusedMappings );
   }

   /// <summary>
   /// A rejected folder covers the files inside it, and a sheet the scan skipped on purpose is
   /// reported with that reason.
   /// </summary>
   [Fact]
   public void Report_CoversContainedOriginsAndKnowsIgnoredOnes()
   {
      var report = new SourceReadReport();
      report.MarkRejected( "sub", "folder cannot be read" );
      report.MarkIgnored( "book.xlsx#Old", "hidden sheet" );

      Assert.True( report.IsRejected( "sub/inner.csv" ) );
      Assert.False( report.IsRejected( "subway.csv" ) );
      Assert.Equal( "hidden sheet", report.IgnoredReason( "book.xlsx#Old" ) );
      Assert.Null( report.IgnoredReason( "book.xlsx#Parts" ) );
   }

   /// <summary>
   /// A sheet hidden since the last run keeps its rows (it still exists), and the run says why.
   /// </summary>
   [Fact]
   public async Task HiddenSheet_KeepsItsRowsAndSaysWhy()
   {
      _data.Write( "p.csv", "id,name\n1,Hammer\n2,Drill\n3,Saw\n" );
      WriteWorkbook( withOldSheet: true );
      await RunAsync();
      WriteWorkbook( withOldSheet: true, hideOld: true );

      RunSnapshot run = await RunAsync();

      Assert.Equal( RunStatus.Completed, run.Status );
      Assert.Equal( 0, run.DocsDeleted );
      Assert.Contains( "id: 8\nname: Retired", _a.Texts() );
      Assert.Contains( run.Errors, e => e.Contains( "book.xlsx#Old", StringComparison.Ordinal ) && e.Contains( "hidden sheet", StringComparison.Ordinal ) );
   }

   /// <summary>
   /// SQL staging rows are clamped to the column widths, so one over-long origin or table name
   /// cannot fail a whole bulk copy and drop the SQL sink.
   /// </summary>
   [Fact]
   public void SqlStagingRows_FitTheColumnWidths()
   {
      string origin = string.Concat( Enumerable.Repeat( "deep_folder/", 500 ) ) + "orders.csv";
      var document = new Document( new string( 'k', 600 ), new string( 't', 300 ), origin, "text", "h", new Dictionary<string, string>() );
      var record = new VectorRecord( document, new Chunk( Guid.NewGuid(), document.DocKey, 0, "text" ), new float[] { 1f } );

      DataRow row = SqlVectorSink.BuildStagingRows( new[] { record } ).Rows[0];

      Assert.Equal( 450, ( (string)row["DocKey"] ).Length );
      Assert.Equal( 256, ( (string)row["TableName"] ).Length );
      Assert.Equal( 4000, ( (string)row["Origin"] ).Length );
      Assert.EndsWith( "orders.csv", (string)row["Origin"] );
   }

   #endregion Public Methods

   #region Private Methods

   /// <summary>
   /// Scans the data folder and runs the pipeline into both memory sinks, keyed on "id".
   /// </summary>
   /// <param name="allowLargeDeletes">Turns off the mass-delete guard.</param>
   /// <returns>The final snapshot.</returns>
   private async Task<RunSnapshot> RunAsync( bool allowLargeDeletes = false )
   {
      FolderScanResult scan = new FolderScanner().Scan( _data.Path, CancellationToken.None );
      var mappings = scan.Tables.ToDictionary( t => t.Name, _ => new TableMapping( "id", null ) );
      var runner = new PipelineRunner( _embedder, new ISink[] { _a, _b }, _state, new TextChunker() ) { AllowLargeDeletes = allowLargeDeletes };
      var progress = new RunProgress( "test", PIPELINE );
      await runner.RunAsync( PIPELINE, new FolderSource( scan ), new RowDocumentMapper( mappings ), progress, CancellationToken.None );
      return progress.Snapshot();
   }

   /// <summary>
   /// Writes book.xlsx with a "Parts" sheet and, optionally, an "Old" sheet.
   /// </summary>
   /// <param name="withOldSheet">Include the "Old" sheet.</param>
   /// <param name="hideOld">Hide the "Old" sheet.</param>
   private void WriteWorkbook( bool withOldSheet, bool hideOld = false )
   {
      using var book = new XLWorkbook();
      AddSheet( book, "Parts", "7", "Clamp" );
      if( withOldSheet )
      {
         IXLWorksheet old = AddSheet( book, "Old", "8", "Retired" );
         if( hideOld )
         {
            old.Hide();
         }
      }

      book.SaveAs( Path.Combine( _data.Path, "book.xlsx" ) );
   }

   /// <summary>
   /// Adds an "id,name" sheet with one row.
   /// </summary>
   /// <param name="book">Workbook.</param>
   /// <param name="name">Sheet name.</param>
   /// <param name="id">Row id.</param>
   /// <param name="value">Row name.</param>
   /// <returns>The sheet.</returns>
   private static IXLWorksheet AddSheet( XLWorkbook book, string name, string id, string value )
   {
      IXLWorksheet sheet = book.AddWorksheet( name );
      sheet.Cell( 1, 1 ).Value = "id";
      sheet.Cell( 1, 2 ).Value = "name";
      sheet.Cell( 2, 1 ).Value = id;
      sheet.Cell( 2, 2 ).Value = value;
      return sheet;
   }

   #endregion Private Methods

   #region IDisposable

   /// <summary>
   /// Deletes the temp folders.
   /// </summary>
   public void Dispose()
   {
      _data.Dispose();
      _stateDir.Dispose();
   }

   #endregion IDisposable
}
