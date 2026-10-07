namespace GenericVectorBuilder.Engines.Tests.Bench;

/// <summary>
/// The test collection for tests that measure real time on a fake machine: a pass of 400 ms, a
/// sampler ticking every 20 ms, a settle check over real windows. The collection runs alone, after
/// every parallel collection has finished, so no other test class is compiling with Roslyn or
/// burning CPU while one of these reads the clock. Why: with the whole suite in parallel the
/// timer jitter alone moved a 400 ms pass to 454 ms and starved the sampler, so an engine figure
/// came out 13% high or came out null, and a test that checks the figure failed one run in three.
/// </summary>
[CollectionDefinition( TimingCollection.NAME, DisableParallelization = true )]
public sealed class TimingCollection
{
   #region Data Members

   /// <summary>
   /// The collection's name, as the test classes name it.
   /// </summary>
   public const string NAME = "BenchRealTimeTests";

   #endregion Data Members
}
