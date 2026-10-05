namespace GenericVectorBuilder.Engines.Sinks;

/// <summary>
/// Settings for <see cref="SqliteVecSink"/>: where the SQLite files live and how many searches
/// may run at once.
/// Why so few index settings: sqlite-vec's vec0 table has no index to tune. It compares the query
/// with every stored vector, so there is nothing to configure except where the data goes.
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
   /// The most searches that may run at once on one collection, which is also the most search
   /// connections the sink opens for it. A connection is opened only when a searcher arrives and
   /// finds none idle, so the pool ends up as large as the caller's concurrency, up to this limit;
   /// searchers beyond the limit wait for a free connection. Values below 8 are raised to 8,
   /// because the benchmark's widest pass uses 8 searchers and a smaller pool would quietly
   /// serialize them.
   /// </summary>
   public int MaxSearchConnections { get; set; } = 32;

   /// <summary>
   /// True to run every search on the single write connection, one at a time, as the sink did
   /// before it had a pool. Why it exists: it is the control the tests compare the concurrent
   /// answers against, and the report says "searches run one at a time (single connection)" when
   /// it is on, so a run made this way cannot be mistaken for a concurrent one.
   /// </summary>
   public bool SerializeSearches { get; set; }

   /// <summary>
   /// Longest, in seconds, that a call may wait for a free search connection or for the write
   /// gate before it fails with a plain message. Why a deadline: a stuck query must end a run
   /// loudly, not hang it.
   /// </summary>
   public int ConnectionWaitSeconds { get; set; } = 120;

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
