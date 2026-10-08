using GenericVectorBuilder.Bench.Report;

namespace GenericVectorBuilder.Bench.Stats;

/// <summary>
/// Everything one consolidation reads, already loaded: the claim sessions, the basis sessions, the
/// other runs of the pipeline found beside them, the exclusion rows, the fact sheet, the observer
/// summary and where the repository is.
/// Why loaded by the command and passed in: the consolidation itself touches no file except the raw
/// results.json files and the repository files its audit re-reads, so tests can build any input.
/// </summary>
public sealed class ConsolidateInput
{
   #region Public Methods

   /// <summary>Claim sessions (one or two), in the order given, each with its runs.</summary>
   public List<(string Name, List<RunResult> Runs)> Sessions { get; init; } = new();

   /// <summary>Basis-only sessions (v5, v6), each with its runs.</summary>
   public List<(string Name, List<RunResult> Runs)> BasisSessions { get; init; } = new();

   /// <summary>Every other run of the pipeline found in the folders holding the session runs.</summary>
   public List<RunResult> OtherRuns { get; init; } = new();

   /// <summary>Folders beside the session runs that could not be read, with the reason.</summary>
   public List<LeftOutRun> Unreadable { get; init; } = new();

   /// <summary>Targets to report, or empty for every target of the claim runs.</summary>
   public List<string> Targets { get; init; } = new();

   /// <summary>The exclusion rows.</summary>
   public List<ExclusionRow> Exclusions { get; init; } = new();

   /// <summary>The fact sheet rows, as P5's loader read them.</summary>
   public IReadOnlyList<EngineFactRow> Facts { get; init; } = Array.Empty<EngineFactRow>();

   /// <summary>SHA-256 of the fact sheet file.</summary>
   public string FactsSha256 { get; init; } = string.Empty;

   /// <summary>SHA-256 of the exclusions file.</summary>
   public string ExclusionsSha256 { get; init; } = string.Empty;

   /// <summary>Repository root: file and doc sources resolve under it.</summary>
   public string RepoRoot { get; init; } = string.Empty;

   /// <summary>The folder session folders were named relative to; run references in the report are relative to it.</summary>
   public string ResultsRoot { get; init; } = string.Empty;

   /// <summary>Every observer summary given, in the order given (the newest session's and any older session's); empty when none.</summary>
   public List<string> ObserverPaths { get; init; } = new();

   /// <summary>The classes of the recorded texts the report prints (deploy/bench/recorded-text-classes.json of the repository).</summary>
   public ClassSheet Classes { get; init; } = new( Array.Empty<ClassEntry>(), new Dictionary<string, string>(), new Dictionary<string, string>() );

   /// <summary>SHA-256 of the classes file.</summary>
   public string ClassesSha256 { get; init; } = string.Empty;

   /// <summary>The build that makes the report.</summary>
   public ConsolidatingBuild Build { get; init; } = new();

   /// <summary>The command line, for the report.</summary>
   public string CommandLine { get; init; } = string.Empty;

   /// <summary>When the report is made (UTC text).</summary>
   public string CreatedUtc { get; init; } = string.Empty;

   #endregion Public Methods
}

/// <summary>
/// A consolidation that could not be made: the runs are not one experiment, a run is missing a
/// value, a fact or an exclusion does not hold, or a sentence failed the audit. Nothing is written.
/// Why its own type: the command maps it to exit 1 with the message, and every reason is a refusal
/// the owner reads, never a run quietly dropped.
/// </summary>
public sealed class ConsolidateRefusal : Exception
{
   #region Constructor

   /// <summary>
   /// Creates the refusal.
   /// </summary>
   /// <param name="message">What is wrong, naming the runs, targets and fields.</param>
   public ConsolidateRefusal( string message ) : base( message )
   {
   }

   #endregion Constructor
}
