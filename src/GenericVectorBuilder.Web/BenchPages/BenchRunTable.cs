using System.Globalization;
using System.Text;
using System.Text.Json;

namespace GenericVectorBuilder.Web.BenchPages;

/// <summary>
/// One engine's figures in one run, as the run page's table shows them.
/// </summary>
/// <param name="Key">The target name the Bench tool used, e.g. "sql-diskann".</param>
/// <param name="Name">The friendly name a reader sees.</param>
/// <param name="P50Ms">Median latency of one search at a time, in milliseconds, or null when missing.</param>
/// <param name="Qps1">Searches per second with one searcher, or null.</param>
/// <param name="Qps8">Searches per second with eight searchers at once, or null.</param>
/// <param name="ExactP50Ms">Median latency in exact mode, or null.</param>
/// <param name="Recall">Recall as the run recorded it (a share from 0 to 1), or null.</param>
/// <param name="RecallHits">The hits behind the recall (the recorded count, or the share times the queries and top when that is a whole number), or null.</param>
/// <param name="ClientCpuMs1">Client CPU per search with one searcher, in milliseconds, or null.</param>
/// <param name="Flags">Warnings about this engine's numbers in this run; empty when there are none.</param>
public sealed record BenchRunRow( string Key, string Name, double? P50Ms, double? Qps1, double? Qps8, double? ExactP50Ms, double? Recall, long? RecallHits, double? ClientCpuMs1,
   IReadOnlyList<BenchFlagEntry> Flags );

/// <summary>
/// The figures of one run: a row per engine that has a result, the engines with none, and the run's conditions.
/// Why one shape for a run alone: a run page states what one run measured, with no order and no
/// headline, because one run has no spread to say whether two engines differ.
/// </summary>
/// <param name="Rows">Engines with a result, in alphabetical order of their names.</param>
/// <param name="NoResult">Friendly names of engines that produced no throughput figure.</param>
/// <param name="Conditions">The conditions the run recorded.</param>
/// <param name="QueryHitsOf">How many hits a perfect run has (queries times top), or null when the run does not say.</param>
public sealed record BenchRunSummary( IReadOnlyList<BenchRunRow> Rows, IReadOnlyList<string> NoResult, BenchConditions Conditions, long? QueryHitsOf );

/// <summary>
/// Reads one run's results.json into a <see cref="BenchRunSummary"/> and prints it.
/// Malformed input throws <see cref="InvalidDataException"/> or <see cref="JsonException"/>, so the page can
/// show the failure instead of a quietly wrong table.
/// </summary>
public static class BenchRunTable
{
   #region Data Members

   /// <summary>Most engines one file may hold; more means the file is not what we think it is.</summary>
   public const int MAX_ENGINES = 200;

   /// <summary>How far a share times the hits of a perfect run may be from a whole number and still count as that whole number.</summary>
   public const double WHOLE_TOLERANCE = 1e-6;

   #endregion Data Members

   #region Public Methods

   /// <summary>
   /// Reads one run's results.json: targets[] with name, search.qps by level, search.p50Ms, exactP50Ms,
   /// recall and the client CPU per search.
   /// </summary>
   /// <param name="json">File text.</param>
   /// <returns>The run's figures.</returns>
   public static BenchRunSummary Read( string json )
   {
      using JsonDocument doc = JsonDocument.Parse( json );
      JsonElement root = doc.RootElement;
      if( root.ValueKind != JsonValueKind.Object || !root.TryGetProperty( "targets", out JsonElement targets ) || targets.ValueKind != JsonValueKind.Array )
      {
         throw new InvalidDataException( "results.json has no targets list." );
      }

      JsonElement conditionsElement = root.TryGetProperty( "conditions", out JsonElement conditions ) && conditions.ValueKind == JsonValueKind.Object ? conditions : default;
      JsonElement passes = conditionsElement.ValueKind == JsonValueKind.Object && conditionsElement.TryGetProperty( "passes", out JsonElement p ) ? p : default;
      long? queries = Whole( root, "queryCount" );
      long? top = Whole( root, "top" );
      long? of = queries != null && top != null ? queries * top : null;
      var rows = new List<BenchRunRow>();
      var missing = new List<string>();
      foreach( JsonElement t in targets.EnumerateArray() )
      {
         if( rows.Count + missing.Count >= MAX_ENGINES )
         {
            throw new InvalidDataException( $"More than {MAX_ENGINES} engines in one file; refusing to draw it." );
         }

         string key = t.ValueKind == JsonValueKind.Object && t.TryGetProperty( "name", out JsonElement n ) && n.ValueKind == JsonValueKind.String ? n.GetString() ?? "?" : "?";
         JsonElement search = t.ValueKind == JsonValueKind.Object && t.TryGetProperty( "search", out JsonElement s ) ? s : default;
         JsonElement qps = search.ValueKind == JsonValueKind.Object && search.TryGetProperty( "qps", out JsonElement q ) ? q : default;
         double? eight = Number( qps, "8" );
         if( eight == null )
         {
            missing.Add( BenchNames.FriendlyName( key ) );
            continue;
         }

         double? recall = Number( search, "recall" );
         rows.Add( new BenchRunRow( key, BenchNames.FriendlyName( key ), Number( search, "p50Ms" ), Number( qps, "1" ), eight, Number( search, "exactP50Ms" ), recall, Hits( search, recall, of ),
            ClientCpuAtOne( search ), t.ValueKind == JsonValueKind.Object ? BenchRunFlags.FromTarget( t, passes, conditionsElement ) : Array.Empty<BenchFlagEntry>() ) );
      }

      return new BenchRunSummary( rows.OrderBy( r => r.Name, StringComparer.Ordinal ).ToList(), missing, BenchConditions.FromRun( root ), of );
   }

   /// <summary>
   /// The block a run page opens with: the run's conditions, its table (alphabetical, no order claim), the
   /// engines with no result, and the legends the table uses.
   /// </summary>
   /// <param name="summary">The run's figures.</param>
   /// <param name="notes">Notes to print under an engine's name, with their sources, by target (taken from the sets that use the run), or null for none.</param>
   /// <param name="mark">Turns a recorded text of the conditions (the warm-up and clock lines) into HTML with the run's correction markers; null prints the text encoded, unmarked.</param>
   /// <returns>HTML fragment.</returns>
   public static string Block( BenchRunSummary summary, IReadOnlyDictionary<string, BenchRowNote>? notes = null, Func<string, string>? mark = null )
   {
      var html = new StringBuilder( "<section class=\"bench-summary\">" );
      html.Append( Conditions( summary.Conditions, mark ) );
      html.Append( "<div class=\"preview\"><table class=\"bench-table\"><thead><tr><th>Engine</th><th class=\"n\">p50 (ms)</th><th class=\"n\">Searches per second, one searcher</th><th class=\"n\">Searches per second, eight searchers at once</th>" );
      html.Append( $"<th class=\"n\">Exact mode p50 (ms)</th><th class=\"n\">{BenchFormat.Enc( BenchLegends.H_CLIENT_CPU_1 )}</th><th>Recall in hits</th><th>Flags</th></tr></thead><tbody>" );
      foreach( BenchRunRow row in summary.Rows )
      {
         html.Append( $"<tr data-target=\"{BenchFormat.Enc( row.Key )}\"><td>{BenchFormat.Enc( row.Name )}{NoteOf( notes, row.Key )}</td><td class=\"n\">{BenchFormat.Enc( BenchFormat.Ms( row.P50Ms ) )}</td>" );
         html.Append( $"<td class=\"n\">{BenchFormat.Enc( row.Qps1 == null ? "-" : BenchFormat.Count( row.Qps1.Value ) )}</td><td class=\"n\">{BenchFormat.Enc( row.Qps8 == null ? "-" : BenchFormat.Count( row.Qps8.Value ) )}</td>" );
         html.Append( $"<td class=\"n\">{BenchFormat.Enc( BenchFormat.Ms( row.ExactP50Ms ) )}</td><td class=\"n\">{BenchFormat.Enc( BenchFormat.Ms( row.ClientCpuMs1 ) )}</td>" );
         html.Append( $"<td>{BenchFormat.Enc( RecallText( row, summary.QueryHitsOf ) )}</td><td>{BenchConsolidatedHtml.Markers( new BenchRow( row.Key, row.Name, "ranked", new Dictionary<string, BenchRange>(), null, Array.Empty<string>(), row.Flags, null ) )}</td></tr>" );
      }

      foreach( string name in summary.NoResult )
      {
         html.Append( $"<tr><td>{BenchFormat.Enc( name )}</td><td colspan=\"7\" class=\"muted\">no result</td></tr>" );
      }

      html.Append( "</tbody></table></div>" );
      html.Append( $"<p class=\"bench-legend muted\">{BenchFormat.Enc( BenchLegends.RUN_ORDER )}</p><p class=\"bench-legend muted\">{BenchFormat.Enc( BenchLegends.DASH )}</p>" );
      return html.Append( Legend( summary ) ).Append( "</section>" ).ToString();
   }

   /// <summary>
   /// The conditions of a run as label and value pairs in one paragraph, or the plain statement that none were recorded.
   /// </summary>
   /// <param name="conditions">The conditions.</param>
   /// <param name="mark">Turns a recorded text into HTML with the correction markers; null prints it encoded, unmarked.</param>
   /// <returns>HTML fragment.</returns>
   public static string Conditions( BenchConditions conditions, Func<string, string>? mark = null )
   {
      var pairs = new List<string>();
      pairs.AddRange( conditions.Machine.Select( p => $"{BenchFormat.Enc( p.Key )} {BenchFormat.Enc( p.Value )}" ) );
      pairs.Add( $"Governor {Value( conditions.Governor, conditions.GovernorIsWrong )}" );
      pairs.Add( $"Partition {Value( conditions.Partition, false )}" );
      pairs.Add( conditions.Build == null ? "Build not recorded" : conditions.BuildIsWrong ? $"<span class=\"bench-no\">{BenchFormat.Enc( conditions.Build )} build</span>" : $"{BenchFormat.Enc( conditions.Build )} build" );
      if( conditions.CodeCommit != null )
      {
         pairs.Add( $"{BenchLegends.L_CODE_COMMIT} {BenchFormat.Enc( conditions.CodeCommit )}" );
      }

      if( conditions.ExactSeconds != null )
      {
         pairs.Add( $"Exact mode seconds {BenchFormat.Enc( conditions.ExactSeconds )}" );
      }

      var html = new StringBuilder( $"<p class=\"bench-conditions muted\">Machine: {string.Join( "; ", pairs )}.</p>" );
      html.Append( Warmup( conditions, mark ) ).Append( ClockLines( conditions, mark ) );
      return html.ToString();
   }

   #endregion Public Methods

   #region Private Methods

   /// <summary>
   /// The warm-up line of a run page: the run's own note on its warm-up, or, when no note describes it, the value of its warmupSearches field beside
   /// the statement that no note describes the method. Never a bare count: the warm-up of a run is timed (a floor of seconds and of searches), and a count
   /// printed as "searches" states a method the run did not use.
   /// </summary>
   /// <param name="conditions">The conditions.</param>
   /// <param name="mark">Marks a recorded text, or null.</param>
   /// <returns>HTML fragment; empty when the run recorded neither.</returns>
   private static string Warmup( BenchConditions conditions, Func<string, string>? mark )
   {
      if( conditions.WarmupMethod != null )
      {
         return $"<p class=\"bench-conditions muted\" data-line=\"warmup\">{BenchFormat.Enc( BenchLegends.L_WARMUP_RECORDED )} {Marked( conditions.WarmupMethod, mark )}</p>";
      }

      return conditions.WarmupField == null ? string.Empty
         : $"<p class=\"bench-conditions muted\" data-line=\"warmup\">{BenchFormat.Enc( BenchLegends.L_WARMUP_FIELD )} {BenchFormat.Enc( conditions.WarmupField )} {BenchFormat.Enc( BenchLegends.L_WARMUP_NO_NOTE )}</p>";
   }

   /// <summary>
   /// The clock lines of a run page: the run's clock block as fields (a v8 run), or, for a run with no block, the clause of its notes that states the
   /// pin, or the statement that none is recorded when machine control was on.
   /// </summary>
   /// <param name="conditions">The conditions.</param>
   /// <param name="mark">Marks a recorded text, or null.</param>
   /// <returns>HTML fragment; empty when the run says nothing about its clock.</returns>
   private static string ClockLines( BenchConditions conditions, Func<string, string>? mark )
   {
      if( conditions.Clock.Count > 0 )
      {
         return $"<p class=\"bench-conditions muted\">Clock: {string.Join( "; ", conditions.Clock.Select( p => $"{BenchFormat.Enc( p.Key )} {BenchFormat.Enc( p.Value )}" ) )}.</p>";
      }

      if( conditions.ClockNote != null )
      {
         return $"<p class=\"bench-conditions muted\" data-line=\"clock\">{BenchFormat.Enc( BenchLegends.L_CLOCK_RECORDED )} {Marked( conditions.ClockNote, mark )}</p>";
      }

      return conditions.NoClockPinRecorded ? $"<p class=\"bench-conditions muted\" data-line=\"clock\">{BenchFormat.Enc( BenchLegends.L_NO_CLOCK_PIN )}</p>" : string.Empty;
   }

   /// <summary>
   /// A recorded text as HTML: marked by the caller's function when it gave one, encoded as it stands otherwise.
   /// </summary>
   /// <param name="text">The recorded text.</param>
   /// <param name="mark">The caller's marking function, or null.</param>
   /// <returns>HTML fragment.</returns>
   private static string Marked( string text, Func<string, string>? mark )
   {
      return mark == null ? BenchFormat.Enc( text ) : mark( text );
   }

   /// <summary>
   /// The note under an engine's name, if the sets that use the run give one.
   /// </summary>
   /// <param name="notes">Notes by target, or null.</param>
   /// <param name="key">The target.</param>
   /// <returns>HTML fragment; empty when there is none.</returns>
   private static string NoteOf( IReadOnlyDictionary<string, BenchRowNote>? notes, string key )
   {
      if( notes == null || !notes.TryGetValue( key, out BenchRowNote? note ) || string.IsNullOrWhiteSpace( note.Text ) )
      {
         return string.Empty;
      }

      return $"<br><small class=\"muted bench-note\">{BenchFormat.Enc( note.Text )}</small>{( note.Sources.Count > 0 ? BenchConsolidatedHtml.SourceList( note.Sources ) : string.Empty )}";
   }

   /// <summary>
   /// The legend of every flag code the table shows.
   /// </summary>
   /// <param name="summary">The run's figures.</param>
   /// <returns>HTML fragment; empty when no row has a flag.</returns>
   private static string Legend( BenchRunSummary summary )
   {
      var codes = summary.Rows.SelectMany( r => r.Flags.Select( f => f.Code ) ).Distinct( StringComparer.Ordinal ).OrderBy( BenchLegends.Order ).ToList();
      if( codes.Count == 0 )
      {
         return string.Empty;
      }

      var html = new StringBuilder( $"<div class=\"bench-legend\"><p class=\"muted\">{BenchFormat.Enc( BenchLegends.FLAGS_INTRO )}</p><ul>" );
      foreach( IGrouping<string, string> group in codes.GroupBy( c => BenchLegends.Marker( c ) + "|" + BenchLegends.Legend( c ) ) )
      {
         string first = group.First();
         html.Append( $"<li><abbr class=\"bench-mark\">{BenchFormat.Enc( BenchLegends.Marker( first ) )}</abbr> {string.Join( ", ", group.Select( c => $"<code>{BenchFormat.Enc( c )}</code>" ) )} {BenchFormat.Enc( BenchLegends.Legend( first ) )}</li>" );
      }

      html.Append( $"</ul><details class=\"bench-evidence\"><summary>{BenchFormat.Enc( BenchLegends.L_EVIDENCE )}</summary><ul>" );
      foreach( BenchRunRow row in summary.Rows.Where( r => r.Flags.Count > 0 ) )
      {
         foreach( BenchFlagEntry flag in row.Flags )
         {
            html.Append( $"<li><strong>{BenchFormat.Enc( row.Name )}</strong> <code>{BenchFormat.Enc( flag.Code )}</code> {BenchFormat.Enc( flag.Text )}</li>" );
         }
      }

      return html.Append( "</ul></details></div>" ).ToString();
   }

   /// <summary>
   /// The recall cell: "hits of perfect" when the hits are known, else the recorded share.
   /// </summary>
   /// <param name="row">The row.</param>
   /// <param name="of">Hits of a perfect run, or null.</param>
   /// <returns>The text; "-" when the run recorded no recall.</returns>
   private static string RecallText( BenchRunRow row, long? of )
   {
      if( row.Recall == null )
      {
         return "-";
      }

      return row.RecallHits != null && of != null ? $"{row.RecallHits} of {of}" : row.Recall.Value.ToString( "0.###", CultureInfo.InvariantCulture );
   }

   /// <summary>
   /// The hits behind a recall: the field the run recorded (recallHits), or the share times a perfect run's hits when
   /// that is a whole number within <see cref="WHOLE_TOLERANCE"/>; null otherwise.
   /// </summary>
   /// <param name="search">The target's search section.</param>
   /// <param name="recall">The recorded share.</param>
   /// <param name="of">Hits of a perfect run.</param>
   /// <returns>The hits, or null.</returns>
   private static long? Hits( JsonElement search, double? recall, long? of )
   {
      if( Whole( search, "recallHits" ) is long recorded )
      {
         return recorded;
      }

      if( recall == null || of == null )
      {
         return null;
      }

      double raw = recall.Value * of.Value;
      return Math.Abs( raw - Math.Round( raw ) ) <= WHOLE_TOLERANCE ? (long)Math.Round( raw ) : null;
   }

   /// <summary>
   /// The client's CPU per search with one searcher, in milliseconds: "clientCpuMsPerSearch" keyed by level ("1" or "default@1").
   /// </summary>
   /// <param name="search">One run's search section.</param>
   /// <returns>The milliseconds, or null when the results did not record it.</returns>
   private static double? ClientCpuAtOne( JsonElement search )
   {
      if( search.ValueKind != JsonValueKind.Object || !search.TryGetProperty( "clientCpuMsPerSearch", out JsonElement levels ) || levels.ValueKind != JsonValueKind.Object )
      {
         return null;
      }

      return Number( levels, "1" ) ?? Number( levels, "default@1" );
   }

   /// <summary>
   /// A condition value for the conditions line: "not recorded" for null, marked when wrong.
   /// </summary>
   /// <param name="value">The value, or null.</param>
   /// <param name="wrong">True to mark it as a flag.</param>
   /// <returns>HTML fragment.</returns>
   private static string Value( string? value, bool wrong )
   {
      return value == null ? "not recorded" : wrong ? $"<span class=\"bench-no\">{BenchFormat.Enc( value )}</span>" : BenchFormat.Enc( value );
   }

   /// <summary>
   /// A finite, non-negative number property, or null.
   /// </summary>
   /// <param name="parent">Object holding the property.</param>
   /// <param name="name">Property name.</param>
   /// <returns>The value or null.</returns>
   private static double? Number( JsonElement parent, string name )
   {
      return parent.ValueKind == JsonValueKind.Object && parent.TryGetProperty( name, out JsonElement v ) && v.ValueKind == JsonValueKind.Number
         && v.TryGetDouble( out double d ) && double.IsFinite( d ) && d >= 0 ? d : null;
   }

   /// <summary>
   /// A whole-number property, or null.
   /// </summary>
   /// <param name="parent">Object holding the property.</param>
   /// <param name="name">Property name.</param>
   /// <returns>The number, or null when absent or not a whole number.</returns>
   private static long? Whole( JsonElement parent, string name )
   {
      return parent.ValueKind == JsonValueKind.Object && parent.TryGetProperty( name, out JsonElement v ) && v.ValueKind == JsonValueKind.Number && v.TryGetInt64( out long n ) && n >= 0 ? n : null;
   }

   #endregion Private Methods
}
