namespace GenericVectorBuilder.Engines.Sinks;

/// <summary>
/// Settings for <see cref="DuckDbSink"/>: where the database files live and how the HNSW index is
/// built and searched. The defaults are the numbers every HNSW engine in the benchmark uses
/// (m 16, ef_construction 128), so the engines are compared on equal terms.
/// Why the index numbers live here: the benchmark report prints them next to the timings, and
/// a run with different numbers is a different experiment.
/// </summary>
public sealed class DuckDbSinkOptions
{
   #region Public Methods

   /// <summary>
   /// Folder that holds one DuckDB file per collection. Defaults to ~/gvb-data/engines/duckdb,
   /// the folder the engine README names for this engine.
   /// </summary>
   public string DirectoryPath { get; set; } = DefaultDirectory();

   /// <summary>HNSW m: links per node in the graph. Higher is more accurate and bigger.</summary>
   public int HnswM { get; set; } = 16;

   /// <summary>HNSW ef_construction: candidate list size while building the graph.</summary>
   public int HnswEfConstruction { get; set; } = 128;

   /// <summary>
   /// HNSW ef_search: candidate list size while searching. The extension's own default is 64.
   /// This sink sets it per connection (SET hnsw_ef_search) because DuckDB lets a query change
   /// it without rebuilding the index, and 64 is too low for the 10 hits a typical search asks for.
   /// </summary>
   public int HnswEfSearch { get; set; } = 100;

   /// <summary>
   /// DuckDB memory limit for one open collection, in DuckDB's own syntax ("8GB"). Why it is
   /// set: DuckDB otherwise takes 80 percent of the machine's RAM, and this box also runs
   /// SQL Server, Qdrant and the other benchmark engines.
   /// </summary>
   public string MemoryLimit { get; set; } = "8GB";

   /// <summary>
   /// How big the write-ahead log may grow before DuckDB writes it into the database file (a
   /// checkpoint), in DuckDB's own syntax. DuckDB's default is 16MB. Why this sink raises it to
   /// 256MB: every checkpoint writes the whole HNSW index again, into new blocks, and the old
   /// blocks were not reused during the same session. Measured on this box, loading 100,000
   /// vectors of 1024 values at the default 16MB made a 5.4 GB file for 0.4 GB of raw vectors. The
   /// price of a bigger log is a longer replay after a crash, because the rows still in the log
   /// are put back into the index one by one when the file is opened again.
   /// </summary>
   public string CheckpointThreshold { get; set; } = "256MB";

   /// <summary>How many records go into one transaction (one appender, one delete).</summary>
   public int UpsertBatch { get; set; } = 2000;

   /// <summary>
   /// The default database folder, under the current user's home directory.
   /// </summary>
   /// <returns>~/gvb-data/engines/duckdb as an absolute path.</returns>
   public static string DefaultDirectory()
   {
      string home = Environment.GetFolderPath( Environment.SpecialFolder.UserProfile );
      return Path.Combine( home, "gvb-data", "engines", "duckdb" );
   }

   #endregion Public Methods
}
