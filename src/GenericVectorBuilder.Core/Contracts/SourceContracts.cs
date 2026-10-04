namespace GenericVectorBuilder.Core.Contracts;

/// <summary>
/// One raw record pulled from a source before any mapping happens: a CSV row, an Excel row,
/// an ODBC row, or a file. The builder keeps the source's own column names and values so the
/// mapper can decide later what becomes embedded text and what becomes metadata.
/// Why: sources should only know how to READ. Every decision about text, keys and metadata
/// lives in one mapper so a new source type never has to repeat that logic.
/// </summary>
/// <param name="Table">Logical table the record belongs to (for a folder of CSVs, the header group name).</param>
/// <param name="Origin">Where the record physically came from, e.g. "orders/2024-01.csv" or "book.xlsx#Sheet1".
/// Deletes are scoped by origin, so a rejected file never loses its previously written vectors.</param>
/// <param name="RowNumber">1-based data row number inside the origin, for traceability in search results.</param>
/// <param name="Columns">Column names in source order.</param>
/// <param name="Values">Values aligned with <paramref name="Columns"/>; null means the cell was empty.</param>
/// <param name="TableId">Stable identity of the table that does not change when its display name does, e.g. a
/// hash of the normalized header. Null means the source has nothing better than <paramref name="Table"/>.</param>
public sealed record SourceRecord( string Table, string Origin, long RowNumber, IReadOnlyList<string> Columns, IReadOnlyList<string?> Values,
   string? TableId = null )
{
   /// <summary>
   /// The table identity used inside document keys: <see cref="TableId"/> when the source has one,
   /// otherwise <see cref="Table"/>.
   /// Why: a display name derived from file names can change between runs (a file is added or
   /// removed and the majority stem flips). Keys built on it would re-key and re-embed the whole
   /// table, so keys use the stable id instead.
   /// </summary>
   public string KeyScope => TableId ?? Table;
}

/// <summary>
/// What a source learned while it was being read: which origins were read cleanly, which were
/// only partly read, and which were rejected and why. The pipeline uses this to decide whether
/// deletes are safe.
/// Why: a failed or half-read source must never look like "everything was deleted". Only
/// origins that were read completely are allowed to drive deletes; everything else keeps its
/// old vectors unless the source positively confirms the origin is gone.
/// </summary>
public sealed class SourceReadReport
{
   #region Data Members

   private readonly object _sync = new();
   private readonly List<RejectedOrigin> _rejected = new();
   private readonly HashSet<string> _clean = new( StringComparer.Ordinal );
   private readonly HashSet<string> _rejectedOrigins = new( StringComparer.Ordinal );
   private readonly Dictionary<string, long> _skippedByOrigin = new( StringComparer.Ordinal );
   private readonly Dictionary<string, string> _ignored = new( StringComparer.Ordinal );
   private long _skippedRows;

   #endregion Data Members

   #region Public Methods

   /// <summary>
   /// True only when the source enumerated everything it was asked to read with no
   /// source-level failure. Deletes are skipped entirely when this is false.
   /// </summary>
   public bool Completed { get; set; }

   /// <summary>
   /// Origins that were read from start to finish, with no rejected or skipped rows. These
   /// are the only origins whose missing rows may be deleted from the sinks.
   /// </summary>
   public IReadOnlyCollection<string> CleanOrigins
   {
      get
      {
         lock( _sync )
         {
            return _clean.ToList();
         }
      }
   }

   /// <summary>
   /// Origins that were skipped, with a human readable reason for the reject list in the UI.
   /// </summary>
   public IReadOnlyList<RejectedOrigin> Rejected
   {
      get
      {
         lock( _sync )
         {
            return _rejected.ToList();
         }
      }
   }

   /// <summary>
   /// Origins that were read but had rows skipped (for example a row with more cells than the
   /// header), with the number skipped. They are protected from deletes like a rejected origin.
   /// </summary>
   public IReadOnlyDictionary<string, long> SkippedByOrigin
   {
      get
      {
         lock( _sync )
         {
            return new Dictionary<string, long>( _skippedByOrigin, StringComparer.Ordinal );
         }
      }
   }

   /// <summary>
   /// Rows skipped across every origin, for the run summary.
   /// </summary>
   public long SkippedRows => Interlocked.Read( ref _skippedRows );

   /// <summary>
   /// Records that an origin was read completely, so its missing rows may be deleted from sinks.
   /// Ignored for an origin that was already rejected or had rows skipped, because a partly read
   /// origin must never drive deletes.
   /// </summary>
   /// <param name="origin">The origin identifier, exactly as stamped on its records.</param>
   public void MarkClean( string origin )
   {
      lock( _sync )
      {
         if( !_rejectedOrigins.Contains( origin ) && !_skippedByOrigin.ContainsKey( origin ) )
         {
            _clean.Add( origin );
         }
      }
   }

   /// <summary>
   /// Records that an origin was skipped. Its previously written vectors are kept untouched.
   /// </summary>
   /// <param name="origin">The origin identifier.</param>
   /// <param name="reason">Short plain-English reason shown to the user.</param>
   public void MarkRejected( string origin, string reason )
   {
      lock( _sync )
      {
         _clean.Remove( origin );
         if( _rejectedOrigins.Add( origin ) )
         {
            _rejected.Add( new RejectedOrigin( origin, reason ) );
         }
      }
   }

   /// <summary>
   /// Records that one row of an origin could not be used and was skipped. The origin stops
   /// counting as clean, so the old version of that row (and every other old row of the origin)
   /// is kept instead of being deleted as "removed from the source".
   /// </summary>
   /// <param name="origin">The origin identifier.</param>
   public void MarkRowSkipped( string origin )
   {
      Interlocked.Increment( ref _skippedRows );
      lock( _sync )
      {
         _clean.Remove( origin );
         _skippedByOrigin[origin] = _skippedByOrigin.TryGetValue( origin, out long count ) ? count + 1 : 1;
      }
   }

   /// <summary>
   /// True when the origin was read completely this run.
   /// </summary>
   /// <param name="origin">The origin identifier.</param>
   /// <returns>True for a clean origin.</returns>
   public bool IsClean( string origin )
   {
      lock( _sync )
      {
         return _clean.Contains( origin );
      }
   }

   /// <summary>
   /// True when at least one row of the origin was skipped this run.
   /// </summary>
   /// <param name="origin">The origin identifier.</param>
   /// <returns>True for a partly read origin.</returns>
   public bool HasSkippedRows( string origin )
   {
      lock( _sync )
      {
         return _skippedByOrigin.ContainsKey( origin );
      }
   }

   /// <summary>
   /// Records an origin the source skipped on purpose (a hidden or empty sheet, a symbolic link).
   /// It is not read, so it never drives deletes, and its kept rows are reported with this reason
   /// instead of as a mystery.
   /// </summary>
   /// <param name="origin">The origin identifier.</param>
   /// <param name="reason">Short plain-English reason, e.g. "hidden sheet".</param>
   public void MarkIgnored( string origin, string reason )
   {
      lock( _sync )
      {
         _ignored.TryAdd( origin, reason );
      }
   }

   /// <summary>
   /// Tells the pipeline whether an origin was rejected in this run, which protects its old data.
   /// A rejection also covers everything inside it: "book.xlsx" covers "book.xlsx#Parts" (a
   /// workbook that will not open is rejected before its sheets are known), and a folder "sub"
   /// that could not be listed covers "sub/inner.csv".
   /// </summary>
   /// <param name="origin">The origin identifier.</param>
   /// <returns>True when the origin, or the file or folder that contains it, appears in the reject list.</returns>
   public bool IsRejected( string origin )
   {
      lock( _sync )
      {
         return SelfOrContainers( origin ).Any( _rejectedOrigins.Contains );
      }
   }

   /// <summary>
   /// The reason an origin, or the file or folder that contains it, was skipped on purpose.
   /// </summary>
   /// <param name="origin">The origin identifier.</param>
   /// <returns>The reason, or null when it was not ignored.</returns>
   public string? IgnoredReason( string origin )
   {
      lock( _sync )
      {
         foreach( string candidate in SelfOrContainers( origin ) )
         {
            if( _ignored.TryGetValue( candidate, out string? reason ) )
            {
               return reason;
            }
         }

         return null;
      }
   }

   #endregion Public Methods

   #region Private Methods

   /// <summary>
   /// The origin itself plus every prefix that ends where a folder ('/') or a sheet ('#') starts,
   /// i.e. every file or folder that contains it.
   /// </summary>
   /// <param name="origin">The origin identifier.</param>
   /// <returns>The origin and its containers.</returns>
   private static IEnumerable<string> SelfOrContainers( string origin )
   {
      yield return origin;
      for( int i = 1; i < origin.Length; i++ )
      {
         if( origin[i] is '/' or '#' )
         {
            yield return origin[..i];
         }
      }
   }

   #endregion Private Methods
}

/// <summary>
/// An origin the source refused to read, and why. Shown in the UI reject list.
/// </summary>
/// <param name="Origin">The origin identifier, e.g. a relative file path.</param>
/// <param name="Reason">Plain-English reason, e.g. "unclosed quote at line 12".</param>
public sealed record RejectedOrigin( string Origin, string Reason );

/// <summary>
/// A readable source of records. Implementations stream records and fill in a
/// <see cref="SourceReadReport"/> as they go.
/// Why: streaming keeps memory flat for large tables, and the report gives the pipeline the
/// facts it needs to decide whether deleting stale vectors is safe.
/// </summary>
public interface ISource
{
   /// <summary>
   /// Short description for logs and the UI, e.g. "folder /home/dan/share/orders".
   /// </summary>
   string Description { get; }

   /// <summary>
   /// Report filled in while <see cref="ReadAsync"/> runs. Only meaningful once enumeration ends.
   /// </summary>
   SourceReadReport Report { get; }

   /// <summary>
   /// Streams every record the source holds. Bad origins are rejected into <see cref="Report"/>
   /// instead of throwing, so one bad file never kills a run.
   /// </summary>
   /// <param name="ct">Cancellation for the whole read.</param>
   /// <returns>The records, in source order.</returns>
   IAsyncEnumerable<SourceRecord> ReadAsync( CancellationToken ct );

   /// <summary>
   /// Asks the source whether an origin that was NOT read this run is positively known to be
   /// gone, for example a file that was deleted. Called after <see cref="ReadAsync"/> for the
   /// origins of stored documents the run did not see.
   /// Why: an origin can be missing from a run because it was removed, or because it could not
   /// be reached (a folder without permission, a share that dropped). Only the first may delete
   /// vectors, so any doubt must answer false.
   /// </summary>
   /// <param name="origin">The origin identifier as it was stamped on the stored documents.</param>
   /// <param name="ct">Cancellation.</param>
   /// <returns>True only when the origin is confirmed gone; false when it exists or cannot be checked.</returns>
   Task<bool> ConfirmGoneAsync( string origin, CancellationToken ct );
}
