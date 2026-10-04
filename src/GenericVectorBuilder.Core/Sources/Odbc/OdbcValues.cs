using System.Data.Odbc;
using System.Globalization;

namespace GenericVectorBuilder.Core.Sources.Odbc;

/// <summary>
/// Decides which ODBC column types can be read as text, and formats every value the same way
/// on every machine: ISO 8601 dates, invariant numbers, "true"/"false", lower-case GUIDs.
/// Why invariant formatting matters: the formatted text is what gets hashed and embedded. A
/// server whose culture prints "1,5" instead of "1.5", or "02/01/2024" instead of "2024-01-02",
/// would re-embed every row the day the culture changed, and dates in ambiguous local formats
/// embed worse than ISO ones.
/// Why some types are read as driver text: System.Data.Odbc has no mapping for SQL Server's
/// datetimeoffset and time(7) types and throws on GetValue (measured 2026-10-03, "Unknown SQL
/// type - -155"), but the driver happily returns them as text, which is then normalized here.
/// </summary>
internal static class OdbcValues
{
   #region Data Members

   internal const short SQL_CHAR = 1;
   internal const short SQL_NUMERIC = 2;
   internal const short SQL_DECIMAL = 3;
   internal const short SQL_INTEGER = 4;
   internal const short SQL_SMALLINT = 5;
   internal const short SQL_FLOAT = 6;
   internal const short SQL_REAL = 7;
   internal const short SQL_DOUBLE = 8;
   internal const short SQL_DATE = 9;
   internal const short SQL_TIME = 10;
   internal const short SQL_TIMESTAMP = 11;
   internal const short SQL_VARCHAR = 12;
   internal const short SQL_TYPE_DATE = 91;
   internal const short SQL_TYPE_TIME = 92;
   internal const short SQL_TYPE_TIMESTAMP = 93;
   internal const short SQL_LONGVARCHAR = -1;
   internal const short SQL_BINARY = -2;
   internal const short SQL_VARBINARY = -3;
   internal const short SQL_LONGVARBINARY = -4;
   internal const short SQL_BIGINT = -5;
   internal const short SQL_TINYINT = -6;
   internal const short SQL_BIT = -7;
   internal const short SQL_WCHAR = -8;
   internal const short SQL_WVARCHAR = -9;
   internal const short SQL_WLONGVARCHAR = -10;
   internal const short SQL_GUID = -11;
   internal const short SQL_SS_VARIANT = -150;
   internal const short SQL_SS_UDT = -151;
   internal const short SQL_SS_XML = -152;
   internal const short SQL_SS_TIME2 = -154;
   internal const short SQL_SS_TIMESTAMPOFFSET = -155;

   private const string DATE_FORMAT = "yyyy-MM-dd";
   private const string DATE_TIME_FORMAT = "yyyy-MM-dd'T'HH:mm:ss.FFFFFFF";
   private const string DATE_TIME_OFFSET_FORMAT = "yyyy-MM-dd'T'HH:mm:ss.FFFFFFFzzz";
   private const string TIME_FORMAT = "c";

   /// <summary>
   /// Every type the source reads. Anything else (binary, rowversion, spatial and other CLR
   /// types, intervals, driver types nobody has tested) is left out and listed as skipped.
   /// </summary>
   private static readonly HashSet<short> SUPPORTED = new()
   {
      SQL_CHAR, SQL_VARCHAR, SQL_LONGVARCHAR, SQL_WCHAR, SQL_WVARCHAR, SQL_WLONGVARCHAR, SQL_SS_XML,
      SQL_NUMERIC, SQL_DECIMAL, SQL_INTEGER, SQL_SMALLINT, SQL_BIGINT, SQL_TINYINT, SQL_BIT,
      SQL_FLOAT, SQL_REAL, SQL_DOUBLE,
      SQL_DATE, SQL_TIME, SQL_TIMESTAMP, SQL_TYPE_DATE, SQL_TYPE_TIME, SQL_TYPE_TIMESTAMP, SQL_SS_TIME2, SQL_SS_TIMESTAMPOFFSET,
      SQL_GUID, SQL_SS_VARIANT,
   };

   /// <summary>Long text types: readable, but not usable in COUNT(DISTINCT) on most databases.</summary>
   private static readonly HashSet<short> LONG_TEXT = new() { SQL_LONGVARCHAR, SQL_WLONGVARCHAR, SQL_SS_XML, SQL_SS_VARIANT };

   #endregion Data Members

   #region Public Methods

   /// <summary>
   /// True when a column of this ODBC SQL type is read; false means it is skipped.
   /// </summary>
   /// <param name="sqlType">ODBC SQL type code (SQLColumns DATA_TYPE).</param>
   /// <returns>True when supported.</returns>
   public static bool IsSupported( short sqlType ) => SUPPORTED.Contains( sqlType );

   /// <summary>
   /// True for types System.Data.Odbc cannot map, which are fetched as the driver's text instead.
   /// </summary>
   /// <param name="sqlType">ODBC SQL type code.</param>
   /// <returns>True when the value is read with GetString.</returns>
   public static bool ReadsAsText( short sqlType ) => sqlType is SQL_SS_TIME2 or SQL_SS_TIMESTAMPOFFSET;

   /// <summary>
   /// True for long text, XML and variant columns, which cannot be compared for uniqueness.
   /// </summary>
   /// <param name="sqlType">ODBC SQL type code.</param>
   /// <returns>True for a long or opaque type.</returns>
   public static bool IsLongText( short sqlType ) => LONG_TEXT.Contains( sqlType );

   /// <summary>
   /// Reads and formats one cell of the current row.
   /// </summary>
   /// <param name="reader">Reader positioned on a row.</param>
   /// <param name="ordinal">Column position.</param>
   /// <param name="sqlType">The column's ODBC SQL type code.</param>
   /// <returns>The formatted value, or null for SQL NULL.</returns>
   public static string? Read( OdbcDataReader reader, int ordinal, short sqlType )
   {
      if( ReadsAsText( sqlType ) )
      {
         return ReadDriverText( reader, ordinal, sqlType );
      }

      try
      {
         return reader.IsDBNull( ordinal ) ? null : Format( reader.GetValue( ordinal ), sqlType );
      }
      catch( ArgumentException ) when( sqlType == SQL_SS_VARIANT )
      {
         // A sql_variant holding a datetimeoffset or time has no .NET mapping either.
         return ReadDriverText( reader, ordinal, sqlType );
      }
   }

   /// <summary>
   /// Formats a value the driver returned as a .NET object.
   /// </summary>
   /// <param name="value">The value (DBNull and null are SQL NULL).</param>
   /// <param name="sqlType">The column's ODBC SQL type code, which decides date-only output and padding trims.</param>
   /// <returns>The text, or null for NULL and for binary values.</returns>
   public static string? Format( object? value, short sqlType )
   {
      return value switch
      {
         null or DBNull => null,
         string text => sqlType is SQL_CHAR or SQL_WCHAR ? text.TrimEnd( ' ' ) : text,
         bool flag => flag ? "true" : "false",
         DateTime date when sqlType is SQL_TYPE_DATE or SQL_DATE => date.ToString( DATE_FORMAT, CultureInfo.InvariantCulture ),
         DateTime date => date.ToString( DATE_TIME_FORMAT, CultureInfo.InvariantCulture ),
         DateTimeOffset offset => offset.ToString( DATE_TIME_OFFSET_FORMAT, CultureInfo.InvariantCulture ),
         TimeSpan time => time.ToString( TIME_FORMAT, CultureInfo.InvariantCulture ),
         Guid guid => guid.ToString( "D" ),
         byte[] => null,
         IFormattable formattable => formattable.ToString( null, CultureInfo.InvariantCulture ),
         _ => value.ToString(),
      };
   }

   /// <summary>
   /// Normalizes a value the driver returned as text (datetimeoffset, time) to the same ISO
   /// shapes <see cref="Format"/> produces. Text that does not parse is kept as the driver sent it.
   /// </summary>
   /// <param name="text">The driver's text.</param>
   /// <param name="sqlType">The column's ODBC SQL type code.</param>
   /// <returns>The normalized text, or null for null.</returns>
   public static string? FormatDriverText( string? text, short sqlType )
   {
      if( text == null )
      {
         return null;
      }

      string trimmed = text.Trim();
      if( sqlType == SQL_SS_TIMESTAMPOFFSET && DateTimeOffset.TryParse( trimmed, CultureInfo.InvariantCulture, DateTimeStyles.None, out DateTimeOffset offset ) )
      {
         return Format( offset, sqlType );
      }

      if( sqlType == SQL_SS_TIME2 && TimeSpan.TryParse( trimmed, CultureInfo.InvariantCulture, out TimeSpan time ) )
      {
         return Format( time, sqlType );
      }

      return trimmed;
   }

   /// <summary>
   /// Maps a .NET field type to the closest ODBC SQL type, for the rare driver whose catalog does
   /// not describe a table that a query can still read.
   /// </summary>
   /// <param name="type">The reader's field type, or null when the driver's type is unknown.</param>
   /// <returns>The SQL type code; <see cref="SQL_BINARY"/> for anything that is not text-like.</returns>
   public static short SqlTypeOf( Type? type )
   {
      return Type.GetTypeCode( type ) switch
      {
         TypeCode.String => SQL_WVARCHAR,
         TypeCode.Boolean => SQL_BIT,
         TypeCode.DateTime => SQL_TYPE_TIMESTAMP,
         TypeCode.Decimal => SQL_DECIMAL,
         TypeCode.Double => SQL_DOUBLE,
         TypeCode.Single => SQL_REAL,
         TypeCode.Byte or TypeCode.SByte => SQL_TINYINT,
         TypeCode.Int16 or TypeCode.UInt16 => SQL_SMALLINT,
         TypeCode.Int32 or TypeCode.UInt32 => SQL_INTEGER,
         TypeCode.Int64 or TypeCode.UInt64 => SQL_BIGINT,
         _ when type == typeof( Guid ) => SQL_GUID,
         _ when type == typeof( TimeSpan ) => SQL_TYPE_TIME,
         _ => SQL_BINARY,
      };
   }

   #endregion Public Methods

   #region Private Methods

   /// <summary>
   /// Reads a cell as the driver's text and normalizes it. Used for types System.Data.Odbc
   /// cannot map: for those even IsDBNull throws, and GetString on a NULL cell fails with an
   /// InvalidCastException (DBNull is not a string), which is how NULL is recognized here.
   /// </summary>
   /// <param name="reader">Reader positioned on a row.</param>
   /// <param name="ordinal">Column position.</param>
   /// <param name="sqlType">The column's ODBC SQL type code.</param>
   /// <returns>The normalized text, or null for NULL.</returns>
   private static string? ReadDriverText( OdbcDataReader reader, int ordinal, short sqlType )
   {
      try
      {
         return FormatDriverText( reader.GetString( ordinal ), sqlType );
      }
      catch( InvalidCastException )
      {
         return null;
      }
   }

   #endregion Private Methods
}
