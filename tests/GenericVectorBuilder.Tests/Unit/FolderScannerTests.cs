using System.Text;
using ClosedXML.Excel;
using GenericVectorBuilder.Core.Sources.Files;
using GenericVectorBuilder.Tests.Fakes;

namespace GenericVectorBuilder.Tests.Unit;

/// <summary>
/// The "200 CSVs in a folder" case: mixed delimiters and encodings collapse into a few
/// tables, broken files land in the reject list, and the rest still go through.
/// </summary>
public class FolderScannerTests
{
   #region Public Methods

   /// <summary>
   /// 195 good files in 3 header shapes (comma, pipe, tab; one shape with a BOM, one with
   /// Windows-1252 bytes) plus 5 broken files become exactly 3 tables and 5 rejects.
   /// </summary>
   [Fact]
   public void Scan_TwoHundredFiles_GroupsIntoThreeTablesAndRejectsTheBrokenFive()
   {
      using var folder = new TempFolder();
      for( int i = 0; i < 65; i++ )
      {
         folder.Write( $"orders/orders_{2020 + i / 12}_{i % 12 + 1:00}.csv", $"order_id,customer,total\n{i}01,Acme,10.50\n{i}02,Globex,99.00\n" );
         byte[] bom = new byte[] { 0xEF, 0xBB, 0xBF }.Concat( Encoding.UTF8.GetBytes( $"ticket_id|subject|status\nT{i}a|Printer jam|open\nT{i}b|Login loop|closed\n" ) ).ToArray();
         folder.WriteBytes( $"tickets/tickets-{i:000}.psv", bom );
         folder.WriteBytes( $"staff/staff {i}.tsv", Encoding.Latin1.GetBytes( $"employee_id\tname\tcity\nE{i}\tRené {i}\tQuébec\n" ) );
      }

      folder.Write( "broken/ragged.csv", "a,b\n1,2,3\n4,5,6\n7,8,9\n" );
      folder.WriteBytes( "broken/junk.csv", new byte[] { 0x00, 0x01, 0x02, 0xFF, 0x00 } );
      folder.Write( "broken/empty.csv", string.Empty );
      folder.Write( "broken/header_only.csv", "x,y,z\n" );
      folder.Write( "broken/unclosed.csv", "p,q\n1,\"never closed\n2,3\n" );
      folder.Write( "notes/readme.md", "# not data" );

      FolderScanResult scan = new FolderScanner().Scan( folder.Path, CancellationToken.None );

      Assert.Equal( 3, scan.Tables.Count );
      Assert.Equal( new[] { "orders", "staff", "tickets" }, scan.Tables.Select( t => t.Name ).OrderBy( n => n ) );
      Assert.All( scan.Tables, t => Assert.Equal( 65, t.Origins.Count ) );
      Assert.Equal( 5, scan.Rejected.Count );
      Assert.Contains( scan.Ignored, i => i.Origin == "notes/readme.md" );
      Assert.Contains( scan.Rejected, r => r.Origin == "broken/unclosed.csv" && r.Reason.Contains( "unclosed quote" ) );
      Assert.Contains( scan.Rejected, r => r.Origin == "broken/header_only.csv" && r.Reason == "no data rows" );
   }

   /// <summary>
   /// Each table gets the right row count, a unique key suggestion, and correctly decoded text.
   /// </summary>
   [Fact]
   public void Scan_SuggestsUniqueKeyAndDecodesEncodings()
   {
      using var folder = new TempFolder();
      folder.Write( "a.csv", "order_id,customer_id,total\n1,7,5\n2,7,6\n" );
      folder.Write( "b.csv", "order_id,customer_id,total\n3,8,1\n" );
      folder.WriteBytes( "c.tsv", Encoding.Latin1.GetBytes( "name\tcity\nRené\tQuébec\n" ) );

      FolderScanResult scan = new FolderScanner().Scan( folder.Path, CancellationToken.None );

      ScannedTable orders = scan.Tables.Single( t => t.Columns.Contains( "order_id" ) );
      Assert.Equal( 3, orders.DataRows );
      Assert.Equal( "order_id", orders.SuggestedKey );
      ScannedTable people = scan.Tables.Single( t => t.Columns.Contains( "city" ) );
      Assert.Equal( "Québec", people.Sample[0][1] );
   }

   /// <summary>
   /// A key column that repeats across two files is not suggested.
   /// </summary>
   [Fact]
   public void Scan_DoesNotSuggestKeyThatRepeatsAcrossFiles()
   {
      using var folder = new TempFolder();
      folder.Write( "x1.csv", "id,v\n1,a\n2,b\n" );
      folder.Write( "x2.csv", "id,v\n2,c\n" );
      Assert.Null( new FolderScanner().Scan( folder.Path, CancellationToken.None ).Tables.Single().SuggestedKey );
   }

   /// <summary>
   /// Each visible sheet of a workbook is its own origin; a sheet with the same header as a
   /// CSV joins that CSV's table, and hidden sheets are ignored.
   /// </summary>
   [Fact]
   public void Scan_ReadsXlsxSheetsAndGroupsThemWithMatchingCsv()
   {
      using var folder = new TempFolder();
      folder.Write( "parts.csv", "part_no,description\nA1,Bolt\n" );
      using( var book = new XLWorkbook() )
      {
         IXLWorksheet parts = book.AddWorksheet( "Parts" );
         parts.Cell( 1, 1 ).Value = "part_no";
         parts.Cell( 1, 2 ).Value = "description";
         parts.Cell( 2, 1 ).Value = "B2";
         parts.Cell( 2, 2 ).Value = "Washer";
         IXLWorksheet hidden = book.AddWorksheet( "Secret" );
         hidden.Cell( 1, 1 ).Value = "x";
         hidden.Hide();
         book.SaveAs( Path.Combine( folder.Path, "book.xlsx" ) );
      }

      FolderScanResult scan = new FolderScanner().Scan( folder.Path, CancellationToken.None );

      ScannedTable table = scan.Tables.Single();
      Assert.Equal( 2, table.DataRows );
      Assert.Contains( table.Origins, o => o.Spec.Origin == "book.xlsx#Parts" );
      Assert.Contains( scan.Ignored, i => i.Origin == "book.xlsx#Secret" && i.Reason == "hidden sheet" );
   }

   /// <summary>
   /// File names become sensible table names.
   /// </summary>
   [Theory]
   [InlineData( "orders_2024_01.csv", "orders" )]
   [InlineData( "2024-01-orders.csv", "orders" )]
   [InlineData( "sub/Sales Report Q1.csv", "sales_report" )]
   [InlineData( "book.xlsx#Sheet1", "book" )]
   [InlineData( "book.xlsx#Customers", "customers" )]
   [InlineData( "123.csv", "table" )]
   public void Stem_MakesReadableTableNames( string origin, string expected )
   {
      Assert.Equal( expected, FolderScanner.Stem( origin ) );
   }

   #endregion Public Methods
}
