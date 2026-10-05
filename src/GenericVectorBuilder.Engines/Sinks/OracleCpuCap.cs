using System.Globalization;

namespace GenericVectorBuilder.Engines.Sinks;

/// <summary>
/// What Oracle itself says about the CPUs it will use, read from V$INSTANCE (edition), V$PARAMETER
/// (cpu_count) and V$OSSTAT (the CPUs the host shows to Oracle), and the words the report prints
/// for it.
/// Why this exists: Oracle Free caps itself at 2 CPU threads whatever the host has (measured
/// 2026-10-04 on this box: edition FREE, cpu_count 2 not set by the user, 8 CPUs in V$OSSTAT NUM_CPUS),
/// so its search timings come from at most two threads next to engines that use all eight. The
/// pinning of the container to some CPUs cannot raise that cap. A result that does not carry the cap
/// invites a reader to compare Oracle's latency with an engine that had four times the CPU.
/// Why the wording is a pure function: it is tested without a database, and the exact phrase
/// "Oracle Free caps itself at N CPUs" is what a reader searches the results for.
/// </summary>
/// <param name="Edition">V$INSTANCE.EDITION, e.g. FREE.</param>
/// <param name="CpuCount">V$PARAMETER cpu_count: how many CPUs the instance schedules on.</param>
/// <param name="OsCpus">V$OSSTAT NUM_CPUS: the CPUs the host shows to Oracle, or null when not read.</param>
public sealed record OracleCpuCap( string Edition, long CpuCount, long? OsCpus )
{
   #region Data Members

   /// <summary>
   /// The CPU threads Oracle documents for its Free edition. Documented by Oracle, not read from the
   /// database: the database shows cpu_count, which is what the edition set it to.
   /// </summary>
   public const int FREE_EDITION_CPU_LIMIT = 2;

   /// <summary>
   /// The statement for a sink that has not read the database yet (the durability text is read
   /// before any connection exists). Says it is the measured value of 2026-10-04, not a live reading.
   /// </summary>
   public const string STATIC_NOTE =
      "Oracle Free caps itself at 2 CPUs (measured 2026-10-04: cpu_count 2 in V$PARAMETER, edition FREE in V$INSTANCE, 8 host CPUs in V$OSSTAT NUM_CPUS; "
      + "the 2 CPU thread limit is Oracle's documented Free edition limit; this sink reads the live value when the index state is read)";

   #endregion Data Members

   #region Public Methods

   /// <summary>True when the instance is the Free edition.</summary>
   public bool IsFree => string.Equals( Edition, "FREE", StringComparison.OrdinalIgnoreCase );

   /// <summary>
   /// How many CPUs the engine can use: for the Free edition the smaller of cpu_count and the
   /// edition limit, for any other edition cpu_count.
   /// </summary>
   public long EffectiveCpus => IsFree ? Math.Min( CpuCount, FREE_EDITION_CPU_LIMIT ) : CpuCount;

   /// <summary>
   /// The sentence for the report, with the numbers read from the database.
   /// </summary>
   /// <returns>Text such as "Oracle Free caps itself at 2 CPUs (cpu_count 2 in V$PARAMETER, edition FREE in V$INSTANCE, 8 host CPUs in V$OSSTAT NUM_CPUS; ...)".</returns>
   public string Describe()
   {
      string host = OsCpus.HasValue ? $", {OsCpus.Value.ToString( CultureInfo.InvariantCulture )} host CPUs in V$OSSTAT NUM_CPUS" : string.Empty;
      string read = $"cpu_count {CpuCount.ToString( CultureInfo.InvariantCulture )} in V$PARAMETER, edition {Edition} in V$INSTANCE{host}";
      return IsFree
         ? $"Oracle Free caps itself at {EffectiveCpus.ToString( CultureInfo.InvariantCulture )} CPUs ({read}; the {FREE_EDITION_CPU_LIMIT} CPU thread limit is Oracle's documented Free edition limit)"
         : $"Oracle {Edition} runs on {EffectiveCpus.ToString( CultureInfo.InvariantCulture )} CPUs by cpu_count ({read}); this edition has no Free-edition CPU cap";
   }

   #endregion Public Methods
}
