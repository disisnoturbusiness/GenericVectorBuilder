using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using GenericVectorBuilder.Bench.Stats;
using GenericVectorBuilder.Bench.Targets;

namespace GenericVectorBuilder.Bench.Report;

/// <summary>
/// Writes a run's results.json (everything, for scripts) and results.md (one comparison table
/// plus the details behind it, for people) into the run's folder.
/// Why both are rewritten after every target: a run-all over many engines takes hours, and a
/// crash or a stopped run must still leave every finished engine's numbers on disk.
/// </summary>
public static class ResultsWriter
{
   #region Data Members

   private static readonly JsonSerializerOptions JSON = new()
   {
      WriteIndented = true,
      PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
      NumberHandling = JsonNumberHandling.AllowNamedFloatingPointLiterals,
      DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
   };

   private const string TEMPORARY = ".tmp";

   #endregion Data Members

   #region Public Methods

   /// <summary>
   /// Writes both files. Both texts are built first, then each goes to a temporary file in the
   /// same folder and is moved over its target, so a failure while rendering writes nothing and a
   /// crash while writing never leaves a half-written results.json or results.md beside a good one.
   /// Why: a run-all takes hours and rewrites these files after every target; the readers (the
   /// consolidation, the web page, the heartbeat) must only ever see a whole file.
   /// </summary>
   /// <param name="report">The run.</param>
   /// <param name="folder">The run's folder (created if missing).</param>
   public static void Write( BenchReport report, string folder )
   {
      string json = JsonSerializer.Serialize( report, JSON );
      string markdown = Markdown( report );
      Directory.CreateDirectory( folder );
      string jsonTemporary = Path.Combine( folder, "results.json" + TEMPORARY );
      string markdownTemporary = Path.Combine( folder, "results.md" + TEMPORARY );
      try
      {
         File.WriteAllText( jsonTemporary, json );
         File.WriteAllText( markdownTemporary, markdown );
         File.Move( jsonTemporary, Path.Combine( folder, "results.json" ), true );
         File.Move( markdownTemporary, Path.Combine( folder, "results.md" ), true );
      }
      finally
      {
         DeleteIfPresent( jsonTemporary );
         DeleteIfPresent( markdownTemporary );
      }
   }

   /// <summary>
   /// Adds one entry to the "conditions" object of a results.json that already holds it (machine
   /// control writes "conditions" after this writer, replacing it whole each time, so a caller
   /// that adds an entry must do so after that write). Written to a temporary file and moved.
   /// Why here: the run's build and boot identity belong in "conditions" beside the clock and the
   /// governor, and the object is replaced on every rewrite of the file.
   /// </summary>
   /// <param name="folder">The run's folder.</param>
   /// <param name="name">Entry name, e.g. "build" or "boot".</param>
   /// <param name="entry">The entry's JSON value.</param>
   /// <exception cref="InvalidDataException">results.json is not a JSON object, or has no "conditions" object to add to.</exception>
   public static void AddCondition( string folder, string name, JsonNode entry )
   {
      string path = Path.Combine( folder, "results.json" );
      JsonObject root = JsonNode.Parse( File.ReadAllText( path ) ) as JsonObject ?? throw new InvalidDataException( $"{path} is not a JSON object." );
      JsonObject conditions = root["conditions"] as JsonObject ?? throw new InvalidDataException( $"{path} has no \"conditions\" object to add \"{name}\" to." );
      conditions[name] = entry;
      string temporary = path + TEMPORARY;
      try
      {
         File.WriteAllText( temporary, root.ToJsonString( JSON ) );
         File.Move( temporary, path, true );
      }
      finally
      {
         DeleteIfPresent( temporary );
      }
   }

   /// <summary>
   /// Renders the Markdown report.
   /// </summary>
   /// <param name="report">The run.</param>
   /// <returns>Markdown text.</returns>
   public static string Markdown( BenchReport report )
   {
      var md = new StringBuilder();
      md.AppendLine( $"# Vector engine benchmark: {report.Pipeline}" ).AppendLine();
      md.AppendLine( $"Run {report.StartedUtc} ({report.Command}). Command line:" ).AppendLine();
      md.AppendLine( "```" ).AppendLine( report.CommandLine ).AppendLine( "```" ).AppendLine();
      MachineFacts m = report.Machine;
      md.AppendLine( $"- Machine: {m.Host}, {m.Cpu} ({m.LogicalCpus} logical CPUs), {m.RamGiB} GiB RAM, GPU {m.Gpu}, {m.Os}, {m.DotNet}, load average at start {m.LoadAverage}" );
      md.AppendLine( $"- Data: {report.Rows:N0} vectors x {report.Dimension} dims, collection `{report.Collection}`. {report.Source}" );
      md.AppendLine( $"- Queries: {report.Queries}. Top {report.Top}. Throughput at concurrency {string.Join( ", ", report.Concurrency )} for {report.SecondsPerLevel} s each." );
      if( report.Targets.Any( t => t.Search != null ) )
      {
         md.AppendLine( $"- {ConsolidateFraming.Title( report.Rows )}: {ConsolidateFraming.Line( report.Rows )}" );
      }

      if( ClientCpuLevels( report ).Count > 0 )
      {
         md.AppendLine( $"- {ConsolidateFraming.CLIENT_CPU_LINE}" );
      }

      if( report.TruthSeconds.HasValue )
      {
         md.AppendLine( $"- Ground truth: brute force over every vector in memory, {report.TruthSeconds:0.0} s for all {report.QueryCount} queries." );
      }

      if( report.TruthNdcg.HasValue )
      {
         md.AppendLine( $"- nDCG@{report.Top} of the exact answer itself (the ceiling for this embedder): {report.TruthNdcg:0.000}" );
      }

      md.AppendLine();
      AppendTable( md, report );
      AppendDetails( md, report );
      if( report.Notes.Count > 0 )
      {
         md.AppendLine().AppendLine( "## Notes" ).AppendLine();
         report.Notes.ForEach( n => md.AppendLine( $"- {n}" ) );
      }

      return md.ToString();
   }

   #endregion Public Methods

   #region Private Methods

   /// <summary>
   /// The comparison table, one row per target.
   /// </summary>
   /// <param name="md">Output.</param>
   /// <param name="report">The run.</param>
   private static void AppendTable( StringBuilder md, BenchReport report )
   {
      bool golden = report.Targets.Any( t => t.Search?.Ndcg != null ) || report.TruthNdcg.HasValue;
      var header = new List<string> { "engine", "index", "load rows/s", "p50 ms", "p95 ms", "p99 ms" };
      header.AddRange( report.Concurrency.Select( c => $"QPS@{c}" ) );
      List<int> cpuLevels = ClientCpuLevels( report );
      header.AddRange( cpuLevels.Select( c => $"client CPU ms/search@{c}" ) );
      header.Add( $"recall@{report.Top}" );
      if( golden )
      {
         header.Add( $"nDCG@{report.Top}" );
      }

      header.AddRange( new[] { "RAM", "disk" } );
      md.AppendLine( "| " + string.Join( " | ", header ) + " |" );
      md.AppendLine( "|" + string.Concat( header.Select( _ => "---|" ) ) );
      foreach( TargetReport t in report.Targets )
      {
         SearchReport? s = t.Search;
         var cells = new List<string> { t.Name, Cell( t.Index ), Number( t.Load?.RowsPerSecond, "N0" ), Ms( s?.P50Ms ), Ms( s?.P95Ms ), Ms( s?.P99Ms ) };
         cells.AddRange( report.Concurrency.Select( c => s != null && s.Qps.TryGetValue( c, out double q ) ? Number( q, "0.0" ) : "-" ) );
         cells.AddRange( cpuLevels.Select( c => s?.ClientCpuMsPerSearch != null && s.ClientCpuMsPerSearch.TryGetValue( c, out double cpu ) ? Ms( cpu ) : "-" ) );
         cells.Add( Number( s?.Recall, "0.000" ) );
         if( golden )
         {
            cells.Add( Number( s?.Ndcg, "0.000" ) );
         }

         cells.Add( t.Ram?.Bytes is long ram ? Measurement.Format( ram ) : "-" );
         cells.Add( t.Disk?.Bytes is long disk ? Measurement.Format( disk ) : "-" );
         md.AppendLine( "| " + string.Join( " | ", cells ) + " |" );
      }
   }

   /// <summary>
   /// The concurrency levels at which at least one target recorded the client's CPU per search, lowest first.
   /// </summary>
   /// <param name="report">The run.</param>
   /// <returns>The levels; empty when no target recorded it.</returns>
   private static List<int> ClientCpuLevels( BenchReport report )
   {
      return report.Targets.Where( t => t.Search?.ClientCpuMsPerSearch != null ).SelectMany( t => t.Search!.ClientCpuMsPerSearch!.Keys ).Distinct().OrderBy( l => l ).ToList();
   }

   /// <summary>
   /// Per-target details: versions, load phases, exact mode, scopes of the RAM and disk
   /// numbers, and errors.
   /// </summary>
   /// <param name="md">Output.</param>
   /// <param name="report">The run.</param>
   private static void AppendDetails( StringBuilder md, BenchReport report )
   {
      md.AppendLine().AppendLine( "## Details per target" ).AppendLine();
      foreach( TargetReport t in report.Targets )
      {
         md.AppendLine( $"### {t.Name}" ).AppendLine();
         md.AppendLine( $"- Engine: {t.Engine} ({t.Hosting})" );
         md.AppendLine( $"- Index: {t.Index}" );
         if( t.SearchSettings is { Count: > 0 } settings )
         {
            md.AppendLine( $"- Search settings: {string.Join( ", ", settings.Select( p => $"{p.Key}={p.Value}" ) )}" );
         }

         AppendEngineRecord( md, t );
         if( t.Load is LoadReport l )
         {
            md.AppendLine( $"- Load: {l.Rows:N0} rows in batches of {l.Batch}, {l.UpsertSeconds:0.0} s of upserts ({l.RowsPerSecond:N0} rows/s); "
               + $"count matched {Seconds( l.CountMatchSeconds )} after the last upsert; index step {Seconds( l.IndexSeconds )}{( l.IndexNote != null ? $" ({l.IndexNote})" : string.Empty )}" );
         }

         if( t.Search is SearchReport s )
         {
            md.AppendLine( $"- Search: {s.LatencySamples} latency samples, target held {s.CountInTarget?.ToString( "N0" ) ?? "?"} rows, {s.Errors} errors{( s.FirstError != null ? $" (first: {s.FirstError})" : string.Empty )}" );
            if( s.ClientCpuMsPerSearch is { Count: > 0 } cpu )
            {
               md.AppendLine( "- Client CPU per search: " + string.Join( ", ", cpu.OrderBy( p => p.Key ).Select( p => $"{p.Key} searcher{( p.Key == 1 ? string.Empty : "s" )} {Ms( p.Value )} ms" ) ) );
            }

            if( s.ExactQueries > 0 )
            {
               md.AppendLine( $"- Exact mode: {s.ExactQueries:N0} searches, p50 {Ms( s.ExactP50Ms )} ms, p95 {Ms( s.ExactP95Ms )} ms, recall {Number( s.ExactRecall, "0.000" )}" );
            }
         }

         md.AppendLine( $"- RAM: {t.Ram?.Text ?? "-"}; disk: {t.Disk?.Text ?? "-"}" );
         if( t.Error != null )
         {
            md.AppendLine( $"- FAILED: {t.Error}" );
         }

         t.Notes.ForEach( n => md.AppendLine( $"- {n}" ) );
         md.AppendLine();
      }
   }

   /// <summary>
   /// The engine record of one target: its settings as read or set, its image against the pinned id and its data
   /// folder, each only when it was recorded. Every figure and word comes from a recorded field, so the page says
   /// nothing the run did not write down.
   /// </summary>
   /// <param name="md">Output.</param>
   /// <param name="t">The target.</param>
   private static void AppendEngineRecord( StringBuilder md, TargetReport t )
   {
      if( t.EngineSettings is { Count: > 0 } settings )
      {
         md.AppendLine( "- Engine settings: " + string.Join( "; ", settings.Select( e => $"{Cell( e.Key )} = {Cell( e.Value )} ({Cell( e.How )})" ) ) );
      }

      if( t.Image is ImageRecord image )
      {
         string pin = image.PinnedId == null ? "no pinned id" : image.PinnedId == image.Id ? "the pinned id" : $"NOT the pinned id {image.PinnedId}";
         md.AppendLine( $"- Image: {Cell( image.Ref )}, id {image.Id} ({pin}), tagged on this machine at {image.LastTagTimeUtc ?? "an unknown time"}" );
      }

      if( t.DataFolder is DataFolderRecord folder )
      {
         string reset = folder.Reset == null ? string.Empty : $"; reset at the start: {folder.Reset}";
         string after = folder.BytesAfterReset is long afterReset ? $", {Measurement.Format( afterReset )} after the reset" : string.Empty;
         string file = folder.BenchFileBytes is long bench ? $"; the benchmark's own database file {Measurement.Format( bench )}" : string.Empty;
         md.AppendLine( $"- Data folder {Cell( folder.Path )}: {FolderBytes( folder.BytesAtStart )} at the start{after}, {FolderBytes( folder.BytesAtEnd )} at the end{file}{reset}" );
      }
   }

   /// <summary>
   /// A folder size as text, "not read" when it was not.
   /// </summary>
   /// <param name="bytes">Bytes, or null.</param>
   /// <returns>Text.</returns>
   private static string FolderBytes( long? bytes )
   {
      return bytes is long value ? Measurement.Format( value ) : "not read";
   }

   /// <summary>
   /// Formats a number, "-" when missing or not a number.
   /// </summary>
   /// <param name="value">Value.</param>
   /// <param name="format">Format string.</param>
   /// <returns>Text.</returns>
   private static string Number( double? value, string format )
   {
      return value.HasValue && !double.IsNaN( value.Value ) ? value.Value.ToString( format, CultureInfo.InvariantCulture ) : "-";
   }

   /// <summary>
   /// Formats milliseconds with two decimals below 10 ms and one above.
   /// </summary>
   /// <param name="ms">Milliseconds.</param>
   /// <returns>Text.</returns>
   private static string Ms( double? ms )
   {
      return Number( ms, ms < 10 ? "0.00" : "0.0" );
   }

   /// <summary>
   /// Formats seconds, "n/a" when the step did not apply.
   /// </summary>
   /// <param name="seconds">Seconds.</param>
   /// <returns>Text.</returns>
   private static string Seconds( double? seconds )
   {
      return seconds.HasValue ? seconds.Value.ToString( "0.0", CultureInfo.InvariantCulture ) + " s" : "n/a";
   }

   /// <summary>
   /// Deletes a leftover temporary file; a file that is already gone is the normal case after a move.
   /// </summary>
   /// <param name="path">The temporary file.</param>
   private static void DeleteIfPresent( string path )
   {
      if( File.Exists( path ) )
      {
         File.Delete( path );
      }
   }

   /// <summary>
   /// Makes text safe inside a Markdown table cell.
   /// </summary>
   /// <param name="text">Text.</param>
   /// <returns>Escaped text.</returns>
   private static string Cell( string text )
   {
      return text.Replace( "|", "\\|" ).Replace( "\n", " " );
   }

   #endregion Private Methods
}
