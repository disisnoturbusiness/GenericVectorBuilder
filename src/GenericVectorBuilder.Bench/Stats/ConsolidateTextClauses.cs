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

   /// <summary>One thing to print: a clause to quote, or a sentence to add.</summary>
   private sealed class Item
   {
      /// <summary>The clause to quote, or null for a sentence.</summary>
      public string? Quote { get; init; }

      /// <summary>The label that groups the clause with its neighbours.</summary>
      public string Label { get; init; } = string.Empty;

      /// <summary>The sources that back the clause.</summary>
      public IReadOnlyList<string> Basis { get; init; } = Array.Empty<string>();

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
            items.Add( new Item { Quote = text, Label = LabelOf( piece ), Basis = piece.Span.Basis, Emit = evidence } );
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

      _w.Add( $"{_slot}.g{_group++}", first.Label, sources );
   }

   /// <summary>
   /// The label of a clause by its class and what backs it.
   /// </summary>
   /// <param name="piece">The piece.</param>
   /// <returns>The label sentence.</returns>
   private static string LabelOf( TextPiece piece )
   {
      ClassSpan span = piece.Span;
      List<string> kinds = span.Basis.Select( b => SourceResolver.KindOf( b ) ).ToList();
      List<string> paths = span.Basis.Select( b => b[..Math.Max( 0, b.IndexOf( '#' ) )] ).ToList();
      return span.Class switch
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

      _w.Add( $"{_slot}.dropped.{_dropped++}", T.Fill( T.DROPPED_UNBACKED, _slot.Split( '.' )[^1] ), S.Token( run, path, piece.Text.Length <= TOKEN_CHARS ? piece.Text : piece.Text[..TOKEN_CHARS] ) );
   }

   #endregion Private Methods
}
