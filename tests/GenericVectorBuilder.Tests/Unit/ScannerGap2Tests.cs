using ClosedXML.Excel;
using GenericVectorBuilder.Core.Sources.Files;
using GenericVectorBuilder.Tests.Fakes;

namespace GenericVectorBuilder.Tests.Unit;

/// <summary>
/// Cross-origin duplicate tracking in the folder scan. Two files of one table that both hold the
/// same key value would otherwise produce one document key, so the scan reports which values of
/// the requested key columns appear in more than one accepted origin. Each test pins one rule:
/// what counts, what does not, and what is not kept in memory afterwards.
/// </summary>
public class ScannerGap2Tests
{
   #region Public Methods

   /// <summary>
   /// Id 5 in two accepted files is a cross-origin duplicate, and only that value is listed.
   /// Ids that appear in a single file must not be retained.
   /// </summary>
   [Fact]
   public void Scan_ValueInTwoAcceptedFiles_IsListedAndSingleOriginValuesAreNot()
   {
      using var folder = new TempFolder();
      folder.Write( "a.csv", "id,name\n1,one\n5,five\n" );
      folder.Write( "b.csv", "id,name\n5,again\n9,nine\n" );

      ScannedTable table = Scan( folder, "id" ).Tables.Single();

      Assert.Equal( new[] { "5" }, table.CrossOriginDuplicates["id"].OrderBy( v => v ) );
      Assert.Single( table.CrossOriginDuplicates );
   }

   /// <summary>
   /// The same data with the second file rejected for an unclosed quote has no cross-origin
   /// duplicate: a rejected origin does not count.
   /// </summary>
   [Fact]
   public void Scan_SecondFileRejected_ReportsNoDuplicates()
   {
      using var folder = new TempFolder();
      folder.Write( "a.csv", "id,name\n1,one\n5,five\n" );
      folder.Write( "b.csv", "id,name\n5,\"never closed\n9,nine\n" );

      FolderScanResult scan = Scan( folder, "id" );

      Assert.Contains( scan.Rejected, r => r.Origin == "b.csv" && r.Reason.Contains( "unclosed quote" ) );
      Assert.Empty( scan.Tables.Single().CrossOriginDuplicates );
   }

   /// <summary>
   /// A rejected file that sorts before the good ones must not pair up with them either.
   /// </summary>
   [Fact]
   public void Scan_RejectedFileSortingFirst_DoesNotPairWithGoodFiles()
   {
      using var folder = new TempFolder();
      folder.Write( "a.csv", "id,name\n5,\"never closed\n" );
      folder.Write( "b.csv", "id,name\n5,five\n" );
      folder.Write( "c.csv", "id,name\n6,six\n" );

      ScannedTable table = Scan( folder, "id" ).Tables.Single();

      Assert.Equal( 2, table.Origins.Count );
      Assert.Empty( table.CrossOriginDuplicates );
   }

   /// <summary>
   /// A value repeated inside one file is not cross-origin. A repeated value that also shows up in
   /// a second file is listed once.
   /// </summary>
   [Fact]
   public void Scan_RepeatInsideOneFile_IsNotCrossOrigin()
   {
      using var folder = new TempFolder();
      folder.Write( "a.csv", "id,name\n7,x\n7,y\n7,z\n8,w\n" );
      folder.Write( "b.csv", "id,name\n9,q\n8,v\n" );

      ScannedTable table = Scan( folder, "id" ).Tables.Single();

      Assert.Equal( new[] { "8" }, table.CrossOriginDuplicates["id"].OrderBy( v => v ) );
   }

   /// <summary>
   /// Duplicates inside a single-file table are never cross-origin, however many there are.
   /// </summary>
   [Fact]
   public void Scan_SingleFileTable_HasNoCrossOriginDuplicates()
   {
      using var folder = new TempFolder();
      folder.Write( "only.csv", "id,name\n1,a\n1,b\n2,c\n2,d\n" );

      Assert.Empty( Scan( folder, "id" ).Tables.Single().CrossOriginDuplicates );
   }

   /// <summary>
   /// A column that was not requested is not tracked, even when its values collide across files
   /// and its name looks like a key. Asking with the two-argument scan tracks nothing at all.
   /// </summary>
   [Fact]
   public void Scan_ColumnNotRequested_IsNotTracked()
   {
      using var folder = new TempFolder();
      folder.Write( "a.csv", "id,code,name\n1,X,a\n" );
      folder.Write( "b.csv", "id,code,name\n2,X,b\n" );

      ScannedTable table = Scan( folder, "id" ).Tables.Single();
      Assert.Empty( table.CrossOriginDuplicates );

      ScannedTable code = Scan( folder, "code" ).Tables.Single();
      Assert.Equal( new[] { "code" }, code.CrossOriginDuplicates.Keys );

      ScannedTable plain = new FolderScanner().Scan( folder.Path, CancellationToken.None ).Tables.Single();
      Assert.Empty( plain.CrossOriginDuplicates );
      Assert.Empty( new FolderScanner().Scan( folder.Path, CancellationToken.None, Array.Empty<string>() ).Tables.Single().CrossOriginDuplicates );
   }

   /// <summary>
   /// Names match the header ignoring case, and the result is keyed by the header's own spelling
   /// but looks up ignoring case, which is how the row mapper finds its key column.
   /// </summary>
   [Fact]
   public void Scan_ColumnNameMatchIgnoresCase()
   {
      using var folder = new TempFolder();
      folder.Write( "a.csv", "OrderId,name\n5,a\n" );
      folder.Write( "b.csv", "orderid,name\n5,b\n" );

      ScannedTable table = Scan( folder, "ORDERID" ).Tables.Single();

      Assert.Equal( "OrderId", table.CrossOriginDuplicates.Keys.Single() );
      Assert.Contains( "5", table.CrossOriginDuplicates["orderID"] );
   }

   /// <summary>
   /// Only tables whose header holds a requested column are tracked; a table without it is empty.
   /// </summary>
   [Fact]
   public void Scan_TableWithoutTheColumn_IsEmpty()
   {
      using var folder = new TempFolder();
      folder.Write( "x1.csv", "id,v\n1,a\n" );
      folder.Write( "x2.csv", "id,v\n1,b\n" );
      folder.Write( "y1.csv", "ref,w\n1,a\n" );
      folder.Write( "y2.csv", "ref,w\n1,b\n" );

      FolderScanResult scan = Scan( folder, "id" );

      Assert.Equal( 2, scan.Tables.Count );
      Assert.NotEmpty( scan.Tables.Single( t => t.Columns.Contains( "id" ) ).CrossOriginDuplicates );
      Assert.Empty( scan.Tables.Single( t => t.Columns.Contains( "ref" ) ).CrossOriginDuplicates );
   }

   /// <summary>
   /// Empty key cells and misaligned rows are not values: the row mapper never keys on them.
   /// </summary>
   [Fact]
   public void Scan_EmptyCellsAndSkippedRows_AreNotValues()
   {
      using var folder = new TempFolder();
      var a = new System.Text.StringBuilder( "id,name\n,blank\n" );
      var b = new System.Text.StringBuilder( "id,name\n,blank\n" );
      for( int i = 0; i < 40; i++ )
      {
         a.Append( $"a{i},n\n" );
         b.Append( $"b{i},n\n" );
      }

      a.Append( "77,x,extra\n" );
      b.Append( "77,y\n" );
      folder.Write( "a.csv", a.ToString() );
      folder.Write( "b.csv", b.ToString() );

      FolderScanResult scan = Scan( folder, "id" );

      Assert.Empty( scan.Rejected );
      Assert.Empty( scan.Tables.Single().CrossOriginDuplicates );
   }

   /// <summary>
   /// Two sheets of one workbook are two origins, so a value on both sheets is a duplicate.
   /// </summary>
   [Fact]
   public void Scan_ValueOnTwoSheets_IsCrossOrigin()
   {
      using var folder = new TempFolder();
      using( var book = new XLWorkbook() )
      {
         foreach( string sheet in new[] { "North", "South" } )
         {
            IXLWorksheet ws = book.AddWorksheet( sheet );
            ws.Cell( 1, 1 ).Value = "store_id";
            ws.Cell( 1, 2 ).Value = "city";
            ws.Cell( 2, 1 ).Value = 3;
            ws.Cell( 2, 2 ).Value = sheet;
            ws.Cell( 3, 1 ).Value = sheet == "North" ? 10 : 20;
            ws.Cell( 3, 2 ).Value = sheet;
         }

         book.SaveAs( Path.Combine( folder.Path, "stores.xlsx" ) );
      }

      ScannedTable table = Scan( folder, "store_id" ).Tables.Single();

      Assert.Equal( 2, table.Origins.Count );
      Assert.Equal( new[] { "3" }, table.CrossOriginDuplicates["store_id"].OrderBy( v => v ) );
   }

   /// <summary>
   /// Tracking is a side channel: tables, key suggestion, origins and rejects are identical to
   /// an untracked scan of the same folder.
   /// </summary>
   [Fact]
   public void Scan_Tracking_DoesNotChangeTheRestOfTheResult()
   {
      using var folder = new TempFolder();
      folder.Write( "a.csv", "id,name\n1,one\n2,two\n" );
      folder.Write( "b.csv", "id,name\n2,again\n3,three\n" );
      folder.Write( "c.csv", "id,name\n4,\"never closed\n" );
      folder.Write( "d.csv", "key,v\n1,a\n2,b\n" );

      FolderScanResult plain = new FolderScanner().Scan( folder.Path, CancellationToken.None );
      FolderScanResult tracked = Scan( folder, "id", "key" );

      Assert.Equal( plain.Tables.Select( t => ( t.Name, t.SuggestedKey, t.DataRows ) ), tracked.Tables.Select( t => ( t.Name, t.SuggestedKey, t.DataRows ) ) );
      Assert.Equal( plain.Rejected.Select( r => r.Origin ), tracked.Rejected.Select( r => r.Origin ) );
      Assert.Null( tracked.Tables.Single( t => t.Columns.Contains( "name" ) ).SuggestedKey );
      Assert.Equal( "key", tracked.Tables.Single( t => t.Columns.Contains( "v" ) ).SuggestedKey );
   }

   #endregion Public Methods

   #region Private Methods

   /// <summary>
   /// Scans a test folder while tracking the given key columns.
   /// </summary>
   /// <param name="folder">The folder.</param>
   /// <param name="columns">Key columns to track.</param>
   /// <returns>The scan result.</returns>
   private static FolderScanResult Scan( TempFolder folder, params string[] columns )
   {
      return new FolderScanner().Scan( folder.Path, CancellationToken.None, columns );
   }

   #endregion Private Methods
}
