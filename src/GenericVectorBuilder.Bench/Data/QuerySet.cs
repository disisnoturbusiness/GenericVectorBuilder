using System.Numerics.Tensors;

namespace GenericVectorBuilder.Bench.Data;

/// <summary>
/// The query vectors every target is asked, with what is needed to score the answers.
/// Two kinds:
/// "random:N" picks N stored vectors (fixed seed, so every run asks the same N). Each query's
/// own row is set aside: it is left out of the exact top k, the engine is asked for one extra
/// hit, and the query's own id is removed from what it returns. Why: a stored vector always
/// finds itself at similarity 1.0, which would make every engine look 10% better than it is
/// and says nothing about finding neighbours. This is how the Qdrant sink's own measurements
/// were taken.
/// "golden" embeds labelled questions with the pipeline's own embedder and also scores
/// file-level nDCG@k against the labels.
/// </summary>
public sealed class QuerySet
{
   #region Data Members

   /// <summary>Seed for picking random query rows, fixed so runs are comparable.</summary>
   public const int RANDOM_SEED = 20261003;

   #endregion Data Members

   #region Constructor

   /// <summary>
   /// Creates a query set.
   /// </summary>
   /// <param name="description">How the queries were made, for the report.</param>
   /// <param name="vectors">Normalized query vectors.</param>
   /// <param name="selfRows">Per query, the stored row to set aside, or -1.</param>
   /// <param name="labels">Per query, a short label (question id or row id).</param>
   /// <param name="grades">Per query, labelled files and grades, or null for random queries.</param>
   public QuerySet( string description, float[][] vectors, int[] selfRows, string[] labels, IReadOnlyDictionary<string, int>[]? grades )
   {
      Description = description;
      Vectors = vectors;
      SelfRows = selfRows;
      Labels = labels;
      Grades = grades;
   }

   #endregion Constructor

   #region Public Methods

   /// <summary>How the queries were made.</summary>
   public string Description { get; }

   /// <summary>Normalized query vectors.</summary>
   public float[][] Vectors { get; }

   /// <summary>Per query, the stored row to set aside, or -1.</summary>
   public int[] SelfRows { get; }

   /// <summary>Per query, a short label.</summary>
   public string[] Labels { get; }

   /// <summary>Per query, labelled files and grades; null for random queries.</summary>
   public IReadOnlyDictionary<string, int>[]? Grades { get; }

   /// <summary>True when queries carry labels, so nDCG is scored.</summary>
   public bool IsGolden => Grades != null;

   /// <summary>Number of queries.</summary>
   public int Count => Vectors.Length;

   /// <summary>Hits to ask an engine for: one extra when the query's own row must be removed.</summary>
   /// <param name="top">Hits wanted.</param>
   /// <returns>Hits to request.</returns>
   public int RequestSize( int top )
   {
      return SelfRows.Any( r => r >= 0 ) ? top + 1 : top;
   }

   /// <summary>
   /// Picks <paramref name="count"/> distinct stored vectors as queries.
   /// </summary>
   /// <param name="data">The loaded rows.</param>
   /// <param name="count">Queries wanted.</param>
   /// <returns>The query set.</returns>
   public static QuerySet Random( PipelineData data, int count )
   {
      if( data.Count < 2 )
      {
         throw new InvalidOperationException( "Random queries need at least 2 stored vectors." );
      }

      int wanted = Math.Min( count, data.Count );
      var random = new Random( RANDOM_SEED );
      var picked = new List<int>( wanted );
      var seen = new HashSet<int>();
      while( picked.Count < wanted )
      {
         int row = random.Next( data.Count );
         if( seen.Add( row ) )
         {
            picked.Add( row );
         }
      }

      string description = $"random:{wanted} (stored vectors picked with seed {RANDOM_SEED}; each query's own row is left out of the exact answer and removed from every engine's hits)";
      return new QuerySet( description, picked.Select( r => data.Vector( r ).ToArray() ).ToArray(), picked.ToArray(),
         picked.Select( r => data.Id( r ).ToString() ).ToArray(), null );
   }

   /// <summary>
   /// Returns a normalized copy of a vector (embedders usually normalize already; this makes
   /// sure the brute force's dot product really is cosine).
   /// </summary>
   /// <param name="vector">A vector.</param>
   /// <returns>The normalized copy.</returns>
   public static float[] Normalize( float[] vector )
   {
      float norm = TensorPrimitives.Norm( vector );
      if( norm <= 0 || float.IsNaN( norm ) )
      {
         throw new InvalidOperationException( "A query vector is all zeros or NaN." );
      }

      var result = new float[vector.Length];
      TensorPrimitives.Divide( vector, norm, result );
      return result;
   }

   #endregion Public Methods
}
