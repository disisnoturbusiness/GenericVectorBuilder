using System.Numerics;

namespace GenericVectorBuilder.Bench.Stats;

/// <summary>
/// The arithmetic behind every number in the benchmark report: latency percentiles, recall
/// against the exact answer, and file-level nDCG for labelled questions.
/// Why it is one small file with no dependencies beyond the base library: these formulas
/// decide who "wins", so they are unit tested on their own, and the tests compile this exact
/// file rather than a copy of it.
/// </summary>
public static class BenchMath
{
   #region Data Members

   /// <summary>
   /// How far below the k-th best exact similarity a hit may score and still count as a
   /// correct answer. Why: the data holds identical vectors (duplicate rows embed to the same
   /// point), and an engine that returns one twin instead of the other is not wrong. Float
   /// rounding between engines is also far below this.
   /// </summary>
   public const double TIE_TOLERANCE = 1e-5;

   #endregion Data Members

   #region Public Methods

   /// <summary>
   /// Percentile by linear interpolation between the two closest ranks, the same rule as
   /// numpy's default, so a p95 here can be checked against a quick script.
   /// </summary>
   /// <param name="values">Samples in any order.</param>
   /// <param name="percentile">0 to 100.</param>
   /// <returns>The percentile, or NaN when there are no samples.</returns>
   public static double Percentile( IReadOnlyList<double> values, double percentile )
   {
      if( values.Count == 0 )
      {
         return double.NaN;
      }

      double[] sorted = values.OrderBy( v => v ).ToArray();
      double rank = Math.Clamp( percentile, 0, 100 ) / 100.0 * ( sorted.Length - 1 );
      int lower = (int)Math.Floor( rank );
      int upper = (int)Math.Ceiling( rank );
      return sorted[lower] + ( sorted[upper] - sorted[lower] ) * ( rank - lower );
   }

   /// <summary>
   /// Recall@k of one query, counting ties as correct. A returned hit counts when it is in the
   /// exact top k, or when its own exact similarity is at least the k-th best exact similarity
   /// (less <see cref="TIE_TOLERANCE"/>). Each id counts once. With no ties this is the usual
   /// |hits ∩ truth| / k.
   /// </summary>
   /// <param name="truthIds">The exact top k, best first.</param>
   /// <param name="kthScore">Exact similarity of the k-th best stored vector.</param>
   /// <param name="hitIds">What the engine returned, best first.</param>
   /// <param name="exactScore">Exact similarity of a returned id to the query, or null when the id is not a stored vector.</param>
   /// <param name="k">Cut-off.</param>
   /// <returns>Recall between 0 and 1.</returns>
   public static double RecallAtK( IReadOnlyList<Guid> truthIds, double kthScore, IReadOnlyList<Guid> hitIds, Func<Guid, double?> exactScore, int k )
   {
      int expected = Math.Min( k, truthIds.Count );
      if( expected == 0 )
      {
         return double.NaN;
      }

      var truth = new HashSet<Guid>( truthIds.Take( k ) );
      var seen = new HashSet<Guid>();
      int correct = 0;
      foreach( Guid id in hitIds.Take( k ) )
      {
         if( !seen.Add( id ) )
         {
            continue;
         }

         if( truth.Contains( id ) || exactScore( id ) is double score && score >= kthScore - TIE_TOLERANCE )
         {
            correct++;
         }
      }

      return Math.Min( correct, expected ) / (double)expected;
   }

   /// <summary>
   /// File-level nDCG@k, the same formula as evalkit's score.py: linear gain (the grade),
   /// discount log2(rank + 1), ideal order from every labelled file. Each labelled file can
   /// earn its gain once, so two chunks of one file never count twice.
   /// </summary>
   /// <param name="rankedFiles">File paths of the hits, best first, already collapsed to one entry per file.</param>
   /// <param name="grades">Labelled relevant files and their grades.</param>
   /// <param name="k">Cut-off.</param>
   /// <returns>nDCG between 0 and 1, or NaN when nothing is labelled.</returns>
   public static double NdcgAtK( IReadOnlyList<string> rankedFiles, IReadOnlyDictionary<string, int> grades, int k )
   {
      double ideal = grades.Values.Where( g => g > 0 ).OrderByDescending( g => g ).Take( k ).Select( ( g, i ) => g / Math.Log2( i + 2 ) ).Sum();
      if( ideal <= 0 )
      {
         return double.NaN;
      }

      var used = new HashSet<string>( StringComparer.OrdinalIgnoreCase );
      double dcg = 0;
      for( int i = 0; i < Math.Min( k, rankedFiles.Count ); i++ )
      {
         string? label = MatchLabel( rankedFiles[i], grades.Keys );
         if( label != null && used.Add( label ) )
         {
            dcg += grades[label] / Math.Log2( i + 2 );
         }
      }

      return dcg / ideal;
   }

   /// <summary>
   /// Keeps the first occurrence of each file, in order. Why: labels are per file, and several
   /// chunks of one file must not push other files out of the top k twice over.
   /// </summary>
   /// <param name="files">File paths of the hits, best first.</param>
   /// <returns>One entry per file, best first.</returns>
   public static IReadOnlyList<string> FirstPerFile( IEnumerable<string> files )
   {
      var seen = new HashSet<string>( StringComparer.OrdinalIgnoreCase );
      return files.Select( NormalizePath ).Where( f => seen.Add( f ) ).ToList();
   }

   /// <summary>
   /// Finds the labelled file a hit's path stands for: the same path, or a path that ends with
   /// it after a folder separator (a pipeline built from a parent folder stores
   /// "eShopOnWeb/src/X.cs" for the label "src/X.cs"). Slashes and case are ignored.
   /// </summary>
   /// <param name="file">The hit's file path.</param>
   /// <param name="labels">Labelled paths.</param>
   /// <returns>The matching label, or null.</returns>
   public static string? MatchLabel( string file, IEnumerable<string> labels )
   {
      string path = NormalizePath( file );
      foreach( string label in labels )
      {
         string wanted = NormalizePath( label );
         if( wanted.Length > 0 && ( path.Equals( wanted, StringComparison.OrdinalIgnoreCase ) || path.EndsWith( "/" + wanted, StringComparison.OrdinalIgnoreCase ) ) )
         {
            return label;
         }
      }

      return null;
   }

   /// <summary>
   /// Mean of the values that are numbers, or NaN when there are none.
   /// </summary>
   /// <param name="values">Values, NaN for "not measured".</param>
   /// <returns>The mean.</returns>
   public static double MeanOfNumbers( IEnumerable<double> values )
   {
      double[] numbers = values.Where( v => !double.IsNaN( v ) ).ToArray();
      return numbers.Length == 0 ? double.NaN : numbers.Average();
   }

   /// <summary>
   /// floor(10000 x num / den), computed exactly on the two numbers as stored, with no floating-point rounding step.
   /// Why exact: 10000.0 * num / den in doubles can round a ratio that is a hair below a whole number of basis points up to it, so a pair whose decimal ratio is exactly
   /// the threshold but whose stored values put it a hair below would be ordered by the rounding alone. Floored exactly, such a pair stays unordered: rounding never creates a claim.
   /// </summary>
   /// <param name="num">Numerator, finite and above zero.</param>
   /// <param name="den">Denominator, finite and above zero.</param>
   /// <returns>The ratio in basis points, rounded down.</returns>
   /// <exception cref="ArgumentOutOfRangeException">A value is not finite or not above zero.</exception>
   /// <exception cref="OverflowException">The result does not fit an int.</exception>
   public static int RatioBpFloor( double num, double den )
   {
      ( BigInteger n, BigInteger d ) = Ratio10000( num, den );
      return checked( (int)BigInteger.Divide( n, d ) );
   }

   /// <summary>
   /// ceiling(10000 x num / den), computed exactly on the two numbers as stored (see <see cref="RatioBpFloor"/>); a move is rounded up so that none is understated.
   /// </summary>
   /// <param name="num">Numerator, finite and above zero.</param>
   /// <param name="den">Denominator, finite and above zero.</param>
   /// <returns>The ratio in basis points, rounded up.</returns>
   /// <exception cref="ArgumentOutOfRangeException">A value is not finite or not above zero.</exception>
   /// <exception cref="OverflowException">The result does not fit an int.</exception>
   public static int RatioBpCeiling( double num, double den )
   {
      ( BigInteger n, BigInteger d ) = Ratio10000( num, den );
      return checked( (int)BigInteger.Divide( n + d - BigInteger.One, d ) );
   }

   #endregion Public Methods

   #region Private Methods

   /// <summary>
   /// 10000 x num / den as an exact fraction of two whole numbers.
   /// </summary>
   /// <param name="num">Numerator.</param>
   /// <param name="den">Denominator.</param>
   /// <returns>The numerator and the denominator of the fraction.</returns>
   private static ( BigInteger Numerator, BigInteger Denominator ) Ratio10000( double num, double den )
   {
      if( !double.IsFinite( num ) || !double.IsFinite( den ) || num <= 0 || den <= 0 )
      {
         throw new ArgumentOutOfRangeException( nameof( num ), $"a ratio needs finite values above zero, got {num} and {den}" );
      }

      ( BigInteger m1, int e1 ) = Decompose( num );
      ( BigInteger m2, int e2 ) = Decompose( den );
      BigInteger n = 10000 * m1;
      BigInteger d = m2;
      int shift = e1 - e2;
      return shift >= 0 ? ( n << shift, d ) : ( n, d << -shift );
   }

   /// <summary>
   /// A positive finite double as mantissa x 2^exponent, exactly.
   /// </summary>
   /// <param name="value">The value.</param>
   /// <returns>The whole-number mantissa and the power of two.</returns>
   private static ( BigInteger Mantissa, int Exponent ) Decompose( double value )
   {
      long bits = BitConverter.DoubleToInt64Bits( value );
      int field = (int)( ( bits >> 52 ) & 0x7FF );
      long fraction = bits & 0xFFFFFFFFFFFFFL;
      return field == 0 ? ( fraction, -1074 ) : ( fraction | ( 1L << 52 ), field - 1075 );
   }

   /// <summary>
   /// Forward slashes, no leading "./" or "/".
   /// </summary>
   /// <param name="path">A path.</param>
   /// <returns>The normalized path.</returns>
   private static string NormalizePath( string path )
   {
      string normalized = path.Replace( '\\', '/' ).Trim();
      while( normalized.StartsWith( "./", StringComparison.Ordinal ) )
      {
         normalized = normalized[2..];
      }

      return normalized.TrimStart( '/' );
   }

   #endregion Private Methods
}
