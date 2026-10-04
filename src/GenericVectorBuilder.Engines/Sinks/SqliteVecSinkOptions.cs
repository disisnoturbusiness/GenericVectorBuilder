namespace GenericVectorBuilder.Engines.Sinks;

/// <summary>
/// Settings for <see cref="SqliteVecSink"/>: where the SQLite files live.
/// Why so few: sqlite-vec's vec0 table has no index to tune. It compares the query with every
/// stored vector, so there is nothing to configure except where the data goes.
/// </summary>
public sealed class SqliteVecSinkOptions
{
   #region Public Methods

   /// <summary>
   /// Folder that holds one SQLite file per collection. Defaults to ~/gvb-data/engines/sqlitevec,
   /// the folder the engine README names for this engine.
   /// </summary>
   public string DirectoryPath { get; set; } = DefaultDirectory();

   /// <summary>
   /// The default database folder, under the current user's home directory.
   /// </summary>
   /// <returns>~/gvb-data/engines/sqlitevec as an absolute path.</returns>
   public static string DefaultDirectory()
   {
      string home = Environment.GetFolderPath( Environment.SpecialFolder.UserProfile );
      return Path.Combine( home, "gvb-data", "engines", "sqlitevec" );
   }

   #endregion Public Methods
}
