namespace GenericVectorBuilder.Engines.Sinks;

/// <summary>
/// Connection and index settings for <see cref="ClickHouseSink"/>. The defaults point at the
/// local gvb-clickhouse container (deploy/engines/clickhouse.compose.yaml) and read its
/// password from the shared secrets file, so a parameterless sink works on this box with
/// nothing configured.
/// Why the index numbers live here: the benchmark report prints them next to the timings, and
/// a run with different numbers is a different experiment.
/// </summary>
public sealed class ClickHouseSinkOptions
{
   #region Data Members

   private const string SECRETS_ENV = "GVB_ENGINE_SECRETS";
   private const string PASSWORD_KEY = "CLICKHOUSE_PASSWORD";

   #endregion Data Members

   #region Public Methods

   /// <summary>ClickHouse host.</summary>
   public string Host { get; set; } = "127.0.0.1";

   /// <summary>ClickHouse HTTP port.</summary>
   public int Port { get; set; } = 8123;

   /// <summary>Login name (the compose file creates this user).</summary>
   public string User { get; set; } = "gvb";

   /// <summary>Login password, or null to connect without one.</summary>
   public string? Password { get; set; }

   /// <summary>Database that holds one table per pipeline; created on first use.</summary>
   public string Database { get; set; } = "gvb";

   /// <summary>HNSW M: links per node in the graph (ClickHouse defaults to 32).</summary>
   public int HnswM { get; set; } = 16;

   /// <summary>HNSW ef_construction: candidate list size while building (ClickHouse default 128).</summary>
   public int HnswEfConstruction { get; set; } = 128;

   /// <summary>
   /// How vectors are held inside the index: "f32", "f16", "bf16", "i8" or "b1". ClickHouse
   /// defaults to "bf16", which halves the index memory; the table column always keeps full
   /// float32 values.
   /// </summary>
   public string Quantization { get; set; } = "bf16";

   /// <summary>
   /// hnsw_candidate_list_size_for_search: the beam width while searching (ClickHouse default 256).
   /// </summary>
   public int HnswEfSearch { get; set; } = 256;

   /// <summary>
   /// vector_search_with_rescoring: when 1, ClickHouse re-ranks the index's candidates with the
   /// exact float32 distance from the column. ClickHouse defaults to 0.
   /// </summary>
   public int Rescoring { get; set; }

   /// <summary>
   /// How many rows go into one INSERT. Each insert becomes one data part with its own HNSW
   /// index, so bigger batches mean fewer, better indexes.
   /// </summary>
   public int UpsertBatch { get; set; } = 10000;

   /// <summary>
   /// Longest <see cref="ClickHouseSink.FinishLoadAsync"/> waits for every data part to carry the
   /// vector index and for background merges to settle before it fails with a plain message.
   /// </summary>
   public int IndexWaitSeconds { get; set; } = 1800;

   /// <summary>
   /// How long the set of data parts must stay unchanged, with no merge running, before
   /// <see cref="ClickHouseSink.FinishLoadAsync"/> calls the table settled. Why: every merge
   /// rebuilds the vector index of the merged part, and a merge that starts during a timed search
   /// run changes what is being timed. Measured 2026-10-04: after 30 small inserts the merges
   /// were over within 2 seconds of the last insert, so 5 seconds covers the scheduler's next look.
   /// </summary>
   public int MergeSettleSeconds { get; set; } = 5;

   /// <summary>
   /// Options for the local container, with the password read from the shared secrets file
   /// (/home/dan/gvb-data/engines/secrets.env, or the file named by GVB_ENGINE_SECRETS).
   /// </summary>
   /// <returns>Options with the password filled in when the file has one.</returns>
   public static ClickHouseSinkOptions LocalDefaults()
   {
      return new ClickHouseSinkOptions { Password = ReadSecret( PASSWORD_KEY ) };
   }

   #endregion Public Methods

   #region Private Methods

   /// <summary>
   /// Reads one KEY=VALUE secret. Blank lines and # comments are skipped, a carriage return from
   /// a Windows-edited file is stripped, and the last matching line wins. The value is never
   /// logged. Falls back to the environment variable of the same name.
   /// </summary>
   /// <param name="key">Secret name.</param>
   /// <returns>The value, or null when neither the file nor the environment has it.</returns>
   private static string? ReadSecret( string key )
   {
      string path = Environment.GetEnvironmentVariable( SECRETS_ENV )
         ?? Path.Combine( Environment.GetFolderPath( Environment.SpecialFolder.UserProfile ), "gvb-data", "engines", "secrets.env" );
      string? value = null;
      if( File.Exists( path ) )
      {
         foreach( string raw in File.ReadLines( path ) )
         {
            string line = raw.Replace( "\r", string.Empty );
            int split = line.IndexOf( '=' );
            if( split > 0 && !line.StartsWith( '#' ) && string.Equals( line[..split].Trim(), key, StringComparison.Ordinal ) )
            {
               value = line[( split + 1 )..];
            }
         }
      }

      return value ?? Environment.GetEnvironmentVariable( key );
   }

   #endregion Private Methods
}
