using GenericVectorBuilder.Bench.Cli;
using GenericVectorBuilder.Bench.Data;
using GenericVectorBuilder.Bench.Report;
using GenericVectorBuilder.Bench.Targets;
using GenericVectorBuilder.Bench.Truth;
using GenericVectorBuilder.Core.Configuration;

namespace GenericVectorBuilder.Bench.Running;

/// <summary>
/// Runs one command end to end: reads the source rows once, builds the queries and the exact
/// answer once, then goes through the targets one at a time (for run-all: start the engine,
/// load, search, measure, drop the copy, stop the engine) and rewrites the results after each.
/// Why one target at a time: engines measured side by side would compete for the same CPU,
/// memory and disk, and every number would depend on what else happened to be running.
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
      List<string> names = _options.Targets.Count > 0 ? _options.Targets.ToList() : _factory.Names.ToList();
      BenchReport report = await NewReportAsync( ct );
      string folder = Path.Combine( _options.OutFolder ?? Path.Combine( _repoRoot, "bench-results" ), $"{DateTime.UtcNow:yyyyMMdd-HHmmss}-{_options.Pipeline}" );
      var reader = new PipelineReader( _settings.BuildSqlConnectionString(), _settings.SqlDatabase );
      _log( $"Reading dbo.gvb_{_options.Pipeline}{( _options.Limit.HasValue ? $" (first {_options.Limit:N0} rows by ChunkId)" : string.Empty )}..." );
      PipelineData data = await reader.LoadAsync( _options.Pipeline, _options.Limit, withText: _options.Command != "bench", ct );
      report.Rows = data.Count;
      report.Dimension = data.Dimension;
      report.Source = $"Read READ-ONLY from {_settings.SqlDatabase}.dbo.gvb_{_options.Pipeline} in ChunkId order, vectors as {reader.VectorReadMode}, {reader.LoadSeconds:0.0} s.";
      _log( $"{data.Count:N0} rows x {data.Dimension} dims in {reader.LoadSeconds:0.0} s ({reader.VectorReadMode})" );
      SearchRunner? runner = _options.Command == "replicate" ? null : await PrepareSearchAsync( data, report, ct );
      foreach( string name in names )
      {
         _log( $"== {name}" );
         report.Targets.Add( await MeasureTargetAsync( name, data, runner, ct ) );
         ResultsWriter.Write( report, folder );
      }

      await DropEmptyDatabaseAsync( _options.Command == "run-all" && !_options.Keep, report );
      ResultsWriter.Write( report, folder );
      _log( $"Results: {Path.Combine( folder, "results.md" )}" );
      return report.Targets.Any( t => t.Error != null ) ? 1 : 0;
   }

   /// <summary>
   /// Builds the queries and the exact answer, shared by every target.
   /// </summary>
   /// <param name="data">Loaded rows.</param>
   /// <param name="report">Report to fill.</param>
   /// <param name="ct">Cancellation.</param>
   /// <returns>The search runner.</returns>
   private async Task<SearchRunner> PrepareSearchAsync( PipelineData data, BenchReport report, CancellationToken ct )
   {
      QuerySet queries = _options.IsGolden
         ? await GoldenQueries.LoadAsync( _options.GoldenFile, _options.Pipeline, _settings, GvbSettings.Expand( GOLDEN_CACHE ), ct )
         : QuerySet.Random( data, _options.RandomCount );
      _log( $"Queries: {queries.Description}" );
      TruthSet truth = BruteForce.Compute( data, queries, _options.Top );
      _log( $"Exact answer of {queries.Count} queries by brute force in {truth.Seconds:0.0} s" );
      var runner = new SearchRunner( data, queries, truth, _options );
      report.Queries = queries.Description;
      report.QueryCount = queries.Count;
      report.TruthSeconds = truth.Seconds;
      report.TruthNdcg = runner.TruthNdcg();
      return runner;
   }

   /// <summary>
   /// Loads and/or searches one target, never letting its failure stop the others.
   /// </summary>
   /// <param name="name">Target name.</param>
   /// <param name="data">Loaded rows.</param>
   /// <param name="runner">Search runner, or null for replicate.</param>
   /// <param name="ct">Cancellation.</param>
   /// <returns>The target's results.</returns>
   private async Task<TargetReport> MeasureTargetAsync( string name, PipelineData data, SearchRunner? runner, CancellationToken ct )
   {
      var result = new TargetReport { Name = name };
      BenchTarget target;
      try
      {
         target = _factory.Create( name );
      }
      catch( ArgumentException ex )
      {
         result.Error = ex.Message;
         return result;
      }

      result.Engine = target.Engine;
      result.Hosting = target.Hosting;
      bool runAll = _options.Command == "run-all";
      bool startedHere = false;
      Measurement? diskBefore = null;
      try
      {
         startedHere = await EnsureRunningAsync( target, runAll, result, ct );
         if( _options.Command != "bench" )
         {
            diskBefore = await DiskBeforeLoadAsync( target, ct );
            result.Load = await Replicator.RunAsync( target, _options.Collection, data, _options.Batch, _log, ct );
            _log( $"  {name}: loaded {data.Count:N0} rows at {result.Load.RowsPerSecond:N0} rows/s" );
         }

         if( runner != null )
         {
            NoteLoad( result );
            result.Search = await runner.RunAsync( target, _options.Collection, _log, ct );
            NoteCountMismatch( result, data.Count );
         }

         result.Ram = await target.MeasureRamAsync( _options.Collection, ct );
         result.Disk = Growth( diskBefore, await target.MeasureDiskAsync( _options.Collection, ct ) );
      }
      catch( Exception ex ) when( ex is not OperationCanceledException || !ct.IsCancellationRequested )
      {
         result.Error = _options.Command == "bench" ? $"{ex.Message} (bench only searches; if {_options.Collection} is missing, run replicate with the same --limit first)" : ex.Message;
         _log( $"  {name} FAILED: {result.Error}" );
      }
      finally
      {
         result.Index = target.Index;
         await TidyAsync( target, runAll, startedHere, result );
      }

      return result;
   }

   /// <summary>
   /// Makes sure a compose-hosted engine is up. run-all starts it when it is down; the other
   /// commands refuse with a message, because they promise not to start anything.
   /// </summary>
   /// <param name="target">The target.</param>
   /// <param name="runAll">True for run-all.</param>
   /// <param name="result">Target results (notes).</param>
   /// <param name="ct">Cancellation.</param>
   /// <returns>True when this call started the engine (so it must stop it again).</returns>
   private async Task<bool> EnsureRunningAsync( BenchTarget target, bool runAll, TargetReport result, CancellationToken ct )
   {
      if( target.ComposePath == null || await ComposeRunner.IsRunningAsync( target.ComposePath, ct ) )
      {
         return false;
      }

      if( !runAll )
      {
         throw new InvalidOperationException( $"{target.Name} is not running. Start it with: sudo docker compose -f {target.ComposePath} up -d (or use run-all)." );
      }

      _log( $"  starting {Path.GetFileName( target.ComposePath )}" );
      await ComposeRunner.UpAsync( target.ComposePath, ct );
      result.Notes.Add( "Started by run-all for this measurement and stopped afterwards." );
      return true;
   }

   /// <summary>
   /// After a run-all target: drops the benchmark copy (unless --keep) and stops the engine if
   /// run-all started it. Best effort; problems become notes.
   /// </summary>
   /// <param name="target">The target.</param>
   /// <param name="runAll">True for run-all.</param>
   /// <param name="startedHere">True when run-all started the engine.</param>
   /// <param name="result">Target results (notes).</param>
   private async Task TidyAsync( BenchTarget target, bool runAll, bool startedHere, TargetReport result )
   {
      if( runAll && !_options.Keep )
      {
         try
         {
            await target.Sink.DropCollectionAsync( _options.Collection, CancellationToken.None );
            result.Notes.Add( $"Benchmark copy {_options.Collection} dropped afterwards." );
         }
         catch( Exception ex )
         {
            result.Notes.Add( $"Could not drop the benchmark copy: {ex.Message}" );
         }
      }

      if( startedHere && await ComposeRunner.DownAsync( target.ComposePath!, CancellationToken.None ) is string problem )
      {
         result.Notes.Add( problem );
      }
   }

   /// <summary>
   /// For engines measured by their whole data folder (compose and embedded), drops any old
   /// benchmark copy and reads the folder size, so the growth after loading is this copy's
   /// footprint. SQL tables and Qdrant collection folders are measured on their own, so they
   /// need no "before".
   /// </summary>
   /// <param name="target">The target.</param>
   /// <param name="ct">Cancellation.</param>
   /// <returns>The size before loading, or null when not needed.</returns>
   private async Task<Measurement?> DiskBeforeLoadAsync( BenchTarget target, CancellationToken ct )
   {
      if( target.Hosting is not ( "compose" or "embedded" ) )
      {
         return null;
      }

      try
      {
         await target.Sink.DropCollectionAsync( _options.Collection, ct );
      }
      catch( Exception ex ) when( ex is not OperationCanceledException )
      {
         return null;
      }

      return await target.MeasureDiskAsync( _options.Collection, ct );
   }

   /// <summary>
   /// Turns a folder size read before and after the load into what the load added. Why: an
   /// engine's data folder also holds whatever else it stores (other collections, test
   /// leftovers, logs), so the growth is the fair footprint of this copy. When no "before"
   /// size exists (a table or collection folder that the load created) the reading is
   /// already this copy's own size and is kept as is.
   /// </summary>
   /// <param name="before">Reading before the load, or null.</param>
   /// <param name="after">Reading after the load.</param>
   /// <returns>The reading to report.</returns>
   private static Measurement Growth( Measurement? before, Measurement after )
   {
      if( before?.Bytes is not long start || after.Bytes is not long end )
      {
         return after;
      }

      long grown = Math.Max( 0, end - start );
      return new Measurement( grown, $"{Measurement.Format( grown )} added by this load ({after.Text}, {Measurement.Format( start )} before)" );
   }

   /// <summary>
   /// Records the box's load average as searching starts, with a warning when work was
   /// queueing for a CPU (latency and QPS then include waiting for other processes).
   /// </summary>
   /// <param name="result">Target results.</param>
   private static void NoteLoad( TargetReport result )
   {
      string? load = MachineFacts.ReadLoadAverage();
      result.Notes.Add( MachineFacts.IsBusy( load )
         ? $"WARNING: load average {load} (1/5/15 min) on {Environment.ProcessorCount} logical CPUs when searching began; the box was busy, so latency and QPS are inflated by other work."
         : $"Load average {load ?? "unknown"} (1/5/15 min) when searching began." );
   }

   /// <summary>
   /// Flags a target that does not hold exactly the loaded rows: its recall then compares
   /// different data and must not be read as a quality number.
   /// </summary>
   /// <param name="result">Target results.</param>
   /// <param name="expected">Rows loaded in memory.</param>
   private static void NoteCountMismatch( TargetReport result, int expected )
   {
      if( result.Search?.CountInTarget is long count && count != expected )
      {
         result.Notes.Add( $"WARNING: the target holds {count:N0} rows but the benchmark used {expected:N0}; recall compares different data. Replicate with the same --limit first." );
      }
   }

   /// <summary>
   /// The run-level part of the report: when, how, on what, and the method notes.
   /// </summary>
   /// <param name="ct">Cancellation.</param>
   /// <returns>The report, without targets.</returns>
   private async Task<BenchReport> NewReportAsync( CancellationToken ct )
   {
      var report = new BenchReport
      {
         StartedUtc = DateTime.UtcNow.ToString( "yyyy-MM-ddTHH:mm:ssZ" ),
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

      report.Notes.AddRange( MethodNotes() );
      report.Notes.AddRange( _factory.Notes );
      return report;
   }

   /// <summary>
   /// How every number was measured, printed with the results.
   /// </summary>
   /// <returns>The notes.</returns>
   private IEnumerable<string> MethodNotes()
   {
      yield return $"Load rows/s counts only time inside each target's upsert calls: one writer, batches of {_options.Batch}, rows already in memory, the collection dropped and created fresh first.";
      yield return $"Latency is client-side wall time around each search (network and driver included), one query at a time, after {_options.Warmup} warm-up queries; at least 200 samples (small query sets are repeated).";
      yield return $"QPS: N workers searching back to back for {_options.Seconds} s per level; completed searches divided by elapsed time.";
      yield return $"Recall@{_options.Top}: share of the exact top {_options.Top} (brute force in memory) that the engine returned. A hit whose exact similarity ties the {_options.Top}th best (within 1e-5) also counts, because duplicate rows embed to identical vectors.";
      yield return "RAM of always-on servers (SQL Server, Qdrant) is the whole process, including every other database or collection it serves; for compose engines it is docker stats of the engine's containers. Disk is the table's reserved pages (SQL), the collection folder (Qdrant), or for other engines what the load added to the engine's data folder (engines that keep data in memory until a snapshot show almost nothing).";
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
