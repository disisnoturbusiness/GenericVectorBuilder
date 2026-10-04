using System.Globalization;
using System.Text;
using GenericVectorBuilder.Code.Chunking;
using GenericVectorBuilder.Code.Git;
using GenericVectorBuilder.Code.Mapping;
using GenericVectorBuilder.Code.Sources;
using GenericVectorBuilder.Core.Chunking;
using GenericVectorBuilder.Core.Contracts;
using GenericVectorBuilder.Core.Pipeline;
using GenericVectorBuilder.Core.State;
using Microsoft.CodeAnalysis.Text;
using Xunit.Abstractions;

namespace GenericVectorBuilder.Engines.Tests.Code;

/// <summary>
/// Clones the AzureDevOpsForager demo corpus (eShopOnWeb, latest commit only) into the real
/// repos folder, chunks every C# file, and prints the shape of the result: file count against
/// the demo's 254, total chunks, longest chunk, and how many chunks each file became. Then runs
/// the whole pipeline twice with a fake embedder to prove the second run embeds nothing.
/// Needs the network; run with --filter Category=Network.
/// </summary>
[Trait( "Category", "Network" )]
public class EShopOnWebCorpusTests
{
   #region Data Members

   private const string URL = "https://github.com/dotnet-architecture/eShopOnWeb";
   private const int DEMO_CS_FILES = 254;

   private readonly ITestOutputHelper _output;

   #endregion Data Members

   #region Constructor

   /// <summary>
   /// Receives xunit's output sink for the report.
   /// </summary>
   /// <param name="output">Test output.</param>
   public EShopOnWebCorpusTests( ITestOutputHelper output )
   {
      _output = output;
   }

   #endregion Constructor

   #region Public Methods

   /// <summary>
   /// Clone, chunk every file, check the cap and full coverage, and report the numbers.
   /// </summary>
   [Fact]
   public async Task CloneAndChunk_ReportsCorpusShape()
   {
      var repo = new GitRepository( URL );
      string sha = await repo.SyncAsync( CancellationToken.None );
      var source = new CodeFolderSource( repo.LocalPath );
      var mapper = new CodeDocumentMapper( sha );
      var perFile = new List<(string Path, int Length, List<string> Chunks, bool Exact, bool CharsOnly)>();
      await foreach( SourceRecord record in source.ReadAsync( CancellationToken.None ) )
      {
         Document? document = mapper.Map( record );
         if( document != null )
         {
            perFile.Add( Measure( document ) );
         }
      }

      Report( repo, sha, source, perFile );
      Assert.True( source.Report.Completed );
      Assert.True( perFile.Count > 200, $"only {perFile.Count} C# files" );
      Assert.All( perFile, f => Assert.True( f.Exact || f.CharsOnly, $"{f.Path} lost content" ) );
      Assert.All( perFile.SelectMany( f => f.Chunks ), c => Assert.True( c.Length <= TextChunker.MAX_CHUNK_CHARS ) );
   }

   /// <summary>
   /// The whole pipeline over the clone, twice: the first run embeds every file, the second
   /// embeds nothing.
   /// </summary>
   [Fact]
   public async Task Pipeline_TwiceOverTheClone_SecondRunEmbedsNothing()
   {
      var repo = new GitRepository( URL );
      string sha = await repo.SyncAsync( CancellationToken.None );
      using var stateDir = new CodeTempFolder();
      var state = new SqliteStateStore( Path.Combine( stateDir.Path, "state.db" ) );
      var embedder = new CodeFakeEmbedder();
      var sink = new CodeMemorySink( "mem" );

      RunSnapshot first = await RunAsync( repo, sha, state, embedder, sink );
      int firstEmbedded = embedder.Embedded.Count;
      embedder.Embedded.Clear();
      RunSnapshot second = await RunAsync( repo, sha, state, embedder, sink );

      _output.WriteLine( $"run 1: {first.Status}, {first.DocsEmbedded} files, {first.ChunksEmbedded} chunks embedded ({firstEmbedded} texts), sink holds {sink.Records.Count}" );
      _output.WriteLine( $"run 2: {second.Status}, {second.DocsUnchanged} unchanged, {second.DocsEmbedded} files and {embedder.Embedded.Count} chunks embedded" );
      Assert.Equal( RunStatus.Completed, first.Status );
      Assert.Equal( RunStatus.Completed, second.Status );
      Assert.Empty( embedder.Embedded );
      Assert.Equal( first.DocsEmbedded, second.DocsUnchanged );
   }

   #endregion Public Methods

   #region Private Methods

   /// <summary>
   /// Chunks one file and checks that its chunks, without their headers, give back the file.
   /// </summary>
   /// <param name="document">The file's document.</param>
   /// <returns>Path, length, chunk texts, whether it reassembles exactly, and whether it does so once line breaks are ignored.</returns>
   private static (string Path, int Length, List<string> Chunks, bool Exact, bool CharsOnly) Measure( Document document )
   {
      List<string> chunks = new RoslynCodeChunker().Split( "eshop", document ).Select( c => c.Text ).ToList();
      SourceText text = SourceText.From( document.Text );
      string normalized = string.Join( "\n", text.Lines.Select( l => text.ToString( l.Span ) ) );
      string rebuilt = RoslynCodeChunkerTests.Reconstruct( chunks );
      bool exact = rebuilt == normalized;
      bool charsOnly = !exact && rebuilt.Replace( "\n", string.Empty ) == normalized.Replace( "\n", string.Empty );
      return (document.Metadata["path"], document.Text.Length, chunks, exact, charsOnly);
   }

   /// <summary>
   /// Writes the corpus report to the test output.
   /// </summary>
   /// <param name="repo">The clone.</param>
   /// <param name="sha">Its commit.</param>
   /// <param name="source">The source, for what it skipped.</param>
   /// <param name="files">Per-file results.</param>
   private void Report( GitRepository repo, string sha, CodeFolderSource source, List<(string Path, int Length, List<string> Chunks, bool Exact, bool CharsOnly)> files )
   {
      List<string> all = files.SelectMany( f => f.Chunks ).ToList();
      List<int> perFile = files.Select( f => f.Chunks.Count ).OrderBy( n => n ).ToList();
      List<int> lengths = all.Select( c => c.Length ).OrderBy( n => n ).ToList();
      var largest = files.OrderByDescending( f => f.Length ).First();
      var s = new StringBuilder();
      s.AppendLine( $"clone: {repo.LocalPath} at {sha}" );
      s.AppendLine( $".cs files read: {files.Count} (demo indexed {DEMO_CS_FILES}, difference {files.Count - DEMO_CS_FILES:+0;-0;0})" );
      s.AppendLine( $"skipped on purpose: {string.Join( ", ", source.Ignored.GroupBy( i => Bucket( i.Reason ) ).OrderBy( g => g.Key ).Select( g => $"{g.Key} {g.Count()}" ) )}" );
      s.AppendLine( $"total chunks: {all.Count}; split parts: {all.Count( c => c[..c.IndexOf( '\n' )].Contains( "(part ", StringComparison.Ordinal ) )}" );
      s.AppendLine( $"chunk length: min {lengths[0]}, median {Pct( lengths, 50 )}, p90 {Pct( lengths, 90 )}, max {lengths[^1]} (cap {TextChunker.MAX_CHUNK_CHARS})" );
      s.AppendLine( $"chunks per file: min {perFile[0]}, median {Pct( perFile, 50 )}, p90 {Pct( perFile, 90 )}, max {perFile[^1]}, mean {perFile.Average():0.00}" );
      s.AppendLine( $"distribution: {string.Join( ", ", new[] { (1, 1), (2, 2), (3, 5), (6, 10), (11, 20), (21, int.MaxValue) }.Select( b => $"{Range( b )}: {perFile.Count( n => n >= b.Item1 && n <= b.Item2 )}" ) )}" );
      s.AppendLine( $"files over one chunk of text ({TextChunker.MAX_CHUNK_CHARS} chars): {files.Count( f => f.Length > TextChunker.MAX_CHUNK_CHARS )}" );
      s.AppendLine( $"largest file: {largest.Path}, {largest.Length} chars -> {largest.Chunks.Count} chunks" );
      s.AppendLine( $"coverage: {files.Count( f => f.Exact )} files reassemble exactly, {files.Count( f => f.CharsOnly )} only after cutting an over-long line, {files.Count( f => !f.Exact && !f.CharsOnly )} lost content" );
      foreach( var top in files.OrderByDescending( f => f.Chunks.Count ).Take( 5 ) )
      {
         s.AppendLine( $"  most chunks: {top.Path} ({top.Length} chars) -> {top.Chunks.Count}" );
      }

      _output.WriteLine( s.ToString() );
   }

   /// <summary>
   /// Runs the pipeline over the clone with the code chunker and mapper.
   /// </summary>
   /// <param name="repo">The clone.</param>
   /// <param name="sha">Its commit.</param>
   /// <param name="state">State store.</param>
   /// <param name="embedder">Embedder.</param>
   /// <param name="sink">Sink.</param>
   /// <returns>The final snapshot.</returns>
   private static async Task<RunSnapshot> RunAsync( GitRepository repo, string sha, SqliteStateStore state, CodeFakeEmbedder embedder, CodeMemorySink sink )
   {
      var runner = new PipelineRunner( embedder, new ISink[] { sink }, state, new RoslynCodeChunker() );
      var progress = new RunProgress( "eshop", "eshop" );
      await runner.RunAsync( "eshop", new CodeFolderSource( repo.LocalPath ), new CodeDocumentMapper( sha ), progress, CancellationToken.None );
      return progress.Snapshot();
   }

   /// <summary>
   /// The value at a percentile of a sorted list (nearest rank).
   /// </summary>
   /// <param name="sorted">Sorted values.</param>
   /// <param name="percent">Percentile, 0 to 100.</param>
   /// <returns>The value.</returns>
   private static int Pct( List<int> sorted, int percent )
   {
      int rank = (int)Math.Ceiling( percent / 100.0 * sorted.Count );
      return sorted[Math.Clamp( rank - 1, 0, sorted.Count - 1 )];
   }

   /// <summary>
   /// A skip reason without its details, for counting.
   /// </summary>
   /// <param name="reason">The reason.</param>
   /// <returns>E.g. "not an included file type".</returns>
   private static string Bucket( string reason )
   {
      int paren = reason.IndexOf( " (", StringComparison.Ordinal );
      return paren > 0 ? reason[..paren] : reason;
   }

   /// <summary>
   /// A bucket's label.
   /// </summary>
   /// <param name="bucket">Lower and upper bound.</param>
   /// <returns>E.g. "3-5" or "21+".</returns>
   private static string Range( (int Low, int High) bucket )
   {
      if( bucket.High == int.MaxValue )
      {
         return bucket.Low.ToString( CultureInfo.InvariantCulture ) + "+";
      }

      return bucket.Low == bucket.High ? bucket.Low.ToString( CultureInfo.InvariantCulture ) : $"{bucket.Low}-{bucket.High}";
   }

   #endregion Private Methods
}
