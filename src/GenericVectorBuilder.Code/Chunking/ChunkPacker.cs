using System.Text;
using GenericVectorBuilder.Core.Chunking;

namespace GenericVectorBuilder.Code.Chunking;

/// <summary>
/// Turns laid-out pieces into chunk texts: neighbouring declarations of the same type (fields,
/// properties, the type's declaration line) and neighbouring top-level statements are grouped
/// while they fit; everything else is one chunk per piece. A piece or group that does not fit
/// is split on line boundaries into parts, each repeating the header with "(part i/n)".
/// Why grouping: twenty one-line properties as twenty chunks would fill the index with
/// near-identical tiny vectors; as one chunk they describe the type's shape in one hit.
/// Why splitting never drops anything: the old code chunker cut long chunks at 5,000
/// characters and lost the rest. Here every line goes into some part, and a single line
/// longer than a whole chunk is cut into pieces instead of being cut off.
/// </summary>
internal static class ChunkPacker
{
   #region Public Methods

   /// <summary>
   /// Packs pieces into chunk texts, in file order.
   /// </summary>
   /// <param name="pieces">Contiguous pieces covering the file.</param>
   /// <param name="code">The file's lines.</param>
   /// <returns>Chunk texts, each at most <see cref="TextChunker.MAX_CHUNK_CHARS"/> characters.</returns>
   public static List<string> Pack( IReadOnlyList<CodePiece> pieces, CodeText code )
   {
      var texts = new List<string>();
      var group = new List<CodePiece>();
      foreach( CodePiece piece in pieces )
      {
         if( group.Count > 0 && CanJoin( group[^1], piece ) && code.Fits( Header( code, group.Append( piece ).ToList() ), group[0].StartLine, piece.EndLine ) )
         {
            group.Add( piece );
            continue;
         }

         Emit( group, code, texts );
         group = new List<CodePiece> { piece };
      }

      Emit( group, code, texts );
      return texts;
   }

   /// <summary>
   /// Packs lines into parts of at most <paramref name="budget"/> characters (lines joined by
   /// "\n"), cutting any single line longer than the budget into pieces.
   /// </summary>
   /// <param name="lines">The lines.</param>
   /// <param name="budget">Longest part.</param>
   /// <returns>The parts, in order.</returns>
   public static List<string> PackLines( IEnumerable<string> lines, int budget )
   {
      var parts = new List<string>();
      var current = new StringBuilder();
      bool open = false;
      foreach( string segment in lines.SelectMany( l => CutLine( l, budget ) ) )
      {
         if( open && current.Length + 1 + segment.Length > budget )
         {
            parts.Add( current.ToString() );
            current.Clear();
            open = false;
         }

         current.Append( open ? "\n" : string.Empty ).Append( segment );
         open = true;
      }

      if( open )
      {
         parts.Add( current.ToString() );
      }

      return parts;
   }

   #endregion Public Methods

   #region Private Methods

   /// <summary>
   /// Two neighbouring pieces may share a chunk when both are declarations, or both are
   /// top-level statements, of the same type in the same namespace.
   /// </summary>
   /// <param name="previous">Last piece of the current group.</param>
   /// <param name="next">The next piece.</param>
   /// <returns>True when they may be grouped.</returns>
   private static bool CanJoin( CodePiece previous, CodePiece next )
   {
      return previous.Kind == next.Kind && next.Kind is PieceKind.Declarations or PieceKind.Statements
         && previous.TypePath == next.TypePath && previous.Namespace == next.Namespace;
   }

   /// <summary>
   /// The header for a group: the last piece's namespace and type, and every piece's label.
   /// </summary>
   /// <param name="code">The file's lines.</param>
   /// <param name="group">The pieces.</param>
   /// <returns>The header line.</returns>
   private static string Header( CodeText code, List<CodePiece> group )
   {
      CodePiece last = group[^1];
      return ChunkHeader.Build( code.Path, last.Namespace, last.TypePath, ChunkHeader.JoinLabels( group.Select( p => p.Label ) ) );
   }

   /// <summary>
   /// Writes a group as one chunk, or as parts when it does not fit.
   /// </summary>
   /// <param name="group">The pieces (nothing happens when empty).</param>
   /// <param name="code">The file's lines.</param>
   /// <param name="texts">Chunk texts to append to.</param>
   private static void Emit( List<CodePiece> group, CodeText code, List<string> texts )
   {
      if( group.Count == 0 )
      {
         return;
      }

      string header = Header( code, group );
      int start = group[0].StartLine;
      int end = group[^1].EndLine;
      if( code.Fits( header, start, end ) )
      {
         texts.Add( header + "\n" + code.Body( start, end ) );
         return;
      }

      // The reserve covers " (part 999/999)"; a member split into 1,000 parts or more needs a
      // longer marker, so it is packed again with that marker's length kept free.
      int reserve = ChunkHeader.PART_SUFFIX_RESERVE;
      List<string> parts = PackLines( code.Lines( start, end ), TextChunker.MAX_CHUNK_CHARS - header.Length - reserve - 1 );
      while( ChunkHeader.PartSuffix( parts.Count, parts.Count ).Length > reserve )
      {
         reserve = ChunkHeader.PartSuffix( parts.Count, parts.Count ).Length + 2;
         parts = PackLines( code.Lines( start, end ), TextChunker.MAX_CHUNK_CHARS - header.Length - reserve - 1 );
      }

      for( int i = 0; i < parts.Count; i++ )
      {
         texts.Add( header + ChunkHeader.PartSuffix( i + 1, parts.Count ) + "\n" + parts[i] );
      }
   }

   /// <summary>
   /// Cuts a line into pieces no longer than the budget, preferring to cut just after a space.
   /// Nothing is removed: the space stays at the end of the earlier piece, and a surrogate pair
   /// is never cut in half.
   /// </summary>
   /// <param name="line">The line.</param>
   /// <param name="budget">Longest piece.</param>
   /// <returns>One or more pieces that concatenate back to the line.</returns>
   private static IEnumerable<string> CutLine( string line, int budget )
   {
      int start = 0;
      while( line.Length - start > budget )
      {
         int space = line.LastIndexOf( ' ', start + budget - 1, budget );
         int end = space > start ? space + 1 : start + budget;
         if( char.IsHighSurrogate( line[end - 1] ) )
         {
            end--;
         }

         yield return line[start..end];
         start = end;
      }

      yield return line[start..];
   }

   #endregion Private Methods
}
