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
/// For bench and run-all the run also takes control of the machine (<see cref="MachineControl"/>):
/// performance governor, engine and client on separate physical cores (an engine run-all starts
/// is created on its cores), each pass's warm-up held while the box is busy, all put back in a
/// finally block and recorded under "conditions", with the CPU idle settings and how each target
/// was reached.
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
   private readonly IMachineSystem _system;

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
      _system = new LinuxMachineSystem( _settings.BuildSqlConnectionString );
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
   /// replicate, bench or run-all. Machine control starts before anything is read and is put
   /// back in the finally block, whatever happens in between.
   /// </summary>
   /// <param name="ct">Cancellation.</param>
   /// <returns>Exit code.</returns>
   private async Task<int> MeasureAsync( CancellationToken ct )
   {
      DateTime started = DateTime.UtcNow;
      int seed = _options.Seed ?? RunOrder.SeedFromTime( started );
      IReadOnlyList<string> order = RunOrder.ShuffleTargets( _options.Targets.Count > 0 ? _options.Targets : _factory.Names, seed );
      _log( $"Seed {seed} ({( _options.Seed.HasValue ? "from --seed" : "from the start time" )}); target order: {string.Join( ", ", order )}" );
      using MachineControl machine = await MachineControl.StartAsync( MachineConditions.ForRun( _options ), MachineOptions(), _system, _log, ct );
      BenchReport report = await NewReportAsync( started, seed, ct );
      string folder = Path.Combine( _options.OutFolder ?? Path.Combine( _repoRoot, "bench-results" ), $"{started:yyyyMMdd-HHmmss}-{_options.Pipeline}" );
      try
      {
         await MeasureTargetsAsync( report, folder, order, seed, machine, ct );
      }
      finally
      {
         await FinishMachineAsync( machine, report, folder );
      }

      _log( $"Results: {Path.Combine( folder, "results.md" )}" );
      return report.Targets.Any( t => t.Error != null ) ? 1 : 0;
   }

   /// <summary>
   /// Reads the rows, prepares the queries, then measures every target in order, rewriting the
   /// results after each.
   /// </summary>
   /// <param name="report">The run's report.</param>
   /// <param name="folder">The run's results folder.</param>
   /// <param name="order">Targets in run order.</param>
   /// <param name="seed">The run's seed.</param>
   /// <param name="machine">Machine control (on or off).</param>
   /// <param name="ct">Cancellation.</param>
   private async Task MeasureTargetsAsync( BenchReport report, string folder, IReadOnlyList<string> order, int seed, MachineControl machine, CancellationToken ct )
   {
      PipelineData data = await ReadDataAsync( report, ct );
      SearchRunner? runner = _options.Command == "replicate" ? null : await PrepareSearchAsync( data, report, seed, ct );
      List<(string Name, BenchTarget? Target, string? Error)> targets = order.Select( Create ).ToList();
      Action<string> log = machine.WrapLog( _log );
      var lifecycle = new EngineLifecycle( machine.WrapHost( _host ), _options.Command == "run-all", log, machine.EngineCpus );
      await lifecycle.SnapshotAsync( targets.Select( t => t.Target?.ComposePath ), ct );
      var connections = new ConnectionRecorder( _system, _settings.SqlServer, _settings.QdrantHost, _settings.QdrantGrpcPort );
      var measurer = new TargetRunner( _options, lifecycle, log ) { AfterSearch = connections.Observe };
      await machine.PinClientAsync( ct );
      foreach( (string name, BenchTarget? target, string? error) in targets )
      {
         log( $"== {name}" );
         report.TargetOrder.Add( name );
         report.Targets.Add( target == null ? new TargetReport { Name = name, Error = error } : await MeasureOneAsync( measurer, machine, connections, target, data, runner, ct ) );
         WriteResults( report, folder, machine );
      }

      report.Notes.AddRange( RunFlags( report ) );
      await DropEmptyDatabaseAsync( _options.Command == "run-all" && !_options.Keep, report );
   }

   /// <summary>
   /// Measures one target with its engine pinned to the engine CPUs, and puts the pin back
   /// afterwards even when the measurement fails. Records how the target was reached.
   /// </summary>
   /// <param name="measurer">Target runner.</param>
   /// <param name="machine">Machine control.</param>
   /// <param name="connections">Records each target's route and open connections.</param>
   /// <param name="target">The target.</param>
   /// <param name="data">Rows.</param>
   /// <param name="runner">Search runner, or null for replicate.</param>
   /// <param name="ct">Cancellation.</param>
   /// <returns>The target's results, with the machine and connection notes added.</returns>
   private static async Task<TargetReport> MeasureOneAsync( TargetRunner measurer, MachineControl machine, ConnectionRecorder connections, BenchTarget target, PipelineData data, SearchRunner? runner, CancellationToken ct )
   {
      await machine.EnterTargetAsync( target.Name, target.Hosting, target.ComposePath, ct );
      TargetReport result;
      try
      {
         result = await measurer.MeasureAsync( target, data, runner, ct );
      }
      finally
      {
         await machine.LeaveTargetAsync( target.Name );
      }

      if( result.Search != null )
      {
         result.SearchSettings = TargetSearchSettings.From( target.Index );
      }

      TargetConnection connection = connections.Describe( target );
      machine.RecordConnection( connection );
      result.Notes.Add( ConnectionRecorder.Note( connection ) );
      machine.AnnotateTarget( result );
      return result;
   }

   /// <summary>
   /// Machine control settings for this command: on for bench and run-all unless
   /// --no-machine-control; replicate times no searches and only records the machine.
   /// </summary>
   /// <returns>The settings.</returns>
   private MachineControlOptions MachineOptions()
   {
      bool timed = _options.Command is "bench" or "run-all";
      return new MachineControlOptions
      {
         Enabled = timed && _options.MachineControl,
         DisabledReason = timed ? "--no-machine-control" : $"{_options.Command} times no searches",
         StateFile = _options.MachineStateFile,
      };
   }

   /// <summary>
   /// Puts the machine back, adds the machine notes and flags, and writes the results a last
   /// time (only when a target was written, so a run that failed before its first target
   /// leaves no half-empty results folder).
   /// </summary>
   /// <param name="machine">Machine control.</param>
   /// <param name="report">The run.</param>
   /// <param name="folder">The run's folder.</param>
   private async Task FinishMachineAsync( MachineControl machine, BenchReport report, string folder )
   {
      await machine.RestoreAsync();
      machine.Annotate( report );
      if( !Directory.Exists( folder ) )
      {
         return;
      }

      try
      {
         WriteResults( report, folder, machine );
      }
      catch( Exception ex ) when( ex is IOException or UnauthorizedAccessException or InvalidDataException )
      {
         _log( $"Could not write the final results to {folder}: {ex.Message}" );
      }
   }

   /// <summary>
   /// Writes results.json and results.md, then the machine conditions into results.json.
   /// </summary>
   /// <param name="report">The run.</param>
   /// <param name="folder">The run's folder.</param>
   /// <param name="machine">Machine control.</param>
   private static void WriteResults( BenchReport report, string folder, MachineControl machine )
   {
      ResultsWriter.Write( report, folder );
      MachineFlags.WriteInto( folder, machine.Conditions );
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
      var runner = new SearchRunner( data, queries, truth, _options, seed ) { WarmupTime = TimeSpan.FromSeconds( _options.WarmupSeconds ) };
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
   /// How every number was measured, printed with the results. Each line says what the code
   /// does now; the review of 2026-10-04 found these lines describing an older method (latency
   /// from a separate one-query-at-a-time burst, no rehearsal, no settle), so they are kept next
   /// to the constants they quote.
   /// </summary>
   /// <param name="seed">The run's seed.</param>
   /// <returns>The notes.</returns>
   private IEnumerable<string> MethodNotes( int seed )
   {
      yield return $"Order: targets ran one at a time in a random order from seed {seed} (runSeed; --seed {seed} repeats it, targetOrder lists it). Inside each target the timed passes also ran in a random order from the same seed and the target's name (passOrder): "
         + $"default@1 is one searcher for {_options.Seconds} s (every search's latency gives p50/p95/p99, the completed searches give QPS@1, its first answer to each query gives recall and nDCG), default@N is N searchers for {_options.Seconds} s (QPS@N), exact is the engine's exact mode, one searcher for {_options.ExactSeconds} s cycling the queries.";
      foreach( string note in SearchRunner.DescribeMethod( _options.Warmup, TimeSpan.FromSeconds( _options.WarmupSeconds ) ) )
      {
         yield return note;
      }

      yield return $"Load rows/s counts only time inside each target's upsert calls: one writer, batches of {_options.Batch}, rows already in memory, the collection dropped and created fresh first. Engines that build or finish their index after the writes do it in a separate timed index step (the load's index seconds), and the run waits for it before searching.";
      yield return "Index proof: each engine's own report of its index (indexState) is read after the load and again after the last pass. A target whose index was not ready after the load was still measured and carries a WARNING; an engine that reports nothing counts as not ready.";
      yield return "Search settings: each searched target records the settings its own index description states (searchSettings: the build parameters and the effort per query, such as m, ef_construction and ef_search), read after the load; consolidating never averages runs whose settings differ.";
      yield return "Durability: each target's crash-safety setting as configured here (durability). Engines that do not force writes to disk on every commit load faster for that reason.";
      yield return $"Latency is client-side wall time around each search (network and driver included), every search of the default@1 window, one searcher, the queries cycled; a window with fewer than {SearchRunner.MIN_LATENCY_SAMPLES} searches, a p50 above {SearchRunner.SKEW_LIMIT} x its mean, or a mean above its p99 is flagged.";
      yield return $"QPS: N searchers back to back for {_options.Seconds} s per level; completed searches divided by the window's elapsed time.";
      yield return $"Recall@{_options.Top}: share of the exact top {_options.Top} (brute force in memory) that the engine returned. A hit whose exact similarity ties the {_options.Top}th best (within 1e-5) also counts, because duplicate rows embed to identical vectors.";
      yield return "Routes: a container engine (the benchmark's own SQL Server and Qdrant containers included) is reached at its container's own address on its Docker network, never through the published 127.0.0.1 port (docker-proxy); the native comparison targets (sql-native, qdrant-native) are native services reached directly over loopback. Each target's addresses and the connections the client held open after its passes are in its notes and in conditions.connections.";
      yield return "RAM of compose engines, the benchmark's own SQL Server and Qdrant containers (sql, sql-diskann, qdrant, qdrant-hnsw) included, is docker stats of the engine's containers; for the native comparison targets (sql-native, qdrant-native) it is the whole native process, including every other database or collection it serves. Disk is the table's reserved pages (SQL), the collection folder (Qdrant), or for other engines what the load added to the engine's data folder (engines that keep data in memory until a snapshot show almost nothing).";
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
      foreach( string name in _options.Targets.Count > 0 ? _options.Targets : _factory.Names.Concat( _factory.ComparisonNames ) )
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
   /// Drops the "sql-native" target's benchmark database on the native server when it is empty
   /// (the container's is dropped by the "sql" target itself). Best effort.
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
