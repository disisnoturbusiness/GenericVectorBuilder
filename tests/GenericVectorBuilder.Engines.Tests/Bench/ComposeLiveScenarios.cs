// Compiled only by the Compose live tests, together with the compose runner's own sources (ComposeRunner.cs
// and the three files it needs). It uses only ComposeRunner's public start, stop and check calls, so it
// compiles against the tree as it was before the start retry existed and shows what that tree did.
// BENCH_UNDER_TEST is defined only in that compilation, so the test project itself sees an empty file.
#if BENCH_UNDER_TEST
using System.Text;
using GenericVectorBuilder.Bench.Targets;

namespace GenericVectorBuilder.Bench.UnderTest;

/// <summary>
/// Starts throwaway containers through the real <see cref="ComposeRunner.UpAsync(string, string, CancellationToken)"/>
/// on the real Docker of this box and hands back plain lines for the tests.
/// What is created: three compose projects named gvbbench-composetest-* with one container each, built
/// from an image already on the box (the container only runs a shell command), a scratch folder under the
/// home directory (never /tmp) holding a "flag" folder bind-mounted into the container, all removed at
/// the end. No engine's container, data folder or port is touched.
/// Why a flag folder: it plays the engine's data folder. The first container writes a file into it and
/// dies; the retry only works if that file is still there (the second container sees it and stays up),
/// which also proves the clean-up between attempts leaves a bind-mounted data folder alone.
/// </summary>
public static class ComposeLiveScenarios
{
   #region Data Members

   // Any image already on the box that has /bin/sh; the benchmark's MariaDB image is used only as a shell here.
   private const string IMAGE = "mariadb:11.8.9";
   private const string CPUSET = "0";
   private static readonly TimeSpan LIMIT = TimeSpan.FromMinutes( 4 );

   #endregion Data Members

   #region Public Methods

   /// <summary>
   /// The container exits with 134 on its first start (and leaves a file in the flag folder), then stays up.
   /// The start must succeed on the second attempt, say so in the line, and leave the flag file in place.
   /// </summary>
   /// <param name="scratch">Folder for the scratch directories (on persistent disk).</param>
   /// <returns>"line|" and what UpAsync returned (or "error|" and the exception), "flag|" and whether the file survived, "running|" and the container's state read back.</returns>
   public static async Task<string[]> RecoversAsync( string scratch )
   {
      const string command = "if [ -f /flag/seen ]; then sleep 3600; else touch /flag/seen; echo 'panic: etcdserver: leader changed'; exit 134; fi";
      return await StartAsync( scratch, "recover", command, "test -f /flag/seen", CPUSET );
   }

   /// <summary>
   /// The container stays up but its health check never passes: not an exit, so the start must fail on the
   /// first attempt without any retry (a second try would only wait out the same fault again).
   /// </summary>
   /// <param name="scratch">Folder for the scratch directories.</param>
   /// <returns>The same lines as <see cref="RecoversAsync"/>.</returns>
   public static async Task<string[]> UnhealthyIsNotRetriedAsync( string scratch )
   {
      return await StartAsync( scratch, "unhealthy", "touch /flag/seen; sleep 3600", "false", null );
   }

   /// <summary>
   /// The container exits with 134 every time: three attempts, all three in the error, and still no data lost.
   /// </summary>
   /// <param name="scratch">Folder for the scratch directories.</param>
   /// <returns>The same lines as <see cref="RecoversAsync"/>.</returns>
   public static async Task<string[]> ExitsEveryTimeAsync( string scratch )
   {
      return await StartAsync( scratch, "always", "touch /flag/seen; echo 'panic: always'; exit 134", "test -f /flag/seen", null );
   }

   #endregion Public Methods

   #region Private Methods

   /// <summary>
   /// Writes a one-container compose project, starts it with the real runner, reads the outcome back and
   /// cleans everything up (in a finally, so a failed assertion elsewhere never leaves the container).
   /// </summary>
   /// <param name="scratch">Folder for the scratch directories.</param>
   /// <param name="tag">Short name; the project and container are gvbbench-composetest-{tag}.</param>
   /// <param name="command">Shell command the container runs.</param>
   /// <param name="healthTest">Shell command of the health check.</param>
   /// <param name="cpuset">CPU list to start with, or null.</param>
   /// <returns>Lines for the test.</returns>
   private static async Task<string[]> StartAsync( string scratch, string tag, string command, string healthTest, string? cpuset )
   {
      string name = $"gvbbench-composetest-{tag}";
      string folder = Path.Combine( scratch, $"compose-live-{Guid.NewGuid():N}" );
      string flag = Path.Combine( folder, "flag" );
      Directory.CreateDirectory( flag );
      string file = Path.Combine( folder, "compose.yaml" );
      await File.WriteAllTextAsync( file, ComposeText( name, flag, command, healthTest ) );
      using var cancel = new CancellationTokenSource( LIMIT );
      var lines = new List<string>();
      try
      {
         try
         {
            lines.Add( "line|" + await ComposeRunner.UpAsync( file, cpuset, cancel.Token ) );
         }
         catch( Exception ex ) when( ex is InvalidOperationException or TimeoutException )
         {
            lines.Add( $"error|{ex.GetType().Name}: {ex.Message}" );
         }

         lines.Add( "flag|" + Directory.EnumerateFileSystemEntries( flag ).Any( f => Path.GetFileName( f ) == "seen" ) );
         lines.Add( "running|" + ( await ComposeRunner.IsRunningAsync( file, cancel.Token ) ) );
      }
      finally
      {
         await ComposeRunner.DownAsync( file, CancellationToken.None );
         await RemoveScratchAsync( scratch, folder );
      }

      return lines.ToArray();
   }

   /// <summary>
   /// The compose text of one throwaway container.
   /// </summary>
   /// <param name="name">Project and container name.</param>
   /// <param name="flag">Host folder mounted at /flag.</param>
   /// <param name="command">Shell command the container runs.</param>
   /// <param name="healthTest">Shell command of the health check.</param>
   /// <returns>The YAML.</returns>
   private static string ComposeText( string name, string flag, string command, string healthTest )
   {
      var text = new StringBuilder();
      text.Append( "name: " ).Append( name ).Append( "\nservices:\n  probe:\n" );
      text.Append( "    image: " ).Append( IMAGE ).Append( "\n    container_name: " ).Append( name ).Append( "\n    restart: \"no\"\n    init: true\n" );
      text.Append( "    entrypoint: [\"sh\", \"-c\", \"" ).Append( command.Replace( "\"", "\\\"" ) ).Append( "\"]\n" );
      text.Append( "    volumes:\n      - " ).Append( flag ).Append( ":/flag\n    mem_limit: 64m\n" );
      text.Append( "    healthcheck:\n      test: [\"CMD-SHELL\", \"" ).Append( healthTest ).Append( "\"]\n      interval: 1s\n      timeout: 2s\n      retries: 3\n      start_period: 1s\n" );
      return text.ToString();
   }

   /// <summary>
   /// Removes the scratch folder this scenario created. The container wrote into it as root, so it takes sudo.
   /// Why the path is checked: "rm -rf" must never be handed anything but the folder created above.
   /// </summary>
   /// <param name="scratch">The scratch root.</param>
   /// <param name="folder">The folder this scenario created.</param>
   private static async Task RemoveScratchAsync( string scratch, string folder )
   {
      if( !Path.GetFileName( folder ).StartsWith( "compose-live-", StringComparison.Ordinal ) || Path.GetDirectoryName( folder ) != scratch )
      {
         throw new InvalidOperationException( $"Refusing to remove '{folder}'." );
      }

      await Shell.RunAsync( "sudo", new[] { "-n", "rm", "-rf", folder }, TimeSpan.FromMinutes( 1 ), CancellationToken.None );
   }

   #endregion Private Methods
}
#endif
