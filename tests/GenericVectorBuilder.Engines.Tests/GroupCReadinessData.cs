using System.Diagnostics;
using GenericVectorBuilder.Core.Contracts;

namespace GenericVectorBuilder.Engines.Tests;

/// <summary>
/// Shared data and loader for the readiness tests of the pgvector, MariaDB, Redis, Weaviate,
/// Chroma, DuckDB and sqlite-vec sinks: 2,000 random normalized 1024-dimension vectors, loaded in
/// batches into a collection whose name starts with gvbbench_ so it can never be mistaken for a
/// real pipeline's collection.
/// Why random vectors: they are the hardest input for a graph index (nothing clusters), so an
/// index that is complete for them is complete for real embeddings too.
/// </summary>
internal static class GroupCReadinessData
{
   #region Data Members

   /// <summary>Vector length used by every readiness test.</summary>
   public const int DIMENSION = 1024;

   /// <summary>How many vectors every readiness test loads.</summary>
   public const int COUNT = 2000;

   /// <summary>Records per upsert call.</summary>
   public const int BATCH = 500;

   /// <summary>Longest a whole test may run before it is cancelled with a plain message.</summary>
   public static readonly TimeSpan TEST_DEADLINE = TimeSpan.FromMinutes( 6 );

   #endregion Data Members

   #region Public Methods

   /// <summary>
   /// A throwaway collection name that starts with gvbbench_ and uses only [a-z0-9_].
   /// </summary>
   /// <param name="engine">Short engine tag, e.g. "pg".</param>
   /// <returns>A unique name.</returns>
   public static string NewCollection( string engine )
   {
      return $"gvbbench_{engine}_ready_{Guid.NewGuid().ToString( "N" )[..8]}";
   }

   /// <summary>
   /// Builds deterministic random unit vectors with distinct payloads.
   /// </summary>
   /// <param name="count">How many records.</param>
   /// <param name="seed">Random seed.</param>
   /// <returns>The records.</returns>
   public static List<VectorRecord> MakeRecords( int count, int seed )
   {
      var random = new Random( seed );
      var records = new List<VectorRecord>( count );
      for( int i = 0; i < count; i++ )
      {
         float[] vector = new float[DIMENSION];
         double sum = 0;
         for( int d = 0; d < DIMENSION; d++ )
         {
            vector[d] = random.NextSingle() * 2f - 1f;
            sum += vector[d] * vector[d];
         }

         float norm = (float)Math.Sqrt( sum );
         for( int d = 0; d < DIMENSION; d++ )
         {
            vector[d] /= norm;
         }

         var document = new Document( $"doc|ready|{i}", $"table{i % 3}", $"file{i % 7}.csv", $"text {i}", $"hash{i}", new Dictionary<string, string> { ["row"] = i.ToString() } );
         records.Add( new VectorRecord( document, new Chunk( Guid.NewGuid(), document.DocKey, 0, $"chunk text {i}" ), vector ) );
      }

      return records;
   }

   /// <summary>
   /// Upserts the records in batches and returns how long the writes took, so the output shows
   /// the load time next to the finish time.
   /// </summary>
   /// <param name="sink">The sink under test.</param>
   /// <param name="collection">Collection name.</param>
   /// <param name="records">Records to write.</param>
   /// <param name="ct">Cancellation.</param>
   /// <returns>Seconds spent in the upsert calls.</returns>
   public static async Task<double> LoadAsync( ISink sink, string collection, IReadOnlyList<VectorRecord> records, CancellationToken ct )
   {
      var clock = Stopwatch.StartNew();
      foreach( VectorRecord[] batch in records.Chunk( BATCH ) )
      {
         await sink.UpsertAsync( collection, batch, ct );
      }

      return clock.Elapsed.TotalSeconds;
   }

   #endregion Public Methods
}
