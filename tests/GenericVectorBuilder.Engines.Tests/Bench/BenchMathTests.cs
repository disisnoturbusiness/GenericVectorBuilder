using System.Reflection;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;

namespace GenericVectorBuilder.Engines.Tests.Bench;

/// <summary>
/// The benchmark's arithmetic: percentiles, recall@k with ties, file-level nDCG@k, and the
/// top-k heap that picks the ground truth. These numbers decide which engine "wins", so each
/// formula is checked against values worked out by hand (and against numpy and evalkit's
/// score.py, which use the same rules).
/// Why the source is compiled here with Roslyn: the benchmark is a console project this test
/// project does not reference (its project file is not this lane's to change). Compiling the
/// real Stats/BenchMath.cs and Truth/TopK.cs from the repository tests exactly the code the
/// benchmark runs, not a copy.
/// </summary>
public class BenchMathTests
{
   #region Data Members

   private const string MATH_TYPE = "GenericVectorBuilder.Bench.Stats.BenchMath";
   private const string TOPK_TYPE = "GenericVectorBuilder.Bench.Truth.TopK";
   private static readonly Lazy<Assembly> COMPILED = new( Compile );

   #endregion Data Members

   #region Public Methods

   /// <summary>
   /// Linear interpolation between ranks, the same as numpy.percentile's default.
   /// </summary>
   [Fact]
   public void Percentile_MatchesNumpy()
   {
      List<double> values = Enumerable.Range( 1, 100 ).Select( i => (double)i ).Reverse().ToList();
      Assert.Equal( 50.5, Percentile( values, 50 ), 9 );
      Assert.Equal( 95.05, Percentile( values, 95 ), 9 );
      Assert.Equal( 99.01, Percentile( values, 99 ), 9 );
      Assert.Equal( 1, Percentile( values, 0 ), 9 );
      Assert.Equal( 100, Percentile( values, 100 ), 9 );
      Assert.Equal( 7, Percentile( new List<double> { 7 }, 99 ), 9 );
      Assert.True( double.IsNaN( Percentile( new List<double>(), 50 ) ) );
   }

   /// <summary>
   /// Without ties, recall is the share of the exact top k returned; order and duplicates do
   /// not matter, and a short answer simply scores lower.
   /// </summary>
   [Fact]
   public void Recall_CountsOverlapOnce()
   {
      List<Guid> truth = Ids( 10 );
      Func<Guid, double?> noTies = _ => 0.0;
      Assert.Equal( 1.0, Recall( truth, 0.5, truth.AsEnumerable().Reverse().ToList(), noTies, 10 ), 9 );
      Assert.Equal( 0.5, Recall( truth, 0.5, truth.Take( 5 ).Concat( Ids( 5, 100 ) ).ToList(), noTies, 10 ), 9 );
      Assert.Equal( 0.1, Recall( truth, 0.5, Enumerable.Repeat( truth[0], 10 ).ToList(), noTies, 10 ), 9 );
      Assert.Equal( 0.3, Recall( truth, 0.5, truth.Take( 3 ).ToList(), noTies, 10 ), 9 );
   }

   /// <summary>
   /// A hit outside the exact top k still counts when its exact similarity ties the k-th best
   /// (an identical duplicate row), but not when it is lower; only the first k hits are read.
   /// </summary>
   [Fact]
   public void Recall_CountsTiesAtTheCutOff()
   {
      List<Guid> truth = Ids( 10 );
      Guid twin = Guid.NewGuid();
      Guid worse = Guid.NewGuid();
      Func<Guid, double?> score = id => id == twin ? 0.8 : id == worse ? 0.79 : null;
      List<Guid> withTwin = truth.Take( 9 ).Append( twin ).ToList();
      List<Guid> withWorse = truth.Take( 9 ).Append( worse ).ToList();
      Assert.Equal( 1.0, Recall( truth, 0.8, withTwin, score, 10 ), 9 );
      Assert.Equal( 0.9, Recall( truth, 0.8, withWorse, score, 10 ), 9 );
      Assert.Equal( 0.9, Recall( truth, 0.8, withWorse.Append( truth[9] ).ToList(), score, 10 ), 9 );
      Assert.Equal( 1.0, Recall( truth.Take( 3 ).ToList(), 0.9, truth.Take( 3 ).ToList(), score, 10 ), 9 );
   }

   /// <summary>
   /// nDCG uses linear gain and log2(rank + 1), with the ideal order from every labelled file,
   /// exactly like evalkit's score.py; values below are worked by hand.
   /// </summary>
   [Fact]
   public void Ndcg_MatchesEvalkitFormula()
   {
      var grades = new Dictionary<string, int> { ["src/A.cs"] = 3, ["src/B.cs"] = 2 };
      double ideal = 3 + 2 / Math.Log2( 3 );
      Assert.Equal( 1.0, Ndcg( new[] { "src/A.cs", "src/B.cs" }, grades, 10 ), 9 );
      Assert.Equal( ( 2 + 3 / Math.Log2( 3 ) ) / ideal, Ndcg( new[] { "src/B.cs", "src/A.cs" }, grades, 10 ), 9 );
      Assert.Equal( 3 / Math.Log2( 3 ) / ideal, Ndcg( new[] { "src/X.cs", "src/A.cs" }, grades, 10 ), 9 );
      Assert.Equal( 0.0, Ndcg( new[] { "src/X.cs", "src/A.cs" }, grades, 1 ), 9 );
      Assert.Equal( 0.0, Ndcg( Array.Empty<string>(), grades, 10 ), 9 );
      Assert.True( double.IsNaN( Ndcg( new[] { "src/A.cs" }, new Dictionary<string, int>(), 10 ) ) );
   }

   /// <summary>
   /// File matching: a path under a parent folder matches its label, slashes and case do not
   /// matter, a longer file name ending in the same text does not match, and a label earns
   /// its gain once even if two hit paths match it.
   /// </summary>
   [Fact]
   public void Ndcg_MatchesPathsSafely()
   {
      var grades = new Dictionary<string, int> { ["src/A.cs"] = 3 };
      Assert.Equal( 1.0, Ndcg( new[] { "eShopOnWeb\\SRC\\a.cs" }, grades, 10 ), 9 );
      Assert.Equal( 0.0, Ndcg( new[] { "src/xA.cs", "other/src/A.cs.bak" }, grades, 10 ), 9 );
      Assert.Equal( 1.0, Ndcg( new[] { "./src/A.cs", "eShopOnWeb/src/A.cs" }, grades, 10 ), 9 );
   }

   /// <summary>
   /// Collapsing chunks to files keeps the first occurrence of each file, in order.
   /// </summary>
   [Fact]
   public void FirstPerFile_KeepsFirstOccurrence()
   {
      var files = (IReadOnlyList<string>)CallMath( "FirstPerFile", new object[] { new[] { "a.cs", "b.cs", "a.cs", "c.cs", "b.cs" } } )!;
      Assert.Equal( new[] { "a.cs", "b.cs", "c.cs" }, files );
   }

   /// <summary>
   /// Means skip "not measured" (NaN) values and are NaN when nothing was measured.
   /// </summary>
   [Fact]
   public void MeanOfNumbers_SkipsNaN()
   {
      Assert.Equal( 0.5, (double)CallMath( "MeanOfNumbers", new object[] { new[] { 0.0, double.NaN, 1.0 } } )!, 9 );
      Assert.True( double.IsNaN( (double)CallMath( "MeanOfNumbers", new object[] { new[] { double.NaN } } )! ) );
   }

   /// <summary>
   /// The ground-truth heap keeps exactly what a full sort keeps: best score first, ties
   /// broken by the lower row, across merges of partial heaps.
   /// </summary>
   [Fact]
   public void TopK_MatchesFullSortWithTies()
   {
      var random = new Random( 5 );
      float[] scores = Enumerable.Range( 0, 5000 ).Select( _ => (float)Math.Round( random.NextDouble(), 2 ) ).ToArray();
      List<int> expected = Enumerable.Range( 0, scores.Length ).OrderByDescending( i => scores[i] ).ThenBy( i => i ).Take( 25 ).ToList();
      object whole = NewTopK( 25 );
      object left = NewTopK( 25 );
      object right = NewTopK( 25 );
      for( int row = 0; row < scores.Length; row++ )
      {
         Offer( whole, scores[row], row );
         Offer( row % 2 == 0 ? left : right, scores[row], row );
      }

      left.GetType().GetMethod( "Merge" )!.Invoke( left, new[] { right } );
      Assert.Equal( expected, SortedRows( whole ) );
      Assert.Equal( expected, SortedRows( left ) );
   }

   #endregion Public Methods

   #region Private Methods

   /// <summary>
   /// Compiles BenchMath.cs and TopK.cs from the repository into an in-memory assembly.
   /// </summary>
   /// <returns>The assembly.</returns>
   private static Assembly Compile()
   {
      string bench = Path.Combine( RepoRoot(), "src", "GenericVectorBuilder.Bench" );
      var trees = new List<SyntaxTree>
      {
         CSharpSyntaxTree.ParseText( File.ReadAllText( Path.Combine( bench, "Stats", "BenchMath.cs" ) ) ),
         CSharpSyntaxTree.ParseText( File.ReadAllText( Path.Combine( bench, "Truth", "TopK.cs" ) ) ),
         CSharpSyntaxTree.ParseText( "global using System; global using System.Collections.Generic; global using System.Linq;" ),
      };
      IEnumerable<MetadataReference> references = ( (string)AppContext.GetData( "TRUSTED_PLATFORM_ASSEMBLIES" )! )
         .Split( Path.PathSeparator ).Select( p => MetadataReference.CreateFromFile( p ) );
      var options = new CSharpCompilationOptions( OutputKind.DynamicallyLinkedLibrary, nullableContextOptions: NullableContextOptions.Enable );
      CSharpCompilation compilation = CSharpCompilation.Create( "BenchMathUnderTest", trees, references, options );
      using var image = new MemoryStream();
      Microsoft.CodeAnalysis.Emit.EmitResult result = compilation.Emit( image );
      Assert.True( result.Success, string.Join( Environment.NewLine, result.Diagnostics.Where( d => d.Severity == DiagnosticSeverity.Error ) ) );
      return Assembly.Load( image.ToArray() );
   }

   /// <summary>
   /// Calls a static BenchMath method.
   /// </summary>
   /// <param name="method">Method name.</param>
   /// <param name="arguments">Arguments.</param>
   /// <returns>The result.</returns>
   private static object? CallMath( string method, object?[] arguments )
   {
      return COMPILED.Value.GetType( MATH_TYPE )!.GetMethod( method )!.Invoke( null, arguments );
   }

   /// <summary>BenchMath.Percentile.</summary>
   /// <param name="values">Samples.</param>
   /// <param name="percentile">0 to 100.</param>
   /// <returns>The percentile.</returns>
   private static double Percentile( List<double> values, double percentile )
   {
      return (double)CallMath( "Percentile", new object[] { values, percentile } )!;
   }

   /// <summary>BenchMath.RecallAtK.</summary>
   /// <param name="truth">Exact top k.</param>
   /// <param name="kth">k-th best exact score.</param>
   /// <param name="hits">Returned ids.</param>
   /// <param name="score">Exact score of an id.</param>
   /// <param name="k">Cut-off.</param>
   /// <returns>Recall.</returns>
   private static double Recall( List<Guid> truth, double kth, List<Guid> hits, Func<Guid, double?> score, int k )
   {
      return (double)CallMath( "RecallAtK", new object[] { truth, kth, hits, score, k } )!;
   }

   /// <summary>BenchMath.NdcgAtK.</summary>
   /// <param name="files">Ranked files.</param>
   /// <param name="grades">Labels.</param>
   /// <param name="k">Cut-off.</param>
   /// <returns>nDCG.</returns>
   private static double Ndcg( string[] files, Dictionary<string, int> grades, int k )
   {
      return (double)CallMath( "NdcgAtK", new object[] { files, grades, k } )!;
   }

   /// <summary>Creates a TopK heap.</summary>
   /// <param name="k">Size.</param>
   /// <returns>The heap.</returns>
   private static object NewTopK( int k )
   {
      return Activator.CreateInstance( COMPILED.Value.GetType( TOPK_TYPE )!, k )!;
   }

   /// <summary>Calls TopK.Offer.</summary>
   /// <param name="heap">The heap.</param>
   /// <param name="score">Score.</param>
   /// <param name="row">Row.</param>
   private static void Offer( object heap, float score, int row )
   {
      heap.GetType().GetMethod( "Offer" )!.Invoke( heap, new object[] { score, row } );
   }

   /// <summary>Reads TopK.Sorted().Rows.</summary>
   /// <param name="heap">The heap.</param>
   /// <returns>Rows, best first.</returns>
   private static int[] SortedRows( object heap )
   {
      object sorted = heap.GetType().GetMethod( "Sorted" )!.Invoke( heap, null )!;
      return (int[])sorted.GetType().GetField( "Item1" )!.GetValue( sorted )!;
   }

   /// <summary>
   /// Distinct ids, deterministic per seed.
   /// </summary>
   /// <param name="count">How many.</param>
   /// <param name="seed">Seed.</param>
   /// <returns>The ids.</returns>
   private static List<Guid> Ids( int count, int seed = 1 )
   {
      return Enumerable.Range( seed, count ).Select( i => new Guid( i, 0, 0, new byte[8] ) ).ToList();
   }

   /// <summary>
   /// Walks up from the test binary to the folder holding the solution file.
   /// </summary>
   /// <returns>The repository root.</returns>
   private static string RepoRoot()
   {
      var dir = new DirectoryInfo( AppContext.BaseDirectory );
      while( dir != null && !File.Exists( Path.Combine( dir.FullName, "GenericVectorBuilder.slnx" ) ) )
      {
         dir = dir.Parent;
      }

      return dir?.FullName ?? throw new InvalidOperationException( "Repository root not found." );
   }

   #endregion Private Methods
}
