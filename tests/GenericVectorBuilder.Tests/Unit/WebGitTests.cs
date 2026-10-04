using System.Diagnostics;
using System.Net;
using System.Text.Json;
using System.Text.RegularExpressions;
using GenericVectorBuilder.Core.Configuration;
using GenericVectorBuilder.Core.Contracts;
using GenericVectorBuilder.Core.Pipeline;
using GenericVectorBuilder.Core.Sources.Odbc;
using GenericVectorBuilder.Core.State;
using GenericVectorBuilder.Tests.Fakes;
using GenericVectorBuilder.Web.Destinations;
using GenericVectorBuilder.Web.Endpoints;
using GenericVectorBuilder.Web.Git;
using GenericVectorBuilder.Web.Runs;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging.Abstractions;

namespace GenericVectorBuilder.Tests.Unit;

/// <summary>
/// A small git repository made for a test: a few C# files committed on "main" in a folder of its
/// own, which the test can edit and commit again.
/// </summary>
public sealed class GitOriginRepo
{
   #region Constructor

   /// <summary>
   /// Creates the folder (not the repository; see <see cref="InitAsync"/>).
   /// </summary>
   /// <param name="path">Folder for the repository; created when missing.</param>
   public GitOriginRepo( string path )
   {
      Path = path;
      Directory.CreateDirectory( path );
   }

   #endregion Constructor

   #region Public Methods

   /// <summary>The repository's folder, which is also its address for a local clone.</summary>
   public string Path { get; }

   /// <summary>
   /// Makes the repository with three C# files and a README, and commits them.
   /// </summary>
   /// <returns>The commit id.</returns>
   public async Task<string> InitAsync()
   {
      await GitAsync( "init", "--quiet", "--initial-branch=main" );
      Write( "src/Shop/Basket.cs", "namespace Shop;\n\npublic class Basket\n{\n   public int Count { get; set; }\n\n   public void Add() => Count++;\n}\n" );
      Write( "src/Shop/Order.cs", "namespace Shop;\n\npublic class Order\n{\n   public decimal Total { get; set; }\n}\n" );
      Write( "tests/Shop.Tests/BasketTests.cs", "namespace Shop.Tests;\n\npublic class BasketTests\n{\n   public void AddCounts() { }\n}\n" );
      Write( "README.md", "# small repo\n" );
      return await CommitAsync( "first" );
   }

   /// <summary>
   /// Writes a file relative to the repository.
   /// </summary>
   /// <param name="relative">Relative path.</param>
   /// <param name="content">Content.</param>
   public void Write( string relative, string content )
   {
      string full = System.IO.Path.Combine( Path, relative );
      Directory.CreateDirectory( System.IO.Path.GetDirectoryName( full )! );
      File.WriteAllText( full, content );
   }

   /// <summary>
   /// Deletes a file relative to the repository.
   /// </summary>
   /// <param name="relative">Relative path.</param>
   public void Delete( string relative )
   {
      File.Delete( System.IO.Path.Combine( Path, relative ) );
   }

   /// <summary>
   /// Commits everything.
   /// </summary>
   /// <param name="message">Commit message.</param>
   /// <returns>The new commit id.</returns>
   public async Task<string> CommitAsync( string message )
   {
      await GitAsync( "add", "-A" );
      await GitAsync( "-c", "user.name=Test", "-c", "user.email=test@example.invalid", "commit", "--quiet", "-m", message );
      return await GitAsync( "rev-parse", "HEAD" );
   }

   #endregion Public Methods

   #region Private Methods

   /// <summary>
   /// Runs git in the repository with a 60 second limit, failing the test on a non-zero exit.
   /// </summary>
   /// <param name="arguments">Arguments.</param>
   /// <returns>Standard output, trimmed.</returns>
   private async Task<string> GitAsync( params string[] arguments )
   {
      var info = new ProcessStartInfo( "git" ) { WorkingDirectory = Path, RedirectStandardOutput = true, RedirectStandardError = true };
      foreach( string argument in arguments )
      {
         info.ArgumentList.Add( argument );
      }

      using Process process = Process.Start( info ) ?? throw new InvalidOperationException( "git did not start." );
      Task<string> output = process.StandardOutput.ReadToEndAsync();
      Task<string> error = process.StandardError.ReadToEndAsync();
      using var limit = new CancellationTokenSource( TimeSpan.FromSeconds( 60 ) );
      await process.WaitForExitAsync( limit.Token );
      Assert.True( process.ExitCode == 0, $"git {string.Join( ' ', arguments )} failed: {await error}" );
      return ( await output ).Trim();
   }

   #endregion Private Methods
}

/// <summary>
/// A "Git repo" run end to end through the real <see cref="RunWorker"/> and registry, with a
/// fake sink, a stub embedding service and a local repository: the files are fetched, embedded
/// with the commit recorded, a re-run embeds nothing, a removed file is deleted, and an address
/// that does not answer fails fast with a plain reason.
/// </summary>
public class GitRunWorkerTests : IDisposable
{
   #region Data Members

   private static readonly JsonSerializerOptions WEB_JSON = new( JsonSerializerDefaults.Web );

   private readonly TempFolder _root = new();
   private readonly MemorySink _sql = new( "sql" );
   private readonly RunRegistry _registry = new();
   private readonly GvbServices _services;
   private readonly GitWorkspace _git;
   private readonly RunWorker _worker;
   private readonly GitOriginRepo _origin;

   #endregion Data Members

   #region Constructor

   /// <summary>
   /// Starts a worker whose data folder holds the origin repository and whose repos folder is
   /// private to the test.
   /// </summary>
   public GitRunWorkerTests()
   {
      var settings = new GvbSettings
      {
         DataRoots = { Path.Combine( _root.Path, "data" ) },
         UploadRoot = Path.Combine( _root.Path, "uploads" ),
         ReposRoot = Path.Combine( _root.Path, "repos" ),
         GitReachTimeoutSeconds = 2,
      }.Normalize();
      _origin = new GitOriginRepo( Path.Combine( _root.Path, "data", "smallrepo" ) );
      var state = new SqliteStateStore( Path.Combine( _root.Path, "state.db" ) );
      _services = new GvbServices( settings, StubHandler.Client( new StubHandler( EmbedAnswer ) ), new ISink[] { _sql }, state );
      _git = new GitWorkspace( settings );
      _worker = new RunWorker( _registry, _services, NullLogger<RunWorker>.Instance, null, _git );
      _worker.StartAsync( CancellationToken.None ).GetAwaiter().GetResult();
   }

   #endregion Constructor

   #region Public Methods

   /// <summary>
   /// The first run reads the three C# files (not the README), counts them for the progress
   /// bar, splits them with the code chunker, stores the commit with every chunk and names it in
   /// the final message.
   /// </summary>
   [Fact]
   public async Task FirstRun_EmbedsEveryFile_AndRecordsTheCommit()
   {
      string commit = await _origin.InitAsync();

      RunSnapshot run = await RunAsync( GitRequest() );

      Assert.Equal( RunStatus.Completed, run.Status );
      Assert.Equal( 3, run.TotalRows );
      Assert.Equal( 3, run.RecordsRead );
      Assert.Equal( 3, run.DocsEmbedded );
      Assert.Equal( $"finished (git commit {commit})", run.Message );
      Assert.All( _sql.Records.Values, r => Assert.Equal( commit, r.Document.Metadata["commit"] ) );
      Assert.Contains( _sql.Records.Values, r => r.Document.DocKey == "code|src/Shop/Basket.cs" );
      Assert.DoesNotContain( _sql.Records.Values, r => r.Document.DocKey.EndsWith( "README.md", StringComparison.Ordinal ) );
      Assert.All( _sql.Records.Values, r => Assert.StartsWith( "// File: ", r.Chunk.Text ) );
   }

   /// <summary>
   /// A second run over the same commit embeds nothing.
   /// </summary>
   [Fact]
   public async Task Rerun_SameCommit_EmbedsNothing()
   {
      await _origin.InitAsync();
      await RunAsync( GitRequest() );

      RunSnapshot again = await RunAsync( GitRequest() );

      Assert.Equal( RunStatus.Completed, again.Status );
      Assert.Equal( 0, again.DocsEmbedded );
      Assert.Equal( 3, again.DocsUnchanged );
      Assert.Equal( 0, again.DocsDeleted );
   }

   /// <summary>
   /// A file removed in a new commit is deleted from the destination on the next run, and the
   /// other files are left alone.
   /// </summary>
   [Fact]
   public async Task RemovedFile_IsDeleted()
   {
      await _origin.InitAsync();
      await RunAsync( GitRequest() );
      _origin.Delete( "src/Shop/Order.cs" );
      string second = await _origin.CommitAsync( "remove order" );

      RunSnapshot run = await RunAsync( GitRequest() );

      Assert.Equal( RunStatus.Completed, run.Status );
      Assert.Equal( 1, run.DocsDeleted );
      Assert.Equal( 0, run.DocsEmbedded );
      Assert.DoesNotContain( _sql.Records.Values, r => r.Document.DocKey == "code|src/Shop/Order.cs" );
      Assert.Contains( _sql.Records.Values, r => r.Document.DocKey == "code|src/Shop/Basket.cs" );
      Assert.EndsWith( $"(git commit {second})", run.Message );
   }

   /// <summary>
   /// An address nothing listens on fails within seconds, with a message that names the address
   /// and says what git saw. Nothing was written.
   /// </summary>
   [Fact]
   public async Task UnreachableAddress_FailsFastWithAPlainMessage()
   {
      var clock = Stopwatch.StartNew();

      RunSnapshot run = await RunAsync( GitRequest( "https://127.0.0.1:1/owner/repo" ) );

      Assert.True( clock.Elapsed < TimeSpan.FromSeconds( 10 ), $"took {clock.Elapsed.TotalSeconds:0.0}s" );
      Assert.Equal( RunStatus.Failed, run.Status );
      Assert.StartsWith( "Could not read https://127.0.0.1:1/owner/repo: ", run.Message );
      Assert.Empty( _sql.Records );
   }

   /// <summary>
   /// An address that never answers (a black-hole address) gives up after the configured first
   /// contact limit instead of the network's own minutes-long wait.
   /// </summary>
   [Fact]
   public async Task SilentAddress_GivesUpAfterTheContactLimit()
   {
      var clock = Stopwatch.StartNew();

      RunSnapshot run = await RunAsync( GitRequest( "https://10.255.255.1/owner/repo" ) );

      Assert.True( clock.Elapsed < TimeSpan.FromSeconds( 15 ), $"took {clock.Elapsed.TotalSeconds:0.0}s" );
      Assert.Equal( RunStatus.Failed, run.Status );
      Assert.Contains( "https://10.255.255.1/owner/repo", run.Message );
   }

   /// <summary>
   /// While a run holds a repository's copy, a Fetch of the same repository is refused rather
   /// than changing the files under the run; the claim is released when the run ends.
   /// </summary>
   [Fact]
   public async Task RunHoldsTheCopy_UntilItEnds()
   {
      await _origin.InitAsync();
      string localPath = _git.Open( _origin.Path, null ).LocalPath;
      using( IDisposable? held = _git.TryClaim( localPath, "a test" ) )
      {
         Assert.NotNull( held );
         Assert.Null( _git.TryClaim( localPath, "a second test" ) );
         Assert.Equal( "a test", _git.HolderOf( localPath ) );
      }

      await RunAsync( GitRequest() );

      using IDisposable? after = _git.TryClaim( localPath, "after the run" );
      Assert.NotNull( after );
   }

   #endregion Public Methods

   #region Private Methods

   /// <summary>
   /// A git run request for the origin repository (or another address) into the fake sink.
   /// </summary>
   /// <param name="url">Address; the origin repository when null.</param>
   /// <returns>The request, as the endpoint would queue it.</returns>
   private RunRequest GitRequest( string? url = null )
   {
      return new RunRequest( "smallrepo", string.Empty, new[] { "sql" }, null, null, false, null, new GitRunRequest( url ?? _origin.Path, null, new[] { ".cs" } ) );
   }

   /// <summary>
   /// Queues a run and waits for the worker to finish it.
   /// </summary>
   /// <param name="request">The request.</param>
   /// <returns>The final snapshot.</returns>
   private async Task<RunSnapshot> RunAsync( RunRequest request )
   {
      RunJob job = _registry.Enqueue( request ).Job ?? throw new InvalidOperationException( "The run was not queued." );
      DateTime until = DateTime.UtcNow.AddSeconds( 60 );
      while( !job.Progress.IsFinished )
      {
         Assert.True( DateTime.UtcNow < until, "The run did not finish in 60 seconds." );
         await Task.Delay( 20 );
      }

      return job.Progress.Snapshot();
   }

   /// <summary>
   /// Answers /embed with one 4-dimension vector per input.
   /// </summary>
   /// <param name="request">The request.</param>
   /// <returns>The response.</returns>
   private static HttpResponseMessage EmbedAnswer( HttpRequestMessage request )
   {
      using JsonDocument body = JsonDocument.Parse( request.Content!.ReadAsStringAsync().GetAwaiter().GetResult() );
      int count = body.RootElement.GetProperty( "inputs" ).GetArrayLength();
      return StubHandler.Json( JsonSerializer.Serialize( Enumerable.Range( 0, count ).Select( _ => new[] { 0.1f, 0.2f, 0.3f, 0.4f } ), WEB_JSON ) );
   }

   #endregion Private Methods

   #region IDisposable

   /// <summary>
   /// Stops the worker and removes the temp folders.
   /// </summary>
   public void Dispose()
   {
      _worker.StopAsync( CancellationToken.None ).GetAwaiter().GetResult();
      _services.Dispose();
      _root.Dispose();
   }

   #endregion IDisposable
}

/// <summary>
/// The run endpoint's checks for a git request, called in process: exactly one source, an
/// acceptable address (a folder only inside the data folders), clean file types, and the
/// pipeline name defaulting to the repository's own name.
/// </summary>
public class GitStartRunTests : IDisposable
{
   #region Data Members

   private readonly TempFolder _root = new();
   private readonly GvbSettings _settings;
   private readonly GvbServices _services;
   private readonly RunRegistry _registry = new();
   private readonly GitWorkspace _git;
   private readonly OdbcCatalog _odbc;
   private readonly DestinationCatalog _destinations = new( new[] { new Destination( "sql", "SQL Server", false, true, null ) } );
   private readonly string _repo;

   #endregion Data Members

   #region Constructor

   /// <summary>
   /// Sets up the endpoint's dependencies over a fake sink; nothing is contacted.
   /// </summary>
   public GitStartRunTests()
   {
      _settings = new GvbSettings { DataRoots = { Path.Combine( _root.Path, "data" ) }, UploadRoot = Path.Combine( _root.Path, "uploads" ), ReposRoot = Path.Combine( _root.Path, "repos" ) }.Normalize();
      _repo = Path.Combine( _root.Path, "data", "eShopOnWeb" );
      Directory.CreateDirectory( _repo );
      _services = new GvbServices( _settings, StubHandler.Client( new StubHandler( _ => throw new InvalidOperationException( "not used" ) ) ), new ISink[] { new MemorySink( "sql" ) }, new SqliteStateStore( Path.Combine( _root.Path, "state.db" ) ) );
      _git = new GitWorkspace( _settings );
      _odbc = new OdbcCatalog( _settings );
   }

   #endregion Constructor

   #region Public Methods

   /// <summary>
   /// A request with no pipeline name is queued under the repository's name, lowercased, and
   /// with its file types cleaned and no folder.
   /// </summary>
   [Theory]
   [InlineData( "https://github.com/dotnet-architecture/eShopOnWeb", "eshoponweb" )]
   [InlineData( "https://github.com/dotnet-architecture/eShopOnWeb.git", "eshoponweb" )]
   [InlineData( "git@github.com:owner/My.Repo.git", "my_repo" )]
   [InlineData( "LOCAL", "eshoponweb" )]
   public async Task BlankPipeline_IsNamedAfterTheRepository( string url, string expected )
   {
      string address = url == "LOCAL" ? _repo : url;
      var request = new RunRequest( " ", string.Empty, new[] { "sql" }, null, null, false, null, new GitRunRequest( address, " ", new[] { "CS", ".ts", "cs" } ) );

      ( int status, JsonElement body ) = Read( await RunEndpoints.StartRunAsync( request, _registry, _services, _odbc, _git, _destinations, CancellationToken.None ) );

      Assert.Equal( StatusCodes.Status200OK, status );
      Assert.Equal( expected, body.GetProperty( "pipeline" ).GetString() );
      RunJob job = _registry.Find( body.GetProperty( "runId" ).GetString()! )!;
      Assert.Equal( string.Empty, job.Request.Path );
      Assert.Null( job.Request.Git!.Branch );
      Assert.Equal( new[] { ".cs", ".ts" }, job.Request.Git.Extensions );
      _registry.Cancel( job.Progress.RunId );
   }

   /// <summary>
   /// A typed pipeline name wins over the repository's name.
   /// </summary>
   [Fact]
   public async Task TypedPipeline_IsKept()
   {
      var request = new RunRequest( "My Shop", string.Empty, new[] { "sql" }, null, null, false, null, new GitRunRequest( "https://github.com/dotnet-architecture/eShopOnWeb", null, null ) );

      ( int status, JsonElement body ) = Read( await RunEndpoints.StartRunAsync( request, _registry, _services, _odbc, _git, _destinations, CancellationToken.None ) );

      Assert.Equal( StatusCodes.Status200OK, status );
      Assert.Equal( "my_shop", body.GetProperty( "pipeline" ).GetString() );
      RunJob job = _registry.Find( body.GetProperty( "runId" ).GetString()! )!;
      Assert.Equal( new[] { ".cs" }, job.Request.Git!.Extensions );
      _registry.Cancel( job.Progress.RunId );
   }

   /// <summary>
   /// Every bad git request is a 400 with a plain reason, and nothing is queued.
   /// </summary>
   [Theory]
   [InlineData( "folder-and-git", "Send only one of a folder, a database or a git repository." )]
   [InlineData( "odbc-and-git", "Send only one of a folder, a database or a git repository." )]
   [InlineData( "http", "Only https, ssh and local folders are supported, not 'http'." )]
   [InlineData( "empty", "Enter a repository address or a local folder." )]
   [InlineData( "password", "Do not put a password or token in the address; git would store it in plain text. Use a git credential helper." )]
   [InlineData( "outside", "That folder is outside the allowed data folders. A repository on this server must be inside one of them." )]
   [InlineData( "outside-missing", "That folder is outside the allowed data folders. A repository on this server must be inside one of them." )]
   [InlineData( "branch", "'-x' is not a valid branch name." )]
   [InlineData( "extension", "'.c#' is not a file type. Use something like .cs or .ts." )]
   [InlineData( "transport", "Git transport helpers (an address containing '::') are not allowed." )]
   public async Task BadGitRequest_IsA400WithAPlainReason( string kind, string expected )
   {
      var odbc = new OdbcRunRequest( "dsn", null, null, new[] { new OdbcSelection( "dbo", "T" ) } );
      RunRequest request = kind switch
      {
         "folder-and-git" => Git( "https://github.com/o/r" ) with { Path = _repo },
         "odbc-and-git" => Git( "https://github.com/o/r" ) with { Odbc = odbc },
         "http" => Git( "http://github.com/o/r" ),
         "empty" => Git( "  " ),
         "password" => Git( "https://me:token@github.com/o/r" ),
         "outside" => Git( "/etc" ),
         "outside-missing" => Git( "/no/such/place/anywhere" ),
         "branch" => Git( "https://github.com/o/r", branch: "-x" ),
         "extension" => Git( "https://github.com/o/r", extensions: new[] { "c#" } ),
         _ => Git( "ext::sh" ),
      };

      ( int status, JsonElement body ) = Read( await RunEndpoints.StartRunAsync( request, _registry, _services, _odbc, _git, _destinations, CancellationToken.None ) );

      Assert.Equal( StatusCodes.Status400BadRequest, status );
      Assert.Equal( expected, body.GetProperty( "error" ).GetString() );
      Assert.Empty( _registry.Recent() );
   }

   /// <summary>
   /// A request with no source at all names all three kinds.
   /// </summary>
   [Fact]
   public async Task NoSource_SaysWhatToPick()
   {
      var request = new RunRequest( "p", string.Empty, new[] { "sql" }, null, null );

      ( int status, JsonElement body ) = Read( await RunEndpoints.StartRunAsync( request, _registry, _services, _odbc, _git, _destinations, CancellationToken.None ) );

      Assert.Equal( StatusCodes.Status400BadRequest, status );
      Assert.Equal( "Pick a folder, a database or a git repository first.", body.GetProperty( "error" ).GetString() );
   }

   /// <summary>
   /// The new member is the last constructor parameter and optional, so every older caller
   /// still compiles and means a folder or database run; the page's JSON binds to it.
   /// </summary>
   [Fact]
   public void Git_IsTheLastMember_AndOptional_AndBindsFromJson()
   {
      System.Reflection.ParameterInfo last = typeof( RunRequest ).GetConstructors().Single( c => c.GetParameters().Length > 1 ).GetParameters().Last();
      RunRequest parsed = JsonSerializer.Deserialize<RunRequest>( "{\"pipeline\":\"\",\"sinks\":[\"sql\"],\"git\":{\"url\":\"https://github.com/o/r\",\"branch\":null,\"extensions\":[\".cs\"]}}", new JsonSerializerOptions( JsonSerializerDefaults.Web ) )!;

      Assert.Equal( "Git", last.Name );
      Assert.True( last.HasDefaultValue );
      Assert.Null( new RunRequest( "p", "/x", new[] { "sql" }, null, null ).Git );
      Assert.Equal( "https://github.com/o/r", parsed.Git!.Url );
      Assert.Equal( new[] { ".cs" }, parsed.Git.Extensions );
   }

   /// <summary>
   /// File types are cleaned the same way everywhere: dots added, lowercased, repeats dropped,
   /// C# when none are named.
   /// </summary>
   [Fact]
   public void CleanExtensions_AddsDots_AndDefaultsToCSharp()
   {
      Assert.Equal( new[] { ".cs" }, GitWorkspace.CleanExtensions( null ) );
      Assert.Equal( new[] { ".cs" }, GitWorkspace.CleanExtensions( new[] { " ", "" } ) );
      Assert.Equal( new[] { ".cs", ".razor", ".ts" }, GitWorkspace.CleanExtensions( new[] { "CS", ".razor", "ts", ".cs" } ) );
      Assert.Throws<ArgumentException>( () => GitWorkspace.CleanExtensions( Enumerable.Range( 0, 21 ).Select( i => $"x{i}" ).ToList() ) );
   }

   /// <summary>
   /// A Fetch is refused when the repos folder's disk is below the free-space floor, before git
   /// is started, and the message says where and how much.
   /// </summary>
   [Fact]
   public async Task Sync_TooLittleDiskSpace_IsRefusedBeforeGitRuns()
   {
      var tight = new GitWorkspace( _settings, long.MaxValue );
      var repo = tight.Open( "https://github.com/dotnet-architecture/eShopOnWeb", null );

      var ex = await Assert.ThrowsAsync<GitDiskSpaceException>( () => tight.SyncAsync( repo, null, CancellationToken.None ) );

      Assert.Contains( "GB is free where the repository copies live", ex.Message );
      Assert.False( Directory.Exists( repo.LocalPath ) );
      Assert.Equal( "failed", tight.StatusOf( repo.LocalPath )!.Text );
   }

   #endregion Public Methods

   #region Private Methods

   /// <summary>
   /// A git run request.
   /// </summary>
   /// <param name="url">Address.</param>
   /// <param name="branch">Branch.</param>
   /// <param name="extensions">File types.</param>
   /// <returns>The request.</returns>
   private static RunRequest Git( string url, string? branch = null, IReadOnlyList<string>? extensions = null )
   {
      return new RunRequest( "p", string.Empty, new[] { "sql" }, null, null, false, null, new GitRunRequest( url, branch, extensions ) );
   }

   /// <summary>
   /// Reads an endpoint result's status code and JSON body.
   /// </summary>
   /// <param name="result">The result.</param>
   /// <returns>Status code and body.</returns>
   internal static ( int Status, JsonElement Body ) Read( IResult result )
   {
      int status = ( result as IStatusCodeHttpResult )?.StatusCode ?? StatusCodes.Status200OK;
      object? value = ( result as IValueHttpResult )?.Value;
      return ( status, JsonDocument.Parse( JsonSerializer.Serialize( value ) ).RootElement.Clone() );
   }

   #endregion Private Methods

   #region IDisposable

   /// <summary>
   /// Removes the temp folders.
   /// </summary>
   public void Dispose()
   {
      _services.Dispose();
      _root.Dispose();
   }

   #endregion IDisposable
}

/// <summary>
/// The page's "Git repo" mode, checked as files: the third switch button, the address box with
/// the sample repository as its placeholder, the file types box, and the request Go builds.
/// </summary>
public class GitPageTests
{
   #region Public Methods

   /// <summary>
   /// Step 1 has a third switch button, "Git repo", starting unpressed, and a git panel that
   /// starts hidden.
   /// </summary>
   [Fact]
   public void StepOne_HasAGitRepoSwitch_AndAHiddenPanel()
   {
      string html = File.ReadAllText( PageFile( "index.html" ) );

      Match git = Regex.Match( html, "<button[^>]*id=\"mode-git\"[^>]*>([^<]*)</button>" );

      Assert.True( git.Success, "the Git repo button is missing" );
      Assert.Equal( "Git repo", git.Groups[1].Value );
      Assert.Contains( "aria-pressed=\"false\"", git.Value );
      Assert.Contains( "<div id=\"source-git\" class=\"hidden\">", html );
   }

   /// <summary>
   /// The address box shows the sample repository in grey, the branch box is optional, file
   /// types start as .cs, and there is a Fetch button and a status line.
   /// </summary>
   [Fact]
   public void GitPanel_HasTheAddressBranchTypesAndFetch()
   {
      string html = File.ReadAllText( PageFile( "index.html" ) );

      Match url = Regex.Match( html, "<input[^>]*id=\"git-url\"[^>]*>" );
      Match types = Regex.Match( html, "<input[^>]*id=\"git-extensions\"[^>]*>" );

      Assert.True( url.Success && types.Success, "the git boxes are missing" );
      Assert.Contains( "placeholder=\"https://github.com/dotnet-architecture/eShopOnWeb\"", url.Value );
      Assert.Contains( "value=\".cs\"", types.Value );
      Assert.Contains( "id=\"git-branch\"", html );
      Assert.Matches( "<button[^>]*id=\"git-fetch\"[^>]*>Fetch</button>", html );
      Assert.Contains( "id=\"git-status\"", html );
   }

   /// <summary>
   /// Fetch posts to the preview endpoint and polls the progress endpoint; Go sends the fetched
   /// repository under "git" and no folder; an empty address uses the placeholder.
   /// </summary>
   [Fact]
   public void Script_FetchesPollsAndSendsTheFetchedRepository()
   {
      string js = File.ReadAllText( PageFile( "app.js" ) );

      Assert.Contains( "postJson(\"/api/git/preview\", request)", js );
      Assert.Contains( "api(`/api/git/progress?${query}`)", js );
      Assert.Contains( "if (state.mode === \"git\") return { git: state.git.request };", js );
      Assert.Contains( "box.value = box.getAttribute(\"placeholder\")", js );
      Assert.Contains( "const MODES = [\"folder\", \"odbc\", \"git\"];", js );
   }

   #endregion Public Methods

   #region Private Methods

   /// <summary>
   /// Finds a file of the web project's static page by walking up to the solution folder.
   /// </summary>
   /// <param name="name">File name inside wwwroot.</param>
   /// <returns>The full path.</returns>
   private static string PageFile( string name )
   {
      string folder = AppContext.BaseDirectory;
      while( !File.Exists( Path.Combine( folder, "GenericVectorBuilder.slnx" ) ) )
      {
         folder = Path.GetDirectoryName( folder ) ?? throw new InvalidOperationException( "Could not find the solution folder." );
      }

      return Path.Combine( folder, "src", "GenericVectorBuilder.Web", "wwwroot", name );
   }

   #endregion Private Methods
}

/// <summary>
/// The git endpoints against the real host (stub embedder, dead SQL and Qdrant): Fetch of a local
/// repository describes it, an address nothing listens on fails fast with a plain message, a
/// folder outside the data folders is refused, the progress endpoint reports the finished fetch,
/// and a run started from the page's JSON fetches the repository and names the commit.
/// </summary>
[Trait( "Category", "WebE2E" )]
public class GitEndpointTests : IClassFixture<WebAppFixture>
{
   #region Data Members

   private readonly WebAppFixture _app;

   #endregion Data Members

   #region Constructor

   /// <summary>
   /// Receives the shared host.
   /// </summary>
   /// <param name="app">The running host.</param>
   public GitEndpointTests( WebAppFixture app )
   {
      _app = app;
   }

   #endregion Constructor

   #region Public Methods

   /// <summary>
   /// Fetch of a local repository returns the commit, the file count by folder, the first files,
   /// the skipped README and a chunk estimate, and offers the repository's name as the pipeline.
   /// </summary>
   [Fact]
   public async Task Preview_LocalRepository_DescribesWhatARunWouldRead()
   {
      ( GitOriginRepo origin, string commit ) = await NewOriginAsync( "preview_repo" );

      HttpResponseMessage response = await _app.PostWithOriginAsync( "api/git/preview", JsonSerializer.Serialize( new { url = origin.Path } ), null );

      Assert.Equal( HttpStatusCode.OK, response.StatusCode );
      using JsonDocument doc = JsonDocument.Parse( await response.Content.ReadAsStringAsync() );
      JsonElement p = doc.RootElement;
      Assert.Equal( commit, p.GetProperty( "commit" ).GetString() );
      Assert.Equal( 3, p.GetProperty( "fileCount" ).GetInt32() );
      Assert.Equal( "preview_repo", p.GetProperty( "suggestedPipeline" ).GetString() );
      Assert.Equal( ".cs", p.GetProperty( "extensions" )[0].GetString() );
      Assert.Equal( "src/Shop/Basket.cs", p.GetProperty( "firstFiles" )[0].GetString() );
      Assert.Equal( "src/Shop", p.GetProperty( "folders" )[0].GetProperty( "folder" ).GetString() );
      Assert.Equal( 2, p.GetProperty( "folders" )[0].GetProperty( "files" ).GetInt32() );
      Assert.True( p.GetProperty( "chunks" ).GetProperty( "chunks" ).GetInt64() >= 3 );
      Assert.False( p.GetProperty( "chunks" ).GetProperty( "extrapolated" ).GetBoolean() );
      Assert.Equal( 1, p.GetProperty( "skippedCount" ).GetInt32() );
      Assert.StartsWith( _app.ReposFolder, p.GetProperty( "localPath" ).GetString() );

      string progress = await _app.Http.GetStringAsync( $"api/git/progress?url={Uri.EscapeDataString( origin.Path )}" );
      using JsonDocument status = JsonDocument.Parse( progress );
      Assert.False( status.RootElement.GetProperty( "active" ).GetBoolean() );
      Assert.Equal( $"at commit {commit}", status.RootElement.GetProperty( "text" ).GetString() );
   }

   /// <summary>
   /// An address nothing listens on answers 502 within seconds, with a message that names it.
   /// </summary>
   [Fact]
   public async Task Preview_UnreachableAddress_FailsFastWithAPlainMessage()
   {
      var clock = Stopwatch.StartNew();

      HttpResponseMessage response = await _app.PostWithOriginAsync( "api/git/preview", JsonSerializer.Serialize( new { url = "https://127.0.0.1:1/owner/repo" } ), null );

      Assert.True( clock.Elapsed < TimeSpan.FromSeconds( 10 ), $"took {clock.Elapsed.TotalSeconds:0.0}s" );
      Assert.Equal( HttpStatusCode.BadGateway, response.StatusCode );
      Assert.StartsWith( "Could not read https://127.0.0.1:1/owner/repo: ", await ErrorOf( response ) );
   }

   /// <summary>
   /// A folder outside the data folders and a bad address are 400s with a reason, and a Fetch
   /// from another website is refused like every other POST.
   /// </summary>
   [Fact]
   public async Task Preview_BadRequests_AreRefused()
   {
      HttpResponseMessage outside = await _app.PostWithOriginAsync( "api/git/preview", JsonSerializer.Serialize( new { url = "/etc" } ), null );
      HttpResponseMessage http = await _app.PostWithOriginAsync( "api/git/preview", JsonSerializer.Serialize( new { url = "http://github.com/o/r" } ), null );
      HttpResponseMessage foreign = await _app.PostWithOriginAsync( "api/git/preview", JsonSerializer.Serialize( new { url = "https://github.com/o/r" } ), "http://evil.example" );

      Assert.Equal( HttpStatusCode.BadRequest, outside.StatusCode );
      Assert.Contains( "outside the allowed data folders", await ErrorOf( outside ) );
      Assert.Equal( HttpStatusCode.BadRequest, http.StatusCode );
      Assert.Contains( "not 'http'", await ErrorOf( http ) );
      Assert.Equal( HttpStatusCode.Forbidden, foreign.StatusCode );
   }

   /// <summary>
   /// A run sent the way the page sends it, with no pipeline name, is queued under the
   /// repository's name, fetches the repository and names the commit in its final message (it
   /// then fails, because this host's destinations are dead on purpose).
   /// </summary>
   [Fact]
   public async Task Run_FromThePagesJson_FetchesAndNamesTheCommit()
   {
      ( GitOriginRepo origin, string commit ) = await NewOriginAsync( "run_repo" );
      string json = JsonSerializer.Serialize( new { pipeline = "", sinks = new[] { "sql" }, git = new { url = origin.Path, branch = (string?)null, extensions = new[] { ".cs" } } } );

      HttpResponseMessage response = await _app.PostWithOriginAsync( "api/runs", json, null );

      Assert.Equal( HttpStatusCode.OK, response.StatusCode );
      using JsonDocument queued = JsonDocument.Parse( await response.Content.ReadAsStringAsync() );
      Assert.Equal( "run_repo", queued.RootElement.GetProperty( "pipeline" ).GetString() );
      string runId = queued.RootElement.GetProperty( "runId" ).GetString()!;
      JsonElement last = await WaitUntilFinishedAsync( runId );
      Assert.EndsWith( $"(git commit {commit})", last.GetProperty( "message" ).GetString() );
      Assert.Equal( 3, last.GetProperty( "totalRows" ).GetInt64() );
   }

   #endregion Public Methods

   #region Private Methods

   /// <summary>
   /// Makes a repository inside the host's git data folder.
   /// </summary>
   /// <param name="name">Folder name.</param>
   /// <returns>The repository and its commit.</returns>
   private async Task<( GitOriginRepo Origin, string Commit )> NewOriginAsync( string name )
   {
      var origin = new GitOriginRepo( Path.Combine( _app.GitDataFolder, name ) );
      return ( origin, await origin.InitAsync() );
   }

   /// <summary>
   /// Waits up to 60 seconds for a run to finish.
   /// </summary>
   /// <param name="runId">Run id.</param>
   /// <returns>The final snapshot.</returns>
   private async Task<JsonElement> WaitUntilFinishedAsync( string runId )
   {
      DateTime until = DateTime.UtcNow.AddSeconds( 60 );
      while( DateTime.UtcNow < until )
      {
         JsonElement snapshot = await _app.SnapshotAsync( runId );
         if( snapshot.GetProperty( "finishedUtc" ).ValueKind != JsonValueKind.Null )
         {
            return snapshot;
         }

         await Task.Delay( 100 );
      }

      throw new TimeoutException( $"Run {runId} did not finish.\n{_app.Log}" );
   }

   /// <summary>
   /// Reads the "error" text of a JSON error response ("" when there is none).
   /// </summary>
   /// <param name="response">The response.</param>
   /// <returns>The error text.</returns>
   private static async Task<string> ErrorOf( HttpResponseMessage response )
   {
      string text = await response.Content.ReadAsStringAsync();
      try
      {
         using JsonDocument doc = JsonDocument.Parse( text );
         return doc.RootElement.TryGetProperty( "error", out JsonElement error ) ? error.GetString() ?? string.Empty : string.Empty;
      }
      catch( JsonException )
      {
         return string.Empty;
      }
   }

   #endregion Private Methods
}
