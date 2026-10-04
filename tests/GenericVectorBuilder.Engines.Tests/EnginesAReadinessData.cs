using System.Security.Cryptography;
using System.Text;
using GenericVectorBuilder.Core.Contracts;

namespace GenericVectorBuilder.Engines.Tests;

/// <summary>
/// Random test vectors for the Milvus, Oracle, MongoDB and ClickHouse readiness tests.
/// Why a shared helper: all four tests load the same kind of 1024-dimension vectors, and the same
/// data keeps their build times comparable.
/// Why unit-length random vectors: they are the worst case for an approximate index (no clusters
/// to exploit), so a build that finishes here finishes on real data.
/// </summary>
internal static class EnginesAReadinessData
{
   #region Public Methods

   /// <summary>
   /// Builds deterministic normalized random records with distinct payloads.
   /// </summary>
   /// <param name="count">How many records.</param>
   /// <param name="dimension">Vector length.</param>
   /// <param name="seed">Random seed.</param>
   /// <returns>The records.</returns>
   public static List<VectorRecord> Records( int count, int dimension, int seed )
   {
      var random = new Random( seed );
      var records = new List<VectorRecord>( count );
      for( int i = 0; i < count; i++ )
      {
         var vector = new float[dimension];
         double sum = 0;
         for( int d = 0; d < dimension; d++ )
         {
            vector[d] = (float)( random.NextDouble() * 2 - 1 );
            sum += vector[d] * vector[d];
         }

         float norm = (float)Math.Sqrt( sum );
         for( int d = 0; d < dimension; d++ )
         {
            vector[d] /= norm;
         }

         var document = new Document( $"ready|{seed}|{i}", $"table{i % 3}", $"file{i % 7}.csv", $"text {i}", $"hash{i}", new Dictionary<string, string> { ["row"] = i.ToString() } );
         var chunk = new Chunk( new Guid( MD5.HashData( Encoding.UTF8.GetBytes( $"ready:{seed}:{i}" ) ) ), document.DocKey, 0, $"chunk text {seed}-{i}" );
         records.Add( new VectorRecord( document, chunk, vector ) );
      }

      return records;
   }

   /// <summary>
   /// A collection name that can never clash with a real pipeline: benchmark collections always
   /// start with gvbbench_.
   /// </summary>
   /// <returns>A fresh name.</returns>
   public static string BenchCollection()
   {
      return "gvbbench_ready_" + Guid.NewGuid().ToString( "N" )[..8];
   }

   #endregion Public Methods
}
