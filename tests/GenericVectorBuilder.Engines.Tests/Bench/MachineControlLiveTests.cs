using Xunit.Abstractions;

namespace GenericVectorBuilder.Engines.Tests.Bench;

/// <summary>
/// Machine control against the real box: the governor of every CPU is set to performance
/// through sudo and put back, both by the run's own restore and, after a simulated crash, by
/// the restore every start does first. Uses the real state file
/// (/home/dan/gvb-work/bench-machine-state.json), so if this test is killed half-way the next
/// benchmark start, or "restore-machine", still puts the governor back. Fails, rather than
/// waits, when a real benchmark run owns the machine.
/// Why live: the unit tests prove the rules on a fake machine; only the real box shows that
/// sudo, the sysfs files and the read-back really behave as the code assumes.
/// </summary>
[Trait( "Category", "Live" )]
public class MachineControlLiveTests
{
   #region Data Members

   private readonly ITestOutputHelper _output;

   #endregion Data Members

   #region Constructor

   /// <summary>
   /// Creates the test with xunit's output sink.
   /// </summary>
   /// <param name="output">Where the evidence is printed.</param>
   public MachineControlLiveTests( ITestOutputHelper output )
   {
      _output = output;
   }

   #endregion Constructor

   #region Public Methods

   /// <summary>
   /// Every CPU reads "performance" while controlled, the state file holds the governors from
   /// before, and afterwards every CPU reads exactly what it read before, with no state file
   /// left; the same after a run that "dies" and is restored by the next start.
   /// </summary>
   [Fact]
   public async Task Governor_SetAndPutBack()
   {
      dynamic r = await MachineControlCompiler.CallAsync( "LiveGovernorAsync" );
      string[] before = r.Before;
      _output.WriteLine( "before:      " + string.Join( " ", before ) );
      _output.WriteLine( "during:      " + string.Join( " ", (string[])r.During ) );
      _output.WriteLine( "recorded:    " + string.Join( " ", (string[])r.Recorded ) );
      _output.WriteLine( "after:       " + string.Join( " ", (string[])r.After ) );
      _output.WriteLine( "crash during:" + string.Join( " ", (string[])r.CrashDuring ) );
      _output.WriteLine( "crash after: " + string.Join( " ", (string[])r.CrashAfter ) );
      ( (string[])r.Log ).ToList().ForEach( _output.WriteLine );
      Assert.All( (string[])r.During, g => Assert.Equal( "performance", g ) );
      Assert.Equal( before, (string[])r.Recorded );
      Assert.Equal( before, (string[])r.After );
      Assert.False( (bool)r.FileAfter );
      Assert.All( (string[])r.CrashDuring, g => Assert.Equal( "performance", g ) );
      Assert.Equal( before, (string[])r.CrashAfter );
      Assert.False( (bool)r.CrashFileAfter );
      Assert.Contains( (string[])r.Log, l => l.Contains( "did not finish", StringComparison.Ordinal ) );
   }

   #endregion Public Methods
}
