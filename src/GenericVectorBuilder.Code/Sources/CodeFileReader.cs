using System.Text;

namespace GenericVectorBuilder.Code.Sources;

/// <summary>
/// Reads a source file as text, or says it is binary.
/// Why the decoding rules: a byte-order mark decides the encoding when there is one. Without
/// one, a NUL byte in the first 8,000 bytes means binary (the same test git uses), valid UTF-8
/// is read as UTF-8, and anything else is read as Latin-1, which maps every byte to a
/// character. So an old Windows-1252 file loses no characters instead of filling up with
/// replacement marks, and the same bytes always give the same text.
/// </summary>
internal static class CodeFileReader
{
   #region Data Members

   private const int BINARY_PROBE_BYTES = 8000;
   private static readonly UTF8Encoding STRICT_UTF8 = new( encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true );

   #endregion Data Members

   #region Public Methods

   /// <summary>
   /// Decodes file bytes into text.
   /// </summary>
   /// <param name="bytes">The whole file.</param>
   /// <returns>The text, or null when the file is binary.</returns>
   public static string? Decode( byte[] bytes )
   {
      ReadOnlySpan<byte> span = bytes;
      if( span.StartsWith( new byte[] { 0xEF, 0xBB, 0xBF } ) )
      {
         return Utf8OrLatin1( bytes, 3 );
      }

      if( span.StartsWith( new byte[] { 0xFF, 0xFE, 0x00, 0x00 } ) )
      {
         return Encoding.UTF32.GetString( bytes, 4, bytes.Length - 4 );
      }

      if( span.StartsWith( new byte[] { 0xFF, 0xFE } ) )
      {
         return Encoding.Unicode.GetString( bytes, 2, bytes.Length - 2 );
      }

      if( span.StartsWith( new byte[] { 0xFE, 0xFF } ) )
      {
         return Encoding.BigEndianUnicode.GetString( bytes, 2, bytes.Length - 2 );
      }

      return span[..Math.Min( span.Length, BINARY_PROBE_BYTES )].Contains( (byte)0 ) ? null : Utf8OrLatin1( bytes, 0 );
   }

   #endregion Public Methods

   #region Private Methods

   /// <summary>
   /// Decodes as strict UTF-8, falling back to Latin-1 when the bytes are not valid UTF-8.
   /// </summary>
   /// <param name="bytes">File bytes.</param>
   /// <param name="offset">Bytes to skip (a byte-order mark).</param>
   /// <returns>The text.</returns>
   private static string Utf8OrLatin1( byte[] bytes, int offset )
   {
      try
      {
         return STRICT_UTF8.GetString( bytes, offset, bytes.Length - offset );
      }
      catch( DecoderFallbackException )
      {
         return Encoding.Latin1.GetString( bytes, offset, bytes.Length - offset );
      }
   }

   #endregion Private Methods
}
