using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace GenericVectorBuilder.Core.Sources.Files;

/// <summary>
/// How a delimited file should be read: encoding, delimiter and whether row one is a header.
/// </summary>
/// <param name="Encoding">Text encoding to decode the file with.</param>
/// <param name="Delimiter">Field separator.</param>
/// <param name="HasHeader">True when the first record holds column names.</param>
/// <param name="Columns">Column names (from the header, or generated as column1..N).</param>
public sealed record FileProfile( Encoding Encoding, char Delimiter, bool HasHeader, IReadOnlyList<string> Columns );

/// <summary>
/// Result of sniffing one file: a profile, or a plain-English reason it cannot be read.
/// </summary>
/// <param name="Profile">The profile when the file is readable.</param>
/// <param name="RejectReason">Why the file was rejected, when it was.</param>
public sealed record SniffResult( FileProfile? Profile, string? RejectReason );

/// <summary>
/// Works out how to read a delimited text file nobody described: encoding, delimiter and
/// header row. This is the piece that lets a non-technical user drop 200 CSVs on the page and
/// press Go without filling in a form per file.
/// Why each rule exists:
/// - Encoding: BOM first, then strict UTF-8 checked over the WHOLE file, then Windows-1252.
///   Office exports on Windows are still often 1252, and decoding those as UTF-8 turns every
///   accented letter into garbage. Checking only a leading sample is not enough: a big legacy
///   export can be pure ASCII for the first 64 KB and hold its first accent much later.
/// - Delimiter: chosen by which candidate gives the most consistent field count across the
///   first records, parsed quote-aware so commas inside quoted text do not vote.
/// - Header: the first non-blank record is a header when none of its cells is a number. Blank
///   cells (a trailing delimiter, a table that starts in column B) and repeated names are
///   repaired with generated and numbered names instead of demoting the whole row to data. A
///   numeric cell still means the row is data, because real header rows rarely contain
///   numbers and real data rows usually do.
/// </summary>
public static class FileSniffer
{
   #region Data Members

   private const int SAMPLE_BYTES = 64 * 1024;
   private const int SAMPLE_RECORDS = 50;
   private const int VALIDATION_BUFFER_BYTES = 64 * 1024;
   private const int MIN_UTF16_UNITS = 4;
   private const double MIN_UTF16_SHARE = 0.8;
   private const double MIN_CONSISTENCY = 0.8;
   private const double MAX_CONTROL_CHAR_RATIO = 0.01;
   private const string BINARY_REASON = "binary content, not a text file";
   private const string UTF16_REASON = "text saved as UTF-16 with no byte order mark, which cannot be read reliably; re-save the file as UTF-8";
   private const string UTF32_REASON = "text saved as UTF-32, which is not supported; re-save the file as UTF-8";
   private static readonly char[] CANDIDATE_DELIMITERS = { ',', '\t', '|', ';' };
   private static readonly Regex SQL_ERROR_LINE = new( @"^Msg \d+, Level \d+, State \d+", RegexOptions.Compiled );

   #endregion Data Members

   #region Constructor

   /// <summary>
   /// Registers the code-page encodings (Windows-1252 and friends) once per process. .NET on
   /// Linux ships them but does not enable them by default.
   /// </summary>
   static FileSniffer()
   {
      Encoding.RegisterProvider( CodePagesEncodingProvider.Instance );
   }

   #endregion Constructor

   #region Public Methods

   /// <summary>
   /// Sniffs a file from disk. The first 64 KB decides delimiter and header. When that sample
   /// reads as UTF-8 the rest of the file is checked too, so a Windows-1252 file whose first
   /// accent comes after the sample is still decoded as 1252.
   /// </summary>
   /// <param name="path">Full path to the file.</param>
   /// <returns>A profile, or a reject reason.</returns>
   public static SniffResult Sniff( string path )
   {
      var info = new FileInfo( path );
      if( info.Length == 0 )
      {
         return Reject( "empty file" );
      }

      using var stream = File.OpenRead( path );
      var sample = new byte[Math.Min( SAMPLE_BYTES, info.Length )];
      stream.ReadExactly( sample );
      bool truncated = info.Length > sample.Length;
      SniffResult result = SniffBytes( sample, truncated, legacyEncoding: false );
      if( truncated && IsSniffedAsBomlessUtf8( result, sample ) && !IsWholeStreamUtf8( stream ) )
      {
         return SniffBytes( sample, truncated, legacyEncoding: true );
      }

      return result;
   }

   /// <summary>
   /// Sniffs a byte sample. Split out from <see cref="Sniff(string)"/> so tests can feed bytes directly.
   /// </summary>
   /// <param name="sample">The first bytes of the file.</param>
   /// <param name="truncated">True when the file is longer than the sample.</param>
   /// <returns>A profile, or a reject reason.</returns>
   public static SniffResult SniffBytes( byte[] sample, bool truncated )
   {
      return SniffBytes( sample, truncated, legacyEncoding: false );
   }

   /// <summary>
   /// Decides whether a row of cells reads like column names. Shared with the Excel reader so
   /// both file types follow the same rule.
   /// </summary>
   /// <param name="cells">The first row's cells.</param>
   /// <returns>True when at least one cell has text and no cell is a number. Blank and
   /// repeated cells are allowed, because <see cref="MakeUnique"/> repairs them.</returns>
   public static bool LooksLikeHeader( IReadOnlyList<string?> cells )
   {
      bool anyText = false;
      foreach( string? raw in cells )
      {
         string cell = ( raw ?? string.Empty ).Trim();
         if( cell.Length == 0 )
         {
            continue;
         }

         if( double.TryParse( cell, NumberStyles.Any, CultureInfo.InvariantCulture, out _ ) )
         {
            return false;
         }

         anyText = true;
      }

      return anyText;
   }

   /// <summary>
   /// Trims header cells and de-duplicates them (Name, Name_2) so every column is addressable.
   /// Blank cells become column{position}.
   /// </summary>
   /// <param name="cells">Raw header cells.</param>
   /// <returns>Unique, trimmed column names.</returns>
   public static IReadOnlyList<string> MakeUnique( IReadOnlyList<string?> cells )
   {
      var result = new List<string>( cells.Count );
      var seen = new HashSet<string>( StringComparer.OrdinalIgnoreCase );
      for( int i = 0; i < cells.Count; i++ )
      {
         string name = ( cells[i] ?? string.Empty ).Trim();
         name = name.Length == 0 ? $"column{i + 1}" : name;
         string unique = name;
         for( int n = 2; !seen.Add( unique ); n++ )
         {
            unique = $"{name}_{n}";
         }

         result.Add( unique );
      }

      return result;
   }

   /// <summary>
   /// Builds column1..N names for files with no header row.
   /// </summary>
   /// <param name="count">Number of columns.</param>
   /// <returns>Generated names.</returns>
   public static IReadOnlyList<string> Generated( int count )
   {
      return Enumerable.Range( 1, count ).Select( i => $"column{i}" ).ToList();
   }

   /// <summary>
   /// Removes blank cells from the end of a row. A trailing delimiter on every line is common
   /// in exports and must not add a phantom column, and worksheets often report a field count
   /// wider than the real data.
   /// </summary>
   /// <param name="cells">Row cells.</param>
   /// <returns>The row without trailing blank cells.</returns>
   internal static IReadOnlyList<string?> TrimTrailingBlank( IReadOnlyList<string?> cells )
   {
      int last = cells.Count - 1;
      while( last >= 0 && string.IsNullOrWhiteSpace( cells[last] ) )
      {
         last--;
      }

      return cells.Take( last + 1 ).ToList();
   }

   #endregion Public Methods

   #region Private Methods

   /// <summary>
   /// Sniffs a byte sample, optionally skipping the UTF-8 test. The skip is used when the
   /// whole-file check found invalid UTF-8 after the sample, which means the file is a legacy
   /// code page even though its first 64 KB happened to be plain ASCII.
   /// </summary>
   /// <param name="sample">The first bytes of the file.</param>
   /// <param name="truncated">True when the file is longer than the sample.</param>
   /// <param name="legacyEncoding">True to decode as Windows-1252 without testing for UTF-8.</param>
   /// <returns>A profile, or a reject reason.</returns>
   private static SniffResult SniffBytes( byte[] sample, bool truncated, bool legacyEncoding )
   {
      if( StartsWithUtf32Bom( sample ) )
      {
         return Reject( UTF32_REASON );
      }

      Encoding? bomEncoding = DetectBom( sample, out int bomLength );
      if( bomEncoding == null && LooksBinary( sample ) )
      {
         return Reject( LooksLikeUtf16WithoutBom( sample ) ? UTF16_REASON : BINARY_REASON );
      }

      Encoding encoding = bomEncoding ?? ChooseEncoding( sample, truncated, legacyEncoding );
      string text = encoding.GetString( sample, bomLength, sample.Length - bomLength );
      if( truncated )
      {
         // Drop the partial last line so it cannot skew the field counts.
         int lastBreak = text.LastIndexOf( '\n' );
         text = lastBreak > 0 ? text[..lastBreak] : text;
      }

      if( string.IsNullOrWhiteSpace( text ) )
      {
         return Reject( "empty file" );
      }

      string? sqlError = SqlErrorText( text );
      if( sqlError != null )
      {
         return Reject( $"SQL Server error message, not data: {sqlError}" );
      }

      char delimiter = DetectDelimiter( text );
      List<string>? first = ReadFirstNonBlankRecord( text, delimiter );
      if( first == null )
      {
         return Reject( "empty file" );
      }

      IReadOnlyList<string?> headerCells = TrimTrailingBlank( first );
      bool hasHeader = LooksLikeHeader( headerCells );
      IReadOnlyList<string> columns = hasHeader ? MakeUnique( headerCells ) : Generated( first.Count );
      return new SniffResult( new FileProfile( encoding, delimiter, hasHeader, columns ), null );
   }

   /// <summary>
   /// Reads the first record that has any content, the same blank-row rule the reader applies
   /// later. Without this, a file that starts with an empty line would sniff its header as a
   /// blank row and the real header would be read as data.
   /// </summary>
   /// <param name="text">Decoded sample text.</param>
   /// <param name="delimiter">Chosen delimiter.</param>
   /// <returns>The first non-blank record, or null when there is none.</returns>
   private static List<string>? ReadFirstNonBlankRecord( string text, char delimiter )
   {
      var parser = new DelimitedParser( new StringReader( text ), delimiter );
      List<string>? record;
      do
      {
         record = parser.ReadRecord();
      }
      while( record != null && record.All( string.IsNullOrWhiteSpace ) );

      return record;
   }

   /// <summary>
   /// Picks the text encoding when there is no byte order mark: strict UTF-8 if the sample is
   /// valid UTF-8 (and legacy is not forced), otherwise Windows-1252.
   /// </summary>
   /// <param name="sample">The first bytes of the file.</param>
   /// <param name="truncated">True when the file continues past the sample.</param>
   /// <param name="legacyEncoding">True to skip the UTF-8 test.</param>
   /// <returns>The encoding to decode with.</returns>
   private static Encoding ChooseEncoding( byte[] sample, bool truncated, bool legacyEncoding )
   {
      bool utf8 = !legacyEncoding && IsStrictUtf8( sample, truncated );
      return utf8 ? new UTF8Encoding( false ) : Encoding.GetEncoding( 1252 );
   }

   /// <summary>
   /// True when a sniff succeeded by choosing UTF-8 without a byte order mark, which is the one
   /// case the whole-file check can overturn.
   /// </summary>
   /// <param name="result">The sample's sniff result.</param>
   /// <param name="sample">The sample bytes.</param>
   /// <returns>True when the file still needs the full UTF-8 check.</returns>
   private static bool IsSniffedAsBomlessUtf8( SniffResult result, byte[] sample )
   {
      return result.Profile != null && result.Profile.Encoding.CodePage == 65001 && DetectBom( sample, out _ ) == null;
   }

   /// <summary>
   /// Reads a stream from the start and checks that every byte decodes as strict UTF-8. The
   /// decoder carries a character split across two reads, so the buffer edge is harmless, and
   /// the final flush catches a file that ends in the middle of a character.
   /// </summary>
   /// <param name="stream">Seekable file stream.</param>
   /// <returns>True when the whole stream is valid UTF-8.</returns>
   private static bool IsWholeStreamUtf8( Stream stream )
   {
      stream.Position = 0;
      Decoder decoder = new UTF8Encoding( false, true ).GetDecoder();
      var buffer = new byte[VALIDATION_BUFFER_BYTES];
      var chars = new char[VALIDATION_BUFFER_BYTES + 4];
      try
      {
         int read;
         while( ( read = stream.Read( buffer, 0, buffer.Length ) ) > 0 )
         {
            decoder.GetChars( buffer, 0, read, chars, 0, flush: false );
         }

         decoder.GetChars( buffer, 0, 0, chars, 0, flush: true );
         return true;
      }
      catch( DecoderFallbackException )
      {
         return false;
      }
   }

   /// <summary>
   /// Picks the delimiter whose quote-aware field counts are the most consistent across the
   /// sample, preferring more columns on a tie. A file where no candidate splits anything is a
   /// single-column file, read with a comma (which never appears unquoted in it).
   /// </summary>
   /// <param name="text">Decoded sample text.</param>
   /// <returns>The chosen delimiter.</returns>
   private static char DetectDelimiter( string text )
   {
      char best = ',';
      double bestConsistency = 0;
      int bestColumns = 1;
      foreach( char candidate in CANDIDATE_DELIMITERS )
      {
         (int mode, double consistency) = MeasureCandidate( text, candidate );
         if( mode < 2 || consistency < MIN_CONSISTENCY )
         {
            continue;
         }

         if( consistency > bestConsistency || ( consistency == bestConsistency && mode > bestColumns ) )
         {
            best = candidate;
            bestConsistency = consistency;
            bestColumns = mode;
         }
      }

      return best;
   }

   /// <summary>
   /// Parses the sample with one candidate delimiter and measures how stable the field count is.
   /// Blank records are skipped, exactly as the reader skips them.
   /// </summary>
   /// <param name="text">Decoded sample text.</param>
   /// <param name="candidate">Delimiter to try.</param>
   /// <returns>The most common field count and the share of records that have it.</returns>
   private static (int Mode, double Consistency) MeasureCandidate( string text, char candidate )
   {
      var parser = new DelimitedParser( new StringReader( text ), candidate );
      var counts = new List<int>();
      while( counts.Count < SAMPLE_RECORDS )
      {
         List<string>? record = parser.ReadRecord();
         if( record == null )
         {
            break;
         }

         if( record.All( string.IsNullOrWhiteSpace ) )
         {
            continue;
         }

         counts.Add( record.Count );
      }

      if( counts.Count == 0 )
      {
         return (0, 0);
      }

      var top = counts.GroupBy( c => c ).OrderByDescending( g => g.Count() ).ThenByDescending( g => g.Key ).First();
      return (top.Key, (double)top.Count() / counts.Count);
   }

   /// <summary>
   /// Spots a file that holds sqlcmd/bcp error output instead of data, e.g.
   /// "Msg 208, Level 16, State 1, Server X, Line 1" followed by "Invalid object name ...".
   /// Why: a failed export script writes that text into every output file. Read as CSV, the
   /// error line becomes a header ("Msg 208", "Level 16") and every failed export groups into
   /// one bogus table. Seen for real 2026-10-02: 68 of 71 AdventureWorks exports were this.
   /// </summary>
   /// <param name="text">Decoded sample text.</param>
   /// <returns>The error message (second line, or the first if alone), or null for real data.</returns>
   private static string? SqlErrorText( string text )
   {
      string[] lines = text.Split( '\n', 3, StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries );
      if( lines.Length == 0 || !SQL_ERROR_LINE.IsMatch( lines[0] ) )
      {
         return null;
      }

      return lines.Length > 1 ? lines[1] : lines[0];
   }

   /// <summary>
   /// Recognizes UTF-8 and UTF-16 byte order marks. UTF-32 is checked first by the caller,
   /// because a UTF-32 little-endian mark starts with the same two bytes as UTF-16.
   /// </summary>
   /// <param name="bytes">File sample.</param>
   /// <param name="length">Length of the BOM found, or 0.</param>
   /// <returns>The encoding the BOM declares, or null.</returns>
   private static Encoding? DetectBom( byte[] bytes, out int length )
   {
      length = 0;
      if( bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF )
      {
         length = 3;
         return new UTF8Encoding( false );
      }

      if( bytes.Length >= 2 && bytes[0] == 0xFF && bytes[1] == 0xFE )
      {
         length = 2;
         return Encoding.Unicode;
      }

      if( bytes.Length >= 2 && bytes[0] == 0xFE && bytes[1] == 0xFF )
      {
         length = 2;
         return Encoding.BigEndianUnicode;
      }

      return null;
   }

   /// <summary>
   /// True when the sample starts with a UTF-32 byte order mark (either byte order).
   /// </summary>
   /// <param name="bytes">File sample.</param>
   /// <returns>True for a UTF-32 file.</returns>
   private static bool StartsWithUtf32Bom( byte[] bytes )
   {
      if( bytes.Length < 4 )
      {
         return false;
      }

      bool little = bytes[0] == 0xFF && bytes[1] == 0xFE && bytes[2] == 0x00 && bytes[3] == 0x00;
      bool big = bytes[0] == 0x00 && bytes[1] == 0x00 && bytes[2] == 0xFE && bytes[3] == 0xFF;
      return little || big;
   }

   /// <summary>
   /// Flags binary files: any NUL byte, or more than 1% control characters other than
   /// tab, CR and LF. Text exports never contain those.
   /// </summary>
   /// <param name="bytes">File sample.</param>
   /// <returns>True when the sample is not text.</returns>
   private static bool LooksBinary( byte[] bytes )
   {
      int control = 0;
      foreach( byte b in bytes )
      {
         if( b == 0 )
         {
            return true;
         }

         if( b < 0x20 && b != (byte)'\t' && b != (byte)'\r' && b != (byte)'\n' )
         {
            control++;
         }
      }

      return control > bytes.Length * MAX_CONTROL_CHAR_RATIO;
   }

   /// <summary>
   /// Spots UTF-16 text that has no byte order mark: ASCII characters stored as a text byte
   /// plus a NUL byte, in either byte order. Used only to give the user an accurate reason
   /// instead of calling a plain text file binary. Text outside ASCII (Cyrillic, CJK) has no
   /// NUL bytes to recognize, so it still reads as binary.
   /// </summary>
   /// <param name="bytes">File sample.</param>
   /// <returns>True when most 2-byte units look like ASCII text in one byte order.</returns>
   private static bool LooksLikeUtf16WithoutBom( byte[] bytes )
   {
      if( bytes.Length / 2 < MIN_UTF16_UNITS )
      {
         return false;
      }

      return AsciiUnitShare( bytes, highByteOffset: 1 ) >= MIN_UTF16_SHARE || AsciiUnitShare( bytes, highByteOffset: 0 ) >= MIN_UTF16_SHARE;
   }

   /// <summary>
   /// Share of 2-byte units that hold a printable ASCII character (or tab, CR, LF) with a zero
   /// high byte, for one assumed byte order.
   /// </summary>
   /// <param name="bytes">File sample.</param>
   /// <param name="highByteOffset">Which byte of each unit is the high byte: 1 for little-endian, 0 for big-endian.</param>
   /// <returns>A share from 0 to 1.</returns>
   private static double AsciiUnitShare( byte[] bytes, int highByteOffset )
   {
      int units = bytes.Length / 2;
      int ascii = 0;
      for( int i = 0; i < units; i++ )
      {
         byte high = bytes[( i * 2 ) + highByteOffset];
         byte low = bytes[( i * 2 ) + 1 - highByteOffset];
         bool text = low == (byte)'\t' || low == (byte)'\r' || low == (byte)'\n' || ( low >= 0x20 && low < 0x7F );
         if( high == 0 && text )
         {
            ascii++;
         }
      }

      return (double)ascii / units;
   }

   /// <summary>
   /// True when the sample decodes as strict UTF-8. When the sample was cut mid-file, an
   /// invalid sequence in the last 3 bytes is a split character, not bad encoding.
   /// </summary>
   /// <param name="bytes">File sample.</param>
   /// <param name="truncated">True when the file continues past the sample.</param>
   /// <returns>True for valid UTF-8.</returns>
   private static bool IsStrictUtf8( byte[] bytes, bool truncated )
   {
      var strict = new UTF8Encoding( false, true );
      try
      {
         strict.GetCharCount( bytes );
         return true;
      }
      catch( DecoderFallbackException ex )
      {
         return truncated && ex.Index >= bytes.Length - 3;
      }
   }

   /// <summary>
   /// Builds a reject result.
   /// </summary>
   /// <param name="reason">Plain-English reason.</param>
   /// <returns>The reject result.</returns>
   private static SniffResult Reject( string reason )
   {
      return new SniffResult( null, reason );
   }

   #endregion Private Methods
}
