using System.Diagnostics;
using System.Text.Json;

namespace GenericVectorBuilder.Engines.Tests.Bench;

/// <summary>
/// The image pins (Targets/ImagePins.cs, deploy/bench/image-pins.json and deploy/bench/make-image-pins.sh): the pins file lists exactly
/// the 17 container targets with real image ids, a container whose image is not the pinned one is reported, a missing pins file is an
/// error and not an empty table, and the script that writes the file fails loud and never leaves a partial file.
/// Why: v7 recorded no image ids, so "the same engine" rested on tag times, and two tags float (chromadb/chroma:latest and
/// gvenzl/oracle-free:23-slim-faststart). A pins file that was silently absent would pin nothing.
/// </summary>
public sealed class ImagePinsTests
{
   #region Data Members

   private const string ID = "sha256:6f81e8915c60b065a524e6967e0ad1c639ba6efa84d669f823683ea04d9150ee";
   private const string OTHER_ID = "sha256:1e0b73a187a28757c572acba508c46f48c9e8b0acaf5c20e6d95cdedce1acdf6";
   private static readonly string PINS = $$"""{ "redis": { "ref": "redis:8.10.2", "id": "{{ID}}" } }""";

   #endregion Data Members

   #region Public Methods

   /// <summary>
   /// The pins file in the repository lists exactly the container targets (the 13 compose engines and the four built-in container targets),
   /// each with a sha256 image id and a reference; sql and sql-diskann share one pin, as do qdrant and qdrant-hnsw; the mariadb pin is
   /// the benchmark's own container's image and not an entry for the daily one.
   /// </summary>
   [Fact]
   public void TheRepositoryPinsFileListsExactlyTheContainerTargets()
   {
      string path = Path.Combine( TargetsHarness.RepoRoot(), "deploy", "bench", "image-pins.json" );
      string line = (string)TargetsHarness.CallSettings( "LoadPins", path );
      string[] expected = (string[])TargetsHarness.CallSettings( "ContainerTargetNames" );
      Assert.Equal( 17, expected.Length );
      Assert.Equal( "targets|" + string.Join( ",", expected ), line );
      using JsonDocument document = JsonDocument.Parse( File.ReadAllText( path ) );
      foreach( JsonProperty pin in document.RootElement.EnumerateObject() )
      {
         Assert.Matches( "^sha256:[0-9a-f]{64}$", pin.Value.GetProperty( "id" ).GetString() );
         Assert.False( string.IsNullOrWhiteSpace( pin.Value.GetProperty( "ref" ).GetString() ), pin.Name );
      }

      Assert.Equal( document.RootElement.GetProperty( "sql" ).GetProperty( "id" ).GetString(), document.RootElement.GetProperty( "sql-diskann" ).GetProperty( "id" ).GetString() );
      Assert.Equal( document.RootElement.GetProperty( "qdrant" ).GetProperty( "id" ).GetString(), document.RootElement.GetProperty( "qdrant-hnsw" ).GetProperty( "id" ).GetString() );
      Assert.Equal( "mariadb:11.8.9", document.RootElement.GetProperty( "mariadb" ).GetProperty( "ref" ).GetString() );
   }

   /// <summary>
   /// A missing pins file is an error that says where to get one, an empty table and a bad id are errors too: never a table that pins nothing.
   /// </summary>
   [Fact]
   public void AMissingOrBadPinsFileFailsLoud()
   {
      string missing = (string)TargetsHarness.CallSettings( "LoadPins", "/home/dan/gvb-work/does-not-exist/image-pins.json" );
      Assert.Equal( "error|The image pins file /home/dan/gvb-work/does-not-exist/image-pins.json is missing. Run deploy/bench/make-image-pins.sh to write it; without it no engine's image can be checked.", missing );
      string folder = Path.Combine( Path.GetTempPath(), "gvb-pins-" + Guid.NewGuid().ToString( "N" ) );
      Directory.CreateDirectory( folder );
      try
      {
         string empty = Path.Combine( folder, "empty.json" );
         File.WriteAllText( empty, "{}" );
         Assert.Equal( $"error|The image pins file {empty} is not usable: the file pins no image.", (string)TargetsHarness.CallSettings( "LoadPins", empty ) );
         string bad = Path.Combine( folder, "bad.json" );
         File.WriteAllText( bad, """{ "redis": { "ref": "redis:8", "id": "sha256:abc" } }""" );
         Assert.Equal( $"error|The image pins file {bad} is not usable: the entry for redis has no ref or an id that is not sha256:<64 hex digits>.", (string)TargetsHarness.CallSettings( "LoadPins", bad ) );
         string odd = Path.Combine( folder, "odd.json" );
         File.WriteAllText( odd, "[1,2" );
         Assert.Equal( $"error|The image pins file {odd} is not usable: it is not valid JSON.", (string)TargetsHarness.CallSettings( "LoadPins", odd ) );
      }
      finally
      {
         Directory.Delete( folder, true );
      }
   }

   /// <summary>
   /// A container running the pinned image gives its ref, id, pinned id and the last tag time Docker prints; the image is looked up by id
   /// (not by tag, which may have moved) with a JSON metadata format.
   /// </summary>
   [Fact]
   public void ThePinnedImageIsReportedWithItsLastTagTime()
   {
      string[] lines = (string[])TargetsHarness.CallSettings( "CheckImage", PINS, "redis", Inspect( ID ), """0|{"LastTagTime":"2026-10-03T14:09:34.185138197Z"}|""" );
      Assert.Equal( new[] { $"facts|redis:8.10.2|{ID}|{ID}|2026-10-03T14:09:34.185138197Z|", $"call|docker image inspect --format {{{{json .Metadata}}}} {ID}" }, lines );
   }

   /// <summary>
   /// A container on another image than the pinned one, or a target with no pin at all, is reported as a problem that names the container,
   /// both ids and the pins file; the facts still carry what was found so the record shows it.
   /// </summary>
   [Fact]
   public void AnotherImageOrNoPinIsAProblemThatNamesBothIds()
   {
      string[] other = (string[])TargetsHarness.CallSettings( "CheckImage", PINS, "redis", Inspect( OTHER_ID ), """0|{"LastTagTime":"2026-10-03T14:09:56.392230738Z"}|""" );
      Assert.Equal( $"facts|redis:8.10.2|{OTHER_ID}|{ID}|2026-10-03T14:09:56.392230738Z|the container gvb-redis runs image {OTHER_ID} but image-pins.json pins {ID} for redis (redis:8.10.2)", other[0] );
      string[] none = (string[])TargetsHarness.CallSettings( "CheckImage", PINS, "typesense", Inspect( ID ), """0|{"LastTagTime":"2026-10-03T14:09:34.185138197Z"}|""" );
      Assert.EndsWith( "|no image is pinned for typesense in image-pins.json, so its image cannot be checked", none[0] );
   }

   /// <summary>
   /// Docker failing to inspect the image, or printing something that is not metadata or a UTC time, is an error that names the image
   /// and the container; metadata without a tag time gives none, which the record keeps as absent.
   /// </summary>
   [Fact]
   public void ADockerFailureOrAnOddAnswerIsAnError()
   {
      string[] failed = (string[])TargetsHarness.CallSettings( "CheckImage", PINS, "redis", Inspect( ID ), "1||Error response from daemon: No such image" );
      Assert.Equal( new[] { $"error|docker image inspect {ID} (the image of gvb-redis) exited with code 1: Error response from daemon: No such image" }, failed );
      Assert.Equal( "error|docker image inspect did not print JSON for .Metadata", (string)TargetsHarness.CallSettings( "TagTime", "not json" ) );
      Assert.Equal( "error|LastTagTime '2026-10-03 14:09:34' is not an ISO 8601 UTC time", (string)TargetsHarness.CallSettings( "TagTime", """{"LastTagTime":"2026-10-03 14:09:34"}""" ) );
      Assert.Equal( "none", (string)TargetsHarness.CallSettings( "TagTime", "{}" ) );
      Assert.Equal( "2026-10-04T22:03:16.973657523Z", (string)TargetsHarness.CallSettings( "TagTime", """{"LastTagTime":"2026-10-04T22:03:16.973657523Z"}""" ) );
   }

   /// <summary>
   /// make-image-pins.sh against a fake docker: it writes one entry per target in sorted order with the ref from the compose file and the
   /// id the fake gives, the file appears only when every target succeeded, and the output is valid JSON with LF endings.
   /// </summary>
   [Fact]
   public void TheScriptWritesOneEntryPerTargetFromTheComposeFilesAndTheFakeDocker()
   {
      using var scratch = new Scratch();
      (int exit, string output, string error) = scratch.Run();
      Assert.Equal( 0, exit );
      Assert.Contains( "wrote 17 pins", output );
      Assert.Equal( string.Empty, error );
      using JsonDocument document = JsonDocument.Parse( File.ReadAllText( scratch.Out ) );
      Assert.Equal( 17, document.RootElement.EnumerateObject().Count() );
      JsonElement redis = document.RootElement.GetProperty( "redis" );
      Assert.Equal( "redis:8.10.2", redis.GetProperty( "ref" ).GetString() );
      Assert.Equal( scratch.IdOf( "redis:8.10.2" ), redis.GetProperty( "id" ).GetString() );
      Assert.Equal( "mariadb:11.8.9", document.RootElement.GetProperty( "mariadb" ).GetProperty( "ref" ).GetString() );
      Assert.Equal( "qdrant/qdrant:v1.17.0@sha256:f1c7272cdac52b38c1a0e89313922d940ba50afd90d593a1605dbbc214e66ffb", document.RootElement.GetProperty( "qdrant-hnsw" ).GetProperty( "ref" ).GetString() );
      Assert.DoesNotContain( '\r', File.ReadAllText( scratch.Out ) );
      Assert.Equal( document.RootElement.EnumerateObject().Select( p => p.Name ).OrderBy( n => n, StringComparer.Ordinal ).ToArray(), document.RootElement.EnumerateObject().Select( p => p.Name ).ToArray() );
   }

   /// <summary>
   /// The script fails loud: an image Docker does not know, a docker that answers with something that is not an image id, a compose file
   /// that is missing, and a docker call that outlives its limit each end it with a non-zero exit, a message naming the target and the step,
   /// and no output file (an earlier good file is left as it was).
   /// </summary>
   [Fact]
   public void TheScriptFailsLoudAndLeavesNoPartialFile()
   {
      using var unknown = new Scratch();
      File.WriteAllText( unknown.Out, "{\"old\":{\"ref\":\"x\",\"id\":\"sha256:" + new string( 'a', 64 ) + "\"}}" );
      (int exit, _, string error) = unknown.Run( ( "FAKE_UNKNOWN", "typesense/typesense:30.2" ) );
      Assert.NotEqual( 0, exit );
      Assert.Contains( "typesense: 'docker image inspect typesense/typesense:30.2' failed or did not answer within", error );
      Assert.Contains( "\"old\"", File.ReadAllText( unknown.Out ) );

      using var odd = new Scratch();
      ( exit, _, error ) = odd.Run( ( "FAKE_ODD", "redis:8.10.2" ) );
      Assert.NotEqual( 0, exit );
      Assert.Contains( "redis: docker answered 'not-an-id' for redis:8.10.2, which is not a sha256 image id", error );
      Assert.False( File.Exists( odd.Out ) );

      using var missing = new Scratch();
      File.Delete( Path.Combine( missing.Engines, "weaviate.compose.yaml" ) );
      ( exit, _, error ) = missing.Run();
      Assert.NotEqual( 0, exit );
      Assert.Contains( "weaviate: compose file", error );
      Assert.False( File.Exists( missing.Out ) );

      using var slow = new Scratch();
      ( exit, _, error ) = slow.Run( ( "FAKE_SLEEP", "elasticsearch:9.5.3" ), ( "GVB_DOCKER_TIMEOUT", "1" ) );
      Assert.NotEqual( 0, exit );
      Assert.Contains( "elasticsearch: 'docker image inspect elasticsearch:9.5.3' failed or did not answer within 1s", error );
      Assert.False( File.Exists( slow.Out ) );
   }

   #endregion Public Methods

   #region Private Methods

   /// <summary>
   /// "docker inspect" text for a gvb-redis container on an image.
   /// </summary>
   /// <param name="imageId">The image id the container runs.</param>
   /// <returns>The JSON text.</returns>
   private static string Inspect( string imageId )
   {
      return (string)TargetsHarness.CallSettings( "Inspect", "gvb-redis", Array.Empty<string>(), new[] { "/home/dan/gvb-data/engines/redis:/data:rw" }, new[] { "6379:6379" }, imageId );
   }

   #endregion Private Methods

   #region Nested Types

   /// <summary>
   /// A scratch copy of the engine compose files and a fake docker, so the script runs without Docker and writes nowhere else.
   /// </summary>
   private sealed class Scratch : IDisposable
   {
      #region Data Members

      private readonly string _root = Path.Combine( Path.GetTempPath(), "gvb-pinscript-" + Guid.NewGuid().ToString( "N" ) );

      #endregion Data Members

      #region Constructor

      /// <summary>
      /// Copies the compose files and writes the fake docker.
      /// </summary>
      public Scratch()
      {
         Engines = Path.Combine( _root, "engines" );
         Directory.CreateDirectory( Engines );
         foreach( string file in Directory.EnumerateFiles( Path.Combine( TargetsHarness.RepoRoot(), "deploy", "engines" ), "*.compose.yaml" ) )
         {
            File.Copy( file, Path.Combine( Engines, Path.GetFileName( file ) ) );
         }

         Out = Path.Combine( _root, "out", "image-pins.json" );
         Directory.CreateDirectory( Path.GetDirectoryName( Out )! );
         Docker = Path.Combine( _root, "fake-docker.sh" );
         File.WriteAllText( Docker, "#!/usr/bin/env bash\n[[ \"$1 $2 $3\" == \"image inspect --format\" ]] || { echo \"unexpected: $*\" >&2; exit 9; }\nref=\"${@: -1}\"\n"
            + "[[ \"$ref\" == \"$FAKE_UNKNOWN\" ]] && { echo \"Error response from daemon: No such image: $ref\" >&2; exit 1; }\n"
            + "[[ \"$ref\" == \"$FAKE_ODD\" ]] && { echo not-an-id; exit 0; }\n[[ \"$ref\" == \"$FAKE_SLEEP\" ]] && { exec sleep 30; }\n"
            + "printf 'sha256:%s\\n' \"$(printf '%s' \"$ref\" | sha256sum | cut -d' ' -f1)\"\n" );
      }

      #endregion Constructor

      #region Public Methods

      /// <summary>The scratch folder of compose files.</summary>
      public string Engines { get; }

      /// <summary>The pins file the script is told to write.</summary>
      public string Out { get; }

      /// <summary>The fake docker's path.</summary>
      public string Docker { get; }

      /// <summary>
      /// The id the fake docker gives an image reference.
      /// </summary>
      /// <param name="reference">The reference.</param>
      /// <returns>The id.</returns>
      public string IdOf( string reference )
      {
         return "sha256:" + Convert.ToHexString( System.Security.Cryptography.SHA256.HashData( System.Text.Encoding.UTF8.GetBytes( reference ) ) ).ToLowerInvariant();
      }

      /// <summary>
      /// Runs the script with the fake docker and extra environment, waiting at most two minutes.
      /// </summary>
      /// <param name="environment">Extra environment variables.</param>
      /// <returns>Exit code, standard output and standard error.</returns>
      public (int Exit, string Output, string Error) Run( params (string Name, string Value)[] environment )
      {
         var start = new ProcessStartInfo( "bash" ) { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false };
         start.ArgumentList.Add( Path.Combine( TargetsHarness.RepoRoot(), "deploy", "bench", "make-image-pins.sh" ) );
         start.Environment["GVB_DOCKER"] = "bash " + Docker;
         start.Environment["GVB_ENGINES_DIR"] = Engines;
         start.Environment["GVB_PINS_OUT"] = Out;
         start.Environment["GVB_DOCKER_TIMEOUT"] = "20";
         foreach( ( string name, string value ) in environment )
         {
            start.Environment[name] = value;
         }

         using Process process = Process.Start( start )!;
         Task<string> output = process.StandardOutput.ReadToEndAsync();
         Task<string> error = process.StandardError.ReadToEndAsync();
         if( !process.WaitForExit( TimeSpan.FromMinutes( 2 ) ) )
         {
            process.Kill( true );
            throw new TimeoutException( "make-image-pins.sh did not finish within 2 minutes." );
         }

         return ( process.ExitCode, output.GetAwaiter().GetResult(), error.GetAwaiter().GetResult() );
      }

      #endregion Public Methods

      #region IDisposable

      /// <summary>
      /// Removes the scratch folder.
      /// </summary>
      public void Dispose()
      {
         Directory.Delete( _root, true );
      }

      #endregion IDisposable
   }

   #endregion Nested Types
}
