using System.Text;

namespace GenericVectorBuilder.Core.Sources.Files;

/// <summary>
/// Streaming, quote-aware parser for delimited text (CSV, pipe, tab, semicolon). Handles quoted
/// fields, doubled quotes inside quotes, embedded line breaks, and CRLF or LF line endings.
/// Why: a hand-rolled parser gives exact control over the two failure modes that matter for a
/// folder of strangers' files. An unclosed quote is REPORTED instead of silently swallowing
/// the rest of the file into one field, and the delimiter is whatever the sniffer decided, with
/// no library second-guessing it.
/// </summary>
public sealed class DelimitedParser
{
   #region Data Members

   private const int BUFFER_SIZE = 64 * 1024;
   private const char QUOTE = '"';

   private readonly TextReader _reader;
   private readonly char _delimiter;
   private readonly char[] _buffer = new char[BUFFER_SIZE];
   private readonly StringBuilder _field = new();
   private int _length;
   private int _position;
   private long _line = 1;

   #endregion Data Members

   #region Constructor

   /// <summary>
   /// Creates a parser over an already-decoded text stream.
   /// </summary>
   /// <param name="reader">Decoded text. The caller owns its lifetime.</param>
   /// <param name="delimiter">Field separator chosen by the sniffer.</param>
   public DelimitedParser( TextReader reader, char delimiter )
   {
      _reader = reader;
      _delimiter = delimiter;
   }

   #endregion Constructor

   #region Public Methods

   /// <summary>
   /// Physical line number (1-based) where the most recently returned record started.
   /// Used in reject reasons so the user can open the file at the right spot.
   /// </summary>
   public long RecordStartLine { get; private set; }

   /// <summary>
   /// True once the parser hit end of file while still inside a quoted field. The caller must
   /// reject the file: everything after the stray quote was merged into a single field.
   /// </summary>
   public bool UnterminatedQuote { get; private set; }

   /// <summary>
   /// Reads the next record.
   /// </summary>
   /// <returns>The fields of the record, or null at end of input.</returns>
   public List<string>? ReadRecord()
   {
      if( Peek() < 0 )
      {
         return null;
      }

      RecordStartLine = _line;
      var fields = new List<string>();
      while( true )
      {
         bool endOfRecord = ReadField( out string value );
         fields.Add( value );
         if( endOfRecord )
         {
            return fields;
         }
      }
   }

   #endregion Public Methods

   #region Private Methods

   /// <summary>
   /// Reads one field and reports whether it ended the record (line break or end of input)
   /// rather than a delimiter.
   /// </summary>
   /// <param name="value">The field text with quoting removed.</param>
   /// <returns>True when this field was the last one in its record.</returns>
   private bool ReadField( out string value )
   {
      _field.Clear();
      if( Peek() == QUOTE )
      {
         Next();
         return ReadQuotedField( out value );
      }

      while( true )
      {
         int c = Next();
         if( c < 0 || IsLineEnd( c ) )
         {
            value = _field.ToString();
            return true;
         }

         if( c == _delimiter )
         {
            value = _field.ToString();
            return false;
         }

         _field.Append( (char)c );
      }
   }

   /// <summary>
   /// Reads the remainder of a field that opened with a quote. A doubled quote is a literal quote.
   /// Text after the closing quote but before the delimiter is kept (lenient), because real
   /// exports do contain things like "abc"def and dropping it would lose data.
   /// </summary>
   /// <param name="value">The field text.</param>
   /// <returns>True when this field was the last one in its record.</returns>
   private bool ReadQuotedField( out string value )
   {
      bool inQuotes = true;
      while( true )
      {
         int c = Next();
         if( c < 0 )
         {
            // End of input while still quoted means the file is malformed from the stray quote on.
            UnterminatedQuote |= inQuotes;
            value = _field.ToString();
            return true;
         }

         if( inQuotes )
         {
            if( c == QUOTE )
            {
               if( Peek() == QUOTE )
               {
                  Next();
                  _field.Append( QUOTE );
               }
               else
               {
                  inQuotes = false;
               }
            }
            else
            {
               if( c == '\n' )
               {
                  _line++;
               }

               _field.Append( (char)c );
            }

            continue;
         }

         if( c == _delimiter )
         {
            value = _field.ToString();
            return false;
         }

         if( IsLineEnd( c ) )
         {
            value = _field.ToString();
            return true;
         }

         _field.Append( (char)c );
      }
   }

   /// <summary>
   /// Treats CR, LF and CRLF as one line break and counts physical lines.
   /// </summary>
   /// <param name="c">The character just consumed.</param>
   /// <returns>True when the character ended a line.</returns>
   private bool IsLineEnd( int c )
   {
      if( c == '\r' )
      {
         if( Peek() == '\n' )
         {
            Next();
         }

         _line++;
         return true;
      }

      if( c == '\n' )
      {
         _line++;
         return true;
      }

      return false;
   }

   /// <summary>
   /// Returns the next character without consuming it, refilling the buffer as needed.
   /// </summary>
   /// <returns>The next character, or -1 at end of input.</returns>
   private int Peek()
   {
      if( _position >= _length && !Fill() )
      {
         return -1;
      }

      return _buffer[_position];
   }

   /// <summary>
   /// Consumes and returns the next character.
   /// </summary>
   /// <returns>The character, or -1 at end of input.</returns>
   private int Next()
   {
      int c = Peek();
      if( c >= 0 )
      {
         _position++;
      }

      return c;
   }

   /// <summary>
   /// Reads the next block from the underlying reader.
   /// </summary>
   /// <returns>False at end of input.</returns>
   private bool Fill()
   {
      _length = _reader.Read( _buffer, 0, _buffer.Length );
      _position = 0;
      return _length > 0;
   }

   #endregion Private Methods
}
