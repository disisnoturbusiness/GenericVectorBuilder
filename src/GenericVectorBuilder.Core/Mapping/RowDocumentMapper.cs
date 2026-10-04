using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using GenericVectorBuilder.Core.Contracts;

namespace GenericVectorBuilder.Core.Mapping;

/// <summary>
/// Per-table mapping choices made in the UI: which column identifies a row, and optionally a
/// text template such as "{Name}: {Description}".
/// </summary>
/// <param name="KeyColumn">Column whose value identifies a row across runs, or null to use the row content.</param>
/// <param name="Template">Template with {Column} placeholders, or null for the default "Column: value" lines.</param>
public sealed record TableMapping( string? KeyColumn, string? Template );

/// <summary>
/// Turns source records into documents: embedded text, stable key, metadata and content hash.
/// Why the default text is "Column: value" lines: the embedder sees the column names as
/// context ("Status: overdue" means more than "overdue"), empty cells add nothing and are
/// left out, and no configuration is needed for the 200-CSV user.
/// Why keys work the way they do: with a key column a changed row keeps its identity and is
/// updated in place. Without one, the row's content plus its origin is its identity, so a
/// changed row is written as new and the old version is deleted, and an identical row in a
/// sibling file never borrows another file's key. Keys start with the table's stable id, not
/// its display name, so a renamed table keeps its keys. Duplicate keys get a #2, #3 suffix
/// instead of overwriting each other, and a generated suffix is reserved so a real key that
/// happens to end in "#2" can never collide with it. Over-long keys are hashed so every sink's
/// key column can hold them.
/// Why a key value shared by several files is qualified by its file: a key column chosen in the
/// UI is often unique inside each file but not across them (two monthly exports that both
/// number their rows from 1). Suffixing by read order then gives the same row a different key
/// whenever an earlier file is rejected or removed, so one file's row overwrites another's and
/// the rejected file's copy is deleted. Values the scan found in more than one file therefore
/// get "@" plus a short hash of their origin, so each file's copy keeps its own key whatever
/// else happens in the folder. Every other value keeps exactly the key it always had.
/// It is the table-row <see cref="IDocumentMapper"/>; source code uses its own mapper.
/// </summary>
public sealed class RowDocumentMapper : IDocumentMapper
{
   #region Data Members

   /// <summary>Longest document key kept as is; longer keys are replaced by a hash.</summary>
   public const int MAX_KEY_CHARS = 200;

   private const int MAX_SCOPE_CHARS_IN_HASHED_KEY = 64;
   private const int CONTENT_KEY_HEX = 16;

   /// <summary>
   /// Hex digits of the origin hash appended to a key value shared across files. 40 bits keep
   /// two files of one table apart with a collision chance of about one in a trillion per pair,
   /// while adding only 11 characters to the key.
   /// </summary>
   private const int ORIGIN_KEY_HEX = 10;

   private static readonly Regex PLACEHOLDER = new( @"\{([^{}]+)\}", RegexOptions.Compiled );

   private readonly IReadOnlyDictionary<string, TableMapping> _mappings;
   private readonly HashSet<string> _usedKeys = new( StringComparer.Ordinal );
   private readonly Dictionary<string, int> _nextSuffix = new( StringComparer.Ordinal );
   private readonly HashSet<string> _matchedMappings = new( StringComparer.Ordinal );
   private readonly Dictionary<string, Dictionary<string, HashSet<string>>> _sharedById = new( StringComparer.Ordinal );
   private readonly Dictionary<string, Dictionary<string, HashSet<string>>> _sharedByName = new( StringComparer.Ordinal );

   #endregion Data Members

   #region Constructor

   /// <summary>
   /// Creates a mapper for one run. It is stateful (duplicate-key tracking), so make a new one per run.
   /// </summary>
   /// <param name="mappings">Mappings by table id or table name; tables not listed use the defaults.</param>
   public RowDocumentMapper( IReadOnlyDictionary<string, TableMapping> mappings )
   {
      _mappings = mappings;
   }

   #endregion Constructor

   #region Public Methods

   /// <summary>
   /// Rows whose key column was empty or missing and fell back to a content key, for the run summary.
   /// </summary>
   public long MissingKeyRows { get; private set; }

   /// <summary>
   /// Mapping entries with a key column or template that matched no record this run, for
   /// example settings saved under a table name the folder no longer produces. The run reports
   /// them so a silently dropped key column does not go unnoticed. Entries that only restate the
   /// defaults are left out, because ignoring them changes nothing.
   /// </summary>
   public IReadOnlyList<string> UnusedMappings => _mappings
      .Where( m => !_matchedMappings.Contains( m.Key ) && ( m.Value.KeyColumn != null || !string.IsNullOrWhiteSpace( m.Value.Template ) ) )
      .Select( m => m.Key )
      .ToList();

   /// <summary>
   /// Tells the mapper which key values of a table appear in more than one file (or sheet), as
   /// found by the folder scan. Rows whose key-column value is in that set get a document key
   /// qualified by their origin; every other row keeps exactly the key it would have had
   /// without this call. Calling it again for the same table replaces the earlier sets.
   /// Why: see the class summary. Without it, a rejected or removed file shifts the duplicate
   /// suffixes of the files after it, and their rows overwrite or delete its kept rows.
   /// </summary>
   /// <param name="tableId">The table's stable id, as stamped on its records.</param>
   /// <param name="tableName">The table's display name, used for records that carry no id.</param>
   /// <param name="duplicates">Key values found in more than one accepted origin, by column name (case-insensitive).</param>
   public void SetCrossOriginDuplicates( string tableId, string tableName, IReadOnlyDictionary<string, IReadOnlySet<string>> duplicates )
   {
      var byColumn = new Dictionary<string, HashSet<string>>( StringComparer.OrdinalIgnoreCase );
      foreach( KeyValuePair<string, IReadOnlySet<string>> column in duplicates )
      {
         if( column.Value.Count > 0 )
         {
            byColumn[column.Key] = new HashSet<string>( column.Value, StringComparer.Ordinal );
         }
      }

      _sharedById.Remove( tableId );
      _sharedByName.Remove( tableName );
      if( byColumn.Count > 0 )
      {
         _sharedById[tableId] = byColumn;
         _sharedByName[tableName] = byColumn;
      }
   }

   /// <summary>
   /// Maps one record to a document.
   /// </summary>
   /// <param name="record">The source record.</param>
   /// <returns>The document, or null when the row has no text at all.</returns>
   public Document? Map( SourceRecord record )
   {
      TableMapping? mapping = FindMapping( record );
      string text = BuildText( record, mapping?.Template );
      if( string.IsNullOrWhiteSpace( text ) )
      {
         return null;
      }

      string? keyValue = KeyValue( record, mapping?.KeyColumn );
      string baseKey = keyValue != null
         ? $"{record.KeyScope}|{QualifySharedValue( record, mapping!.KeyColumn!, keyValue )}"
         : $"{record.KeyScope}|h:{Hash( record.Origin + '\u001E' + string.Join( '\u001F', record.Values ) )[..CONTENT_KEY_HEX]}";
      string docKey = Deduplicate( CapLength( baseKey, record.KeyScope ) );
      var metadata = new Dictionary<string, string>
      {
         ["row"] = record.RowNumber.ToString( System.Globalization.CultureInfo.InvariantCulture ),
      };

      if( keyValue != null )
      {
         metadata["key"] = keyValue;
      }

      return new Document( docKey, record.Table, record.Origin, text, Hash( text ), metadata );
   }

   /// <summary>
   /// SHA-256 of a string as lowercase hex. Used for content hashes and content keys.
   /// </summary>
   /// <param name="value">Text to hash.</param>
   /// <returns>64 hex characters.</returns>
   public static string Hash( string value )
   {
      return Convert.ToHexStringLower( SHA256.HashData( Encoding.UTF8.GetBytes( value ) ) );
   }

   #endregion Public Methods

   #region Private Methods

   /// <summary>
   /// Finds the mapping for a record: by stable table id first, then by table name, so settings
   /// keyed either way apply. Remembers which entries matched.
   /// </summary>
   /// <param name="record">The record.</param>
   /// <returns>The mapping, or null for the defaults.</returns>
   private TableMapping? FindMapping( SourceRecord record )
   {
      foreach( string candidate in record.TableId != null ? new[] { record.TableId, record.Table } : new[] { record.Table } )
      {
         if( _mappings.TryGetValue( candidate, out TableMapping? mapping ) )
         {
            _matchedMappings.Add( candidate );
            return mapping;
         }
      }

      return null;
   }

   /// <summary>
   /// Builds the embedded text from a template, or as "Column: value" lines by default.
   /// The template is filled in ONE pass, so a cell value that itself contains "{OtherColumn}"
   /// is embedded literally instead of being expanded.
   /// </summary>
   /// <param name="record">The record.</param>
   /// <param name="template">Optional template with {Column} placeholders.</param>
   /// <returns>The text.</returns>
   private static string BuildText( SourceRecord record, string? template )
   {
      if( !string.IsNullOrWhiteSpace( template ) )
      {
         string filled = PLACEHOLDER.Replace( template, match =>
         {
            int index = IndexOf( record.Columns, match.Groups[1].Value, StringComparison.Ordinal );
            return index >= 0 ? record.Values[index] ?? string.Empty : match.Value;
         } );

         return filled.Trim();
      }

      var lines = new StringBuilder();
      for( int i = 0; i < record.Columns.Count; i++ )
      {
         if( !string.IsNullOrWhiteSpace( record.Values[i] ) )
         {
            lines.Append( record.Columns[i] ).Append( ": " ).Append( record.Values[i] ).Append( '\n' );
         }
      }

      return lines.ToString().TrimEnd();
   }

   /// <summary>
   /// Reads the key column's value, counting rows where it is empty or the column is missing.
   /// </summary>
   /// <param name="record">The record.</param>
   /// <param name="keyColumn">Key column name, or null.</param>
   /// <returns>The key value, or null when there is no key column or it is empty.</returns>
   private string? KeyValue( SourceRecord record, string? keyColumn )
   {
      if( keyColumn == null )
      {
         return null;
      }

      int index = IndexOf( record.Columns, keyColumn, StringComparison.OrdinalIgnoreCase );
      string? value = index >= 0 ? record.Values[index] : null;
      if( value == null )
      {
         MissingKeyRows++;
      }

      return value;
   }

   /// <summary>
   /// Appends "@" and a short hash of the record's origin to a key value that the scan found in
   /// more than one origin of this table, so each origin's copy has its own key. Values not in
   /// that set are returned unchanged, which keeps their document keys identical to before.
   /// </summary>
   /// <param name="record">The record.</param>
   /// <param name="keyColumn">The key column chosen for the table.</param>
   /// <param name="keyValue">The record's value in that column.</param>
   /// <returns>The value, qualified by origin when it is shared.</returns>
   private string QualifySharedValue( SourceRecord record, string keyColumn, string keyValue )
   {
      Dictionary<string, HashSet<string>>? byColumn = null;
      bool known = ( record.TableId != null && _sharedById.TryGetValue( record.TableId, out byColumn ) )
         || _sharedByName.TryGetValue( record.Table, out byColumn );
      if( !known || !byColumn!.TryGetValue( keyColumn, out HashSet<string>? shared ) || !shared.Contains( keyValue ) )
      {
         return keyValue;
      }

      return $"{keyValue}@{Hash( record.Origin )[..ORIGIN_KEY_HEX]}";
   }

   /// <summary>
   /// Position of a column name in the record's columns.
   /// </summary>
   /// <param name="columns">Column names.</param>
   /// <param name="name">Name to find.</param>
   /// <param name="comparison">How names are compared.</param>
   /// <returns>The index, or -1.</returns>
   private static int IndexOf( IReadOnlyList<string> columns, string name, StringComparison comparison )
   {
      for( int i = 0; i < columns.Count; i++ )
      {
         if( string.Equals( columns[i], name, comparison ) )
         {
            return i;
         }
      }

      return -1;
   }

   /// <summary>
   /// Replaces a key longer than <see cref="MAX_KEY_CHARS"/> with the table scope plus a SHA-256 of
   /// the whole key. Deterministic, so the same long value gets the same key every run.
   /// Why: the SQL sink's DocKey column holds 450 characters, and one longer key used to fail
   /// every batch that contained it.
   /// </summary>
   /// <param name="key">Candidate key.</param>
   /// <param name="scope">The record's key scope (table id or name).</param>
   /// <returns>The key, at most about 135 characters when it had to be hashed.</returns>
   private static string CapLength( string key, string scope )
   {
      if( key.Length <= MAX_KEY_CHARS )
      {
         return key;
      }

      string prefix = scope.Length <= MAX_SCOPE_CHARS_IN_HASHED_KEY ? scope : scope[..MAX_SCOPE_CHARS_IN_HASHED_KEY];
      return $"{prefix}|k:{Hash( key )}";
   }

   /// <summary>
   /// Makes a key unique within the run by suffixing repeats with #2, #3. Every key handed out,
   /// generated or not, is reserved, so a generated "5#2" and a real key "5#2" can never both
   /// be issued.
   /// </summary>
   /// <param name="key">Candidate key.</param>
   /// <returns>A key not used before in this run.</returns>
   private string Deduplicate( string key )
   {
      if( _usedKeys.Add( key ) )
      {
         return key;
      }

      int n = _nextSuffix.TryGetValue( key, out int next ) ? next : 2;
      string candidate = $"{key}#{n}";
      while( !_usedKeys.Add( candidate ) )
      {
         n++;
         candidate = $"{key}#{n}";
      }

      _nextSuffix[key] = n + 1;
      return candidate;
   }

   #endregion Private Methods
}
