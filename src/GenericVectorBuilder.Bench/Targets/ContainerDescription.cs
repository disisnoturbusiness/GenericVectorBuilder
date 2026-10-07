using System.Text.Json;

namespace GenericVectorBuilder.Bench.Targets;

/// <summary>
/// One setting read for a target: what it is called, its value as text, and how it was
/// obtained ("read: ..." from the running engine or from Docker, "set: path#token" when this
/// setup's own code sets it and the token is found in that file).
/// </summary>
/// <param name="Key">The setting's name, from <see cref="EngineSettingsReader.ALLOWED_KEYS"/>.</param>
/// <param name="Value">The value, as text.</param>
/// <param name="How">How it was obtained.</param>
public sealed record SettingRead( string Key, string Value, string How );

/// <summary>
/// One mount of a container, from the "Mounts" list of "docker inspect".
/// </summary>
/// <param name="Type">"bind" or "volume".</param>
/// <param name="Source">The host path.</param>
/// <param name="Destination">The path inside the container.</param>
/// <param name="ReadWrite">True when the container may write to it.</param>
public sealed record ContainerMount( string Type, string Source, string Destination, bool ReadWrite );

/// <summary>
/// The parts of "docker inspect" text that the settings, image and data-folder reads use: the
/// container's name and id, the image it runs (id and reference), its memory, CPU and CPU-set
/// limits, the few environment values that are allowed to be kept, and its mounts.
/// Why its own parser and not <see cref="ContainerFacts"/>: that record is the address finder's and
/// its constructor is not to change; these facts are read tolerantly (a field Docker leaves out is
/// zero or empty, as Docker itself prints it).
/// Why environment values are dropped: eight of the engine compose files load the whole of
/// secrets.env into the container, so the environment holds passwords. Only the names listed by the
/// caller keep their value, and every other value is discarded while it is read.
/// </summary>
/// <param name="Name">Container name without the leading slash.</param>
/// <param name="Id">The container's full id.</param>
/// <param name="ImageId">The id of the image the container was created from (the container's own Image field).</param>
/// <param name="ImageRef">The image reference as the container's config states it (Config.Image).</param>
/// <param name="MemoryBytes">HostConfig.Memory; 0 means no limit.</param>
/// <param name="Cpuset">HostConfig.CpusetCpus; empty means every CPU.</param>
/// <param name="NanoCpus">HostConfig.NanoCpus; 0 means no limit.</param>
/// <param name="Env">The environment entries whose names the caller asked to keep, by name.</param>
/// <param name="Mounts">The container's mounts.</param>
public sealed record ContainerDescription( string Name, string Id, string ImageId, string ImageRef, long MemoryBytes, string Cpuset, long NanoCpus,
   IReadOnlyDictionary<string, string> Env, IReadOnlyList<ContainerMount> Mounts )
{
   #region Public Methods

   /// <summary>
   /// Reads the text "docker inspect NAME" prints (a JSON array with one container).
   /// Why the message never carries the text: the environment holds passwords, so no error
   /// or note may repeat any part of the inspect output.
   /// </summary>
   /// <param name="json">The inspect output.</param>
   /// <param name="keepEnv">Environment names whose values may be kept (all others are dropped).</param>
   /// <returns>The description.</returns>
   /// <exception cref="InvalidOperationException">The text is not an inspect result for a container.</exception>
   public static ContainerDescription Parse( string json, IReadOnlyCollection<string> keepEnv )
   {
      try
      {
         using JsonDocument document = JsonDocument.Parse( json );
         JsonElement root = document.RootElement.ValueKind == JsonValueKind.Array && document.RootElement.GetArrayLength() > 0 ? document.RootElement[0] : document.RootElement;
         if( root.ValueKind != JsonValueKind.Object )
         {
            throw new InvalidOperationException( "the text is not a JSON object or an array holding one" );
         }

         string id = Text( root, "Id" );
         string name = Text( root, "Name" ).TrimStart( '/' );
         if( id.Length == 0 || name.Length == 0 )
         {
            throw new InvalidOperationException( "the container has no Id or no Name" );
         }

         return new ContainerDescription( name, id, Text( root, "Image" ), Inner( root, "Config", "Image" ).GetStringOrEmpty(), Inner( root, "HostConfig", "Memory" ).GetLongOrZero(),
            Inner( root, "HostConfig", "CpusetCpus" ).GetStringOrEmpty(), Inner( root, "HostConfig", "NanoCpus" ).GetLongOrZero(), ReadEnv( root, keepEnv ), ReadMounts( root ) );
      }
      catch( Exception ex ) when( ex is JsonException or InvalidOperationException or KeyNotFoundException or FormatException )
      {
         string why = ex is InvalidOperationException ? ex.Message : ex.GetType().Name;
         throw new InvalidOperationException( $"docker inspect did not return a container description ({why})" );
      }
   }

   #endregion Public Methods

   #region Private Methods

   /// <summary>
   /// A string property of the container object, or empty.
   /// </summary>
   /// <param name="root">The container object.</param>
   /// <param name="name">Property name.</param>
   /// <returns>The text.</returns>
   private static string Text( JsonElement root, string name )
   {
      return root.TryGetProperty( name, out JsonElement value ) && value.ValueKind == JsonValueKind.String ? value.GetString() ?? string.Empty : string.Empty;
   }

   /// <summary>
   /// A property two levels down, or an empty (undefined) element when either level is missing.
   /// </summary>
   /// <param name="root">The container object.</param>
   /// <param name="section">First level, e.g. "HostConfig".</param>
   /// <param name="key">Second level, e.g. "Memory".</param>
   /// <returns>The element; its ValueKind is Undefined when it is not there.</returns>
   private static JsonElement Inner( JsonElement root, string section, string key )
   {
      return root.TryGetProperty( section, out JsonElement outer ) && outer.ValueKind == JsonValueKind.Object && outer.TryGetProperty( key, out JsonElement value ) ? value : default;
   }

   /// <summary>
   /// The environment entries named in <paramref name="keep"/>; every other entry's value is dropped here.
   /// </summary>
   /// <param name="root">The container object.</param>
   /// <param name="keep">Names to keep.</param>
   /// <returns>Name to value.</returns>
   private static Dictionary<string, string> ReadEnv( JsonElement root, IReadOnlyCollection<string> keep )
   {
      var kept = new Dictionary<string, string>( StringComparer.Ordinal );
      JsonElement env = Inner( root, "Config", "Env" );
      if( env.ValueKind != JsonValueKind.Array )
      {
         return kept;
      }

      foreach( JsonElement entry in env.EnumerateArray().Where( e => e.ValueKind == JsonValueKind.String ) )
      {
         string[] parts = ( entry.GetString() ?? string.Empty ).Split( '=', 2 );
         if( parts.Length == 2 && keep.Contains( parts[0] ) )
         {
            kept[parts[0]] = parts[1];
         }
      }

      return kept;
   }

   /// <summary>
   /// Reads the mounts ("Mounts": [ { Type, Source, Destination, RW } ]).
   /// </summary>
   /// <param name="root">The container object.</param>
   /// <returns>The mounts, in the order Docker lists them.</returns>
   private static List<ContainerMount> ReadMounts( JsonElement root )
   {
      var mounts = new List<ContainerMount>();
      if( !root.TryGetProperty( "Mounts", out JsonElement list ) || list.ValueKind != JsonValueKind.Array )
      {
         return mounts;
      }

      foreach( JsonElement mount in list.EnumerateArray().Where( m => m.ValueKind == JsonValueKind.Object ) )
      {
         bool writable = !mount.TryGetProperty( "RW", out JsonElement rw ) || rw.ValueKind != JsonValueKind.False;
         mounts.Add( new ContainerMount( Text( mount, "Type" ), Text( mount, "Source" ), Text( mount, "Destination" ), writable ) );
      }

      return mounts;
   }

   #endregion Private Methods
}

/// <summary>
/// Small readers for a JSON element that may be missing: the text or the whole number it holds, else empty or zero.
/// Why extension methods: the inspect fields are optional, and every use would otherwise repeat the same kind checks.
/// </summary>
internal static class JsonElementReads
{
   #region Public Methods

   /// <summary>
   /// The element's text, or empty when it is missing or not text.
   /// </summary>
   /// <param name="element">The element (possibly undefined).</param>
   /// <returns>The text.</returns>
   public static string GetStringOrEmpty( this JsonElement element )
   {
      return element.ValueKind == JsonValueKind.String ? element.GetString() ?? string.Empty : string.Empty;
   }

   /// <summary>
   /// The element's whole number, or zero when it is missing or not a whole number.
   /// </summary>
   /// <param name="element">The element (possibly undefined).</param>
   /// <returns>The number.</returns>
   public static long GetLongOrZero( this JsonElement element )
   {
      return element.ValueKind == JsonValueKind.Number && element.TryGetInt64( out long value ) ? value : 0;
   }

   #endregion Public Methods
}
