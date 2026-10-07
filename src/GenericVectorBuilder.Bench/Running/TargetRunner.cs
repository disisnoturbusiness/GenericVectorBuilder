using System.Globalization;
using GenericVectorBuilder.Bench.Cli;
using GenericVectorBuilder.Bench.Data;
using GenericVectorBuilder.Bench.Report;
using GenericVectorBuilder.Bench.Targets;
using GenericVectorBuilder.Engines.Common;

namespace GenericVectorBuilder.Bench.Running;

/// <summary>
/// Measures one target from start to finish: make sure its engine is up, load it (with its
/// index step), read the engine's own index state, rehearse every pass type (untimed), run the
/// timed passes (each right after its own warm-up and settle check at its own concurrency; a
/// check that disagrees extends the warm-up once), read the index state again, read memory and
/// disk, then drop the copy and stop the engine if this run started it. Each timed pass is
/// written into the target's notes with its UTC window, its searches and the pass that ran
/// before it, and the settle note says what every settle check found.
/// Why the index state is read on both sides of the searches: a number is only worth quoting
/// when the engine itself says which index produced it. A target whose index is not ready after
/// the load is still measured, and its notes say so in capitals, so the gap is visible instead
/// of the engine silently missing from the table.
/// Why it is its own class: <see cref="BenchSession"/> needs SQL Server and the real engines,
/// and this flow is tested with fake targets.
/// What it records about the engine itself, in this order: right after the engine is bound, its
/// settings (<see cref="TargetReport.EngineSettings"/>), the container image checked against the pinned id
/// (<see cref="TargetReport.Image"/>, a different id is the target's error) and the size of its data folder;
/// then, for ClickHouse in run-all, the reset of its system log tables (before anything is loaded, so the
/// copy's disk figure starts after it); after the last pass and before the copy is dropped, the data folder
/// again, and for DuckDB and sqlite-vec their bench file's settings. Why before the load: a read after it
/// would sit between the load and the searches, and a read that fails must stop the target before hours of
/// loading, not after. <see cref="SettingsOnlyAsync"/> runs the first part alone, for the pilot.
/// </summary>
public sealed class TargetRunner
{
   #region Data Members

   private static readonly TimeSpan STATE_TIMEOUT = TimeSpan.FromSeconds( 60 );
   private static readonly TimeSpan DROP_TIMEOUT = TimeSpan.FromMinutes( 5 );

   private readonly BenchOptions _options;
   private readonly EngineLifecycle _lifecycle;
   private readonly Action<string> _log;
   private IEngineProbe? _defaultProbe;
   private EngineSettingsReader? _defaultReader;
   private ImagePins? _defaultPins;
   private DataFolderState? _defaultFolders;
   private ClickHouseStartState? _defaultClickHouse;

   #endregion Data Members

   #region Constructor

   /// <summary>
   /// Creates the runner.
   /// </summary>
   /// <param name="options">Parsed command line.</param>
   /// <param name="lifecycle">Starts and stops engines for this run.</param>
   /// <param name="log">Progress output.</param>
   public TargetRunner( BenchOptions options, EngineLifecycle lifecycle, Action<string> log )
   {
      _options = options;
      _lifecycle = lifecycle;
      _log = log;
   }

   #endregion Constructor

   #region Public Methods

   /// <summary>
   /// Called right after a target's last timed pass, while its connections are still open (the
   /// session reads which connections the client holds); null for none. Must not throw.
   /// </summary>
   public Action<BenchTarget>? AfterSearch { get; init; }

   /// <summary>
   /// The outside load right now as machine control measures it (CPU time of every process but
   /// this client and the engine under test), with the limit it is judged by; null for no reading.
   /// Must not throw. The load note when searching begins is judged by it (see <see cref="DescribeLoad"/>).
   /// Why not the load average: it counts this benchmark's own client and engines, and for a
   /// minute after a pass it still carries that pass. Run 601 of 2026-10-05 printed "WARNING: load
   /// average 10.04 ... the box was busy" as sqlite-vec began searching, about a minute after
   /// ClickHouse's 8-searcher pass, while the outside load machine control measured at sqlite-vec's
   /// three quiet checks was 0.15 to 0.16 CPUs over its busy window (limit 0.3).
   /// </summary>
   public Func<OutsideLoadSample?>? OutsideLoad { get; init; }

   /// <summary>
   /// Runs Docker commands and web reads for the image check; null for the real one. Tests pass a fake.
   /// </summary>
   public IEngineProbe? Probe { get; init; }

   /// <summary>
   /// Reads each engine's settings; null for one built on <see cref="Probe"/> (or the real probe). Tests pass a fake.
   /// </summary>
   public EngineSettingsReader? SettingsReader { get; init; }

   /// <summary>
   /// The pinned image ids; null to load deploy/bench/image-pins.json from beside the program the first
   /// time a container target needs it (a missing file is then that target's error). Tests pass a table.
   /// </summary>
   public ImagePins? Pins { get; init; }

   /// <summary>
   /// Measures data folders; null for the real one (sudo du). Tests pass a fake runner.
   /// </summary>
   public DataFolderState? Folders { get; init; }

   /// <summary>
   /// Resets ClickHouse's log tables; null for the real one. Tests pass a fake client.
   /// </summary>
   public ClickHouseStartState? ClickHouse { get; init; }

   /// <summary>
   /// Loads and/or searches one target, never letting its failure stop the others.
   /// </summary>
   /// <param name="target">The target.</param>
   /// <param name="data">Loaded rows.</param>
   /// <param name="runner">Search runner, or null for replicate.</param>
   /// <param name="ct">Cancellation.</param>
   /// <returns>The target's results.</returns>
   public async Task<TargetReport> MeasureAsync( BenchTarget target, PipelineData data, SearchRunner? runner, CancellationToken ct )
   {
      var result = new TargetReport { Name = target.Name, Engine = target.Engine, Hosting = target.Hosting, Durability = target.Durability };
      Measurement? diskBefore = null;
      try
      {
         await _lifecycle.EnsureRunningAsync( target, result.Notes, ct );
         await target.BindAsync( ct );
         result.Engine = target.Engine;
         result.Durability = target.Durability;
         ContainerDescription? container = await RecordStartAsync( target, result, ct );
         if( _options.Command == "run-all" )
         {
            await ResetStartStateAsync( target, container, result, ct );
         }

         if( _options.Command != "bench" )
         {
            diskBefore = await DiskBeforeLoadAsync( target, ct );
            await LoadAsync( target, data, result, ct );
         }

         result.IndexState = new TargetIndexState { AfterLoad = await target.ReadIndexStateAsync( _options.Collection, STATE_TIMEOUT, ct ) };
         NoteIndexAfterLoad( result );
         if( runner != null )
         {
            await SearchAsync( target, runner, result, data.Count, ct );
         }

         result.Ram = await target.MeasureRamAsync( _options.Collection, ct );
         result.Disk = Growth( diskBefore, await target.MeasureDiskAsync( _options.Collection, ct ) );
         await RecordEndAsync( target, result, ct );
      }
      catch( Exception ex ) when( ex is not OperationCanceledException || !ct.IsCancellationRequested )
      {
         result.Error = _options.Command == "bench" ? $"{ex.Message} (bench only searches; if {_options.Collection} is missing, run replicate with the same --limit first)" : ex.Message;
         _log( $"  {target.Name} FAILED: {result.Error}" );
      }
      finally
      {
         result.Index = target.Index;
         result.Engine = target.Engine;
         result.Durability = target.Durability;
         await TidyAsync( target, result );
      }

      return result;
   }

   /// <summary>
   /// The settings-only measurement of one target: starts its engine when run-all must, binds to it,
   /// reads its settings, its image and the size of its data folder (see <see cref="RecordStartAsync"/> and
   /// <see cref="RecordEndAsync"/>), and stops the engine again if this run started it. Nothing is
   /// loaded, searched, truncated or dropped.
   /// Why it exists: the settings readers talk to nineteen engines and the only proof that each works
   /// is to run each one; the pilot does that before a run of hours depends on them.
   /// A failed read is the target's error and never stops the others, as in a real run.
   /// </summary>
   /// <param name="target">The target.</param>
   /// <param name="ct">Cancellation.</param>
   /// <returns>The target's results, with no search or load.</returns>
   public async Task<TargetReport> SettingsOnlyAsync( BenchTarget target, CancellationToken ct )
   {
      var result = new TargetReport { Name = target.Name, Engine = target.Engine, Hosting = target.Hosting, Durability = target.Durability };
      try
      {
         await _lifecycle.EnsureRunningAsync( target, result.Notes, ct );
         await target.BindAsync( ct );
         result.Engine = target.Engine;
         result.Durability = target.Durability;
         await RecordStartAsync( target, result, ct );
         await RecordEndAsync( target, result, ct );
         result.Notes.Add( "Settings-only run: the engine's settings, image and data folder were read; nothing was loaded, searched or reset." );
      }
      catch( Exception ex ) when( ex is not OperationCanceledException || !ct.IsCancellationRequested )
      {
         result.Error = ex.Message;
         _log( $"  {target.Name} FAILED: {result.Error}" );
      }
      finally
      {
         result.Index = target.Index;
         result.Engine = target.Engine;
         result.Durability = target.Durability;
         await _lifecycle.ReleaseAsync( target, result.Notes );
      }

      return result;
   }

   /// <summary>
   /// One line for an index state: "ready, 524 of 524 indexed (detail)".
   /// </summary>
   /// <param name="state">The state, or null.</param>
   /// <returns>The text.</returns>
   public static string Describe( IndexState? state )
   {
      if( state == null )
      {
         return "not read";
      }

      string counts = state.IndexedVectors.HasValue || state.TotalVectors.HasValue
         ? $", {state.IndexedVectors?.ToString( "N0" ) ?? "?"} of {state.TotalVectors?.ToString( "N0" ) ?? "?"} indexed"
         : string.Empty;
      return $"{( state.Ready ? "ready" : "NOT ready" )}{counts} ({state.Detail})";
   }

   /// <summary>
   /// One line for the rehearsal: which pass types ran, for how long, and how many searches.
   /// </summary>
   /// <param name="preparation">The preparation.</param>
   /// <param name="each">Rehearsal length per pass type.</param>
   /// <returns>The note.</returns>
   public static string DescribePreparation( Preparation preparation, TimeSpan each )
   {
      string passes = string.Join( ", ", preparation.Rehearsals.Select( r => $"{r.Pass} {r.Searches:N0} searches in {r.Seconds:0.0} s{( r.Errors > 0 ? $" ({r.Errors} FAILED)" : string.Empty )}" ) );
      return $"Rehearsal before any timed pass, untimed, every pass type at its own concurrency for at least {each.TotalSeconds:0.#} s through the same code the timed passes use: {passes}.";
   }

   /// <summary>
   /// One line for the settle checks, one made before each timed pass at that pass's own
   /// concurrency: "Settle: settled ..." when every check confirmed its warm-up (with or without
   /// the one extension) and every timed pass held to its settled figure, a WARNING naming the
   /// passes when any did not, or when none ran. The words "NOT settled" appear only in the
   /// warning, because the consolidation reads them.
   /// </summary>
   /// <param name="checks">The checks, in pass order (empty when none ran).</param>
   /// <returns>The note.</returns>
   public static string DescribeSettle( IReadOnlyList<SettleCheck> checks )
   {
      if( checks.Count == 0 )
      {
         return "WARNING: no settle ran before the timed passes.";
      }

      string each = string.Join( "; ", checks.Select( DescribeCheck ) );
      List<string> unsettled = checks.Where( c => !c.Settled ).Select( c => c.Pass ).ToList();
      string rule = $"each by its own warm-up at its own concurrency and a trial of the same pass within {SearchRunner.TRIAL_TOLERANCE:0%} of the warm-up's settled figure, and the timed pass itself within {SearchRunner.TRIAL_TOLERANCE:0%} of it";
      return unsettled.Count == 0
         ? $"Settle: settled before every timed pass, {rule}. {each}."
         : $"WARNING: latency had NOT settled when timing began for {string.Join( ", ", unsettled )} ({rule}). {each}. Its numbers may still include warm-up or a change the engine was still going through; rerun before quoting them.";
   }

   /// <summary>
   /// The load note written when searching begins. A WARNING only when the measured outside load
   /// (over either window of machine control's busy rule) is above its limit; the load average is
   /// recorded beside it but never judged, because it counts this benchmark's own client and
   /// engines and still carries the previous pass for a minute. With no reading (machine control
   /// off, no reader handed to the runner, or too little history yet) the note says so and does
   /// not warn. Why the warning does not say the numbers are inflated: searching begins with the
   /// untimed rehearsal, and each timed pass waits for a quiet box and is judged on its own.
   /// </summary>
   /// <param name="loadAverage">The 1/5/15 minute load average as read, or null.</param>
   /// <param name="outside">The measured outside load and its limit, or null when there is none.</param>
   /// <param name="wired">False when no outside-load reader was handed to the runner at all.</param>
   /// <returns>The note.</returns>
   public static string DescribeLoad( string? loadAverage, OutsideLoadSample? outside, bool wired = true )
   {
      string average = $"load average {loadAverage ?? "unknown"} (1/5/15 min; it also counts this benchmark's own client and engines, so it is recorded, not judged)";
      if( outside == null || ( outside.Reading.Window == null && outside.Reading.Recent == null ) )
      {
         string why = !wired ? "not read here (machine control's quiet check before each pass judges the box)"
            : outside == null ? "not measured (machine control off)"
            : "not known yet (too little sampled history)";
         return $"Outside load when searching began: {why}. {Capitalized( average )}.";
      }

      OutsideReading reading = outside.Reading;
      string limit = string.Create( CultureInfo.InvariantCulture, $"limit {outside.Limit:0.0#}" );
      return reading.IsBusy( outside.Limit )
         ? $"WARNING: processes outside the benchmark used {reading.Describe()} when searching began ({limit}): the box was busy as the untimed rehearsal started. "
            + $"Each timed pass still waits for a quiet box before its warm-up, and one that ran busy is flagged 'busy box' on its own. The {average}."
         : $"Outside load when searching began: {reading.Describe()} ({limit}), a quiet box. The {average}.";
   }

   /// <summary>
   /// One line for a timed pass: what ran before it, its window in UTC, its searches and its
   /// latency summary.
   /// </summary>
   /// <param name="pass">The pass record.</param>
   /// <returns>The note.</returns>
   public static string DescribePass( PassRecord pass )
   {
      string latency = pass.P50Ms is double p50
         ? $"p50 {p50:0.000} ms, mean {pass.MeanMs:0.000} ms, p99 {pass.P99Ms:0.000} ms, "
         : "no latency (no search completed), ";
      string perSearch = pass.Qps > 0 ? $" (1000/QPS {1000 / pass.Qps:0.000} ms)" : string.Empty;
      return $"Pass {pass.Name} after {pass.Previous ?? "nothing"}: {pass.StartUtc:yyyy-MM-ddTHH:mm:ss.fffZ} to {pass.EndUtc:yyyy-MM-ddTHH:mm:ss.fffZ} ({pass.Seconds:0.0} s), "
         + $"{pass.Searches:N0} searches, {pass.Errors} failed, {latency}{pass.Qps:0.0} QPS{perSearch}.";
   }

   #endregion Public Methods

   #region Private Methods

   /// <summary>
   /// One pass's settle check as text: its warm-up, its trial, and the extension when one ran.
   /// </summary>
   /// <param name="check">The check.</param>
   /// <returns>"default@8: warm-up 15.0 s and 22,431 searches (0 failed) at 8 searchers; QPS of the last windows 1,512, 1,530, 1,524 (...); trial of ...".</returns>
   private static string DescribeCheck( SettleCheck check )
   {
      string warm = $"{check.Pass}: warm-up {SettleText( check.Settle )}{( check.Settle.Settled ? string.Empty : $" ({check.Settle.StoppedBecause})" )}";
      string trial = check.Trial.Describe( "trial" );
      if( check.Extension == null )
      {
         return check.Confirmed ? $"{warm}; {trial}{HeldText( check )}" : $"{warm}; {trial}; not extended, because the searches kept failing";
      }

      string extension = $"{SettleText( check.Extension )}{( check.Extension.Settled ? string.Empty : $", {check.Extension.StoppedBecause}" )}";
      string verdict = check.Confirmed ? "settled after the extension" : "still disagreeing after the extension";
      return $"{warm}; {trial}, so the warm-up was EXTENDED once ({extension}); {check.Retrial!.Describe( "second trial" )}; {verdict}{HeldText( check )}";
   }

   /// <summary>
   /// The timed pass held against the settled figure, as text, or nothing before the pass ran.
   /// </summary>
   /// <param name="check">The check.</param>
   /// <returns>"; the timed pass 2,880 QPS, 1% from the settled 2,849 QPS (limit 10%)", with "NOT HELD" when it was not.</returns>
   private static string HeldText( SettleCheck check )
   {
      if( check.PassFigure is not double figure || check.SettledFigure is not double settled )
      {
         return string.Empty;
      }

      int searchers = check.Trial.Concurrency;
      string limit = searchers == 1 ? $"limit {SearchRunner.TRIAL_TOLERANCE:0%} or {SearchRunner.P50_FLOOR_MS:0.0#} ms; {Math.Abs( figure - settled ):0.000} ms" : $"limit {SearchRunner.TRIAL_TOLERANCE:0%}";
      string held = check.Held ? string.Empty : ", NOT HELD: the engine was still changing when it was timed";
      return $"; the timed pass {SearchRunner.FigureText( figure, searchers )}, {check.PassApart:0%} from the settled {SearchRunner.FigureText( settled, searchers )} ({limit}){held}";
   }

   /// <summary>
   /// A warm-up's or an extension's length, searches and last window figures as text.
   /// </summary>
   /// <param name="settle">The warm-up or extension, read as a settle.</param>
   /// <returns>"15.0 s and 22,431 searches (0 failed) at 8 searchers; QPS of the last windows 1,512, 1,530, 1,524 (windows of at least 2 s and 100 searches)"; for an extension its level test with the windows of both halves.</returns>
   private static string SettleText( SettleResult settle )
   {
      IEnumerable<double> last = settle.WindowFigures.TakeLast( SearchRunner.SETTLE_WINDOWS );
      string Figure( double f ) => settle.Concurrency == 1 ? $"{f:0.000}" : $"{f:N0}";
      string unit = settle.Concurrency == 1 ? " ms" : string.Empty;
      string what = settle.Concurrency == 1 ? "p50" : "QPS";
      string figures = settle.WindowFigures.Count == 0 ? "no full window"
         : settle.Level is SettleLevel level
            ? $"{what} of its older {level.HalfWindows} windows {Figure( level.Older )}{unit} (windows {string.Join( ", ", level.OlderWindows.Select( Figure ) )}) and of its newer {level.HalfWindows} {Figure( level.Newer )}{unit} "
               + $"(windows {string.Join( ", ", level.NewerWindows.Select( Figure ) )}), {level.Apart:0.0%} apart (limit {SearchRunner.SETTLE_TOLERANCE:0%}{( settle.Concurrency == 1 ? $" or {SearchRunner.P50_FLOOR_MS:0.0#} ms; {Math.Abs( level.Newer - level.Older ):0.000} ms" : string.Empty )})"
         : $"{what} of the last windows " + string.Join( ", ", last.Select( Figure ) ) + unit;
      return $"{settle.Seconds:0.0} s and {settle.Searches:N0} searches ({settle.Errors} failed) at {SearchRunner.Searchers( settle.Concurrency )}; {figures} (windows of at least {settle.WindowSeconds:0.##} s and {SearchRunner.SETTLE_WINDOW} searches)";
   }

   /// <summary>
   /// Loads the target (upserts, count, index step) and flags a failed index step.
   /// </summary>
   /// <param name="target">The target.</param>
   /// <param name="data">Rows.</param>
   /// <param name="result">Target results.</param>
   /// <param name="ct">Cancellation.</param>
   private async Task LoadAsync( BenchTarget target, PipelineData data, TargetReport result, CancellationToken ct )
   {
      LoadOutcome load = await Replicator.RunAsync( target, _options.Collection, data, _options.Batch, _log, ct );
      result.Load = load.Report;
      _log( $"  {target.Name}: loaded {data.Count:N0} rows at {load.Report.RowsPerSecond:N0} rows/s" );
      if( load.IndexFailure != null )
      {
         result.Notes.Add( $"WARNING: the index step after the load FAILED ({load.IndexFailure}). Searched anyway; see the index state." );
      }
   }

   /// <summary>
   /// Rehearses the target, runs every timed pass (each after its warm-up and settle check),
   /// then reads the index state again and writes the method notes, one record per pass and the
   /// measurement flags.
   /// Why the preparation runs here, after the index state was read: the warm-ups have to run on
   /// the index the engine says it is using, and the timed passes have to start from a settled
   /// engine and a client whose code for every pass is already compiled.
   /// </summary>
   /// <param name="target">The target.</param>
   /// <param name="runner">Search runner.</param>
   /// <param name="result">Target results.</param>
   /// <param name="expected">Rows loaded in memory.</param>
   /// <param name="ct">Cancellation.</param>
   private async Task SearchAsync( BenchTarget target, SearchRunner runner, TargetReport result, int expected, CancellationToken ct )
   {
      NoteLoad( result );
      Preparation preparation = await runner.PrepareAsync( target, _options.Collection, _log, ct );
      SearchOutcome outcome = await runner.RunAsync( target, _options.Collection, _log, ct, preparation );
      AfterSearch?.Invoke( target );
      result.Search = outcome.Report;
      result.PassOrder = outcome.PassOrder;
      result.WarmupErrors = outcome.UntimedErrors;
      result.IndexState!.AfterSearch = await target.ReadIndexStateAsync( _options.Collection, STATE_TIMEOUT, ct );
      result.Notes.Add( DescribePreparation( preparation, runner.RehearsalTime ) );
      result.Notes.Add( DescribeSettle( preparation.Checks ) );
      result.Notes.AddRange( outcome.Passes.Select( DescribePass ) );
      result.Notes.Add( $"Passes in the order run: {string.Join( ", ", outcome.PassOrder )}; each after its own warm-up. "
         + $"Untimed searches (rehearsals, per-pass warm-ups, settle checks and extensions): {outcome.UntimedSearches:N0} sent, {outcome.UntimedErrors} failed (warmupErrors)"
         + $"{( outcome.FirstWarmupError != null ? $"; first warm-up failure {outcome.FirstWarmupError}" : string.Empty )}"
         + $"{( preparation.FirstError != null ? $"; first {preparation.FirstError}" : string.Empty )}"
         + $"{( outcome.WarmupsCutShort.Count > 0 ? $"; warm-up stopped by its time cap before its minimum of searches before {string.Join( ", ", outcome.WarmupsCutShort )}" : string.Empty )}. "
         + $"Index after the searches: {Describe( result.IndexState.AfterSearch )}." );
      result.Notes.AddRange( outcome.Flags );
      NoteIndexChange( result );
      NoteCountMismatch( result, expected );
   }

   /// <summary>
   /// Records the index state after the load, with a warning when the engine does not say its
   /// index is ready.
   /// </summary>
   /// <param name="result">Target results.</param>
   private void NoteIndexAfterLoad( TargetReport result )
   {
      IndexState? state = result.IndexState?.AfterLoad;
      string when = _options.Command == "bench" ? "before searching (bench does not load)" : "after the load";
      result.Notes.Add( state is { Ready: true }
         ? $"Index {when}: {Describe( state )}. Durability: {result.Durability}."
         : $"WARNING: index not ready {when}: {Describe( state )}. Measured anyway, so its search numbers may come from a scan or a half-built index. Durability: {result.Durability}." );
   }

   /// <summary>
   /// Warns when the index changed between the load and the end of the searches.
   /// </summary>
   /// <param name="result">Target results.</param>
   private static void NoteIndexChange( TargetReport result )
   {
      IndexState? before = result.IndexState?.AfterLoad;
      IndexState? after = result.IndexState?.AfterSearch;
      if( before != null && after != null && ( before.Ready != after.Ready || before.IndexedVectors != after.IndexedVectors ) )
      {
         result.Notes.Add( $"WARNING: the index changed while it was searched (after the load: {Describe( before )}; after the searches: {Describe( after )}). The passes did not all search the same index." );
      }
   }

   /// <summary>
   /// After a target: drops the benchmark copy (run-all without --keep) and stops the engine if
   /// this run started it. Best effort; problems become notes.
   /// </summary>
   /// <param name="target">The target.</param>
   /// <param name="result">Target results (notes).</param>
   private async Task TidyAsync( BenchTarget target, TargetReport result )
   {
      if( _options.Command == "run-all" && !_options.Keep )
      {
         using var limit = new CancellationTokenSource( DROP_TIMEOUT );
         try
         {
            await target.Sink.DropCollectionAsync( _options.Collection, limit.Token );
            result.Notes.Add( $"Benchmark copy {_options.Collection} dropped afterwards." );
         }
         catch( Exception ex )
         {
            result.Notes.Add( $"Could not drop the benchmark copy: {( ex is OperationCanceledException ? $"no answer within {DROP_TIMEOUT.TotalMinutes:0} minutes" : ex.Message )}" );
         }
      }

      await _lifecycle.ReleaseAsync( target, result.Notes );
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
   /// When the folder is smaller after the load than before it (an engine that deletes old
   /// files while it loads: ClickHouse rotating its logs, Milvus compacting), the growth is not
   /// zero, it is not known: the reading then has no bytes and its text says by how much the folder
   /// shrank. The older code clamped it to "0 B added", which is a measurement that was never made;
   /// 16 target runs between v5 and v7 carry it (v7 run 154837 among them). Disk is not ranked, but
   /// a printed zero was read as "adds nothing".
   /// </summary>
   /// <param name="before">Reading before the load, or null.</param>
   /// <param name="after">Reading after the load.</param>
   /// <returns>The reading to report.</returns>
   public static Measurement Growth( Measurement? before, Measurement after )
   {
      if( before?.Bytes is not long start || after.Bytes is not long end )
      {
         return after;
      }

      if( end < start )
      {
         return Measurement.None( $"not measured: the folder was {Measurement.Format( start - end )} smaller after the load than before it, so the load's own growth cannot be told ({after.Text}, {Measurement.Format( start )} before)" );
      }

      long grown = end - start;
      return new Measurement( grown, $"{Measurement.Format( grown )} added by this load ({after.Text}, {Measurement.Format( start )} before)" );
   }

   /// <summary>
   /// Reads what the engine is running with right after it was bound, in this order: its container as
   /// Docker describes it, its settings, the image against the pinned id, and its data folder's size.
   /// The settings and the image are recorded before any check can fail, so a failed target still shows
   /// what was read; an image that is not the pinned one then ends the target with the reason.
   /// </summary>
   /// <param name="target">The target, bound.</param>
   /// <param name="result">Target results.</param>
   /// <param name="ct">Cancellation.</param>
   /// <returns>The container, or null for a target that has none.</returns>
   /// <exception cref="InvalidOperationException">A read failed, the image is not the pinned one, or the data folder does not match the container's mounts.</exception>
   private async Task<ContainerDescription?> RecordStartAsync( BenchTarget target, TargetReport result, CancellationToken ct )
   {
      ContainerDescription? container = await DescribeContainerAsync( target, ct );
      EngineSettingsReader reader = UseReader();
      result.EngineSettings = ( await reader.ReadAsync( target, container, ct ) ).Select( s => new EngineSetting { Key = s.Key, Value = s.Value, How = s.How } ).ToList();
      if( container != null )
      {
         ImagePins pins = Pins ?? ( _defaultPins ??= ImagePins.LoadDefault() );
         ImageFacts image = await pins.ReadAsync( UseProbe(), target.Name, container, ct );
         result.Image = new ImageRecord { Ref = image.Ref, Id = image.Id, PinnedId = image.PinnedId, LastTagTimeUtc = image.LastTagTimeUtc };
         if( image.Problem != null )
         {
            throw new InvalidOperationException( image.Problem );
         }
      }

      string? folder = DataFolderState.FolderFor( target );
      if( folder != null )
      {
         if( container != null )
         {
            DataFolderState.VerifyAgainstMounts( folder, container );
         }

         result.DataFolder = new DataFolderRecord { Path = folder, BytesAtStart = await UseFolders().BytesAsync( folder, ct ) };
      }

      return container;
   }

   /// <summary>
   /// The container a container target runs in, as "docker inspect" describes it, or null for a
   /// target that has none (embedded engines, the native servers, and the fake targets of tests).
   /// </summary>
   /// <param name="target">The target.</param>
   /// <param name="ct">Cancellation.</param>
   /// <returns>The description, or null.</returns>
   /// <exception cref="InvalidOperationException">The route names more than one container, Docker has none of that name, or its answer is not an inspect result.</exception>
   private static async Task<ContainerDescription?> DescribeContainerAsync( BenchTarget target, CancellationToken ct )
   {
      if( target.Container == null )
      {
         return null;
      }

      IReadOnlyList<string> names = target.Container.Route.Containers;
      if( names.Count != 1 )
      {
         throw new InvalidOperationException( $"{target.Name} reaches {names.Count} containers; the settings reader handles one, so it must be taught the others before it records anything for them." );
      }

      string json = await target.Container.Inspector.InspectAsync( names[0], ct ) ?? throw new InvalidOperationException( $"Docker has no container {names[0]} for {target.Name}." );
      return ContainerDescription.Parse( json, EngineSettingsReader.ENV_KEYS );
   }

   /// <summary>
   /// ClickHouse only, in run-all only: truncates its system log tables so every session starts from
   /// the same state, and records what was done in the data-folder record. Runs after the bind and the
   /// start-of-run size, and before anything is loaded.
   /// </summary>
   /// <param name="target">The target.</param>
   /// <param name="container">Its container, or null.</param>
   /// <param name="result">Target results (their data-folder record is filled in).</param>
   /// <param name="ct">Cancellation.</param>
   /// <exception cref="InvalidOperationException">The reset failed, or the target has no address or data folder to reset through.</exception>
   private async Task ResetStartStateAsync( BenchTarget target, ContainerDescription? container, TargetReport result, CancellationToken ct )
   {
      if( target.Name != "clickhouse" || _options.SettingsOnly || container == null )
      {
         return;
      }

      DataFolderRecord folder = result.DataFolder ?? throw new InvalidOperationException( "ClickHouse has no recorded data folder to reset against." );
      ContainerAddress address = target.Connections.FirstOrDefault( c => c.Container == container.Name )
         ?? throw new InvalidOperationException( $"{target.Name} has no recorded address for container {container.Name}, so its log tables cannot be reset." );
      ClickHouseStartState reset = ClickHouse ?? ( _defaultClickHouse ??= new ClickHouseStartState() );
      DataFolderState meter = UseFolders();
      ClickHouseReset done = await reset.ResetAsync( address, token => meter.BytesAsync( folder.Path, token ), folder.BytesAtStart ?? 0, ct );
      folder.Reset = done.Describe();
      folder.BytesAfterReset = done.FolderBytesAfter;
      _log( $"  {target.Name}: {folder.Reset}" );
   }

   /// <summary>
   /// After the last pass and before the copy is dropped: the data folder's size again, the embedded
   /// engine's bench file size, and the embedded engines' file settings.
   /// </summary>
   /// <param name="target">The target.</param>
   /// <param name="result">Target results.</param>
   /// <param name="ct">Cancellation.</param>
   /// <exception cref="InvalidOperationException">A read failed.</exception>
   private async Task RecordEndAsync( BenchTarget target, TargetReport result, CancellationToken ct )
   {
      if( result.DataFolder is DataFolderRecord folder )
      {
         folder.BytesAtEnd = await UseFolders().BytesAsync( folder.Path, ct );
         folder.BenchFileBytes = DataFolderState.BenchFileBytes( target, _options.Collection );
      }

      string? file = DataFolderState.BenchFile( target, _options.Collection );
      EngineSettingsReader reader = UseReader();
      IReadOnlyList<SettingRead> more = await reader.ReadEmbeddedFileAsync( target, file != null && File.Exists( file ) ? file : null, ct );
      result.EngineSettings?.AddRange( more.Select( s => new EngineSetting { Key = s.Key, Value = s.Value, How = s.How } ) );
   }

   /// <summary>
   /// The probe handed to the runner, else the real one (made once).
   /// </summary>
   /// <returns>The probe.</returns>
   private IEngineProbe UseProbe()
   {
      return Probe ?? ( _defaultProbe ??= new DockerEngineProbe() );
   }

   /// <summary>
   /// The settings reader handed to the runner, else one on the probe (made once).
   /// </summary>
   /// <returns>The reader.</returns>
   private EngineSettingsReader UseReader()
   {
      return SettingsReader ?? ( _defaultReader ??= new EngineSettingsReader( UseProbe() ) );
   }

   /// <summary>
   /// The data-folder measurer handed to the runner, else the real one (made once).
   /// </summary>
   /// <returns>The measurer.</returns>
   private DataFolderState UseFolders()
   {
      return Folders ?? ( _defaultFolders ??= new DataFolderState() );
   }

   /// <summary>
   /// Records the box's load as searching starts (see <see cref="DescribeLoad"/>): the measured
   /// outside load judges it, the load average is only recorded.
   /// </summary>
   /// <param name="result">Target results.</param>
   private void NoteLoad( TargetReport result )
   {
      result.Notes.Add( DescribeLoad( MachineFacts.ReadLoadAverage(), OutsideLoad?.Invoke(), OutsideLoad != null ) );
   }

   /// <summary>
   /// The text with its first letter in capitals.
   /// </summary>
   /// <param name="text">The text.</param>
   /// <returns>"Load average ..." from "load average ...".</returns>
   private static string Capitalized( string text )
   {
      return text.Length == 0 ? text : char.ToUpperInvariant( text[0] ) + text[1..];
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

   #endregion Private Methods
}

/// <summary>
/// The outside load at one moment as machine control measures it, with the busy limit it is
/// judged by. Why a pair: the target runner must judge the load by the same rule as the per-pass
/// quiet check, and the limit lives in machine control's settings.
/// </summary>
/// <param name="Reading">Outside load over the busy window and the recent window.</param>
/// <param name="Limit">CPUs of outside work above which the box counts as busy.</param>
public sealed record OutsideLoadSample( OutsideReading Reading, double Limit );
