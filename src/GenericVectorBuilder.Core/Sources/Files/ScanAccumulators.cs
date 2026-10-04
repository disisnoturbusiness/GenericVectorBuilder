using System.Security.Cryptography;
using System.Text;

namespace GenericVectorBuilder.Core.Sources.Files;

/// <summary>
/// Per-origin counters collected while the scanner reads a file: good rows, bad rows, the
/// preview sample, and the values of columns that might be a unique key.
/// Why track key values: a key column lets re-runs recognize a changed row as the SAME row
/// (update in place) instead of a delete plus an insert. Only columns proven unique across
/// the whole table are suggested, so a "customer_id" inside an orders file is never offered.
/// A second, separate tracking serves the columns the caller asked about by name: it keeps the
/// distinct values of each such column, so the group can later work out which values turn up in
/// more than one file. Within-file repeats are folded away here, so a value that appears twice
/// in one file is still a single value of that origin.
/// </summary>
internal sealed class OriginStats
{
   #region Data Members

   private readonly IReadOnlyList<int> _keyCandidates;
   private readonly IReadOnlyList<int> _trackedColumns;

   #endregion Data Members

   #region Constructor

   /// <summary>
   /// Creates stats tracking the given key-candidate column indexes.
   /// </summary>
   /// <param name="keyCandidates">Column indexes whose names look like keys.</param>
   /// <param name="trackedColumns">Column indexes the caller asked to track across origins; null or empty for none.</param>
   public OriginStats( IReadOnlyList<int> keyCandidates, IReadOnlyList<int>? trackedColumns = null )
   {
      _keyCandidates = keyCandidates;
      _trackedColumns = trackedColumns ?? Array.Empty<int>();
      KeyValues = keyCandidates.ToDictionary( i => i, _ => (HashSet<string>?)new HashSet<string>( StringComparer.Ordinal ) );
      TrackedValues = _trackedColumns.Select( _ => new HashSet<string>( StringComparer.Ordinal ) ).ToArray();
   }

   #endregion Constructor

   #region Public Methods

   /// <summary>Readable rows.</summary>
   public long DataRows { get; private set; }

   /// <summary>Rows skipped for extra non-empty cells.</summary>
   public long BadRows { get; private set; }

   /// <summary>First rows, for the preview.</summary>
   public List<IReadOnlyList<string?>> Sample { get; } = new();

   /// <summary>Values per key candidate; null once the column proved not to be a key.</summary>
   public Dictionary<int, HashSet<string>?> KeyValues { get; }

   /// <summary>
   /// Distinct non-empty values of each tracked column, in the same order as the tracked column
   /// indexes given to the constructor. Unlike <see cref="KeyValues"/> a repeat does not discard
   /// the column: the group needs every value to compare against its other origins.
   /// </summary>
   public HashSet<string>[] TrackedValues { get; }

   /// <summary>
   /// Counts one row and tracks its key-candidate values. A missing or repeated value
   /// disqualifies the column for good.
   /// </summary>
   /// <param name="row">The row.</param>
   public void Add( OriginRow row )
   {
      if( row.WrongWidth )
      {
         BadRows++;
         return;
      }

      DataRows++;
      if( Sample.Count < FolderScanner.SAMPLE_ROWS )
      {
         Sample.Add( row.Values );
      }

      foreach( int index in _keyCandidates )
      {
         HashSet<string>? seen = KeyValues[index];
         string? value = row.Values[index];
         if( seen != null && ( value == null || !seen.Add( value ) || seen.Count > FolderScanner.MAX_KEY_VALUES ) )
         {
            KeyValues[index] = null;
         }
      }

      for( int t = 0; t < _trackedColumns.Count; t++ )
      {
         string? value = row.Values[_trackedColumns[t]];
         if( value != null )
         {
            TrackedValues[t].Add( value );
         }
      }
   }

   /// <summary>
   /// Decides whether the origin should be rejected after a full read: no data at all, or
   /// more than 5% of rows misaligned, which means the file is not what its header says.
   /// </summary>
   /// <returns>A plain-English reason, or null when the origin is usable.</returns>
   public string? RejectReason()
   {
      if( DataRows == 0 )
      {
         return BadRows == 0 ? "no data rows" : $"all {BadRows} rows have extra columns";
      }

      long total = DataRows + BadRows;
      return BadRows > total * FolderScanner.MAX_BAD_ROW_RATIO ? $"{BadRows} of {total} rows have extra columns" : null;
   }

   #endregion Public Methods
}

/// <summary>
/// Accumulates the origins of one header group and builds the final logical table.
/// </summary>
internal sealed class GroupBuilder
{
   #region Data Members

   private readonly IReadOnlyList<string> _columns;
   private readonly Dictionary<int, HashSet<string>?> _keyValues;
   private readonly List<IReadOnlyList<string?>> _sample = new();
   private readonly HashSet<string>?[] _trackedSeen;
   private readonly HashSet<string>?[] _trackedRepeats;

   #endregion Data Members

   #region Constructor

   /// <summary>
   /// Starts a group for the given columns and picks which columns could be keys by name.
   /// </summary>
   /// <param name="groupKey">The scanner's grouping key (normalized header), which identifies this group.</param>
   /// <param name="columns">Shared column names.</param>
   /// <param name="trackKeyColumns">Column names (any case) whose values are compared across origins; null or empty for none.</param>
   public GroupBuilder( string groupKey, IReadOnlyList<string> columns, IReadOnlyCollection<string>? trackKeyColumns = null )
   {
      _columns = columns;
      HeaderHash = Convert.ToHexString( SHA256.HashData( Encoding.UTF8.GetBytes( groupKey ) ) ).ToLowerInvariant();
      KeyCandidates = Enumerable.Range( 0, columns.Count ).Where( i => FolderScanner.KEY_COLUMN.IsMatch( columns[i] ) ).ToList();
      _keyValues = KeyCandidates.ToDictionary( i => i, _ => (HashSet<string>?)new HashSet<string>( StringComparer.Ordinal ) );
      TrackedColumns = PickTrackedColumns( columns, trackKeyColumns );
      _trackedSeen = new HashSet<string>?[TrackedColumns.Count];
      _trackedRepeats = new HashSet<string>?[TrackedColumns.Count];
   }

   #endregion Constructor

   #region Public Methods

   /// <summary>Column indexes whose names look like keys.</summary>
   public IReadOnlyList<int> KeyCandidates { get; }

   /// <summary>Column indexes whose names the caller asked to track across origins.</summary>
   public IReadOnlyList<int> TrackedColumns { get; }

   /// <summary>Accepted origins.</summary>
   public List<ScannedOrigin> Origins { get; } = new();

   /// <summary>
   /// Stable hex fingerprint of this group's header. Unlike a counter, it does not change when
   /// other files appear or the folder is walked in a different order.
   /// </summary>
   public string HeaderHash { get; }

   /// <summary>
   /// Adds an accepted origin and merges its key values, dropping any candidate that repeats
   /// a value already seen in another file of the group. The tracked columns are merged here
   /// too, which is why a rejected origin never takes part: it is never added.
   /// </summary>
   /// <param name="spec">The origin.</param>
   /// <param name="stats">Its row stats.</param>
   public void Add( OriginSpec spec, OriginStats stats )
   {
      Origins.Add( new ScannedOrigin( spec, stats.DataRows, stats.BadRows ) );
      _sample.AddRange( stats.Sample.Take( FolderScanner.SAMPLE_ROWS - _sample.Count ) );
      foreach( int index in KeyCandidates )
      {
         HashSet<string>? merged = _keyValues[index];
         HashSet<string>? incoming = stats.KeyValues[index];
         if( merged == null || incoming == null || incoming.Any( v => !merged.Add( v ) ) || merged.Count > FolderScanner.MAX_KEY_VALUES )
         {
            _keyValues[index] = null;
         }
      }

      MergeTracked( stats );
   }

   /// <summary>
   /// The table name stem for this group: the most common file stem among its origins, with
   /// ties going to the alphabetically first stem.
   /// </summary>
   /// <returns>The stem, before any collision suffix.</returns>
   public string Stem()
   {
      return Origins.GroupBy( o => FolderScanner.Stem( o.Spec ) )
         .OrderByDescending( g => g.Count() ).ThenBy( g => g.Key, StringComparer.Ordinal ).First().Key;
   }

   /// <summary>
   /// Builds the table under a name chosen by <see cref="TableNamer"/>.
   /// </summary>
   /// <param name="name">The table name.</param>
   /// <returns>The table.</returns>
   public ScannedTable Build( string name )
   {
      int? key = KeyCandidates.Where( i => _keyValues[i] != null ).Select( i => (int?)i ).FirstOrDefault();
      return new ScannedTable( name, _columns, Origins, Origins.Sum( o => o.DataRows ), _sample, key.HasValue ? _columns[key.Value] : null )
      {
         CrossOriginDuplicates = TakeCrossOriginDuplicates(),
      };
   }

   #endregion Public Methods

   #region Private Methods

   /// <summary>
   /// Finds the tracked columns: those whose header name matches a requested name, ignoring case.
   /// The match is exact apart from case, the same rule the row mapper uses to find its key column,
   /// so a name that finds a column here finds the same column there.
   /// </summary>
   /// <param name="columns">Shared column names.</param>
   /// <param name="requested">Requested names, or null.</param>
   /// <returns>Column indexes in header order.</returns>
   private static IReadOnlyList<int> PickTrackedColumns( IReadOnlyList<string> columns, IReadOnlyCollection<string>? requested )
   {
      if( requested == null || requested.Count == 0 )
      {
         return Array.Empty<int>();
      }

      var names = new HashSet<string>( requested.Where( n => !string.IsNullOrWhiteSpace( n ) ), StringComparer.OrdinalIgnoreCase );
      return Enumerable.Range( 0, columns.Count ).Where( i => names.Contains( columns[i] ) ).ToList();
   }

   /// <summary>
   /// Folds one accepted origin's distinct values into the group. A value already seen in an
   /// earlier origin is a cross-origin repeat. Because each origin's set holds a value once,
   /// a collision can only come from a different origin, never from a repeat inside one file.
   /// The first origin's set is adopted as is rather than copied, which halves the peak memory
   /// for the common one-big-file table.
   /// </summary>
   /// <param name="stats">Row stats of the origin just accepted.</param>
   private void MergeTracked( OriginStats stats )
   {
      for( int t = 0; t < TrackedColumns.Count; t++ )
      {
         HashSet<string> incoming = stats.TrackedValues[t];
         HashSet<string>? seen = _trackedSeen[t];
         if( seen == null )
         {
            _trackedSeen[t] = incoming;
            continue;
         }

         foreach( string value in incoming )
         {
            if( !seen.Add( value ) )
            {
               ( _trackedRepeats[t] ??= new HashSet<string>( StringComparer.Ordinal ) ).Add( value );
            }
         }
      }
   }

   /// <summary>
   /// Hands over the cross-origin repeats and lets go of everything else. Only a tracked column
   /// that really has a repeat gets an entry, and only the repeated values are kept: the values
   /// that were seen in a single origin are dropped, so a million-row key column does not stay
   /// in memory after the scan. Call once, when the table is built.
   /// </summary>
   /// <returns>Repeated values by column name (case-insensitive); empty when there are none.</returns>
   private IReadOnlyDictionary<string, IReadOnlySet<string>> TakeCrossOriginDuplicates()
   {
      var result = new Dictionary<string, IReadOnlySet<string>>( StringComparer.OrdinalIgnoreCase );
      for( int t = 0; t < TrackedColumns.Count; t++ )
      {
         HashSet<string>? repeats = _trackedRepeats[t];
         if( repeats != null && repeats.Count > 0 )
         {
            result[_columns[TrackedColumns[t]]] = repeats;
         }

         _trackedSeen[t] = null;
         _trackedRepeats[t] = null;
      }

      return result;
   }

   #endregion Private Methods
}

/// <summary>
/// Chooses the final table names for a scan.
/// Why names must be stable: a table name is part of every document key ("orders|42") and of
/// the user's key-column mapping. If a table's name changes between runs, every row is
/// re-keyed, re-embedded and the old copies deleted, and the user's mapping silently stops
/// matching. So a name depends only on the group's own content, never on path order or on
/// which other groups happened to be accepted first: a stem used by one header group is the
/// name as is, and a stem shared by several header groups (a/orders.csv and b/orders.csv with
/// different columns) gives each group "stem_" plus a short fingerprint of its header.
/// </summary>
internal static class TableNamer
{
   #region Data Members

   private const int HEADER_HASH_LENGTH = 6;

   #endregion Data Members

   #region Public Methods

   /// <summary>
   /// Assigns a unique, order-independent name to every group.
   /// </summary>
   /// <param name="groups">The accepted groups.</param>
   /// <returns>The name of each group.</returns>
   public static IReadOnlyDictionary<GroupBuilder, string> Assign( IReadOnlyList<GroupBuilder> groups )
   {
      var used = new HashSet<string>( StringComparer.OrdinalIgnoreCase );
      var names = new Dictionary<GroupBuilder, string>();
      IEnumerable<IGrouping<string, GroupBuilder>> families = groups.GroupBy( g => g.Stem(), StringComparer.OrdinalIgnoreCase )
         .OrderBy( f => f.Key, StringComparer.Ordinal );
      foreach( IGrouping<string, GroupBuilder> family in families )
      {
         if( family.Count() == 1 )
         {
            names[family.Single()] = Claim( used, family.Key );
            continue;
         }

         foreach( GroupBuilder group in family.OrderBy( g => g.HeaderHash, StringComparer.Ordinal ) )
         {
            names[group] = Claim( used, $"{family.Key}_{group.HeaderHash[..HEADER_HASH_LENGTH]}" );
         }
      }

      return names;
   }

   #endregion Public Methods

   #region Private Methods

   /// <summary>
   /// Takes a name, adding a numeric suffix only in the vanishingly rare case that two
   /// different stems or fingerprints still produce the same text.
   /// </summary>
   /// <param name="used">Names already taken (case-insensitive).</param>
   /// <param name="wanted">The preferred name.</param>
   /// <returns>A name no other table has.</returns>
   private static string Claim( HashSet<string> used, string wanted )
   {
      string name = wanted;
      for( int n = 2; !used.Add( name ); n++ )
      {
         name = $"{wanted}_{n}";
      }

      return name;
   }

   #endregion Private Methods
}
