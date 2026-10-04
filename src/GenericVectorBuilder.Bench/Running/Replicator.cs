using GenericVectorBuilder.Engines.Common;
using System.Diagnostics;
using GenericVectorBuilder.Bench.Data;
using GenericVectorBuilder.Bench.Report;
using GenericVectorBuilder.Bench.Targets;
using GenericVectorBuilder.Core.Contracts;

namespace GenericVectorBuilder.Bench.Running;

/// <summary>
/// Copies every loaded row into one target and times it.
/// What is timed: only the target's upsert calls. The rows are already in memory and each
/// batch's records are built before its clock starts, so the source database and the
/// benchmark's own work are not in the number. One writer, batches in order, the same for
/// every engine; an engine that is faster with parallel writers is not given them here.
/// After the last upsert two more things are timed separately: how long until the target
/// counts every row (engines that index asynchronously), and the index step for targets that
/// build their index after loading (<see cref="IIndexFinisher"/>) or keep optimising in the
/// background (<see cref="BenchTarget.Settle"/>), so searches are never timed against a
/// half-built index or beside a busy optimiser.
/// The collection is dropped first, so every load starts from empty and measures inserts, not
/// overwrites.
/// </summary>
public static class Replicator
{
   #region Data Members

   private static readonly TimeSpan ENSURE_PATIENCE = TimeSpan.FromMinutes( 3 );
   private static readonly TimeSpan COUNT_PATIENCE = TimeSpan.FromMinutes( 30 );

   #endregion Data Members

   #region Public Methods

   /// <summary>
   /// Loads a target.
   /// </summary>
   /// <param name="target">The target.</param>
   /// <param name="collection">Benchmark collection name.</param>
   /// <param name="data">Rows to copy.</param>
   /// <param name="batch">Rows per upsert call.</param>
   /// <param name="log">Progress output.</param>
   /// <param name="ct">Cancellation.</param>
   /// <returns>What the load measured.</returns>
   public static async Task<LoadReport> RunAsync( BenchTarget target, string collection, PipelineData data, int batch, Action<string> log, CancellationToken ct )
   {
      ISink sink = target.Sink;
      await EnsureFreshAsync( sink, collection, data.Dimension, log, ct );
      var report = new LoadReport { Rows = data.Count, Batch = batch };
      var upserts = new Stopwatch();
      int nextLog = data.Count / 10;
      for( int from = 0; from < data.Count; from += batch )
      {
         IReadOnlyList<VectorRecord> records = data.Records( from, Math.Min( batch, data.Count - from ) );
         upserts.Start();
         await sink.UpsertAsync( collection, records, ct );
         upserts.Stop();
         if( from + records.Count >= nextLog && nextLog > 0 )
         {
            log( $"  {target.Name}: {from + records.Count:N0} of {data.Count:N0} rows, {( from + records.Count ) / upserts.Elapsed.TotalSeconds:N0} rows/s" );
            nextLog += data.Count / 10;
         }
      }

      report.UpsertSeconds = upserts.Elapsed.TotalSeconds;
      report.RowsPerSecond = data.Count / Math.Max( report.UpsertSeconds, 1e-9 );
      report.CountMatchSeconds = await WaitForCountAsync( sink, collection, data.Count, ct );
      Func<string, CancellationToken, Task<string>>? finish = sink is IIndexFinisher finisher ? finisher.FinishLoadAsync : target.Settle;
      if( finish != null )
      {
         Stopwatch index = Stopwatch.StartNew();
         report.IndexNote = await finish( collection, ct );
         report.IndexSeconds = index.Elapsed.TotalSeconds;
      }

      return report;
   }

   #endregion Public Methods

   #region Private Methods

   /// <summary>
   /// Drops the benchmark collection and creates it again. Retries for a few minutes, because a
   /// container reported healthy can still refuse its first connections.
   /// </summary>
   /// <param name="sink">The sink.</param>
   /// <param name="collection">Collection name.</param>
   /// <param name="dimension">Vector length.</param>
   /// <param name="log">Progress output.</param>
   /// <param name="ct">Cancellation.</param>
   private static async Task EnsureFreshAsync( ISink sink, string collection, int dimension, Action<string> log, CancellationToken ct )
   {
      DateTime deadline = DateTime.UtcNow + ENSURE_PATIENCE;
      while( true )
      {
         try
         {
            await sink.DropCollectionAsync( collection, ct );
            await sink.EnsureCollectionAsync( collection, dimension, ct );
            return;
         }
         catch( Exception ex ) when( ex is not OperationCanceledException && DateTime.UtcNow < deadline )
         {
            log( $"  {sink.Name}: not ready yet ({ex.Message}); retrying in 5 s" );
            await Task.Delay( TimeSpan.FromSeconds( 5 ), ct );
         }
      }
   }

   /// <summary>
   /// Polls the target's count until it holds every row.
   /// </summary>
   /// <param name="sink">The sink.</param>
   /// <param name="collection">Collection name.</param>
   /// <param name="expected">Rows written.</param>
   /// <param name="ct">Cancellation.</param>
   /// <returns>Seconds until the count matched, or null when the sink cannot count.</returns>
   /// <exception cref="InvalidOperationException">The count never matched.</exception>
   private static async Task<double?> WaitForCountAsync( ISink sink, string collection, long expected, CancellationToken ct )
   {
      Stopwatch clock = Stopwatch.StartNew();
      long count = -1;
      while( clock.Elapsed < COUNT_PATIENCE )
      {
         try
         {
            count = await sink.CountAsync( collection, ct );
         }
         catch( NotSupportedException )
         {
            return null;
         }

         if( count == expected )
         {
            return clock.Elapsed.TotalSeconds;
         }

         await Task.Delay( TimeSpan.FromMilliseconds( 250 ), ct );
      }

      throw new InvalidOperationException( $"{sink.Name} still counts {count:N0} rows, not {expected:N0}, {COUNT_PATIENCE.TotalMinutes:0} minutes after the load." );
   }

   #endregion Private Methods
}
