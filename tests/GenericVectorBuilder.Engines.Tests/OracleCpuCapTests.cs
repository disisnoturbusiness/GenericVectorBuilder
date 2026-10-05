using GenericVectorBuilder.Engines.Sinks;

namespace GenericVectorBuilder.Engines.Tests;

/// <summary>
/// The wording and arithmetic of the Oracle CPU cap statement, without a database. The numbers are
/// the ones read from the local container on 2026-10-04: edition FREE, cpu_count 2, 8 host CPUs.
/// </summary>
public sealed class OracleCpuCapTests
{
   #region Public Methods

   /// <summary>
   /// The Free edition on this box: the phrase a reader searches for, with where each number came from.
   /// </summary>
   [Fact]
   public void Describe_Free_SaysItCapsItselfAtTwoCpus()
   {
      var cap = new OracleCpuCap( "FREE", 2, 8 );
      string text = cap.Describe();
      Assert.Contains( "Oracle Free caps itself at 2 CPUs", text );
      Assert.Contains( "cpu_count 2 in V$PARAMETER", text );
      Assert.Contains( "edition FREE in V$INSTANCE", text );
      Assert.Contains( "8 host CPUs in V$OSSTAT NUM_CPUS", text );
      Assert.Contains( "documented Free edition limit", text );
      Assert.True( cap.IsFree );
      Assert.Equal( 2, cap.EffectiveCpus );
   }

   /// <summary>
   /// A cpu_count below the edition limit is the cap that counts.
   /// </summary>
   [Fact]
   public void Describe_Free_WithCpuCountOne_CapsAtOne()
   {
      var cap = new OracleCpuCap( "FREE", 1, 8 );
      Assert.Equal( 1, cap.EffectiveCpus );
      Assert.Contains( "Oracle Free caps itself at 1 CPUs", cap.Describe() );
   }

   /// <summary>
   /// A cpu_count above the Free limit cannot lift the cap: the edition limit still applies.
   /// </summary>
   [Fact]
   public void Describe_Free_WithCpuCountAboveTheLimit_StaysAtTheLimit()
   {
      var cap = new OracleCpuCap( "FREE", 8, 8 );
      Assert.Equal( 2, cap.EffectiveCpus );
      Assert.Contains( "Oracle Free caps itself at 2 CPUs", cap.Describe() );
      Assert.Contains( "cpu_count 8", cap.Describe() );
   }

   /// <summary>
   /// Another edition has no Free cap and must not claim one.
   /// </summary>
   [Fact]
   public void Describe_OtherEdition_HasNoFreeCap()
   {
      var cap = new OracleCpuCap( "EE", 16, 32 );
      string text = cap.Describe();
      Assert.DoesNotContain( "caps itself", text );
      Assert.Contains( "Oracle EE runs on 16 CPUs", text );
      Assert.Contains( "no Free-edition CPU cap", text );
      Assert.False( cap.IsFree );
   }

   /// <summary>
   /// When the host CPU count could not be read the text leaves that part out instead of printing 0.
   /// </summary>
   [Fact]
   public void Describe_WithoutHostCpus_OmitsThem()
   {
      string text = new OracleCpuCap( "FREE", 2, null ).Describe();
      Assert.DoesNotContain( "host CPUs", text );
      Assert.Contains( "Oracle Free caps itself at 2 CPUs", text );
   }

   /// <summary>
   /// The static note, used when nothing has been read yet, names the measured values and says
   /// they are not a live reading.
   /// </summary>
   [Fact]
   public void StaticNote_SaysItIsTheMeasuredValue()
   {
      Assert.Contains( "Oracle Free caps itself at 2 CPUs", OracleCpuCap.STATIC_NOTE );
      Assert.Contains( "measured 2026-10-04", OracleCpuCap.STATIC_NOTE );
      Assert.Contains( "reads the live value", OracleCpuCap.STATIC_NOTE );
   }

   #endregion Public Methods
}
