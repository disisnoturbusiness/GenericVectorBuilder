using System.Text.RegularExpressions;
using GenericVectorBuilder.Core.Contracts;
using GenericVectorBuilder.Engines.Common;
using GenericVectorBuilder.Engines.Sinks;
using Oracle.ManagedDataAccess.Client;
using Xunit.Abstractions;

namespace GenericVectorBuilder.Engines.Tests;

/// <summary>
/// Proves against the real Oracle container (deploy/engines/oracle.compose.yaml must be up) that
/// the sink reports the CPU cap it reads from the database, and that the number is the one the
/// database itself holds: cpu_count is read here again with a plain query, apart from the sink.
/// Why: Oracle Free caps itself at 2 CPU threads whatever the host has, so its timings must
/// carry that next to them.
/// </summary>
[Trait( "Category", "Live" )]
public sealed class OracleCpuCapLiveTests
{
   #region Data Members

   private const int DIMENSION = 64;
   private const int COUNT = 200;
   private static readonly Regex CAP = new( @"Oracle Free caps itself at (\d+) CPUs", RegexOptions.Compiled | RegexOptions.CultureInvariant );

   private readonly ITestOutputHelper _output;

   #endregion Data Members

   #region Constructor

   /// <summary>
   /// Receives the xunit output writer.
   /// </summary>
   /// <param name="output">Where the evidence is printed.</param>
   public OracleCpuCapLiveTests( ITestOutputHelper output )
   {
      _output = output;
   }

   #endregion Constructor

   #region Public Methods

   /// <summary>
   /// Loads a small collection, reads the index state, and requires the detail and the index
   /// description to say "Oracle Free caps itself at N CPUs" with N equal to cpu_count as a plain
   /// query reads it, and the host CPU count to be the one V$OSSTAT reports.
   /// </summary>
   [Fact]
   public async Task GetIndexState_ReportsTheCpuCapReadFromTheDatabase()
   {
      string collection = EnginesAReadinessData.BenchCollection();
      var sink = new OracleSink();
      try
      {
         await sink.EnsureCollectionAsync( collection, DIMENSION, CancellationToken.None );
         await sink.UpsertAsync( collection, EnginesAReadinessData.Records( COUNT, DIMENSION, seed: 41 ), CancellationToken.None );
         string note = await sink.FinishLoadAsync( collection, CancellationToken.None );
         IndexState state = await sink.GetIndexStateAsync( collection, CancellationToken.None );
         _output.WriteLine( $"Oracle index state detail: {state.Detail}" );
         _output.WriteLine( $"Oracle FinishLoadAsync note: {note}" );
         _output.WriteLine( $"Oracle IndexDescription: {( (IEngineDescription)sink ).IndexDescription}" );
         _output.WriteLine( $"Oracle Durability: {( (IEngineDescription)sink ).Durability}" );

         ( long cpuCount, long osCpus, string edition ) = await ReadCpuFactsAsync();
         _output.WriteLine( $"Independent query: edition {edition}, cpu_count {cpuCount}, V$OSSTAT NUM_CPUS {osCpus}" );

         Match cap = CAP.Match( state.Detail );
         Assert.True( cap.Success, state.Detail );
         Assert.Equal( Math.Min( cpuCount, 2 ), long.Parse( cap.Groups[1].Value ) );
         Assert.Contains( $"cpu_count {cpuCount} in V$PARAMETER", state.Detail );
         Assert.Contains( $"{osCpus} host CPUs in V$OSSTAT NUM_CPUS", state.Detail );
         Assert.Contains( $"edition {edition} in V$INSTANCE", state.Detail );
         Assert.Contains( "Oracle Free caps itself at", note );
         Assert.Contains( $"cpu_count {cpuCount} in V$PARAMETER", ( (IEngineDescription)sink ).IndexDescription );
         Assert.Contains( "Oracle Free caps itself at 2 CPUs", ( (IEngineDescription)sink ).Durability );
      }
      finally
      {
         await sink.DropCollectionAsync( collection, CancellationToken.None );
      }
   }

   #endregion Public Methods

   #region Private Methods

   /// <summary>
   /// Reads the edition, cpu_count and NUM_CPUS with its own connection, apart from the sink.
   /// </summary>
   /// <returns>The three facts.</returns>
   private static async Task<( long CpuCount, long OsCpus, string Edition )> ReadCpuFactsAsync()
   {
      OracleSinkOptions options = OracleSinkOptions.LocalDefaults();
      string user = string.IsNullOrEmpty( options.EvidenceUser ) ? options.User : options.EvidenceUser;
      string password = string.IsNullOrEmpty( options.EvidenceUser ) ? options.Password ?? string.Empty : options.EvidencePassword ?? string.Empty;
      string connectionString = new OracleConnectionStringBuilder { UserID = user, Password = password, DataSource = $"{options.Host}:{options.Port}/{options.ServiceName}", ConnectionTimeout = 30 }.ConnectionString;
      await using var connection = new OracleConnection( connectionString );
      await connection.OpenAsync();
      await using OracleCommand command = connection.CreateCommand();
      command.CommandTimeout = 60;
      command.CommandText = "SELECT ( SELECT TO_NUMBER( value ) FROM v$parameter WHERE name = 'cpu_count' ), ( SELECT value FROM v$osstat WHERE stat_name = 'NUM_CPUS' ), ( SELECT edition FROM v$instance ) FROM dual";
      await using OracleDataReader reader = await command.ExecuteReaderAsync();
      Assert.True( await reader.ReadAsync() );
      return ( Convert.ToInt64( reader.GetValue( 0 ) ), Convert.ToInt64( reader.GetValue( 1 ) ), reader.GetString( 2 ) );
   }

   #endregion Private Methods
}
