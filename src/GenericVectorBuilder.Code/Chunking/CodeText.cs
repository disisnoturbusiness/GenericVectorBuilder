using System.Text;
using GenericVectorBuilder.Core.Chunking;
using Microsoft.CodeAnalysis.Text;

namespace GenericVectorBuilder.Code.Chunking;

/// <summary>
/// A source file split into lines exactly the way Roslyn counts them, with the measurements the
/// chunker needs: how long a run of lines is, and whether it fits in one chunk under a header.
/// Why Roslyn's line breaks: syntax positions are turned into line numbers by Roslyn, which
/// also treats a lone carriage return or a Unicode line separator as a line break. Splitting
/// the text any other way would put a member's lines in the wrong place. Chunks join lines with
/// "\n", so a Windows-style file and its Unix-style checkout give the same chunks and ids.
/// </summary>
internal sealed class CodeText
{
   #region Data Members

   private readonly string[] _lines;
   private readonly long[] _prefix;

   #endregion Data Members

   #region Constructor

   /// <summary>
   /// Splits the text into lines and precomputes their running lengths.
   /// </summary>
   /// <param name="path">File path shown in every chunk header.</param>
   /// <param name="text">The parsed source text.</param>
   public CodeText( string path, SourceText text )
   {
      Path = path;
      _lines = text.Lines.Select( l => text.ToString( l.Span ) ).ToArray();
      _prefix = new long[_lines.Length + 1];
      for( int i = 0; i < _lines.Length; i++ )
      {
         _prefix[i + 1] = _prefix[i] + _lines[i].Length;
      }
   }

   #endregion Constructor

   #region Public Methods

   /// <summary>File path shown in every chunk header.</summary>
   public string Path { get; }

   /// <summary>Number of lines (a text ending in a line break has an empty last line).</summary>
   public int LineCount => _lines.Length;

   /// <summary>
   /// One line's text, without its line break.
   /// </summary>
   /// <param name="line">0-based line number.</param>
   /// <returns>The line.</returns>
   public string Line( int line ) => _lines[line];

   /// <summary>
   /// Length of lines start..end joined with "\n".
   /// </summary>
   /// <param name="start">First line.</param>
   /// <param name="end">Last line, inclusive.</param>
   /// <returns>Characters.</returns>
   public long BodyLength( int start, int end ) => _prefix[end + 1] - _prefix[start] + ( end - start );

   /// <summary>
   /// Lines start..end joined with "\n".
   /// </summary>
   /// <param name="start">First line.</param>
   /// <param name="end">Last line, inclusive.</param>
   /// <returns>The text.</returns>
   public string Body( int start, int end )
   {
      var body = new StringBuilder( (int)BodyLength( start, end ) );
      for( int i = start; i <= end; i++ )
      {
         body.Append( i > start ? "\n" : string.Empty ).Append( _lines[i] );
      }

      return body.ToString();
   }

   /// <summary>
   /// The lines start..end, for splitting a run that does not fit.
   /// </summary>
   /// <param name="start">First line.</param>
   /// <param name="end">Last line, inclusive.</param>
   /// <returns>The lines.</returns>
   public IEnumerable<string> Lines( int start, int end ) => _lines.Skip( start ).Take( end - start + 1 );

   /// <summary>
   /// The header of a single piece.
   /// </summary>
   /// <param name="piece">The piece.</param>
   /// <returns>The header line.</returns>
   public string Header( CodePiece piece ) => ChunkHeader.Build( Path, piece.Namespace, piece.TypePath, piece.Label );

   /// <summary>
   /// True when the header, a line break and lines start..end fit in one chunk.
   /// </summary>
   /// <param name="header">The header line.</param>
   /// <param name="start">First line.</param>
   /// <param name="end">Last line, inclusive.</param>
   /// <returns>True when it fits under <see cref="TextChunker.MAX_CHUNK_CHARS"/>.</returns>
   public bool Fits( string header, int start, int end ) => header.Length + 1 + BodyLength( start, end ) <= TextChunker.MAX_CHUNK_CHARS;

   /// <summary>
   /// True when a piece fits in one chunk under its own header.
   /// </summary>
   /// <param name="piece">The piece.</param>
   /// <returns>True when it fits.</returns>
   public bool Fits( CodePiece piece ) => Fits( Header( piece ), piece.StartLine, piece.EndLine );

   /// <summary>
   /// True when a line holds nothing but whitespace.
   /// </summary>
   /// <param name="line">0-based line number.</param>
   /// <returns>True for a blank line.</returns>
   public bool IsBlank( int line ) => string.IsNullOrWhiteSpace( _lines[line] );

   #endregion Public Methods
}
