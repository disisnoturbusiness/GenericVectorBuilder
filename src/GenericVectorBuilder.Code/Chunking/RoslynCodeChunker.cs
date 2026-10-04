using GenericVectorBuilder.Code.Sources;
using GenericVectorBuilder.Core.Chunking;
using GenericVectorBuilder.Core.Contracts;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Text;

namespace GenericVectorBuilder.Code.Chunking;

/// <summary>
/// Splits C# files by their structure, and every other file the way table text is split.
/// A C# file small enough for one chunk stays whole. A larger file is split into its types:
/// a type that fits is one chunk; a type that does not becomes its declaration (grouped with
/// its fields and properties) and one chunk per method, constructor or operator, with nested
/// types treated the same way and top-level statements grouped. Every chunk starts with a
/// one-line header naming the file, namespace, type and member, so a chunk read alone says
/// where it is. A member longer than a chunk is split on line boundaries with the header
/// repeated and "(part i/n)" added.
/// Why nothing can be lost: the file is first divided into runs of whole lines that together
/// cover every line exactly once, and each run goes into chunks whole or in parts. Removing the
/// header from each chunk and joining the rest gives back the file (line breaks become "\n";
/// only a single line longer than a chunk gains a break where it was cut).
/// Why ids match the text chunker's: the same pipeline, key and text always give the same id,
/// so unchanged chunks keep their ids and re-runs overwrite instead of duplicating.
/// </summary>
public sealed class RoslynCodeChunker : IChunker
{
   #region Data Members

   private static readonly CSharpParseOptions PARSE_OPTIONS = CSharpParseOptions.Default
      .WithLanguageVersion( LanguageVersion.Preview )
      .WithDocumentationMode( DocumentationMode.Parse );

   private readonly TextChunker _fallback = new();

   #endregion Data Members

   #region Public Methods

   /// <inheritdoc />
   public IReadOnlyList<Chunk> Split( string pipeline, Document document )
   {
      string path = document.Metadata.TryGetValue( "path", out string? stored ) && stored.Length > 0 ? stored : document.Origin;
      if( !IsCSharp( document, path ) || string.IsNullOrWhiteSpace( document.Text ) )
      {
         return _fallback.Split( pipeline, document );
      }

      List<string> texts = SplitCSharp( path, document.Text );
      var chunks = new List<Chunk>( texts.Count );
      var seen = new Dictionary<string, int>( StringComparer.Ordinal );
      for( int i = 0; i < texts.Count; i++ )
      {
         // Identical texts inside one file get distinct ids via an occurrence counter.
         int occurrence = seen.TryGetValue( texts[i], out int n ) ? n + 1 : 1;
         seen[texts[i]] = occurrence;
         chunks.Add( new Chunk( TextChunker.ChunkId( pipeline, document.DocKey, texts[i], occurrence ), document.DocKey, i, texts[i] ) );
      }

      return chunks;
   }

   /// <summary>
   /// Splits C# source into chunk texts. Exposed for tests and reports that want the texts
   /// without building documents.
   /// </summary>
   /// <param name="path">File path shown in each header.</param>
   /// <param name="source">The file content.</param>
   /// <returns>Chunk texts in file order.</returns>
   public static List<string> SplitCSharp( string path, string source )
   {
      SourceText text = SourceText.From( source );
      var root = (CompilationUnitSyntax)CSharpSyntaxTree.ParseText( text, PARSE_OPTIONS ).GetRoot();
      var code = new CodeText( path, text );
      var whole = new CodePiece( 0, code.LineCount - 1, FileNamespaces( root ), FileTypes( root ), string.Empty, PieceKind.Whole );
      if( code.Fits( whole ) )
      {
         return new List<string> { code.Header( whole ) + "\n" + code.Body( 0, code.LineCount - 1 ) };
      }

      List<CodePiece> pieces = PieceLayout.Arrange( CSharpPieceCollector.Collect( root, text, code ), code );
      return ChunkPacker.Pack( pieces, code );
   }

   #endregion Public Methods

   #region Private Methods

   /// <summary>
   /// True for a C# document: language "csharp" in its metadata, or a .cs or .csx path.
   /// </summary>
   /// <param name="document">The document.</param>
   /// <param name="path">Its path.</param>
   /// <returns>True when the Roslyn split applies.</returns>
   private static bool IsCSharp( Document document, string path )
   {
      if( document.Metadata.TryGetValue( "language", out string? language ) )
      {
         return string.Equals( language, CodeLanguages.CSHARP, StringComparison.OrdinalIgnoreCase );
      }

      return CodeLanguages.FromPath( path ) == CodeLanguages.CSHARP;
   }

   /// <summary>
   /// The types declared directly in the file or its namespaces (not nested types), for the
   /// header of a whole-file chunk.
   /// </summary>
   /// <param name="root">The compilation unit.</param>
   /// <returns>Type names joined by ", ".</returns>
   private static string FileTypes( CompilationUnitSyntax root )
   {
      IEnumerable<BaseTypeDeclarationSyntax> types = root
         .DescendantNodes( n => n is CompilationUnitSyntax or BaseNamespaceDeclarationSyntax )
         .OfType<BaseTypeDeclarationSyntax>();
      return ChunkHeader.JoinLabels( types.Select( CSharpNames.TypeName ) );
   }

   /// <summary>
   /// The namespaces declared in the file, for the header of a whole-file chunk.
   /// </summary>
   /// <param name="root">The compilation unit.</param>
   /// <returns>Namespaces joined by ", ".</returns>
   private static string FileNamespaces( CompilationUnitSyntax root )
   {
      IEnumerable<string> names = root
         .DescendantNodes( n => n is CompilationUnitSyntax or BaseNamespaceDeclarationSyntax )
         .OfType<BaseNamespaceDeclarationSyntax>()
         .Select( CSharpNames.Namespace );
      return ChunkHeader.JoinLabels( names );
   }

   #endregion Private Methods
}
