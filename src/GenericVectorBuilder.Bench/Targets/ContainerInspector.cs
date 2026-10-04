namespace GenericVectorBuilder.Bench.Targets;

/// <summary>
/// Asks Docker about one container. An interface so the address rules are tested with
/// captured "docker inspect" text and without Docker.
/// </summary>
public interface IContainerInspector
{
   /// <summary>
   /// Returns the text "docker inspect" prints for a container.
   /// </summary>
   /// <param name="container">Container name.</param>
   /// <param name="ct">Cancellation.</param>
   /// <returns>The JSON text, or null when no container of that name exists.</returns>
   /// <exception cref="InvalidOperationException">Docker could not be asked (not null: a missing container is null, anything else is a failure).</exception>
   Task<string?> InspectAsync( string container, CancellationToken ct );
}

/// <summary>
/// The real inspector: "sudo -n docker inspect", the same way every other docker call in the
/// benchmark is made (see <see cref="ComposeRunner"/>), with a time limit.
/// Why "-n": sudo must never wait for a password inside a benchmark; a box without passwordless
/// sudo gets a plain failure instead of a hang.
/// </summary>
public sealed class DockerInspector : IContainerInspector
{
   #region Data Members

   private static readonly TimeSpan INSPECT_TIMEOUT = TimeSpan.FromSeconds( 30 );

   #endregion Data Members

   #region Public Methods

   /// <summary>
   /// Runs "docker inspect" for one container.
   /// </summary>
   /// <param name="container">Container name.</param>
   /// <param name="ct">Cancellation.</param>
   /// <returns>The JSON text, or null when Docker says there is no such container.</returns>
   public async Task<string?> InspectAsync( string container, CancellationToken ct )
   {
      ShellResult result;
      try
      {
         result = await Shell.RunAsync( "sudo", new[] { "-n", "docker", "inspect", "--type", "container", container }, INSPECT_TIMEOUT, ct );
      }
      catch( Exception ex ) when( ex is TimeoutException or System.ComponentModel.Win32Exception )
      {
         throw new InvalidOperationException( $"Could not ask Docker about {container}: {ex.Message}" );
      }

      if( result.ExitCode == 0 )
      {
         return result.Output;
      }

      if( result.Error.Contains( "No such", StringComparison.OrdinalIgnoreCase ) )
      {
         return null;
      }

      throw new InvalidOperationException( $"docker inspect {container} failed with exit code {result.ExitCode}: {result.Error.Trim()}" );
   }

   #endregion Public Methods
}
