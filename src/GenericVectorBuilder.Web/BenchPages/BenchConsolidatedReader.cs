using System.Globalization;
using System.Text.Json;

namespace GenericVectorBuilder.Web.BenchPages;

/// <summary>
/// The shapes of consolidated.json the pages meet.
/// </summary>
public enum BenchShape
{
   /// <summary>The v8 shape: sessions, metrics, sentences.</summary>
   Consolidated,

   /// <summary>The shape of the consolidate command before v8 (targetSummaries, settings, runs, flags).</summary>
   Report,

   /// <summary>The first published shape: an object keyed by engine.</summary>
   KeyedByEngine,
}

/// <summary>
/// Reads a consolidated.json of the v8 shape (see the contract between the benchmark and this page)
/// into a <see cref="BenchConsolidated"/>, and tells the older shapes apart so the page can say so
/// without reading them as results.
/// Why strict: a published page must print what the file says. Anything the page needs that is
/// missing or of the wrong type throws <see cref="InvalidDataException"/> with the path of the field,
/// and so do contradictions the file could hold (a row order that disagrees with the separations the
/// file lists, a headline on a stopped set, a section with no sentence the page needs).
/// What is optional: blocks that only exist for two sessions (drift, basis, the clock, the images, the
/// facts and costs); with two sessions or more they are required.
/// </summary>
public static class BenchConsolidatedReader
{
   #region Data Members

   /// <summary>Most words a headline may hold; the consolidate command refuses a longer one, and the page refuses it again.</summary>
   public const int MAX_HEADLINE_WORDS = 34;

   /// <summary>Most rows one metric table may hold; more means the file is not what the page thinks it is.</summary>
   public const int MAX_ROWS = 200;

   /// <summary>Most sentences one file may hold.</summary>
   public const int MAX_SENTENCES = 2000;

   /// <summary>How many levels below a basis kind the page looks for moves.</summary>
   public const int MAX_MOVE_DEPTH = 4;

   private const string FILE = "consolidated.json";
   private static readonly string[] EFFORT_NAMES = { "searchEffort", "searchSettings" };
   private const string SEARCH_KIND = "search";
   private const string RANKED = "ranked";
   private const string ONE_SESSION = "one-session";
   private const string NOT_HELD = BenchRow.NOT_HELD;

   #endregion Data Members

   #region Public Methods

   /// <summary>
   /// Which shape a consolidated.json is in.
   /// </summary>
   /// <param name="json">File text.</param>
   /// <returns>The shape.</returns>
   public static BenchShape Detect( string json )
   {
      using JsonDocument doc = JsonDocument.Parse( json );
      JsonElement root = doc.RootElement;
      if( root.ValueKind != JsonValueKind.Object )
      {
         throw new InvalidDataException( $"{FILE} is not an object." );
      }

      if( root.TryGetProperty( "metrics", out JsonElement metrics ) && metrics.ValueKind == JsonValueKind.Array )
      {
         return BenchShape.Consolidated;
      }

      if( root.TryGetProperty( "targetSummaries", out JsonElement summaries ) && summaries.ValueKind == JsonValueKind.Array )
      {
         return BenchShape.Report;
      }

      return root.EnumerateObject().Any() && root.EnumerateObject().All( p => p.Value.ValueKind == JsonValueKind.Object ) && root.EnumerateObject().Any( p => p.Value.TryGetProperty( "qps8", out _ ) )
         ? BenchShape.KeyedByEngine
         : throw new InvalidDataException( $"{FILE} is in a shape this page does not know." );
   }

   /// <summary>
   /// Reads and checks a v8 consolidated.json.
   /// </summary>
   /// <param name="json">File text.</param>
   /// <returns>The model.</returns>
   public static BenchConsolidated Read( string json )
   {
      using JsonDocument doc = JsonDocument.Parse( json );
      JsonElement root = doc.RootElement;
      if( root.ValueKind != JsonValueKind.Object )
      {
         throw new InvalidDataException( $"{FILE} is not an object." );
      }

      List<BenchSession> sessions = ReadSessions( root );
      JsonElement threshold = BenchJson.RequireObject( root, "threshold", FILE );
      bool two = sessions.Count >= 2;
      BenchGuards guards = ReadGuards( root );
      var model = new BenchConsolidated( sessions, BenchJson.Whole( threshold, "tBp", "threshold" ) ?? throw BenchJson.Bad( "threshold", "tBp", "a whole number" ),
         BenchJson.RequireNumber( threshold, "ratio", "threshold" ), ReadMetrics( root ), guards, ReadBasis( root, two ), ReadDrift( root, two ), ReadRecall( root ),
         ReadClock( root, two ), ReadPairs( BenchJson.Get( root, "machine" ), "machine" ), ReadImages( root ), ReadWhy( root ), ReadSentences( root ), ReadAudit( root ), ReadEngineSettings( root ),
         BenchConsolidatedFields.Unread( root ), ReadRunNotes( root ), ReadRecordedTexts( root ) );
      Validate( model, two );
      return model;
   }

   #endregion Public Methods

   #region Private Methods

   /// <summary>
   /// The sessions: a name and the runs of each.
   /// </summary>
   /// <param name="root">The file's root.</param>
   /// <returns>The sessions, at least one.</returns>
   private static List<BenchSession> ReadSessions( JsonElement root )
   {
      JsonElement list = BenchJson.RequireArray( root, "sessions", FILE );
      var sessions = new List<BenchSession>();
      foreach( ( JsonElement s, int i ) in list.EnumerateArray().Select( ( e, i ) => ( e, i ) ) )
      {
         string path = $"sessions[{i}]";
         var runs = BenchJson.Items( s, "runs", path ).Select( ( r, j ) => new BenchSessionRun( BenchJson.RequireText( r, "folder", $"{path}.runs[{j}]" ),
            BenchJson.Whole( r, "seed", $"{path}.runs[{j}]" ), BenchJson.Text( r, "startedUtc", $"{path}.runs[{j}]" ), BenchJson.Text( r, "resultsSha256", $"{path}.runs[{j}]" ) ) ).ToList();
         sessions.Add( new BenchSession( BenchJson.RequireText( s, "name", path ), runs ) );
      }

      if( sessions.Count == 0 || sessions.Any( s => s.Runs.Count == 0 ) || sessions.Select( s => s.Name ).Distinct( StringComparer.Ordinal ).Count() != sessions.Count )
      {
         throw new InvalidDataException( $"{FILE}: sessions must hold at least one session, each with a distinct name and at least one run." );
      }

      return sessions;
   }

   /// <summary>
   /// The guards: the stopped state and the targets whose setup differs between sessions. A set is stopped when the second guard says so, and
   /// also when the first or third guard does or the file's own status says "stopped": a stopped file that the page drew as a result would be the
   /// one failure this page must not have.
   /// </summary>
   /// <param name="root">The file's root.</param>
   /// <returns>The guards; "not stopped, no differing target" when the file has none.</returns>
   private static BenchGuards ReadGuards( JsonElement root )
   {
      var stoppedBy = new List<string>();
      if( string.Equals( BenchJson.Text( root, "status", FILE ), "stopped", StringComparison.OrdinalIgnoreCase ) )
      {
         stoppedBy.Add( "status" );
      }

      JsonElement? guards = BenchJson.Get( root, "guards" );
      if( guards == null )
      {
         return new BenchGuards( false, Array.Empty<string>(), Array.Empty<( string, IReadOnlyList<string> )>(), stoppedBy );
      }

      JsonElement g1 = BenchJson.Get( guards.Value, "g1" ) ?? default;
      JsonElement g2 = BenchJson.Get( guards.Value, "g2" ) ?? default;
      JsonElement g3 = BenchJson.Get( guards.Value, "g3" ) ?? default;
      stoppedBy.AddRange( new[] { ( "guards.g1.stopped", g1 ), ( "guards.g3.stopped", g3 ) }.Where( g => BenchJson.Flag( g.Item2, "stopped", g.Item1 ) == true ).Select( g => g.Item1 ) );
      var pairs = BenchJson.Items( g2, "pairs", "guards.g2" ).Select( p => Describe( p ) ).ToList();
      var targets = BenchJson.Items( g3, "targets", "guards.g3" ).Select( ( t, i ) => ( BenchJson.RequireText( t, "target", $"guards.g3.targets[{i}]" ),
         BenchJson.Strings( t, "fields", $"guards.g3.targets[{i}]" ) ) ).ToList();
      return new BenchGuards( BenchJson.Flag( g2, "stopped", "guards.g2" ) ?? false, pairs, targets, stoppedBy );
   }

   /// <summary>
   /// The metric tables with their rows, each row checked against the others.
   /// </summary>
   /// <param name="root">The file's root.</param>
   /// <returns>The metrics.</returns>
   private static List<BenchMetric> ReadMetrics( JsonElement root )
   {
      var metrics = new List<BenchMetric>();
      foreach( ( JsonElement m, int i ) in BenchJson.RequireArray( root, "metrics", FILE ).EnumerateArray().Select( ( e, i ) => ( e, i ) ) )
      {
         string path = $"metrics[{i}]";
         var rows = new List<BenchRow>();
         foreach( ( JsonElement r, int j ) in BenchJson.RequireArray( m, "rows", path ).EnumerateArray().Select( ( e, j ) => ( e, j ) ) )
         {
            rows.Add( ReadRow( r, $"{path}.rows[{j}]" ) );
         }

         if( rows.Count > MAX_ROWS )
         {
            throw new InvalidDataException( $"{path} holds more than {MAX_ROWS} rows; refusing to draw it." );
         }

         bool lower = BenchJson.Flag( m, "lowerIsBetter", path ) ?? throw BenchJson.Bad( path, "lowerIsBetter", "true or false" );
         metrics.Add( new BenchMetric( BenchJson.RequireText( m, "metric", path ), lower, rows ) );
      }

      return metrics;
   }

   /// <summary>
   /// One metric row.
   /// </summary>
   /// <param name="r">The row's object.</param>
   /// <param name="path">Where it sits in the file.</param>
   /// <returns>The row.</returns>
   private static BenchRow ReadRow( JsonElement r, string path )
   {
      string status = BenchJson.RequireText( r, "status", path );
      if( status is not ( RANKED or ONE_SESSION or NOT_HELD ) )
      {
         throw new InvalidDataException( $"{path}.status is \"{status}\"; the page knows \"{RANKED}\", \"{ONE_SESSION}\" and \"{NOT_HELD}\"." );
      }

      var per = new Dictionary<string, BenchRange>( StringComparer.Ordinal );
      if( BenchJson.Get( r, "perSession" ) is { ValueKind: JsonValueKind.Object } sessions )
      {
         foreach( JsonProperty p in sessions.EnumerateObject() )
         {
            string where = $"{path}.perSession.{p.Name}";
            per[p.Name] = new BenchRange( BenchJson.RequireNumber( p.Value, "min", where ), BenchJson.RequireNumber( p.Value, "max", where ),
               BenchJson.Items( p.Value, "runs", where ).Select( ( v, k ) => v.ValueKind == JsonValueKind.Number && v.TryGetDouble( out double d ) ? d : throw BenchJson.Bad( where, $"runs[{k}]", "a number" ) ).ToList() );
         }
      }

      string target = BenchJson.RequireText( r, "target", path );
      var flags = BenchJson.Items( r, "flags", path ).Select( ( f, k ) => new BenchFlagEntry( BenchJson.RequireText( f, "code", $"{path}.flags[{k}]" ), BenchJson.Text( f, "text", $"{path}.flags[{k}]" ) ?? string.Empty ) ).ToList();
      string? note = BenchJson.Text( r, "note", path );
      List<BenchSource> noteSources = ReadSources( r, "noteSources", path );
      if( noteSources.Count > 0 && string.IsNullOrWhiteSpace( note ) )
      {
         throw new InvalidDataException( $"{path}.noteSources holds sources for a note, and the row has no note." );
      }

      return new BenchRow( target, BenchJson.Text( r, "display", path ) is { Length: > 0 } display ? display : BenchNames.FriendlyName( target ), status, per, BenchJson.Number( r, "median", path ),
         BenchJson.Strings( r, "notSeparatedFrom", path ), flags, note, BenchJson.Text( r, "searchMode", path ) is { Length: > 0 } mode ? mode : null, noteSources );
   }

   /// <summary>
   /// The sources bound to a text: a list of { kind, ref, value } objects.
   /// </summary>
   /// <param name="parent">The object that holds the list.</param>
   /// <param name="name">The list's name.</param>
   /// <param name="path">Where the parent sits in the file.</param>
   /// <returns>The sources; none when the list is absent.</returns>
   private static List<BenchSource> ReadSources( JsonElement parent, string name, string path )
   {
      return BenchJson.Items( parent, name, path ).Select( ( x, j ) => new BenchSource( BenchJson.RequireText( x, "kind", $"{path}.{name}[{j}]" ),
         BenchJson.RequireText( x, "ref", $"{path}.{name}[{j}]" ), Describe( BenchJson.Get( x, "value" ) ?? default ) ) ).ToList();
   }

   /// <summary>
   /// The sentences with their sources.
   /// </summary>
   /// <param name="root">The file's root.</param>
   /// <returns>The sentences.</returns>
   private static List<BenchSentence> ReadSentences( JsonElement root )
   {
      JsonElement list = BenchJson.RequireArray( root, "sentences", FILE );
      if( list.GetArrayLength() > MAX_SENTENCES )
      {
         throw new InvalidDataException( $"{FILE} holds more than {MAX_SENTENCES} sentences; refusing to draw it." );
      }

      return list.EnumerateArray().Select( ( s, i ) =>
      {
         string path = $"sentences[{i}]";
         var sources = BenchJson.Items( s, "sources", path ).Select( ( x, j ) => new BenchSource( BenchJson.RequireText( x, "kind", $"{path}.sources[{j}]" ),
            BenchJson.RequireText( x, "ref", $"{path}.sources[{j}]" ), Describe( BenchJson.Get( x, "value" ) ?? default ) ) ).ToList();
         bool quote = BenchJson.Flag( s, "quote", path ) == true || string.Equals( BenchJson.Text( s, "kind", path ), "quote", StringComparison.Ordinal ) || sources.Any( x => x.Kind == "quote" );
         return new BenchSentence( BenchJson.RequireText( s, "slot", path ), BenchJson.RequireText( s, "text", path ), sources, quote );
      } ).ToList();
   }

   /// <summary>
   /// The threshold basis, required for two sessions.
   /// </summary>
   /// <param name="root">The file's root.</param>
   /// <param name="required">True when the file must hold it.</param>
   /// <returns>The basis, or null when absent and not required.</returns>
   private static BenchBasis? ReadBasis( JsonElement root, bool required )
   {
      JsonElement? basis = BenchJson.Get( root, "basis" );
      if( basis == null )
      {
         return required ? throw BenchJson.Bad( FILE, "basis", "an object (two sessions need it)" ) : null;
      }

      JsonElement b = basis.Value;
      var runs = BenchJson.Items( b, "runs", "basis" ).Select( ( r, i ) => new BenchBasisRun( BenchJson.RequireText( r, "folder", $"basis.runs[{i}]" ), BenchJson.Whole( r, "seed", $"basis.runs[{i}]" ),
         ReadPairs( BenchJson.Get( r, "conditions" ), $"basis.runs[{i}].conditions" ), BenchJson.Text( r, "session", $"basis.runs[{i}]" ), BenchJson.Text( r, "startedUtc", $"basis.runs[{i}]" ), BenchJson.Text( r, "resultsSha256", $"basis.runs[{i}]" ) ) ).ToList();
      List<BenchBasisMoves> moves = ReadMoves( BenchJson.Get( b, "perMetric" ), "basis.perMetric" );
      moves.AddRange( ReadNamedMoves( b, "basis", "maxMove", "exclusionsKept" ) );
      List<BenchBasisMoves> none = ReadMoves( BenchJson.Get( b, "noMachineControl" ), "basis.noMachineControl", true );
      return new BenchBasis( runs, ReadRecords( b, "leftOut", "basis" ), ReadRecords( b, "exclusions", "basis" ), moves, none, BenchJson.Whole( b, "maxBp", "basis" ), BenchJson.Whole( b, "tBp", "basis" ), ReadSetupSplits( b ) );
   }

   /// <summary>
   /// The changes of an engine's recorded setup that the basis does not compare across, each with the fields that changed, the runs on its two sides, the
   /// largest move it hides and the threshold it would give. The file's own count (setupSplitCount) must equal the list: a table that shows fewer changes than
   /// the file counts would hide the very thing the threshold depends on.
   /// </summary>
   /// <param name="basis">The basis object.</param>
   /// <returns>The splits; none when the file lists none and counts none.</returns>
   private static List<BenchSetupSplit> ReadSetupSplits( JsonElement basis )
   {
      var splits = new List<BenchSetupSplit>();
      foreach( ( JsonElement e, int i ) in BenchJson.Items( basis, "setupSplits", "basis" ).Select( ( e, i ) => ( e, i ) ) )
      {
         string path = $"basis.setupSplits[{i}]";
         List<BenchSetupChange> changes = BenchJson.Items( e, "changes", path ).Select( ( c, j ) => new BenchSetupChange( BenchJson.RequireText( c, "field", $"{path}.changes[{j}]" ),
            BenchJson.Text( c, "before", $"{path}.changes[{j}]" ) ?? string.Empty, BenchJson.Text( c, "after", $"{path}.changes[{j}]" ) ?? string.Empty ) ).ToList();
         if( changes.Count == 0 )
         {
            throw new InvalidDataException( $"{path}.changes names no field that changed; a setup split with nothing changed is not a split." );
         }

         JsonElement before = BenchJson.Get( e, "before" ) ?? default;
         JsonElement after = BenchJson.Get( e, "after" ) ?? default;
         splits.Add( new BenchSetupSplit( BenchJson.RequireText( e, "target", path ), changes, BenchJson.Strings( before, "sessions", $"{path}.before" ), BenchJson.Strings( before, "runs", $"{path}.before" ),
            BenchJson.Strings( after, "sessions", $"{path}.after" ), BenchJson.Strings( after, "runs", $"{path}.after" ), ReadSplitMove( e, "oneEngine", path ), ReadSplitMove( e, "pair", path ),
            BenchJson.Whole( e, "tBpIfCounted", path ) ) );
      }

      long? counted = BenchJson.Whole( basis, "setupSplitCount", "basis" );
      if( counted != null && counted != splits.Count )
      {
         throw new InvalidDataException( $"basis.setupSplitCount is {counted} and basis.setupSplits lists {splits.Count}; the page draws every change the file counts or none." );
      }

      return splits;
   }

   /// <summary>
   /// The move a setup split hides, or null when the split has none for that kind.
   /// </summary>
   /// <param name="split">The split's object.</param>
   /// <param name="name">"oneEngine" or "pair".</param>
   /// <param name="path">Where the split sits in the file.</param>
   /// <returns>The move with its metric, or null.</returns>
   private static BenchSplitMove? ReadSplitMove( JsonElement split, string name, string path )
   {
      return BenchJson.Get( split, name ) is { ValueKind: JsonValueKind.Object } move
         ? new BenchSplitMove( BenchJson.RequireText( move, "metric", $"{path}.{name}" ), ReadMove( move, $"{path}.{name}" ) )
         : null;
   }

   /// <summary>
   /// The largest moves: either { metric: { kind: move } } (perMetric) or { kind: move } (no machine control, the maximum, the exclusions kept in).
   /// A kind may hold one move, a list of moves, or objects that hold moves (a kept-in row holds a one-engine move and a pair move); each move found
   /// is listed under the path of its kind, so what the file records is what the page lists and a shape the page has not seen is still shown.
   /// </summary>
   /// <param name="element">The object, or null.</param>
   /// <param name="path">Where it sits in the file.</param>
   /// <param name="flat">True when the object holds the kinds directly.</param>
   /// <returns>The moves grouped by kind, in the file's order.</returns>
   private static List<BenchBasisMoves> ReadMoves( JsonElement? element, string path, bool flat = false )
   {
      var byKind = new Dictionary<string, List<( string Metric, BenchMove Move )>>( StringComparer.Ordinal );
      if( element is not { ValueKind: JsonValueKind.Object } root )
      {
         return new List<BenchBasisMoves>();
      }

      foreach( JsonProperty first in root.EnumerateObject() )
      {
         if( flat )
         {
            CollectMoves( first.Value, string.Empty, first.Name, $"{path}.{first.Name}", byKind, 0 );
         }
         else if( first.Value.ValueKind == JsonValueKind.Object )
         {
            foreach( JsonProperty kind in first.Value.EnumerateObject() )
            {
               CollectMoves( kind.Value, first.Name, kind.Name, $"{path}.{first.Name}.{kind.Name}", byKind, 0 );
            }
         }
      }

      return byKind.Select( k => new BenchBasisMoves( k.Key, k.Value ) ).ToList();
   }

   /// <summary>
   /// The moves held in named fields of the basis block (the largest move, the exclusions kept in), each field its own kind.
   /// </summary>
   /// <param name="basis">The basis object.</param>
   /// <param name="path">Where it sits in the file.</param>
   /// <param name="names">The fields to read; one the file lacks is skipped.</param>
   /// <returns>The moves grouped by kind.</returns>
   private static List<BenchBasisMoves> ReadNamedMoves( JsonElement basis, string path, params string[] names )
   {
      var byKind = new Dictionary<string, List<( string Metric, BenchMove Move )>>( StringComparer.Ordinal );
      foreach( string name in names )
      {
         if( BenchJson.Get( basis, name ) is JsonElement value )
         {
            CollectMoves( value, string.Empty, name, $"{path}.{name}", byKind, 0 );
         }
      }

      return byKind.Select( k => new BenchBasisMoves( k.Key, k.Value ) ).ToList();
   }

   /// <summary>
   /// Walks one value of the basis block and adds every move in it (an object that holds "moveBp") to its kind; objects and lists that hold
   /// no move themselves are walked, down to <see cref="MAX_MOVE_DEPTH"/>, and extend the kind with the field's name.
   /// </summary>
   /// <param name="value">The value.</param>
   /// <param name="metric">The metric the value sits under, or empty.</param>
   /// <param name="kind">The kind so far.</param>
   /// <param name="path">Where it sits in the file.</param>
   /// <param name="byKind">The moves found, by kind.</param>
   /// <param name="depth">How far below the kind the value is.</param>
   private static void CollectMoves( JsonElement value, string metric, string kind, string path, Dictionary<string, List<( string Metric, BenchMove Move )>> byKind, int depth )
   {
      if( depth > MAX_MOVE_DEPTH )
      {
         return;
      }

      switch( value.ValueKind )
      {
         case JsonValueKind.Array:
            foreach( ( JsonElement item, int i ) in value.EnumerateArray().Select( ( e, i ) => ( e, i ) ) )
            {
               CollectMoves( item, metric, kind, $"{path}[{i}]", byKind, depth + 1 );
            }

            break;
         case JsonValueKind.Object when value.TryGetProperty( "moveBp", out _ ):
            byKind.TryAdd( kind, new List<( string, BenchMove )>() );
            byKind[kind].Add( ( metric, ReadMove( value, path ) ) );
            break;
         case JsonValueKind.Object:
            foreach( JsonProperty property in value.EnumerateObject() )
            {
               CollectMoves( property.Value, metric, $"{kind}.{property.Name}", $"{path}.{property.Name}", byKind, depth + 1 );
            }

            break;
      }
   }

   /// <summary>
   /// One move.
   /// </summary>
   /// <param name="move">The move's object.</param>
   /// <param name="path">Where it sits.</param>
   /// <returns>The move.</returns>
   private static BenchMove ReadMove( JsonElement move, string path )
   {
      string who = BenchJson.Get( move, "target" ) is { ValueKind: JsonValueKind.String } t ? t.GetString()! : Describe( BenchJson.Get( move, "pair" ) ?? default );
      return new BenchMove( BenchJson.Whole( move, "moveBp", path ) ?? throw BenchJson.Bad( path, "moveBp", "a whole number" ), who,
         BenchJson.Items( move, "runs", path ).Select( r => Describe( r ) ).ToList(), BenchJson.Items( move, "values", path ).Select( v => Describe( v ) ).ToList(),
         BenchJson.Strings( move, "differences", path ).Concat( BenchJson.Strings( move, "measured", path ).Select( m => $"measured: {m}" ) ).ToList() );
   }

   /// <summary>
   /// Drift between two sessions, required for two sessions.
   /// </summary>
   /// <param name="root">The file's root.</param>
   /// <param name="required">True when the file must hold it.</param>
   /// <returns>The drift, or null.</returns>
   private static BenchDrift? ReadDrift( JsonElement root, bool required )
   {
      JsonElement? drift = BenchJson.Get( root, "drift" );
      if( drift == null )
      {
         return required ? throw BenchJson.Bad( FILE, "drift", "an object (two sessions need it)" ) : null;
      }

      JsonElement d = drift.Value;
      var per = BenchJson.Items( d, "perTarget", "drift" ).Select( ( e, i ) => new BenchDriftEntry( BenchJson.RequireText( e, "target", $"drift.perTarget[{i}]" ),
         BenchJson.RequireText( e, "metric", $"drift.perTarget[{i}]" ), BenchJson.Get( e, "moveBp" ) is { ValueKind: JsonValueKind.Number } n ? n.GetDouble() : throw BenchJson.Bad( $"drift.perTarget[{i}]", "moveBp", "a number" ) ) ).ToList();
      double? median = BenchJson.Get( d, "medianAbsMoveBp" ) is { ValueKind: JsonValueKind.Number } m ? m.GetDouble() : null;
      string? largest = BenchJson.Get( d, "largest" ) is JsonElement l ? DescribeLargest( l ) : null;
      return new BenchDrift( BenchJson.Text( d, "from", "drift" ), BenchJson.Text( d, "to", "drift" ), per, median, largest, ReadPairRefs( d, "unconfirmedOrders" ), ReadPairRefs( d, "closeToLine" ),
         ReadPairRefs( d, "onLine" ) );
   }

   /// <summary>
   /// The largest move as the drift block gives it: an object with a target (or pair), a metric and a move in basis points becomes
   /// "target, metric, percent"; anything else is described as it is written.
   /// </summary>
   /// <param name="largest">The value of drift.largest.</param>
   /// <returns>The text.</returns>
   private static string DescribeLargest( JsonElement largest )
   {
      if( largest.ValueKind != JsonValueKind.Object || !largest.TryGetProperty( "moveBp", out JsonElement move ) || move.ValueKind != JsonValueKind.Number )
      {
         return Describe( largest );
      }

      var parts = new List<string>();
      foreach( string name in new[] { "target", "pair", "metric" } )
      {
         if( BenchJson.Get( largest, name ) is JsonElement value )
         {
            parts.Add( Describe( value ) );
         }
      }

      parts.Add( BenchFormat.Percent( move.GetDouble() ) );
      return string.Join( ", ", parts );
   }

   /// <summary>
   /// A list of { metric, a, b } objects, each with the smallest ratio ("minRatioBp") or the session it held in ("session") when the list has them.
   /// </summary>
   /// <param name="parent">The drift object.</param>
   /// <param name="name">The list's name.</param>
   /// <returns>The pairs.</returns>
   private static List<BenchPairRef> ReadPairRefs( JsonElement parent, string name )
   {
      return BenchJson.Items( parent, name, "drift" ).Select( ( e, i ) => new BenchPairRef( BenchJson.RequireText( e, "metric", $"drift.{name}[{i}]" ), BenchJson.RequireText( e, "a", $"drift.{name}[{i}]" ),
         BenchJson.RequireText( e, "b", $"drift.{name}[{i}]" ), BenchJson.Whole( e, "minRatioBp", $"drift.{name}[{i}]" ), BenchJson.Text( e, "session", $"drift.{name}[{i}]" ) ) ).ToList();
   }

   /// <summary>
   /// Recall per engine as hits.
   /// </summary>
   /// <param name="root">The file's root.</param>
   /// <returns>The rows, none when the file has none.</returns>
   private static List<BenchRecall> ReadRecall( JsonElement root )
   {
      var list = new List<BenchRecall>();
      foreach( ( JsonElement e, int i ) in BenchJson.Items( root, "recall", FILE ).Select( ( e, i ) => ( e, i ) ) )
      {
         string path = $"recall[{i}]";
         var hits = new Dictionary<string, IReadOnlyList<long>>( StringComparer.Ordinal );
         if( BenchJson.Get( e, "hits" ) is { ValueKind: JsonValueKind.Object } byName )
         {
            foreach( JsonProperty p in byName.EnumerateObject() )
            {
               hits[p.Name] = BenchJson.Items( byName, p.Name, $"{path}.hits" ).Select( ( v, k ) => v.ValueKind == JsonValueKind.Number && v.TryGetInt64( out long n ) ? n : throw BenchJson.Bad( $"{path}.hits.{p.Name}", $"[{k}]", "a whole number" ) ).ToList();
            }
         }

         list.Add( new BenchRecall( BenchJson.RequireText( e, "target", path ), hits, BenchJson.Whole( e, "of", path ) ?? throw BenchJson.Bad( path, "of", "a whole number" ), BenchJson.Flag( e, "differs", path ) ?? false ) );
      }

      return list;
   }

   /// <summary>
   /// The clock block, required for two sessions.
   /// </summary>
   /// <param name="root">The file's root.</param>
   /// <param name="required">True when the file must hold it.</param>
   /// <returns>The clock, or null.</returns>
   private static BenchClock? ReadClock( JsonElement root, bool required )
   {
      JsonElement? clock = BenchJson.Get( root, "clock" );
      if( clock == null )
      {
         return required ? throw BenchJson.Bad( FILE, "clock", "an object (two sessions need it)" ) : null;
      }

      var per = new List<( string, IReadOnlyList<BenchPair> )>();
      if( BenchJson.Get( clock.Value, "perSession" ) is { ValueKind: JsonValueKind.Object } sessions )
      {
         per.AddRange( sessions.EnumerateObject().Select( p => ( p.Name, ReadPairs( p.Value, $"clock.perSession.{p.Name}" ) ) ) );
      }

      var dropped = BenchJson.Items( clock.Value, "droppedWarnings", "clock" ).Select( ( w, i ) => new BenchDroppedWarning( BenchJson.RequireText( w, "run", $"clock.droppedWarnings[{i}]" ),
         BenchJson.Text( w, "text", $"clock.droppedWarnings[{i}]" ) ?? string.Empty, BenchJson.Number( w, "engineMedianMhz", $"clock.droppedWarnings[{i}]" ), BenchJson.Number( w, "clientMedianMhz", $"clock.droppedWarnings[{i}]" ),
         BenchJson.Text( w, "target", $"clock.droppedWarnings[{i}]" ), BenchJson.Text( w, "pass", $"clock.droppedWarnings[{i}]" ) ) ).ToList();
      return new BenchClock( per, dropped );
   }

   /// <summary>
   /// The images list.
   /// </summary>
   /// <param name="root">The file's root.</param>
   /// <returns>The images, none when the file has none.</returns>
   private static List<BenchImage> ReadImages( JsonElement root )
   {
      return BenchJson.Items( root, "images", FILE ).Select( ( e, i ) => new BenchImage( BenchJson.RequireText( e, "target", $"images[{i}]" ), BenchJson.Text( e, "id", $"images[{i}]" ) ?? string.Empty,
         BenchJson.Text( e, "lastTagTimeUtc", $"images[{i}]" ), BenchJson.Flag( e, "beforeFirstV7Start", $"images[{i}]" ), BenchJson.Text( e, "ref", $"images[{i}]" ) is { Length: > 0 } image ? image : null ) ).ToList();
   }

   /// <summary>
   /// The facts and costs table, with the settings that control the effort of each search: each row's own "searchEffort" (or "searchSettings") and the
   /// file's top-level list of that name, which holds one entry per engine.
   /// </summary>
   /// <param name="root">The file's root.</param>
   /// <returns>The rows, none when the file has none.</returns>
   private static List<BenchWhy> ReadWhy( JsonElement root )
   {
      var list = new List<BenchWhy>();
      Dictionary<string, List<BenchEffort>> listed = ReadEffortList( root );
      foreach( ( JsonElement e, int i ) in BenchJson.Items( root, "why", FILE ).Select( ( e, i ) => ( e, i ) ) )
      {
         string path = $"why[{i}]";
         var facts = BenchJson.Items( e, "facts", path ).Select( ( f, j ) => new BenchFact( BenchJson.RequireText( f, "kind", $"{path}.facts[{j}]" ), BenchJson.RequireText( f, "text", $"{path}.facts[{j}]" ),
            BenchJson.RequireText( f, "source", $"{path}.facts[{j}]" ), BenchJson.RequireText( f, "confidence", $"{path}.facts[{j}]" ),
            BenchJson.Text( f, "mode", $"{path}.facts[{j}]" ) is { Length: > 0 } mode ? mode : null, BenchJson.Text( f, "where", $"{path}.facts[{j}]" ) is { Length: > 0 } where ? where : null ) ).ToList();
         JsonElement costs = BenchJson.Get( e, "costs" ) ?? default;
         JsonElement engine = BenchJson.Get( costs, "engineCpuMsPerSearch" ) ?? default;
         JsonElement client = BenchJson.Get( costs, "clientCpuMsPerSearch" ) ?? default;
         string target = BenchJson.RequireText( e, "target", path );
         List<BenchEffort> effort = EffortOf( e, path );
         if( listed.Remove( target, out List<BenchEffort>? more ) )
         {
            effort.AddRange( more );
         }

         list.Add( new BenchWhy( target, facts, new BenchCosts( BenchJson.Number( engine, "1", $"{path}.costs.engineCpuMsPerSearch" ), BenchJson.Number( engine, "8", $"{path}.costs.engineCpuMsPerSearch" ),
            BenchJson.Number( client, "1", $"{path}.costs.clientCpuMsPerSearch" ), BenchJson.Number( client, "8", $"{path}.costs.clientCpuMsPerSearch" ), BenchJson.Number( costs, "engineCpusBusyAt8", $"{path}.costs" ) ), effort ) );
      }

      if( listed.Count > 0 )
      {
         throw new InvalidDataException( $"{FILE}: the search effort list names {string.Join( ", ", listed.Keys )}, which the facts and costs table (why) does not list." );
      }

      return list;
   }

   /// <summary>
   /// The file's top-level search effort list: an array of objects with a "target", or an object keyed by target; entries of every name in <see cref="EFFORT_NAMES"/>.
   /// </summary>
   /// <param name="root">The file's root.</param>
   /// <returns>The settings by target; empty when the file has no such list.</returns>
   private static Dictionary<string, List<BenchEffort>> ReadEffortList( JsonElement root )
   {
      var byTarget = new Dictionary<string, List<BenchEffort>>( StringComparer.Ordinal );
      foreach( string name in EFFORT_NAMES )
      {
         switch( BenchJson.Get( root, name ) )
         {
            case { ValueKind: JsonValueKind.Array } array:
               foreach( ( JsonElement item, int i ) in array.EnumerateArray().Select( ( e, i ) => ( e, i ) ) )
               {
                  string target = BenchJson.RequireText( item, "target", $"{name}[{i}]" );
                  AddEffort( byTarget, target, EffortValues( item, $"{name}[{i}]" ) );
               }

               break;
            case { ValueKind: JsonValueKind.Object } keyed:
               foreach( JsonProperty property in keyed.EnumerateObject() )
               {
                  AddEffort( byTarget, property.Name, EffortValues( property.Value, $"{name}.{property.Name}" ) );
               }

               break;
            case null:
               break;
            default:
               throw BenchJson.Bad( FILE, name, "an array or an object" );
         }
      }

      return byTarget;
   }

   /// <summary>
   /// Adds settings to a target's list.
   /// </summary>
   /// <param name="byTarget">The lists by target.</param>
   /// <param name="target">The target.</param>
   /// <param name="settings">The settings.</param>
   private static void AddEffort( Dictionary<string, List<BenchEffort>> byTarget, string target, List<BenchEffort> settings )
   {
      if( !byTarget.TryAdd( target, settings ) )
      {
         byTarget[target].AddRange( settings );
      }
   }

   /// <summary>
   /// The settings one why row carries in its own field, under the first of <see cref="EFFORT_NAMES"/> it has.
   /// </summary>
   /// <param name="row">The why row.</param>
   /// <param name="path">Where it sits in the file.</param>
   /// <returns>The settings; none when the row has no such field.</returns>
   private static List<BenchEffort> EffortOf( JsonElement row, string path )
   {
      var all = new List<BenchEffort>();
      foreach( string name in EFFORT_NAMES )
      {
         if( BenchJson.Get( row, name ) is JsonElement value )
         {
            all.AddRange( EffortValues( value, $"{path}.{name}" ) );
         }
      }

      return all;
   }

   /// <summary>
   /// Settings written as a string, an array of settings, or an object (see <see cref="EffortFromObject"/>); any other shape fails with its path.
   /// </summary>
   /// <param name="value">The value.</param>
   /// <param name="path">Where it sits in the file.</param>
   /// <returns>The settings.</returns>
   private static List<BenchEffort> EffortValues( JsonElement value, string path )
   {
      switch( value.ValueKind )
      {
         case JsonValueKind.String when !string.IsNullOrWhiteSpace( value.GetString() ):
            return new List<BenchEffort> { new( value.GetString()!.Trim(), null ) };
         case JsonValueKind.Array:
            return value.EnumerateArray().SelectMany( ( e, i ) => EffortValues( e, $"{path}[{i}]" ) ).ToList();
         case JsonValueKind.Object:
            return EffortFromObject( value, path );
         default:
            throw new InvalidDataException( $"{path} is not search effort the page can read (a string, a list, or an object with a text, with a key and a value, with settings, or of names and values)." );
      }
   }

   /// <summary>
   /// Settings in an object: one setting for a "text" (or for a "key" and a "value"), the settings under "settings", or, for any other object, one
   /// setting per member written "name=value", as results.json writes the settings of a target. A "source" member is where the settings come from (an
   /// entry of the file's top-level list that names no source gives the run it was read from, "run", in its place), and a "target" member names the engine
   /// of a top-level entry; none of them is a setting.
   /// </summary>
   /// <param name="obj">The object.</param>
   /// <param name="path">Where it sits in the file.</param>
   /// <returns>The settings.</returns>
   private static List<BenchEffort> EffortFromObject( JsonElement obj, string path )
   {
      string? source = BenchJson.Text( obj, "source", path ) ?? BenchJson.Text( obj, "run", path );
      if( BenchJson.Get( obj, "text" ) is { ValueKind: JsonValueKind.String } text )
      {
         return new List<BenchEffort> { new( text.GetString()!.Trim(), source ) };
      }

      if( BenchJson.Get( obj, "key" ) is { ValueKind: JsonValueKind.String } key && BenchJson.Get( obj, "value" ) is JsonElement setting )
      {
         return new List<BenchEffort> { new( $"{key.GetString()!.Trim()}={Describe( setting )}", source ) };
      }

      if( BenchJson.Get( obj, "settings" ) is JsonElement inner )
      {
         return EffortValues( inner, $"{path}.settings" ).Select( e => e with { Source = e.Source ?? source } ).ToList();
      }

      return obj.EnumerateObject().Where( p => p.Name is not ( "target" or "source" or "run" ) ).Select( p => new BenchEffort( $"{p.Name}={Describe( p.Value )}", source ) ).ToList();
   }

   /// <summary>
   /// The engine settings of each engine: a list of { target, session, settings: [ { key, value, how } ] }.
   /// </summary>
   /// <param name="root">The file's root.</param>
   /// <returns>The rows, none when the file has none.</returns>
   private static List<BenchEngineSettings> ReadEngineSettings( JsonElement root )
   {
      return BenchJson.Items( root, "engineSettings", FILE ).Select( ( e, i ) =>
      {
         string path = $"engineSettings[{i}]";
         var settings = BenchJson.Items( e, "settings", path ).Select( ( x, j ) => new BenchSetting( BenchJson.RequireText( x, "key", $"{path}.settings[{j}]" ),
            BenchJson.Text( x, "value", $"{path}.settings[{j}]" ) ?? string.Empty, BenchJson.Text( x, "how", $"{path}.settings[{j}]" ) ?? string.Empty ) ).ToList();
         return new BenchEngineSettings( BenchJson.RequireText( e, "target", path ), BenchJson.Text( e, "session", path ), settings );
      } ).ToList();
   }

   /// <summary>
   /// The list of recorded statements the set marks as false or misleading ("runPageNotes"), with each correction and its sources.
   /// </summary>
   /// <param name="root">The file's root.</param>
   /// <returns>The entries in file order; none when the file has no list.</returns>
   private static IReadOnlyList<BenchRunNote> ReadRunNotes( JsonElement root )
   {
      return BenchJson.Get( root, BenchRunNotes.FIELD ) is { } list ? BenchRunNotes.Read( list, BenchRunNotes.FIELD, FILE ) : Array.Empty<BenchRunNote>();
   }

   /// <summary>
   /// The engine texts the runs recorded, clause by clause ("recordedTexts"), with the class and basis the report gives each clause.
   /// </summary>
   /// <param name="root">The file's root.</param>
   /// <returns>The texts in file order; none when the file has none.</returns>
   private static IReadOnlyList<BenchRecordedText> ReadRecordedTexts( JsonElement root )
   {
      return BenchJson.Items( root, "recordedTexts", FILE ).Select( ( e, i ) =>
      {
         string path = $"recordedTexts[{i}]";
         var clauses = BenchJson.Items( e, "clauses", path ).Select( ( c, j ) => new BenchClause( BenchJson.RequireText( c, "text", $"{path}.clauses[{j}]" ),
            BenchJson.Text( c, "class", $"{path}.clauses[{j}]" ) ?? string.Empty, BenchJson.Strings( c, "basis", $"{path}.clauses[{j}]" ),
            BenchJson.Flag( c, "printed", $"{path}.clauses[{j}]" ) ?? true, BenchJson.Text( c, "notPrinted", $"{path}.clauses[{j}]" ) ) ).ToList();
         return new BenchRecordedText( BenchJson.Text( e, "target", path ) ?? string.Empty, BenchJson.RequireText( e, "field", path ), BenchJson.Text( e, "run", path ), BenchJson.Get( e, "note" ) is { } note ? Describe( note ) : null, clauses );
      } ).ToList();
   }

   /// <summary>
   /// The audit block.
   /// </summary>
   /// <param name="root">The file's root.</param>
   /// <returns>The audit, or null when the file has none.</returns>
   private static BenchAudit? ReadAudit( JsonElement root )
   {
      JsonElement? audit = BenchJson.Get( root, "audit" );
      return audit == null ? null : new BenchAudit( BenchJson.Whole( audit.Value, "sentencesChecked", "audit" ) ?? 0, BenchJson.Items( audit.Value, "failures", "audit" ).Select( f => Describe( f ) ).ToList(),
         BenchJson.Text( audit.Value, "factsSha256", "audit" ), BenchJson.Text( audit.Value, "exclusionsSha256", "audit" ), BenchJson.Text( audit.Value, "observerSha256", "audit" ),
         BenchJson.Whole( audit.Value, "factsChecked", "audit" ), BenchJson.Whole( audit.Value, "rowSentencesChecked", "audit" ) );
   }

   /// <summary>
   /// A list of objects, each as its fields in file order.
   /// </summary>
   /// <param name="parent">The parent object.</param>
   /// <param name="name">The list's name.</param>
   /// <param name="path">Where the parent sits in the file.</param>
   /// <returns>The records.</returns>
   private static List<IReadOnlyList<BenchPair>> ReadRecords( JsonElement parent, string name, string path )
   {
      return BenchJson.Items( parent, name, path ).Select( ( e, i ) => ReadPairs( e, $"{path}.{name}[{i}]" ) ).ToList();
   }

   /// <summary>
   /// An object's fields as key and text pairs in file order; an empty list for a missing object.
   /// </summary>
   /// <param name="element">The object, or null.</param>
   /// <param name="path">Where it sits in the file.</param>
   /// <returns>The pairs.</returns>
   private static IReadOnlyList<BenchPair> ReadPairs( JsonElement? element, string path )
   {
      if( element == null )
      {
         return Array.Empty<BenchPair>();
      }

      return element.Value.ValueKind == JsonValueKind.Object
         ? element.Value.EnumerateObject().Select( p => new BenchPair( p.Name, Describe( p.Value ) ) ).ToList()
         : throw new InvalidDataException( $"{path} is not an object." );
   }

   /// <summary>
   /// Any JSON value as short text for a cell: strings as written, numbers as written, true and false,
   /// arrays and objects as their members joined.
   /// </summary>
   /// <param name="value">The value.</param>
   /// <returns>The text; empty for null or a missing value.</returns>
   private static string Describe( JsonElement value )
   {
      return value.ValueKind switch
      {
         JsonValueKind.String => value.GetString() ?? string.Empty,
         JsonValueKind.Number => value.TryGetInt64( out long n ) ? n.ToString( CultureInfo.InvariantCulture ) : value.GetDouble().ToString( "0.###", CultureInfo.InvariantCulture ),
         JsonValueKind.True => "true",
         JsonValueKind.False => "false",
         JsonValueKind.Array => string.Join( ", ", value.EnumerateArray().Select( Describe ) ),
         JsonValueKind.Object => string.Join( "; ", value.EnumerateObject().Select( p => $"{p.Name}: {Describe( p.Value )}" ) ),
         _ => string.Empty,
      };
   }

   /// <summary>
   /// The checks across blocks: the headline, the stopped state, the rows of each metric and the
   /// sessions the per-session figures name.
   /// </summary>
   /// <param name="model">The model read.</param>
   /// <param name="two">True when the file holds two sessions or more.</param>
   private static void Validate( BenchConsolidated model, bool two )
   {
      int headlines = model.Sentences.Count( s => SectionOf( s.Slot ) == BenchSlots.HEADLINE );
      if( model.Stopped && headlines > 0 )
      {
         throw new InvalidDataException( $"{FILE}: a stopped set must carry no headline sentence." );
      }

      if( !model.Stopped && headlines != 1 )
      {
         throw new InvalidDataException( $"{FILE}: a set that is not stopped must carry exactly one headline sentence, and this one carries {headlines}." );
      }

      if( model.Sentences.FirstOrDefault( s => SectionOf( s.Slot ) == BenchSlots.HEADLINE ) is { } headline && WordCount( headline.Text ) > MAX_HEADLINE_WORDS )
      {
         throw new InvalidDataException( $"{FILE}: the headline holds {WordCount( headline.Text )} words; the most the page prints is {MAX_HEADLINE_WORDS}." );
      }

      if( model.Metrics.Count == 0 )
      {
         throw new InvalidDataException( $"{FILE}: metrics holds no table." );
      }

      var names = model.Sessions.Select( s => s.Name ).ToHashSet( StringComparer.Ordinal );
      foreach( BenchMetric metric in model.Metrics )
      {
         ValidateMetric( metric, names );
      }

      if( two && model.Why.Count == 0 )
      {
         throw new InvalidDataException( $"{FILE}: two sessions need the facts and costs table (why)." );
      }

      ValidateSearchModes( model );
      ValidateReuse( model );
      ValidateRunNotes( model );
   }

   /// <summary>
   /// Every run a correction names must be a run the set uses: a correction for a run the set does not link to would be printed on no page a reader reaches
   /// from the set, and a mistyped folder name must fail here and not vanish.
   /// </summary>
   /// <param name="model">The model read.</param>
   private static void ValidateRunNotes( BenchConsolidated model )
   {
      var used = model.UsedRunRefs.Select( r => BenchFolderNames.LastSegment( r.Folder ) ).ToHashSet( StringComparer.Ordinal );
      foreach( ( BenchRunNote note, int i ) in model.RunNoteList.Select( ( n, i ) => ( n, i ) ) )
      {
         string? unknown = note.Runs.FirstOrDefault( r => !used.Contains( r ) );
         if( unknown != null )
         {
            throw new InvalidDataException( $"{BenchRunNotes.FIELD}[{i}] names run {unknown}, which the sessions and the basis runs do not list." );
         }
      }
   }

   /// <summary>
   /// The search mode a metric row prints for an engine must be the mode the engine's own search fact states: the column and the fact are two prints of one
   /// recorded state, and a row that says "approximate" beside a fact that says "exact" (or the reverse) would show the reader a label the facts table
   /// contradicts. Modes are compared by their first word (see <see cref="BenchFormat.SameMode"/>), so a mode the report words "approximate (engine's own
   /// report)" in one place and "approximate" in another is one mode, and "approximate" against "exact" is a contradiction. A row whose engine has no search
   /// fact that states a mode cannot be checked and is not refused; a row whose engine has such facts and none of them states the row's mode is refused,
   /// naming the engine, the table and both modes.
   /// </summary>
   /// <param name="model">The model read.</param>
   private static void ValidateSearchModes( BenchConsolidated model )
   {
      foreach( BenchMetric metric in model.Metrics )
      {
         foreach( BenchRow row in metric.Rows.Where( r => r.SearchMode != null ) )
         {
            List<string> stated = model.Why.Where( w => w.Target == row.Target ).SelectMany( w => w.Facts ).Where( f => f.Kind == SEARCH_KIND && f.Mode != null ).Select( f => f.Mode! ).ToList();
            if( stated.Count > 0 && !stated.Any( m => BenchFormat.SameMode( m, row.SearchMode! ) ) )
            {
               throw new InvalidDataException( $"metrics[{metric.Metric}].{row.Target}.searchMode is \"{row.SearchMode}\", and the search facts of {row.Target} state {string.Join( ", ", stated.Distinct( StringComparer.Ordinal ).Select( m => $"\"{m}\"" ) )}." );
            }
         }
      }
   }

   /// <summary>
   /// Every reuse sentence must name, in its slot, a session the file holds (a compared session or a session of basis runs), so a run page can print it on
   /// the pages of that session's runs and on no other page. A reuse sentence the page could not place would be printed nowhere a reader looks, so the file
   /// is refused instead.
   /// </summary>
   /// <param name="model">The model read.</param>
   private static void ValidateReuse( BenchConsolidated model )
   {
      var known = model.Sessions.Select( s => s.Name ).Concat( model.Basis?.Runs.Select( r => r.Session ).OfType<string>() ?? Enumerable.Empty<string>() ).ToHashSet( StringComparer.Ordinal );
      foreach( BenchSentence sentence in model.Sentences.Where( s => s.Slot == BenchRunUse.REUSE_SLOT || s.Slot.StartsWith( BenchSlots.REUSE_PREFIX, StringComparison.Ordinal ) ) )
      {
         if( BenchSlots.ReuseTarget( sentence.Slot ) is not { } target )
         {
            throw new InvalidDataException( $"{FILE}: the slot \"{sentence.Slot}\" is a reuse slot that does not read \"{BenchSlots.REUSE_PREFIX}SESSION\", \"{BenchSlots.REUSE_PREFIX}{BenchSlots.ROLE_CLAIM}.SESSION\" or \"{BenchSlots.REUSE_PREFIX}{BenchSlots.ROLE_BASIS}.SESSION\"." );
         }

         if( !known.Contains( target.Session ) )
         {
            throw new InvalidDataException( $"{FILE}: the slot \"{sentence.Slot}\" names session \"{target.Session}\", which neither the sessions nor the basis runs list." );
         }
      }
   }

   /// <summary>
   /// The checks of one metric table: one row per engine, per-session figures that name a session of
   /// the file, separations that are mutual and name rows of the table, and an order that agrees with
   /// every separation the file does not list.
   /// </summary>
   /// <param name="metric">The table.</param>
   /// <param name="sessions">The session names of the file.</param>
   private static void ValidateMetric( BenchMetric metric, HashSet<string> sessions )
   {
      string path = $"metrics[{metric.Metric}]";
      var byTarget = new Dictionary<string, BenchRow>( StringComparer.Ordinal );
      foreach( BenchRow row in metric.Rows )
      {
         if( !byTarget.TryAdd( row.Target, row ) )
         {
            throw new InvalidDataException( $"{path} holds {row.Target} twice." );
         }

         if( row.PerSession.Keys.FirstOrDefault( k => !sessions.Contains( k ) ) is { } unknown )
         {
            throw new InvalidDataException( $"{path}.{row.Target}.perSession names session \"{unknown}\", which sessions does not list." );
         }

         if( row.Status == RANKED && row.Median == null )
         {
            throw new InvalidDataException( $"{path}.{row.Target} is ranked and has no median." );
         }
      }

      foreach( BenchRow row in metric.Rows )
      {
         string bad = row.NotSeparatedFrom.FirstOrDefault( t => !byTarget.ContainsKey( t ) || t == row.Target ) ?? string.Empty;
         if( bad.Length > 0 )
         {
            throw new InvalidDataException( $"{path}.{row.Target}.notSeparatedFrom names {bad}, which is this row or not a row of the table." );
         }
      }

      ValidateOrder( metric, byTarget, path );
   }

   /// <summary>
   /// For every pair of ranked rows: the two rows must agree on whether they are separated, and a
   /// separated pair must be in the order of the medians.
   /// </summary>
   /// <param name="metric">The table.</param>
   /// <param name="byTarget">Rows by target.</param>
   /// <param name="path">Where the table sits, for messages.</param>
   private static void ValidateOrder( BenchMetric metric, Dictionary<string, BenchRow> byTarget, string path )
   {
      List<BenchRow> ranked = metric.Rows.Where( r => r.Status == RANKED && !r.IsNotHeld ).ToList();
      for( int i = 1; i < ranked.Count; i++ )
      {
         double before = ranked[i - 1].Median!.Value;
         double after = ranked[i].Median!.Value;
         if( metric.LowerIsBetter ? after < before : after > before )
         {
            throw new InvalidDataException( $"{path}: {ranked[i].Target} is listed after {ranked[i - 1].Target} and its median is the better one; the rows are not in median order." );
         }
      }

      for( int i = 0; i < ranked.Count; i++ )
      {
         for( int j = i + 1; j < ranked.Count; j++ )
         {
            bool ab = ranked[i].NotSeparatedFrom.Contains( ranked[j].Target );
            bool ba = ranked[j].NotSeparatedFrom.Contains( ranked[i].Target );
            if( ab != ba )
            {
               throw new InvalidDataException( $"{path}: {ranked[i].Target} and {ranked[j].Target} disagree about whether they are separated." );
            }

            bool inOrder = metric.LowerIsBetter ? ranked[i].Median < ranked[j].Median : ranked[i].Median > ranked[j].Median;
            if( !ab && !inOrder )
            {
               throw new InvalidDataException( $"{path}: {ranked[i].Target} is listed above {ranked[j].Target} and is separated from it, but its median is not the better one." );
            }
         }
      }
   }

   /// <summary>
   /// The section of a slot name: the part before the first dot.
   /// </summary>
   /// <param name="slot">The slot.</param>
   /// <returns>The section.</returns>
   internal static string SectionOf( string slot )
   {
      int dot = slot.IndexOf( '.' );
      return dot < 0 ? slot : slot[..dot];
   }

   /// <summary>
   /// How many words a text holds (runs of characters between spaces).
   /// </summary>
   /// <param name="text">Text.</param>
   /// <returns>The count.</returns>
   internal static int WordCount( string text )
   {
      return text.Split( new[] { ' ', '\t', '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries ).Length;
   }

   #endregion Private Methods
}
