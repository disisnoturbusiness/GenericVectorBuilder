using System.Runtime.CompilerServices;
using System.Security;
using GenericVectorBuilder.Core.Contracts;
using GenericVectorBuilder.Core.Mapping;

namespace GenericVectorBuilder.Core.Sources.Files;

/// <summary>
/// Source over a scanned folder: streams every row of every accepted origin as a
/// <see cref="SourceRecord"/>, tagged with its logical table, the table's stable id and origin.
/// Why it starts from a scan result instead of the folder: the user approved a specific set of
/// tables in the preview. Reading exactly that set means the run does what the preview showed,
/// and the scan's rejects are carried into the report so those files keep their old vectors.
/// Why it can confirm an origin is gone: a file missing from the scan was either deleted or
/// could not be reached (a subfolder without permission, a share that dropped mid-walk). The
/// scan cannot tell those apart, so before anything is deleted the source checks the disk: the
/// file must be absent from a directory listing that actually worked, and a sheet must be absent
/// from a workbook that actually opened.
/// </summary>
public sealed class FolderSource : ISource
{
   #region Data Members

   private const int TABLE_ID_HEX = 16;
   private static readonly HashSet<string> EXCEL_EXTENSIONS = new( StringComparer.OrdinalIgnoreCase ) { ".xlsx", ".xlsm", ".xls" };
   private static readonly EnumerationOptions LIST_EVERYTHING = new()
   {
      RecurseSubdirectories = false,
      IgnoreInaccessible = false,
      AttributesToSkip = 0,
      ReturnSpecialDirectories = false,
   };

   private readonly FolderScanResult _scan;
   private readonly string _root;
   private readonly string _rootPrefix;
   private readonly Dictionary<string, HashSet<string>?> _listings = new( StringComparer.Ordinal );
   private readonly Dictionary<string, HashSet<string>?> _workbookSheets = new( StringComparer.Ordinal );

   #endregion Data Members

   #region Constructor

   /// <summary>
   /// Creates the source over a completed scan.
   /// </summary>
   /// <param name="scan">The scan the user approved.</param>
   public FolderSource( FolderScanResult scan )
   {
      _scan = scan;
      _root = Path.TrimEndingDirectorySeparator( Path.GetFullPath( scan.Root ) );
      _rootPrefix = Path.EndsInDirectorySeparator( _root ) ? _root : _root + Path.DirectorySeparatorChar;
      foreach( RejectedOrigin rejected in scan.Rejected )
      {
         Report.MarkRejected( rejected.Origin, rejected.Reason );
      }

      foreach( RejectedOrigin ignored in scan.Ignored )
      {
         Report.MarkIgnored( ignored.Origin, ignored.Reason );
      }
   }

   #endregion Constructor

   #region Public Methods

   /// <inheritdoc />
   public string Description => $"folder {_scan.Root}";

   /// <inheritdoc />
   public SourceReadReport Report { get; } = new();

   /// <summary>
   /// Rows skipped during the run because they had extra non-empty cells.
   /// </summary>
   public long SkippedRows => Report.SkippedRows;

   /// <summary>
   /// The stable id of a scanned table: a hash of its normalized header (trimmed, lower-cased
   /// column names in order, and whether the file had a header row at all). It is the same id
   /// every run as long as the header is the same, whatever the table's display name is.
   /// Why: the display name comes from the majority file stem, so adding or removing one file
   /// can rename the table. Document keys built on the name would then change and the whole
   /// table would be re-embedded. The web layer can also key mappings by this id.
   /// </summary>
   /// <param name="table">A table from the scan.</param>
   /// <returns>An id such as "t3f9a2c41d0e7b6a5".</returns>
   public static string TableId( ScannedTable table )
   {
      bool hasHeader = table.Origins.Count == 0 || table.Origins[0].Spec.Profile.HasHeader;
      string header = string.Join( '\u001F', table.Columns.Select( c => c.Trim().ToLowerInvariant() ) );
      return "t" + RowDocumentMapper.Hash( hasHeader ? header : $"noheader|{header}" )[..TABLE_ID_HEX];
   }

   /// <inheritdoc />
   public async IAsyncEnumerable<SourceRecord> ReadAsync( [EnumeratorCancellation] CancellationToken ct )
   {
      foreach( ScannedTable table in _scan.Tables )
      {
         string tableId = TableId( table );
         foreach( ScannedOrigin origin in table.Origins )
         {
            ct.ThrowIfCancellationRequested();
            await foreach( SourceRecord record in ReadOriginAsync( table, tableId, origin.Spec, ct ) )
            {
               yield return record;
            }
         }
      }

      Report.Completed = true;
   }

   /// <inheritdoc />
   public Task<bool> ConfirmGoneAsync( string origin, CancellationToken ct )
   {
      ct.ThrowIfCancellationRequested();
      return Task.FromResult( IsGone( origin ) );
   }

   #endregion Public Methods

   #region Private Methods

   /// <summary>
   /// Reads one origin. A failure part way through (the file changed since the scan) rejects
   /// that origin and moves on; rows already emitted from it are still valid rows. A row with
   /// extra non-empty cells is skipped and marks the origin as only partly read, which keeps
   /// its old vectors safe from deletes.
   /// </summary>
   /// <param name="table">Owning table.</param>
   /// <param name="tableId">The table's stable id.</param>
   /// <param name="spec">Origin to read.</param>
   /// <param name="ct">Cancellation.</param>
   /// <returns>The origin's records.</returns>
   private async IAsyncEnumerable<SourceRecord> ReadOriginAsync( ScannedTable table, string tableId, OriginSpec spec, [EnumeratorCancellation] CancellationToken ct )
   {
      using IEnumerator<OriginRow> rows = OriginReader.Read( spec ).GetEnumerator();
      while( true )
      {
         ct.ThrowIfCancellationRequested();
         OriginRow row;
         try
         {
            if( !rows.MoveNext() )
            {
               break;
            }

            row = rows.Current;
         }
         catch( Exception ex ) when( ex is not OperationCanceledException )
         {
            Report.MarkRejected( spec.Origin, ex.Message );
            yield break;
         }

         if( row.WrongWidth )
         {
            Report.MarkRowSkipped( spec.Origin );
            continue;
         }

         yield return new SourceRecord( table.Name, spec.Origin, row.RowNumber, table.Columns, row.Values, tableId );
      }

      Report.MarkClean( spec.Origin );
      await Task.CompletedTask;
   }

   /// <summary>
   /// Decides whether an origin is positively gone. An origin the run saw in any form (read,
   /// rejected, partly read, skipped on purpose) exists. A sheet origin is gone only when its workbook opens cleanly
   /// and does not list the sheet. A file origin is gone only when a working directory listing
   /// proves it absent.
   /// </summary>
   /// <param name="origin">Origin id, a path relative to the root, plus "#Sheet" for a workbook sheet.</param>
   /// <returns>True only when the origin is confirmed gone.</returns>
   private bool IsGone( string origin )
   {
      if( origin.Length == 0 || Report.IsClean( origin ) || Report.IsRejected( origin ) || Report.HasSkippedRows( origin )
         || Report.IgnoredReason( origin ) != null )
      {
         return false;
      }

      // A sheet origin is "<workbook path>#<sheet>". File names may contain '#' too, so try
      // every split whose left side is an Excel file that exists.
      var candidates = new List<string> { origin };
      for( int hash = origin.IndexOf( '#' ); hash >= 0; hash = origin.IndexOf( '#', hash + 1 ) )
      {
         string workbook = origin[..hash];
         if( hash > 0 && EXCEL_EXTENSIONS.Contains( Path.GetExtension( workbook ) ) )
         {
            string? full = FullPathUnderRoot( workbook );
            if( full != null && File.Exists( full ) )
            {
               return SheetGone( full, origin[( hash + 1 )..] );
            }

            candidates.Add( workbook );
         }
      }

      // No workbook reading applies, so every way of reading the origin as a path must be absent.
      return candidates.All( PathConfirmedAbsent );
   }

   /// <summary>
   /// True when the workbook opens and has no sheet by that name. A workbook that will not open
   /// proves nothing, so its sheets are kept.
   /// </summary>
   /// <param name="workbookPath">Absolute workbook path.</param>
   /// <param name="sheet">Sheet name.</param>
   /// <returns>True when the sheet is confirmed gone.</returns>
   private bool SheetGone( string workbookPath, string sheet )
   {
      if( !_workbookSheets.TryGetValue( workbookPath, out HashSet<string>? sheets ) )
      {
         try
         {
            sheets = OriginReader.ListSheets( workbookPath ).Select( s => s.Name ).ToHashSet( StringComparer.Ordinal );
         }
         catch( Exception ex ) when( ex is not OperationCanceledException )
         {
            sheets = null;
         }

         _workbookSheets[workbookPath] = sheets;
      }

      return sheets != null && !sheets.Contains( sheet );
   }

   /// <summary>
   /// True when a relative path is proven absent: nothing exists at it, and the nearest
   /// directory above it that does exist can be listed and does not contain the next path
   /// component. A directory that cannot be listed (no permission, I/O error) proves nothing.
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
   /// Lists the entry names of a directory, including hidden ones, caching the answer for the run.
   /// </summary>
   /// <param name="directory">Absolute directory path.</param>
   /// <returns>The names, or null when the directory cannot be listed.</returns>
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
   /// Resolves a relative origin path against the root, refusing anything that escapes it.
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

   #endregion Private Methods
}
