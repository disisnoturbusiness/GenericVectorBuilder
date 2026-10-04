using System.Diagnostics;
using GenericVectorBuilder.Code.Chunking;
using GenericVectorBuilder.Code.Git;
using GenericVectorBuilder.Code.Mapping;
using GenericVectorBuilder.Code.Sources;
using GenericVectorBuilder.Core.Contracts;

namespace GenericVectorBuilder.Web.Git;

/// <summary>
/// How many files one folder of a repository holds.
/// </summary>
/// <param name="Folder">Folder path up to two levels deep, e.g. "src/Web", or "(top folder)".</param>
/// <param name="Files">Files that will be read in it and below it.</param>
public sealed record GitFolderCount( string Folder, int Files );

/// <summary>
/// How many chunks a run is expected to embed.
/// </summary>
/// <param name="Chunks">Expected chunks for the whole repository.</param>
/// <param name="FilesMeasured">Files actually split to get the number.</param>
/// <param name="Extrapolated">True when only some files were split and the rest was scaled up from them.</param>
public sealed record GitChunkEstimate( long Chunks, int FilesMeasured, bool Extrapolated );

/// <summary>
/// What the page shows after Fetch: the commit, what will be read, where, and roughly how much
/// embedding it means.
/// </summary>
/// <param name="Url">The repository address.</param>
/// <param name="Branch">The branch, or null for the default branch.</param>
/// <param name="Commit">The full commit id the copy is at.</param>
/// <param name="LocalPath">Where the copy lives on the server.</param>
/// <param name="SuggestedPipeline">The pipeline name to offer, e.g. "eshoponweb".</param>
/// <param name="Extensions">File types that will be read.</param>
/// <param name="FileCount">Files that will be read.</param>
/// <param name="TotalBytes">Their size on disk.</param>
/// <param name="Folders">File counts by folder, biggest first.</param>
/// <param name="MoreFolders">Folders left out of <paramref name="Folders"/>.</param>
/// <param name="FirstFiles">The first files in read order.</param>
/// <param name="SkippedCount">Files and folders skipped on purpose (other file types, build output, too large), not counting git's own folder.</param>
/// <param name="Skipped">The first few of them, with the reason.</param>
/// <param name="Chunks">The chunk estimate.</param>
public sealed record GitPreview( string Url, string? Branch, string Commit, string LocalPath, string SuggestedPipeline, IReadOnlyList<string> Extensions,
   int FileCount, long TotalBytes, IReadOnlyList<GitFolderCount> Folders, int MoreFolders, IReadOnlyList<string> FirstFiles, int SkippedCount,
   IReadOnlyList<RejectedOrigin> Skipped, GitChunkEstimate Chunks );

/// <summary>
/// Builds the preview of a fetched repository. The file list uses exactly the rules a run uses,
/// and the chunk count comes from the run's own chunker, so the numbers match what Go does.
/// Why the estimate is capped: splitting means parsing every C# file, which for a very large
/// repository would hold the page for minutes. After 2,000 files or 10 seconds the rest is
/// scaled up from what was split, and the page says so.
/// </summary>
public static class GitPreviewBuilder
{
   #region Data Members

   private const int MAX_FOLDERS = 40;
   private const int FIRST_FILES = 20;
   private const int SKIPPED_SHOWN = 20;
   private const int MAX_ESTIMATE_FILES = 2000;
   private const string TOP_FOLDER = "(top folder)";

   /// <summary>git's own folder in every copy; always skipped, so listing it would only be noise.</summary>
   private const string GIT_FOLDER = ".git";
   private const string ESTIMATE_PIPELINE = "estimate";
   private static readonly TimeSpan ESTIMATE_BUDGET = TimeSpan.FromSeconds( 10 );

   #endregion Data Members

   #region Public Methods

   /// <summary>
   /// Builds the preview of a copy that was just fetched.
   /// </summary>
   /// <param name="repo">The repository, already synced.</param>
   /// <param name="commit">The commit the copy is at.</param>
   /// <param name="extensions">File types to read.</param>
   /// <param name="ct">Cancellation.</param>
   /// <returns>The preview.</returns>
   public static async Task<GitPreview> BuildAsync( GitRepository repo, string commit, IReadOnlyList<string> extensions, CancellationToken ct )
   {
      CodeFolderOptions options = CodeFolderOptions.Default with { Extensions = extensions };
      var lister = new CodeFolderSource( repo.LocalPath, options );
      IReadOnlyList<string> files = await Task.Run( () => lister.ListFiles( ct ), ct );
      long bytes = files.Sum( f => new FileInfo( Path.Combine( repo.LocalPath, f ) ).Length );
      List<GitFolderCount> folders = files.GroupBy( FolderOf, StringComparer.Ordinal )
         .Select( g => new GitFolderCount( g.Key, g.Count() ) )
         .OrderByDescending( f => f.Files ).ThenBy( f => f.Folder, StringComparer.Ordinal )
         .ToList();
      GitChunkEstimate chunks = await Task.Run( () => EstimateChunksAsync( repo.LocalPath, options, commit, files.Count, ct ), ct );
      List<RejectedOrigin> skipped = lister.Ignored.Where( i => i.Origin != GIT_FOLDER ).ToList();
      return new GitPreview( repo.Url, repo.Branch, commit, repo.LocalPath, GitWorkspace.SuggestPipeline( repo.Url ), extensions, files.Count, bytes,
         folders.Take( MAX_FOLDERS ).ToList(), Math.Max( 0, folders.Count - MAX_FOLDERS ), files.Take( FIRST_FILES ).ToList(), skipped.Count,
         skipped.Take( SKIPPED_SHOWN ).ToList(), chunks );
   }

   /// <summary>
   /// The folder a file is counted under: its folder path cut to two levels.
   /// </summary>
   /// <param name="relative">File path relative to the root, '/' separated.</param>
   /// <returns>E.g. "src/Web" for "src/Web/Pages/Index.cs", "(top folder)" for "Program.cs".</returns>
   public static string FolderOf( string relative )
   {
      string[] parts = relative.Split( '/', StringSplitOptions.RemoveEmptyEntries );
      return parts.Length <= 1 ? TOP_FOLDER : string.Join( "/", parts.Take( Math.Min( 2, parts.Length - 1 ) ) );
   }

   #endregion Public Methods

   #region Private Methods

   /// <summary>
   /// Reads, maps and splits the files the way a run does and counts the chunks, stopping at the
   /// file or time cap and scaling the count up to every listed file when it stopped early.
   /// </summary>
   /// <param name="root">The copy's folder.</param>
   /// <param name="options">Which files to read.</param>
   /// <param name="commit">The commit, for the mapper.</param>
   /// <param name="listed">How many files the listing found.</param>
   /// <param name="ct">Cancellation.</param>
   /// <returns>The estimate.</returns>
   private static async Task<GitChunkEstimate> EstimateChunksAsync( string root, CodeFolderOptions options, string commit, int listed, CancellationToken ct )
   {
      var source = new CodeFolderSource( root, options );
      var mapper = new CodeDocumentMapper( commit );
      var chunker = new RoslynCodeChunker();
      var clock = Stopwatch.StartNew();
      long chunks = 0;
      bool stoppedEarly = false;
      await foreach( SourceRecord record in source.ReadAsync( ct ) )
      {
         Document? document = mapper.Map( record );
         chunks += document == null ? 0 : chunker.Split( ESTIMATE_PIPELINE, document ).Count;
         if( source.FilesRead >= MAX_ESTIMATE_FILES || clock.Elapsed > ESTIMATE_BUDGET )
         {
            stoppedEarly = source.FilesRead < listed;
            break;
         }
      }

      if( !stoppedEarly || source.FilesRead == 0 )
      {
         return new GitChunkEstimate( chunks, source.FilesRead, false );
      }

      long scaled = (long)Math.Round( chunks * (double)listed / source.FilesRead );
      return new GitChunkEstimate( scaled, source.FilesRead, true );
   }

   #endregion Private Methods
}
