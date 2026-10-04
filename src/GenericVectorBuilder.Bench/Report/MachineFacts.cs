using System.Runtime.InteropServices;
using GenericVectorBuilder.Bench.Targets;

namespace GenericVectorBuilder.Bench.Report;

/// <summary>
/// The box a run happened on. Why: a latency only means something next to the CPU, memory,
/// disk and OS that produced it, and the same benchmark on another box will differ.
/// Every fact is read best-effort; a missing tool leaves "unknown", never an error.
/// </summary>
public sealed class MachineFacts
{
   #region Public Methods

   /// <summary>Host name.</summary>
   public string Host { get; set; } = Environment.MachineName;

   /// <summary>CPU model.</summary>
   public string Cpu { get; set; } = "unknown";

   /// <summary>Logical CPUs.</summary>
   public int LogicalCpus { get; set; } = Environment.ProcessorCount;

   /// <summary>Installed memory, GiB.</summary>
   public double RamGiB { get; set; }

   /// <summary>GPU model and memory (the embedder runs there; engines do not use it).</summary>
   public string Gpu { get; set; } = "unknown";

   /// <summary>Operating system and kernel.</summary>
   public string Os { get; set; } = RuntimeInformation.OSDescription;

   /// <summary>.NET runtime of the benchmark client.</summary>
   public string DotNet { get; set; } = RuntimeInformation.FrameworkDescription;

   /// <summary>
   /// Load average (1, 5 and 15 minutes) when the run started. Why: other work on the box
   /// (other engines, builds, the embedder) slows every engine, and a busy box must be visible
   /// next to the numbers it produced.
   /// </summary>
   public string LoadAverage { get; set; } = "unknown";

   /// <summary>
   /// Reads the load average from /proc/loadavg.
   /// </summary>
   /// <returns>"1-min 5-min 15-min", or null when unavailable.</returns>
   public static string? ReadLoadAverage()
   {
      try
      {
         string[] parts = File.ReadAllText( "/proc/loadavg" ).Split( ' ' );
         return parts.Length >= 3 ? $"{parts[0]} {parts[1]} {parts[2]}" : null;
      }
      catch( IOException )
      {
         return null;
      }
   }

   /// <summary>
   /// True when the 1-minute load average is above the number of logical CPUs, i.e. work was
   /// queueing for a CPU and every latency is inflated by an unknown amount.
   /// </summary>
   /// <param name="loadAverage">Text from <see cref="ReadLoadAverage"/>.</param>
   /// <returns>True when the box was overloaded.</returns>
   public static bool IsBusy( string? loadAverage )
   {
      return double.TryParse( loadAverage?.Split( ' ' )[0], System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out double load )
         && load > Environment.ProcessorCount;
   }

   /// <summary>
   /// Reads the facts from /proc, /etc/os-release and nvidia-smi.
   /// </summary>
   /// <param name="ct">Cancellation.</param>
   /// <returns>The facts.</returns>
   public static async Task<MachineFacts> ReadAsync( CancellationToken ct )
   {
      var facts = new MachineFacts();
      string[] cpuInfo = File.Exists( "/proc/cpuinfo" ) ? await File.ReadAllLinesAsync( "/proc/cpuinfo", ct ) : Array.Empty<string>();
      facts.Cpu = Value( cpuInfo.FirstOrDefault( l => l.StartsWith( "model name", StringComparison.Ordinal ) ) ) ?? facts.Cpu;
      string[] memInfo = File.Exists( "/proc/meminfo" ) ? await File.ReadAllLinesAsync( "/proc/meminfo", ct ) : Array.Empty<string>();
      string? total = Value( memInfo.FirstOrDefault( l => l.StartsWith( "MemTotal", StringComparison.Ordinal ) ) );
      if( total != null && long.TryParse( total.Split( ' ' )[0], out long kb ) )
      {
         facts.RamGiB = Math.Round( kb / 1024.0 / 1024.0, 1 );
      }

      string[] release = File.Exists( "/etc/os-release" ) ? await File.ReadAllLinesAsync( "/etc/os-release", ct ) : Array.Empty<string>();
      string? pretty = release.FirstOrDefault( l => l.StartsWith( "PRETTY_NAME=", StringComparison.Ordinal ) )?["PRETTY_NAME=".Length..].Trim( '"' );
      string? kernel = await Shell.TryOutputAsync( "uname", new[] { "-r" }, ct );
      facts.Os = pretty != null ? $"{pretty}, kernel {kernel ?? "unknown"}" : facts.Os;
      string? gpu = await Shell.TryOutputAsync( "nvidia-smi", new[] { "--query-gpu=name,memory.total", "--format=csv,noheader" }, ct );
      facts.Gpu = string.IsNullOrWhiteSpace( gpu ) ? "none found" : gpu.Replace( '\n', ';' );
      facts.LoadAverage = ReadLoadAverage() ?? facts.LoadAverage;
      return facts;
   }

   #endregion Public Methods

   #region Private Methods

   /// <summary>
   /// The text after the first colon of a "key : value" line.
   /// </summary>
   /// <param name="line">The line, or null.</param>
   /// <returns>The value, or null.</returns>
   private static string? Value( string? line )
   {
      int colon = line?.IndexOf( ':' ) ?? -1;
      return colon < 0 ? null : line![( colon + 1 )..].Trim();
   }

   #endregion Private Methods
}
