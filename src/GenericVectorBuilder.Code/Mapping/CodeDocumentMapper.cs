using GenericVectorBuilder.Code.Sources;
using GenericVectorBuilder.Core.Contracts;
using GenericVectorBuilder.Core.Mapping;

namespace GenericVectorBuilder.Code.Mapping;

/// <summary>
/// Turns a code file record into a document: the file's content verbatim as the text, its path
/// as the key, and path, language and commit as metadata.
/// Why the content is not reformatted: the chunker needs the exact source to parse it, and the
/// content hash must change only when the file does.
/// Why the key is the path: a file keeps its identity while its content changes, so an edited
/// file is updated in place and a deleted file is removed by key.
/// Why the commit is metadata and not part of the hash: a new commit that leaves a file
/// untouched must not re-embed it. The stored commit is therefore the commit the file's current
/// text was first indexed from, which still points at a version containing exactly that text.
/// </summary>
public sealed class CodeDocumentMapper : IDocumentMapper
{
   #region Data Members

   /// <summary>Prefix of every code document key.</summary>
   public const string KEY_PREFIX = "code|";

   private readonly string? _commit;

   #endregion Data Members

   #region Constructor

   /// <summary>
   /// Creates the mapper for one run.
   /// </summary>
   /// <param name="commit">Commit id the files were read at, or null when the folder is not a git copy.</param>
   public CodeDocumentMapper( string? commit = null )
   {
      _commit = string.IsNullOrWhiteSpace( commit ) ? null : commit.Trim();
   }

   #endregion Constructor

   #region Public Methods

   /// <summary>
   /// Maps one file record.
   /// </summary>
   /// <param name="record">A record from <see cref="CodeFolderSource"/>.</param>
   /// <returns>The document, or null for an empty or whitespace-only file.</returns>
   public Document? Map( SourceRecord record )
   {
      string content = Column( record, CodeFolderSource.CONTENT_COLUMN ) ?? string.Empty;
      if( string.IsNullOrWhiteSpace( content ) )
      {
         return null;
      }

      string path = Column( record, CodeFolderSource.PATH_COLUMN ) ?? record.Origin;
      string language = Column( record, CodeFolderSource.LANGUAGE_COLUMN ) ?? CodeLanguages.FromPath( path );
      var metadata = new Dictionary<string, string>
      {
         ["path"] = path,
         ["language"] = language,
      };

      if( _commit != null )
      {
         metadata["commit"] = _commit;
      }

      return new Document( DocKey( path ), record.Table, record.Origin, content, RowDocumentMapper.Hash( content ), metadata );
   }

   /// <summary>
   /// The document key of a file: "code|" plus its path. A path too long for every sink's key
   /// column is replaced by a SHA-256 of the path, so the key stays unique and fits.
   /// </summary>
   /// <param name="path">File path relative to the root.</param>
   /// <returns>The key, at most <see cref="RowDocumentMapper.MAX_KEY_CHARS"/> characters.</returns>
   public static string DocKey( string path )
   {
      string key = KEY_PREFIX + path;
      return key.Length <= RowDocumentMapper.MAX_KEY_CHARS ? key : $"{KEY_PREFIX}k:{RowDocumentMapper.Hash( path )}";
   }

   #endregion Public Methods

   #region Private Methods

   /// <summary>
   /// A column's value by name, ignoring case.
   /// </summary>
   /// <param name="record">The record.</param>
   /// <param name="name">Column name.</param>
   /// <returns>The value, or null when the column is missing or empty.</returns>
   private static string? Column( SourceRecord record, string name )
   {
      for( int i = 0; i < record.Columns.Count && i < record.Values.Count; i++ )
      {
         if( string.Equals( record.Columns[i], name, StringComparison.OrdinalIgnoreCase ) )
         {
            return record.Values[i];
         }
      }

      return null;
   }

   #endregion Private Methods
}
