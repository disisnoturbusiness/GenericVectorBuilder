using GenericVectorBuilder.Bench.Cli;
using GenericVectorBuilder.Bench.Data;
using GenericVectorBuilder.Bench.Report;
using GenericVectorBuilder.Bench.Targets;
using GenericVectorBuilder.Bench.Truth;
using GenericVectorBuilder.Core.Configuration;

namespace GenericVectorBuilder.Bench.Running;

/// <summary>
/// Runs one command end to end: reads the source rows once, builds the queries and the exact
/// answer once, then goes through the targets one at a time in a seeded random order (for
/// run-all: start the engine if it is down, load, search, measure, drop the copy, stop the
/// engine only if this run started it) and rewrites the results after each.
/// Why one target at a time: engines measured side by side would compete for the same CPU,
/// memory and disk, and every number would depend on what else happened to be running.
/// Why a random order: in a fixed order the same query ran faster later in a run, so the order
/// favoured whichever engine came last. The seed is recorded so the order can be repeated.
/// </summary>
public sealed class BenchSession : IDisposable
{
   #region Data Members

   private const string DEFAULT_REPO = "/home/dan/ForClaude/GenericVectorBuilder";
   private const string GOLDEN_CACHE = "~/gvb-data/bench-cache";

   private readonly BenchOptions _options;
   private readonly Action<string> _log;
   private readonly GvbSettings _settings = new();
   private readonly string _repoRoot;
   private readonly TargetFactory _factory;
   private readonly IEngineHost _host = new ComposeEngineHost();

   #endregion Data Members

   #region Constructor

   /// <summary>
   /// Creates the session.
   /// </summary>
   /// <param name="options">Parsed command line.</param>
   /// <param name="log">Progress output.</param>
   public BenchSession( BenchOptions options, Action<string> log )
   {
      _options = options;
      _log = log;
      _repoRoot = FindRepoRoot( options.RepoRoot );
      _factory = new TargetFactory( _settings, _repoRoot, options.HnswEf );
   }

   #endregion Constructor

   #region Public Methods

   /// <summary>
   /// Runs the command.
   /// </summary>
   /// <param name="ct">Cancellation.</param>
   /// <returns>Process exit code: 0 when every target succeeded, 1 when any failed.</returns>
   public async Task<int> RunAsync( CancellationToken ct )
   {
      await _factory.InitializeAsync( ct );
      _factory.Notes.ToList().ForEach( n => _log( $"Note: {n}" ) );
      return _options.Command switch
      {
         "list" => List(),
         "clean" => await CleanAsync( ct ),
         _ => await MeasureAsync( ct ),
      };
   }

   /// <summary>
   /// The repository root: the override, else the folder above the running binary that holds
   /// GenericVectorBuilder.slnx, else the usual location on linus7795.
   /// </summary>
   /// <param name="overrideRoot">--repo, or null.</param>
   /// <returns>The root folder.</returns>
   public static string FindRepoRoot( string? overrideRoot )
   {
      if( overrideRoot != null )
      {
         return Path.GetFullPath( overrideRoot );
      }

      for( var dir = new DirectoryInfo( AppContext.BaseDirectory ); dir != null; dir = dir.Parent )
      {
         if( File.Exists( Path.Combine( dir.FullName, "GenericVectorBuilder.slnx" ) ) )
         {
            return dir.FullName;
         }
      }

      return DEFAULT_REPO;
   }

   #endregion Public Methods

   #region Private Methods

   /// <summary>
   /// replicate, bench or run-all.
   /// </summary>
   /// <param name="ct">Cancellation.</param>
   /// <returns>Exit code.</returns>
   private async Task<int> MeasureAsync( CancellationToken ct )
   {
      DateTime started = DateTime.UtcNow;
      int seed = _options.Seed ?? RunOrder.SeedFromTime( started );
      IReadOnlyList<string> order = RunOrder.ShuffleTargets( _options.Targets.Count > 0 ? _options.Targets : _factory.Names, seed );
      _log( $"Seed {seed} ({( _options.Seed.HasValue ? "from --seed" : "from the start time" )}); target order: {string.Join( ", ", order )}" );
      BenchReport report = await NewReportAsync( started, seed, ct );
      string folder = Path.Combine( _options.OutFolder ?? Path.Combine( _repoRoot, "bench-results" ), $"{started:yyyyMMdd-HHmmss}-{_options.Pipeline}" );
      PipelineData data = await ReadDataAsync( report, ct );
      SearchRunner? runner = _options.Command == "replicate" ? null : await PrepareSearchAsync( data, report, seed, ct );
      List<(string Name, BenchTarget? Target, string? Error)> targets = order.Select( Create ).ToList();
      var lifecycle = new EngineLifecycle( _host, _options.Command == "run-all", _log );
      await lifecycle.SnapshotAsync( targets.Select( t => t.Target?.ComposePath ), ct );
      var measurer = new TargetRunner( _options, lifecycle, _log );
      foreach( (string name, BenchTarget? target, string? error) in targets )
      {
         _log( $"== {name}" );
         report.TargetOrder.Add( name );
         report.Targets.Add( target == null ? new TargetReport { Name = name, Error = error } : await measurer.MeasureAsync( target, data, runner, ct ) );
         ResultsWriter.Write( report, folder );
      }

      report.Notes.AddRange( RunFlags( report ) );
      await DropEmptyDatabaseAsync( _options.Command == "run-all" && !_options.Keep, report );
      ResultsWriter.Write( report, folder );
      _log( $"Results: {Path.Combine( folder, "results.md" )}" );
      return report.Targets.Any( t => t.Error != null ) ? 1 : 0;
   }

   /// <summary>
   /// Reads the source rows once for every target.
   /// </summary>
   /// <param name="report">Report to fill (rows, dimension, source).</param>
   /// <param name="ct">Cancellation.</param>
   /// <returns>The rows.</returns>
   private async Task<PipelineData> ReadDataAsync( BenchReport report, CancellationToken ct )
   {
      var reader = new PipelineReader( _settings.BuildSqlConnectionString(), _settings.SqlDatabase );
      _log( $"Reading dbo.gvb_{_options.Pipeline}{( _options.Limit.HasValue ? $" (first {_options.Limit:N0} rows by ChunkId)" : string.Empty )}..." );
      PipelineData data = await reader.LoadAsync( _options.Pipeline, _options.Limit, withText: _options.Command != "bench", ct );
      report.Rows = data.Count;
      report.Dimension = data.Dimension;
      report.Source = $"Read READ-ONLY from {_settings.SqlDatabase}.dbo.gvb_{_options.Pipeline} in ChunkId order, vectors as {reader.VectorReadMode}, {reader.LoadSeconds:0.0} s.";
      _log( $"{data.Count:N0} rows x {data.Dimension} dims in {reader.LoadSeconds:0.0} s ({reader.VectorReadMode})" );
      return data;
   }

   /// <summary>
   /// Creates a target, turning an unknown name into an error for that target alone.
   /// </summary>
   /// <param name="name">Target name.</param>
   /// <returns>The target, or the reason it could not be created.</returns>
   private (string Name, BenchTarget? Target, string? Error) Create( string name )
   {
      try
      {
         return ( name, _factory.Create( name ), null );
      }
      catch( ArgumentException ex )
      {
         return ( name, null, ex.Message );
      }
   }

   /// <summary>
   /// Run-level warnings gathered from every target, so the problems are listed in one place
   /// at the end of the report as well as under each target.
   /// </summary>
   /// <param name="report">The run.</param>
   /// <returns>The warnings (none when every target was clean).</returns>
   private static IEnumerable<string> RunFlags( BenchReport report )
   {
      List<string> notReady = report.Targets.Where( t => t.IndexState?.AfterLoad is { Ready: false } ).Select( t => t.Name ).ToList();
      if( notReady.Count > 0 )
      {
         yield return $"WARNING: the engine did not report a ready index after the load for: {string.Join( ", ", notReady )}. They were measured anyway; their numbers may come from a scan or a half-built index (see each target's index state).";
      }

      List<string> warmupFailures = report.Targets.Where( t => t.WarmupErrors > 0 ).Select( t => $"{t.Name} ({t.WarmupErrors})" ).ToList();
      if( warmupFailures.Count > 0 )
      {
         yield return $"WARNING: warm-up searches failed for: {string.Join( ", ", warmupFailures )}. They are not in the error counts of the timed passes.";
      }

      List<string> unstated = report.Targets.Where( t => t.Durability is null or "not stated" && t.Error == null ).Select( t => t.Name ).ToList();
      if( unstated.Count > 0 )
      {
         yield return $"Durability not stated for: {string.Join( ", ", unstated )}.";
      }
   }

   /// <summary>
   /// Builds the queries and the exact answer, shared by every target.
   /// </summary>
   /// <param name="data">Loaded rows.</param>
   /// <param name="report">Report to fill.</param>
   /// <param name="seed">The run's seed (fixes each target's pass order).</param>
   /// <param name="ct">Cancellation.</param>
   /// <returns>The search runner.</returns>
   private async Task<SearchRunner> PrepareSearchAsync( PipelineData data, BenchReport report, int seed, CancellationToken ct )
   {
      QuerySet queries = _options.IsGolden
         ? await GoldenQueries.LoadAsync( _options.GoldenFile, _options.Pipeline, _settings, GvbSettings.Expand( GOLDEN_CACHE ), ct )
         : QuerySet.Random( data, _options.RandomCount );
      _log( $"Queries: {queries.Description}" );
      TruthSet truth = BruteForce.Compute( data, queries, _options.Top );
      _log( $"Exact answer of {queries.Count} queries by brute force in {truth.Seconds:0.0} s" );
      var runner = new SearchRunner( data, queries, truth, _options, seed );
      report.Queries = queries.Description;
      report.QueryCount = queries.Count;
      report.TruthSeconds = truth.Seconds;
      report.TruthNdcg = runner.TruthNdcg();
      return runner;
   }

   /// <summary>
   /// The run-level part of the report: when, how, on what, in what order, and the method notes.
   /// </summary>
   /// <param name="started">When the run started.</param>
   /// <param name="seed">The run's seed.</param>
   /// <param name="ct">Cancellation.</param>
   /// <returns>The report, without targets.</returns>
   private async Task<BenchReport> NewReportAsync( DateTime started, int seed, CancellationToken ct )
   {
      var report = new BenchReport
      {
         StartedUtc = started.ToString( "yyyy-MM-ddTHH:mm:ssZ" ),
         RunSeed = seed,
         Command = _options.Command,
         CommandLine = Environment.CommandLine,
         Machine = await MachineFacts.ReadAsync( ct ),
         Pipeline = _options.Pipeline,
         Collection = _options.Collection,
         Top = _options.Top,
         Concurrency = _options.Concurrency,
         SecondsPerLevel = _options.Seconds,
      };
      if( MachineFacts.IsBusy( report.Machine.LoadAverage ) )
      {
         report.Notes.Add( $"WARNING: load average {report.Machine.LoadAverage} on {report.Machine.LogicalCpus} logical CPUs when the run started. Other work was competing for the CPU, so absolute latency and QPS are worse than this box can do; compare engines only within one run, and rerun on a quiet box before quoting numbers." );
      }

      report.Notes.AddRange( MethodNotes( seed ) );
      report.Notes.AddRange( _factory.Notes );
      return report;
   }

   /// <summary>
   /// How every number was measured, printed with the results.
   /// </summary>
   /// <param name="seed">The run's seed.</param>
   /// <returns>The notes.</returns>
   private IEnumerable<string> MethodNotes( int seed )
   {
      yield return $"Order: targets ran one at a time in a random order from seed {seed} (runSeed; --seed {seed} repeats it, targetOrder lists it). Inside each target the timed passes also ran in a random order from the same seed and the target's name (passOrder): default@1 is latency one query at a time (which also gives recall) followed by throughput with one searcher, default@N is throughput with N searchers, exact is the engine's exact mode.";
      yield return $"Warm-up: every timed pass started with its own untimed warm-up of {_options.Warmup} searches in the same search mode and with the same number of searchers, stopped early after 60 s. Failed warm-up searches are counted per target (warmupErrors) and are not in the timed error counts.";
      yield return $"Load rows/s counts only time inside each target's upsert calls: one writer, batches of {_options.Batch}, rows already in memory, the collection dropped and created fresh first. Engines that build or finish their index after the writes do it in a separate timed index step (the load's index seconds), and the run waits for it before searching.";
      yield return "Index proof: each engine's own report of its index (indexState) is read after the load and again after the last pass. A target whose index was not ready after the load was still measured and carries a WARNING; an engine that reports nothing counts as not ready.";
      yield return "Durability: each target's crash-safety setting as configured here (durability). Engines that do not force writes to disk on every commit load faster for that reason.";
      yield return "Latency is client-side wall time around each search (network and driver included), one query at a time; at least 200 samples (small query sets are repeated).";
      yield return $"QPS: N workers searching back to back for {_options.Seconds} s per level; completed searches divided by elapsed time.";
      yield return $"Recall@{_options.Top}: share of the exact top {_options.Top} (brute force in memory) that the engine returned. A hit whose exact similarity ties the {_options.Top}th best (within 1e-5) also counts, because duplicate rows embed to identical vectors.";
      yield return "RAM of always-on servers (SQL Server, Qdrant) is the whole process, including every other database or collection it serves; for compose engines it is docker stats of the engine's containers. Disk is the table's reserved pages (SQL), the collection folder (Qdrant), or for other engines what the load added to the engine's data folder (engines that keep data in memory until a snapshot show almost nothing).";
      yield return _options.Command == "run-all"
         ? "Engines: run-all starts an engine that is down and stops it afterwards only if it was not running when the run began; an engine that was already running is left running."
         : "Engines: this command starts and stops nothing; every engine had to be running already.";
   }

   /// <summary>
   /// Prints every target with its engine, index and hosting.
   /// </summary>
   /// <returns>Exit code.</returns>
   private int List()
   {
      foreach( string name in _options.Targets.Count > 0 ? _options.Targets : _factory.Names )
      {
         try
         {
            BenchTarget target = _factory.Create( name );
            string compose = target.ComposePath == null ? string.Empty : File.Exists( target.ComposePath ) ? $", {target.ComposePath}" : $", MISSING {target.ComposePath}";
            _log( $"{name,-14} {target.Hosting}{compose}\n               {target.Engine}\n               {target.Index}" );
         }
         catch( ArgumentException ex )
         {
            _log( $"{name,-14} {ex.Message}" );
         }
      }

      return 0;
   }

   /// <summary>
   /// Drops the benchmark collection from each named target.
   /// </summary>
   /// <param name="ct">Cancellation.</param>
   /// <returns>Exit code: 1 when any drop failed.</returns>
   private async Task<int> CleanAsync( CancellationToken ct )
   {
      int failures = 0;
      foreach( string name in _options.Targets )
      {
         try
         {
            BenchTarget target = _factory.Create( name );
            await target.Sink.DropCollectionAsync( _options.Collection, ct );
            _log( $"{name}: dropped {_options.Collection}" );
         }
         catch( Exception ex ) when( ex is not OperationCanceledException )
         {
            failures++;
            _log( $"{name}: could not drop {_options.Collection}: {ex.Message}" );
         }
      }

      await DropEmptyDatabaseAsync( true, null );
      return failures == 0 ? 0 : 1;
   }

   /// <summary>
   /// Drops the "sql" target's benchmark database when it is empty. Best effort.
   /// </summary>
   /// <param name="wanted">False to skip (bench and replicate keep their copies).</param>
   /// <param name="report">Report for a note, or null.</param>
   private async Task DropEmptyDatabaseAsync( bool wanted, BenchReport? report )
   {
      try
      {
         if( wanted && await _factory.DropEmptyBenchDatabaseAsync( CancellationToken.None ) )
         {
            _log( $"Dropped the empty benchmark database {TargetFactory.SQL_BENCH_DATABASE}." );
            report?.Notes.Add( $"The empty benchmark database {TargetFactory.SQL_BENCH_DATABASE} was dropped at the end." );
         }
      }
      catch( Microsoft.Data.SqlClient.SqlException ex )
      {
         _log( $"Could not drop {TargetFactory.SQL_BENCH_DATABASE}: {ex.Message}" );
      }
   }

   #endregion Private Methods

   #region IDisposable

   /// <summary>
   /// Disposes the targets' clients.
   /// </summary>
   public void Dispose()
   {
      _factory.Dispose();
   }

   #endregion IDisposable
}
