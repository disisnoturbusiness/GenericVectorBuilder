using GenericVectorBuilder.Bench.Cli;
using GenericVectorBuilder.Bench.Data;
using GenericVectorBuilder.Bench.Report;
using GenericVectorBuilder.Bench.Targets;
using GenericVectorBuilder.Engines.Common;

namespace GenericVectorBuilder.Bench.Running;

/// <summary>
/// Measures one target from start to finish: make sure its engine is up, load it (with its
/// index step), read the engine's own index state, rehearse every pass type and let the latency
/// settle (untimed), run the timed passes, read the index state again, read memory and disk,
/// then drop the copy and stop the engine if this run started it. Each timed pass is written
/// into the target's notes with its UTC window, its searches and the pass that ran before it.
/// Why the index state is read on both sides of the searches: a number is only worth quoting
/// when the engine itself says which index produced it. A target whose index is not ready after
/// the load is still measured, and its notes say so in capitals, so the gap is visible instead
/// of the engine silently missing from the table.
/// Why it is its own class: <see cref="BenchSession"/> needs SQL Server and the real engines,
/// and this flow is tested with fake targets.
/// </summary>
public sealed class TargetRunner
{
   #region Data Members

   private static readonly TimeSpan STATE_TIMEOUT = TimeSpan.FromSeconds( 60 );
   private static readonly TimeSpan DROP_TIMEOUT = TimeSpan.FromMinutes( 5 );

   private readonly BenchOptions _options;
   private readonly EngineLifecycle _lifecycle;
   private readonly Action<string> _log;

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
      }
      catch( Exception ex ) when( ex is not OperationCanceledException || !ct.IsCancellationRequested )
      {
         result.Error = _options.Command == "bench" ? $"{ex.Message} (bench only searches; if {_options.Collection} is missing, run replicate with the same --limit first)" : ex.Message;
         _log( $"  {target.Name} FAILED: {result.Error}" );
      }
      finally
      {
         result.Index = target.Index;
         await TidyAsync( target, result );
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
      return $"Rehearsal before any timed pass, untimed, every pass type for at least {each.TotalSeconds:0.#} s through the same code the timed passes use: {passes}.";
   }

   /// <summary>
   /// One line for the settle, a WARNING when the latency had not settled.
   /// </summary>
   /// <param name="settle">The settle, or null when it did not run.</param>
   /// <returns>The note.</returns>
   public static string DescribeSettle( SettleResult? settle )
   {
      if( settle == null )
      {
         return "WARNING: no settle ran before the timed passes.";
      }

      string p50s = settle.WindowP50s.Count == 0 ? "no full window" : string.Join( ", ", settle.WindowP50s.TakeLast( SearchRunner.SETTLE_WINDOWS ).Select( p => $"{p:0.00}" ) ) + " ms";
      string how = $"{settle.Seconds:0.0} s and {settle.Searches:N0} searches ({settle.Errors} failed); p50 of the last windows of {SearchRunner.SETTLE_WINDOW}: {p50s}";
      return settle.Settled
         ? $"Settle: settled after {how}, within {SearchRunner.SETTLE_TOLERANCE:0%} across {SearchRunner.SETTLE_WINDOWS} windows."
         : $"WARNING: latency had NOT settled when timing began ({settle.StoppedBecause}) after {how}. Its numbers may still include warm-up; rerun before quoting them.";
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
   /// Rehearses and settles the target, runs every timed pass, then reads the index state again
   /// and writes the method notes, one record per pass and the measurement flags.
   /// Why the preparation runs here, after the index state was read: the settle has to start
   /// from the index the engine says it is using, and the timed passes have to start from a
   /// settled engine and a client whose code for every pass is already compiled.
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
      result.Search = outcome.Report;
      result.PassOrder = outcome.PassOrder;
      result.WarmupErrors = outcome.UntimedErrors;
      result.IndexState!.AfterSearch = await target.ReadIndexStateAsync( _options.Collection, STATE_TIMEOUT, ct );
      result.Notes.Add( DescribePreparation( preparation, runner.RehearsalTime ) );
      result.Notes.Add( DescribeSettle( preparation.Settle ) );
      result.Notes.AddRange( outcome.Passes.Select( DescribePass ) );
      result.Notes.Add( $"Passes in the order run: {string.Join( ", ", outcome.PassOrder )}; each after its own warm-up. "
         + $"Untimed searches (rehearsal, settle and per-pass warm-ups): {outcome.UntimedSearches:N0} sent, {outcome.UntimedErrors} failed (warmupErrors)"
         + $"{( outcome.FirstWarmupError != null ? $"; first warm-up failure {outcome.FirstWarmupError}" : string.Empty )}"
         + $"{( preparation.FirstError != null ? $"; first {preparation.FirstError}" : string.Empty )}"
         + $"{( outcome.WarmupsCutShort.Count > 0 ? $"; warm-up stopped early by its time budget before {string.Join( ", ", outcome.WarmupsCutShort )}" : string.Empty )}. "
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

   #endregion Private Methods
}
