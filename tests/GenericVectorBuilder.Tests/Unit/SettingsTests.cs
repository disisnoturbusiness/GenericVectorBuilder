using GenericVectorBuilder.Core.Configuration;
using GenericVectorBuilder.Tests.Fakes;

namespace GenericVectorBuilder.Tests.Unit;

/// <summary>
/// The allowed-folder check is the only thing stopping the page from reading any folder on
/// the server, so its edge cases are pinned here.
/// </summary>
public class SettingsTests
{
   #region Public Methods

   /// <summary>
   /// Configured roots replace the default instead of being added to it.
   /// </summary>
   [Fact]
   public void Normalize_ConfiguredRootsReplaceTheDefault()
   {
      var settings = new GvbSettings { DataRoots = { "/srv/data" }, UploadRoot = "/srv/uploads" }.Normalize();
      Assert.Equal( new[] { "/srv/data", "/srv/uploads" }, settings.DataRoots );
      Assert.False( settings.IsAllowedDataPath( GvbSettings.Expand( "~/share" ) ) );
   }

   /// <summary>
   /// Paths inside a root pass; siblings with a shared prefix and ".." escapes do not.
   /// </summary>
   [Theory]
   [InlineData( "/srv/data", true )]
   [InlineData( "/srv/data/sub/folder", true )]
   [InlineData( "/srv/data2", false )]
   [InlineData( "/srv/data/../secrets", false )]
   [InlineData( "/etc", false )]
   public void IsAllowedDataPath_RespectsRootBoundaries( string path, bool allowed )
   {
      var settings = new GvbSettings { DataRoots = { "/srv/data" }, UploadRoot = "/srv/uploads" }.Normalize();
      Assert.Equal( allowed, settings.IsAllowedDataPath( path ) );
   }


   /// <summary>
   /// Empty paths and paths with a NUL character are refused with false. They used to throw
   /// out of Path.GetFullPath, which the endpoints turned into a bare 500 instead of a 400.
   /// </summary>
   [Theory]
   [InlineData( "" )]
   [InlineData( "   " )]
   [InlineData( null )]
   [InlineData( "/srv/data/a\0b" )]
   [InlineData( "\0" )]
   public void IsAllowedDataPath_EmptyOrNulPath_IsFalseAndNeverThrows( string? path )
   {
      var settings = new GvbSettings { DataRoots = { "/srv/data" }, UploadRoot = "/srv/uploads" }.Normalize();
      Assert.False( settings.IsAllowedDataPath( path ) );
   }

   /// <summary>
   /// A symbolic link below a root (to a folder outside, or to a file) is not allowed, and
   /// neither is any path that goes through one. A plain sub-folder still is.
   /// </summary>
   [Fact]
   public void IsAllowedDataPath_SymlinkBelowRoot_IsRefused()
   {
      if( OperatingSystem.IsWindows() )
      {
         return;
      }

      using var root = new TempFolder();
      using var outside = new TempFolder();
      outside.Write( "secret.csv", "id\n1\n" );
      Directory.CreateDirectory( Path.Combine( root.Path, "plain" ) );
      Directory.CreateSymbolicLink( Path.Combine( root.Path, "linkdir" ), outside.Path );
      File.CreateSymbolicLink( Path.Combine( root.Path, "leak.txt" ), Path.Combine( outside.Path, "secret.csv" ) );
      var settings = new GvbSettings { DataRoots = { root.Path }, UploadRoot = "/srv/uploads" }.Normalize();

      Assert.True( settings.IsAllowedDataPath( Path.Combine( root.Path, "plain" ) ) );
      Assert.False( settings.IsAllowedDataPath( Path.Combine( root.Path, "linkdir" ) ) );
      Assert.False( settings.IsAllowedDataPath( Path.Combine( root.Path, "linkdir", "deeper" ) ) );
      Assert.False( settings.IsAllowedDataPath( Path.Combine( root.Path, "leak.txt" ) ) );
   }

   /// <summary>
   /// Upload names that stay inside the upload folder resolve to a path there; climbing,
   /// empty, folder-like and NUL names are refused instead of throwing.
   /// </summary>
   [Theory]
   [InlineData( "a.csv", true )]
   [InlineData( "sub/b.csv", true )]
   [InlineData( "sub\\b.csv", true )]
   [InlineData( "/abs/c.csv", true )]
   [InlineData( "../evil.csv", false )]
   [InlineData( "a/../../evil.csv", false )]
   [InlineData( "..\\evil.csv", false )]
   [InlineData( "", false )]
   [InlineData( null, false )]
   [InlineData( "dir/", false )]
   [InlineData( "a\0b.csv", false )]
   [InlineData( ".", false )]
   public void TryResolveUploadTarget_OnlyAllowsNamesInsideTheFolder( string? fileName, bool allowed )
   {
      using var folder = new TempFolder();
      bool ok = GvbSettings.TryResolveUploadTarget( folder.Path, fileName, out string target );
      Assert.Equal( allowed, ok );
      if( allowed )
      {
         Assert.StartsWith( folder.Path + "/", target, StringComparison.Ordinal );
      }
   }

   /// <summary>
   /// A name that points at an existing folder is refused, because File.Create on it throws.
   /// </summary>
   [Fact]
   public void TryResolveUploadTarget_ExistingFolderName_IsRefused()
   {
      using var folder = new TempFolder();
      Directory.CreateDirectory( Path.Combine( folder.Path, "taken" ) );
      Assert.False( GvbSettings.TryResolveUploadTarget( folder.Path, "taken", out _ ) );
   }

   #endregion Public Methods
}
