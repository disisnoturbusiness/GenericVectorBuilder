namespace GenericVectorBuilder.Code.Sources;

/// <summary>
/// Which files a code folder source reads.
/// Why these defaults: C# is the first target, and build output, package caches, vendored
/// front-end libraries and git's own folder are never source anyone wants to search. Files over
/// 1 MB are almost always generated or minified, and one of them can outweigh the rest of a
/// repository in embedding time.
/// </summary>
/// <param name="Extensions">File extensions to read, with the dot, compared ignoring case, e.g. ".cs".</param>
/// <param name="ExcludedDirectories">Folder names to skip anywhere in the tree ("bin"), or a relative
/// path suffix of folders to skip ("wwwroot/lib"), compared ignoring case.</param>
/// <param name="MaxFileBytes">Files larger than this are skipped.</param>
public sealed record CodeFolderOptions( IReadOnlyCollection<string> Extensions, IReadOnlyCollection<string> ExcludedDirectories, long MaxFileBytes )
{
   #region Data Members

   /// <summary>Default size limit: 1 MB.</summary>
   public const long DEFAULT_MAX_FILE_BYTES = 1024 * 1024;

   #endregion Data Members

   #region Public Methods

   /// <summary>
   /// C# files only, skipping bin, obj, node_modules, .git, wwwroot/lib and packages, up to 1 MB.
   /// </summary>
   public static CodeFolderOptions Default { get; } = new(
      new[] { ".cs" },
      new[] { "bin", "obj", "node_modules", ".git", "wwwroot/lib", "packages" },
      DEFAULT_MAX_FILE_BYTES );

   /// <summary>
   /// True when a file's extension is one of <see cref="Extensions"/>.
   /// </summary>
   /// <param name="fileName">File name or path.</param>
   /// <returns>True when the file should be read.</returns>
   public bool IncludesExtension( string fileName )
   {
      string extension = Path.GetExtension( fileName );
      return Extensions.Any( e => string.Equals( NormalizeExtension( e ), extension, StringComparison.OrdinalIgnoreCase ) );
   }

   /// <summary>
   /// True when a folder must be skipped: its name is an excluded name, or its relative path ends
   /// with an excluded multi-part path such as "wwwroot/lib" (matched on whole folder names).
   /// </summary>
   /// <param name="relativePath">Folder path relative to the root, with '/' separators.</param>
   /// <returns>True when the folder and everything in it is skipped.</returns>
   public bool ExcludesDirectory( string relativePath )
   {
      string path = relativePath.Trim( '/' );
      foreach( string excluded in ExcludedDirectories.Select( e => e.Replace( '\\', '/' ).Trim( '/' ) ).Where( e => e.Length > 0 ) )
      {
         if( path.Equals( excluded, StringComparison.OrdinalIgnoreCase )
            || path.EndsWith( "/" + excluded, StringComparison.OrdinalIgnoreCase ) )
         {
            return true;
         }
      }

      return false;
   }

   #endregion Public Methods

   #region Private Methods

   /// <summary>
   /// Adds the leading dot to an extension written without one, so "cs" and ".cs" both work.
   /// </summary>
   /// <param name="extension">Extension as configured.</param>
   /// <returns>The extension with a leading dot.</returns>
   private static string NormalizeExtension( string extension )
   {
      string trimmed = extension.Trim();
      return trimmed.StartsWith( '.' ) ? trimmed : "." + trimmed;
   }

   #endregion Private Methods
}
