using System.Text;
using GenericVectorBuilder.Code.Chunking;
using GenericVectorBuilder.Code.Mapping;
using GenericVectorBuilder.Core.Chunking;
using GenericVectorBuilder.Core.Contracts;
using GenericVectorBuilder.Core.Mapping;

namespace GenericVectorBuilder.Engines.Tests.Code;

/// <summary>
/// The C# chunker's promises: the same file always gives the same chunks and ids, every line of
/// the file lands in exactly one chunk in order, nothing is over the size cap, and every chunk
/// starts with a header saying where it is.
/// </summary>
public class RoslynCodeChunkerTests
{
   #region Data Members

   private const string PIPELINE = "codetest";
   private const string PATH = "src/Shop/BasketService.cs";

   #endregion Data Members

   #region Public Methods

   /// <summary>
   /// Two chunkers given the same file produce identical texts, ids and ordinals.
   /// </summary>
   [Fact]
   public void Split_IsDeterministic()
   {
      Document document = Doc( PATH, BigFile() );

      IReadOnlyList<Chunk> first = new RoslynCodeChunker().Split( PIPELINE, document );
      IReadOnlyList<Chunk> second = new RoslynCodeChunker().Split( PIPELINE, document );

      Assert.True( first.Count > 5 );
      Assert.Equal( first.Select( c => (c.ChunkId, c.Ordinal, c.Text) ), second.Select( c => (c.ChunkId, c.Ordinal, c.Text) ) );
      Assert.Equal( first.Count, first.Select( c => c.ChunkId ).Distinct().Count() );
   }

   /// <summary>
   /// Removing each chunk's header and joining the rest gives back the whole file, so every
   /// member's text (and every brace, using and comment) is in some chunk.
   /// </summary>
   [Fact]
   public void Split_CoversEveryLineExactlyOnce()
   {
      string source = BigFile();

      List<string> texts = RoslynCodeChunker.SplitCSharp( PATH, source );

      Assert.Equal( source, Reconstruct( texts ) );
      foreach( string member in new[] { "public void AddItem( int catalogItemId, decimal price, int quantity )", "public int Count( string sku )",
         "public sealed class Line", "public enum BasketState", "OrderTotal", "#region Private Methods", "using System.Text;" } )
      {
         Assert.Contains( texts, t => t.Contains( member, StringComparison.Ordinal ) );
      }
   }

   /// <summary>
   /// A file is split by member: each method has its own chunk whose header names the file,
   /// namespace, type and member, and overloads are told apart by their parameter types.
   /// </summary>
   [Fact]
   public void Split_HeadersNameFileNamespaceTypeAndMember()
   {
      List<string> texts = RoslynCodeChunker.SplitCSharp( PATH, BigFile() );

      Assert.All( texts, t => Assert.StartsWith( $"// File: {PATH}", t, StringComparison.Ordinal ) );
      Assert.All( texts, t => Assert.DoesNotContain( '\n', t[..t.IndexOf( '\n' )] ) );
      Assert.Contains( texts, t => t.StartsWith( $"// File: {PATH} | Namespace: Shop.Baskets | Type: BasketService | Member: AddItem(int, decimal, int)\n", StringComparison.Ordinal ) );
      Assert.Contains( texts, t => t.Contains( "| Member: Count(string)\n", StringComparison.Ordinal ) );
      Assert.Contains( texts, t => t.Contains( "| Member: Count(string, int)\n", StringComparison.Ordinal ) );
      Assert.Contains( texts, t => t.Contains( "| Type: BasketService.Line\n", StringComparison.Ordinal ) );
   }

   /// <summary>
   /// A method longer than a chunk is split on line boundaries into parts under the cap, each
   /// repeating the header with "(part i/n)", and nothing is lost.
   /// </summary>
   [Fact]
   public void Split_LongMember_IsSplitUnderTheCapWithHeaderRepeated()
   {
      var source = new StringBuilder( "namespace Big;\n\npublic class Calculator\n{\n   public int Huge()\n   {\n      int total = 0;\n" );
      for( int i = 0; i < 400; i++ )
      {
         source.Append( $"      total += Compute( {i}, \"value number {i}\" );\n" );
      }

      source.Append( "      return total;\n   }\n\n   private int Compute( int a, string b ) => a + b.Length;\n}\n" );

      List<string> texts = RoslynCodeChunker.SplitCSharp( "Calc.cs", source.ToString() );
      List<string> parts = texts.Where( t => t.Contains( "Member: Huge()", StringComparison.Ordinal ) ).ToList();

      Assert.True( parts.Count >= 5, $"expected the 16k method in 5+ parts, got {parts.Count}" );
      Assert.All( texts, t => Assert.True( t.Length <= TextChunker.MAX_CHUNK_CHARS, $"chunk of {t.Length} chars" ) );
      Assert.All( parts, ( t, i ) => Assert.Contains( $"(part {i + 1}/{parts.Count})\n", t, StringComparison.Ordinal ) );
      Assert.Equal( source.ToString(), Reconstruct( texts ) );
   }

   /// <summary>
   /// The case that broke the old chunker: a class whose fields and properties alone run to
   /// about 23,000 characters. Every declaration is kept, grouped into chunks under the cap.
   /// </summary>
   [Fact]
   public void Split_HugeClassShell_KeepsEveryDeclaration()
   {
      var source = new StringBuilder( "namespace Dto;\n\n/// <summary>Wide record.</summary>\npublic class WideRecord\n{\n" );
      for( int i = 0; i < 300; i++ )
      {
         source.Append( $"   /// <summary>Field number {i}.</summary>\n   public string Property{i:000} {{ get; set; }}\n" );
      }

      source.Append( "}\n" );

      List<string> texts = RoslynCodeChunker.SplitCSharp( "WideRecord.cs", source.ToString() );

      Assert.True( source.Length > 20000 );
      Assert.All( texts, t => Assert.True( t.Length <= TextChunker.MAX_CHUNK_CHARS ) );
      Assert.All( Enumerable.Range( 0, 300 ), i => Assert.Contains( texts, t => t.Contains( $"public string Property{i:000} {{ get; set; }}", StringComparison.Ordinal ) ) );
      Assert.Equal( source.ToString(), Reconstruct( texts ) );
      Assert.True( texts.Count < 30, $"properties should be grouped, got {texts.Count} chunks" );
   }

   /// <summary>
   /// A single line longer than a chunk (a giant string literal) is cut into pieces, not cut off:
   /// every character survives, in order.
   /// </summary>
   [Fact]
   public void Split_GiantSingleLine_LosesNoCharacters()
   {
      string literal = string.Concat( Enumerable.Range( 0, 900 ).Select( i => $"w{i} " ) );
      string source = $"namespace Data;\n\npublic static class Blob\n{{\n   public const string Text = \"{literal}\";\n\n   public static int Size() => Text.Length;\n}}\n";

      List<string> texts = RoslynCodeChunker.SplitCSharp( "Blob.cs", source );

      Assert.All( texts, t => Assert.True( t.Length <= TextChunker.MAX_CHUNK_CHARS ) );
      Assert.Equal( source.Replace( "\n", string.Empty ), Reconstruct( texts ).Replace( "\n", string.Empty ) );
   }

   /// <summary>
   /// A small file is one chunk: the header plus the whole file.
   /// </summary>
   [Fact]
   public void Split_SmallFile_IsOneWholeChunk()
   {
      const string source = "using System;\n\nnamespace Tiny;\n\npublic record Price( decimal Amount, string Currency );\n";

      List<string> texts = RoslynCodeChunker.SplitCSharp( "Price.cs", source );

      Assert.Equal( new[] { "// File: Price.cs | Namespace: Tiny | Type: Price\n" + source }, texts );
   }

   /// <summary>
   /// Top-level statements are grouped into chunks labelled as such, and nothing is lost.
   /// </summary>
   [Fact]
   public void Split_TopLevelStatements_AreGroupedAndCovered()
   {
      var source = new StringBuilder( "using System;\n\nvar builder = new Builder();\n" );
      for( int i = 0; i < 120; i++ )
      {
         source.Append( $"builder.Services.AddScoped<IService{i}, Service{i}>();\n" );
      }

      source.Append( "builder.Build().Run();\n\nstatic int Helper( int x ) => x * 2;\n" );

      List<string> texts = RoslynCodeChunker.SplitCSharp( "Program.cs", source.ToString() );

      Assert.True( texts.Count >= 3 );
      Assert.All( texts, t => Assert.Contains( "Member: top-level statements", t, StringComparison.Ordinal ) );
      Assert.Equal( source.ToString(), Reconstruct( texts ) );
   }

   /// <summary>
   /// Windows line endings give exactly the same chunks and ids as Unix ones, so the same commit
   /// checked out on either system never re-embeds.
   /// </summary>
   [Fact]
   public void Split_CrLfAndLf_GiveTheSameChunks()
   {
      string lf = BigFile();
      string crlf = lf.Replace( "\n", "\r\n" );

      IReadOnlyList<Chunk> a = new RoslynCodeChunker().Split( PIPELINE, Doc( PATH, lf ) with { DocKey = "k" } );
      IReadOnlyList<Chunk> b = new RoslynCodeChunker().Split( PIPELINE, Doc( PATH, crlf ) with { DocKey = "k" } );

      Assert.Equal( a.Select( c => c.ChunkId ), b.Select( c => c.ChunkId ) );
   }

   /// <summary>
   /// Code that does not compile (missing braces, junk tokens) still loses nothing.
   /// </summary>
   [Fact]
   public void Split_BrokenCode_StillCoversEveryLine()
   {
      string source = BigFile().Replace( "public int Count( string sku )\n   {", "public int Count( string sku )\n   {{ ]] @@ " )
         + "\n#if NEVER\npublic class Ghost { }\n#endif\nclass Unclosed {\n   void M( {\n";

      List<string> texts = RoslynCodeChunker.SplitCSharp( PATH, source );

      Assert.Equal( source, Reconstruct( texts ) );
      Assert.All( texts, t => Assert.True( t.Length <= TextChunker.MAX_CHUNK_CHARS ) );
   }

   /// <summary>
   /// Anything that is not C# goes through the text chunker unchanged.
   /// </summary>
   [Fact]
   public void Split_NonCSharp_FallsBackToTheTextChunker()
   {
      Document document = Doc( "docs/readme.md", string.Join( "\n", Enumerable.Range( 0, 300 ).Select( i => $"Line {i} of the readme." ) ) ) with
      {
         Metadata = new Dictionary<string, string> { ["path"] = "docs/readme.md", ["language"] = "markdown" },
      };

      IReadOnlyList<Chunk> code = new RoslynCodeChunker().Split( PIPELINE, document );
      IReadOnlyList<Chunk> text = new TextChunker().Split( PIPELINE, document );

      Assert.Equal( text.Select( c => (c.ChunkId, c.Text) ), code.Select( c => (c.ChunkId, c.Text) ) );
   }

   /// <summary>
   /// Joins chunk bodies (each chunk minus its header line) with "\n".
   /// </summary>
   /// <param name="texts">Chunk texts in order.</param>
   /// <returns>The reassembled file.</returns>
   public static string Reconstruct( IEnumerable<string> texts )
   {
      return string.Join( "\n", texts.Select( t => t[( t.IndexOf( '\n' ) + 1 )..] ) );
   }

   #endregion Public Methods

   #region Private Methods

   /// <summary>
   /// A document the way the code mapper builds it.
   /// </summary>
   /// <param name="path">File path.</param>
   /// <param name="text">File content.</param>
   /// <returns>The document.</returns>
   private static Document Doc( string path, string text )
   {
      var metadata = new Dictionary<string, string> { ["path"] = path, ["language"] = "csharp" };
      return new Document( CodeDocumentMapper.DocKey( path ), "repo", path, text, RowDocumentMapper.Hash( text ), metadata );
   }

   /// <summary>
   /// A C# file of about 6,000 characters with most kinds of members: usings, a file-scoped
   /// namespace, doc comments, regions, fields, properties, constructors, overloads, an
   /// expression-bodied member, a nested class, an enum, a record and an interface.
   /// </summary>
   /// <returns>The source, with "\n" line breaks.</returns>
   private static string BigFile()
   {
      var s = new StringBuilder();
      s.Append( "// Licensed under the MIT license.\nusing System;\nusing System.Text;\n\nnamespace Shop.Baskets;\n\n" );
      s.Append( "/// <summary>\n/// Holds a shopper's basket.\n/// </summary>\npublic class BasketService : IBasketService\n{\n" );
      s.Append( "   #region Data Members\n\n   private readonly List<Line> _lines = new();\n   private readonly string _owner;\n\n   #endregion Data Members\n\n" );
      s.Append( "   /// <summary>The basket owner.</summary>\n   public string Owner => _owner;\n\n   public BasketState State { get; private set; }\n\n" );
      s.Append( "   /// <summary>\n   /// Creates the service.\n   /// </summary>\n   public BasketService( string owner )\n   {\n      _owner = owner;\n   }\n\n" );
      s.Append( "   #region Public Methods\n\n" );
      s.Append( Method( "public void AddItem( int catalogItemId, decimal price, int quantity )", "Adds an item to the basket, merging with an existing line.", 12 ) );
      s.Append( Method( "public int Count( string sku )", "Counts the units of one stock-keeping unit.", 10 ) );
      s.Append( Method( "public int Count( string sku, int warehouse )", "Counts units of one sku in one warehouse.", 10 ) );
      s.Append( "   public decimal OrderTotal() => _lines.Sum( l => l.Price * l.Quantity );\n\n   #endregion Public Methods\n\n" );
      s.Append( "   #region Private Methods\n\n" );
      s.Append( Method( "private void Recalculate( bool force )", "Recomputes totals after a change.", 14 ) );
      s.Append( "   #endregion Private Methods\n\n" );
      s.Append( "   /// <summary>One basket line.</summary>\n   public sealed class Line\n   {\n      public int ItemId { get; set; }\n" );
      s.Append( "      public decimal Price { get; set; }\n      public int Quantity { get; set; }\n   }\n}\n\n" );
      s.Append( "public enum BasketState\n{\n   Open,\n   CheckedOut,\n   Abandoned,\n}\n\n" );
      s.Append( "public record BasketSummary( string Owner, decimal Total );\n\n" );
      s.Append( "public interface IBasketService\n{\n   void AddItem( int catalogItemId, decimal price, int quantity );\n\n   decimal OrderTotal();\n}\n" );
      return s.ToString();
   }

   /// <summary>
   /// A documented method whose body has a given number of statements.
   /// </summary>
   /// <param name="signature">The method signature.</param>
   /// <param name="summary">Its doc comment text.</param>
   /// <param name="statements">Body statements.</param>
   /// <returns>The method text, indented for a class body, followed by a blank line.</returns>
   private static string Method( string signature, string summary, int statements )
   {
      var s = new StringBuilder( $"   /// <summary>\n   /// {summary}\n   /// </summary>\n   {signature}\n   {{\n" );
      for( int i = 0; i < statements; i++ )
      {
         s.Append( $"      var step{i} = _lines.Where( l => l.ItemId > {i} ).Select( l => l.Quantity * {i + 1} ).ToList();\n" );
      }

      return s.Append( "   }\n\n" ).ToString();
   }

   #endregion Private Methods
}
