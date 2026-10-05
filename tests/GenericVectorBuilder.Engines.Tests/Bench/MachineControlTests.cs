using System.Reflection;
using System.Runtime.Loader;
using System.Text.Json;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;

namespace GenericVectorBuilder.Engines.Tests.Bench;

/// <summary>
/// Machine control of the benchmark, checked against the real code on a fake machine: the CPU
/// split from lscpu, the state file, putting the machine back after a crash (and refusing while
/// another run owns it), recording each change before making it, how each engine kind is
/// pinned, the busy-box wait, the sampler's arithmetic, and the flags written with the results.
/// Why these get tests: each decides whether the box is left changed after a run, or whether a
/// number was measured under the conditions the results claim.
/// </summary>
public class MachineControlTests
{
   #region Public Methods

   /// <summary>
   /// On linus7795 (siblings 0/4, 1/5, 2/6, 3/7) the engine gets cores 2 and 3 with both
   /// threads, the client cores 0 and 1, and SQL Server's ids for the engine CPUs are 4-7
   /// (its numbering puts siblings next to each other).
   /// </summary>
   [Fact]
   public void Partition_ThisBox()
   {
      dynamic p = MachineControlCompiler.Call( "Partition", MachineControlCompiler.LinusLscpu() );
      Assert.Equal( "2-3,6-7", (string)p.Engine );
      Assert.Equal( "0-1,4-5", (string)p.Client );
      Assert.Equal( new[] { "0,4", "1,5", "2,6", "3,7" }, (string[])p.Siblings );
      Assert.Null( (string?)p.Problem );
      Assert.Equal( "4-7", (string)p.SqlEngineIds );
      Assert.Contains( "cores 2,6 and 3,7", (string)p.Description );
   }

   /// <summary>
   /// Other layouts: no hyperthreads, an offline CPU, siblings numbered next to each other, two
   /// packages, and a single core (not split, with the reason).
   /// </summary>
   [Fact]
   public void Partition_OtherLayouts()
   {
      dynamic noSmt = MachineControlCompiler.Call( "Partition", "CPU SOCKET CORE ONLINE\n0 0 0 yes\n1 0 1 yes\n2 0 2 yes\n3 0 3 yes\n" );
      Assert.Equal( "2-3", (string)noSmt.Engine );
      Assert.Equal( "0-1", (string)noSmt.Client );
      dynamic offline = MachineControlCompiler.Call( "Partition", "CPU SOCKET CORE ONLINE\n0 0 0 yes\n1 0 0 yes\n2 0 1 yes\n3 0 1 yes\n4 - - no\n5 0 2 yes\n" );
      Assert.Equal( "2-3,5", (string)offline.Engine );
      Assert.Equal( "0-1", (string)offline.Client );
      dynamic sockets = MachineControlCompiler.Call( "Partition", "CPU SOCKET CORE\n0 0 0\n1 1 4\n2 0 1\n3 1 5\n" );
      Assert.Equal( "1,3", (string)sockets.Engine );
      Assert.Equal( "0,2", (string)sockets.Client );
      dynamic single = MachineControlCompiler.Call( "Partition", "CPU CORE\n0 0\n1 0\n" );
      Assert.Contains( "only one physical core", (string)single.Problem );
      Assert.Equal( string.Empty, (string)single.Engine );
   }

   /// <summary>
   /// CPU lists compare in one canonical form.
   /// </summary>
   [Fact]
   public void CpuList_Canonical()
   {
      Assert.Equal( "2-3,6-7", (string)MachineControlCompiler.Call( "CanonicalCpus", "2,6,3,7" ) );
      Assert.Equal( "0-7", (string)MachineControlCompiler.Call( "CanonicalCpus", " 0-3, 4,5-7 " ) );
      Assert.Equal( "1,3,5", (string)MachineControlCompiler.Call( "CanonicalCpus", "5,3,1,3" ) );
      var ex = Assert.Throws<TargetInvocationException>( () => MachineControlCompiler.Call( "CanonicalCpus", "0-x" ) );
      Assert.IsType<FormatException>( ex.InnerException );
   }

   /// <summary>
   /// --no-machine-control and --machine-state are read; restore-machine needs no pipeline.
   /// </summary>
   [Fact]
   public void Options_MachineSwitches()
   {
      dynamic on = MachineControlCompiler.Call( "Options", (object)new[] { "bench", "--pipeline", "p", "--targets", "sql" } );
      Assert.True( (bool)on.MachineControl );
      Assert.Equal( "/home/dan/gvb-work/bench-machine-state.json", (string)on.StateFile );
      dynamic off = MachineControlCompiler.Call( "Options", (object)new[] { "run-all", "--pipeline", "p", "--no-machine-control", "--machine-state", "/x/s.json" } );
      Assert.False( (bool)off.MachineControl );
      Assert.Equal( "/x/s.json", (string)off.StateFile );
      dynamic restore = MachineControlCompiler.Call( "Options", (object)new[] { "restore-machine" } );
      Assert.Equal( "restore-machine", (string)restore.Command );
   }

   /// <summary>
   /// The state file keeps every field through a save and a load, leaves no temporary file,
   /// loads as nothing when missing, and a broken file is an error naming the file.
   /// </summary>
   [Fact]
   public void StateFile_RoundTrip()
   {
      dynamic r = MachineControlCompiler.InFolder( folder => MachineControlCompiler.Call( "StateRoundTrip", folder ) );
      Assert.True( (bool)r.Same );
      Assert.False( (bool)r.TmpLeft );
      Assert.True( (bool)r.MissingIsNull );
      Assert.Contains( (string)r.Path, (string)r.CorruptMessage );
      Assert.Contains( "not readable", (string)r.CorruptMessage );
      Assert.Contains( "SQL Server affinity (was AUTO)", (string)r.Describe );
   }

   /// <summary>
   /// After a crash the next start puts back every recorded change (governor, container cpuset,
   /// SQL Server affinity, Qdrant affinity) and removes the state file. A container that had no
   /// cpuset gets every CPU, since docker cannot clear the field.
   /// </summary>
   [Fact]
   public async Task Restore_AfterCrash_PutsEverythingBack()
   {
      dynamic r = await MachineControlCompiler.InFolderAsync( folder => MachineControlCompiler.CallAsync( "RestoreAfterCrashAsync", folder, "dead-owner" ) );
      string[] calls = r.Calls;
      Assert.Null( (string?)r.Error );
      Assert.False( (bool)r.FileLeft );
      Assert.All( (string[])r.Governors, g => Assert.Equal( "schedutil", g ) );
      Assert.Contains( "sudo -n docker update --cpuset-cpus 0-7 c0ffee", calls );
      Assert.Equal( "0-7", (string)r.ContainerCpuset );
      Assert.Contains( "SQL ALTER SERVER CONFIGURATION SET PROCESS AFFINITY CPU = AUTO;", calls );
      Assert.Equal( "AUTO", (string)r.SqlType );
      Assert.Contains( "sudo -n taskset -a -c -p 0-7 1688", calls );
      Assert.Equal( "0-7 x3", (string)r.QdrantThreads );
   }

   /// <summary>
   /// A SQL Server that had a manual affinity before gets exactly those CPUs back, written as a
   /// plain list (SQL Server rejects "0-3").
   /// </summary>
   [Fact]
   public async Task Restore_SqlManualAffinity()
   {
      dynamic r = await MachineControlCompiler.InFolderAsync( folder => MachineControlCompiler.CallAsync( "RestoreAfterCrashAsync", folder, "sql-manual" ) );
      Assert.Null( (string?)r.Error );
      Assert.Contains( "SQL ALTER SERVER CONFIGURATION SET PROCESS AFFINITY CPU = 0,1,2,3;", (string[])r.Calls );
      Assert.Equal( "MANUAL", (string)r.SqlType );
      Assert.False( (bool)r.FileLeft );
   }

   /// <summary>
   /// While the run that owns the state file is alive, a new start refuses and touches nothing.
   /// </summary>
   [Fact]
   public async Task Restore_RefusesWhileOwnerAlive()
   {
      dynamic r = await MachineControlCompiler.InFolderAsync( folder => MachineControlCompiler.CallAsync( "RestoreAfterCrashAsync", folder, "live-owner" ) );
      Assert.Contains( "Another benchmark run (pid 777", (string)r.Error );
      Assert.True( (bool)r.FileLeft );
      Assert.Empty( (string[])r.Calls );
      Assert.All( (string[])r.Governors, g => Assert.Equal( "performance", g ) );
   }

   /// <summary>
   /// A record that cannot be put back stays in the state file (and the start fails loudly);
   /// everything else is put back and removed from the file.
   /// </summary>
   [Fact]
   public async Task Restore_FailureKeepsOnlyTheFailedRecord()
   {
      dynamic r = await MachineControlCompiler.InFolderAsync( folder => MachineControlCompiler.CallAsync( "RestoreAfterCrashAsync", folder, "docker-fails" ) );
      Assert.Contains( "Could not put the machine back", (string)r.Error );
      Assert.Contains( "permission denied", (string)r.Error );
      Assert.True( (bool)r.FileLeft );
      using JsonDocument left = JsonDocument.Parse( (string)r.FileText );
      Assert.Equal( 1, left.RootElement.GetProperty( "containers" ).GetArrayLength() );
      Assert.Empty( left.RootElement.GetProperty( "governors" ).EnumerateObject() );
      Assert.Equal( JsonValueKind.Null, left.RootElement.GetProperty( "sqlServer" ).ValueKind );
      Assert.Equal( 0, left.RootElement.GetProperty( "processes" ).GetArrayLength() );
      Assert.All( (string[])r.Governors, g => Assert.Equal( "schedutil", g ) );
   }

   /// <summary>
   /// A container that was removed and a Qdrant that was restarted since the crash count as put
   /// back: nothing is sent to them, and the state file is removed.
   /// </summary>
   [Fact]
   public async Task Restore_GoneThingsNeedNothing()
   {
      dynamic r = await MachineControlCompiler.InFolderAsync( folder => MachineControlCompiler.CallAsync( "RestoreAfterCrashAsync", folder, "gone" ) );
      string[] calls = r.Calls;
      Assert.Null( (string?)r.Error );
      Assert.False( (bool)r.FileLeft );
      Assert.DoesNotContain( calls, c => c.Contains( "docker update", StringComparison.Ordinal ) );
      Assert.DoesNotContain( calls, c => c.Contains( "taskset", StringComparison.Ordinal ) );
   }

   /// <summary>
   /// A whole run on the fake machine: governors are in the state file before the first write,
   /// SQL Server gets SQL ids 4-7 (kernel 2-3,6-7), Qdrant every thread on 2-3,6-7, the redis
   /// container cpuset 2-3,6-7, the client 0-1,4-5, the embedded engine nothing; each target's
   /// pins are put back after it, the governors at the end, and the state file is removed. A
   /// container moved with docker update after its start says it sized its pools for every CPU.
   /// </summary>
   [Fact]
   public async Task Run_PinsEachEngineAndPutsItBack()
   {
      dynamic r = await MachineControlCompiler.InFolderAsync( folder => MachineControlCompiler.CallAsync( "RunAsync", folder, false ) );
      string[] calls = r.Calls;
      string[] state = r.StateAtCall;
      string firstWrite = state.First( s => s.Contains( "sh -c", StringComparison.Ordinal ) );
      Assert.EndsWith( "=> schedutil,schedutil,schedutil,schedutil,schedutil,schedutil,schedutil,schedutil", firstWrite );
      Assert.All( (string[])r.GovernorsDuring, g => Assert.Equal( "performance", g ) );
      Assert.All( (string[])r.GovernorsAfter, g => Assert.Equal( "schedutil", g ) );
      Assert.True( (bool)r.FileDuring );
      Assert.False( (bool)r.FileAfter );
      AssertInOrder( calls, "taskset -a -c -p 0-1,4-5 4242", "SQL ALTER SERVER CONFIGURATION SET PROCESS AFFINITY CPU = 4,5,6,7;", "SQL ALTER SERVER CONFIGURATION SET PROCESS AFFINITY CPU = AUTO;",
         "sudo -n taskset -a -c -p 2-3,6-7 1688", "sudo -n taskset -a -c -p 0-7 1688", "sudo -n docker update --cpuset-cpus 2-3,6-7 beef01", "sudo -n docker update --cpuset-cpus 0-7 beef01", "taskset -a -c -p 0-7 4242" );
      string[] snapshots = r.Snapshots;
      Assert.Contains( "sql MANUAL 4-7; sqlservr 0-7 x3;2-3,6-7 x1", snapshots[0] );
      Assert.Contains( "qdrant 2-3,6-7 x3", snapshots[1] );
      Assert.Contains( "gvb-redis=2-3,6-7", snapshots[2] );
      Assert.All( snapshots, s => Assert.Contains( "client 0-1,4-5 x3", s ) );
      Assert.Contains( "sql AUTO 0-7", snapshots[3] );
      using JsonDocument json = JsonDocument.Parse( (string)r.ConditionsJson );
      JsonElement c = json.RootElement;
      Assert.Equal( "2-3,6-7", c.GetProperty( "engineCpus" ).GetString() );
      Assert.Equal( "0-1,4-5", c.GetProperty( "clientCpus" ).GetString() );
      Assert.Equal( "performance", c.GetProperty( "governor" ).GetString() );
      Assert.Equal( "performance", c.GetProperty( "governorAtEnd" ).GetString() );
      Assert.Equal( "schedutil", c.GetProperty( "governorAfterRestore" ).GetString() );
      Assert.Equal( 7, c.GetProperty( "warmupSearches" ).GetInt32() );
      Assert.Equal( 9, c.GetProperty( "exactSeconds" ).GetInt32() );
      Assert.Equal( 4, c.GetProperty( "passes" ).GetArrayLength() );
      Assert.All( c.GetProperty( "engines" ).EnumerateArray(), e => Assert.Empty( e.GetProperty( "problems" ).EnumerateArray() ) );
      Assert.Contains( "container gvb-redis: cpuset '' -> 2-3,6-7 with docker update after it had started, so it sized its thread pools for every CPU",
         c.GetProperty( "engines" )[2].GetProperty( "changes" ).EnumerateArray().Select( e => e.GetString() ) );
      Assert.Contains( "embedded", c.GetProperty( "engines" )[3].GetProperty( "method" ).GetString() );
      Assert.Equal( "0-1,4-5", c.GetProperty( "engines" )[3].GetProperty( "cpus" ).GetString() );
      Assert.Contains( "governor back to schedutil on CPUs 0-7", c.GetProperty( "restored" ).EnumerateArray().Select( e => e.GetString() ) );
      Assert.Contains( (string[])r.RunNotes, n => n.Contains( "no timed pass of never-seen", StringComparison.Ordinal ) );
      Assert.Contains( (string[])r.TargetNotes, n => n.StartsWith( "CPU pinning: ALTER SERVER CONFIGURATION", StringComparison.Ordinal ) );
      Assert.Contains( (string[])r.TargetNotes, n => n.StartsWith( "Clock per pass", StringComparison.Ordinal ) && n.Contains( "default@1 3491/3491 MHz, performance", StringComparison.Ordinal ) );
   }

   /// <summary>
   /// If SQL Server's numbering did not map as expected (threads bound to the wrong kernel
   /// CPUs), the target says so as a warning instead of claiming it was pinned.
   /// </summary>
   [Fact]
   public async Task Run_WrongSqlMappingIsFlagged()
   {
      dynamic r = await MachineControlCompiler.InFolderAsync( folder => MachineControlCompiler.CallAsync( "RunAsync", folder, true ) );
      Assert.Contains( (string[])r.RunNotes, n => n.StartsWith( "WARNING: sql not fully pinned", StringComparison.Ordinal ) && n.Contains( "4-7", StringComparison.Ordinal ) );
   }

   /// <summary>
   /// When SQL Server rejects the pin, the target carries the problem, the state still holds the
   /// record (putting AUTO back is harmless), and SQL Server's CPU time still counts as the
   /// engine's, not as outside work that would hold its passes as a busy box.
   /// </summary>
   [Fact]
   public async Task FailedPin_StillCountsEngineCpu()
   {
      dynamic r = await MachineControlCompiler.InFolderAsync( folder => MachineControlCompiler.CallAsync( "GroupsWhenPinFailsAsync", folder ) );
      string[][] parts = r;
      Assert.Equal( new[] { "/sys/fs/cgroup/system.slice/mssql-server.service" }, parts[0] );
      Assert.Contains( "Incorrect syntax", parts[1].Single() );
      Assert.Equal( "AUTO", parts[2][0] );
   }

   /// <summary>
   /// The quiet check is made at the warm-up line: a warm-up announced while 3 CPUs of outside
   /// work run waits its limit and its pass is flagged busy (although the box went quiet before
   /// the timing line); a warm-up on a quiet box goes on at once, and work that starts between
   /// the warm-up and the timing line does not hold the pass a second time; a pass with no
   /// warm-up line is checked at its timing line instead. Each pass ends at its last result
   /// line, and one with no result line says so.
   /// </summary>
   [Fact]
   public async Task BusyBox_WaitsBeforeTheWarmupThenFlags()
   {
      dynamic r = await MachineControlCompiler.InFolderAsync( folder => MachineControlCompiler.CallAsync( "GateAsync", folder ) );
      string[] passes = r.Passes;
      Assert.Equal( 3, passes.Length );
      Assert.StartsWith( "t default@8 busy=True waited=0.", passes[0] );
      Assert.InRange( Waited( passes[0] ), 0.4, 0.7 );
      Assert.Contains( "by=result line before=warm-up", passes[0] );
      Assert.StartsWith( "t exact busy=False waited=0.0 by=result line before=warm-up load=0.00", passes[1] );
      Assert.StartsWith( "t default@1 busy=True waited=0.", passes[2] );
      Assert.Contains( "by=end of the target (no result line seen) before=timed pass (no warm-up line seen)", passes[2] );
      Assert.Contains( "deadline", ( (string[])r.Reasons )[0] );
      string[] log = r.Log;
      Assert.Contains( log, l => l.Contains( "box busy before t default@8 (warm-up)", StringComparison.Ordinal ) );
      Assert.Contains( log, l => l.Contains( "BUSY BOX", StringComparison.Ordinal ) );
      Assert.DoesNotContain( log, l => l.Contains( "box busy before t exact", StringComparison.Ordinal ) );
   }

   /// <summary>
   /// The deadline is per pass: a warm-up announced twice for the same pass on a busy box waits
   /// the 0.6 s deadline once in all (not 1.2 s), and the pass is flagged.
   /// </summary>
   [Fact]
   public async Task BusyBox_DeadlineIsPerPass()
   {
      dynamic r = await MachineControlCompiler.InFolderAsync( folder => MachineControlCompiler.CallAsync( "RecheckAsync", folder ) );
      string[] passes = r.Passes;
      Assert.Single( passes );
      Assert.StartsWith( "default@1 busy=True waited=0.6", passes[0] );
      Assert.InRange( (double)r.Seconds, 0.55, 1.1 );
      Assert.Equal( 2, ( (string[])r.Log ).Count( l => l.Contains( "box busy before t default@1 (warm-up)", StringComparison.Ordinal ) ) );
   }

   /// <summary>
   /// The defaults: 0.3 CPUs of outside work at most, a 10-minute deadline per pass, a 60 s
   /// window and a 5 s window; the rule text says the check is before the warm-up.
   /// </summary>
   [Fact]
   public void BusyBox_Defaults()
   {
      string[] d = (string[])MachineControlCompiler.Call( "BusyDefaults" );
      Assert.Equal( new[] { "0.3", "600", "60", "5" }, d.Take( 4 ) );
      Assert.Contains( "right before each pass's warm-up", d[4] );
      Assert.Contains( "up to 10 min", d[4] );
      Assert.Contains( "0.3 CPUs", d[4] );
   }

   /// <summary>
   /// An engine's own start-up is not outside work: the warm-up after run-all started a compose
   /// engine (3 CPUs of start-up burn before its cgroup was known) goes on at once, while the
   /// same burn from outside the benchmark holds the next target's warm-up and flags its pass.
   /// The start is recorded in the state file before it happens; the container created on the
   /// engine CPUs needs no docker update; leaving the target gives a container still on them
   /// every CPU back, and nothing is left in the state file.
   /// </summary>
   [Fact]
   public async Task BusyBox_EngineStartupIsNotOutsideWork()
   {
      dynamic r = await MachineControlCompiler.InFolderAsync( folder => MachineControlCompiler.CallAsync( "StartupAsync", folder ) );
      string[] passes = r.Passes;
      Assert.Equal( 2, passes.Length );
      Assert.StartsWith( "es busy=False waited=0.0 load=0.00", passes[0] );
      Assert.StartsWith( "other busy=True", passes[1] );
      Assert.Equal( "/x/es.compose.yaml 2-3,6-7 es", (string)r.StateDuringUp );
      string[] calls = r.Calls;
      Assert.DoesNotContain( "sudo -n docker update --cpuset-cpus 2-3,6-7 e5e5", calls );
      Assert.Contains( "sudo -n docker update --cpuset-cpus 0-7 e5e5", calls );
      Assert.Contains( "sudo -n docker compose -f /x/es.compose.yaml ps -q --status running --orphans=false", calls );
      Assert.Contains( "sudo -n docker compose -f /x/es.compose.yaml ps -a -q --orphans=false", calls );
      Assert.DoesNotContain( calls, c => c.Contains( " ps " ) && !c.Contains( "--orphans=false" ) );
      Assert.StartsWith( "cpuset from the container's creation when run-all started it on the engine CPUs", (string)r.Method );
      Assert.Contains( "container gvb-elasticsearch: already on CPUs 2-3,6-7 (its cpuset since creation), not changed", (string[])r.Changes );
      Assert.Contains( (string[])r.Restored, l => l.Contains( "e5e5 of es.compose.yaml were still on CPUs 2-3,6-7", StringComparison.Ordinal ) );
      Assert.False( (bool)r.FileAfter );
   }

   /// <summary>
   /// A run that died after a pinned start leaves the containers on the engine CPUs; the next
   /// start gives the one still on exactly those CPUs every CPU back and leaves alone the one
   /// someone has changed since.
   /// </summary>
   [Fact]
   public async Task Restore_PinnedStartAfterCrash()
   {
      dynamic r = await MachineControlCompiler.InFolderAsync( folder => MachineControlCompiler.CallAsync( "RestoreAfterCrashAsync", folder, "pinned-start" ) );
      Assert.Null( (string?)r.Error );
      Assert.False( (bool)r.FileLeft );
      Assert.Equal( "0-7|1", (string)r.PinnedStartCpusets );
      Assert.Contains( "sudo -n docker compose -f /x/es.compose.yaml ps -a -q --orphans=false", (string[])r.Calls );
      Assert.DoesNotContain( "sudo -n docker update --cpuset-cpus 0-7 d0d0", (string[])r.Calls );
   }

   /// <summary>
   /// The CPU idle settings are read at the start and the end, with machine control on or off,
   /// never written; a change made under the run is flagged.
   /// </summary>
   [Theory]
   [InlineData( true )]
   [InlineData( false )]
   public async Task Idle_RecordedNeverChanged( bool on )
   {
      dynamic r = await MachineControlCompiler.InFolderAsync( folder => MachineControlCompiler.CallAsync( "IdleAsync", folder, on ) );
      Assert.Equal( "driver intel_idle, governor menu, intel_idle max_cstate 9; POLL on, C1 on, C1E on, C3 OFF on CPUs 0-7 (default disabled), C6 on", (string)r.Start );
      Assert.Contains( "C6 OFF on CPUs 5 (default enabled)", (string)r.End );
      Assert.DoesNotContain( (string[])r.Calls, c => c.Contains( "cpuidle", StringComparison.Ordinal ) || c.Contains( "max_cstate", StringComparison.Ordinal ) );
      using JsonDocument json = JsonDocument.Parse( (string)r.ConditionsJson );
      JsonElement idle = json.RootElement.GetProperty( "cpuIdle" );
      Assert.Equal( 5, idle.GetProperty( "states" ).GetArrayLength() );
      Assert.Equal( "0-7", idle.GetProperty( "states" )[3].GetProperty( "disabledOn" ).GetString() );
      Assert.Equal( 133, idle.GetProperty( "states" )[4].GetProperty( "latencyUs" ).GetInt32() );
      Assert.Contains( (string[])r.RunNotes, n => n.StartsWith( "WARNING: the CPU idle settings changed during the run", StringComparison.Ordinal ) );
      Assert.Contains( (string[])r.RunNotes, n => n.Contains( "C3 OFF on CPUs 0-7", StringComparison.Ordinal ) );
   }

   /// <summary>
   /// Connections: /proc/net addresses decode (IPv4, IPv4-mapped IPv6, IPv6 loopback); only this
   /// process's established sockets count; each target's record names its route and endpoints
   /// and classifies what was open (its own endpoint, the docker-proxy port, something else); a
   /// connection through docker-proxy is flagged; SQL data sources split into host and port.
   /// </summary>
   [Fact]
   public async Task Connections_RecordedAndChecked()
   {
      dynamic r = await MachineControlCompiler.CallAsync( "ConnectionsAsync" );
      Assert.Equal( new[] { "127.0.0.1:1433", "127.0.0.1:6334", "172.22.0.2:9200", "::1:8080" }, (string[])r.Decoded );
      Assert.Equal( new[] { "127.0.0.1:1433 x1", "127.0.0.1:9200 x1", "172.22.0.2:9200 x2" }, (string[])r.Peers );
      string[] notes = r.Notes;
      Assert.StartsWith( "Connection: container address on its Docker network, not the published port (no docker-proxy): container gvb-elasticsearch (e5e5e5e5e5e5) on network engines_default at 172.22.0.2:9200, not through docker-proxy 127.0.0.1:9200.", notes[0] );
      Assert.Contains( "172.22.0.2:9200 x2 (this target)", notes[0] );
      Assert.Contains( "127.0.0.1:9200 x1 (docker-proxy (published port))", notes[0] );
      Assert.Contains( "127.0.0.1:1433 x1 (not this target)", notes[0] );
      Assert.StartsWith( "Connection: native service on this host, reached directly (no container, no docker-proxy): SQL Server at localhost:1433.", notes[1] );
      Assert.Contains( "127.0.0.1:1433 x1 (this target)", notes[1] );
      Assert.Equal( "Connection: in this process (embedded), no network: no network address. Open connections not read (not searched).", notes[2] );
      using JsonDocument json = JsonDocument.Parse( (string)r.Json );
      JsonElement es = json.RootElement[0];
      Assert.Equal( 9200, es.GetProperty( "endpoints" )[0].GetProperty( "port" ).GetInt32() );
      Assert.Equal( "127.0.0.1:9200", es.GetProperty( "endpoints" )[0].GetProperty( "notUsed" ).GetString() );
      Assert.Contains( (string[])r.Flags, f => f.StartsWith( "WARNING: elasticsearch had 1 connection(s) open to its published port 127.0.0.1:9200", StringComparison.Ordinal ) );
      Assert.Equal( "(db1, 1500)", (string)r.SqlWithPort );
      Assert.Equal( "(db2, )", (string)r.SqlNamed );
   }

   /// <summary>
   /// Outside CPU = all busy time minus this process minus the engine's cgroup (3 - 0.5 - 1.5 =
   /// 1.0 CPU); per-CPU min/median/max over a window; a window shorter than one sample takes
   /// the nearest sample.
   /// </summary>
   [Fact]
   public void Sampler_Arithmetic()
   {
      dynamic r = MachineControlCompiler.Call( "SamplerMath" );
      Assert.Null( (string?)r.Error );
      Assert.Equal( 1.0, (double)r.Outside, 6 );
      Assert.Equal( new[] { "cpu0 3400/3475/3600", "cpu1 1200/1275/1400" }, (string[])r.Cpus );
      Assert.Equal( 4, (int)r.Samples );
      Assert.Equal( 0, (int)r.ShortSamples );
      Assert.True( (bool)r.Nearest );
   }

   /// <summary>
   /// A clean run has no warning; a bad one names every problem; machine control off says so.
   /// </summary>
   [Fact]
   public void Flags_NameEveryProblem()
   {
      Assert.DoesNotContain( (string[])MachineControlCompiler.Call( "Flags", "clean" ), f => f.StartsWith( "WARNING", StringComparison.Ordinal ) );
      string[] bad = (string[])MachineControlCompiler.Call( "Flags", "bad" );
      string all = string.Join( "\n", bad );
      foreach( string expected in new[] { "governor was performance during the run and mixed", "governor schedutil during a default@8", "Debug build of the client with the JIT optimiser off",
         "CPUs not split", "b not fully pinned", "duckdb is embedded", "busy box during a default@8", "no timed pass of b was seen", "nearest their middle: a default@8",
         "CPU sampling failed", "NOT fully put back", "restore-machine" } )
      {
         Assert.Contains( expected, all );
      }

      Assert.Contains( (string[])MachineControlCompiler.Call( "Flags", "off" ), f => f.StartsWith( "WARNING: machine control off (--no-machine-control): the governor was schedutil", StringComparison.Ordinal ) );
   }

   /// <summary>
   /// The conditions land in results.json under "conditions" with the names the consolidation
   /// reads, and the rest of the file is kept.
   /// </summary>
   [Fact]
   public void Conditions_WrittenIntoResults()
   {
      string text = (string)MachineControlCompiler.InFolder( folder => MachineControlCompiler.Call( "ConditionsJson", folder ) );
      using JsonDocument json = JsonDocument.Parse( text );
      JsonElement root = json.RootElement;
      Assert.Equal( 5, root.GetProperty( "runSeed" ).GetInt32() );
      JsonElement c = root.GetProperty( "conditions" );
      foreach( string name in new[] { "buildConfiguration", "governor", "governorAtEnd", "clientCpus", "engineCpus", "threadSiblings", "warmupSearches", "exactSeconds", "dotNet", "search" } )
      {
         Assert.True( c.TryGetProperty( name, out _ ), $"conditions.{name} missing" );
      }

      Assert.Equal( 100, c.GetProperty( "search" ).GetProperty( "hnswEf" ).GetInt32() );
      Assert.Equal( 4, c.GetProperty( "threadSiblings" ).GetArrayLength() );
   }

   /// <summary>
   /// The consolidation's own reader (RunConditions) finds every condition machine control
   /// writes, under the names it accepts, and reports none of them missing.
   /// </summary>
   [Fact]
   public void Conditions_ReadByConsolidation()
   {
      string[] read = (string[])MachineControlCompiler.InFolder( folder => MachineControlCompiler.Call( "ConditionsReadBack", folder ) );
      Assert.Equal( "Release", read[0] );
      Assert.Equal( "performance", read[1] );
      Assert.Equal( "0-1,4-5", read[2].Replace( " ", string.Empty ) );
      Assert.Equal( "2-3,6-7", read[3].Replace( " ", string.Empty ) );
      Assert.Equal( "7", read[4] );
      Assert.Equal( "9", read[5] );
      Assert.Equal( "0,4;1,5;2,6;3,7", read[6] );
      Assert.Equal( string.Empty, read[7] );
   }

   #endregion Public Methods

   #region Private Methods

   /// <summary>
   /// The waited seconds of a pass line ("... waited=0.4 ...").
   /// </summary>
   /// <param name="pass">The line.</param>
   /// <returns>Seconds.</returns>
   private static double Waited( string pass )
   {
      string part = pass.Split( ' ' ).First( p => p.StartsWith( "waited=", StringComparison.Ordinal ) );
      return double.Parse( part["waited=".Length..], System.Globalization.CultureInfo.InvariantCulture );
   }

   /// <summary>
   /// Asserts that the calls contain each expected call, in this order.
   /// </summary>
   /// <param name="calls">Calls made.</param>
   /// <param name="expected">Calls expected, in order.</param>
   private static void AssertInOrder( string[] calls, params string[] expected )
   {
      int at = -1;
      foreach( string call in expected )
      {
         int found = Array.IndexOf( calls, call, at + 1 );
         Assert.True( found > at, $"'{call}' missing or out of order in:\n{string.Join( "\n", calls )}" );
         at = found;
      }
   }

   #endregion Private Methods
}

/// <summary>
/// Compiles the benchmark's sources with MachineControlScenarios.cs into one in-memory assembly
/// and calls its scenarios. Why Roslyn: the benchmark is a console project this test project
/// does not reference (the same approach as RunnerTests).
/// </summary>
internal static class MachineControlCompiler
{
   #region Data Members

   private const string SCENARIOS_TYPE = "GenericVectorBuilder.Bench.UnderTest.MachineControlScenarios";
   private const string IMPLICIT_USINGS = "global using System; global using System.Collections.Generic; global using System.IO; global using System.Linq; "
      + "global using System.Net.Http; global using System.Threading; global using System.Threading.Tasks;";
   private static readonly TimeSpan SCENARIO_DEADLINE = TimeSpan.FromMinutes( 4 );
   private static readonly Lazy<Assembly> COMPILED = new( Compile );

   #endregion Data Members

   #region Public Methods

   /// <summary>
   /// The lscpu text of linus7795 from the scenarios.
   /// </summary>
   /// <returns>The text.</returns>
   public static string LinusLscpu()
   {
      return (string)COMPILED.Value.GetType( SCENARIOS_TYPE )!.GetField( "LINUS_LSCPU" )!.GetValue( null )!;
   }

   /// <summary>
   /// Calls a synchronous scenario.
   /// </summary>
   /// <param name="method">Method name.</param>
   /// <param name="arguments">Arguments.</param>
   /// <returns>Its result.</returns>
   public static dynamic Call( string method, params object[] arguments )
   {
      return COMPILED.Value.GetType( SCENARIOS_TYPE )!.GetMethod( method )!.Invoke( null, arguments )!;
   }

   /// <summary>
   /// Calls an async scenario with a deadline.
   /// </summary>
   /// <param name="method">Method name.</param>
   /// <param name="arguments">Arguments.</param>
   /// <returns>Its result.</returns>
   public static async Task<dynamic> CallAsync( string method, params object[] arguments )
   {
      var task = (Task)COMPILED.Value.GetType( SCENARIOS_TYPE )!.GetMethod( method )!.Invoke( null, arguments )!;
      Task finished = await Task.WhenAny( task, Task.Delay( SCENARIO_DEADLINE ) );
      Assert.True( finished == task, $"{method} did not finish within {SCENARIO_DEADLINE.TotalMinutes:0} minutes" );
      await task;
      return task.GetType().GetProperty( "Result" )!.GetValue( task )!;
   }

   /// <summary>
   /// Runs a scenario in a fresh folder under the test output and removes the folder after.
   /// </summary>
   /// <param name="run">The scenario.</param>
   /// <returns>Its result.</returns>
   public static dynamic InFolder( Func<string, dynamic> run )
   {
      string folder = NewFolder();
      try
      {
         return run( folder );
      }
      finally
      {
         Directory.Delete( folder, true );
      }
   }

   /// <summary>
   /// Runs an async scenario in a fresh folder under the test output and removes the folder after.
   /// </summary>
   /// <param name="run">The scenario.</param>
   /// <returns>Its result.</returns>
   public static async Task<dynamic> InFolderAsync( Func<string, Task<dynamic>> run )
   {
      string folder = NewFolder();
      try
      {
         return await run( folder );
      }
      finally
      {
         Directory.Delete( folder, true );
      }
   }

   #endregion Public Methods

   #region Private Methods

   /// <summary>
   /// A new empty folder under the test output (never /tmp).
   /// </summary>
   /// <returns>Its path.</returns>
   private static string NewFolder()
   {
      string folder = Path.Combine( AppContext.BaseDirectory, "machine-control-tests", Guid.NewGuid().ToString( "N" ) );
      Directory.CreateDirectory( folder );
      return folder;
   }

   /// <summary>
   /// Compiles every benchmark source plus MachineControlScenarios.cs.
   /// </summary>
   /// <returns>The assembly.</returns>
   private static Assembly Compile()
   {
      string root = RepoRoot();
      string bench = Path.Combine( root, "src", "GenericVectorBuilder.Bench" );
      var parse = new CSharpParseOptions( LanguageVersion.Latest );
      List<SyntaxTree> trees = Directory.EnumerateFiles( bench, "*.cs", SearchOption.AllDirectories )
         .Where( f => Path.GetRelativePath( bench, f ).Split( Path.DirectorySeparatorChar )[0] is not ( "bin" or "obj" ) )
         .Select( f => CSharpSyntaxTree.ParseText( File.ReadAllText( f ), parse, f ) ).ToList();
      string scenarios = Path.Combine( root, "tests", "GenericVectorBuilder.Engines.Tests", "Bench", "MachineControlScenarios.cs" );
      trees.Add( CSharpSyntaxTree.ParseText( File.ReadAllText( scenarios ), parse.WithPreprocessorSymbols( "BENCH_UNDER_TEST" ), scenarios ) );
      trees.Add( CSharpSyntaxTree.ParseText( IMPLICIT_USINGS, parse ) );
      List<string> platform = ( (string)AppContext.GetData( "TRUSTED_PLATFORM_ASSEMBLIES" )! ).Split( Path.PathSeparator ).ToList();
      List<string> extra = BenchOnlyPackages( bench, platform.Select( Path.GetFileName ).ToHashSet( StringComparer.OrdinalIgnoreCase ) );
      AssemblyLoadContext.Default.Resolving += ( context, name ) =>
         extra.FirstOrDefault( p => Path.GetFileNameWithoutExtension( p ).Equals( name.Name, StringComparison.OrdinalIgnoreCase ) ) is string path ? context.LoadFromAssemblyPath( path ) : null;
      var options = new CSharpCompilationOptions( OutputKind.DynamicallyLinkedLibrary, nullableContextOptions: NullableContextOptions.Enable );
      CSharpCompilation compilation = CSharpCompilation.Create( "BenchMachineControlUnderTest", trees, platform.Concat( extra ).Select( p => MetadataReference.CreateFromFile( p ) ), options );
      using var image = new MemoryStream();
      Microsoft.CodeAnalysis.Emit.EmitResult result = compilation.Emit( image );
      Assert.True( result.Success, string.Join( Environment.NewLine, result.Diagnostics.Where( d => d.Severity == DiagnosticSeverity.Error ) ) );
      return Assembly.Load( image.ToArray() );
   }

   /// <summary>
   /// Runtime files of packages the benchmark restored that the test host does not have.
   /// </summary>
   /// <param name="bench">Benchmark project folder.</param>
   /// <param name="loaded">File names already on the platform list.</param>
   /// <returns>Full paths.</returns>
   private static List<string> BenchOnlyPackages( string bench, HashSet<string?> loaded )
   {
      string assets = Path.Combine( bench, "obj", "project.assets.json" );
      Assert.True( File.Exists( assets ), $"Restore the benchmark project first (no {assets})." );
      using JsonDocument doc = JsonDocument.Parse( File.ReadAllText( assets ) );
      string packages = doc.RootElement.GetProperty( "packageFolders" ).EnumerateObject().First().Name;
      JsonElement libraries = doc.RootElement.GetProperty( "libraries" );
      var extra = new List<string>();
      foreach( JsonProperty library in doc.RootElement.GetProperty( "targets" ).EnumerateObject().First().Value.EnumerateObject() )
      {
         if( !library.Value.TryGetProperty( "runtime", out JsonElement runtime ) || library.Value.GetProperty( "type" ).GetString() != "package" )
         {
            continue;
         }

         string folder = libraries.GetProperty( library.Name ).GetProperty( "path" ).GetString()!;
         extra.AddRange( runtime.EnumerateObject().Select( r => r.Name ).Where( r => r.EndsWith( ".dll", StringComparison.OrdinalIgnoreCase ) && !loaded.Contains( Path.GetFileName( r ) ) )
            .Select( r => Path.Combine( packages, folder, r ) ) );
      }

      return extra;
   }

   /// <summary>
   /// Walks up from the test binary to the folder holding the solution file.
   /// </summary>
   /// <returns>The repository root.</returns>
   private static string RepoRoot()
   {
      var dir = new DirectoryInfo( AppContext.BaseDirectory );
      while( dir != null && !File.Exists( Path.Combine( dir.FullName, "GenericVectorBuilder.slnx" ) ) )
      {
         dir = dir.Parent;
      }

      return dir?.FullName ?? throw new InvalidOperationException( "Repository root not found." );
   }

   #endregion Private Methods
}
