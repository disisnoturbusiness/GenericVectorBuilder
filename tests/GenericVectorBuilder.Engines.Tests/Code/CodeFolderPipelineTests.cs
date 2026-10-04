using System.Runtime.Versioning;
using System.Text;
using GenericVectorBuilder.Code.Chunking;
using GenericVectorBuilder.Code.Mapping;
using GenericVectorBuilder.Code.Sources;
using GenericVectorBuilder.Core.Contracts;
using GenericVectorBuilder.Core.Pipeline;
using GenericVectorBuilder.Core.State;

namespace GenericVectorBuilder.Engines.Tests.Code;

/// <summary>
/// The code folder source run through the real pipeline with a fake embedder, in-memory sinks
/// and the real SQLite state store: which files are read, what a re-run costs, and when stored
/// files are deleted or kept.
/// </summary>
public class CodeFolderPipelineTests : IDisposable
{
   #region Data Members

   private const string PIPELINE = "codeunit";
   private const string COMMIT = "0123456789abcdef0123456789abcdef01234567";

   private readonly CodeTempFolder _repo = new();
   private readonly CodeTempFolder _stateDir = new();
   private readonly SqliteStateStore _state;
   private readonly CodeFakeEmbedder _embedder = new();
   private readonly CodeMemorySink _sink = new( "mem" );

   #endregion Data Members

   #region Constructor

   /// <summary>
   /// Sets up a fresh state store per test.
   /// </summary>
   public CodeFolderPipelineTests()
   {
      _state = new SqliteStateStore( Path.Combine( _stateDir.Path, "state.db" ) );
   }

   #endregion Constructor

   #region Public Methods

   /// <summary>
   /// Only included files outside excluded folders are read; links, binaries, big files and
   /// other file types are skipped with a reason.
   /// </summary>
   [Fact]
   public async Task Source_ReadsOnlyIncludedFiles()
   {
      WriteRepo();
      _repo.Write( "bin/Debug/Generated.cs", "class Generated { }" );
      _repo.Write( "src/Web/wwwroot/lib/jquery/Fake.cs", "class Fake { }" );
      _repo.Write( "node_modules/pkg/Index.cs", "class Index { }" );
      _repo.Write( "README.md", "# readme" );
      _repo.WriteBytes( "src/Blob.cs", new byte[] { 0x4D, 0x5A, 0x00, 0x01, 0x02 } );
      _repo.Write( "src/Huge.cs", new string( 'x', 20000 ) );
      File.CreateSymbolicLink( Path.Combine( _repo.Path, "src", "Link.cs" ), Path.Combine( _repo.Path, "src", "A.cs" ) );
      var source = new CodeFolderSource( _repo.Path, CodeFolderOptions.Default with { MaxFileBytes = 10000 } );

      List<SourceRecord> records = await ReadAll( source );

      Assert.Equal( new[] { "src/A.cs", "src/B.cs", "src/Sub/C.cs" }, records.Select( r => r.Origin ) );
      Assert.All( records, r => Assert.Equal( "code:" + Path.GetFileName( _repo.Path ), r.TableId ) );
      Assert.True( source.Report.Completed );
      Assert.Equal( "binary file", source.Report.IgnoredReason( "src/Blob.cs" ) );
      Assert.Equal( "excluded folder", source.Report.IgnoredReason( "bin/Debug/Generated.cs" ) );
      Assert.Equal( "excluded folder", source.Report.IgnoredReason( "src/Web/wwwroot/lib/jquery/Fake.cs" ) );
      Assert.Contains( "link", source.Report.IgnoredReason( "src/Link.cs" ) );
      Assert.Contains( "larger than", source.Report.IgnoredReason( "src/Huge.cs" ) );
      Assert.Contains( "not an included file type", source.Report.IgnoredReason( "README.md" ) );
   }

   /// <summary>
   /// Listing the files gives exactly the files a read emits, in the same order, without
   /// reading them, and records the same skips.
   /// </summary>
   [Fact]
   public async Task ListFiles_IsExactlyWhatAReadEmits()
   {
      WriteRepo();
      _repo.Write( "bin/Debug/Generated.cs", "class Generated { }" );
      _repo.Write( "README.md", "# readme" );

      IReadOnlyList<string> listed = new CodeFolderSource( _repo.Path ).ListFiles( CancellationToken.None );
      var reader = new CodeFolderSource( _repo.Path );
      List<SourceRecord> read = await ReadAll( reader );

      Assert.Equal( read.Select( r => r.Origin ), listed );
      Assert.Equal( new[] { "src/A.cs", "src/B.cs", "src/Sub/C.cs" }, listed );
   }

   /// <summary>
   /// The first run embeds every file with path, language and commit metadata; a second run over
   /// the same files embeds nothing.
   /// </summary>
   [Fact]
   public async Task Rerun_UnchangedFiles_EmbedsNothing()
   {
      WriteRepo();
      RunSnapshot first = await RunAsync();
      Assert.Equal( RunStatus.Completed, first.Status );
      Assert.Equal( 3, first.DocsEmbedded );
      Assert.True( first.ChunksEmbedded > 3, "A.cs is big enough to be split by member" );
      VectorRecord sample = _sink.Records.Values.First( r => r.Document.DocKey == "code|src/A.cs" );
      Assert.Equal( "src/A.cs", sample.Document.Metadata["path"] );
      Assert.Equal( "csharp", sample.Document.Metadata["language"] );
      Assert.Equal( COMMIT, sample.Document.Metadata["commit"] );

      _embedder.Embedded.Clear();
      RunSnapshot second = await RunAsync();

      Assert.Equal( RunStatus.Completed, second.Status );
      Assert.Empty( _embedder.Embedded );
      Assert.Equal( 0, second.DocsEmbedded );
      Assert.Equal( 3, second.DocsUnchanged );
   }

   /// <summary>
   /// An edited file is the only one embedded again.
   /// </summary>
   [Fact]
   public async Task EditedFile_IsTheOnlyOneReembedded()
   {
      WriteRepo();
      await RunAsync();
      _repo.Write( "src/B.cs", "namespace Shop;\n\npublic class B\n{\n   public int Value => 42;\n}\n" );
      _embedder.Embedded.Clear();

      RunSnapshot run = await RunAsync();

      Assert.Equal( 1, run.DocsEmbedded );
      Assert.All( _embedder.Embedded, t => Assert.Contains( "src/B.cs", t, StringComparison.Ordinal ) );
      Assert.Contains( _sink.Texts(), t => t.Contains( "Value => 42", StringComparison.Ordinal ) );
   }

   /// <summary>
   /// A deleted file has every one of its chunks removed from the sink.
   /// </summary>
   [Fact]
   public async Task DeletedFile_IsRemoved()
   {
      WriteRepo();
      await RunAsync();
      Assert.Contains( _sink.Records.Values, r => r.Document.DocKey == "code|src/Sub/C.cs" );
      File.Delete( Path.Combine( _repo.Path, "src", "Sub", "C.cs" ) );

      RunSnapshot run = await RunAsync();

      Assert.Equal( RunStatus.Completed, run.Status );
      Assert.Equal( 1, run.DocsDeleted );
      Assert.DoesNotContain( _sink.Records.Values, r => r.Document.DocKey == "code|src/Sub/C.cs" );
      Assert.Contains( _sink.Records.Values, r => r.Document.DocKey == "code|src/A.cs" );
   }

   /// <summary>
   /// A subfolder that cannot be read is rejected, and the files stored from it are kept rather
   /// than deleted as if they had been removed.
   /// </summary>
   [Fact]
   [UnsupportedOSPlatform( "windows" )]
   public async Task UnreadableSubfolder_KeepsItsFiles()
   {
      WriteRepo();
      await RunAsync();
      string locked = Path.Combine( _repo.Path, "src", "Sub" );
      File.SetUnixFileMode( locked, UnixFileMode.None );
      try
      {
         RunSnapshot run = await RunAsync();

         Assert.Equal( 0, run.DocsDeleted );
         Assert.Contains( run.Rejected, r => r.Origin == "src/Sub" );
         Assert.Contains( _sink.Records.Values, r => r.Document.DocKey == "code|src/Sub/C.cs" );
      }
      finally
      {
         File.SetUnixFileMode( locked, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute );
      }
   }

   /// <summary>
   /// A file is confirmed gone only when a working listing proves it absent; a file in an
   /// unreadable folder, or one skipped on purpose, is never confirmed gone.
   /// </summary>
   [Fact]
   public async Task ConfirmGone_OnlyForProvenAbsence()
   {
      WriteRepo();
      _repo.Write( "bin/Old.cs", "class Old { }" );
      var source = new CodeFolderSource( _repo.Path );
      await ReadAll( source );

      Assert.True( await source.ConfirmGoneAsync( "src/Deleted.cs", CancellationToken.None ) );
      Assert.True( await source.ConfirmGoneAsync( "src/GoneFolder/X.cs", CancellationToken.None ) );
      Assert.False( await source.ConfirmGoneAsync( "src/A.cs", CancellationToken.None ) );
      Assert.False( await source.ConfirmGoneAsync( "bin/Old.cs", CancellationToken.None ) );
      Assert.False( await source.ConfirmGoneAsync( "../outside.cs", CancellationToken.None ) );
   }

   #endregion Public Methods

   #region Private Methods

   /// <summary>
   /// Writes three C# files: a large one that splits by member, a small one, and one in a subfolder.
   /// </summary>
   private void WriteRepo()
   {
      var big = new StringBuilder( "using System;\n\nnamespace Shop;\n\npublic class A\n{\n   private int _total;\n\n" );
      for( int m = 0; m < 6; m++ )
      {
         big.Append( $"   /// <summary>Step {m}.</summary>\n   public int Step{m}( int x )\n   {{\n" );
         for( int i = 0; i < 6; i++ )
         {
            big.Append( $"      _total += x * {i} + {m}; // accumulate part {i} of step {m} for the running total\n" );
         }

         big.Append( "      return _total;\n   }\n\n" );
      }

      _repo.Write( "src/A.cs", big.Append( "}\n" ).ToString() );
      _repo.Write( "src/B.cs", "namespace Shop;\n\npublic class B\n{\n   public int Value => 1;\n}\n" );
      _repo.Write( "src/Sub/C.cs", "namespace Shop.Sub;\n\npublic record C( int Id );\n" );
   }

   /// <summary>
   /// Runs the pipeline over the temp repository with the code chunker and mapper.
   /// </summary>
   /// <returns>The final snapshot.</returns>
   private async Task<RunSnapshot> RunAsync()
   {
      var runner = new PipelineRunner( _embedder, new ISink[] { _sink }, _state, new RoslynCodeChunker() );
      var progress = new RunProgress( "test", PIPELINE );
      await runner.RunAsync( PIPELINE, new CodeFolderSource( _repo.Path ), new CodeDocumentMapper( COMMIT ), progress, CancellationToken.None );
      return progress.Snapshot();
   }

   /// <summary>
   /// Reads every record of a source.
   /// </summary>
   /// <param name="source">The source.</param>
   /// <returns>The records.</returns>
   private static async Task<List<SourceRecord>> ReadAll( ISource source )
   {
      var records = new List<SourceRecord>();
      await foreach( SourceRecord record in source.ReadAsync( CancellationToken.None ) )
      {
         records.Add( record );
      }

      return records;
   }

   #endregion Private Methods

   #region IDisposable

   /// <summary>
   /// Releases the temp folders.
   /// </summary>
   public void Dispose()
   {
      _repo.Dispose();
      _stateDir.Dispose();
   }

   #endregion IDisposable
}
