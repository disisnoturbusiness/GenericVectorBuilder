using GenericVectorBuilder.Core.Contracts;
using GenericVectorBuilder.Engines.Common;

namespace GenericVectorBuilder.Engines.Tests;

/// <summary>
/// One contract every engine sink must pass, run against the real container. Each engine adds a
/// tiny subclass that only says how to create its sink, so every engine is held to exactly the
/// same bar: create, verify dimension, upsert, count, search, exact search, delete, idempotency.
/// Why one shared test: the benchmark compares engines, and a comparison is only fair if every
/// engine stores and returns the same thing. These tests need the engine's container running
/// (see deploy/engines/README.md) and FAIL, not skip, when it is down.
/// </summary>
public abstract class SinkContractTests : IAsyncLifetime
{
   #region Data Members

   private const int DIMENSION = 128;
   private const int COUNT = 2000;
   private const int TOP = 10;
   private const double MIN_RECALL = 0.9;

   private readonly string _collection = "gvbct_" + Guid.NewGuid().ToString( "N" )[..10];
   private ISink _sink = null!;
   private List<VectorRecord> _records = null!;

   #endregion Data Members

   #region Public Methods

   /// <summary>
   /// Creates the sink and a deterministic random data set of normalized vectors.
   /// </summary>
   public Task InitializeAsync()
   {
      _sink = CreateSink();
      _records = MakeRecords( COUNT, seed: 42 );
      return Task.CompletedTask;
   }

   /// <summary>
   /// Drops the throwaway collection whatever happened.
   /// </summary>
   public async Task DisposeAsync()
   {
      await _sink.DropCollectionAsync( _collection, CancellationToken.None );
      ( _sink as IDisposable )?.Dispose();
   }

   /// <summary>
   /// First ensure creates, second ensure verifies, a different dimension is refused.
   /// </summary>
   [Fact]
   public async Task Ensure_CreatesThenVerifiesDimension()
   {
      Assert.True( await _sink.EnsureCollectionAsync( _collection, DIMENSION, CancellationToken.None ) );
      Assert.False( await _sink.EnsureCollectionAsync( _collection, DIMENSION, CancellationToken.None ) );
      await Assert.ThrowsAnyAsync<Exception>( () => _sink.EnsureCollectionAsync( _collection, DIMENSION * 2, CancellationToken.None ) );
   }

   /// <summary>
   /// Everything upserted is counted, a re-upsert does not duplicate, and deletes remove.
   /// </summary>
   [Fact]
   public async Task Upsert_Count_Idempotent_Delete()
   {
      await _sink.EnsureCollectionAsync( _collection, DIMENSION, CancellationToken.None );
      await _sink.UpsertAsync( _collection, _records, CancellationToken.None );
      await _sink.UpsertAsync( _collection, _records.Take( 100 ).ToList(), CancellationToken.None );
      Assert.Equal( COUNT, await CountEventuallyAsync( COUNT ) );

      await _sink.DeleteAsync( _collection, _records.Take( 50 ).Select( r => r.Chunk.ChunkId ).ToList(), CancellationToken.None );
      Assert.Equal( COUNT - 50, await CountEventuallyAsync( COUNT - 50 ) );
   }

   /// <summary>
   /// The default search finds a stored vector's own record first, returns payload intact, and
   /// reaches the recall bar against an in-memory brute force over 30 queries.
   /// </summary>
   [Fact]
   public async Task Search_ReturnsPayloadAndMeetsRecall()
   {
      await LoadAsync();
      VectorRecord probe = _records[123];
      IReadOnlyList<SearchHit> hits = await _sink.SearchAsync( _collection, probe.Vector, TOP, CancellationToken.None );
      Assert.Equal( probe.Chunk.ChunkId, hits[0].ChunkId );
      Assert.Equal( probe.Chunk.Text, hits[0].Text );
      Assert.Equal( probe.Document.DocKey, hits[0].DocKey );
      Assert.Equal( probe.Document.Table, hits[0].Table );
      Assert.InRange( hits[0].Score, 0.99, 1.01 );

      double recall = await RecallAsync( ( v, ct ) => _sink.SearchAsync( _collection, v, TOP, ct ) );
      Assert.True( recall >= MIN_RECALL, $"recall@{TOP} {recall:F3} below {MIN_RECALL}" );
   }

   /// <summary>
   /// Engines with an exact mode must match the brute force exactly.
   /// </summary>
   [Fact]
   public async Task ExactSearch_MatchesBruteForce()
   {
      if( _sink is not IExactSearchSink exact )
      {
         return;
      }

      await LoadAsync();
      double recall = await RecallAsync( ( v, ct ) => exact.SearchExactAsync( _collection, v, TOP, ct ) );
      Assert.Equal( 1.0, recall, 3 );
   }

   /// <summary>
   /// Every engine describes itself for the report.
   /// </summary>
   [Fact]
   public void Engine_DescribesItself()
   {
      var description = Assert.IsAssignableFrom<IEngineDescription>( _sink );
      Assert.False( string.IsNullOrWhiteSpace( description.Engine ) );
      Assert.False( string.IsNullOrWhiteSpace( description.IndexDescription ) );
      Assert.True( File.Exists( Path.Combine( RepoRoot(), "deploy", "engines", description.ComposeFile ) ) || description.ComposeFile == "embedded" );
   }

   #endregion Public Methods

   #region Private Methods

   /// <summary>
   /// Creates the engine's sink with its defaults (the local container).
   /// </summary>
   /// <returns>The sink under test.</returns>
   protected abstract ISink CreateSink();

   /// <summary>
   /// Creates the collection and loads the data set, waiting until the engine counts all of it
   /// (some engines index asynchronously).
   /// </summary>
   private async Task LoadAsync()
   {
      await _sink.EnsureCollectionAsync( _collection, DIMENSION, CancellationToken.None );
      await _sink.UpsertAsync( _collection, _records, CancellationToken.None );
      await CountEventuallyAsync( COUNT );
   }

   /// <summary>
   /// Polls the count for up to 60 seconds until it reaches the expected value.
   /// </summary>
   /// <param name="expected">Expected count.</param>
   /// <returns>The last count read.</returns>
   private async Task<long> CountEventuallyAsync( long expected )
   {
      long count = -1;
      for( int i = 0; i < 120 && count != expected; i++ )
      {
         count = await _sink.CountAsync( _collection, CancellationToken.None );
         if( count != expected )
         {
            await Task.Delay( 500 );
         }
      }

      return count;
   }

   /// <summary>
   /// Average recall@10 of a search function against in-memory brute force over 30 queries
   /// that are fresh random vectors (not stored ones).
   /// </summary>
   /// <param name="search">The search to measure.</param>
   /// <returns>Mean recall.</returns>
   private async Task<double> RecallAsync( Func<float[], CancellationToken, Task<IReadOnlyList<SearchHit>>> search )
   {
      List<VectorRecord> queries = MakeRecords( 30, seed: 7 );
      double total = 0;
      foreach( VectorRecord q in queries )
      {
         var truth = _records.OrderByDescending( r => Dot( r.Vector, q.Vector ) ).Take( TOP ).Select( r => r.Chunk.ChunkId ).ToHashSet();
         IReadOnlyList<SearchHit> hits = await search( q.Vector, CancellationToken.None );
         total += hits.Take( TOP ).Count( h => truth.Contains( h.ChunkId ) ) / (double)TOP;
      }

      return total / queries.Count;
   }

   /// <summary>
   /// Deterministic normalized random records with distinct payloads.
   /// </summary>
   /// <param name="count">How many.</param>
   /// <param name="seed">Random seed.</param>
   /// <returns>The records.</returns>
   private static List<VectorRecord> MakeRecords( int count, int seed )
   {
      var random = new Random( seed );
      var records = new List<VectorRecord>( count );
      for( int i = 0; i < count; i++ )
      {
         float[] v = Enumerable.Range( 0, DIMENSION ).Select( _ => (float)( random.NextDouble() * 2 - 1 ) ).ToArray();
         float norm = MathF.Sqrt( v.Sum( x => x * x ) );
         v = v.Select( x => x / norm ).ToArray();
         var document = new Document( $"doc|{seed}|{i}", $"table{i % 3}", $"file{i % 7}.csv", $"text {i}", $"hash{i}",
            new Dictionary<string, string> { ["row"] = i.ToString() } );
         var chunk = new Chunk( new Guid( System.Security.Cryptography.MD5.HashData( System.Text.Encoding.UTF8.GetBytes( $"{seed}:{i}" ) ) ), document.DocKey, 0, $"chunk text {seed}-{i}" );
         records.Add( new VectorRecord( document, chunk, v ) );
      }

      return records;
   }

   /// <summary>
   /// Dot product (cosine for normalized vectors).
   /// </summary>
   /// <param name="a">First vector.</param>
   /// <param name="b">Second vector.</param>
   /// <returns>The dot product.</returns>
   private static double Dot( float[] a, float[] b )
   {
      double sum = 0;
      for( int i = 0; i < a.Length; i++ )
      {
         sum += a[i] * b[i];
      }

      return sum;
   }

   /// <summary>
   /// Walks up from the test binary to the folder that holds the solution file.
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
