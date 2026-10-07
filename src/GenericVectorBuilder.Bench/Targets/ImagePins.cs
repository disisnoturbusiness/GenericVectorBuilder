using System.Text.Json;
using System.Text.RegularExpressions;

namespace GenericVectorBuilder.Bench.Targets;

/// <summary>
/// One pinned image: the reference a compose file names and the id Docker held for it when the
/// pins were written.
/// </summary>
/// <param name="Ref">The reference exactly as the compose file writes it.</param>
/// <param name="Id">The image id, "sha256:" and 64 hex digits.</param>
public sealed record ImagePin( string Ref, string Id );

/// <summary>
/// What was found out about the image one target's container runs.
/// </summary>
/// <param name="Ref">The image reference the container was created from.</param>
/// <param name="Id">The id of the image it is running.</param>
/// <param name="PinnedId">The id pinned for the target, or null when none is.</param>
/// <param name="LastTagTimeUtc">When Docker last tagged the image here, as Docker prints it, or null when it says nothing.</param>
/// <param name="Problem">Why the image is not the pinned one (a different id, or no pin), or null when it is.</param>
public sealed record ImageFacts( string Ref, string Id, string? PinnedId, string? LastTagTimeUtc, string? Problem );

/// <summary>
/// The image ids the benchmark expects each container engine to run, read from
/// deploy/bench/image-pins.json (written by deploy/bench/make-image-pins.sh from "docker image
/// inspect"), and the check of a running container against them.
/// Why: v7 recorded no image ids, so "the same engine" rested on tag times, and two tags
/// (chromadb/chroma:latest, gvenzl/oracle-free:23-slim-faststart) float. A container whose image id
/// is not the pinned one is that target's error, so a run on a different build is never taken for
/// the build the earlier sessions measured.
/// Why the file is looked for beside the program (<see cref="AppContext.BaseDirectory"/>) and a
/// missing file is an error: the frozen build runs from its own folder and finds the repository
/// by walking up to the solution file, which a published build does not have; a pins file that is
/// silently absent would pin nothing.
/// </summary>
public sealed class ImagePins
{
   #region Data Members

   /// <summary>The file's name; it is copied next to the program at build time.</summary>
   public const string FILE_NAME = "image-pins.json";

   /// <summary>Longest "docker image inspect" may take.</summary>
   public static readonly TimeSpan INSPECT_TIMEOUT = TimeSpan.FromSeconds( 30 );

   private static readonly Regex IMAGE_ID = new( "^sha256:[0-9a-f]{64}$", RegexOptions.Compiled );
   private static readonly Regex TAG_TIME = new( @"^\d{4}-\d{2}-\d{2}T\d{2}:\d{2}:\d{2}(\.\d+)?Z$", RegexOptions.Compiled );

   private readonly IReadOnlyDictionary<string, ImagePin> _pins;

   #endregion Data Members

   #region Constructor

   /// <summary>
   /// Creates the pins from a table.
   /// </summary>
   /// <param name="pins">Target name to pin.</param>
   public ImagePins( IReadOnlyDictionary<string, ImagePin> pins )
   {
      _pins = pins;
   }

   #endregion Constructor

   #region Public Methods

   /// <summary>The targets that have a pin.</summary>
   public IReadOnlyCollection<string> Targets => _pins.Keys.ToList();

   /// <summary>
   /// Loads the pins file from beside the program.
   /// </summary>
   /// <returns>The pins.</returns>
   /// <exception cref="InvalidOperationException">The file is missing or unreadable.</exception>
   public static ImagePins LoadDefault()
   {
      return Load( Path.Combine( AppContext.BaseDirectory, FILE_NAME ) );
   }

   /// <summary>
   /// Loads a pins file.
   /// </summary>
   /// <param name="path">The file.</param>
   /// <returns>The pins.</returns>
   /// <exception cref="InvalidOperationException">The file is missing, is not a JSON object of { ref, id } entries, or an id is not a sha256 image id.</exception>
   public static ImagePins Load( string path )
   {
      if( !File.Exists( path ) )
      {
         throw new InvalidOperationException( $"The image pins file {path} is missing. Run deploy/bench/make-image-pins.sh to write it; without it no engine's image can be checked." );
      }

      try
      {
         using JsonDocument document = JsonDocument.Parse( File.ReadAllText( path ) );
         var pins = new Dictionary<string, ImagePin>( StringComparer.Ordinal );
         foreach( JsonProperty entry in document.RootElement.EnumerateObject() )
         {
            string reference = entry.Value.GetProperty( "ref" ).GetString() ?? string.Empty;
            string id = entry.Value.GetProperty( "id" ).GetString() ?? string.Empty;
            if( reference.Length == 0 || !IMAGE_ID.IsMatch( id ) )
            {
               throw new InvalidOperationException( $"the entry for {entry.Name} has no ref or an id that is not sha256:<64 hex digits>" );
            }

            pins[entry.Name] = new ImagePin( reference, id );
         }

         return pins.Count > 0 ? new ImagePins( pins ) : throw new InvalidOperationException( "the file pins no image" );
      }
      catch( Exception ex ) when( ex is JsonException or KeyNotFoundException or InvalidOperationException or InvalidCastException )
      {
         string why = ex switch
         {
            InvalidOperationException => ex.Message,
            JsonException => "it is not valid JSON",
            KeyNotFoundException => "an entry has no ref or no id",
            _ => "an entry is not an object with a ref and an id",
         };
         throw new InvalidOperationException( $"The image pins file {path} is not usable: {why}." );
      }
   }

   /// <summary>
   /// The pin of a target, or null.
   /// </summary>
   /// <param name="target">Target name.</param>
   /// <returns>The pin.</returns>
   public ImagePin? Find( string target )
   {
      return _pins.TryGetValue( target, out ImagePin? pin ) ? pin : null;
   }

   /// <summary>
   /// Reads what Docker says about the image a container runs and sets it against the pin.
   /// </summary>
   /// <param name="probe">Runs "docker image inspect".</param>
   /// <param name="target">Target name.</param>
   /// <param name="container">The container.</param>
   /// <param name="ct">Cancellation.</param>
   /// <returns>The facts; <see cref="ImageFacts.Problem"/> says when the image is not the pinned one.</returns>
   /// <exception cref="InvalidOperationException">Docker could not be asked about the image, or answered with something that is not a tag time.</exception>
   public async Task<ImageFacts> ReadAsync( IEngineProbe probe, string target, ContainerDescription container, CancellationToken ct )
   {
      ImagePin? pin = Find( target );
      string? tagged = await ReadTagTimeAsync( probe, container, ct );
      string? problem = pin == null
         ? $"no image is pinned for {target} in {FILE_NAME}, so its image cannot be checked"
         : pin.Id == container.ImageId ? null : $"the container {container.Name} runs image {container.ImageId} but {FILE_NAME} pins {pin.Id} for {target} ({pin.Ref})";
      return new ImageFacts( container.ImageRef, container.ImageId, pin?.Id, tagged, problem );
   }

   /// <summary>
   /// Reads the last tag time out of the JSON "docker image inspect --format '{{json .Metadata}}'" prints.
   /// </summary>
   /// <param name="json">The JSON text.</param>
   /// <returns>The time as Docker prints it (ISO 8601, UTC), or null when the metadata has none.</returns>
   /// <exception cref="InvalidOperationException">The text is not JSON, or the time is not an ISO 8601 UTC time.</exception>
   public static string? ParseTagTime( string json )
   {
      try
      {
         using JsonDocument document = JsonDocument.Parse( json );
         if( document.RootElement.ValueKind != JsonValueKind.Object || !document.RootElement.TryGetProperty( "LastTagTime", out JsonElement time ) || time.ValueKind != JsonValueKind.String )
         {
            return null;
         }

         string text = time.GetString() ?? string.Empty;
         return TAG_TIME.IsMatch( text ) ? text : throw new InvalidOperationException( $"LastTagTime '{text}' is not an ISO 8601 UTC time" );
      }
      catch( JsonException )
      {
         throw new InvalidOperationException( "docker image inspect did not print JSON for .Metadata" );
      }
   }

   #endregion Public Methods

   #region Private Methods

   /// <summary>
   /// Asks Docker for the image's metadata by its id (not by its tag, which may have moved).
   /// </summary>
   /// <param name="probe">Runs Docker.</param>
   /// <param name="container">The container.</param>
   /// <param name="ct">Cancellation.</param>
   /// <returns>The last tag time, or null.</returns>
   private static async Task<string?> ReadTagTimeAsync( IEngineProbe probe, ContainerDescription container, CancellationToken ct )
   {
      ShellResult result;
      try
      {
         result = await probe.DockerAsync( new[] { "image", "inspect", "--format", "{{json .Metadata}}", container.ImageId }, INSPECT_TIMEOUT, ct );
      }
      catch( Exception ex ) when( ex is TimeoutException or System.ComponentModel.Win32Exception )
      {
         throw new InvalidOperationException( $"Could not ask Docker about image {container.ImageId} of {container.Name}: {ex.Message}" );
      }

      if( result.ExitCode != 0 )
      {
         string error = string.Join( ' ', result.Error.Split( new[] { ' ', '\t', '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries ) );
         throw new InvalidOperationException( $"docker image inspect {container.ImageId} (the image of {container.Name}) exited with code {result.ExitCode}: {( error.Length <= 200 ? error : error[..200] + "..." )}" );
      }

      return ParseTagTime( result.Output.Trim() );
   }

   #endregion Private Methods
}
