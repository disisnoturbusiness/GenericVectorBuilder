using System.Globalization;
using System.Text;
using System.Text.Json;
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

   #endregion Data Members

   #region Public Methods

   /// <summary>
   /// Writes both files.
   /// </summary>
   /// <param name="report">The run.</param>
   /// <param name="folder">The run's folder (created if missing).</param>
   public static void Write( BenchReport report, string folder )
   {
      Directory.CreateDirectory( folder );
      File.WriteAllText( Path.Combine( folder, "results.json" ), JsonSerializer.Serialize( report, JSON ) );
      File.WriteAllText( Path.Combine( folder, "results.md" ), Markdown( report ) );
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
