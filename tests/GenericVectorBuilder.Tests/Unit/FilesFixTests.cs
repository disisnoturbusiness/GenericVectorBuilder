using System.IO.Compression;
using System.Text;
using ClosedXML.Excel;
using GenericVectorBuilder.Core.Contracts;
using GenericVectorBuilder.Core.Sources.Files;
using GenericVectorBuilder.Tests.Fakes;

namespace GenericVectorBuilder.Tests.Unit;

/// <summary>
/// Regression tests for the 2026-10-02 review findings in the file-reading lane: symlinks,
/// unreadable folders, lock files, encoding detection, header tolerance, Excel number
/// formatting, table naming and table-name stability.
/// </summary>
public class FilesFixTests
{
   #region Public Methods

   /// <summary>
   /// A directory link that points back at the root is not followed, so one real file is one
   /// origin and not forty.
   /// </summary>
   [Fact]
   public void Scan_DoesNotFollowDirectorySymlinkLoops()
   {
      using var folder = new TempFolder();
      folder.Write( "a/data.csv", "id,name\n1,Hammer\n2,Drill\n" );
      if( !TryLinkDirectory( Path.Combine( folder.Path, "a", "loop" ), folder.Path ) )
      {
         return;
      }

      FolderScanResult scan = new FolderScanner().Scan( folder.Path, CancellationToken.None );

      Assert.Single( scan.Tables.Single().Origins );
      Assert.Equal( 2, scan.TotalRows );
      Assert.Contains( scan.Ignored, i => i.Origin == "a/loop" && i.Reason.Contains( "link" ) );
   }

   /// <summary>
   /// A link to a folder outside the scanned root and a link to a file outside it are both
   /// left alone, so nothing outside the allowed roots is read.
   /// </summary>
   [Fact]
   public void Scan_DoesNotReadThroughSymlinksToOutsideFilesOrFolders()
   {
      using var outside = new TempFolder();
      outside.Write( "secret.csv", "user,pass\nroot,hunter2\n" );
      outside.Write( "one.txt", "user,pass\nroot,hunter2\nbob,pw\n" );
      using var folder = new TempFolder();
      folder.Write( "keep.csv", "id,name\n1,Hammer\n" );
      bool linkedDirectory = TryLinkDirectory( Path.Combine( folder.Path, "linkdir" ), outside.Path );
      bool linkedFile = TryLinkFile( Path.Combine( folder.Path, "leak.txt" ), Path.Combine( outside.Path, "one.txt" ) );
      if( !linkedDirectory || !linkedFile )
      {
         return;
      }

      FolderScanResult scan = new FolderScanner().Scan( folder.Path, CancellationToken.None );

      ScannedTable only = scan.Tables.Single();
      Assert.Equal( new[] { "id", "name" }, only.Columns );
      Assert.Empty( scan.Rejected );
   }

   /// <summary>
   /// A subfolder the scanner cannot open is reported as a rejected entry instead of vanishing.
   /// </summary>
   [Fact]
   public void Scan_ReportsUnreadableSubfolderAsRejected()
   {
      if( OperatingSystem.IsWindows() )
      {
         return;
      }

      using var folder = new TempFolder();
      folder.Write( "top.csv", "id,name\n1,Hammer\n" );
      folder.Write( "sub/inner.csv", "id,name\n2,Drill\n" );
      string sub = Path.Combine( folder.Path, "sub" );
      File.SetUnixFileMode( sub, UnixFileMode.None );
      try
      {
         if( CanListDirectory( sub ) )
         {
            return;
         }

         FolderScanResult scan = new FolderScanner().Scan( folder.Path, CancellationToken.None );

         Assert.Contains( scan.Rejected, r => r.Origin == "sub" && r.Reason.Contains( "folder" ) );
         Assert.Equal( 1, scan.TotalRows );
      }
      finally
      {
         File.SetUnixFileMode( sub, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute );
      }
   }

   /// <summary>
   /// A root folder that cannot be listed fails the scan with a clear message instead of
   /// looking like an empty folder (which would read as "everything was removed").
   /// </summary>
   [Fact]
   public void Scan_UnreadableRootFolder_Throws()
   {
      if( OperatingSystem.IsWindows() )
      {
         return;
      }

      using var folder = new TempFolder();
      folder.Write( "top.csv", "id,name\n1,Hammer\n" );
      File.SetUnixFileMode( folder.Path, UnixFileMode.None );
      try
      {
         if( CanListDirectory( folder.Path ) )
         {
            return;
         }

         var ex = Assert.Throws<DirectoryNotFoundException>( () => new FolderScanner().Scan( folder.Path, CancellationToken.None ) );

         Assert.Contains( "cannot be read", ex.Message );
      }
      finally
      {
         File.SetUnixFileMode( folder.Path, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute );
      }
   }

   /// <summary>
   /// Office lock files (~$book.xlsx) are skipped, not listed as unreadable workbooks.
   /// </summary>
   [Fact]
   public void Scan_SkipsOfficeLockFiles()
   {
      using var folder = new TempFolder();
      folder.Write( "keep.csv", "id,name\n1,Hammer\n" );
      folder.WriteBytes( "~$book.xlsx", new byte[] { 0x01, 0x05, 0x44, 0x61, 0x6E } );
      folder.Write( "~$notes.csv", "x" );

      FolderScanResult scan = new FolderScanner().Scan( folder.Path, CancellationToken.None );

      Assert.Empty( scan.Rejected );
      Assert.DoesNotContain( scan.Ignored, i => i.Origin.Contains( "~$" ) );
      Assert.Single( scan.Tables.Single().Origins );
   }

   /// <summary>
   /// A Windows-1252 file that is plain ASCII for the first 64 KB and has an accent after that
   /// is decoded as 1252, and the accented value reads back intact.
   /// </summary>
   [Fact]
   public void Sniff_ChecksTheWholeFileForUtf8_NotJustTheFirst64KB()
   {
      using var folder = new TempFolder();
      var text = new StringBuilder( "id,name\n" );
      for( int i = 1; i <= 9000; i++ )
      {
         text.Append( i ).Append( ",Customer " ).Append( i ).Append( '\n' );
      }

      text.Append( "9001,René\n" );
      string path = folder.WriteBytes( "big.csv", Encoding.Latin1.GetBytes( text.ToString() ) );
      Assert.True( new FileInfo( path ).Length > 64 * 1024 );

      SniffResult result = FileSniffer.Sniff( path );

      Assert.Equal( 1252, result.Profile!.Encoding.CodePage );
      FolderScanResult scan = new FolderScanner().Scan( folder.Path, CancellationToken.None );
      OriginSpec spec = scan.Tables.Single().Origins.Single().Spec;
      string? last = OriginReader.Read( spec ).Last().Values[1];
      Assert.Equal( "René", last );
   }

   /// <summary>
   /// A genuine UTF-8 file over 64 KB (accents throughout, including across the 64 KB cut) stays UTF-8.
   /// </summary>
   [Fact]
   public void Sniff_KeepsLargeValidUtf8AsUtf8()
   {
      using var folder = new TempFolder();
      var text = new StringBuilder( "id,name\n" );
      for( int i = 1; i <= 6000; i++ )
      {
         text.Append( i ).Append( ",René über " ).Append( i ).Append( '\n' );
      }

      string path = folder.Write( "big8.csv", text.ToString() );
      Assert.True( new FileInfo( path ).Length > 64 * 1024 );

      Assert.Equal( 65001, FileSniffer.Sniff( path ).Profile!.Encoding.CodePage );
   }

   /// <summary>
   /// A multi-byte UTF-8 character that straddles the 64 KB sample edge is a split character,
   /// not bad encoding, so the file stays UTF-8.
   /// </summary>
   [Fact]
   public void Sniff_AcceptsUtf8CharacterSplitAcrossTheSampleEdge()
   {
      using var folder = new TempFolder();
      var text = new StringBuilder( "id,name\n" );
      for( int i = 1; i <= 3000; i++ )
      {
         text.Append( i ).Append( ",Row " ).Append( i ).Append( '\n' );
      }

      int used = Encoding.UTF8.GetByteCount( text.ToString() );
      int padding = ( 64 * 1024 ) - 1 - used - "9999,".Length;
      Assert.True( padding > 0 );
      text.Append( "9999," ).Append( 'a', padding ).Append( "\u00E9 end\nlast,row\n" );
      string path = folder.Write( "edge.csv", text.ToString() );

      FileProfile profile = FileSniffer.Sniff( path ).Profile!;

      Assert.Equal( 65001, profile.Encoding.CodePage );
   }

   /// <summary>
   /// If a UTF-8 file changes after the scan and now holds invalid bytes, the reader refuses it
   /// with a plain message instead of embedding U+FFFD.
   /// </summary>
   [Fact]
   public void Reader_RejectsInvalidUtf8InsteadOfReplacingIt()
   {
      using var folder = new TempFolder();
      string path = folder.WriteBytes( "late.csv", Encoding.Latin1.GetBytes( "id,name\n1,René\n" ) );
      var profile = new FileProfile( new UTF8Encoding( false ), ',', true, new[] { "id", "name" } );
      var spec = new OriginSpec( "late.csv", path, OriginKind.Delimited, profile, null );

      var ex = Assert.Throws<MalformedFileException>( () => OriginReader.Read( spec ).ToList() );

      Assert.Contains( "encoding", ex.Message );
   }

   /// <summary>
   /// A trailing delimiter on the header row no longer turns the header into data.
   /// </summary>
   [Fact]
   public void Sniffer_AcceptsHeaderWithTrailingDelimiter()
   {
      FileProfile profile = Sniff( "id,name,\n1,Hammer,\n2,Drill,\n" );

      Assert.True( profile.HasHeader );
      Assert.Equal( new[] { "id", "name" }, profile.Columns );
   }

   /// <summary>
   /// Duplicate header names are made unique and the row is still a header.
   /// </summary>
   [Fact]
   public void Sniffer_AcceptsHeaderWithDuplicateNames()
   {
      FileProfile profile = Sniff( "id,name,name\n1,Hammer,Big\n2,Drill,Small\n" );

      Assert.True( profile.HasHeader );
      Assert.Equal( new[] { "id", "name", "name_2" }, profile.Columns );
   }

   /// <summary>
   /// A blank cell inside the header gets a generated name.
   /// </summary>
   [Fact]
   public void Sniffer_AcceptsHeaderWithBlankCell()
   {
      FileProfile profile = Sniff( "id,,name\n1,x,Hammer\n2,y,Drill\n" );

      Assert.True( profile.HasHeader );
      Assert.Equal( new[] { "id", "column2", "name" }, profile.Columns );
   }

   /// <summary>
   /// A first row with a numeric cell is still treated as data, not as a header.
   /// </summary>
   [Fact]
   public void Sniffer_StillRejectsNumericFirstRowAsHeader()
   {
      Assert.False( Sniff( "Region,2022,2023\nNorth,5,6\nSouth,7,8\n" ).HasHeader );
      Assert.False( FileSniffer.LooksLikeHeader( new string?[] { "id", "name", "7" } ) );
   }

   /// <summary>
   /// An Excel table that starts in column B (blank first header cell) keeps its header row.
   /// </summary>
   [Fact]
   public void Scan_WorkbookTableStartingInColumnB_KeepsHeader()
   {
      using var folder = new TempFolder();
      using( var book = new XLWorkbook() )
      {
         IXLWorksheet sheet = book.AddWorksheet( "Parts" );
         sheet.Cell( 1, 2 ).Value = "part_no";
         sheet.Cell( 1, 3 ).Value = "description";
         sheet.Cell( 2, 2 ).Value = "A1";
         sheet.Cell( 2, 3 ).Value = "Bolt";
         book.SaveAs( Path.Combine( folder.Path, "book.xlsx" ) );
      }

      ScannedTable table = new FolderScanner().Scan( folder.Path, CancellationToken.None ).Tables.Single();

      Assert.Equal( new[] { "column1", "part_no", "description" }, table.Columns );
      Assert.Equal( 1, table.DataRows );
   }

   /// <summary>
   /// A file that starts with blank or whitespace-only lines is read normally.
   /// </summary>
   [Theory]
   [InlineData( "\nid,name\n1,Hammer\n2,Drill\n" )]
   [InlineData( "  \nid,name\n1,Hammer\n2,Drill\n" )]
   [InlineData( "\r\n\r\n\r\nid,name\r\n1,Hammer\r\n2,Drill\r\n" )]
   public void Scan_SkipsLeadingBlankLinesWhenSniffing( string content )
   {
      using var folder = new TempFolder();
      folder.Write( "parts.csv", content );

      FolderScanResult scan = new FolderScanner().Scan( folder.Path, CancellationToken.None );

      Assert.Empty( scan.Rejected );
      ScannedTable table = scan.Tables.Single();
      Assert.Equal( new[] { "id", "name" }, table.Columns );
      Assert.Equal( 2, table.DataRows );
   }

   /// <summary>
   /// Whole numbers of 16 digits keep every digit and never turn into scientific notation, so
   /// adjacent ids stay distinct. Fractions stay plain, and time-only cells read as a time.
   /// ClosedXML itself writes numbers with 15 digits, so the exact digits are patched into the
   /// sheet XML afterwards, the way a writer that stores full precision would leave them.
   /// </summary>
   [Fact]
   public void Reader_FormatsExcelNumbersAndTimesCleanly()
   {
      using var folder = new TempFolder();
      string path = Path.Combine( folder.Path, "book.xlsx" );
      using( var book = new XLWorkbook() )
      {
         IXLWorksheet sheet = book.AddWorksheet( "Data" );
         string[] headers = { "id", "small", "ratio", "at", "hits", "clock" };
         for( int c = 0; c < headers.Length; c++ )
         {
            sheet.Cell( 1, c + 1 ).Value = headers[c];
         }

         sheet.Cell( 2, 1 ).Value = 7000001d;
         sheet.Cell( 2, 2 ).Value = 0.0000123d;
         sheet.Cell( 2, 3 ).Value = 12.5d;
         sheet.Cell( 2, 4 ).Value = new TimeSpan( 9, 30, 0 );
         sheet.Cell( 2, 5 ).Value = 42d;
         sheet.Cell( 2, 6 ).Value = new TimeSpan( 9, 30, 0 );
         sheet.Cell( 2, 6 ).Style.DateFormat.Format = "hh:mm:ss";
         sheet.Cell( 3, 1 ).Value = 7000002d;
         sheet.Cell( 3, 2 ).Value = 1.5e-9d;
         sheet.Cell( 3, 3 ).Value = 0.3d;
         sheet.Cell( 3, 4 ).Value = new TimeSpan( 18, 5, 9 );
         sheet.Cell( 3, 5 ).Value = 7000003d;
         sheet.Cell( 3, 6 ).Value = new TimeSpan( 18, 5, 9 );
         sheet.Cell( 3, 6 ).Style.DateFormat.Format = "hh:mm:ss";
         book.SaveAs( path );
      }

      PatchSheetXml( path, ( "<x:v>7000001</x:v>", "<x:v>1234567890123456</x:v>" ), ( "<x:v>7000002</x:v>", "<x:v>1234567890123457</x:v>" ),
         ( "<x:v>7000003</x:v>", "<x:v>123456789012345678</x:v>" ) );
      ScannedTable table = new FolderScanner().Scan( folder.Path, CancellationToken.None ).Tables.Single();
      List<OriginRow> rows = OriginReader.Read( table.Origins.Single().Spec ).ToList();

      Assert.Equal( "1234567890123456", rows[0].Values[0] );
      Assert.Equal( "1234567890123457", rows[1].Values[0] );
      Assert.Equal( "0.0000123", rows[0].Values[1] );
      Assert.Equal( "0.0000000015", rows[1].Values[1] );
      Assert.Equal( "12.5", rows[0].Values[2] );
      Assert.Equal( "0.3", rows[1].Values[2] );
      Assert.Equal( "09:30:00", rows[0].Values[3] );
      Assert.Equal( "18:05:09", rows[1].Values[3] );
      Assert.Equal( "42", rows[0].Values[4] );
      Assert.Equal( "123456789012345680", rows[1].Values[4] );
      Assert.Equal( "09:30:00", rows[0].Values[5] );
      Assert.Equal( "18:05:09", rows[1].Values[5] );
   }

   /// <summary>
   /// Date-only and date-time cells keep their existing ISO form.
   /// </summary>
   [Fact]
   public void Reader_KeepsDateFormatsUnchanged()
   {
      using var folder = new TempFolder();
      using( var book = new XLWorkbook() )
      {
         IXLWorksheet sheet = book.AddWorksheet( "Data" );
         sheet.Cell( 1, 1 ).Value = "day";
         sheet.Cell( 1, 2 ).Value = "stamp";
         sheet.Cell( 2, 1 ).Value = new DateTime( 2024, 1, 5 );
         sheet.Cell( 2, 2 ).Value = new DateTime( 2024, 1, 5, 14, 30, 0 );
         book.SaveAs( Path.Combine( folder.Path, "book.xlsx" ) );
      }

      ScannedTable table = new FolderScanner().Scan( folder.Path, CancellationToken.None ).Tables.Single();
      OriginRow row = OriginReader.Read( table.Origins.Single().Spec ).Single();

      Assert.Equal( "2024-01-05", row.Values[0] );
      Assert.Equal( "2024-01-05 14:30:00", row.Values[1] );
   }

   /// <summary>
   /// A '#' in a CSV file or folder name is not a sheet separator, and letters outside A to Z
   /// stay in the table name.
   /// </summary>
   [Theory]
   [InlineData( "orders #1.csv", "orders" )]
   [InlineData( "Q1 #2/sales.csv", "sales" )]
   [InlineData( "reports #3/Inventory Levels.csv", "inventory_levels" )]
   [InlineData( "résumé.csv", "résumé" )]
   [InlineData( "データ.csv", "データ" )]
   [InlineData( "my #1.xlsx#Customers", "customers" )]
   [InlineData( "my #1.xlsx#Sheet2", "my" )]
   [InlineData( "book.xlsx#Q#1 totals", "totals" )]
   public void Stem_OnlySplitsOnHashForWorkbookOrigins( string origin, string expected )
   {
      Assert.Equal( expected, FolderScanner.Stem( origin ) );
   }

   /// <summary>
   /// A workbook whose first sheet is named Sheet1 and whose file name contains '#' still takes
   /// its name from the file when scanned for real.
   /// </summary>
   [Fact]
   public void Scan_WorkbookWithHashInFileName_IsNamedFromTheFile()
   {
      using var folder = new TempFolder();
      using( var book = new XLWorkbook() )
      {
         IXLWorksheet sheet = book.AddWorksheet( "Sheet1" );
         sheet.Cell( 1, 1 ).Value = "part_no";
         sheet.Cell( 2, 1 ).Value = "A1";
         book.SaveAs( Path.Combine( folder.Path, "parts #2.xlsx" ) );
      }

      FolderScanResult scan = new FolderScanner().Scan( folder.Path, CancellationToken.None );

      Assert.Equal( "parts", scan.Tables.Single().Name );
   }

   /// <summary>
   /// Adding another folder that holds a same-named file does not shuffle the names of the
   /// tables that were already there.
   /// </summary>
   [Fact]
   public void Scan_TableNamesDoNotChangeWhenAnotherSameNamedFileAppears()
   {
      using var folder = new TempFolder();
      folder.Write( "a/orders.csv", "id,name\n1,x\n" );
      folder.Write( "b/orders.csv", "id,city\n1,y\n" );
      Dictionary<string, string> before = NameByColumns( new FolderScanner().Scan( folder.Path, CancellationToken.None ) );

      folder.Write( "0/orders.csv", "id,total\n1,z\n" );
      Dictionary<string, string> after = NameByColumns( new FolderScanner().Scan( folder.Path, CancellationToken.None ) );

      Assert.Equal( 3, after.Count );
      Assert.Equal( before["id|name"], after["id|name"] );
      Assert.Equal( before["id|city"], after["id|city"] );
      Assert.Equal( 3, after.Values.Distinct( StringComparer.OrdinalIgnoreCase ).Count() );
   }

   /// <summary>
   /// Table names do not depend on the order files happen to be found in.
   /// </summary>
   [Fact]
   public void Scan_TableNamesDoNotDependOnFilePathOrder()
   {
      using var one = new TempFolder();
      one.Write( "a/orders.csv", "id,name\n1,x\n" );
      one.Write( "b/orders.csv", "id,city\n1,y\n" );
      using var two = new TempFolder();
      two.Write( "y/orders.csv", "id,name\n1,x\n" );
      two.Write( "x/orders.csv", "id,city\n1,y\n" );

      Dictionary<string, string> first = NameByColumns( new FolderScanner().Scan( one.Path, CancellationToken.None ) );
      Dictionary<string, string> second = NameByColumns( new FolderScanner().Scan( two.Path, CancellationToken.None ) );

      Assert.Equal( first["id|name"], second["id|name"] );
      Assert.Equal( first["id|city"], second["id|city"] );
   }

   /// <summary>
   /// UTF-16 text with no byte order mark gets an accurate reason, not "binary content".
   /// </summary>
   [Fact]
   public void Sniffer_ExplainsUtf16WithoutByteOrderMark()
   {
      byte[] little = Encoding.Unicode.GetBytes( "id,name\r\n1,A\r\n2,B\r\n" );
      byte[] big = Encoding.BigEndianUnicode.GetBytes( "id,name\r\n1,A\r\n2,B\r\n" );

      Assert.Contains( "UTF-16", FileSniffer.SniffBytes( little, false ).RejectReason );
      Assert.Contains( "UTF-16", FileSniffer.SniffBytes( big, false ).RejectReason );
   }

   /// <summary>
   /// UTF-16 with a byte order mark still works, and a UTF-32 byte order mark is refused by name
   /// instead of being decoded as NUL-riddled UTF-16.
   /// </summary>
   [Fact]
   public void Sniffer_KeepsUtf16WithBomAndNamesUtf32()
   {
      byte[] utf16 = new byte[] { 0xFF, 0xFE }.Concat( Encoding.Unicode.GetBytes( "id,name\r\n1,A\r\n" ) ).ToArray();
      byte[] utf32 = new byte[] { 0xFF, 0xFE, 0x00, 0x00 }.Concat( Encoding.UTF32.GetBytes( "id,name\r\n1,A\r\n" ) ).ToArray();

      Assert.Equal( new[] { "id", "name" }, FileSniffer.SniffBytes( utf16, false ).Profile!.Columns );
      Assert.Contains( "UTF-32", FileSniffer.SniffBytes( utf32, false ).RejectReason );
   }

   /// <summary>
   /// A workbook that will not open is rejected under its bare file path. The pipeline treats
   /// that as covering every "file#Sheet" origin stored from it, so the old vectors survive.
   /// </summary>
   [Fact]
   public void Scan_CorruptWorkbook_IsRejectedUnderItsBareFilePath()
   {
      using var folder = new TempFolder();
      folder.WriteBytes( "book.xlsx", Encoding.ASCII.GetBytes( "this is not a zip file at all" ) );

      FolderScanResult scan = new FolderScanner().Scan( folder.Path, CancellationToken.None );

      RejectedOrigin rejected = Assert.Single( scan.Rejected );
      Assert.Equal( "book.xlsx", rejected.Origin );
      Assert.Contains( "workbook will not open", rejected.Reason );
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

   /// <summary>
   /// Maps each table's column list (joined with a bar) to its name.
   /// </summary>
   /// <param name="scan">The scan result.</param>
   /// <returns>Name by column signature.</returns>
   private static Dictionary<string, string> NameByColumns( FolderScanResult scan )
   {
      return scan.Tables.ToDictionary( t => string.Join( '|', t.Columns ), t => t.Name );
   }

   /// <summary>
   /// Rewrites text inside the first worksheet's XML, so a test can store numbers with more
   /// digits than the workbook writer would.
   /// </summary>
   /// <param name="path">Workbook path.</param>
   /// <param name="edits">Pairs of text to find and text to put in its place.</param>
   private static void PatchSheetXml( string path, params (string From, string To)[] edits )
   {
      using ZipArchive zip = ZipFile.Open( path, ZipArchiveMode.Update );
      ZipArchiveEntry entry = zip.GetEntry( "xl/worksheets/sheet1.xml" )!;
      string xml;
      using( var reader = new StreamReader( entry.Open(), Encoding.UTF8 ) )
      {
         xml = reader.ReadToEnd();
      }

      foreach( (string from, string to) in edits )
      {
         Assert.Contains( from, xml );
         xml = xml.Replace( from, to );
      }

      entry.Delete();
      ZipArchiveEntry replacement = zip.CreateEntry( "xl/worksheets/sheet1.xml" );
      using var writer = new StreamWriter( replacement.Open(), new UTF8Encoding( false ) );
      writer.Write( xml );
   }

   /// <summary>
   /// Creates a directory symlink, returning false when the platform or account cannot.
   /// </summary>
   /// <param name="link">Path of the link to create.</param>
   /// <param name="target">Directory it points at.</param>
   /// <returns>True when the link exists.</returns>
   private static bool TryLinkDirectory( string link, string target )
   {
      try
      {
         Directory.CreateSymbolicLink( link, target );
         return true;
      }
      catch( Exception ex ) when( ex is IOException or UnauthorizedAccessException or PlatformNotSupportedException )
      {
         return false;
      }
   }

   /// <summary>
   /// Creates a file symlink, returning false when the platform or account cannot.
   /// </summary>
   /// <param name="link">Path of the link to create.</param>
   /// <param name="target">File it points at.</param>
   /// <returns>True when the link exists.</returns>
   private static bool TryLinkFile( string link, string target )
   {
      try
      {
         File.CreateSymbolicLink( link, target );
         return true;
      }
      catch( Exception ex ) when( ex is IOException or UnauthorizedAccessException or PlatformNotSupportedException )
      {
         return false;
      }
   }

   /// <summary>
   /// True when the current account can still list the directory (for example when running as root).
   /// </summary>
   /// <param name="path">Directory to try.</param>
   /// <returns>True when listing works.</returns>
   private static bool CanListDirectory( string path )
   {
      try
      {
         _ = Directory.GetFileSystemEntries( path );
         return true;
      }
      catch( Exception ex ) when( ex is IOException or UnauthorizedAccessException )
      {
         return false;
      }
   }

   #endregion Private Methods
}
