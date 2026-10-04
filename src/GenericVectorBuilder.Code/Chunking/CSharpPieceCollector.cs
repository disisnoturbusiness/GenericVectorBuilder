using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Text;

namespace GenericVectorBuilder.Code.Chunking;

/// <summary>
/// Walks a C# syntax tree and lists its pieces in file order: a type small enough for one chunk
/// becomes one piece; a larger type becomes its declaration plus one piece per member, with
/// nested types handled the same way; top-level statements become statement pieces.
/// Each piece starts at the first comment, doc comment or attribute in front of its node, so
/// a method's documentation travels with the method. Lines that belong to no node (usings,
/// braces, blank lines) are not assigned here; <see cref="PieceLayout"/> does that.
/// </summary>
internal sealed class CSharpPieceCollector
{
   #region Data Members

   private readonly CodeText _code;
   private readonly SourceText _text;
   private readonly List<CodePiece> _pieces = new();

   #endregion Data Members

   #region Constructor

   /// <summary>
   /// Creates a collector for one file.
   /// </summary>
   /// <param name="code">The file's lines.</param>
   /// <param name="text">The parsed source text, for line numbers.</param>
   private CSharpPieceCollector( CodeText code, SourceText text )
   {
      _code = code;
      _text = text;
   }

   #endregion Constructor

   #region Public Methods

   /// <summary>
   /// Lists the pieces of a file, in file order. Pieces may share a line when two nodes do;
   /// <see cref="PieceLayout"/> resolves that.
   /// </summary>
   /// <param name="root">The compilation unit.</param>
   /// <param name="text">The parsed source text.</param>
   /// <param name="code">The file's lines.</param>
   /// <returns>The raw pieces.</returns>
   public static List<CodePiece> Collect( CompilationUnitSyntax root, SourceText text, CodeText code )
   {
      var collector = new CSharpPieceCollector( code, text );
      collector.AddContainerMembers( root.Members );
      return collector._pieces;
   }

   #endregion Public Methods

   #region Private Methods

   /// <summary>
   /// Adds the members of the compilation unit or of a namespace, descending into namespaces.
   /// </summary>
   /// <param name="members">The members.</param>
   private void AddContainerMembers( SyntaxList<MemberDeclarationSyntax> members )
   {
      foreach( MemberDeclarationSyntax member in members )
      {
         if( member is BaseNamespaceDeclarationSyntax ns )
         {
            AddContainerMembers( ns.Members );
         }
         else
         {
            AddMember( member );
         }
      }
   }

   /// <summary>
   /// Adds one member: a type is added whole or expanded, anything else becomes one piece.
   /// </summary>
   /// <param name="member">The member.</param>
   private void AddMember( MemberDeclarationSyntax member )
   {
      if( member is BaseTypeDeclarationSyntax type )
      {
         AddType( type );
      }
      else
      {
         Add( member, CSharpNames.KindOf( member ), CSharpNames.Label( member ) );
      }
   }

   /// <summary>
   /// Adds a type as one piece when it fits in a chunk (or has no body to split), otherwise as
   /// its declaration (through the opening brace) followed by each of its members.
   /// </summary>
   /// <param name="type">The type.</param>
   private void AddType( BaseTypeDeclarationSyntax type )
   {
      var whole = new CodePiece( StartLine( type ), EndLine( type ), CSharpNames.Namespace( type ), CSharpNames.TypePath( type ), string.Empty, PieceKind.Whole );
      SyntaxToken brace = type.OpenBraceToken;
      if( _code.Fits( whole ) || !brace.IsKind( SyntaxKind.OpenBraceToken ) || brace.IsMissing )
      {
         _pieces.Add( whole );
         return;
      }

      _pieces.Add( whole with { EndLine = Line( brace.SpanStart ), Label = "declaration", Kind = PieceKind.Declarations } );
      IEnumerable<MemberDeclarationSyntax> members = type switch
      {
         TypeDeclarationSyntax declaration => declaration.Members,
         EnumDeclarationSyntax enumeration => enumeration.Members,
         _ => Array.Empty<MemberDeclarationSyntax>(),
      };

      foreach( MemberDeclarationSyntax member in members )
      {
         AddMember( member );
      }
   }

   /// <summary>
   /// Adds one node as a piece.
   /// </summary>
   /// <param name="node">The node.</param>
   /// <param name="kind">How it may be packed.</param>
   /// <param name="label">Its label.</param>
   private void Add( SyntaxNode node, PieceKind kind, string label )
   {
      _pieces.Add( new CodePiece( StartLine( node ), EndLine( node ), CSharpNames.Namespace( node ), CSharpNames.TypePath( node ), label, kind ) );
   }

   /// <summary>
   /// The line a node starts on, counting its leading comments, doc comments, directives and
   /// attributes, but not the blank lines in front of them.
   /// </summary>
   /// <param name="node">The node.</param>
   /// <returns>0-based line.</returns>
   private int StartLine( SyntaxNode node )
   {
      foreach( SyntaxTrivia trivia in node.GetLeadingTrivia() )
      {
         if( !trivia.IsKind( SyntaxKind.WhitespaceTrivia ) && !trivia.IsKind( SyntaxKind.EndOfLineTrivia ) )
         {
            return Line( trivia.SpanStart );
         }
      }

      return Line( node.SpanStart );
   }

   /// <summary>
   /// The line a node's last character is on (trailing comments on that line come along).
   /// </summary>
   /// <param name="node">The node.</param>
   /// <returns>0-based line.</returns>
   private int EndLine( SyntaxNode node )
   {
      return Line( Math.Max( node.SpanStart, node.Span.End - 1 ) );
   }

   /// <summary>
   /// The line of a position.
   /// </summary>
   /// <param name="position">Character position.</param>
   /// <returns>0-based line.</returns>
   private int Line( int position )
   {
      return _text.Lines.GetLineFromPosition( position ).LineNumber;
   }

   #endregion Private Methods
}
