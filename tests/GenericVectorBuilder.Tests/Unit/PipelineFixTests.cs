using System.Text;
using ClosedXML.Excel;
using GenericVectorBuilder.Core.Chunking;
using GenericVectorBuilder.Core.Contracts;
using GenericVectorBuilder.Core.Mapping;
using GenericVectorBuilder.Core.Pipeline;
using GenericVectorBuilder.Core.Sources.Files;
using GenericVectorBuilder.Core.State;
using GenericVectorBuilder.Tests.Fakes;

namespace GenericVectorBuilder.Tests.Unit;

/// <summary>
/// Regression tests for the 2026-10-02 review of the pipeline: every test here reproduces a
/// verified finding and failed on the code before the fix. Most are about data safety: a row
/// may only leave the index when the run has proof it left the source.
/// </summary>
public class PipelineFixTests : IDisposable
{
   #region Data Members

   private const string PIPELINE = "fix";

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
   public PipelineFixTests()
   {
      _state = new SqliteStateStore( Path.Combine( _stateDir.Path, "state.db" ) );
   }

   #endregion Constructor

   #region Public Methods

   /// <summary>
   /// Finding (high): a workbook that will not open was rejected under its file name while its
   /// documents carry "file.xlsx#Sheet" origins, so every sheet was deleted. Its sheets must keep
   /// their vectors.
   /// </summary>
   [Fact]
   public async Task UnopenableWorkbook_KeepsItsSheetVectors()
   {
      _data.Write( "p.csv", Rows( "id,name", 10, "Tool" ) );
      WriteWorkbook( "book.xlsx", ("Parts", new[] { ("101", "Bolt"), ("102", "Washer") }) );
      await RunAsync();
      Assert.Equal( 12, _a.Records.Count );

      _data.WriteBytes( "book.xlsx", Encoding.ASCII.GetBytes( "this is not a workbook any more" ) );
      RunSnapshot run = await RunAsync();

      Assert.Contains( run.Rejected, r => r.Origin == "book.xlsx" );
      Assert.Equal( 0, run.DocsDeleted );
      Assert.Equal( 12, _a.Records.Count );
   }

   /// <summary>
   /// Finding (high): a subfolder that cannot be listed vanished from the scan with no reject,
   /// and its rows were deleted as "removed from the source". They must be kept, and the run
   /// must say so instead of reporting a clean Completed.
   /// </summary>
   [Fact]
   public async Task UnlistableSubfolder_KeepsItsVectors()
   {
      if( OperatingSystem.IsWindows() )
      {
         return; // Unix permissions are what this test takes away.
      }

      _data.Write( "top.csv", Rows( "id,name", 10, "Tool" ) );
      string inner = _data.Write( "sub/inner.csv", "id,name\n11,Clamp\n12,Vise\n" );
      await RunAsync();
      string sub = Path.GetDirectoryName( inner )!;
      File.SetUnixFileMode( sub, UnixFileMode.None );
      try
      {
         if( CanList( sub ) )
         {
            return; // Running as root: permissions are not enforced, so there is nothing to test.
         }

         RunSnapshot run = await RunAsync();

         Assert.Equal( 0, run.DocsDeleted );
         Assert.Equal( 12, _a.Records.Count );

         // The user must be told, either by a reject entry for the folder or by a note.
         bool rejected = run.Rejected.Any( r => r.Origin == "sub" );
         Assert.True( rejected || ( run.Status == RunStatus.Partial && run.Errors.Any( e => e.Contains( "sub/inner.csv", StringComparison.Ordinal ) ) ) );
      }
      finally
      {
         File.SetUnixFileMode( sub, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute );
      }
   }

   /// <summary>
   /// Finding (medium): an empty folder (an unmounted share looks exactly like this) wiped the
   /// whole pipeline. A run that read zero rows must not delete anything.
   /// </summary>
   [Fact]
   public async Task EmptiedFolder_DoesNotWipeThePipeline()
   {
      string file = _data.Write( "p.csv", "id,name\n1,Hammer\n2,Drill\n" );
      await RunAsync();
      File.Delete( file );

      RunSnapshot run = await RunAsync();

      Assert.Equal( 0, run.DocsDeleted );
      Assert.Equal( 2, _a.Records.Count );
      Assert.Equal( RunStatus.Partial, run.Status );
      Assert.Contains( "Nothing was deleted", run.Message );
   }

   /// <summary>
   /// Finding (medium), second half of the guard: a run that would delete more than half of
   /// what a sink holds is refused, because that is usually a wrong or half-copied folder.
   /// </summary>
   [Fact]
   public async Task MostRowsMissing_DeletesAreRefused()
   {
      _data.Write( "p.csv", Rows( "id,name", 10, "Tool" ) );
      await RunAsync();
      _data.Write( "p.csv", "id,name\n1,Tool 1\n" );

      RunSnapshot run = await RunAsync();

      Assert.Equal( 0, run.DocsDeleted );
      Assert.Equal( 10, _a.Records.Count );
      Assert.Equal( RunStatus.Partial, run.Status );
      Assert.Contains( "more than half", run.Message );
   }

   /// <summary>
   /// Finding (medium): a row skipped for having an extra cell was deleted from the sinks and
   /// the run still said Completed. The old row must be kept and the skip reported.
   /// </summary>
   [Fact]
   public async Task SkippedWideRow_KeepsItsOldVectorAndIsReported()
   {
      _data.Write( "p.csv", Rows( "id,name", 30, "Name" ) );
      await RunAsync();
      _data.Write( "p.csv", Rows( "id,name", 30, "Name" ).Replace( "\n7,Name 7\n", "\n7,Smith, John\n", StringComparison.Ordinal ) );

      RunSnapshot run = await RunAsync();

      Assert.Equal( 0, run.DocsDeleted );
      Assert.Contains( "id: 7\nname: Name 7", _a.Texts() );
      Assert.Equal( RunStatus.Partial, run.Status );
      Assert.Contains( run.Errors, e => e.Contains( "p.csv", StringComparison.Ordinal ) && e.Contains( "skipped", StringComparison.Ordinal ) );
   }

   /// <summary>
   /// Finding (medium): when a sink's collection was wiped and recreated, the old state said
   /// everything was unchanged and the sink stayed empty forever. It must be rewritten.
   /// </summary>
   [Fact]
   public async Task RecreatedCollection_IsWrittenAgain()
   {
      _data.Write( "p.csv", "id,name\n1,Hammer\n2,Drill\n" );
      await RunAsync();
      await _a.DropCollectionAsync( PIPELINE, CancellationToken.None );
      _embedder.Embedded.Clear();

      RunSnapshot run = await RunAsync();

      Assert.Equal( 2, _a.Records.Count );
      Assert.Equal( 2, _b.Records.Count );
      Assert.Equal( 2, run.DocsEmbedded );
   }

   /// <summary>
   /// Finding (medium): when the stale-chunk delete failed after an upsert, state never learned
   /// the new chunk id, and the next content change orphaned it for good.
   /// </summary>
   [Fact]
   public async Task FailedStaleDelete_LeavesNoOrphanChunk()
   {
      _data.Write( "p.csv", "id,name\n1,Hammer\n2,Drill\n" );
      await RunAsync();
      _data.Write( "p.csv", "id,name\n1,Claw\n2,Drill\n" );
      _a.FailDeletes = true;
      await RunAsync();
      _a.FailDeletes = false;
      _data.Write( "p.csv", "id,name\n1,Claw Hammer\n2,Drill\n" );

      await RunAsync();

      Assert.Equal( new[] { "id: 1\nname: Claw Hammer", "id: 2\nname: Drill" }, _a.Texts() );
   }

   /// <summary>
   /// The cleanup half of the orphan fix: an unchanged row whose state lists a chunk id the
   /// current chunking does not produce has that chunk deleted and its state trimmed.
   /// </summary>
   [Fact]
   public async Task LeftoverChunkOnUnchangedRow_IsRemoved()
   {
      _data.Write( "p.csv", "id,name\n1,Hammer\n2,Drill\n" );
      await RunAsync();
      Dictionary<string, DocState> states = await _state.LoadAsync( PIPELINE, "a", CancellationToken.None );
      DocState hammer = states.Values.Single( s => s.DocKey.EndsWith( "|1", StringComparison.Ordinal ) );
      Guid extra = Guid.NewGuid();
      VectorRecord original = _a.Records[hammer.ChunkIds[0]];
      _a.Records[extra] = original with { Chunk = original.Chunk with { ChunkId = extra, Text = "leftover" } };
      await _state.SaveAsync( PIPELINE, "a", new[] { hammer with { ChunkIds = hammer.ChunkIds.Append( extra ).ToList() } }, CancellationToken.None );

      await RunAsync();

      Assert.DoesNotContain( "leftover", _a.Texts() );
      Dictionary<string, DocState> after = await _state.LoadAsync( PIPELINE, "a", CancellationToken.None );
      Assert.DoesNotContain( extra, after[hammer.DocKey].ChunkIds );
   }

   /// <summary>
   /// Finding (low): cancelling during the delete phase said "nothing was deleted" although the
   /// sink had already removed rows. The message must be truthful and state must match the sink.
   /// </summary>
   [Fact]
   public async Task CancelDuringDelete_ReportsWhatWasRemoved()
   {
      _data.Write( "p.csv", "id,name\n1,Hammer\n2,Drill\n3,Saw\n4,Level\n" );
      await RunAsync();
      _data.Write( "p.csv", "id,name\n1,Hammer\n2,Drill\n3,Saw\n" );
      using var cancel = new CancellationTokenSource();
      _a.AfterDelete = cancel.Cancel;

      RunSnapshot run = await RunAsync( "id", cancel.Token );

      Assert.Equal( RunStatus.Cancelled, run.Status );
      Assert.DoesNotContain( "nothing was deleted", run.Message );
      Assert.Equal( 1, run.DocsDeleted );
      Assert.Equal( 3, ( await _state.LoadAsync( PIPELINE, "a", CancellationToken.None ) ).Count );
   }

   /// <summary>
   /// Finding (low): without a key column, identical rows in two files were keyed by read order,
   /// so when the first file was rejected the second file's copy was deleted. Each file's copy
   /// must keep its own identity.
   /// </summary>
   [Fact]
   public async Task IdenticalRowsInTwoFiles_RejectedFileKeepsItsCopy()
   {
      _data.Write( "p.csv", "id,name\n1,Hammer\n" );
      _data.Write( "q.csv", "id,name\n1,Hammer\n" );
      await RunAsync( keyColumn: null );
      Assert.Equal( 2, _a.Records.Count );
      _data.Write( "p.csv", "id,name\n1,\"Hammer never closed\n" );

      RunSnapshot run = await RunAsync( keyColumn: null );

      Assert.Contains( run.Rejected, r => r.Origin == "p.csv" );
      Assert.Equal( 0, run.DocsDeleted );
      Assert.Equal( 2, _a.Records.Count );
   }

   /// <summary>
   /// Finding (low): the table name is derived from file stems and is part of every key, so a
   /// file leaving the folder renamed the table and re-embedded every row. Keys now use a stable
   /// id from the header, so the remaining rows are untouched.
   /// </summary>
   [Fact]
   public async Task TableRenamedByAFileLeaving_IsNotReembedded()
   {
      _data.Write( "top.csv", "id,name\n1,Hammer\n2,Drill\n3,Saw\n" );
      _data.Write( "sub/inner.csv", "id,name\n4,Clamp\n" );
      await RunAsync();
      Directory.Delete( Path.Combine( _data.Path, "sub" ), recursive: true );
      _embedder.Embedded.Clear();

      RunSnapshot run = await RunAsync();

      Assert.Empty( _embedder.Embedded );
      Assert.Equal( 3, run.DocsUnchanged );
      Assert.Equal( 1, run.DocsDeleted );
   }

   /// <summary>
   /// Finding (low): the generated duplicate suffix was not reserved, so a real key "5#2" got
   /// the same document key as the second "5" and one of them was lost every run.
   /// </summary>
   [Fact]
   public void DuplicateSuffix_CannotCollideWithARealKey()
   {
      var mapper = new RowDocumentMapper( new Dictionary<string, TableMapping> { ["t"] = new TableMapping( "id", null ) } );
      var columns = new[] { "id", "name" };

      string[] keys = new[] { ("5", "Alpha"), ("5", "Beta"), ("5#2", "Gamma") }
         .Select( ( r, i ) => mapper.Map( new SourceRecord( "t", "f.csv", i + 1, columns, new string?[] { r.Item1, r.Item2 } ) )!.DocKey )
         .ToArray();

      Assert.Equal( 3, keys.Distinct( StringComparer.Ordinal ).Count() );
   }

   /// <summary>
   /// Finding (medium): a long key value made a document key longer than the SQL sink's 450
   /// character DocKey column, which failed every batch containing it. Keys are capped, and the
   /// cap is deterministic so the row keeps its identity across runs.
   /// </summary>
   [Fact]
   public void LongKeyValue_IsCappedAndStable()
   {
      var mappings = new Dictionary<string, TableMapping> { ["t"] = new TableMapping( "description", null ) };
      var columns = new[] { "description" };
      var values = new string?[] { new string( 'x', 1000 ) };

      string first = new RowDocumentMapper( mappings ).Map( new SourceRecord( "t", "f.csv", 1, columns, values ) )!.DocKey;
      string second = new RowDocumentMapper( mappings ).Map( new SourceRecord( "t", "f.csv", 1, columns, values ) )!.DocKey;

      Assert.True( first.Length <= RowDocumentMapper.MAX_KEY_CHARS, $"key is {first.Length} characters" );
      Assert.Equal( first, second );
   }

   /// <summary>
   /// Finding (low): templates were filled column by column on the already filled text, so a
   /// cell containing "{B}" was expanded with column B's value.
   /// </summary>
   [Fact]
   public void Template_IsFilledInOnePass()
   {
      var mapper = new RowDocumentMapper( new Dictionary<string, TableMapping> { ["t"] = new TableMapping( null, "A={A} B={B}" ) } );

      Document? document = mapper.Map( new SourceRecord( "t", "f.csv", 1, new[] { "A", "B" }, new string?[] { "see {B}", "SECRET" } ) );

      Assert.Equal( "A=see {B} B=SECRET", document!.Text );
   }

   /// <summary>
   /// Finding (high), the report side: the reject list protects a workbook's sheets when the
   /// workbook itself was rejected.
   /// </summary>
   [Fact]
   public void RejectedWorkbook_CoversItsSheets()
   {
      var report = new SourceReadReport();
      report.MarkRejected( "book.xlsx", "workbook will not open" );

      Assert.True( report.IsRejected( "book.xlsx#Parts" ) );
      Assert.False( report.IsRejected( "book.xlsx.csv" ) );
      Assert.False( report.IsRejected( "book" ) );
   }

   #endregion Public Methods

   #region Private Methods

   /// <summary>
   /// Scans the data folder and runs the pipeline into both memory sinks.
   /// </summary>
   /// <param name="keyColumn">Key column for every table, or null for content keys.</param>
   /// <param name="ct">Cancellation for the run.</param>
   /// <returns>The final snapshot.</returns>
   private async Task<RunSnapshot> RunAsync( string? keyColumn = "id", CancellationToken ct = default )
   {
      FolderScanResult scan = new FolderScanner().Scan( _data.Path, CancellationToken.None );
      var mappings = scan.Tables.ToDictionary( t => t.Name, _ => new TableMapping( keyColumn, null ) );
      var runner = new PipelineRunner( _embedder, new ISink[] { _a, _b }, _state, new TextChunker() );
      var progress = new RunProgress( "test", PIPELINE );
      await runner.RunAsync( PIPELINE, new FolderSource( scan ), new RowDocumentMapper( mappings ), progress, ct );
      return progress.Snapshot();
   }

   /// <summary>
   /// Builds CSV text with a header and rows "n,{prefix} n".
   /// </summary>
   /// <param name="header">Header line.</param>
   /// <param name="count">Number of rows.</param>
   /// <param name="prefix">Text before each row number.</param>
   /// <returns>The CSV text.</returns>
   private static string Rows( string header, int count, string prefix )
   {
      return header + "\n" + string.Concat( Enumerable.Range( 1, count ).Select( i => $"{i},{prefix} {i}\n" ) );
   }

   /// <summary>
   /// Writes a workbook with "id,name" sheets.
   /// </summary>
   /// <param name="relative">Path relative to the data folder.</param>
   /// <param name="sheets">Sheet names with their rows.</param>
   private void WriteWorkbook( string relative, params (string Name, (string Id, string Name)[] Rows)[] sheets )
   {
      using var book = new XLWorkbook();
      foreach( var (name, rows) in sheets )
      {
         IXLWorksheet sheet = book.AddWorksheet( name );
         sheet.Cell( 1, 1 ).Value = "id";
         sheet.Cell( 1, 2 ).Value = "name";
         for( int i = 0; i < rows.Length; i++ )
         {
            sheet.Cell( i + 2, 1 ).Value = rows[i].Id;
            sheet.Cell( i + 2, 2 ).Value = rows[i].Name;
         }
      }

      book.SaveAs( Path.Combine( _data.Path, relative ) );
   }

   /// <summary>
   /// True when the current user can list a directory despite its permissions (root can).
   /// </summary>
   /// <param name="path">Directory.</param>
   /// <returns>True when listing works.</returns>
   private static bool CanList( string path )
   {
      try
      {
         Directory.GetFileSystemEntries( path );
         return true;
      }
      catch( UnauthorizedAccessException )
      {
         return false;
      }
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
