using System.Text.RegularExpressions;
using GenericVectorBuilder.Bench.Report;

namespace GenericVectorBuilder.Bench.Stats;

/// <summary>
/// Checks the list that classifies the recorded texts the report prints, against the claim runs and the repository, before any sentence is
/// written, and records every clause it classified in the report (recordedTexts) so a reader and a test can see how each printed clause is backed.
/// What is checked: every durability and index text of every reported target in every claim run is covered by the entry of the list (a clause the list
/// does not cover stops the report; for run notes it is printed as unverified and counted); every basis source of an entry resolves again (a results
/// token is found in every claim run, a code, compose, log or test line is found in its file, a quote is found in one text node of its saved page); every
/// documentation row an entry names holds in its saved page; every file an entry is contradicted by, and every piece of evidence an entry names, exists.
/// Why before the sentences: a clause with no class or a basis that no longer resolves must stop the report, not print.
/// </summary>
public static class ConsolidateClasses
{
   #region Data Members

   /// <summary>The recorded fact sheet's label for a fact the engine reported about itself that this tool did not check.</summary>
   public const string RECORDED = "recorded";

   /// <summary>How many characters of a clause the report does not print its table row shows.</summary>
   private const int ABBREVIATED_CHARS = 24;

   /// <summary>How many text nodes after a documentation row's name its value may sit in.</summary>
   private const int DOC_ROW_WINDOW = 14;

   private static readonly Regex FACT_FIELD = new( @"^results[^:]*:targets\[(?<t>[^\]]+)\]\.(?<f>[A-Za-z]+)#(?<token>.+)$", RegexOptions.Compiled | RegexOptions.CultureInvariant | RegexOptions.Singleline, TimeSpan.FromSeconds( 5 ) );

   #endregion Data Members

   #region Public Methods

   /// <summary>
   /// Checks the list against the claim runs and the repository and fills the report's recorded texts.
   /// </summary>
   /// <param name="input">The input, which holds the list and the repository root.</param>
   /// <param name="sessions">The claim sessions.</param>
   /// <param name="claim">The raw claim runs.</param>
   /// <param name="report">The report, whose targets are filled; its recorded texts and unlisted clauses are filled here.</param>
   /// <exception cref="ConsolidateRefusal">A clause the list must classify is not covered, or a basis, documentation row, contradicting file or piece of evidence does not hold.</exception>
   public static void Validate( ConsolidateInput input, IReadOnlyList<ClaimSession> sessions, IReadOnlyList<FactRun> claim, ConsolidatedReport report )
   {
      var problems = new List<string>();
      var spans = new List<ClassSpan>();
      foreach( RunResult run in sessions.SelectMany( s => s.Runs ) )
      {
         foreach( string target in report.Targets )
         {
            foreach( string field in RecordedTextClasses.TARGET_FIELDS )
            {
               string? text = TextOf( run.Find( target ), field );
               if( text != null )
               {
                  Collect( input.Classes, target, field, text, run.Name, spans, report, problems );
               }
            }
         }

         foreach( string note in run.Notes.Where( n => !n.StartsWith( "WARNING", StringComparison.Ordinal ) ) )
         {
            Collect( input.Classes, string.Empty, RecordedTextClasses.NOTES_FIELD, note, run.Name, spans, report, problems );
         }
      }

      CheckSpans( input, claim, spans, problems );
      if( problems.Count > 0 )
      {
         throw new ConsolidateRefusal( $"{problems.Count} problem(s) in {RecordedTextClasses.FILE}: " + string.Join( " | ", problems.Distinct( StringComparer.Ordinal ) ) );
      }

      report.RecordedTexts = Records( input, sessions[^1].Runs[0], report.Targets );
   }

   /// <summary>
   /// The class of a fact row and the label the report prints for it. A measured fact, and a fact that is the engine's own report, was read back; a documented or
   /// set-by-code fact rests on a saved source; a checked fact is an absence this tool checked; a recorded fact rests on a clause of the recorded index or durability text, so it
   /// takes the weakest class of the clauses its source token lies in, and its label says so.
   /// </summary>
   /// <param name="sheet">The classes list.</param>
   /// <param name="confidence">The fact sheet's confidence label.</param>
   /// <param name="source">The fact's source string.</param>
   /// <param name="run">A claim run, whose recorded texts hold the token.</param>
   /// <returns>The class and the label to print.</returns>
   public static ( string Class, string Label ) Classify( ClassSheet sheet, string confidence, string source, RunResult run )
   {
      switch( confidence )
      {
         case "measured" or EngineFactSheet.OWN_REPORT:
            return ( TextClass.READ_BACK, confidence );
         case "documented" or "set-by-code":
            return ( TextClass.DOCUMENTED, confidence );
         case EngineFactSheet.CHECKED:
            return ( TextClass.MEASURED, confidence );
      }

      string cls = RecordedClass( sheet, source, run );
      return ( cls, $"{RECORDED}, {cls}" );
   }

   /// <summary>
   /// The clauses of a recorded text the report does not print, each with the reason: a clause dropped by name, a clause about targets this report does not have, and a clause a repository file contradicts.
   /// </summary>
   /// <param name="span">The span.</param>
   /// <param name="targets">The targets of the report.</param>
   /// <param name="repoRoot">Repository root.</param>
   /// <returns>The reason the span is not printed, or null when it is printed.</returns>
   public static string? NotPrinted( ClassSpan span, IReadOnlyCollection<string> targets, string repoRoot )
   {
      if( span.Drop != null )
      {
         return span.Drop;
      }

      if( span.DropWhenAbsent.Count > 0 && !span.DropWhenAbsent.Any( targets.Contains ) )
      {
         return "absent-targets";
      }

      ContradictedBy? c = span.Contradicted;
      return c != null && File.Exists( Path.Combine( repoRoot, c.File ) ) && ConsolidateTextDurability.Variables( repoRoot, c.File, c.Prefix ).Count > 0 ? "contradicted" : null;
   }

   #endregion Public Methods

   #region Private Methods

   /// <summary>
   /// Splits one recorded text, notes the spans it used and the clauses the list did not cover.
   /// </summary>
   /// <param name="sheet">The list.</param>
   /// <param name="target">Target, or empty for a run note.</param>
   /// <param name="field">Field.</param>
   /// <param name="text">The recorded text.</param>
   /// <param name="run">The run, for messages.</param>
   /// <param name="spans">Receives the spans used.</param>
   /// <param name="report">The report, which receives the unlisted clauses.</param>
   /// <param name="problems">Receives what the list cannot cover.</param>
   private static void Collect( ClassSheet sheet, string target, string field, string text, string run, List<ClassSpan> spans, ConsolidatedReport report, List<string> problems )
   {
      try
      {
         foreach( TextPiece piece in sheet.Split( target, field, text ) )
         {
            spans.Add( piece.Span );
            if( piece.Unlisted )
            {
               report.Audit.UnlistedClauses.Add( $"{run} {( target.Length == 0 ? field : target + "." + field )}: {piece.Text}" );
            }
         }
      }
      catch( RecordedTextException ex )
      {
         problems.Add( $"{run}: {ex.Message}" );
      }
   }

   /// <summary>
   /// Resolves the basis of every span that was used, and the files and evidence the spans name.
   /// </summary>
   /// <param name="input">The input.</param>
   /// <param name="claim">The raw claim runs.</param>
   /// <param name="spans">The spans used.</param>
   /// <param name="problems">Receives what does not hold.</param>
   private static void CheckSpans( ConsolidateInput input, IReadOnlyList<FactRun> claim, List<ClassSpan> spans, List<string> problems )
   {
      var resolver = new SourceResolver( input.RepoRoot, claim );
      foreach( ClassSpan span in spans.Distinct() )
      {
         foreach( string basis in span.Basis )
         {
            CheckBasis( resolver, basis, problems );
         }

         foreach( DocRow row in span.DocRows )
         {
            CheckDocRow( input.RepoRoot, span, row, problems );
         }

         if( span.Contradicted != null && !File.Exists( Path.Combine( input.RepoRoot, span.Contradicted.File ) ) )
         {
            problems.Add( $"the file {span.Contradicted.File}, which contradicts a clause, does not exist" );
         }

         if( span.Evidence != null && ( !input.Classes.Evidence.TryGetValue( span.Evidence, out string? path ) || !File.Exists( Path.Combine( input.RepoRoot, path ) ) ) )
         {
            problems.Add( $"the evidence '{span.Evidence}' of a clause has no saved file" );
         }
      }
   }

   /// <summary>
   /// Resolves one basis source.
   /// </summary>
   /// <param name="resolver">Reads the sources.</param>
   /// <param name="basis">The basis, in the fact sheet's grammar.</param>
   /// <param name="problems">Receives what does not hold.</param>
   private static void CheckBasis( SourceResolver resolver, string basis, List<string> problems )
   {
      try
      {
         SourceResolution found = resolver.Resolve( SourceResolver.FromFactSource( basis ) );
         if( !found.Found )
         {
            problems.Add( $"basis {basis} does not resolve ({found.Detail})" );
         }
      }
      catch( FormatException ex )
      {
         problems.Add( $"basis {basis}: {ex.Message}" );
      }
   }

   /// <summary>
   /// Checks that a documentation row holds in a saved page that backs a span. In an HTML page the row's name node is followed, within a few text nodes, by the first node that is a plain
   /// number, and that is its value; in a Markdown source file the row is one line of a table, which holds the name in backticks and the value in a cell of its own.
   /// </summary>
   /// <param name="repoRoot">Repository root.</param>
   /// <param name="span">The span, whose basis names the saved pages.</param>
   /// <param name="row">The row.</param>
   /// <param name="problems">Receives what does not hold.</param>
   private static void CheckDocRow( string repoRoot, ClassSpan span, DocRow row, List<string> problems )
   {
      List<string> pages = span.Basis.Select( b => b.StartsWith( "doc:", StringComparison.Ordinal ) ? b[4..] : b ).Select( b => b[..Math.Max( 0, b.IndexOf( '#' ) )] )
         .Where( p => p.StartsWith( "design/engine-docs/", StringComparison.Ordinal ) && File.Exists( Path.Combine( repoRoot, p ) ) ).Distinct( StringComparer.Ordinal ).ToList();
      if( pages.Count == 0 )
      {
         problems.Add( $"documentation row {row.Name} names a page that is not a saved page in the basis of its clause" );
         return;
      }

      if( pages.Any( p => RowHolds( File.ReadAllText( Path.Combine( repoRoot, p ) ), p, row ) ) )
      {
         return;
      }

      problems.Add( $"{string.Join( " or ", pages )} holds no row {row.Name} with the value {row.Value}" );
   }

   /// <summary>
   /// Whether a saved page holds a documentation row.
   /// </summary>
   /// <param name="text">The page.</param>
   /// <param name="path">Its path, whose extension says whether it is HTML or a Markdown source.</param>
   /// <param name="row">The row.</param>
   /// <returns>True when the row holds.</returns>
   private static bool RowHolds( string text, string path, DocRow row )
   {
      var number = new Regex( @"^-?\d+$", RegexOptions.CultureInvariant, TimeSpan.FromSeconds( 5 ) );
      if( !path.EndsWith( ".html", StringComparison.OrdinalIgnoreCase ) )
      {
         var cell = new Regex( @"\|\s*" + Regex.Escape( row.Value ) + @"\s*\|", RegexOptions.CultureInvariant, TimeSpan.FromSeconds( 5 ) );
         return text.Split( '\n' ).Any( line => line.Contains( $"`{row.Name}`", StringComparison.Ordinal ) && line.TrimStart().StartsWith( '|' ) && cell.IsMatch( line ) );
      }

      IReadOnlyList<string> nodes = SourceResolver.PageNodes( text );
      for( int i = 0; i < nodes.Count; i++ )
      {
         if( nodes[i] == row.Name && nodes.Skip( i + 1 ).Take( DOC_ROW_WINDOW ).FirstOrDefault( n => number.IsMatch( n ) ) == row.Value )
         {
            return true;
         }
      }

      return false;
   }

   /// <summary>
   /// The recorded text of a target.
   /// </summary>
   /// <param name="target">The target's result, or null.</param>
   /// <param name="field">durability or index.</param>
   /// <returns>The text, or null.</returns>
   private static string? TextOf( TargetResult? target, string field )
   {
      return field == "durability" ? target?.Durability : target?.Index;
   }

   /// <summary>
   /// The records of the recorded texts the report prints: each reported target's durability and index text and the run notes, from the run the report quotes.
   /// </summary>
   /// <param name="input">The input.</param>
   /// <param name="run">The newest session's first run.</param>
   /// <param name="targets">The reported targets.</param>
   /// <returns>The records.</returns>
   private static List<RecordedTextRecord> Records( ConsolidateInput input, RunResult run, IReadOnlyList<string> targets )
   {
      var records = new List<RecordedTextRecord>();
      foreach( string target in targets )
      {
         foreach( string field in RecordedTextClasses.TARGET_FIELDS )
         {
            string? text = TextOf( run.Find( target ), field );
            if( text != null )
            {
               records.Add( Record( input, targets, target, field, run.Name, null, text ) );
            }
         }
      }

      for( int i = 0; i < run.Notes.Count; i++ )
      {
         if( !run.Notes[i].StartsWith( "WARNING", StringComparison.Ordinal ) )
         {
            records.Add( Record( input, targets, string.Empty, RecordedTextClasses.NOTES_FIELD, run.Name, i, run.Notes[i] ) );
         }
      }

      return records;
   }

   /// <summary>
   /// One record.
   /// </summary>
   /// <param name="input">The input.</param>
   /// <param name="targets">The reported targets.</param>
   /// <param name="target">Target, or empty.</param>
   /// <param name="field">Field.</param>
   /// <param name="run">Run folder.</param>
   /// <param name="note">The note's position, or null.</param>
   /// <param name="text">The text.</param>
   /// <returns>The record.</returns>
   private static RecordedTextRecord Record( ConsolidateInput input, IReadOnlyCollection<string> targets, string target, string field, string run, int? note, string text )
   {
      var record = new RecordedTextRecord { Target = target, Field = field, Run = run, Note = note };
      foreach( TextPiece piece in input.Classes.Split( target, field, text ) )
      {
         string? reason = NotPrinted( piece.Span, targets, input.RepoRoot );
         record.Clauses.Add( new RecordedClause { Text = reason == null || piece.Text.Length <= ABBREVIATED_CHARS ? piece.Text : piece.Text[..ABBREVIATED_CHARS] + "...", Length = piece.Text.Length, Class = piece.Span.Class, Basis = piece.Span.Basis.ToList(), Printed = reason == null, NotPrinted = reason, Unlisted = piece.Unlisted } );
      }

      return record;
   }

   /// <summary>
   /// The class of a recorded fact: the weakest class of the clauses of its recorded text that its source token lies in.
   /// </summary>
   /// <param name="sheet">The list.</param>
   /// <param name="source">The fact's source string.</param>
   /// <param name="run">A claim run.</param>
   /// <returns>The class; unverified when the token lies in no classified clause.</returns>
   private static string RecordedClass( ClassSheet sheet, string source, RunResult run )
   {
      Match m = FACT_FIELD.Match( source );
      if( !m.Success )
      {
         return TextClass.UNVERIFIED;
      }

      string? text = TextOf( run.Find( m.Groups["t"].Value ), m.Groups["f"].Value );
      if( text == null || !RecordedTextClasses.TARGET_FIELDS.Contains( m.Groups["f"].Value ) )
      {
         return TextClass.UNVERIFIED;
      }

      string collapsed = RecordedTextClasses.Collapse( text );
      string token = RecordedTextClasses.Collapse( m.Groups["token"].Value );
      int at = collapsed.IndexOf( token, StringComparison.Ordinal );
      if( at < 0 )
      {
         return TextClass.UNVERIFIED;
      }

      List<string> covering = sheet.Split( m.Groups["t"].Value, m.Groups["f"].Value, text ).Where( p => p.Start < at + token.Length && p.Start + p.Text.Length > at ).Select( p => p.Span.Class ).ToList();
      return covering.Count == 0 ? TextClass.UNVERIFIED : covering.OrderBy( TextClass.Strength ).First();
   }

   #endregion Private Methods
}
