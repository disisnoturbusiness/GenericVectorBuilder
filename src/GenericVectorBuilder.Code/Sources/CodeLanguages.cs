namespace GenericVectorBuilder.Code.Sources;

/// <summary>
/// Maps file extensions to the language name stored with every chunk.
/// Why: the language travels into every sink as metadata, so a search can be narrowed to one
/// language, and the chunker uses it to decide whether a file can be split by its syntax.
/// </summary>
public static class CodeLanguages
{
   #region Data Members

   /// <summary>The language name the C# chunker recognizes.</summary>
   public const string CSHARP = "csharp";

   private static readonly Dictionary<string, string> BY_EXTENSION = new( StringComparer.OrdinalIgnoreCase )
   {
      [".cs"] = CSHARP,
      [".csx"] = CSHARP,
      [".vb"] = "vb",
      [".fs"] = "fsharp",
      [".razor"] = "razor",
      [".cshtml"] = "razor",
      [".js"] = "javascript",
      [".ts"] = "typescript",
      [".py"] = "python",
      [".sql"] = "sql",
      [".ps1"] = "powershell",
      [".sh"] = "shell",
      [".json"] = "json",
      [".xml"] = "xml",
      [".csproj"] = "msbuild",
      [".props"] = "msbuild",
      [".targets"] = "msbuild",
      [".yml"] = "yaml",
      [".yaml"] = "yaml",
      [".md"] = "markdown",
      [".html"] = "html",
      [".css"] = "css",
   };

   #endregion Data Members

   #region Public Methods

   /// <summary>
   /// The language of a file from its extension.
   /// </summary>
   /// <param name="path">File name or path.</param>
   /// <returns>A known language name, otherwise the extension without its dot ("text" when there is none).</returns>
   public static string FromPath( string path )
   {
      string extension = Path.GetExtension( path );
      if( BY_EXTENSION.TryGetValue( extension, out string? language ) )
      {
         return language;
      }

      return extension.Length > 1 ? extension[1..].ToLowerInvariant() : "text";
   }

   #endregion Public Methods
}
