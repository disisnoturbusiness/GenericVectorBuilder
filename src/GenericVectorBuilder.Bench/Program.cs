using GenericVectorBuilder.Bench.Cli;
using GenericVectorBuilder.Bench.Running;

namespace GenericVectorBuilder.Bench;

/// <summary>
/// Entry point of the vector engine benchmark. Parses the command line and runs one
/// <see cref="BenchSession"/>. Ctrl+C stops cleanly: run-all still drops its copy and stops
/// any engine it started.
/// </summary>
internal static class Program
{
   #region Private Methods

   /// <summary>
   /// Runs the command.
   /// </summary>
   /// <param name="args">Command-line arguments; see <see cref="BenchOptions.Usage"/>.</param>
   /// <returns>0 on success, 1 when a target failed, 2 for a bad command line.</returns>
   private static async Task<int> Main( string[] args )
   {
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
      Console.CancelKeyPress += ( _, e ) =>
      {
         e.Cancel = true;
         cancel.Cancel();
      };

      try
      {
         using var session = new BenchSession( options, line => Console.WriteLine( $"{DateTime.UtcNow:HH:mm:ss} {line}" ) );
         return await session.RunAsync( cancel.Token );
      }
      catch( Exception ex ) when( ex is not OperationCanceledException )
      {
         Console.Error.WriteLine( $"Failed: {ex.Message}" );
         return 1;
      }
   }

   #endregion Private Methods
}
