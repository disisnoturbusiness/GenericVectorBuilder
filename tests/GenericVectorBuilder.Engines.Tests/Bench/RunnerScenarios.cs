// Compiled only by RunnerTests, together with the benchmark's own sources (the test project
// does not reference the benchmark console project). BENCH_UNDER_TEST is defined only in that
// compilation, so the test project itself sees an empty file.
#if BENCH_UNDER_TEST
using GenericVectorBuilder.Bench.Cli;
using GenericVectorBuilder.Bench.Data;
using GenericVectorBuilder.Bench.Report;
using GenericVectorBuilder.Bench.Running;
using GenericVectorBuilder.Bench.Targets;
using GenericVectorBuilder.Bench.Truth;
using GenericVectorBuilder.Core.Contracts;
using GenericVectorBuilder.Engines.Common;

namespace GenericVectorBuilder.Bench.UnderTest;

/// <summary>
/// Runs the benchmark's real runner code against fake targets and a fake engine host, and
/// hands back plain results for the tests to check.
/// Why fakes: the rules under test (pass order, a warm-up before every pass, which engines
/// get stopped) are about what the runner does, not about any engine, and a fake makes every
/// call visible and every run take seconds.
/// </summary>
public static class RunnerScenarios
{
   #region Data Members

   /// <summary>Collection every scenario loads, as the benchmark names it.</summary>
   public const string COLLECTION = "gvbbench_t";

   #endregion Data Members

   #region Public Methods

   /// <summary>
   /// The target order the runner uses for a seed.
   /// </summary>
   /// <param name="names">Targets as requested.</param>
   /// <param name="seed">Run seed.</param>
   /// <returns>Targets in run order.</returns>
   public static string[] TargetOrder( string[] names, int seed )
   {
      return RunOrder.ShuffleTargets( names, seed ).ToArray();
   }

   /// <summary>
   /// The pass order the runner uses for a target and a seed.
   /// </summary>
   /// <param name="concurrency">Concurrency levels.</param>
   /// <param name="exact">True when the target has an exact mode.</param>
   /// <param name="seed">Run seed.</param>
   /// <param name="target">Target name.</param>
   /// <returns>Passes in run order.</returns>
   public static string[] PassOrder( int[] concurrency, bool exact, int seed, string target )
   {
      return RunOrder.ShufflePasses( RunOrder.Passes( concurrency, exact ), seed, target ).ToArray();
   }

   /// <summary>
   /// Searches one fake target with the real <see cref="SearchRunner"/> and records every
   /// search call and every progress line in one ordered trace: "S" is a default search, "X" an
   /// exact search, "LOG ..." a progress line.
   /// </summary>
   /// <param name="warmup">Warm-up searches per pass.</param>
   /// <param name="seed">Run seed.</param>
   /// <param name="failFirst">How many of the first default searches throw.</param>
   /// <returns>The trace, the pass order, warm-up errors and timed errors.</returns>
   public static async Task<SearchScenario> SearchAsync( int warmup, int seed, int failFirst )
   {
      var trace = new CallTrace();
      PipelineData data = Data( 30, 4 );
      BenchOptions options = BenchOptions.Parse( new[] { "bench", "--pipeline", "t", "--targets", "fake", "--warmup", warmup.ToString(),
         "--seconds", "1", "--exact-seconds", "5", "--concurrency", "1,4", "--queries", "random:5", "--top", "3" } );
      var sink = new FakeFullSink( "fake", trace ) { FailFirst = failFirst };
      await sink.UpsertAsync( COLLECTION, data.Records( 0, data.Count ), CancellationToken.None );
      QuerySet queries = QuerySet.Random( data, 5 );
      var runner = new SearchRunner( data, queries, BruteForce.Compute( data, queries, options.Top ), options, seed ) { WarmupTime = TimeSpan.Zero };
      SearchOutcome outcome = await runner.RunAsync( Target( sink, null ), COLLECTION, line => trace.Add( "LOG " + line ), CancellationToken.None );
      string[] searching = trace.ToArray().Where( e => e != "upsert" ).ToArray();
      return new SearchScenario( searching, outcome.PassOrder.ToArray(), outcome.WarmupErrors, outcome.Report.Errors );
   }

   /// <summary>
   /// Measures fake compose-hosted targets one after another with the real
   /// <see cref="EngineLifecycle"/> and <see cref="TargetRunner"/>, the way run-all does.
   /// </summary>
   /// <param name="command">"run-all" or "bench".</param>
   /// <param name="targets">Per target: name and compose file.</param>
   /// <param name="runningBefore">Compose files running when the run starts.</param>
   /// <param name="downBeforeTurn">Compose files that go down after the snapshot, before their turn.</param>
   /// <param name="failUp">Compose files whose start fails half-way.</param>
   /// <returns>The host's calls, which compose files run at the end, and each target's error.</returns>
   public static async Task<LifecycleScenario> RunAllAsync( string command, (string Name, string Compose)[] targets, string[] runningBefore, string[] downBeforeTurn, string[] failUp )
   {
      var host = new FakeHost( runningBefore, failUp );
      BenchOptions options = BenchOptions.Parse( new[] { command, "--pipeline", "t", "--targets", string.Join( ',', targets.Select( t => t.Name ) ),
         "--warmup", "2", "--seconds", "1", "--exact-seconds", "1", "--concurrency", "1", "--queries", "random:3", "--top", "3" } );
      var lifecycle = new EngineLifecycle( host, command == "run-all", _ => { } );
      List<BenchTarget> fakes = targets.Select( t => Target( new FakeFullSink( t.Name, new CallTrace() ), t.Compose ) ).ToList();
      await lifecycle.SnapshotAsync( fakes.Select( f => f.ComposePath ), CancellationToken.None );
      downBeforeTurn.ToList().ForEach( path => host.Running[path] = false );
      var measurer = new TargetRunner( options, lifecycle, _ => { } );
      PipelineData data = Data( 20, 4 );
      SearchRunner runner = Runner( data, options );
      var errors = new List<string?>();
      foreach( BenchTarget fake in fakes )
      {
         TargetReport report = await measurer.MeasureAsync( fake, data, runner, CancellationToken.None );
         errors.Add( report.Error ?? ( report.Search == null ? "not searched" : null ) );
      }

      return new LifecycleScenario( host.Calls.ToArray(), host.Running.Where( p => p.Value ).Select( p => p.Key ).OrderBy( p => p ).ToArray(), errors.ToArray() );
   }

   /// <summary>
   /// run-all with machine control's engine CPU set: an engine the run starts (and stops) is
   /// asked to be created on those CPUs; one that was running before the run but went down before
   /// its turn is started unrestricted, because run-all leaves it running as it found it.
   /// </summary>
   /// <param name="targets">Per target: name and compose file.</param>
   /// <param name="runningBefore">Compose files running when the run starts.</param>
   /// <param name="downBeforeTurn">Compose files that go down after the snapshot, before their turn.</param>
   /// <param name="hostApplies">True for a host that creates the containers on the CPU set and reads it back; false for one that cannot.</param>
   /// <returns>The host's calls and each target's start note.</returns>
   public static async Task<PinnedLifecycleScenario> RunAllPinnedAsync( (string Name, string Compose)[] targets, string[] runningBefore, string[] downBeforeTurn, bool hostApplies )
   {
      var host = new FakeHost( runningBefore, Array.Empty<string>() ) { AppliesCpuset = hostApplies };
      var lifecycle = new EngineLifecycle( host, true, _ => { }, "2-3,6-7" );
      List<BenchTarget> fakes = targets.Select( t => Target( new FakeFullSink( t.Name, new CallTrace() ), t.Compose ) ).ToList();
      await lifecycle.SnapshotAsync( fakes.Select( f => f.ComposePath ), CancellationToken.None );
      downBeforeTurn.ToList().ForEach( path => host.Running[path] = false );
      var notes = new List<string>();
      foreach( BenchTarget fake in fakes )
      {
         var mine = new List<string>();
         await lifecycle.EnsureRunningAsync( fake, mine, CancellationToken.None );
         await lifecycle.ReleaseAsync( fake, mine );
         notes.Add( string.Join( " | ", mine ) );
      }

      return new PinnedLifecycleScenario( host.Calls.ToArray(), notes.ToArray() );
   }

   /// <summary>
   /// Loads and searches one fake target with the real <see cref="TargetRunner"/> and writes the
   /// results with the real <see cref="ResultsWriter"/>.
   /// </summary>
   /// <param name="finisher">True for a sink with an index step; false for one with no index proof.</param>
   /// <param name="readyAfterFinish">Whether the fake index says it is ready after its step.</param>
   /// <param name="folder">Folder for results.json and results.md.</param>
   /// <returns>The sink's call trace, the target's notes, and the results.json text.</returns>
   public static async Task<TargetScenario> MeasureOneAsync( bool finisher, bool readyAfterFinish, string folder )
   {
      var trace = new CallTrace();
      BenchOptions options = BenchOptions.Parse( new[] { "run-all", "--pipeline", "t", "--warmup", "2", "--seconds", "1", "--exact-seconds", "1",
         "--concurrency", "1", "--queries", "random:3", "--top", "3", "--seed", "42" } );
      FakeSink sink = finisher ? new FakeFullSink( "fake", trace ) { ReadyAfterFinish = readyAfterFinish } : new FakeSink( "plain", trace );
      var measurer = new TargetRunner( options, new EngineLifecycle( new FakeHost( Array.Empty<string>(), Array.Empty<string>() ), true, _ => { } ), _ => { } );
      PipelineData data = Data( 20, 4 );
      TargetReport result = await measurer.MeasureAsync( Target( sink, null ), data, Runner( data, options ), CancellationToken.None );
      var report = new BenchReport { RunSeed = 42, Command = "run-all", Pipeline = "t" };
      report.TargetOrder.Add( result.Name );
      report.Targets.Add( result );
      ResultsWriter.Write( report, folder );
      return new TargetScenario( trace.ToArray(), result.Notes.ToArray(), result.Error, File.ReadAllText( Path.Combine( folder, "results.json" ) ) );
   }

   #endregion Public Methods

   #region Private Methods

   /// <summary>
   /// A small deterministic data set: unit-length vectors from a fixed generator.
   /// </summary>
   /// <param name="rows">Rows.</param>
   /// <param name="dimension">Vector length.</param>
   /// <returns>The rows.</returns>
   private static PipelineData Data( int rows, int dimension )
   {
      var data = new PipelineData( "t", rows, dimension ) { HasText = true };
      var random = new Random( 7 );
      for( int row = 0; row < rows; row++ )
      {
         float[] vector = Enumerable.Range( 0, dimension ).Select( _ => (float)( random.NextDouble() - 0.5 ) ).ToArray();
         float norm = MathF.Sqrt( vector.Sum( v => v * v ) );
         data.Add( new Guid( row + 1, 0, 0, new byte[8] ), $"doc{row}.cs", "t", "fake", 0, $"text {row}", null, vector.Select( v => v / norm ).ToArray() );
      }

      return data;
   }

   /// <summary>
   /// A search runner over random queries from the data, with rehearsal, warm-up, window, trial
   /// and extension lengths of a fraction of a second. Why: these scenarios check which engines
   /// start and stop and what the results say, and the benchmark's own lengths (30 s per
   /// rehearsal, 15 s per warm-up) would make every target take minutes.
   /// </summary>
   /// <param name="data">Rows.</param>
   /// <param name="options">Options.</param>
   /// <returns>The runner.</returns>
   private static SearchRunner Runner( PipelineData data, BenchOptions options )
   {
      QuerySet queries = QuerySet.Random( data, options.RandomCount );
      return new SearchRunner( data, queries, BruteForce.Compute( data, queries, options.Top ), options, 42 )
      {
         RehearsalTime = TimeSpan.FromSeconds( 0.2 ),
         WarmupTime = TimeSpan.FromSeconds( 0.3 ),
         WindowTime = TimeSpan.FromSeconds( 0.05 ),
         TrialTime = TimeSpan.FromSeconds( 0.1 ),
         ExtensionMinimum = TimeSpan.FromSeconds( 0.1 ),
         ExtensionCap = TimeSpan.FromSeconds( 0.5 ),
      };
   }

   /// <summary>
   /// Wraps a fake sink as a benchmark target.
   /// </summary>
   /// <param name="sink">The sink.</param>
   /// <param name="composePath">Compose file, or null for an engine that needs no starting.</param>
   /// <returns>The target.</returns>
   private static BenchTarget Target( ISink sink, string? composePath )
   {
      Func<string, CancellationToken, Task<Measurement>> none = ( _, _ ) => Task.FromResult( Measurement.None( "fake" ) );
      return new BenchTarget( sink, "Fake 1.0", "fake flat index", composePath == null ? "embedded" : "compose", composePath, none, none );
   }

   #endregion Private Methods
}

/// <summary>What <see cref="RunnerScenarios.SearchAsync"/> saw.</summary>
/// <param name="Events">Search calls and progress lines, in order.</param>
/// <param name="PassOrder">Passes as the runner reported them.</param>
/// <param name="WarmupErrors">Failed warm-up searches.</param>
/// <param name="TimedErrors">Failed timed searches.</param>
public sealed record SearchScenario( string[] Events, string[] PassOrder, int WarmupErrors, int TimedErrors );

/// <summary>What <see cref="RunnerScenarios.RunAllAsync"/> saw.</summary>
/// <param name="Calls">Host calls in order, e.g. "up b.compose.yaml".</param>
/// <param name="RunningAfter">Compose files running at the end.</param>
/// <param name="Errors">Per target, its error, "not searched", or null.</param>
public sealed record LifecycleScenario( string[] Calls, string[] RunningAfter, string?[] Errors );

/// <summary>What <see cref="RunnerScenarios.RunAllPinnedAsync"/> saw.</summary>
/// <param name="Calls">Host calls in order, e.g. "up b.yaml on 2-3,6-7".</param>
/// <param name="Notes">Per target, its lifecycle notes joined by " | ".</param>
public sealed record PinnedLifecycleScenario( string[] Calls, string[] Notes );

/// <summary>What <see cref="RunnerScenarios.MeasureOneAsync"/> saw.</summary>
/// <param name="Events">Sink calls in order.</param>
/// <param name="Notes">The target's notes.</param>
/// <param name="Error">The target's error, or null.</param>
/// <param name="Json">The results.json text.</param>
public sealed record TargetScenario( string[] Events, string[] Notes, string? Error, string Json );

/// <summary>
/// An ordered, thread-safe list of events. Why thread-safe: the throughput passes and their
/// warm-ups search from several workers at once.
/// </summary>
public sealed class CallTrace
{
   #region Data Members

   private readonly List<string> _events = new();

   #endregion Data Members

   #region Public Methods

   /// <summary>
   /// Appends an event.
   /// </summary>
   /// <param name="item">The event.</param>
   public void Add( string item )
   {
      lock( _events )
      {
         _events.Add( item );
      }
   }

   /// <summary>
   /// A copy of the events so far.
   /// </summary>
   /// <returns>The events.</returns>
   public string[] ToArray()
   {
      lock( _events )
      {
         return _events.ToArray();
      }
   }

   #endregion Public Methods
}

/// <summary>
/// An in-memory sink with exact cosine search, a one-millisecond pause per search (so a timed
/// second holds a few thousand calls, not millions), and every call written to a trace.
/// </summary>
public class FakeSink : ISink
{
   #region Data Members

   private readonly Dictionary<Guid, float[]> _rows = new();
   private int _failed;

   #endregion Data Members

   #region Constructor

   /// <summary>
   /// Creates the sink.
   /// </summary>
   /// <param name="name">Sink name.</param>
   /// <param name="trace">Where calls are recorded.</param>
   public FakeSink( string name, CallTrace trace )
   {
      Name = name;
      Calls = trace;
   }

   #endregion Constructor

   #region Public Methods

   /// <summary>Sink name.</summary>
   public string Name { get; }

   /// <summary>Where calls are recorded.</summary>
   public CallTrace Calls { get; }

   /// <summary>How many of the first default searches throw.</summary>
   public int FailFirst { get; init; }

   /// <summary>
   /// Records the call; the collection always exists.
   /// </summary>
   /// <param name="collection">Collection.</param>
   /// <param name="dimension">Dimension.</param>
   /// <param name="ct">Cancellation.</param>
   /// <returns>True.</returns>
   public Task<bool> EnsureCollectionAsync( string collection, int dimension, CancellationToken ct )
   {
      Calls.Add( "ensure" );
      return Task.FromResult( true );
   }

   /// <summary>
   /// Stores the records.
   /// </summary>
   /// <param name="collection">Collection.</param>
   /// <param name="records">Records.</param>
   /// <param name="ct">Cancellation.</param>
   /// <returns>A completed task.</returns>
   public Task UpsertAsync( string collection, IReadOnlyList<VectorRecord> records, CancellationToken ct )
   {
      Calls.Add( "upsert" );
      lock( _rows )
      {
         records.ToList().ForEach( r => _rows[r.Chunk.ChunkId] = r.Vector );
      }

      return Task.CompletedTask;
   }

   /// <summary>
   /// Removes rows.
   /// </summary>
   /// <param name="collection">Collection.</param>
   /// <param name="chunkIds">Ids.</param>
   /// <param name="ct">Cancellation.</param>
   /// <returns>A completed task.</returns>
   public Task DeleteAsync( string collection, IReadOnlyList<Guid> chunkIds, CancellationToken ct )
   {
      lock( _rows )
      {
         chunkIds.ToList().ForEach( id => _rows.Remove( id ) );
      }

      return Task.CompletedTask;
   }

   /// <summary>
   /// Rows held.
   /// </summary>
   /// <param name="collection">Collection.</param>
   /// <param name="ct">Cancellation.</param>
   /// <returns>The count.</returns>
   public Task<long> CountAsync( string collection, CancellationToken ct )
   {
      lock( _rows )
      {
         return Task.FromResult( (long)_rows.Count );
      }
   }

   /// <summary>
   /// Default search: records "S", fails the first <see cref="FailFirst"/> calls.
   /// </summary>
   /// <param name="collection">Collection.</param>
   /// <param name="vector">Query.</param>
   /// <param name="top">Hits.</param>
   /// <param name="ct">Cancellation.</param>
   /// <returns>Hits, best first.</returns>
   public async Task<IReadOnlyList<SearchHit>> SearchAsync( string collection, float[] vector, int top, CancellationToken ct )
   {
      Calls.Add( "S" );
      if( Interlocked.Increment( ref _failed ) <= FailFirst )
      {
         throw new InvalidOperationException( "fake search failure" );
      }

      return await NearestAsync( vector, top, ct );
   }

   /// <summary>
   /// Drops every row.
   /// </summary>
   /// <param name="collection">Collection.</param>
   /// <param name="ct">Cancellation.</param>
   /// <returns>A completed task.</returns>
   public Task DropCollectionAsync( string collection, CancellationToken ct )
   {
      Calls.Add( "drop" );
      lock( _rows )
      {
         _rows.Clear();
      }

      return Task.CompletedTask;
   }

   #endregion Public Methods

   #region Private Methods

   /// <summary>
   /// Exact nearest rows by dot product (the vectors are unit length), after a short pause.
   /// </summary>
   /// <param name="vector">Query.</param>
   /// <param name="top">Hits.</param>
   /// <param name="ct">Cancellation.</param>
   /// <returns>Hits, best first.</returns>
   protected async Task<IReadOnlyList<SearchHit>> NearestAsync( float[] vector, int top, CancellationToken ct )
   {
      await Task.Delay( 1, ct );
      List<KeyValuePair<Guid, float[]>> rows;
      lock( _rows )
      {
         rows = _rows.ToList();
      }

      return rows.Select( r => new SearchHit( r.Key, "doc", "t", string.Empty, r.Value.Zip( vector, ( a, b ) => (double)a * b ).Sum() ) )
         .OrderByDescending( h => h.Score ).Take( top ).ToList();
   }

   #endregion Private Methods
}

/// <summary>
/// A fake sink with everything the benchmark can use: an exact mode (records "X"), an index
/// step (records "finish"), an index state (records "state") and a self-description.
/// </summary>
public sealed class FakeFullSink : FakeSink, IExactSearchSink, IIndexFinisher, IEngineDescription
{
   #region Data Members

   private bool _finished;

   #endregion Data Members

   #region Constructor

   /// <summary>
   /// Creates the sink.
   /// </summary>
   /// <param name="name">Sink name.</param>
   /// <param name="trace">Where calls are recorded.</param>
   public FakeFullSink( string name, CallTrace trace ) : base( name, trace )
   {
   }

   #endregion Constructor

   #region Public Methods

   /// <summary>Whether the fake index reports ready once its step has run.</summary>
   public bool ReadyAfterFinish { get; init; } = true;

   /// <summary>Engine name.</summary>
   public string Engine => "Fake 1.0";

   /// <summary>Index description.</summary>
   public string IndexDescription => "fake flat index";

   /// <summary>Compose file.</summary>
   public string ComposeFile => "fake.compose.yaml";

   /// <summary>Durability statement.</summary>
   public string Durability => "fsync on every commit (fake)";

   /// <summary>
   /// Exact search: records "X".
   /// </summary>
   /// <param name="collection">Collection.</param>
   /// <param name="vector">Query.</param>
   /// <param name="top">Hits.</param>
   /// <param name="ct">Cancellation.</param>
   /// <returns>Hits, best first.</returns>
   public Task<IReadOnlyList<SearchHit>> SearchExactAsync( string collection, float[] vector, int top, CancellationToken ct )
   {
      Calls.Add( "X" );
      return NearestAsync( vector, top, ct );
   }

   /// <summary>
   /// The index step: records "finish".
   /// </summary>
   /// <param name="collection">Collection.</param>
   /// <param name="ct">Cancellation.</param>
   /// <returns>A note.</returns>
   public Task<string> FinishLoadAsync( string collection, CancellationToken ct )
   {
      Calls.Add( "finish" );
      _finished = true;
      return Task.FromResult( "fake index built" );
   }

   /// <summary>
   /// The index state: records "state".
   /// </summary>
   /// <param name="collection">Collection.</param>
   /// <param name="ct">Cancellation.</param>
   /// <returns>Ready once the step has run (unless told otherwise).</returns>
   public async Task<IndexState> GetIndexStateAsync( string collection, CancellationToken ct )
   {
      Calls.Add( "state" );
      long count = await CountAsync( collection, ct );
      return new IndexState( _finished && ReadyAfterFinish, _finished ? count : 0, count, _finished ? "fake index" : "no index yet" );
   }

   #endregion Public Methods
}

/// <summary>
/// A fake docker compose: a running flag per compose file, every call recorded, and optional
/// starts that fail half-way (the engine comes up, then the start reports failure).
/// </summary>
public sealed class FakeHost : IEngineHost
{
   #region Data Members

   private readonly HashSet<string> _failUp;

   #endregion Data Members

   #region Constructor

   /// <summary>
   /// Creates the host.
   /// </summary>
   /// <param name="runningBefore">Compose files running at the start.</param>
   /// <param name="failUp">Compose files whose start fails.</param>
   public FakeHost( IEnumerable<string> runningBefore, IEnumerable<string> failUp )
   {
      runningBefore.ToList().ForEach( path => Running[path] = true );
      _failUp = failUp.ToHashSet();
   }

   #endregion Constructor

   #region Public Methods

   /// <summary>Running flag per compose file.</summary>
   public Dictionary<string, bool> Running { get; } = new();

   /// <summary>Calls in order: "up X" and "down X".</summary>
   public List<string> Calls { get; } = new();

   /// <summary>True to act as a host that creates the containers on the CPU set and reads it back.</summary>
   public bool AppliesCpuset { get; init; }

   /// <summary>
   /// Whether the compose file runs.
   /// </summary>
   /// <param name="composePath">Compose file.</param>
   /// <param name="ct">Cancellation.</param>
   /// <returns>True when running.</returns>
   public Task<bool> IsRunningAsync( string composePath, CancellationToken ct )
   {
      return Task.FromResult( Running.GetValueOrDefault( composePath ) );
   }

   /// <summary>
   /// Starts it, or fails half-way for a file in the fail list.
   /// </summary>
   /// <param name="composePath">Compose file.</param>
   /// <param name="cpuset">CPU set it is asked to be created on, or null.</param>
   /// <param name="ct">Cancellation.</param>
   /// <returns>The read-back line when <see cref="AppliesCpuset"/> and a set was given, else null.</returns>
   public Task<string?> UpAsync( string composePath, string? cpuset, CancellationToken ct )
   {
      Calls.Add( cpuset == null ? $"up {composePath}" : $"up {composePath} on {cpuset}" );
      Running[composePath] = true;
      if( _failUp.Contains( composePath ) )
      {
         return Task.FromException<string?>( new InvalidOperationException( $"Could not start {composePath}: fake failure" ) );
      }

      return Task.FromResult( AppliesCpuset && cpuset != null ? $"{composePath}: 1 container started on CPUs {cpuset} (read back from Docker)" : null );
   }

   /// <summary>
   /// Stops it.
   /// </summary>
   /// <param name="composePath">Compose file.</param>
   /// <param name="ct">Cancellation.</param>
   /// <returns>Null (stopped cleanly).</returns>
   public Task<string?> DownAsync( string composePath, CancellationToken ct )
   {
      Calls.Add( $"down {composePath}" );
      Running[composePath] = false;
      return Task.FromResult<string?>( null );
   }

   #endregion Public Methods
}
#endif
