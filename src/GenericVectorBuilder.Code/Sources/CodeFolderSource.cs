using System.Globalization;
using System.Runtime.CompilerServices;
using System.Security;
using GenericVectorBuilder.Core.Contracts;

namespace GenericVectorBuilder.Code.Sources;

/// <summary>
/// Source over a folder of source code, typically a git clone: one record per file, with the
/// file's relative path, language and full content.
/// Why one record per file: the file is the unit that changes, so its content hash decides
/// whether anything needs embedding again, and its path is a key that never moves while the
/// file stays put. Splitting into types and methods is the chunker's job, not the source's.
/// Why the read report matters: the pipeline deletes stored files only when this source says
/// it read their origin cleanly or proves the file is gone. A folder that cannot be listed is
/// rejected, so every file under it keeps its vectors. A file skipped on purpose (too large,
/// binary, a symbolic link, an excluded folder or file type) is recorded as ignored, so its old
/// vectors are kept with that reason instead of being deleted as if the file were removed.
/// </summary>
public sealed class CodeFolderSource : ISource
{
   #region Data Members

   /// <summary>Column holding the file path relative to the root, with '/' separators.</summary>
   public const string PATH_COLUMN = "path";

   /// <summary>Column holding the language name, e.g. "csharp".</summary>
   public const string LANGUAGE_COLUMN = "language";

   /// <summary>Column holding the full file content.</summary>
   public const string CONTENT_COLUMN = "content";

   private static readonly string[] COLUMNS = { PATH_COLUMN, LANGUAGE_COLUMN, CONTENT_COLUMN };
   private static readonly EnumerationOptions LIST_EVERYTHING = new()
   {
      RecurseSubdirectories = false,
      IgnoreInaccessible = false,
      AttributesToSkip = 0,
      ReturnSpecialDirectories = false,
   };

   private readonly string _root;
   private readonly string _rootPrefix;
   private readonly CodeFolderOptions _options;
   private readonly List<RejectedOrigin> _ignored = new();
   private readonly Dictionary<string, HashSet<string>?> _listings = new( StringComparer.Ordinal );

   #endregion Data Members

   #region Constructor

   /// <summary>
   /// Creates the source. Nothing is read until <see cref="ReadAsync"/>.
   /// </summary>
   /// <param name="root">The folder to read, e.g. a clone's local path.</param>
   /// <param name="options">Which files to read; null for <see cref="CodeFolderOptions.Default"/>.</param>
   public CodeFolderSource( string root, CodeFolderOptions? options = null )
   {
      _root = Path.TrimEndingDirectorySeparator( Path.GetFullPath( root ) );
      _rootPrefix = Path.EndsInDirectorySeparator( _root ) ? _root : _root + Path.DirectorySeparatorChar;
      _options = options ?? CodeFolderOptions.Default;
      TableName = Path.GetFileName( _root ) is { Length: > 0 } name ? name : "code";
      TableId = "code:" + TableName;
   }

   #endregion Constructor

   #region Public Methods

   /// <inheritdoc />
   public string Description => $"code folder {_root}";

   /// <inheritdoc />
   public SourceReadReport Report { get; } = new();

   /// <summary>Logical table name stamped on every record: the folder's own name.</summary>
   public string TableName { get; }

   /// <summary>Stable table id stamped on every record: "code:" plus the folder's name.</summary>
   public string TableId { get; }

   /// <summary>Files read and emitted this run.</summary>
   public int FilesRead { get; private set; }

   /// <summary>Everything skipped on purpose this run, with the reason, for reporting.</summary>
   public IReadOnlyList<RejectedOrigin> Ignored => _ignored;

   /// <inheritdoc />
   public async IAsyncEnumerable<SourceRecord> ReadAsync( [EnumeratorCancellation] CancellationToken ct )
   {
      if( !Directory.Exists( _root ) )
      {
         throw new DirectoryNotFoundException( $"The folder {_root} does not exist." );
      }

      foreach( (string full, string relative) in EnumerateFiles( ct ) )
      {
         ct.ThrowIfCancellationRequested();
         string? content = ReadContent( full, relative );
         if( content == null )
         {
            continue;
         }

         FilesRead++;
         yield return new SourceRecord( TableName, relative, 1, COLUMNS, new string?[] { relative, CodeLanguages.FromPath( relative ), content }, TableId );
         Report.MarkClean( relative );
      }

      Report.Completed = true;
      await Task.CompletedTask;
   }

   /// <summary>
   /// Lists the files a read would open, as relative paths in read order, without opening any
   /// of them. Everything the walk skips on purpose lands in <see cref="Ignored"/>.
   /// Why: the web page shows how many files a repository holds before anything is embedded,
   /// and the progress bar needs that count, using exactly the rules the read uses.
   /// The walk records what it skipped in this instance's <see cref="Report"/>, so use a fresh
   /// instance for the read itself.
   /// </summary>
   /// <param name="ct">Cancellation.</param>
   /// <returns>Relative paths with '/' separators, sorted.</returns>
   /// <exception cref="DirectoryNotFoundException">The folder does not exist or cannot be read.</exception>
   public IReadOnlyList<string> ListFiles( CancellationToken ct )
   {
      if( !Directory.Exists( _root ) )
      {
         throw new DirectoryNotFoundException( $"The folder {_root} does not exist." );
      }

      return EnumerateFiles( ct ).Select( f => f.Relative ).ToList();
   }

   /// <inheritdoc />
   public Task<bool> ConfirmGoneAsync( string origin, CancellationToken ct )
   {
      ct.ThrowIfCancellationRequested();
      bool seen = origin.Length == 0 || Report.IsClean( origin ) || Report.IsRejected( origin ) || Report.IgnoredReason( origin ) != null;
      return Task.FromResult( !seen && PathConfirmedAbsent( origin ) );
   }

   #endregion Public Methods

   #region Private Methods

   /// <summary>
   /// Walks the folder one level at a time and returns the files to read, sorted by relative path
   /// so every run reads them in the same order. Folders that cannot be listed are rejected (the
   /// root itself is fatal, so an unreadable folder never looks empty).
   /// </summary>
   /// <param name="ct">Cancellation.</param>
   /// <returns>Absolute and relative path of each file to read.</returns>
   private List<(string Full, string Relative)> EnumerateFiles( CancellationToken ct )
   {
      var files = new List<(string Full, string Relative)>();
      var pending = new Stack<string>();
      pending.Push( _root );
      while( pending.Count > 0 )
      {
         ct.ThrowIfCancellationRequested();
         string directory = pending.Pop();
         List<FileSystemInfo> entries;
         try
         {
            entries = new DirectoryInfo( directory ).EnumerateFileSystemInfos( "*", LIST_EVERYTHING ).ToList();
         }
         catch( Exception ex ) when( ex is IOException or UnauthorizedAccessException or SecurityException )
         {
            if( string.Equals( directory, _root, StringComparison.Ordinal ) )
            {
               throw new DirectoryNotFoundException( $"The folder {_root} cannot be read: {ex.Message}", ex );
            }

            Report.MarkRejected( Relative( directory ), $"folder could not be read, so nothing inside it was read: {ex.Message}" );
            continue;
         }

         foreach( FileSystemInfo entry in entries )
         {
            Classify( entry, files, pending );
         }
      }

      files.Sort( ( a, b ) => string.CompareOrdinal( a.Relative, b.Relative ) );
      return files;
   }

   /// <summary>
   /// Sorts one directory entry: links and excluded folders or file types are ignored, folders
   /// are queued, files within the size limit are collected.
   /// </summary>
   /// <param name="entry">The entry.</param>
   /// <param name="files">Collected files.</param>
   /// <param name="pending">Folders still to walk.</param>
   private void Classify( FileSystemInfo entry, List<(string Full, string Relative)> files, Stack<string> pending )
   {
      string relative = Relative( entry.FullName );
      if( ( entry.Attributes & FileAttributes.ReparsePoint ) != 0 || entry.LinkTarget != null )
      {
         Ignore( relative, "link to another location, not followed" );
      }
      else if( ( entry.Attributes & FileAttributes.Directory ) != 0 )
      {
         if( _options.ExcludesDirectory( relative ) )
         {
            Ignore( relative, "excluded folder" );
         }
         else
         {
            pending.Push( entry.FullName );
         }
      }
      else if( !_options.IncludesExtension( entry.Name ) )
      {
         Ignore( relative, $"not an included file type ({( entry.Extension.Length == 0 ? "no extension" : entry.Extension )})" );
      }
      else if( entry is FileInfo file && file.Length > _options.MaxFileBytes )
      {
         Ignore( relative, TooLargeReason() );
      }
      else
      {
         files.Add( (entry.FullName, relative) );
      }
   }

   /// <summary>
   /// Reads one file's text. A file that cannot be read is rejected (its old vectors are kept);
   /// a binary file, or one that grew past the size limit since the walk, is ignored.
   /// </summary>
   /// <param name="full">Absolute path.</param>
   /// <param name="relative">Origin id.</param>
   /// <returns>The text, or null when the file was skipped.</returns>
   private string? ReadContent( string full, string relative )
   {
      byte[] bytes;
      try
      {
         bytes = File.ReadAllBytes( full );
      }
      catch( Exception ex ) when( ex is IOException or UnauthorizedAccessException or SecurityException )
      {
         Report.MarkRejected( relative, $"file could not be read: {ex.Message}" );
         return null;
      }

      if( bytes.LongLength > _options.MaxFileBytes )
      {
         Ignore( relative, TooLargeReason() );
         return null;
      }

      string? text = CodeFileReader.Decode( bytes );
      if( text == null )
      {
         Ignore( relative, "binary file" );
      }

      return text;
   }

   /// <summary>
   /// The reason given for a file over the size limit.
   /// </summary>
   /// <returns>E.g. "larger than 1,048,576 bytes".</returns>
   private string TooLargeReason()
   {
      return $"larger than {_options.MaxFileBytes.ToString( "N0", CultureInfo.InvariantCulture )} bytes";
   }

   /// <summary>
   /// Records something skipped on purpose, in the read report and in <see cref="Ignored"/>.
   /// </summary>
   /// <param name="relative">Origin id of the file or folder.</param>
   /// <param name="reason">Plain-English reason.</param>
   private void Ignore( string relative, string reason )
   {
      Report.MarkIgnored( relative, reason );
      _ignored.Add( new RejectedOrigin( relative, reason ) );
   }

   /// <summary>
   /// True when a relative path is proven absent: nothing exists at it, and the nearest folder
   /// above it that does exist can be listed and does not contain the next path part. A folder
   /// that cannot be listed proves nothing. Same rule as the table folder source.
   /// </summary>
   /// <param name="relative">Path relative to the root.</param>
   /// <returns>True when the path is confirmed absent.</returns>
   private bool PathConfirmedAbsent( string relative )
   {
      string? full = FullPathUnderRoot( relative );
      if( full == null || File.Exists( full ) || Directory.Exists( full ) )
      {
         return false;
      }

      string child = full;
      string? parent = Path.GetDirectoryName( full );
      while( parent != null && IsUnderRoot( parent ) && !Directory.Exists( parent ) )
      {
         child = parent;
         parent = Path.GetDirectoryName( parent );
      }

      if( parent == null || !IsUnderRoot( parent ) )
      {
         return false;
      }

      HashSet<string>? names = Listing( parent );
      return names != null && !names.Contains( Path.GetFileName( child ) );
   }

   /// <summary>
   /// Lists a folder's entry names, including hidden ones, cached for the run.
   /// </summary>
   /// <param name="directory">Absolute folder path.</param>
   /// <returns>The names, or null when the folder cannot be listed.</returns>
   private HashSet<string>? Listing( string directory )
   {
      if( !_listings.TryGetValue( directory, out HashSet<string>? names ) )
      {
         try
         {
            names = Directory.EnumerateFileSystemEntries( directory, "*", LIST_EVERYTHING )
               .Select( e => Path.GetFileName( e ) )
               .ToHashSet( StringComparer.Ordinal );
         }
         catch( Exception ex ) when( ex is IOException or UnauthorizedAccessException or SecurityException )
         {
            names = null;
         }

         _listings[directory] = names;
      }

      return names;
   }

   /// <summary>
   /// Resolves a relative origin against the root, refusing anything that escapes it.
   /// </summary>
   /// <param name="relative">Relative path.</param>
   /// <returns>The absolute path, or null when it is invalid or outside the root.</returns>
   private string? FullPathUnderRoot( string relative )
   {
      try
      {
         string full = Path.GetFullPath( Path.Combine( _root, relative ) );
         return IsUnderRoot( full ) && full.Length > _root.Length ? full : null;
      }
      catch( Exception ex ) when( ex is ArgumentException or NotSupportedException or PathTooLongException )
      {
         return null;
      }
   }

   /// <summary>
   /// True when a path is the root or inside it.
   /// </summary>
   /// <param name="path">Absolute path.</param>
   /// <returns>True when under the root.</returns>
   private bool IsUnderRoot( string path )
   {
      string trimmed = Path.TrimEndingDirectorySeparator( path );
      return trimmed.Equals( _root, StringComparison.Ordinal ) || trimmed.StartsWith( _rootPrefix, StringComparison.Ordinal );
   }

   /// <summary>
   /// The origin id of a path: relative to the root, with '/' separators.
   /// </summary>
   /// <param name="path">Absolute path under the root.</param>
   /// <returns>The relative path.</returns>
   private string Relative( string path )
   {
      return Path.GetRelativePath( _root, path ).Replace( '\\', '/' );
   }

   #endregion Private Methods
}
