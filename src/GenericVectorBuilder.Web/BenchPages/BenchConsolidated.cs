namespace GenericVectorBuilder.Web.BenchPages;

/// <summary>One run a session used, as the consolidated file names it.</summary>
/// <param name="Folder">The run's folder name under the results root (untrusted text: the page checks it before it links to it).</param>
/// <param name="Seed">The run's seed, or null when the file does not give it.</param>
/// <param name="StartedUtc">When the run started, as the file wrote it, or null.</param>
public sealed record BenchSessionRun( string Folder, long? Seed, string? StartedUtc );

/// <summary>One session of runs (for example the three runs of one night) a consolidated set compares.</summary>
/// <param name="Name">The session's name, e.g. "v7"; it is also the key of every row's per-session figures.</param>
/// <param name="Runs">The runs it holds.</param>
public sealed record BenchSession( string Name, IReadOnlyList<BenchSessionRun> Runs );

/// <summary>The figures of one engine in one session for one metric.</summary>
/// <param name="Min">Lowest run figure.</param>
/// <param name="Max">Highest run figure.</param>
/// <param name="Runs">Every run's figure, in run order.</param>
public sealed record BenchRange( double Min, double Max, IReadOnlyList<double> Runs );

/// <summary>One warning on a row of a metric table.</summary>
/// <param name="Code">The flag code (the kind the consolidate command wrote, e.g. "clock-off").</param>
/// <param name="Text">The evidence, as the consolidate command wrote it.</param>
public sealed record BenchFlagEntry( string Code, string Text );

/// <summary>One engine's row in one metric table.</summary>
/// <param name="Target">The target name the Bench tool used.</param>
/// <param name="Display">The name a reader sees.</param>
/// <param name="Status">"ranked" or "one-session".</param>
/// <param name="PerSession">The figures per session, by session name.</param>
/// <param name="Median">The median of all runs of all sessions, or null for a row with no median.</param>
/// <param name="NotSeparatedFrom">Targets this row is not separated from by the claim rule.</param>
/// <param name="Flags">Warnings about this row.</param>
/// <param name="Note">A note printed with the engine's name (for example that it holds its data in memory), or null.</param>
/// <param name="SearchMode">How the engine's search ran, as the report's search fact for the engine says ("approximate" or "exact", printed as written), or null for a table that has no such column (the exact-mode table) and for a file that gives none.</param>
/// <param name="NoteSources">What the note is bound to (a saved page, a repository file), printed with the note; empty when the note has none or the row has no note.</param>
public sealed record BenchRow( string Target, string Display, string Status, IReadOnlyDictionary<string, BenchRange> PerSession, double? Median,
   IReadOnlyList<string> NotSeparatedFrom, IReadOnlyList<BenchFlagEntry> Flags, string? Note, string? SearchMode = null, IReadOnlyList<BenchSource>? NoteSources = null )
{
   #region Public Methods

   /// <summary>The sources of the row's note; never null.</summary>
   public IReadOnlyList<BenchSource> NoteSourceList => NoteSources ?? Array.Empty<BenchSource>();

   #endregion Public Methods
}

/// <summary>One metric table.</summary>
/// <param name="Metric">The metric's name: "p50Ms", "qps1", "qps8" or "exactP50Ms".</param>
/// <param name="LowerIsBetter">True for a latency metric.</param>
/// <param name="Rows">The rows in the order the consolidate command wrote them (median order).</param>
public sealed record BenchMetric( string Metric, bool LowerIsBetter, IReadOnlyList<BenchRow> Rows );

/// <summary>A text with the sources the consolidate command bound to it.</summary>
/// <param name="Kind">"results", "file", "doc" or "consolidated".</param>
/// <param name="Ref">Where the value lives (a JSON path, a repo path with a token, a saved page).</param>
/// <param name="Value">The value the sentence uses from that place.</param>
public sealed record BenchSource( string Kind, string Ref, string Value );

/// <summary>One sentence the page prints. Nothing else on the page is prose.</summary>
/// <param name="Slot">The slot name, "section" or "section.name"; the section decides where the page puts it.</param>
/// <param name="Text">The sentence.</param>
/// <param name="Sources">What it is bound to.</param>
/// <param name="Quote">True when the text is a recorded text quoted as it was written.</param>
public sealed record BenchSentence( string Slot, string Text, IReadOnlyList<BenchSource> Sources, bool Quote );

/// <summary>One key and its value, for blocks whose fields the page prints as they are.</summary>
/// <param name="Key">The field name.</param>
/// <param name="Value">The value as text.</param>
public sealed record BenchPair( string Key, string Value );

/// <summary>One move the threshold basis names: the largest change one engine, or one pair of engines, showed.</summary>
/// <param name="MoveBp">The move in basis points.</param>
/// <param name="Who">The engine, or the two engines of a pair.</param>
/// <param name="Runs">The two runs the move sits between.</param>
/// <param name="Values">The two figures.</param>
/// <param name="Differences">Every recorded difference between the two runs.</param>
public sealed record BenchMove( long MoveBp, string Who, IReadOnlyList<string> Runs, IReadOnlyList<string> Values, IReadOnlyList<string> Differences );

/// <summary>The largest moves of one kind, per metric.</summary>
/// <param name="Kind">The kind's name as the file gives it (e.g. "oneEngine", "pairSameSettings").</param>
/// <param name="Moves">The moves, one per metric when the file gives a per-metric object, else the moves listed.</param>
public sealed record BenchBasisMoves( string Kind, IReadOnlyList<( string Metric, BenchMove Move )> Moves );

/// <summary>One field of an engine's recorded setup that differs between the runs before and after a change.</summary>
/// <param name="Field">The field's name ("engine", "index", "durability", ...).</param>
/// <param name="Before">The text the earlier runs recorded.</param>
/// <param name="After">The text the later runs recorded.</param>
public sealed record BenchSetupChange( string Field, string Before, string After );

/// <summary>The largest move an engine, or a pair, showed between runs on the two sides of a change of recorded setup, with the metric it was on.</summary>
/// <param name="Metric">The metric the move is on.</param>
/// <param name="Move">The move: its size, the engine or pair, the two runs, the two figures and the recorded differences between the runs.</param>
public sealed record BenchSplitMove( string Metric, BenchMove Move );

/// <summary>
/// One change of an engine's recorded setup that the threshold basis does not compare across, with the move it hides and the threshold the basis would give if
/// it were counted.
/// </summary>
/// <param name="Target">The engine.</param>
/// <param name="Changes">The fields that differ, each with the text before and after.</param>
/// <param name="BeforeSessions">The sessions of the runs that recorded the earlier setup.</param>
/// <param name="BeforeRuns">The runs that recorded the earlier setup.</param>
/// <param name="AfterSessions">The sessions of the runs that recorded the later setup.</param>
/// <param name="AfterRuns">The runs that recorded the later setup.</param>
/// <param name="OneEngine">The largest one-engine move across the change, or null when the file gives none.</param>
/// <param name="Pair">The largest pair move across the change, or null when the file gives none.</param>
/// <param name="TBpIfCounted">The threshold in basis points the basis would give with the change counted, or null.</param>
public sealed record BenchSetupSplit( string Target, IReadOnlyList<BenchSetupChange> Changes, IReadOnlyList<string> BeforeSessions, IReadOnlyList<string> BeforeRuns,
   IReadOnlyList<string> AfterSessions, IReadOnlyList<string> AfterRuns, BenchSplitMove? OneEngine, BenchSplitMove? Pair, long? TBpIfCounted );

/// <summary>One run that counts towards the threshold.</summary>
/// <param name="Folder">The run's folder name (untrusted text).</param>
/// <param name="Seed">The run's seed, or null.</param>
/// <param name="Conditions">The conditions the file records for it (turbo, uncore, warm-up and so on), as they are written.</param>
/// <param name="Session">The name of the session of earlier runs the run belongs to (for example "v5"), or null when the file does not give it.</param>
/// <param name="StartedUtc">When the run started, as the file wrote it, or null.</param>
public sealed record BenchBasisRun( string Folder, long? Seed, IReadOnlyList<BenchPair> Conditions, string? Session = null, string? StartedUtc = null );

/// <summary>The data the threshold rests on.</summary>
/// <param name="Runs">The runs that count towards it.</param>
/// <param name="LeftOut">Folders that were not used, each with its reason.</param>
/// <param name="Exclusions">The exclusions applied, each as its recorded fields.</param>
/// <param name="Moves">The largest moves by kind.</param>
/// <param name="NoMachineControl">The largest moves seen without machine control.</param>
/// <param name="MaxBp">The largest move in basis points, or null.</param>
/// <param name="TBp">The threshold in basis points, or null.</param>
/// <param name="SetupSplits">The changes of an engine's recorded setup the basis does not compare across; empty when the file lists none.</param>
public sealed record BenchBasis( IReadOnlyList<BenchBasisRun> Runs, IReadOnlyList<IReadOnlyList<BenchPair>> LeftOut, IReadOnlyList<IReadOnlyList<BenchPair>> Exclusions,
   IReadOnlyList<BenchBasisMoves> Moves, IReadOnlyList<BenchBasisMoves> NoMachineControl, long? MaxBp, long? TBp, IReadOnlyList<BenchSetupSplit>? SetupSplits = null )
{
   #region Public Methods

   /// <summary>The changes of recorded setup the basis does not compare across; never null.</summary>
   public IReadOnlyList<BenchSetupSplit> Splits => SetupSplits ?? Array.Empty<BenchSetupSplit>();

   #endregion Public Methods
}

/// <summary>One engine's move between two sessions on one metric.</summary>
/// <param name="Target">The engine.</param>
/// <param name="Metric">The metric.</param>
/// <param name="MoveBp">The move in basis points.</param>
public sealed record BenchDriftEntry( string Target, string Metric, double MoveBp );

/// <summary>Two engines and the metric they were compared on.</summary>
/// <param name="Metric">The metric.</param>
/// <param name="A">The first engine.</param>
/// <param name="B">The second engine.</param>
/// <param name="MinRatioBp">The smaller of the two sessions' ratios in basis points, or null when the list has no ratio.</param>
/// <param name="Session">For an order that held in one session only, the session it held in; null for any other list.</param>
public sealed record BenchPairRef( string Metric, string A, string B, long? MinRatioBp, string? Session = null );

/// <summary>How far the figures moved between two sessions.</summary>
/// <param name="From">The first session's name.</param>
/// <param name="To">The second session's name.</param>
/// <param name="PerTarget">Every engine's move on every metric.</param>
/// <param name="MedianAbsMoveBp">The median absolute move in basis points, or null.</param>
/// <param name="Largest">The largest move as the file describes it, as text, or null.</param>
/// <param name="UnconfirmedOrders">Orders that held in the first session and not in both.</param>
/// <param name="CloseToLine">Pairs near the line that separates two engines.</param>
/// <param name="OnLine">Ordered pairs that clear the line by a hair: the orders the report publishes that a little drift would remove.</param>
public sealed record BenchDrift( string? From, string? To, IReadOnlyList<BenchDriftEntry> PerTarget, double? MedianAbsMoveBp, string? Largest,
   IReadOnlyList<BenchPairRef> UnconfirmedOrders, IReadOnlyList<BenchPairRef> CloseToLine, IReadOnlyList<BenchPairRef>? OnLine = null )
{
   #region Public Methods

   /// <summary>The ordered pairs on the line; never null.</summary>
   public IReadOnlyList<BenchPairRef> OnLinePairs => OnLine ?? Array.Empty<BenchPairRef>();

   #endregion Public Methods
}

/// <summary>A clock warning a v7 report recorded and the consolidated set dropped, with the recomputed figures.</summary>
/// <param name="Run">The run the warning was recorded for (a folder name or a path ending in one).</param>
/// <param name="Text">The warning as recorded.</param>
/// <param name="EngineMedianMhz">The median MHz of the engine CPUs, or null.</param>
/// <param name="ClientMedianMhz">The median MHz of the client CPUs, or null.</param>
/// <param name="Target">The engine the warning was recorded for, or null when the file does not say.</param>
/// <param name="Pass">The pass the warning was recorded for, or null when the file does not say.</param>
public sealed record BenchDroppedWarning( string Run, string Text, double? EngineMedianMhz, double? ClientMedianMhz, string? Target = null, string? Pass = null );

/// <summary>What the clock did during the sessions.</summary>
/// <param name="PerSession">Each session's clock figures as the file gives them.</param>
/// <param name="Dropped">The warnings the set dropped.</param>
public sealed record BenchClock( IReadOnlyList<( string Session, IReadOnlyList<BenchPair> Fields )> PerSession, IReadOnlyList<BenchDroppedWarning> Dropped );

/// <summary>One container image the sessions used.</summary>
/// <param name="Target">The engine.</param>
/// <param name="Id">The image id.</param>
/// <param name="LastTagTimeUtc">When the tag was last set, or null.</param>
/// <param name="BeforeFirstStart">True when that time precedes the first session's first start; null when not given.</param>
public sealed record BenchImage( string Target, string Id, string? LastTagTimeUtc, bool? BeforeFirstStart );

/// <summary>One engine setting as the runs recorded it.</summary>
/// <param name="Key">The setting's name.</param>
/// <param name="Value">Its value.</param>
/// <param name="How">How it was read (a command or an API) or where it was set (a repository file and a token), as the run wrote it.</param>
public sealed record BenchSetting( string Key, string Value, string How );

/// <summary>The engine settings of one engine in one session.</summary>
/// <param name="Target">The engine.</param>
/// <param name="Session">The session they were read in, or null when the file does not say.</param>
/// <param name="Settings">The settings.</param>
public sealed record BenchEngineSettings( string Target, string? Session, IReadOnlyList<BenchSetting> Settings );

/// <summary>One setting that controls the effort of each search of an engine (for example a candidate list size), as the runs recorded it.</summary>
/// <param name="Text">The setting as text, for example "hnsw.ef_search=100".</param>
/// <param name="Source">Where the file says it comes from, or null when it gives none.</param>
public sealed record BenchEffort( string Text, string? Source );

/// <summary>One fact about an engine, with where it comes from.</summary>
/// <param name="Kind">"index", "search", "storage", "protocol", "cap" or "set-by-setup".</param>
/// <param name="Text">The fact.</param>
/// <param name="Source">Where it comes from.</param>
/// <param name="Confidence">The label the report gives the fact (for example "measured", "recorded", "documented", "set-by-code", "checked" or "recorded (engine's own report)"). The page prints it as written and never maps it to another label: a label the page upgraded would say more than the report does.</param>
/// <param name="Mode">For a search fact, the mode it states ("approximate" or "exact", as written); null for any other fact and for a file that gives none.</param>
/// <param name="Where">Where in the runs the fact was found (for example "in all 6 run(s) selected, at targets[redis].index"), or null.</param>
public sealed record BenchFact( string Kind, string Text, string Source, string Confidence, string? Mode = null, string? Where = null );

/// <summary>What one engine cost per search, as measured.</summary>
/// <param name="EngineCpuMs1">Engine CPU milliseconds per search with one searcher, or null.</param>
/// <param name="EngineCpuMs8">Engine CPU milliseconds per search with eight searchers, or null.</param>
/// <param name="ClientCpuMs1">Client CPU milliseconds per search with one searcher, or null.</param>
/// <param name="ClientCpuMs8">Client CPU milliseconds per search with eight searchers, or null.</param>
/// <param name="EngineCpusBusyAt8">How many engine CPUs were busy with eight searchers, or null.</param>
public sealed record BenchCosts( double? EngineCpuMs1, double? EngineCpuMs8, double? ClientCpuMs1, double? ClientCpuMs8, double? EngineCpusBusyAt8 );

/// <summary>One engine's row of the facts and costs table.</summary>
/// <param name="Target">The engine.</param>
/// <param name="Facts">Its facts.</param>
/// <param name="Costs">Its costs.</param>
/// <param name="Effort">The settings that control the effort of each of its searches; empty when the file gives none for the engine.</param>
public sealed record BenchWhy( string Target, IReadOnlyList<BenchFact> Facts, BenchCosts Costs, IReadOnlyList<BenchEffort>? Effort = null )
{
   #region Public Methods

   /// <summary>The settings that control the effort of each search; never null.</summary>
   public IReadOnlyList<BenchEffort> SearchEffort => Effort ?? Array.Empty<BenchEffort>();

   #endregion Public Methods
}

/// <summary>One run a consolidated set uses and the part it plays there.</summary>
/// <param name="Folder">The run's folder name as the file writes it (untrusted text: whoever links to it checks it first).</param>
/// <param name="Role">The label of the part: the session's name for a run of a compared session, "basis" or "basis" and the session's name for a run that feeds the threshold.</param>
/// <param name="Session">The session the run belongs to, or null when the file does not give one for a basis run.</param>
/// <param name="IsBasis">True for a run that feeds the threshold, false for a run of a compared session.</param>
public sealed record BenchUsedRun( string Folder, string Role, string? Session, bool IsBasis );

/// <summary>One engine's recall as hits per run.</summary>
/// <param name="Target">The engine.</param>
/// <param name="Hits">Hits per run, by session name.</param>
/// <param name="Of">The number of hits a perfect run has.</param>
/// <param name="Differs">True when any two runs differ.</param>
public sealed record BenchRecall( string Target, IReadOnlyDictionary<string, IReadOnlyList<long>> Hits, long Of, bool Differs );

/// <summary>What the consolidate command's own checks found.</summary>
/// <param name="G2Stopped">True when the second guard (an order that held in the first session and is reversed in the second) stopped the set.</param>
/// <param name="G2Pairs">The pairs that stopped it, each as text.</param>
/// <param name="G3Targets">Targets whose recorded setup differs between sessions, each with the differing fields.</param>
/// <param name="StoppedBy">The other places the file marks the set stopped (the first or third guard, or the file's own status), each as the field's path; empty when none.</param>
public sealed record BenchGuards( bool G2Stopped, IReadOnlyList<string> G2Pairs, IReadOnlyList<( string Target, IReadOnlyList<string> Fields )> G3Targets, IReadOnlyList<string>? StoppedBy = null )
{
   #region Public Methods

   /// <summary>True when any guard or the file's status marks the set stopped.</summary>
   public bool AnyStopped => G2Stopped || StoppedBy is { Count: > 0 };

   #endregion Public Methods
}

/// <summary>The sentence audit's result.</summary>
/// <param name="SentencesChecked">How many sentences the audit checked.</param>
/// <param name="Failures">Failures it found (a published set has none).</param>
/// <param name="FactsSha256">SHA-256 of the facts file used, or null.</param>
/// <param name="ExclusionsSha256">SHA-256 of the exclusions file used, or null.</param>
/// <param name="ObserverSha256">SHA-256 of the observer's summary used, or null.</param>
/// <param name="FactsChecked">How many engine facts the audit checked, or null.</param>
/// <param name="RowSentencesChecked">How many of the sentences it checked were row notes, or null.</param>
public sealed record BenchAudit( long SentencesChecked, IReadOnlyList<string> Failures, string? FactsSha256, string? ExclusionsSha256, string? ObserverSha256 = null, long? FactsChecked = null, long? RowSentencesChecked = null );

/// <summary>
/// A consolidated.json of the v8 shape, read into the parts the page prints: sessions, the four metric
/// tables, the threshold basis, drift, recall, the clock, the machine, the images, the facts and costs,
/// and every sentence with its sources.
/// Why a model apart from the file: the page must print what the file says and nothing else, so the
/// reader validates the file once and the renderer only formats fields; a file that does not hold
/// together (a row order that contradicts its own separations, a headline on a stopped set) fails at
/// the reader and the page shows the error, not a quietly wrong table.
/// </summary>
/// <param name="Sessions">The sessions compared.</param>
/// <param name="TBp">The threshold in basis points.</param>
/// <param name="Ratio">The threshold as a ratio (1.35 for 3500 bp).</param>
/// <param name="Metrics">The metric tables.</param>
/// <param name="Guards">The guards' results.</param>
/// <param name="Basis">The threshold basis, or null.</param>
/// <param name="Drift">The drift between sessions, or null for a one-session set.</param>
/// <param name="Recall">Recall per engine.</param>
/// <param name="Clock">The clock figures, or null.</param>
/// <param name="Machine">The machine's fields as the file gives them, or empty.</param>
/// <param name="Images">The images used.</param>
/// <param name="Why">The facts and costs table.</param>
/// <param name="Sentences">Every sentence.</param>
/// <param name="Audit">The audit result, or null.</param>
/// <param name="EngineSettings">The settings read from each running engine or set from a repository file, by engine; none when the runs recorded none.</param>
/// <param name="UnreadFields">The paths of fields the file holds that the page neither draws nor lists as deliberately left out (see <see cref="BenchConsolidatedFields"/>); none for a file the page reads whole.</param>
public sealed record BenchConsolidated( IReadOnlyList<BenchSession> Sessions, long TBp, double Ratio, IReadOnlyList<BenchMetric> Metrics, BenchGuards Guards, BenchBasis? Basis,
   BenchDrift? Drift, IReadOnlyList<BenchRecall> Recall, BenchClock? Clock, IReadOnlyList<BenchPair> Machine, IReadOnlyList<BenchImage> Images, IReadOnlyList<BenchWhy> Why,
   IReadOnlyList<BenchSentence> Sentences, BenchAudit? Audit, IReadOnlyList<BenchEngineSettings>? EngineSettings = null, IReadOnlyList<string>? UnreadFields = null )
{
   #region Public Methods

   /// <summary>True when the set is marked stopped: the page then prints no headline and no tables.</summary>
   public bool Stopped => Guards.AnyStopped;

   /// <summary>
   /// Every run the set uses with the part it plays: a run of a compared session carries the session's name; a run that feeds the threshold is
   /// labelled "basis", followed by its session's name when the file gives it and the run is not also a run of that same compared session (a run that is
   /// both reads "v7, basis", not "v7, basis v7"). Folder names are the file's text and are checked by whoever links to them.
   /// </summary>
   public IEnumerable<BenchUsedRun> UsedRunRefs
   {
      get
      {
         List<BenchUsedRun> claim = Sessions.SelectMany( s => s.Runs.Select( r => new BenchUsedRun( r.Folder, s.Name, s.Name, false ) ) ).ToList();
         IEnumerable<BenchUsedRun> basis = Basis?.Runs.Select( r => new BenchUsedRun( r.Folder, r.Session == null || claim.Any( c => c.Folder == r.Folder && c.Session == r.Session ) ? "basis" : $"basis {r.Session}", r.Session, true ) )
            ?? Enumerable.Empty<BenchUsedRun>();
         return claim.Concat( basis );
      }
   }

   #endregion Public Methods
}
