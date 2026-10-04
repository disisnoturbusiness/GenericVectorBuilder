using System.Diagnostics;

namespace GenericVectorBuilder.Bench.Targets;

/// <summary>
/// Runs an outside command (docker, du, nvidia-smi) and captures what it prints.
/// Why arguments are passed as a list: nothing is ever glued into a shell string, so a path or
/// name can never be read as another command.
/// </summary>
public static class Shell
{
   #region Public Methods

   /// <summary>
   /// Runs a program and waits for it.
   /// </summary>
   /// <param name="program">Program, e.g. "sudo".</param>
   /// <param name="arguments">Arguments, each passed as one argument.</param>
   /// <param name="timeout">Longest wait before the process is killed.</param>
   /// <param name="ct">Cancellation.</param>
   /// <returns>Exit code, standard output and standard error.</returns>
   public static async Task<ShellResult> RunAsync( string program, IEnumerable<string> arguments, TimeSpan timeout, CancellationToken ct )
   {
      var start = new ProcessStartInfo( program ) { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false };
      foreach( string argument in arguments )
      {
         start.ArgumentList.Add( argument );
      }

      using var process = Process.Start( start ) ?? throw new InvalidOperationException( $"Could not start {program}." );
      using var limit = CancellationTokenSource.CreateLinkedTokenSource( ct );
      limit.CancelAfter( timeout );
      Task<string> output = process.StandardOutput.ReadToEndAsync( limit.Token );
      Task<string> error = process.StandardError.ReadToEndAsync( limit.Token );
      try
      {
         await process.WaitForExitAsync( limit.Token );
      }
      catch( OperationCanceledException )
      {
         process.Kill( entireProcessTree: true );
         throw new TimeoutException( $"{program} {string.Join( ' ', start.ArgumentList )} did not finish within {timeout.TotalMinutes:0.#} minutes." );
      }

      return new ShellResult( process.ExitCode, await output, await error );
   }

   /// <summary>
   /// Runs a program and returns its trimmed output, or null when it fails or is missing.
   /// For facts that are nice to have (GPU name, a folder size), where a failure must not stop
   /// the benchmark.
   /// </summary>
   /// <param name="program">Program.</param>
   /// <param name="arguments">Arguments.</param>
   /// <param name="ct">Cancellation.</param>
   /// <returns>The output, or null.</returns>
   public static async Task<string?> TryOutputAsync( string program, IEnumerable<string> arguments, CancellationToken ct )
   {
      try
      {
         ShellResult result = await RunAsync( program, arguments, TimeSpan.FromMinutes( 5 ), ct );
         return result.ExitCode == 0 ? result.Output.Trim() : null;
      }
      catch( Exception ex ) when( ex is not OperationCanceledException || !ct.IsCancellationRequested )
      {
         return null;
      }
   }

   #endregion Public Methods
}

/// <summary>
/// What a finished command returned.
/// </summary>
/// <param name="ExitCode">Exit code, 0 for success.</param>
/// <param name="Output">Standard output.</param>
/// <param name="Error">Standard error.</param>
public sealed record ShellResult( int ExitCode, string Output, string Error );
