using System.Collections.Concurrent;
using System.Diagnostics;
using System.Numerics.Tensors;
using GenericVectorBuilder.Bench.Data;

namespace GenericVectorBuilder.Bench.Truth;

/// <summary>
/// The exact top k of every query, by brute force over every loaded vector in memory.
/// Why this is the yardstick: it has no index, no approximation and no engine in the way, so
/// "recall" means "how much of the true answer the engine found". Two engines with an exact
/// mode (SQL Server's VECTOR_DISTANCE and Qdrant's exact search) are scored against it as
/// well, and must come out at 1.0; that is the check that the yardstick itself is right.
/// How it stays fast: the rows are split into blocks across all cores, each block is read
/// from memory once and compared with every query (SIMD dot products), and each thread keeps
/// its own top-k heaps that are merged at the end.
/// </summary>
public static class BruteForce
{
   #region Data Members

   private const int BLOCK_ROWS = 128;

   #endregion Data Members

   #region Public Methods

   /// <summary>
   /// Computes the exact answer of every query.
   /// </summary>
   /// <param name="data">Loaded rows.</param>
   /// <param name="queries">Queries.</param>
   /// <param name="k">Hits per query.</param>
   /// <returns>The truth.</returns>
   public static TruthSet Compute( PipelineData data, QuerySet queries, int k )
   {
      Stopwatch clock = Stopwatch.StartNew();
      var partials = new ConcurrentBag<TopK[]>();
      var blocks = Partitioner.Create( 0, data.Count, BLOCK_ROWS );
      Parallel.ForEach( blocks, () => NewHeaps( queries.Count, k ), ( range, _, heaps ) =>
      {
         ScanBlock( data, queries, range.Item1, range.Item2, heaps );
         return heaps;
      }, partials.Add );

      TopK[] merged = NewHeaps( queries.Count, k );
      foreach( TopK[] partial in partials )
      {
         for( int q = 0; q < merged.Length; q++ )
         {
            merged[q].Merge( partial[q] );
         }
      }

      var rows = new int[queries.Count][];
      var scores = new float[queries.Count][];
      for( int q = 0; q < merged.Length; q++ )
      {
         ( rows[q], scores[q] ) = merged[q].Sorted();
      }

      return new TruthSet( rows, scores, clock.Elapsed.TotalSeconds );
   }

   #endregion Public Methods

   #region Private Methods

   /// <summary>
   /// Compares every row of one block with every query.
   /// </summary>
   /// <param name="data">Loaded rows.</param>
   /// <param name="queries">Queries.</param>
   /// <param name="from">First row.</param>
   /// <param name="to">Row after the last.</param>
   /// <param name="heaps">This thread's heaps, one per query.</param>
   private static void ScanBlock( PipelineData data, QuerySet queries, int from, int to, TopK[] heaps )
   {
      for( int q = 0; q < queries.Count; q++ )
      {
         float[] query = queries.Vectors[q];
         int self = queries.SelfRows[q];
         TopK heap = heaps[q];
         for( int row = from; row < to; row++ )
         {
            if( row != self )
            {
               heap.Offer( TensorPrimitives.Dot( query, data.Vector( row ) ), row );
            }
         }
      }
   }

   /// <summary>
   /// One empty heap per query.
   /// </summary>
   /// <param name="count">Queries.</param>
   /// <param name="k">Heap size.</param>
   /// <returns>The heaps.</returns>
   private static TopK[] NewHeaps( int count, int k )
   {
      return Enumerable.Range( 0, count ).Select( _ => new TopK( k ) ).ToArray();
   }

   #endregion Private Methods
}

/// <summary>
/// The exact answer of every query: row numbers and similarities, best first.
/// </summary>
/// <param name="Rows">Per query, the exact top-k rows.</param>
/// <param name="Scores">Per query, their similarities.</param>
/// <param name="Seconds">How long the brute force took (all queries, all cores).</param>
public sealed record TruthSet( int[][] Rows, float[][] Scores, double Seconds )
{
   /// <summary>
   /// The k-th best exact similarity of a query, the bar a tied hit must reach.
   /// </summary>
   /// <param name="query">Query index.</param>
   /// <returns>The similarity, or +infinity when the query has no truth.</returns>
   public double KthScore( int query )
   {
      return Scores[query].Length == 0 ? double.PositiveInfinity : Scores[query][^1];
   }
}
