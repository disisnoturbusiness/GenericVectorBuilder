namespace GenericVectorBuilder.Bench.Truth;

/// <summary>
/// Keeps the k best (score, row) pairs seen so far in a fixed-size min-heap, so scanning
/// hundreds of thousands of rows costs O(log k) per row and no allocation.
/// Why ties break on the lower row number: two identical vectors score the same, and the
/// ground truth must come out the same on every run.
/// </summary>
public sealed class TopK
{
   #region Data Members

   private readonly float[] _scores;
   private readonly int[] _rows;
   private int _count;

   #endregion Data Members

   #region Constructor

   /// <summary>
   /// Creates an empty heap.
   /// </summary>
   /// <param name="k">How many pairs to keep.</param>
   public TopK( int k )
   {
      _scores = new float[k];
      _rows = new int[k];
   }

   #endregion Constructor

   #region Public Methods

   /// <summary>
   /// Offers one candidate; it is kept when it beats the worst pair held.
   /// </summary>
   /// <param name="score">Similarity, higher is better.</param>
   /// <param name="row">Row number.</param>
   public void Offer( float score, int row )
   {
      if( _count < _scores.Length )
      {
         _scores[_count] = score;
         _rows[_count] = row;
         SiftUp( _count++ );
      }
      else if( _scores.Length > 0 && Better( score, row, _scores[0], _rows[0] ) )
      {
         _scores[0] = score;
         _rows[0] = row;
         SiftDown( 0 );
      }
   }

   /// <summary>
   /// Offers every pair another heap holds (merging per-thread results).
   /// </summary>
   /// <param name="other">The other heap.</param>
   public void Merge( TopK other )
   {
      for( int i = 0; i < other._count; i++ )
      {
         Offer( other._scores[i], other._rows[i] );
      }
   }

   /// <summary>
   /// The kept pairs, best first.
   /// </summary>
   /// <returns>Rows and scores in matching order.</returns>
   public (int[] Rows, float[] Scores) Sorted()
   {
      int[] order = Enumerable.Range( 0, _count ).ToArray();
      Array.Sort( order, ( a, b ) => Better( _scores[a], _rows[a], _scores[b], _rows[b] ) ? -1 : a == b ? 0 : 1 );
      return ( order.Select( i => _rows[i] ).ToArray(), order.Select( i => _scores[i] ).ToArray() );
   }

   #endregion Public Methods

   #region Private Methods

   /// <summary>
   /// True when (scoreA, rowA) ranks before (scoreB, rowB).
   /// </summary>
   /// <param name="scoreA">First score.</param>
   /// <param name="rowA">First row.</param>
   /// <param name="scoreB">Second score.</param>
   /// <param name="rowB">Second row.</param>
   /// <returns>True when the first pair is better.</returns>
   private static bool Better( float scoreA, int rowA, float scoreB, int rowB )
   {
      return scoreA > scoreB || ( scoreA == scoreB && rowA < rowB );
   }

   /// <summary>
   /// Restores the heap after adding at the end (worst pair at the root).
   /// </summary>
   /// <param name="index">Index of the new pair.</param>
   private void SiftUp( int index )
   {
      while( index > 0 )
      {
         int parent = ( index - 1 ) / 2;
         if( !Better( _scores[parent], _rows[parent], _scores[index], _rows[index] ) )
         {
            return;
         }

         Swap( parent, index );
         index = parent;
      }
   }

   /// <summary>
   /// Restores the heap after replacing the root.
   /// </summary>
   /// <param name="index">Index to sift from.</param>
   private void SiftDown( int index )
   {
      while( true )
      {
         int worst = index;
         int left = index * 2 + 1;
         int right = left + 1;
         if( left < _count && Better( _scores[worst], _rows[worst], _scores[left], _rows[left] ) )
         {
            worst = left;
         }

         if( right < _count && Better( _scores[worst], _rows[worst], _scores[right], _rows[right] ) )
         {
            worst = right;
         }

         if( worst == index )
         {
            return;
         }

         Swap( worst, index );
         index = worst;
      }
   }

   /// <summary>
   /// Swaps two heap slots.
   /// </summary>
   /// <param name="a">First slot.</param>
   /// <param name="b">Second slot.</param>
   private void Swap( int a, int b )
   {
      ( _scores[a], _scores[b] ) = ( _scores[b], _scores[a] );
      ( _rows[a], _rows[b] ) = ( _rows[b], _rows[a] );
   }

   #endregion Private Methods
}
