using System.Globalization;
using System.Text;
using ExcelDataReader;

namespace GenericVectorBuilder.Core.Sources.Files;

/// <summary>
/// The physical kind of an origin: a delimited text file, or one sheet of a workbook.
/// </summary>
public enum OriginKind
{
   /// <summary>CSV, pipe, tab or other delimited text file.</summary>
   Delimited,

   /// <summary>One worksheet of an .xlsx, .xlsm or .xls workbook.</summary>
   ExcelSheet,
}

/// <summary>
/// Everything needed to re-open one origin and read its rows.
/// </summary>
/// <param name="Origin">Stable origin id: relative path, plus "#Sheet" for workbooks.</param>
/// <param name="FullPath">Absolute file path.</param>
/// <param name="Kind">Delimited file or workbook sheet.</param>
/// <param name="Profile">How to read it (encoding and delimiter only matter for delimited files).</param>
/// <param name="Sheet">Worksheet name for workbook origins.</param>
public sealed record OriginSpec( string Origin, string FullPath, OriginKind Kind, FileProfile Profile, string? Sheet );

/// <summary>
/// One data row read from an origin.
/// </summary>
/// <param name="RowNumber">1-based data row number (header excluded).</param>
/// <param name="Values">Cell values aligned to the origin's columns; null for empty cells.</param>
/// <param name="WrongWidth">True when the row had extra non-empty cells and must be skipped.</param>
public sealed record OriginRow( long RowNumber, IReadOnlyList<string?> Values, bool WrongWidth );

/// <summary>
/// One worksheet found in a workbook, with its first non-empty row for header detection.
/// </summary>
/// <param name="Name">Sheet name.</param>
/// <param name="Hidden">True for hidden or very hidden sheets, which are skipped.</param>
/// <param name="FirstRow">First non-empty row, or null for an empty sheet.</param>
public sealed record SheetInfo( string Name, bool Hidden, IReadOnlyList<string?>? FirstRow );

/// <summary>
/// Thrown when a file turns out to be structurally broken part way through, e.g. an unclosed
/// quote. The message is shown to the user in the reject list.
/// </summary>
public sealed class MalformedFileException : Exception
{
   #region Constructor

   /// <summary>
   /// Creates the exception with a plain-English reason.
   /// </summary>
   /// <param name="message">Reason shown in the reject list.</param>
   public MalformedFileException( string message ) : base( message )
   {
   }

   #endregion Constructor
}

/// <summary>
/// Reads the data rows of one origin, whatever its kind. The folder scanner (to validate and
/// sample) and the folder source (to feed the pipeline) both use this, so a file is read the
/// same way during preview and during the real run.
/// Why rows are normalized here: short rows are padded with empty cells because exports often
/// drop trailing empty columns. Rows with extra NON-empty cells are flagged, not guessed at,
/// because shifting values into the wrong column would embed wrong facts.
/// </summary>
public static class OriginReader
{
   #region Data Members

   private const string HIDDEN = "hidden";
   private const string VERY_HIDDEN = "veryhidden";
   private const int UTF8_CODE_PAGE = 65001;
   private const double LARGE_WHOLE_NUMBER = 1e15;
   private static readonly DateTime TIME_ONLY_LAST_DATE = new( 1899, 12, 31 );

   #endregion Data Members

   #region Public Methods

   /// <summary>
   /// Streams the data rows of an origin.
   /// </summary>
   /// <param name="spec">The origin to read.</param>
   /// <returns>Data rows in file order.</returns>
   /// <exception cref="MalformedFileException">The file is structurally broken.</exception>
   public static IEnumerable<OriginRow> Read( OriginSpec spec )
   {
      int width = spec.Profile.Columns.Count;
      IEnumerable<IReadOnlyList<string?>> raw = spec.Kind == OriginKind.Delimited
         ? ReadDelimitedRaw( spec.FullPath, spec.Profile )
         : ReadSheetRaw( spec.FullPath, spec.Sheet! );

      long rowNumber = 0;
      bool skipHeader = spec.Profile.HasHeader;
      foreach( IReadOnlyList<string?> cells in raw )
      {
         if( skipHeader )
         {
            skipHeader = false;
            continue;
         }

         rowNumber++;
         yield return Normalize( rowNumber, cells, width );
      }
   }

   /// <summary>
   /// Lists the worksheets in a workbook with their first non-empty row.
   /// </summary>
   /// <param name="path">Workbook path.</param>
   /// <returns>One entry per sheet, in workbook order.</returns>
   public static IReadOnlyList<SheetInfo> ListSheets( string path )
   {
      var sheets = new List<SheetInfo>();
      using var stream = File.OpenRead( path );
      using var reader = OpenWorkbook( stream );
      do
      {
         bool hidden = reader.VisibleState is HIDDEN or VERY_HIDDEN;
         IReadOnlyList<string?>? first = null;
         while( first == null && reader.Read() )
         {
            IReadOnlyList<string?> cells = ReadCells( reader );
            first = IsBlank( cells ) ? null : FileSniffer.TrimTrailingBlank( cells );
         }

         sheets.Add( new SheetInfo( reader.Name, hidden, first ) );
      }
      while( reader.NextResult() );

      return sheets;
   }

   #endregion Public Methods

   #region Private Methods

   /// <summary>
   /// Reads raw records (header included) from a delimited file, skipping blank lines and
   /// stopping hard on an unclosed quote or on bytes that are not valid in the file's encoding.
   /// </summary>
   /// <param name="path">File path.</param>
   /// <param name="profile">Encoding and delimiter.</param>
   /// <returns>Raw records.</returns>
   private static IEnumerable<IReadOnlyList<string?>> ReadDelimitedRaw( string path, FileProfile profile )
   {
      using StreamReader reader = OpenDelimitedReader( path, profile.Encoding );
      var parser = new DelimitedParser( reader, profile.Delimiter );
      while( true )
      {
         List<string>? record = ReadRecordOrExplain( parser );
         if( parser.UnterminatedQuote )
         {
            throw new MalformedFileException( $"unclosed quote starting near line {parser.RecordStartLine}" );
         }

         if( record == null )
         {
            yield break;
         }

         IReadOnlyList<string?> cells = record.Select( v => (string?)v ).ToList();
         if( !IsBlank( cells ) )
         {
            yield return cells;
         }
      }
   }

   /// <summary>
   /// Reads the next record, turning a decoding failure into a plain-English exception. Why:
   /// the scanner already proved the file decodes cleanly, so a failure here means the file
   /// changed since then. Replacing the bad bytes with U+FFFD would embed garbage as if it
   /// were data, so the origin is rejected instead and keeps its old vectors.
   /// </summary>
   /// <param name="parser">Parser over the file.</param>
   /// <returns>The next record, or null at end of file.</returns>
   /// <exception cref="MalformedFileException">The bytes are not valid in the file's encoding.</exception>
   private static List<string>? ReadRecordOrExplain( DelimitedParser parser )
   {
      try
      {
         return parser.ReadRecord();
      }
      catch( DecoderFallbackException )
      {
         throw new MalformedFileException( "the file is not valid text in its detected encoding; it may have changed since it was scanned" );
      }
   }

   /// <summary>
   /// Opens a delimited file for reading. UTF-8 files are decoded strictly (invalid bytes throw
   /// instead of becoming U+FFFD) with the byte order mark skipped by hand; other encodings use
   /// the stream reader's normal byte order mark handling.
   /// </summary>
   /// <param name="path">File path.</param>
   /// <param name="encoding">Encoding chosen by the sniffer.</param>
   /// <returns>A reader that owns the file stream.</returns>
   private static StreamReader OpenDelimitedReader( string path, Encoding encoding )
   {
      FileStream stream = File.OpenRead( path );
      try
      {
         if( encoding.CodePage != UTF8_CODE_PAGE )
         {
            return new StreamReader( stream, encoding, detectEncodingFromByteOrderMarks: true );
         }

         SkipUtf8ByteOrderMark( stream );
         return new StreamReader( stream, new UTF8Encoding( false, true ), detectEncodingFromByteOrderMarks: false );
      }
      catch
      {
         stream.Dispose();
         throw;
      }
   }

   /// <summary>
   /// Moves a stream past a leading UTF-8 byte order mark, or back to the start when there is none.
   /// </summary>
   /// <param name="stream">Seekable file stream positioned at the start.</param>
   private static void SkipUtf8ByteOrderMark( Stream stream )
   {
      Span<byte> head = stackalloc byte[3];
      int read = stream.ReadAtLeast( head, head.Length, throwOnEndOfStream: false );
      bool hasMark = read == 3 && head[0] == 0xEF && head[1] == 0xBB && head[2] == 0xBF;
      if( !hasMark )
      {
         stream.Position = 0;
      }
   }

   /// <summary>
   /// Reads raw rows (header included) from one worksheet, skipping fully empty rows.
   /// </summary>
   /// <param name="path">Workbook path.</param>
   /// <param name="sheet">Worksheet name.</param>
   /// <returns>Raw rows.</returns>
   private static IEnumerable<IReadOnlyList<string?>> ReadSheetRaw( string path, string sheet )
   {
      using var stream = File.OpenRead( path );
      using var reader = OpenWorkbook( stream );
      while( reader.Name != sheet )
      {
         if( !reader.NextResult() )
         {
            throw new MalformedFileException( $"sheet '{sheet}' no longer exists" );
         }
      }

      while( reader.Read() )
      {
         IReadOnlyList<string?> cells = ReadCells( reader );
         if( !IsBlank( cells ) )
         {
            yield return cells;
         }
      }
   }

   /// <summary>
   /// Opens a workbook of either format. ExcelDataReader tells .xls from .xlsx by content, and
   /// old .xls files need a code-page fallback for their text.
   /// </summary>
   /// <param name="stream">Open workbook stream.</param>
   /// <returns>The reader, positioned on the first sheet.</returns>
   private static IExcelDataReader OpenWorkbook( Stream stream )
   {
      Encoding.RegisterProvider( CodePagesEncodingProvider.Instance );
      var config = new ExcelReaderConfiguration { FallbackEncoding = Encoding.GetEncoding( 1252 ) };
      return ExcelReaderFactory.CreateReader( stream, config );
   }

   /// <summary>
   /// Reads the current worksheet row as formatted strings.
   /// </summary>
   /// <param name="reader">Reader positioned on a row.</param>
   /// <returns>Formatted cells.</returns>
   private static IReadOnlyList<string?> ReadCells( IExcelDataReader reader )
   {
      var cells = new string?[reader.FieldCount];
      for( int i = 0; i < cells.Length; i++ )
      {
         cells[i] = FormatCell( reader.GetValue( i ) );
      }

      return cells;
   }

   /// <summary>
   /// Formats a worksheet value culture-independently, so the embedded text does not change
   /// with the server's locale: ISO dates, plain times, invariant numbers.
   /// </summary>
   /// <param name="value">Raw cell value.</param>
   /// <returns>Text, or null for an empty cell.</returns>
   private static string? FormatCell( object? value )
   {
      string? text = value switch
      {
         null or DBNull => null,
         DateTime d => FormatDateTime( d ),
         TimeSpan t => FormatTime( t ),
         double n => FormatNumber( n ),
         bool b => b ? "true" : "false",
         IFormattable f => f.ToString( null, CultureInfo.InvariantCulture ),
         _ => value.ToString(),
      };

      return string.IsNullOrWhiteSpace( text ) ? null : text.Trim();
   }

   /// <summary>
   /// Formats a date cell. Excel stores a time of day as a fraction of a day, which the reader
   /// returns as a date at the Excel epoch (1899-12-31). Printing that date would put a bogus
   /// "1899-12-31" into the embedded text, so a time-only cell is written as HH:mm:ss.
   /// </summary>
   /// <param name="d">The date value.</param>
   /// <returns>A time, a date, or a date and time.</returns>
   private static string FormatDateTime( DateTime d )
   {
      if( d.Date <= TIME_ONLY_LAST_DATE )
      {
         return FormatTime( d.TimeOfDay );
      }

      return d.TimeOfDay == TimeSpan.Zero
         ? d.ToString( "yyyy-MM-dd", CultureInfo.InvariantCulture )
         : d.ToString( "yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture );
   }

   /// <summary>
   /// Formats a time of day or an elapsed time as HH:mm:ss, rounded to the nearest second. A
   /// fraction of a day carries binary noise (09:30 is stored as 0.395833333333333, which is
   /// 09:29:59.9999999), and Excel shows whole seconds too. Elapsed times past 24 hours keep
   /// counting hours.
   /// </summary>
   /// <param name="time">The time or duration.</param>
   /// <returns>The time as text.</returns>
   private static string FormatTime( TimeSpan time )
   {
      TimeSpan whole = TimeSpan.FromSeconds( Math.Round( time.TotalSeconds ) );
      string hours = ( (long)whole.TotalHours ).ToString( "00", CultureInfo.InvariantCulture );
      return $"{hours}:{whole.Minutes:00}:{whole.Seconds:00}";
   }

   /// <summary>
   /// Formats a numeric cell without scientific notation. Ordinary values keep 15 significant
   /// digits, which is what Excel shows and what hides binary noise like 0.30000000000000004.
   /// Whole numbers of 1e15 and above (16-digit ids, microsecond timestamps) use the shortest
   /// round-trip form instead, so every digit survives and neighbouring ids stay distinct.
   /// </summary>
   /// <param name="n">The number.</param>
   /// <returns>Plain digits, with no exponent when the value fits a decimal.</returns>
   private static string FormatNumber( double n )
   {
      bool largeWhole = Math.Abs( n ) >= LARGE_WHOLE_NUMBER && n == Math.Truncate( n );
      string text = n.ToString( largeWhole ? "R" : "G15", CultureInfo.InvariantCulture );
      if( text.Contains( 'E' ) && decimal.TryParse( text, NumberStyles.Float, CultureInfo.InvariantCulture, out decimal plain ) && ( plain != 0 || n == 0 ) )
      {
         // Out-of-range values (beyond about 7.9e28 or below 1e-28) keep their exponent form.
         return plain.ToString( CultureInfo.InvariantCulture );
      }

      return text;
   }

   /// <summary>
   /// Aligns a raw record to the column count: trims values, pads short rows, drops trailing
   /// empty extras, and flags rows that still have extra non-empty cells.
   /// </summary>
   /// <param name="rowNumber">Data row number.</param>
   /// <param name="cells">Raw cells.</param>
   /// <param name="width">Expected column count.</param>
   /// <returns>The normalized row.</returns>
   private static OriginRow Normalize( long rowNumber, IReadOnlyList<string?> cells, int width )
   {
      var values = new string?[width];
      bool wrongWidth = false;
      for( int i = 0; i < cells.Count; i++ )
      {
         string? value = string.IsNullOrWhiteSpace( cells[i] ) ? null : cells[i]!.Trim();
         if( i < width )
         {
            values[i] = value;
         }
         else if( value != null )
         {
            wrongWidth = true;
         }
      }

      return new OriginRow( rowNumber, values, wrongWidth );
   }

   /// <summary>
   /// True when every cell is empty or whitespace.
   /// </summary>
   /// <param name="cells">Cells to check.</param>
   /// <returns>True for a blank row.</returns>
   private static bool IsBlank( IReadOnlyList<string?> cells )
   {
      return cells.All( string.IsNullOrWhiteSpace );
   }

   #endregion Private Methods
}
