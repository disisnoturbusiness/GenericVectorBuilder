using System.Text.RegularExpressions;
using GenericVectorBuilder.Bench.Report;
using S = GenericVectorBuilder.Bench.Stats.ConsolidateSources;
using T = GenericVectorBuilder.Bench.Stats.ConsolidateText;

namespace GenericVectorBuilder.Bench.Stats;

/// <summary>
/// Prints a recorded text clause by clause: a sentence that says how the next clauses are backed (read back, measured, documented in a saved source, or
/// unverified), then each of those clauses as a verbatim quote of the recorded field. A clause the report does not print (one a repository file contradicts, one
/// about targets this report does not have, one whose figures a saved log replaces) is replaced by a sentence that says so.
/// Why clause by clause and not the whole text as one quote: a recorded text mixes clauses the engine reported with clauses the program only typed, and a reader
/// must see which is which beside each clause; every clause is a verbatim part of the recorded field, so the audit still checks each against the raw file.
/// </summary>
public sealed class ClauseWriter
{
   #region Data Members

   /// <summary>How many characters of a dropped clause its notice cites, enough to find it in the recorded field.</summary>
   private const int TOKEN_CHARS = 48;

   /// <summary>The prefix an engine setting's "how" carries when the benchmark read it from the running engine.</summary>
   private const string READ_PREFIX = "read:";

   /// <summary>Fewest characters of a setting's name for it to count as stated by name in a clause when it has no underscore.</summary>
   private const int MIN_KEY_CHARS = 8;

   private static readonly TimeSpan MATCH_TIMEOUT = TimeSpan.FromSeconds( 5 );

   private readonly Writer _w;
   private readonly string _slot;
   private int _group;
   private int _quote;
   private int _dropped;
   private string? _lastLabel;

   #endregion Data Members

   #region Constructor

   /// <summary>
   /// Creates a writer of clauses under a slot.
   /// </summary>
   /// <param name="w">The sentence writer.</param>
   /// <param name="slot">The slot prefix of every sentence this writer adds, such as disclosure.durability.mariadb.</param>
   public ClauseWriter( Writer w, string slot )
   {
      _w = w;
      _slot = slot;
   }

   #endregion Constructor

   #region Public Methods

   /// <summary>
   /// Prints the pieces of one recorded text, or the part of it inside a range.
   /// </summary>
   /// <param name="run">The run the text is quoted from.</param>
   /// <param name="path">The JSON path of the text in that run's results.json.</param>
   /// <param name="pieces">The pieces of the text.</param>
   /// <param name="range">The part of the text to print (start and end in the collapsed text), or null for all of it.</param>
   public void Add( string run, string path, IReadOnlyList<TextPiece> pieces, ( int Start, int End )? range = null )
   {
      List<Item> items = Items( run, path, pieces, range );
      int i = 0;
      while( i < items.Count )
      {
         if( items[i].Quote == null )
         {
            items[i].Emit!();
            _lastLabel = null;
            i++;
            continue;
         }

         int end = i;
         while( end < items.Count && items[end].Quote != null && items[end].Label == items[i].Label )
         {
            end++;
         }

         if( items[i].Label != _lastLabel || items[i].Basis.Count > 0 )
         {
            Label( items[i], items.Skip( i ).Take( end - i ) );
         }

         _lastLabel = items[i].Label;
         for( int k = i; k < end; k++ )
         {
            _w.Add( $"{_slot}.g{_group - 1}.q{_quote++}", items[k].Quote!, S.Quote( run, path, items[k].Quote! ) );
            items[k].Emit?.Invoke();
         }

         i = end;
      }
   }

   #endregion Public Methods

   #region Private Methods

   /// <summary>A setting of the newest session's engine settings that a clause states by name.</summary>
   /// <param name="Session">The session whose engine settings list it.</param>
   /// <param name="Key">The setting's name.</param>
   /// <param name="KeySource">The source of the name in consolidated.json.</param>
   /// <param name="HowSource">The source of how the benchmark obtained it.</param>
   private sealed record SettingRead( string Session, string Key, SentenceSource KeySource, SentenceSource HowSource );

   /// <summary>One thing to print: a clause to quote, or a sentence to add.</summary>
   private sealed class Item
   {
      /// <summary>The clause to quote, or null for a sentence.</summary>
      public string? Quote { get; init; }

      /// <summary>The label that groups the clause with its neighbours.</summary>
      public string Label { get; init; } = string.Empty;

      /// <summary>The sources that back the clause.</summary>
      public IReadOnlyList<string> Basis { get; init; } = Array.Empty<string>();

      /// <summary>The sources of the label beyond the basis: the engine settings the clause's label names.</summary>
      public IReadOnlyList<SentenceSource> LabelSources { get; init; } = Array.Empty<SentenceSource>();

      /// <summary>Adds the sentence of a sentence item, or the evidence that follows a quoted clause; null when there is none.</summary>
      public Action? Emit { get; init; }
   }

   /// <summary>
   /// The items of the pieces: a quote per printed clause, and a sentence per clause that is not printed, with the evidence a span names.
   /// </summary>
   /// <param name="run">The run.</param>
   /// <param name="path">The text's path.</param>
   /// <param name="pieces">The pieces.</param>
   /// <param name="range">The part to print, or null.</param>
   /// <returns>The items in text order.</returns>
   private List<Item> Items( string run, string path, IReadOnlyList<TextPiece> pieces, ( int Start, int End )? range )
   {
      var items = new List<Item>();
      foreach( TextPiece piece in pieces )
      {
         string text = Clip( piece, range );
         if( text.Length == 0 )
         {
            continue;
         }

         string? reason = ConsolidateClasses.NotPrinted( piece.Span, _w.Report.Targets, _w.RepoRoot );
         Action? evidence = piece.Span.Evidence == null ? null : () => ConsolidateTextEvidence.Write( _w, _slot, piece.Span.Evidence, run, path );
         if( reason == null )
         {
            ( string label, List<SentenceSource> labelSources ) = LabelOf( piece, text );
            items.Add( new Item { Quote = text, Label = label, Basis = piece.Span.Basis, LabelSources = labelSources, Emit = evidence } );
            continue;
         }

         items.Add( new Item { Emit = () => Notice( reason, piece, run, path ) } );
         if( evidence != null )
         {
            items.Add( new Item { Emit = evidence } );
         }
      }

      return items;
   }

   /// <summary>
   /// The part of a piece inside a range.
   /// </summary>
   /// <param name="piece">The piece.</param>
   /// <param name="range">The range, or null for all.</param>
   /// <returns>The clipped text, empty when the piece is outside the range.</returns>
   private static string Clip( TextPiece piece, ( int Start, int End )? range )
   {
      if( range == null )
      {
         return piece.Text;
      }

      int from = Math.Max( piece.Start, range.Value.Start );
      int to = Math.Min( piece.Start + piece.Text.Length, range.Value.End );
      return to > from ? piece.Text.Substring( from - piece.Start, to - from ).Trim() : string.Empty;
   }

   /// <summary>
   /// The label sentence of a group of clauses.
   /// </summary>
   /// <param name="first">The first item of the group.</param>
   /// <param name="group">Every item of the group.</param>
   private void Label( Item first, IEnumerable<Item> group )
   {
      var sources = new List<SentenceSource>();
      foreach( string basis in group.SelectMany( g => g.Basis ).Distinct( StringComparer.Ordinal ) )
      {
         sources.Add( SourceResolver.FromFactSource( basis ) );
      }

      sources.AddRange( group.SelectMany( g => g.LabelSources ).DistinctBy( x => x.Ref ) );

      _w.Add( $"{_slot}.g{_group++}", first.Label, sources );
   }

   /// <summary>
   /// The label of a clause by its class and what backs it, and the sources the label itself needs.
   /// A clause documented in a saved source is labelled "not read back from the engine" only when the newest session's engine settings do not list a setting the clause states; when they do (a setting
   /// read from the running engine by name, such as innodb_flush_log_at_trx_commit), the label says the settings list it, so the label never contradicts the settings table.
   /// </summary>
   /// <param name="piece">The piece.</param>
   /// <param name="text">The clause as printed.</param>
   /// <returns>The label sentence and the sources of the settings it names.</returns>
   private ( string Label, List<SentenceSource> Sources ) LabelOf( TextPiece piece, string text )
   {
      ClassSpan span = piece.Span;
      List<string> kinds = span.Basis.Select( b => SourceResolver.KindOf( b ) ).ToList();
      List<string> paths = span.Basis.Select( b => b[..Math.Max( 0, b.IndexOf( '#' ) )] ).ToList();
      string label = span.Class switch
      {
         _ when piece.Unlisted => T.CLAUSE_UNLISTED,
         TextClass.READ_BACK => kinds.Contains( SourceKinds.RESULTS ) ? T.CLAUSE_READ_BACK : T.CLAUSE_READ_BY_CODE,
         TextClass.MEASURED => T.CLAUSE_MEASURED,
         TextClass.DOCUMENTED when kinds.Contains( SourceKinds.DOC ) || paths.Any( IsSavedPage ) => T.CLAUSE_DOC_PAGE,
         TextClass.DOCUMENTED when paths.Any( p => p.StartsWith( "design/", StringComparison.Ordinal ) ) => T.CLAUSE_DOC_LOG,
         TextClass.DOCUMENTED when paths.Any( p => p.StartsWith( "tests/", StringComparison.Ordinal ) ) => T.CLAUSE_DOC_TEST,
         TextClass.DOCUMENTED => T.CLAUSE_DOC_CODE,
         TextClass.UNVERIFIED when span.Cites != null => T.CLAUSE_UNVERIFIED_CITES,
         _ => T.CLAUSE_UNVERIFIED,
      };
      string? withSettings = label switch
      {
         T.CLAUSE_DOC_PAGE => T.CLAUSE_DOC_PAGE_AND_SETTINGS,
         T.CLAUSE_DOC_LOG => T.CLAUSE_DOC_LOG_AND_SETTINGS,
         T.CLAUSE_DOC_CODE => T.CLAUSE_DOC_CODE_AND_SETTINGS,
         _ => null,
      };
      if( withSettings == null )
      {
         return ( label, new List<SentenceSource>() );
      }

      List<SettingRead> read = SettingsRead( text );
      return read.Count == 0 ? ( label, new List<SentenceSource>() )
         : ( T.Fill( withSettings, read[0].Session, S.List( read.Select( r => r.Key ).ToList() ) ), read.SelectMany( r => new[] { r.KeySource, r.HowSource } ).ToList() );
   }

   /// <summary>
   /// The settings of this target that the newest session's engine settings list as read from the running engine and that a clause states by name ("name=value" or "name value").
   /// Why by name and not by value: a clause may type a setting in one unit ("256MB", "4G") and the engine report it in another (244.1 MiB, 4294967296), and the label only says the settings list it.
   /// Why a name of eight characters or more, or one with an underscore: "save" and "version" are words of the recorded texts that no setting is meant by.
   /// </summary>
   /// <param name="text">The clause.</param>
   /// <returns>The session, the setting's key, and the sources of its key and of how it was obtained, in the order of the settings.</returns>
   private List<SettingRead> SettingsRead( string text )
   {
      var found = new List<SettingRead>();
      string target = _slot.Split( '.' )[^1];
      for( int r = 0; r < _w.Report.EngineSettings.Count; r++ )
      {
         EngineSettingsRow row = _w.Report.EngineSettings[r];
         for( int j = 0; row.Target == target && j < row.Settings.Count; j++ )
         {
            EngineSettingEntry setting = row.Settings[j];
            bool distinctive = setting.Key.Length >= MIN_KEY_CHARS || setting.Key.Contains( '_', StringComparison.Ordinal );
            if( distinctive && setting.How.StartsWith( READ_PREFIX, StringComparison.Ordinal ) && Regex.IsMatch( text, @"(?<![A-Za-z0-9_.])" + Regex.Escape( setting.Key ) + @"(?=[= ])", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, MATCH_TIMEOUT ) )
            {
               found.Add( new SettingRead( row.Session, setting.Key, S.Consolidated( $"engineSettings[{r}].settings[{j}].key", setting.Key ), S.Consolidated( $"engineSettings[{r}].settings[{j}].how", setting.How ) ) );
            }
         }
      }

      return found;
   }

   /// <summary>
   /// Whether a repository path is a saved documentation page or its source file, as opposed to a saved log or measurement.
   /// </summary>
   /// <param name="path">The path.</param>
   /// <returns>True for a file of design/engine-docs that is not under the durability logs.</returns>
   private static bool IsSavedPage( string path )
   {
      return path.StartsWith( "design/engine-docs/", StringComparison.Ordinal ) && !path.Contains( "durability-logs", StringComparison.Ordinal );
   }

   /// <summary>
   /// The sentence for a clause that is not printed.
   /// </summary>
   /// <param name="reason">Why: contradicted, absent-targets or a drop code.</param>
   /// <param name="piece">The piece that is not printed.</param>
   /// <param name="run">The run.</param>
   /// <param name="path">The text's path.</param>
   private void Notice( string reason, TextPiece piece, string run, string path )
   {
      ClassSpan span = piece.Span;
      if( reason == "contradicted" )
      {
         ContradictedBy c = span.Contradicted!;
         List<string> names = ConsolidateTextDurability.Variables( _w.RepoRoot, c.File, c.Prefix );
         var sources = names.Select( n => S.File( c.File, $"{n}:" ) ).ToList();
         sources.Add( S.Token( run, path, c.Words ) );
         _w.Add( $"{_slot}.correction", T.Fill( T.DISCLOSURE_CORRECTION, _slot.Split( '.' )[^1], c.File, S.List( names ), c.Words ), sources );
         return;
      }

      if( reason == "absent-targets" )
      {
         var sources = span.DropWhenAbsent.Select( n => S.Token( run, path, n ) ).ToList();
         _w.Add( $"{_slot}.dropped.{_dropped++}", T.Fill( T.DROPPED_ABSENT, S.List( span.DropWhenAbsent.ToList() ) ), sources );
         return;
      }

      if( reason == ConsolidateClasses.DROP_CLOCK_HELD )
      {
         ClockHeldNotice( piece, run, path );
         return;
      }

      _w.Add( $"{_slot}.dropped.{_dropped++}", T.Fill( T.DROPPED_UNBACKED, _slot.Split( '.' )[^1] ), S.Token( run, path, piece.Text.Length <= TOKEN_CHARS ? piece.Text : piece.Text[..TOKEN_CHARS] ) );
   }

   /// <summary>
   /// The sentence for the clause of a run note that says every CPU's clock is held at its ceiling whatever the engine runs.
   /// With an observer summary that read the per-CPU clock it names the pass the observer found under the pin and how far; without one it says no reading was given to check the clause.
   /// Why not printed: the clause is the tool's own words about every engine, and the observer's APERF and MPERF readings put one engine's pass under the pin in every run.
   /// Why the source is the clause's first characters and not the whole clause: the run-page notes list marks the whole clause (false for v8, unbacked for v7), and no string of the report may quote a
   /// marked statement without its correction; the words that start the clause are enough for the audit to find the note in the run.
   /// </summary>
   /// <param name="piece">The clause.</param>
   /// <param name="run">The run the note is quoted from.</param>
   /// <param name="path">The note's path in that run's results.json.</param>
   private void ClockHeldNotice( TextPiece piece, string run, string path )
   {
      string clause = piece.Text.Trim( ',', ';', ' ' );
      int cut = clause.LastIndexOf( ' ', Math.Min( TOKEN_CHARS, clause.Length - 1 ) );
      SentenceSource cited = S.Token( run, path, clause.Length <= TOKEN_CHARS ? clause : clause[..( cut > 0 ? cut : TOKEN_CHARS )] );
      string slot = $"{_slot}.dropped.{_dropped++}";
      if( _w.Report.Observer?.Dip is not ObserverDip dip )
      {
         _w.Add( slot, T.DROPPED_CLOCK_HELD_UNCHECKED, cited );
         return;
      }

      string low = S.Percent( dip.MinBp );
      string high = S.Percent( dip.MaxBp );
      _w.Add( slot, T.Fill( T.DROPPED_CLOCK_HELD, dip.Target, T.PassLabel( dip.Pass ), low, high ), cited, S.Consolidated( "observer.dip.target", dip.Target ),
         S.Consolidated( "observer.dip.minBp", low, "bp-pct" ), S.Consolidated( "observer.dip.maxBp", high, "bp-pct" ) );
   }

   #endregion Private Methods
}
