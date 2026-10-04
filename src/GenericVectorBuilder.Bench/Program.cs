using System.Runtime.InteropServices;
using GenericVectorBuilder.Bench.Cli;
using GenericVectorBuilder.Bench.Report;
using GenericVectorBuilder.Bench.Running;
using GenericVectorBuilder.Core.Configuration;

namespace GenericVectorBuilder.Bench;

/// <summary>
/// Entry point of the vector engine benchmark. Parses the command line and runs one
/// <see cref="BenchSession"/>. Ctrl+C, SIGTERM and SIGHUP stop cleanly: run-all still drops its
/// copy and stops any engine it started, and machine control puts the governor and every CPU
/// pin back. A second signal ends the process at once; the state file then lets the next start
/// (or "restore-machine") put the machine back.
/// </summary>
internal static class Program
{
   #region Data Members

   private static int _signals;

   #endregion Data Members

   #region Private Methods

   /// <summary>
   /// Runs the command.
   /// </summary>
   /// <param name="args">Command-line arguments; see <see cref="BenchOptions.Usage"/>.</param>
   /// <returns>0 on success, 1 when a target failed, 2 for a bad command line. "consolidate" only reads result folders (see <see cref="ConsolidateCommand"/>).</returns>
   private static async Task<int> Main( string[] args )
   {
      if( args.Length > 0 && args[0] == ConsolidateCommand.NAME )
      {
         return ConsolidateCommand.Run( args[1..], Console.WriteLine, CancellationToken.None );
      }

      BenchOptions options;
      try
      {
         options = BenchOptions.Parse( args );
      }
      catch( ArgumentException ex )
      {
         Console.Error.WriteLine( ex.Message );
         Console.Error.WriteLine();
         Console.Error.WriteLine( BenchOptions.Usage() );
         return 2;
      }

      using var cancel = new CancellationTokenSource();
      Console.CancelKeyPress += ( _, e ) => e.Cancel = Stop( cancel, "Ctrl+C" );
      using PosixSignalRegistration term = PosixSignalRegistration.Create( PosixSignal.SIGTERM, c => c.Cancel = Stop( cancel, "SIGTERM" ) );
      using PosixSignalRegistration hangup = PosixSignalRegistration.Create( PosixSignal.SIGHUP, c => c.Cancel = Stop( cancel, "SIGHUP" ) );
      return options.Command == "restore-machine" ? await RestoreMachineAsync( options, cancel.Token ) : await RunAsync( options, cancel.Token );
   }

   /// <summary>
   /// Runs a measuring or housekeeping command.
   /// </summary>
   /// <param name="options">Parsed command line.</param>
   /// <param name="ct">Cancellation.</param>
   /// <returns>Exit code.</returns>
   private static async Task<int> RunAsync( BenchOptions options, CancellationToken ct )
   {
      try
      {
         using var session = new BenchSession( options, line => Console.WriteLine( $"{DateTime.UtcNow:HH:mm:ss} {line}" ) );
         return await session.RunAsync( ct );
      }
      catch( Exception ex ) when( ex is not OperationCanceledException )
      {
         Console.Error.WriteLine( $"Failed: {ex.Message}" );
         return 1;
      }
   }

   /// <summary>
   /// "restore-machine": puts back what a run that did not finish left changed, from the state file.
   /// </summary>
   /// <param name="options">Parsed command line (--machine-state).</param>
   /// <param name="ct">Cancellation.</param>
   /// <returns>0 when the machine is back (or nothing was changed), 1 when something could not be put back.</returns>
   private static async Task<int> RestoreMachineAsync( BenchOptions options, CancellationToken ct )
   {
      var settings = new GvbSettings();
      var store = new MachineStateStore( options.MachineStateFile );
      try
      {
         bool restored = await MachineControl.RestoreStaleAsync( store, new LinuxMachineSystem( settings.BuildSqlConnectionString ), Console.WriteLine, ct );
         Console.WriteLine( restored ? "Machine put back." : $"Nothing to put back: there is no state file at {store.Path}." );
         return 0;
      }
      catch( Exception ex ) when( ex is InvalidOperationException or InvalidDataException )
      {
         Console.Error.WriteLine( ex.Message );
         return 1;
      }
   }

   /// <summary>
   /// Handles a stop signal: the first cancels the run so it can put everything back; a second
   /// lets the process end at once.
   /// </summary>
   /// <param name="cancel">The run's cancellation.</param>
   /// <param name="signal">Signal name, for the message.</param>
   /// <returns>True to keep the process alive (first signal), false to let it end.</returns>
   private static bool Stop( CancellationTokenSource cancel, string signal )
   {
      bool first = Interlocked.Increment( ref _signals ) == 1;
      Console.Error.WriteLine( first
         ? $"{signal}: stopping; putting the machine back first (send it again to end at once; the next start then puts the machine back)."
         : $"{signal} again: ending now." );
      try
      {
         cancel.Cancel();
      }
      catch( ObjectDisposedException )
      {
         // The run has already ended; nothing left to stop.
      }

      return first;
   }

   #endregion Private Methods
}
