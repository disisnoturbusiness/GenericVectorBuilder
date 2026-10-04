using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace GenericVectorBuilder.Code.Chunking;

/// <summary>
/// Names and kinds of C# syntax nodes, as they appear in chunk headers.
/// Why parameter types in member labels: overloads share a name, and "Add(int)" versus
/// "Add(string, int)" is what tells two chunks of the same class apart in a search result.
/// </summary>
internal static class CSharpNames
{
   #region Public Methods

   /// <summary>
   /// The dotted namespace around a node, joining nested namespace blocks, or empty.
   /// </summary>
   /// <param name="node">Any node.</param>
   /// <returns>E.g. "Microsoft.eShopWeb.Web.Services".</returns>
   public static string Namespace( SyntaxNode node )
   {
      return string.Join( '.', node.AncestorsAndSelf().OfType<BaseNamespaceDeclarationSyntax>().Reverse().Select( n => n.Name.ToString() ) );
   }

   /// <summary>
   /// The types around a node, outermost first, joined by '.'. A type node includes itself.
   /// </summary>
   /// <param name="node">Any node.</param>
   /// <returns>E.g. "Basket.Item&lt;T&gt;", or empty outside any type.</returns>
   public static string TypePath( SyntaxNode node )
   {
      return string.Join( '.', node.AncestorsAndSelf().OfType<BaseTypeDeclarationSyntax>().Reverse().Select( TypeName ) );
   }

   /// <summary>
   /// A type's name with its type parameters. A block without a name (a C# 14 extension block)
   /// is named by its keyword and parameter, so a header never shows an empty type.
   /// </summary>
   /// <param name="type">The type declaration.</param>
   /// <returns>E.g. "Repository&lt;T&gt;" or "extension(string)".</returns>
   public static string TypeName( BaseTypeDeclarationSyntax type )
   {
      if( type is not TypeDeclarationSyntax declaration )
      {
         return type.Identifier.Text;
      }

      string name = declaration.Identifier.Text.Length > 0 ? declaration.Identifier.Text : declaration.Keyword.Text;
      string parameters = declaration.Identifier.Text.Length == 0 && declaration.ParameterList != null ? $"({Parameters( declaration.ParameterList )})" : string.Empty;
      return name + declaration.TypeParameterList + parameters;
   }

   /// <summary>
   /// How a member inside a type is packed: members with a body stand alone, body-less
   /// declarations (fields, properties, events, signatures) are grouped.
   /// </summary>
   /// <param name="member">The member.</param>
   /// <returns>The piece kind.</returns>
   public static PieceKind KindOf( MemberDeclarationSyntax member )
   {
      return member switch
      {
         BaseMethodDeclarationSyntax method => method.Body != null || method.ExpressionBody != null ? PieceKind.Member : PieceKind.Declarations,
         GlobalStatementSyntax => PieceKind.Statements,
         BasePropertyDeclarationSyntax or BaseFieldDeclarationSyntax or EnumMemberDeclarationSyntax or DelegateDeclarationSyntax => PieceKind.Declarations,
         _ => PieceKind.Member,
      };
   }

   /// <summary>
   /// The label of a member: its name, plus parameter types for anything callable.
   /// </summary>
   /// <param name="member">The member.</param>
   /// <returns>E.g. "AddItemToBasket(string, int, decimal, int)", "this[int]", "_items, _count".</returns>
   public static string Label( MemberDeclarationSyntax member )
   {
      return member switch
      {
         MethodDeclarationSyntax m => $"{m.Identifier.Text}{m.TypeParameterList}({Parameters( m.ParameterList )})",
         ConstructorDeclarationSyntax c => $"{c.Identifier.Text}({Parameters( c.ParameterList )})",
         DestructorDeclarationSyntax d => $"~{d.Identifier.Text}()",
         OperatorDeclarationSyntax o => $"operator {o.OperatorToken.Text}({Parameters( o.ParameterList )})",
         ConversionOperatorDeclarationSyntax c => $"{c.ImplicitOrExplicitKeyword.Text} operator {c.Type}({Parameters( c.ParameterList )})",
         PropertyDeclarationSyntax p => p.Identifier.Text,
         IndexerDeclarationSyntax i => $"this[{Parameters( i.ParameterList )}]",
         EventDeclarationSyntax e => e.Identifier.Text,
         BaseFieldDeclarationSyntax f => string.Join( ", ", f.Declaration.Variables.Select( v => v.Identifier.Text ) ),
         EnumMemberDeclarationSyntax e => e.Identifier.Text,
         DelegateDeclarationSyntax d => $"delegate {d.Identifier.Text}",
         GlobalStatementSyntax => "top-level statements",
         BaseTypeDeclarationSyntax t => TypeName( t ),
         _ => member.Kind().ToString(),
      };
   }

   #endregion Public Methods

   #region Private Methods

   /// <summary>
   /// Parameter types joined by ", " (the name for a parameter written without a type).
   /// </summary>
   /// <param name="parameters">The parameter list.</param>
   /// <returns>E.g. "string, int".</returns>
   private static string Parameters( BaseParameterListSyntax parameters )
   {
      return string.Join( ", ", parameters.Parameters.Select( p => p.Type?.ToString() ?? p.Identifier.Text ) );
   }

   #endregion Private Methods
}
