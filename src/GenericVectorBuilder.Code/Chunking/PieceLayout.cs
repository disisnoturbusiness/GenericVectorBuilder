namespace GenericVectorBuilder.Code.Chunking;

/// <summary>
/// Turns the raw pieces of a file into pieces that cover every line exactly once, in order.
/// Why: this is the step that makes "no content is ever dropped" true by construction. Every
/// line no node claimed (usings, a namespace line, closing braces, blank lines, an #endregion)
/// is handed to a neighbouring piece, and a line two nodes share goes to the first of them.
/// The rules for unclaimed lines: everything before the first piece (the file header and
/// usings) goes to the first piece; between two pieces, closing-brace lines and the blank
/// lines after them stay with the piece above, and from the first other line on they go to
/// the piece below; everything after the last piece goes to the last piece.
/// </summary>
internal static class PieceLayout
{
   #region Data Members

   private const string FILE_HEADER_LABEL = "file header";

   #endregion Data Members

   #region Public Methods

   /// <summary>
   /// Lays the pieces out over the whole file.
   /// </summary>
   /// <param name="raw">Pieces in file order, possibly overlapping on shared lines.</param>
   /// <param name="code">The file's lines.</param>
   /// <returns>Contiguous pieces from line 0 to the last line.</returns>
   public static List<CodePiece> Arrange( IReadOnlyList<CodePiece> raw, CodeText code )
   {
      List<CodePiece> pieces = RemoveOverlaps( raw );
      if( pieces.Count == 0 )
      {
         return new List<CodePiece> { new( 0, code.LineCount - 1, string.Empty, string.Empty, string.Empty, PieceKind.Whole ) };
      }

      for( int i = 1; i < pieces.Count; i++ )
      {
         int gapStart = pieces[i - 1].EndLine + 1;
         int split = SplitGap( code, gapStart, pieces[i].StartLine - 1 );
         pieces[i - 1] = pieces[i - 1] with { EndLine = split - 1 };
         pieces[i] = pieces[i] with { StartLine = split };
      }

      pieces[^1] = pieces[^1] with { EndLine = code.LineCount - 1 };
      AttachFileHeader( pieces, code );
      return pieces;
   }

   #endregion Public Methods

   #region Private Methods

   /// <summary>
   /// Makes pieces strictly increasing: a piece starting on a line the previous piece already
   /// holds starts on the next line instead, and a piece entirely inside lines already held is
   /// folded into the previous piece's label.
   /// </summary>
   /// <param name="raw">Raw pieces in file order.</param>
   /// <returns>Non-overlapping pieces.</returns>
   private static List<CodePiece> RemoveOverlaps( IReadOnlyList<CodePiece> raw )
   {
      var pieces = new List<CodePiece>( raw.Count );
      foreach( CodePiece piece in raw )
      {
         if( pieces.Count == 0 || piece.StartLine > pieces[^1].EndLine )
         {
            pieces.Add( piece );
         }
         else if( piece.EndLine > pieces[^1].EndLine )
         {
            pieces.Add( piece with { StartLine = pieces[^1].EndLine + 1 } );
         }
         else
         {
            pieces[^1] = pieces[^1] with { Label = ChunkHeader.JoinLabels( new[] { pieces[^1].Label, piece.Label } ) };
         }
      }

      return pieces;
   }

   /// <summary>
   /// Finds where an unclaimed run of lines between two pieces is divided: after the last
   /// closing-brace line and the blank lines that follow it.
   /// </summary>
   /// <param name="code">The file's lines.</param>
   /// <param name="start">First unclaimed line.</param>
   /// <param name="end">Last unclaimed line (start - 1 when there are none).</param>
   /// <returns>The first line that goes to the piece below.</returns>
   private static int SplitGap( CodeText code, int start, int end )
   {
      int split = start;
      for( int line = start; line <= end; line++ )
      {
         if( code.Line( line ).TrimStart().StartsWith( '}' ) )
         {
            split = line + 1;
         }
      }

      while( split <= end && code.IsBlank( split ) )
      {
         split++;
      }

      return split;
   }

   /// <summary>
   /// The first piece receives the file header (comments, usings, a namespace line). When that
   /// pushes a piece that fit on its own over the chunk size, the header becomes its own piece
   /// instead, so a small first type is not split in two because of its usings.
   /// </summary>
   /// <param name="pieces">Laid-out pieces; the first may be split in two.</param>
   /// <param name="code">The file's lines.</param>
   private static void AttachFileHeader( List<CodePiece> pieces, CodeText code )
   {
      CodePiece first = pieces[0];
      int header = first.StartLine;
      if( header == 0 || code.Fits( first with { StartLine = 0 } ) || !code.Fits( first ) )
      {
         pieces[0] = first with { StartLine = 0 };
         return;
      }

      pieces.Insert( 0, new CodePiece( 0, header - 1, first.Namespace, string.Empty, FILE_HEADER_LABEL, PieceKind.Whole ) );
   }

   #endregion Private Methods
}
