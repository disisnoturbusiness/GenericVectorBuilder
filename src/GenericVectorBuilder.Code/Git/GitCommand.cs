using System.Diagnostics;
using System.Text;

namespace GenericVectorBuilder.Code.Git;

/// <summary>
/// The output of one finished git command.
/// </summary>
/// <param name="ExitCode">Process exit code; 0 means success.</param>
/// <param name="Output">Everything git wrote to standard output.</param>
/// <param name="Error">Everything git wrote to standard error.</param>
internal sealed record GitResult( int ExitCode, string Output, string Error );

/// <summary>
/// Runs the git executable with an argument list, never through a shell.
/// Why an argument list: every argument reaches git exactly as given, so a repository URL or
/// branch name can never be read as a second command, a redirect or an extra option.
/// Why the environment is pinned: git must never stop and wait for a password nobody can type
/// (the web app has no terminal), and only the https, ssh and local file transports are
/// allowed, so a crafted URL cannot reach git's "ext::" transport, which runs commands.
/// </summary>
internal static class GitCommand
{
   #region Data Members

   private const string ALLOWED_PROTOCOLS = "https:ssh:file";
   private const int MAX_ERROR_CHARS = 600;
   private const int MAX_KEPT_ERROR_CHARS = 16 * 1024;
   private const int BUFFER_CHARS = 1024;

   #endregion Data Members

   #region Public Methods

   /// <summary>
   /// Runs git and waits for it, killing it on cancellation or when the timeout passes.
   /// </summary>
   /// <param name="workingDirectory">Folder git runs in, or null for the current folder.</param>
   /// <param name="arguments">Arguments, each passed to git as one argument.</param>
   /// <param name="timeout">Longest the command may run.</param>
   /// <param name="ct">Cancellation.</param>
   /// <param name="onProgress">Receives each line or progress update git writes to standard error
   /// as it arrives (git ends progress updates with a carriage return), or null. Called from a
   /// background task, so it must be quick and thread-safe.</param>
   /// <returns>Exit code and output. The error text holds whole lines only; progress updates are
   /// left out of it so a failure message stays readable.</returns>
   public static async Task<GitResult> RunAsync( string? workingDirectory, IReadOnlyList<string> arguments, TimeSpan timeout, CancellationToken ct,
      Action<string>? onProgress = null )
   {
      using Process process = new() { StartInfo = BuildStartInfo( workingDirectory, arguments ) };
      try
      {
         process.Start();
      }
      catch( System.ComponentModel.Win32Exception ex )
      {
         throw new InvalidOperationException( $"git could not be started. Is git installed? ({ex.Message})", ex );
      }

      Task<string> output = process.StandardOutput.ReadToEndAsync( CancellationToken.None );
      Task<string> error = ReadErrorAsync( process.StandardError, onProgress );
      using var limit = CancellationTokenSource.CreateLinkedTokenSource( ct );
      limit.CancelAfter( timeout );
      try
      {
         await process.WaitForExitAsync( limit.Token );
      }
      catch( OperationCanceledException )
      {
         Kill( process );
         ct.ThrowIfCancellationRequested();
         throw new TimeoutException( $"git {arguments.FirstOrDefault()} did not finish within {Describe( timeout )} and was stopped." );
      }

      return new GitResult( process.ExitCode, await output, await error );
   }

   /// <summary>
   /// Runs git and throws a plain-English error when it fails.
   /// </summary>
   /// <param name="workingDirectory">Folder git runs in, or null for the current folder.</param>
   /// <param name="arguments">Arguments, each passed to git as one argument.</param>
   /// <param name="timeout">Longest the command may run.</param>
   /// <param name="ct">Cancellation.</param>
   /// <param name="onProgress">Receives git's progress lines as they arrive, or null; see <see cref="RunAsync"/>.</param>
   /// <returns>Standard output, trimmed.</returns>
   public static async Task<string> RunCheckedAsync( string? workingDirectory, IReadOnlyList<string> arguments, TimeSpan timeout, CancellationToken ct,
      Action<string>? onProgress = null )
   {
      GitResult result = await RunAsync( workingDirectory, arguments, timeout, ct, onProgress );
      if( result.ExitCode != 0 )
      {
         string detail = result.Error.Trim();
         if( detail.Length > MAX_ERROR_CHARS )
         {
            detail = "..." + detail[^MAX_ERROR_CHARS..];
         }

         throw new InvalidOperationException( $"git {arguments.FirstOrDefault()} failed (exit code {result.ExitCode}): {detail}" );
      }

      return result.Output.Trim();
   }

   #endregion Public Methods

   #region Private Methods

   /// <summary>
   /// Reads git's standard error to the end, passing every line and every progress update to
   /// <paramref name="onProgress"/> as it arrives. Only lines ended by a line feed are kept for
   /// the error text, because git rewrites progress in place with carriage returns and hundreds
   /// of those would bury the real error. The kept text is capped at
   /// <see cref="MAX_KEPT_ERROR_CHARS"/> (the end is kept) so a chatty command cannot grow it
   /// without limit.
   /// </summary>
   /// <param name="reader">git's standard error.</param>
   /// <param name="onProgress">Receives each line or progress update, or null.</param>
   /// <returns>The kept error text.</returns>
   private static async Task<string> ReadErrorAsync( StreamReader reader, Action<string>? onProgress )
   {
      var kept = new StringBuilder();
      var line = new StringBuilder();
      var buffer = new char[BUFFER_CHARS];
      int read;
      while( ( read = await reader.ReadAsync( buffer, CancellationToken.None ) ) > 0 )
      {
         for( int i = 0; i < read; i++ )
         {
            char c = buffer[i];
            if( c != '\r' && c != '\n' )
            {
               line.Append( c );
               continue;
            }

            EndSegment( line, kept, keep: c == '\n', onProgress );
         }
      }

      EndSegment( line, kept, keep: true, onProgress );
      return kept.ToString();
   }

   /// <summary>
   /// Finishes one segment of git's standard error: reports it, keeps it when it was a whole
   /// line, and empties the segment buffer.
   /// </summary>
   /// <param name="line">The segment read so far.</param>
   /// <param name="kept">The kept error text.</param>
   /// <param name="keep">True when the segment ended with a line feed (or the stream ended).</param>
   /// <param name="onProgress">Receives the segment, or null.</param>
   private static void EndSegment( StringBuilder line, StringBuilder kept, bool keep, Action<string>? onProgress )
   {
      if( line.Length == 0 )
      {
         return;
      }

      string text = line.ToString();
      line.Clear();
      onProgress?.Invoke( text );
      if( keep )
      {
         kept.Append( text ).Append( '\n' );
         if( kept.Length > MAX_KEPT_ERROR_CHARS )
         {
            kept.Remove( 0, kept.Length - MAX_KEPT_ERROR_CHARS );
         }
      }
   }

   /// <summary>
   /// Says how long a timeout is in the unit a person would use.
   /// </summary>
   /// <param name="timeout">The timeout.</param>
   /// <returns>E.g. "20 seconds" or "10 minute(s)".</returns>
   private static string Describe( TimeSpan timeout )
   {
      return timeout < TimeSpan.FromMinutes( 1 ) ? $"{timeout.TotalSeconds:0} seconds" : $"{timeout.TotalMinutes:0} minute(s)";
   }

   /// <summary>
   /// Builds the start info: no shell, redirected output, no prompts, restricted transports.
   /// </summary>
   /// <param name="workingDirectory">Folder git runs in, or null.</param>
   /// <param name="arguments">Arguments.</param>
   /// <returns>The start info.</returns>
   private static ProcessStartInfo BuildStartInfo( string? workingDirectory, IReadOnlyList<string> arguments )
   {
      var info = new ProcessStartInfo( "git" )
      {
         UseShellExecute = false,
         RedirectStandardOutput = true,
         RedirectStandardError = true,
         RedirectStandardInput = false,
         CreateNoWindow = true,
         StandardOutputEncoding = Encoding.UTF8,
         StandardErrorEncoding = Encoding.UTF8,
      };

      if( workingDirectory != null )
      {
         info.WorkingDirectory = workingDirectory;
      }

      foreach( string argument in arguments )
      {
         info.ArgumentList.Add( argument );
      }

      info.Environment["GIT_TERMINAL_PROMPT"] = "0";
      info.Environment["GIT_ALLOW_PROTOCOL"] = ALLOWED_PROTOCOLS;
      info.Environment["GCM_INTERACTIVE"] = "never";
      if( string.IsNullOrEmpty( Environment.GetEnvironmentVariable( "GIT_SSH_COMMAND" ) ) )
      {
         // BatchMode makes ssh fail at once instead of asking for a passphrase or host approval.
         info.Environment["GIT_SSH_COMMAND"] = "ssh -o BatchMode=yes";
      }

      return info;
   }

   /// <summary>
   /// Stops git and anything it started, ignoring a process that already ended.
   /// </summary>
   /// <param name="process">The git process.</param>
   private static void Kill( Process process )
   {
      try
      {
         process.Kill( entireProcessTree: true );
      }
      catch( InvalidOperationException )
      {
         // Already exited.
      }
   }

   #endregion Private Methods
}
