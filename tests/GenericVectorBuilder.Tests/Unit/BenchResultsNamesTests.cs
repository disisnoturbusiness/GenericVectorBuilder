using System.Text.Json;
using GenericVectorBuilder.Web.BenchPages;

namespace GenericVectorBuilder.Tests.Unit;

/// <summary>
/// The names a reader sees for a benchmark target, and the conditions a run page reads from one run's own fields.
/// Why here: the consolidated page carries its own display names, so these two readers serve only the run pages and
/// the fallback of a row with no name, and nothing else would notice if a target lost its friendly name or a run's
/// build and governor stopped being read.
/// </summary>
public class BenchResultsNamesTests
{
   #region Public Methods

   /// <summary>
   /// The native comparison targets say so, a target that runs in the benchmark's own container keeps its engine's name,
   /// an unknown target is printed as it is, and the lookup ignores case.
   /// </summary>
   [Theory]
   [InlineData( "sql", "SQL Server 2025" )]
   [InlineData( "sql-native", "SQL Server 2025 (native service)" )]
   [InlineData( "qdrant-native", "Qdrant (native service, exact)" )]
   [InlineData( "mariadb", "MariaDB" )]
   [InlineData( "mariadb-bench", "MariaDB" )]
   [InlineData( "MARIADB", "MariaDB" )]
   [InlineData( "sqlitevec", "sqlite-vec" )]
   [InlineData( "a-target-nobody-named", "a-target-nobody-named" )]
   public void FriendlyName_FollowsTheTable( string key, string expected )
   {
      Assert.Equal( expected, BenchNames.FriendlyName( key ) );
   }

   /// <summary>
   /// Every target the table knows has a name of its own, and no two targets share one, so two rows of a table can never read alike.
   /// </summary>
   [Fact]
   public void EveryKnownTarget_HasADistinctName()
   {
      List<string> names = BenchNames.KnownTargets.Select( BenchNames.FriendlyName ).ToList();

      Assert.All( names, n => Assert.False( string.IsNullOrWhiteSpace( n ) ) );
      Assert.Equal( names.Count, names.Distinct( StringComparer.Ordinal ).Count() );
   }

   /// <summary>
   /// The build comes from the field, or from the binary path in the command line when no field has it; the warm-up is the run's own note (a count
   /// field is kept apart and is never read as the method); the partition from its own two lists or from one text; a governor that changed during the
   /// run reads "a, then b".
   /// </summary>
   [Fact]
   public void Conditions_AreReadFromTheSpellingsARunMayUse()
   {
      using JsonDocument fromPath = JsonDocument.Parse( """
         { "commandLine": "dotnet /x/bin/Debug/net10.0/Bench.dll run-all --exact-seconds 45", "governor": "performance", "governorAtEnd": "powersave", "cpuPartition": "client 0; engines 1",
           "notes": [ "Preparation, untimed warm-up of 25 searches before each pass." ] }
         """ );
      using JsonDocument fromFields = JsonDocument.Parse( """
         { "conditions": { "buildConfiguration": "RELEASE", "clientCpus": "0-1", "engineCpus": "2-3", "warmupSearches": 20, "exactSeconds": 60, "governor": "unknown" } }
         """ );

      BenchConditions a = BenchConditions.FromRun( fromPath.RootElement );
      BenchConditions b = BenchConditions.FromRun( fromFields.RootElement );

      Assert.Equal( "Debug", a.Build );
      Assert.True( a.BuildIsWrong );
      Assert.Equal( "performance, then powersave", a.Governor );
      Assert.True( a.GovernorIsWrong );
      Assert.Equal( "client 0; engines 1", a.Partition );
      Assert.Equal( "Preparation, untimed warm-up of 25 searches before each pass.", a.WarmupMethod );
      Assert.Null( a.WarmupField );
      Assert.Equal( "45", a.ExactSeconds );
      Assert.Equal( "Release", b.Build );
      Assert.False( b.BuildIsWrong );
      Assert.Null( b.Governor );
      Assert.False( b.GovernorIsWrong );
      Assert.Equal( "client 0-1; engines 2-3", b.Partition );
      Assert.Null( b.WarmupMethod );
      Assert.Equal( "20", b.WarmupField );
      Assert.Equal( "60", b.ExactSeconds );
   }

   /// <summary>
   /// A run that recorded nothing reads as nothing recorded: no value is made up.
   /// </summary>
   [Fact]
   public void Conditions_OfAnEmptyRun_AreNotRecorded()
   {
      using JsonDocument empty = JsonDocument.Parse( "{}" );

      BenchConditions conditions = BenchConditions.FromRun( empty.RootElement );

      Assert.True( conditions.NoneRecorded );
      Assert.Empty( conditions.Machine );
      Assert.Empty( conditions.Clock );
      Assert.Null( conditions.WarmupMethod );
      Assert.Null( conditions.WarmupField );
      Assert.Null( conditions.ClockNote );
      Assert.False( conditions.NoClockPinRecorded );
      Assert.Null( conditions.CodeCommit );
      Assert.Equal( BenchConditions.None.Governor, conditions.Governor );
   }

   #endregion Public Methods
}
