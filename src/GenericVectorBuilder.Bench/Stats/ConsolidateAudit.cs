using System.Text.Json;
using System.Text.Json.Serialization;
using GenericVectorBuilder.Bench.Report;

namespace GenericVectorBuilder.Bench.Stats;

/// <summary>
/// The two checks that stand between the consolidation and a written report: the fact sheet is
/// validated against the claim runs (P5's <see cref="EngineFactSheet.Validate"/>), and every
/// sentence is audited against the raw files (P5's <see cref="SentenceAudit.Check"/>) through a
/// resolver over the raw results.json files, the repository and the report itself.
/// Why raw files: a sentence must hold against what the runs recorded, not against this tool's
/// model of them, so a bug in the model cannot hide a false sentence.
/// Any failure is a <see cref="ConsolidateRefusal"/>, and the command writes nothing.
/// </summary>
public static class ConsolidateAudit
{
   #region Data Members

   /// <summary>The serializer settings of consolidated.json; consolidated sources are resolved in exactly this text.</summary>
   public static readonly JsonSerializerOptions JSON = new()
   {
      WriteIndented = true,
      PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
      NumberHandling = JsonNumberHandling.AllowNamedFloatingPointLiterals,
      DefaultIgnoreCondition = JsonIgnoreCondition.Never,
      Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
   };

   #endregion Data Members

   #region Public Methods

   /// <summary>
   /// Validates the fact rows of every target the claim runs hold against the claim runs and the repository.
   /// Why every target of the runs and not the reported ones: the fact sheet's coverage rule counts the
   /// runs' targets, and a fact that fails for a target left out of the tables is still a false fact.
   /// </summary>
   /// <param name="input">The input (facts and repository).</param>
   /// <param name="claim">The claim runs, raw.</param>
   /// <param name="reported">Targets with rows.</param>
   /// <returns>The resolved rows of the reported targets.</returns>
   /// <exception cref="ConsolidateRefusal">The sheet has a problem.</exception>
   public static IReadOnlyList<ResolvedFact> ValidateFacts( ConsolidateInput input, IReadOnlyList<FactRun> claim, IReadOnlyList<string> reported )
   {
      var held = new HashSet<string>( claim.SelectMany( TargetNames ), StringComparer.Ordinal );
      List<EngineFactRow> rows = input.Facts.Where( f => held.Contains( f.Target ) ).ToList();
      FactValidation outcome;
      try
      {
         outcome = EngineFactSheet.Validate( rows, claim, input.RepoRoot );
      }
      catch( Exception ex ) when( ex is IOException or InvalidDataException or UnauthorizedAccessException )
      {
         throw new ConsolidateRefusal( $"the fact sheet could not be checked: {ex.Message}" );
      }

      if( !outcome.Ok )
      {
         throw new ConsolidateRefusal( $"{outcome.Problems.Count} problem(s) in the engine fact sheet against the claim runs: " + string.Join( " | ", outcome.Problems ) );
      }

      return outcome.Resolved.Where( r => reported.Contains( r.Row.Target ) ).ToList();
   }

   /// <summary>
   /// Audits every sentence; on success stores them in the report with the audit counts.
   /// </summary>
   /// <param name="report">The report, every figure and why row filled.</param>
   /// <param name="sentences">Every sentence the report prints.</param>
   /// <param name="raw">The raw runs.</param>
   /// <param name="repoRoot">Repository root.</param>
   /// <param name="notes">The run-page notes list: a sentence that prints a statement it marks, without the correction, fails the audit.</param>
   /// <exception cref="ConsolidateRefusal">A sentence failed; the message lists every failure.</exception>
   public static void CheckSentences( ConsolidatedReport report, IReadOnlyList<Sentence> sentences, RawRuns raw, string repoRoot, RunPageNoteList notes )
   {
      using JsonDocument doc = JsonDocument.Parse( JsonSerializer.Serialize( report, JSON ) );
      var resolver = new ReportResolver( repoRoot, raw, doc.RootElement );
      IReadOnlyList<AuditFailure> failures = SentenceAudit.Check( sentences, resolver ).Concat( Marked( sentences, notes ) ).ToList();
      if( failures.Count > 0 )
      {
         throw new ConsolidateRefusal( $"{failures.Count} sentence audit failure(s); nothing was written:{Environment.NewLine}{SentenceAudit.Format( failures )}" );
      }

      List<Sentence> onRows = sentences.Where( s => s.Slot.StartsWith( "row.", StringComparison.Ordinal ) || s.Slot.StartsWith( "flag.", StringComparison.Ordinal ) ).ToList();
      Attach( report, onRows );
      report.Sentences = sentences.Except( onRows ).Select( s => new SentenceRecord { Slot = s.Slot, Text = s.Text, Sources = Records( s ) } ).ToList();
      report.Audit.SentencesChecked = sentences.Count;
      report.Audit.RowSentencesChecked = onRows.Count;
   }

   #endregion Public Methods

   #region Private Methods

   /// <summary>
   /// The sentences that print a statement the run-page notes list marks as false, misleading or unbacked without carrying the correction.
   /// Why every sentence, quotes included: a verbatim quote of a recorded field is exactly where a marked statement comes from, and the run page that corrects it must not be contradicted by the summary.
   /// </summary>
   /// <param name="sentences">Every sentence the report prints.</param>
   /// <param name="notes">The list.</param>
   /// <returns>One failure per sentence and statement.</returns>
   private static IEnumerable<AuditFailure> Marked( IReadOnlyList<Sentence> sentences, RunPageNoteList notes )
   {
      foreach( Sentence sentence in sentences )
      {
         foreach( RunPageNote note in notes.MarkedIn( sentence.Text ).Where( n => !sentence.Text.Contains( n.Text, StringComparison.Ordinal ) ) )
         {
            yield return new AuditFailure( sentence.Slot, "marked", $"the sentence prints \"{note.Statement}\", which the run-page notes list marks as {note.Kind} ({RunPageNoteList.FILE}); the report may not print it without the correction: {sentence.Text}" );
         }
      }
   }

   /// <summary>
   /// Puts the audited row notes and flag texts, with their sources, on their rows: "row.&lt;metric&gt;.&lt;target&gt;.note" and
   /// "flag.&lt;metric&gt;.&lt;target&gt;.&lt;n&gt;".
   /// Why on the rows: the page prints them in the row and its flag list, so a copy in sentences[] would print twice.
   /// </summary>
   /// <param name="report">The report.</param>
   /// <param name="onRows">The row and flag sentences.</param>
   /// <exception cref="InvalidOperationException">A slot names no row or flag (a bug guard).</exception>
   private static void Attach( ConsolidatedReport report, IEnumerable<Sentence> onRows )
   {
      foreach( Sentence s in onRows )
      {
         string[] part = s.Slot.Split( '.' );
         MetricRow row = report.Metrics.FirstOrDefault( m => m.Metric == part[1] )?.Rows.FirstOrDefault( r => r.Target == part[2] )
            ?? throw new InvalidOperationException( $"slot {s.Slot} names no row" );
         if( part[0] == "row" )
         {
            row.Note = s.Text;
            row.NoteSources = Records( s );
            continue;
         }

         RowFlag flag = row.Flags[int.Parse( part[3], System.Globalization.CultureInfo.InvariantCulture )];
         flag.Text = s.Text;
         flag.Sources = Records( s );
      }
   }

   /// <summary>
   /// A sentence's sources as the report writes them.
   /// </summary>
   /// <param name="s">The sentence.</param>
   /// <returns>The records.</returns>
   private static List<SourceRecord> Records( Sentence s )
   {
      return s.Sources.Select( src => new SourceRecord { Kind = src.Kind, Ref = src.Ref, Value = src.Value } ).ToList();
   }

   /// <summary>
   /// The target names of a raw run.
   /// </summary>
   /// <param name="run">The raw run.</param>
   /// <returns>Names.</returns>
   private static IEnumerable<string> TargetNames( FactRun run )
   {
      return run.Root.TryGetProperty( "targets", out JsonElement list ) && list.ValueKind == JsonValueKind.Array
         ? list.EnumerateArray().Select( t => t.TryGetProperty( "name", out JsonElement n ) && n.ValueKind == JsonValueKind.String ? n.GetString()! : string.Empty ).Where( n => n.Length > 0 ).ToList()
         : Enumerable.Empty<string>();
   }

   #endregion Private Methods
}

/// <summary>
/// The raw results.json of every run a sentence may cite: the claim runs, and every run (claim,
/// basis and the other runs of the pipeline) for references that name one run with "@folder".
/// Why two lists: a reference without a folder means "every claim run agrees", which must not
/// include a basis run of another method.
/// </summary>
public sealed class RawRuns : IDisposable
{
   #region Data Members

   private readonly List<FactRun> _owned = new();

   #endregion Data Members

   #region Public Methods

   /// <summary>The claim runs.</summary>
   public List<FactRun> Claim { get; } = new();

   /// <summary>Every run, each once.</summary>
   public List<FactRun> All { get; } = new();

   /// <summary>
   /// Loads the raw files.
   /// </summary>
   /// <param name="sessions">Claim sessions.</param>
   /// <param name="input">Input (basis sessions and other runs).</param>
   /// <returns>The loaded runs; the caller disposes them.</returns>
   /// <exception cref="ConsolidateRefusal">A file cannot be read again.</exception>
   public static RawRuns Load( IReadOnlyList<ClaimSession> sessions, ConsolidateInput input )
   {
      var raw = new RawRuns();
      try
      {
         foreach( RunResult run in sessions.SelectMany( s => s.Runs ) )
         {
            raw.Claim.Add( raw.Read( run ) );
         }

         raw.All.AddRange( raw.Claim );
         foreach( RunResult run in input.BasisSessions.SelectMany( s => s.Runs ).Concat( input.OtherRuns ) )
         {
            raw.All.Add( raw.Read( run ) );
         }

         return raw;
      }
      catch( Exception ex ) when( ex is IOException or InvalidDataException or UnauthorizedAccessException )
      {
         raw.Dispose();
         throw new ConsolidateRefusal( $"a results.json could not be read again for the audit: {ex.Message}" );
      }
   }

   #endregion Public Methods

   #region Private Methods

   /// <summary>
   /// Reads one run's results.json.
   /// </summary>
   /// <param name="run">The run.</param>
   /// <returns>The raw run.</returns>
   private FactRun Read( RunResult run )
   {
      FactRun loaded = FactRun.Load( Path.Combine( run.Folder, "results.json" ) );
      _owned.Add( loaded );
      return loaded;
   }

   #endregion Private Methods

   #region IDisposable

   /// <summary>
   /// Releases every parsed file.
   /// </summary>
   public void Dispose()
   {
      _owned.ForEach( r => r.Dispose() );
      _owned.Clear();
   }

   #endregion IDisposable
}

/// <summary>
/// The resolver the audit uses: P5's <see cref="SourceResolver"/> over the claim runs for a results,
/// quote or absent reference without "@folder", and over every run for one that names a folder; file,
/// doc and consolidated references resolve the same either way.
/// </summary>
public sealed class ReportResolver : ISourceResolver
{
   #region Data Members

   private readonly SourceResolver _claim;
   private readonly SourceResolver _all;

   #endregion Data Members

   #region Constructor

   /// <summary>
   /// Creates the resolver.
   /// </summary>
   /// <param name="repoRoot">Repository root.</param>
   /// <param name="raw">The raw runs.</param>
   /// <param name="consolidated">The report as serialized.</param>
   public ReportResolver( string repoRoot, RawRuns raw, JsonElement consolidated )
   {
      _claim = new SourceResolver( repoRoot, raw.Claim, consolidated );
      _all = new SourceResolver( repoRoot, raw.All, consolidated );
   }

   #endregion Constructor

   #region Public Methods

   /// <summary>
   /// Resolves a source with the claim-run resolver or the every-run resolver.
   /// </summary>
   /// <param name="source">The source.</param>
   /// <returns>What it resolves to.</returns>
   public SourceResolution Resolve( SentenceSource source )
   {
      bool named = source.Ref.StartsWith( "results@", StringComparison.Ordinal ) || source.Ref.StartsWith( "absent:results@", StringComparison.Ordinal );
      return named ? _all.Resolve( source ) : _claim.Resolve( source );
   }

   #endregion Public Methods
}
