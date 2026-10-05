using GenericVectorBuilder.Engines.Common;
using GenericVectorBuilder.Engines.Sinks;

namespace GenericVectorBuilder.Engines.Tests;

/// <summary>
/// Checks the wording of the Oracle durability statement. It opens no connection, so it runs
/// without the container.
/// Why wording is tested: the report prints this text next to the speed numbers, and a claim
/// that was read from settings or measured in a running database, but never shown by pulling
/// power, must say so, the same way the SQL Server statement does.
/// </summary>
public sealed class OracleDurabilityTests
{
   #region Public Methods

   /// <summary>
   /// The statement must say it was not tested by cutting power and that the disk's own write
   /// cache was not checked, and must not state the crash and power-loss behaviour as proven.
   /// </summary>
   [Fact]
   public void Durability_SaysItWasNotTestedByCuttingPower()
   {
      string text = ( (IEngineDescription)new OracleSink() ).Durability;
      Assert.Contains( "Not tested by cutting power", text );
      Assert.Contains( "disk's own write cache", text );
      Assert.DoesNotContain( "Committed rows survive a crash or power loss", text );
      Assert.Contains( "should survive", text );
   }

   /// <summary>
   /// The earlier measured facts stay in the statement next to the caveat.
   /// </summary>
   [Fact]
   public void Durability_KeepsTheMeasuredFacts()
   {
      string text = ( (IEngineDescription)new OracleSink() ).Durability;
      Assert.Contains( "redo synch writes", text );
      Assert.Contains( "O_DSYNC", text );
      Assert.Contains( "NOARCHIVELOG", text );
   }

   /// <summary>
   /// The durability text, which the report reads before any connection exists, carries the CPU cap
   /// as the value measured on this box, and says it is not a live reading.
   /// </summary>
   [Fact]
   public void Durability_StatesTheCpuCap()
   {
      string text = ( (IEngineDescription)new OracleSink() ).Durability;
      Assert.Contains( "Oracle Free caps itself at 2 CPUs", text );
      Assert.Contains( "cpu_count 2", text );
      Assert.Contains( "measured 2026-10-04", text );
   }

   /// <summary>
   /// The index description, which the report prints next to every Oracle timing, carries the CPU
   /// cap before the sink has read the database.
   /// </summary>
   [Fact]
   public void IndexDescription_StatesTheCpuCap()
   {
      string text = ( (IEngineDescription)new OracleSink() ).IndexDescription;
      Assert.Contains( "Oracle Free caps itself at 2 CPUs", text );
      Assert.Contains( "HNSW", text );
   }

   #endregion Public Methods
}
