namespace GenericVectorBuilder.Code.Chunking;

/// <summary>
/// How a piece of a source file may be combined with its neighbours when chunks are packed.
/// </summary>
internal enum PieceKind
{
   /// <summary>A whole type, or a whole file, small enough to be one chunk. Never combined.</summary>
   Whole,

   /// <summary>A member with a body (method, constructor, operator). Always its own chunk.</summary>
   Member,

   /// <summary>A type's declaration line, fields, properties, events and other body-less members. Neighbours of the same type are grouped.</summary>
   Declarations,

   /// <summary>Top-level statements (a Program.cs without a Main method). Neighbours are grouped.</summary>
   Statements,
}

/// <summary>
/// A run of whole lines of a source file, with what it is and where it sits. Pieces are laid out
/// so that together they cover every line of the file exactly once, in order.
/// Why lines: a line is never shared by two pieces, so "every line belongs to exactly one piece"
/// is easy to guarantee and easy to test, and that guarantee is what stops content from being
/// dropped.
/// </summary>
/// <param name="StartLine">First line, 0-based.</param>
/// <param name="EndLine">Last line, 0-based, inclusive.</param>
/// <param name="Namespace">Enclosing namespace, or empty.</param>
/// <param name="TypePath">Enclosing types joined by '.', e.g. "Outer.Inner", or empty.</param>
/// <param name="Label">What the piece holds, e.g. "AddItemToBasket(string, int)", or empty for a whole type.</param>
/// <param name="Kind">How the piece may be combined with its neighbours.</param>
internal sealed record CodePiece( int StartLine, int EndLine, string Namespace, string TypePath, string Label, PieceKind Kind );
