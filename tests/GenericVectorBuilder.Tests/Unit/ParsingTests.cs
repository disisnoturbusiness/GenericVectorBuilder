using System.Text;
using GenericVectorBuilder.Core.Sources.Files;

namespace GenericVectorBuilder.Tests.Unit;

/// <summary>
/// Parser and sniffer behaviour on the file shapes real folders contain.
/// </summary>
public class ParsingTests
{
   #region Public Methods

   /// <summary>
   /// Quoted fields keep their delimiters, doubled quotes and line breaks.
   /// </summary>
   [Fact]
   public void Parser_HandlesQuotesEscapesAndEmbeddedNewlines()
   {
      var parser = new DelimitedParser( new StringReader( "a,\"b,1\",\"say \"\"hi\"\"\"\r\n\"multi\nline\",x,y\n" ), ',' );
      Assert.Equal( new[] { "a", "b,1", "say \"hi\"" }, parser.ReadRecord() );
      Assert.Equal( new[] { "multi\nline", "x", "y" }, parser.ReadRecord() );
      Assert.Null( parser.ReadRecord() );
      Assert.False( parser.UnterminatedQuote );
   }

   /// <summary>
   /// An unclosed quote is reported instead of silently swallowing the rest of the file.
   /// </summary>
   [Fact]
   public void Parser_FlagsUnterminatedQuote()
   {
      var parser = new DelimitedParser( new StringReader( "a,b\n1,\"oops\n2,3\n4,5\n" ), ',' );
      parser.ReadRecord();
      parser.ReadRecord();
      Assert.True( parser.UnterminatedQuote );
      Assert.Equal( 2, parser.RecordStartLine );
   }

   /// <summary>
   /// Pipe wins over comma when commas only appear inside the text column.
   /// </summary>
   [Fact]
   public void Sniffer_PicksPipeOverIncidentalCommas()
   {
      string text = "id|name|note\n1|Ann|likes red, blue\n2|Bob|tall, quiet, kind\n3|Cy|none\n";
      FileProfile profile = Sniff( text );
      Assert.Equal( '|', profile.Delimiter );
      Assert.True( profile.HasHeader );
      Assert.Equal( new[] { "id", "name", "note" }, profile.Columns );
   }

   /// <summary>
   /// Tab-delimited with no header gets generated column names.
   /// </summary>
   [Fact]
   public void Sniffer_DetectsTabAndMissingHeader()
   {
      FileProfile profile = Sniff( "1\t2.5\tx\n2\t3.5\ty\n" );
      Assert.Equal( '\t', profile.Delimiter );
      Assert.False( profile.HasHeader );
      Assert.Equal( new[] { "column1", "column2", "column3" }, profile.Columns );
   }

   /// <summary>
   /// Windows-1252 bytes (an e-acute as 0xE9) are decoded as 1252, not mangled as UTF-8.
   /// </summary>
   [Fact]
   public void Sniffer_FallsBackToWindows1252()
   {
      byte[] bytes = Encoding.Latin1.GetBytes( "name,city\nRené,Québec\n" );
      SniffResult result = FileSniffer.SniffBytes( bytes, truncated: false );
      Assert.Equal( 1252, result.Profile!.Encoding.CodePage );
   }

   /// <summary>
   /// A UTF-8 BOM is recognized and does not leak into the first column name.
   /// </summary>
   [Fact]
   public void Sniffer_StripsUtf8Bom()
   {
      byte[] bytes = new byte[] { 0xEF, 0xBB, 0xBF }.Concat( Encoding.UTF8.GetBytes( "id,name\n1,a\n" ) ).ToArray();
      FileProfile profile = FileSniffer.SniffBytes( bytes, truncated: false ).Profile!;
      Assert.Equal( "id", profile.Columns[0] );
   }

   /// <summary>
   /// Binary junk and empty files are rejected with a reason.
   /// </summary>
   [Fact]
   public void Sniffer_RejectsBinaryAndEmpty()
   {
      Assert.Equal( "binary content, not a text file", FileSniffer.SniffBytes( new byte[] { 0x50, 0x4B, 0x03, 0x04, 0x00, 0x01 }, false ).RejectReason );
      Assert.Equal( "empty file", FileSniffer.SniffBytes( Encoding.UTF8.GetBytes( "  \n\n" ), false ).RejectReason );
   }

   /// <summary>
   /// A failed sqlcmd export (error text instead of rows) is rejected with the SQL message,
   /// not read as a table whose header is "Msg 208, Level 16".
   /// </summary>
   [Fact]
   public void Sniffer_RejectsSqlErrorOutput()
   {
      byte[] bytes = Encoding.UTF8.GetBytes( "Msg 208, Level 16, State 1, Server BOX, Line 1\r\nInvalid object name 'AdventureWorks2025.dbo.Address'.\r\n" );
      Assert.Equal( "SQL Server error message, not data: Invalid object name 'AdventureWorks2025.dbo.Address'.", FileSniffer.SniffBytes( bytes, false ).RejectReason );
   }

   #endregion Public Methods

   #region Private Methods

   /// <summary>
   /// Sniffs a UTF-8 string and asserts it was accepted.
   /// </summary>
   /// <param name="text">File content.</param>
   /// <returns>The profile.</returns>
   private static FileProfile Sniff( string text )
   {
      SniffResult result = FileSniffer.SniffBytes( Encoding.UTF8.GetBytes( text ), truncated: false );
      Assert.Null( result.RejectReason );
      return result.Profile!;
   }

   #endregion Private Methods
}
