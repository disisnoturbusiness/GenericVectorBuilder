using System.Collections.ObjectModel;
using System.Text;
using System.Text.RegularExpressions;
using GenericVectorBuilder.Core.Contracts;

namespace GenericVectorBuilder.Core.Sources.Files;

/// <summary>
/// One origin that passed the scan, with its row counts.
/// </summary>
/// <param name="Spec">How to re-open and read it.</param>
/// <param name="DataRows">Readable data rows.</param>
/// <param name="BadRows">Rows with extra non-empty cells, which are skipped.</param>
public sealed record ScannedOrigin( OriginSpec Spec, long DataRows, long BadRows );

/// <summary>
/// A logical table: every origin that shares the same header, configured once.
/// </summary>
/// <param name="Name">Table name derived from the file names, unique within the scan. It depends only on the
/// group's own files and header, never on path order: when several header groups share a file stem, each gets
/// the stem plus a short fingerprint of its header (orders_3fa9c1).</param>
/// <param name="Columns">Column names shared by every origin in the group.</param>
/// <param name="Origins">The files or sheets in the group.</param>
/// <param name="DataRows">Total readable rows across the group.</param>
/// <param name="Sample">Up to 10 rows for the preview.</param>
/// <param name="SuggestedKey">A column whose values are present and unique across the whole group, if any.</param>
public sealed record ScannedTable( string Name, IReadOnlyList<string> Columns, IReadOnlyList<ScannedOrigin> Origins, long DataRows,
   IReadOnlyList<IReadOnlyList<string?>> Sample, string? SuggestedKey )
{
   private static readonly IReadOnlyDictionary<string, IReadOnlySet<string>> NO_DUPLICATES =
      new ReadOnlyDictionary<string, IReadOnlySet<string>>( new Dictionary<string, IReadOnlySet<string>>( StringComparer.OrdinalIgnoreCase ) );

   /// <summary>
   /// Values of the requested key columns that appear in more than one accepted origin of this
   /// table, by column name as written in the header (looked up ignoring case). Only a column that
   /// has at least one such value gets an entry, and only the repeated values are kept.
   /// Why: two files of one table that both hold id 5 would otherwise produce the same document
   /// key, so the row mapper needs to know which values to qualify by their origin. A value that
   /// repeats only inside a single file is not listed, and neither is anything from a rejected
   /// file. It is empty unless the scan was asked to track columns
   /// (see <see cref="FolderScanner.Scan(string, CancellationToken, IReadOnlyCollection{string})"/>).
   /// </summary>
   public IReadOnlyDictionary<string, IReadOnlySet<string>> CrossOriginDuplicates { get; init; } = NO_DUPLICATES;
}

/// <summary>
/// Everything the scan found under a root folder.
/// </summary>
/// <param name="Root">The folder scanned.</param>
/// <param name="Tables">Header groups, each one logical table.</param>
/// <param name="Rejected">Data files (and folders) that could not be read, with reasons.</param>
/// <param name="Ignored">Files skipped on purpose (not a data file, hidden sheet, empty sheet, symbolic link).</param>
public sealed record FolderScanResult( string Root, IReadOnlyList<ScannedTable> Tables, IReadOnlyList<RejectedOrigin> Rejected,
   IReadOnlyList<RejectedOrigin> Ignored )
{
   /// <summary>
   /// Total readable rows across every table, used for the progress bar.
   /// </summary>
   public long TotalRows => Tables.Sum( t => t.DataRows );
}

/// <summary>
/// Turns a folder of arbitrary CSV, delimited and Excel files into a short list of logical
/// tables. Every file is sniffed, fully validated, and grouped with the other files that have
/// the same header, so 200 monthly exports become one "orders" table configured once.
/// Why validate every row up front: the preview the user approves must be the truth. A file
/// with an unclosed quote or mostly misaligned rows is rejected here, with the reason, before
/// a single vector is written, and the rest of the folder still goes through.
/// Reject origins: a file is rejected under its relative path, a workbook that will not open
/// under its bare file path (its sheet names are unknown), and a folder that cannot be listed
/// under the folder's relative path. Anything stored under "file.xlsx#Sheet" or "folder/file"
/// is therefore covered by a reject of its parent, which is how a run keeps old vectors for
/// data it could not read this time.
/// Symbolic links are never followed: a link can loop back on its own folder (multiplying
/// every file) or point outside the folder the user was allowed to pick.
/// </summary>
public sealed class FolderScanner
{
   #region Data Members

   internal const int SAMPLE_ROWS = 10;
   internal const double MAX_BAD_ROW_RATIO = 0.05;
   internal const int MAX_KEY_VALUES = 2_000_000;
   private const string OFFICE_LOCK_PREFIX = "~$";
   private static readonly HashSet<string> DELIMITED_EXTENSIONS = new( StringComparer.OrdinalIgnoreCase ) { ".csv", ".tsv", ".txt", ".psv", ".dat", ".tab" };
   private static readonly HashSet<string> EXCEL_EXTENSIONS = new( StringComparer.OrdinalIgnoreCase ) { ".xlsx", ".xlsm", ".xls" };
   internal static readonly Regex KEY_COLUMN = new( "^(id|key)$|(id|key|code|number|_no)$", RegexOptions.IgnoreCase | RegexOptions.Compiled );
   private static readonly Regex NON_LETTERS = new( @"[^\p{L}\p{M}]+", RegexOptions.Compiled );
   private static readonly Regex DEFAULT_SHEET_NAME = new( @"^sheet\d*$", RegexOptions.IgnoreCase | RegexOptions.Compiled );
   private static readonly Regex WORKBOOK_ORIGIN = new( @"^(?<file>.*?\.(?:xlsx|xlsm|xls))#(?<sheet>.*)$", RegexOptions.IgnoreCase | RegexOptions.Singleline | RegexOptions.Compiled );

   #endregion Data Members

   #region Public Methods

   /// <summary>
   /// Scans a folder recursively, without tracking any column across files.
   /// </summary>
   /// <param name="root">Folder to scan.</param>
   /// <param name="ct">Cancellation.</param>
   /// <returns>Tables, rejects and ignored files.</returns>
   /// <exception cref="DirectoryNotFoundException">The folder is missing or cannot be listed.
   /// Raised for the root only, so a run that cannot see its folder fails instead of looking empty.</exception>
   public FolderScanResult Scan( string root, CancellationToken ct )
   {
      return Scan( root, ct, null );
   }

   /// <summary>
   /// Scans a folder recursively and, for the named key columns, records which values appear in
   /// more than one accepted file or sheet of the same table (see
   /// <see cref="ScannedTable.CrossOriginDuplicates"/>).
   /// Why: files that share a header are one table, and a key column is only a key within one file
   /// when each file restarts its own numbering. The run needs to know which values collide across
   /// files so it can keep them apart. Tracking costs one set of distinct values per tracked column
   /// per table while the scan runs, and keeps only the colliding values afterwards.
   /// </summary>
   /// <param name="root">Folder to scan.</param>
   /// <param name="ct">Cancellation.</param>
   /// <param name="trackKeyColumns">Column names (any case) to track; null or empty tracks nothing. A table is tracked
   /// for every one of these names its header contains.</param>
   /// <returns>Tables, rejects and ignored files.</returns>
   /// <exception cref="DirectoryNotFoundException">The folder is missing or cannot be listed.
   /// Raised for the root only, so a run that cannot see its folder fails instead of looking empty.</exception>
   public FolderScanResult Scan( string root, CancellationToken ct, IReadOnlyCollection<string>? trackKeyColumns )
   {
      root = Path.GetFullPath( root );
      if( !Directory.Exists( root ) )
      {
         throw new DirectoryNotFoundException( $"Folder not found: {root}" );
      }

      var rejected = new List<RejectedOrigin>();
      var ignored = new List<RejectedOrigin>();
      var groups = new Dictionary<string, GroupBuilder>( StringComparer.Ordinal );
      foreach( string path in EnumerateFiles( root, rejected, ignored, ct ) )
      {
         ct.ThrowIfCancellationRequested();
         string relative = RelativeOrigin( root, path );
         foreach( OriginSpec spec in DiscoverOrigins( path, relative, rejected, ignored ) )
         {
            ValidateInto( spec, groups, rejected, trackKeyColumns );
         }
      }

      List<GroupBuilder> accepted = groups.Values.Where( g => g.Origins.Count > 0 ).ToList();
      IReadOnlyDictionary<GroupBuilder, string> names = TableNamer.Assign( accepted );
      List<ScannedTable> tables = accepted.Select( g => g.Build( names[g] ) ).ToList();
      return new FolderScanResult( root, tables, rejected, ignored );
   }

   #endregion Public Methods

   #region Private Methods

   /// <summary>
   /// Lists files under the root in a stable order, skipping hidden and system entries, Office
   /// lock files and symbolic links. The walk is done by hand, one folder at a time, so a
   /// folder that cannot be opened (permissions changed, a share dropped part way) is reported
   /// as a rejected entry. Letting the framework ignore inaccessible folders would make
   /// everything under them look deleted from the source.
   /// </summary>
   /// <param name="root">Folder to walk.</param>
   /// <param name="rejected">Reject list for folders that cannot be read.</param>
   /// <param name="ignored">Ignore list for symbolic links.</param>
   /// <param name="ct">Cancellation.</param>
   /// <returns>Absolute file paths sorted by path.</returns>
   private static List<string> EnumerateFiles( string root, List<RejectedOrigin> rejected, List<RejectedOrigin> ignored, CancellationToken ct )
   {
      var files = new List<string>();
      var pending = new Stack<string>();
      pending.Push( root );
      while( pending.Count > 0 )
      {
         ct.ThrowIfCancellationRequested();
         string directory = pending.Pop();
         List<FileSystemInfo>? entries = ListDirectory( directory, out string? problem );
         if( entries == null )
         {
            ReportUnreadableFolder( root, directory, problem!, rejected );
            continue;
         }

         foreach( FileSystemInfo entry in entries )
         {
            Classify( entry, root, files, pending, ignored );
         }
      }

      files.Sort( StringComparer.Ordinal );
      return files;
   }

   /// <summary>
   /// Lists one folder's immediate entries, without recursing and without hiding failures.
   /// </summary>
   /// <param name="directory">Folder to list.</param>
   /// <param name="problem">The failure message when the folder cannot be listed.</param>
   /// <returns>The entries, or null when the folder cannot be listed.</returns>
   private static List<FileSystemInfo>? ListDirectory( string directory, out string? problem )
   {
      var options = new EnumerationOptions
      {
         RecurseSubdirectories = false,
         IgnoreInaccessible = false,
         AttributesToSkip = FileAttributes.Hidden | FileAttributes.System,
      };

      try
      {
         problem = null;
         return new DirectoryInfo( directory ).EnumerateFileSystemInfos( "*", options ).ToList();
      }
      catch( Exception ex ) when( ex is IOException or UnauthorizedAccessException )
      {
         problem = ex.Message;
         return null;
      }
   }

   /// <summary>
   /// Records a folder that could not be listed. The root itself is fatal (a scan that cannot
   /// see its folder must not look like an empty folder); a sub-folder becomes a reject.
   /// </summary>
   /// <param name="root">Scan root.</param>
   /// <param name="directory">The folder that failed.</param>
   /// <param name="problem">The failure message.</param>
   /// <param name="rejected">Reject list to append to.</param>
   private static void ReportUnreadableFolder( string root, string directory, string problem, List<RejectedOrigin> rejected )
   {
      if( string.Equals( directory, root, StringComparison.Ordinal ) )
      {
         throw new DirectoryNotFoundException( $"Folder cannot be read: {root} ({problem})" );
      }

      rejected.Add( new RejectedOrigin( RelativeOrigin( root, directory ), $"folder could not be read, so nothing inside it was scanned: {problem}" ) );
   }

   /// <summary>
   /// Sorts one directory entry into the walk: skip dot-names and Office lock files, note
   /// symbolic links as ignored, queue folders, collect files.
   /// </summary>
   /// <param name="entry">The entry.</param>
   /// <param name="root">Scan root.</param>
   /// <param name="files">Collected file paths.</param>
   /// <param name="pending">Folders still to walk.</param>
   /// <param name="ignored">Ignore list for symbolic links.</param>
   private static void Classify( FileSystemInfo entry, string root, List<string> files, Stack<string> pending, List<RejectedOrigin> ignored )
   {
      if( entry.Name.StartsWith( '.' ) || entry.Name.StartsWith( OFFICE_LOCK_PREFIX, StringComparison.Ordinal ) )
      {
         return;
      }

      if( ( entry.Attributes & FileAttributes.ReparsePoint ) != 0 )
      {
         ignored.Add( new RejectedOrigin( RelativeOrigin( root, entry.FullName ), "link to another location, not followed" ) );
      }
      else if( ( entry.Attributes & FileAttributes.Directory ) != 0 )
      {
         pending.Push( entry.FullName );
      }
      else
      {
         files.Add( entry.FullName );
      }
   }

   /// <summary>
   /// Builds the origin id of a path: relative to the root, with forward slashes.
   /// </summary>
   /// <param name="root">Scan root.</param>
   /// <param name="path">Absolute path under the root.</param>
   /// <returns>The relative origin id.</returns>
   private static string RelativeOrigin( string root, string path )
   {
      return Path.GetRelativePath( root, path ).Replace( '\\', '/' );
   }

   /// <summary>
   /// Works out the readable origins inside one file: the file itself for delimited text, or
   /// each visible non-empty sheet for a workbook. Unreadable files go to the reject list.
   /// </summary>
   /// <param name="path">Absolute file path.</param>
   /// <param name="relative">Path relative to the scan root, used as the origin id.</param>
   /// <param name="rejected">Reject list to append to.</param>
   /// <param name="ignored">Ignore list to append to.</param>
   /// <returns>Origins to validate.</returns>
   private static IEnumerable<OriginSpec> DiscoverOrigins( string path, string relative, List<RejectedOrigin> rejected, List<RejectedOrigin> ignored )
   {
      string extension = Path.GetExtension( path );
      if( DELIMITED_EXTENSIONS.Contains( extension ) )
      {
         SniffResult sniff = SafeSniff( path );
         if( sniff.Profile == null )
         {
            rejected.Add( new RejectedOrigin( relative, sniff.RejectReason! ) );
            return Array.Empty<OriginSpec>();
         }

         return new[] { new OriginSpec( relative, path, OriginKind.Delimited, sniff.Profile, null ) };
      }

      if( EXCEL_EXTENSIONS.Contains( extension ) )
      {
         return DiscoverSheets( path, relative, rejected, ignored );
      }

      ignored.Add( new RejectedOrigin( relative, $"not a data file ({( extension.Length == 0 ? "no extension" : extension )})" ) );
      return Array.Empty<OriginSpec>();
   }

   /// <summary>
   /// Sniffs a file, converting I/O failures into a reject reason.
   /// </summary>
   /// <param name="path">File path.</param>
   /// <returns>The sniff result.</returns>
   private static SniffResult SafeSniff( string path )
   {
      try
      {
         return FileSniffer.Sniff( path );
      }
      catch( Exception ex ) when( ex is IOException or UnauthorizedAccessException )
      {
         return new SniffResult( null, $"could not read: {ex.Message}" );
      }
   }

   /// <summary>
   /// Lists a workbook's sheets as origins. Hidden and empty sheets are ignored, and a workbook
   /// that will not open (corrupt, password protected) is rejected under its bare file path.
   /// The sheet names of a workbook that will not open are unknown, so the file path is the
   /// narrowest origin that can be rejected; it covers every "file#Sheet" origin stored from it.
   /// </summary>
   /// <param name="path">Workbook path.</param>
   /// <param name="relative">Relative path used in origin ids.</param>
   /// <param name="rejected">Reject list to append to.</param>
   /// <param name="ignored">Ignore list to append to.</param>
   /// <returns>One origin per usable sheet.</returns>
   private static IEnumerable<OriginSpec> DiscoverSheets( string path, string relative, List<RejectedOrigin> rejected, List<RejectedOrigin> ignored )
   {
      IReadOnlyList<SheetInfo> sheets;
      try
      {
         sheets = OriginReader.ListSheets( path );
      }
      catch( Exception ex )
      {
         rejected.Add( new RejectedOrigin( relative, $"workbook will not open: {ex.Message}" ) );
         return Array.Empty<OriginSpec>();
      }

      var specs = new List<OriginSpec>();
      foreach( SheetInfo sheet in sheets )
      {
         string origin = $"{relative}#{sheet.Name}";
         if( sheet.Hidden || sheet.FirstRow == null )
         {
            ignored.Add( new RejectedOrigin( origin, sheet.Hidden ? "hidden sheet" : "empty sheet" ) );
            continue;
         }

         bool hasHeader = FileSniffer.LooksLikeHeader( sheet.FirstRow );
         IReadOnlyList<string> columns = hasHeader ? FileSniffer.MakeUnique( sheet.FirstRow ) : FileSniffer.Generated( sheet.FirstRow.Count );
         var profile = new FileProfile( Encoding.UTF8, '\0', hasHeader, columns );
         specs.Add( new OriginSpec( origin, path, OriginKind.ExcelSheet, profile, sheet.Name ) );
      }

      return specs;
   }

   /// <summary>
   /// Reads every row of an origin to count good and bad rows, collect the preview sample and
   /// track key candidates, then files it under its header group or rejects it.
   /// </summary>
   /// <param name="spec">Origin to validate.</param>
   /// <param name="groups">Header groups built so far.</param>
   /// <param name="rejected">Reject list to append to.</param>
   /// <param name="trackKeyColumns">Column names to track across origins, or null.</param>
   private static void ValidateInto( OriginSpec spec, Dictionary<string, GroupBuilder> groups, List<RejectedOrigin> rejected,
      IReadOnlyCollection<string>? trackKeyColumns )
   {
      string groupKey = GroupKey( spec.Profile );
      if( !groups.TryGetValue( groupKey, out GroupBuilder? group ) )
      {
         group = new GroupBuilder( groupKey, spec.Profile.Columns, trackKeyColumns );
         groups[groupKey] = group;
      }

      var stats = new OriginStats( group.KeyCandidates, group.TrackedColumns );
      try
      {
         foreach( OriginRow row in OriginReader.Read( spec ) )
         {
            stats.Add( row );
         }
      }
      catch( Exception ex ) when( ex is not OperationCanceledException )
      {
         // Any read failure (bad quote, I/O, a corrupt workbook part) rejects this origin only.
         rejected.Add( new RejectedOrigin( spec.Origin, ex.Message ) );
         return;
      }

      string? reason = stats.RejectReason();
      if( reason != null )
      {
         rejected.Add( new RejectedOrigin( spec.Origin, reason ) );
         return;
      }

      group.Add( spec, stats );
   }

   /// <summary>
   /// Builds the grouping key: the normalized header, so files with the same columns in the
   /// same order land in one table. Header-less files group by column count.
   /// </summary>
   /// <param name="profile">Origin profile.</param>
   /// <returns>The group key.</returns>
   private static string GroupKey( FileProfile profile )
   {
      string columns = string.Join( '\u001F', profile.Columns.Select( c => c.Trim().ToLowerInvariant() ) );
      return profile.HasHeader ? columns : $"noheader|{columns}";
   }

   /// <summary>
   /// Turns a scanned origin into a table name stem. Uses the origin's real kind, so a '#' in a
   /// CSV or folder name is never mistaken for a sheet separator.
   /// </summary>
   /// <param name="spec">The origin.</param>
   /// <returns>The stem, or "table" when nothing usable is left.</returns>
   internal static string Stem( OriginSpec spec )
   {
      string? sheet = spec.Kind == OriginKind.ExcelSheet ? spec.Sheet : null;
      if( sheet == null )
      {
         return NameStem( spec.Origin, null );
      }

      string suffix = $"#{sheet}";
      string file = spec.Origin.EndsWith( suffix, StringComparison.Ordinal ) ? spec.Origin[..^suffix.Length] : spec.Origin;
      return NameStem( file, sheet );
   }

   /// <summary>
   /// Turns an origin id into a table name stem: letters only (any alphabet), words of two or
   /// more letters, joined with underscores. "orders_2024-01.csv" and "orders_2024-02.csv" both
   /// become "orders", which is what the user would call them. Only an origin that names a
   /// workbook ("book.xlsx#Sheet") is split at '#', so "orders #1.csv" keeps its name.
   /// </summary>
   /// <param name="origin">Origin id.</param>
   /// <returns>The stem, or "table" when nothing usable is left.</returns>
   internal static string Stem( string origin )
   {
      Match workbook = WORKBOOK_ORIGIN.Match( origin );
      return workbook.Success ? NameStem( workbook.Groups["file"].Value, workbook.Groups["sheet"].Value ) : NameStem( origin, null );
   }

   /// <summary>
   /// Builds the stem from a file path and, for workbooks, the sheet name. A sheet named like
   /// the Excel default ("Sheet1") says nothing, so the file name is used instead.
   /// </summary>
   /// <param name="file">File path (relative origin without any sheet part).</param>
   /// <param name="sheet">Sheet name for workbook origins, otherwise null.</param>
   /// <returns>The stem, or "table" when nothing usable is left.</returns>
   private static string NameStem( string file, string? sheet )
   {
      string name = sheet != null && !DEFAULT_SHEET_NAME.IsMatch( sheet ) ? sheet : Path.GetFileNameWithoutExtension( file );
      name = Compose( name ).ToLowerInvariant();
      string stem = string.Join( '_', NON_LETTERS.Split( name ).Where( t => t.Length > 1 ) );
      return stem.Length == 0 ? "table" : stem;
   }

   /// <summary>
   /// Composes accents into single characters so "e" plus a combining accent counts as one letter.
   /// A name that is not valid Unicode is returned unchanged.
   /// </summary>
   /// <param name="name">File or sheet name.</param>
   /// <returns>The composed name.</returns>
   private static string Compose( string name )
   {
      try
      {
         return name.Normalize( NormalizationForm.FormC );
      }
      catch( ArgumentException )
      {
         return name;
      }
   }

   #endregion Private Methods
}
