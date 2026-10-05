using System.Globalization;
using GenericVectorBuilder.Bench.Running;

namespace GenericVectorBuilder.Bench.Cli;

/// <summary>
/// The command line, parsed and checked. Format: a command word, then "--name value" pairs
/// (and the "--keep" and "--no-machine-control" switches).
/// Why a hand-rolled parser instead of a library: there are a dozen options, all simple, and
/// every mistake gets a plain-English message naming the option at fault.
/// </summary>
public sealed class BenchOptions
{
   #region Data Members

   /// <summary>Prefix of every collection the benchmark creates; live collection names never start with it.</summary>
   public const string COLLECTION_PREFIX = "gvbbench_";

   /// <summary>Commands the tool understands.</summary>
   public static readonly string[] COMMANDS = { "replicate", "bench", "run-all", "clean", "list", "restore-machine" };

   private const string DEFAULT_GOLDEN = "/home/dan/ForClaude/evalkit/questions_golden.json";
   private static readonly HashSet<string> SWITCHES = new( StringComparer.Ordinal ) { "keep", "no-machine-control" };
   private static readonly HashSet<string> VALUED = new( StringComparer.Ordinal )
   {
      "pipeline", "targets", "limit", "batch", "queries", "top", "concurrency", "seconds", "warmup", "hnsw-ef",
      "golden-file", "out", "repo", "exact-seconds", "search-timeout", "seed", "machine-state",
   };

   #endregion Data Members

   #region Public Methods

   /// <summary>The command: replicate, bench, run-all, clean or list.</summary>
   public string Command { get; private set; } = string.Empty;

   /// <summary>Pipeline whose table is the source.</summary>
   public string Pipeline { get; private set; } = string.Empty;

   /// <summary>Target names; empty means "all" for run-all and list.</summary>
   public IReadOnlyList<string> Targets { get; private set; } = Array.Empty<string>();

   /// <summary>Most rows to use, or null for all.</summary>
   public int? Limit { get; private set; }

   /// <summary>Rows per upsert call.</summary>
   public int Batch { get; private set; } = 1000;

   /// <summary>"golden" or "random:N".</summary>
   public string Queries { get; private set; } = "random:200";

   /// <summary>Hits per query (the k of recall@k and nDCG@k).</summary>
   public int Top { get; private set; } = 10;

   /// <summary>Concurrency levels for the throughput test.</summary>
   public IReadOnlyList<int> Concurrency { get; private set; } = new[] { 1, 8 };

   /// <summary>Seconds per concurrency level.</summary>
   public int Seconds { get; private set; } = 20;

   /// <summary>
   /// Warm-up searches run immediately before EVERY timed pass, with that pass's own search
   /// mode and concurrency. Why every pass and not only the first: a pass that follows another
   /// pass inherits a warm cache and warm connections, and one that comes first does not, so
   /// order alone would change the numbers.
   /// </summary>
   public int Warmup { get; private set; } = 20;

   /// <summary>
   /// Seed for the random target order and the random pass order inside each target, or null
   /// to derive one from the run start time. Why random at all: in a fixed order the same
   /// query ran faster when timed later in a run, so a fixed order biases every comparison the
   /// same way. The seed actually used is always written to the results, so any run's order can
   /// be repeated with --seed.
   /// </summary>
   public int? Seed { get; private set; }

   /// <summary>Search beam for qdrant-hnsw, or null for the server default.</summary>
   public int? HnswEf { get; private set; }

   /// <summary>Labelled questions file for --queries golden.</summary>
   public string GoldenFile { get; private set; } = DEFAULT_GOLDEN;

   /// <summary>Results root folder, or null for {repo}/bench-results.</summary>
   public string? OutFolder { get; private set; }

   /// <summary>Repository root override, or null to find it.</summary>
   public string? RepoRoot { get; private set; }

   /// <summary>Time budget for timing each engine's exact mode.</summary>
   public int ExactSeconds { get; private set; } = 60;

   /// <summary>Longest a single search may take before it counts as an error.</summary>
   public int SearchTimeoutSeconds { get; private set; } = 120;

   /// <summary>run-all only: keep the benchmark collections instead of dropping them afterwards.</summary>
   public bool Keep { get; private set; }

   /// <summary>
   /// bench and run-all: put the machine into a known state for the timed passes (performance
   /// governor, engine and client on separate physical cores, passes held while the box is
   /// busy) and back afterwards. False with --no-machine-control, which changes nothing on the
   /// machine and says so in the results; meant for tests and for boxes without sudo.
   /// </summary>
   public bool MachineControl { get; private set; } = true;

   /// <summary>
   /// The state file recording every machine change before it is made, so a run killed without
   /// its finally block is put back by the next start or by "restore-machine".
   /// </summary>
   public string MachineStateFile { get; private set; } = MachineStateStore.DEFAULT_PATH;

   /// <summary>Collection name every target is loaded under: "gvbbench_" + pipeline.</summary>
   public string Collection => COLLECTION_PREFIX + Pipeline;

   /// <summary>True for golden queries.</summary>
   public bool IsGolden => Queries == "golden";

   /// <summary>Number of random queries (0 for golden).</summary>
   public int RandomCount => IsGolden ? 0 : int.Parse( Queries[( Queries.IndexOf( ':' ) + 1 )..], CultureInfo.InvariantCulture );

   /// <summary>
   /// Parses the arguments.
   /// </summary>
   /// <param name="args">Command-line arguments.</param>
   /// <returns>The options.</returns>
   /// <exception cref="ArgumentException">Something is missing or malformed; the message says what.</exception>
   public static BenchOptions Parse( IReadOnlyList<string> args )
   {
      if( args.Count == 0 || !COMMANDS.Contains( args[0] ) )
      {
         throw new ArgumentException( $"Start with a command: {string.Join( ", ", COMMANDS )}." );
      }

      var options = new BenchOptions { Command = args[0] };
      Dictionary<string, string> values = ReadPairs( args );
      foreach( KeyValuePair<string, string> pair in values )
      {
         options.Apply( pair.Key, pair.Value );
      }

      options.Validate();
      return options;
   }

   /// <summary>
   /// The usage text printed on a mistake.
   /// </summary>
   /// <returns>Usage text.</returns>
   public static string Usage()
   {
      var busy = new MachineControlOptions();
      string held = string.Create( CultureInfo.InvariantCulture,
         $"hold each pass's warm-up (up to {busy.BusyWait.TotalMinutes:0.#} min per pass) while processes outside the benchmark use more than {busy.BusyThreshold:0.0#} CPUs on average over the last {busy.BusyWindow.TotalSeconds:0} s or the last {busy.RecentWindow.TotalSeconds:0} s" );
      return @"GenericVectorBuilder.Bench: compare vector engines on the same vectors and the same queries.

  list      [--targets a,b]
  replicate --pipeline P --targets a,b [--limit N] [--batch 1000]
  bench     --pipeline P --targets a,b [--limit N] [--queries random:200|golden] [--top 10] [--concurrency 1,8] [--seconds 20]
  run-all   --pipeline P [--targets a,b] [same options as replicate and bench] [--keep]
  clean     --pipeline P --targets a,b
  restore-machine [--machine-state FILE]   put back what a run that did not finish changed

Other options: --warmup 20, --seed N, --hnsw-ef N, --golden-file F, --exact-seconds 60, --search-timeout 120, --out DIR, --repo DIR,
--no-machine-control, --machine-state FILE (default /home/dan/gvb-work/bench-machine-state.json).
Every target is loaded under the collection 'gvbbench_' + P, never the live name.
Targets run in a random order, and each target's timed passes (default search at each concurrency
level, exact mode) run in a random order, each after its own warm-up. --seed N repeats an order;
without it the seed comes from the start time and is written to results.json as runSeed.
bench and run-all set every CPU's governor to performance, run the engine under test on the upper half
of the physical cores and this client on the lower half, and " + held + @"; a box that stays busy
past that deadline runs the pass anyway, flagged 'busy box'. Everything is put back at the end and recorded under 'conditions'
in results.json, with each pass's outside load and this client's own CPU time per search. --no-machine-control changes
nothing on the machine.";
   }

   #endregion Public Methods

   #region Private Methods

   /// <summary>
   /// Reads "--name value" pairs and switches after the command word.
   /// </summary>
   /// <param name="args">Arguments.</param>
   /// <returns>Values by option name ("true" for switches).</returns>
   private static Dictionary<string, string> ReadPairs( IReadOnlyList<string> args )
   {
      var values = new Dictionary<string, string>( StringComparer.Ordinal );
      for( int i = 1; i < args.Count; i++ )
      {
         string name = args[i].StartsWith( "--", StringComparison.Ordinal ) ? args[i][2..] : throw new ArgumentException( $"Expected an option starting with --, got '{args[i]}'." );
         if( SWITCHES.Contains( name ) )
         {
            values[name] = "true";
         }
         else if( VALUED.Contains( name ) )
         {
            values[name] = i + 1 < args.Count ? args[++i] : throw new ArgumentException( $"--{name} needs a value." );
         }
         else
         {
            throw new ArgumentException( $"Unknown option --{name}." );
         }
      }

      return values;
   }

   /// <summary>
   /// Stores one option.
   /// </summary>
   /// <param name="name">Option name without dashes.</param>
   /// <param name="value">Its value.</param>
   private void Apply( string name, string value )
   {
      switch( name )
      {
         case "pipeline": Pipeline = value; break;
         case "targets": Targets = value.Split( ',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries ); break;
         case "limit": Limit = Positive( name, value ); break;
         case "batch": Batch = Positive( name, value ); break;
         case "queries": Queries = value; break;
         case "top": Top = Positive( name, value ); break;
         case "concurrency": Concurrency = value.Split( ',', StringSplitOptions.RemoveEmptyEntries ).Select( v => Positive( name, v ) ).ToArray(); break;
         case "seconds": Seconds = Positive( name, value ); break;
         case "warmup": Warmup = NonNegative( name, value ); break;
         case "hnsw-ef": HnswEf = Positive( name, value ); break;
         case "golden-file": GoldenFile = value; break;
         case "out": OutFolder = value; break;
         case "repo": RepoRoot = value; break;
         case "exact-seconds": ExactSeconds = NonNegative( name, value ); break;
         case "search-timeout": SearchTimeoutSeconds = Positive( name, value ); break;
         case "seed": Seed = WholeNumber( name, value ); break;
         case "keep": Keep = true; break;
         case "no-machine-control": MachineControl = false; break;
         case "machine-state": MachineStateFile = value; break;
      }
   }

   /// <summary>
   /// Checks the combination of options for the command.
   /// </summary>
   private void Validate()
   {
      if( Command is not ( "list" or "restore-machine" ) && !System.Text.RegularExpressions.Regex.IsMatch( Pipeline, "^[a-z0-9_]{1,100}$" ) )
      {
         throw new ArgumentException( "--pipeline is required: the pipeline name (lower-case letters, digits, underscores)." );
      }

      if( Command is "replicate" or "bench" or "clean" && Targets.Count == 0 )
      {
         throw new ArgumentException( $"--targets is required for {Command}, e.g. --targets sql,qdrant." );
      }

      bool random = Queries.StartsWith( "random:", StringComparison.Ordinal ) && int.TryParse( Queries[7..], out int n ) && n > 0;
      if( !IsGolden && !random )
      {
         throw new ArgumentException( $"--queries must be 'golden' or 'random:N' (N above 0), not '{Queries}'." );
      }

      if( Concurrency.Count == 0 )
      {
         throw new ArgumentException( "--concurrency needs at least one level, e.g. 1,8." );
      }
   }

   /// <summary>
   /// Parses a whole number above zero.
   /// </summary>
   /// <param name="name">Option name, for the message.</param>
   /// <param name="value">Text.</param>
   /// <returns>The number.</returns>
   private static int Positive( string name, string value )
   {
      return int.TryParse( value, NumberStyles.Integer, CultureInfo.InvariantCulture, out int number ) && number > 0
         ? number : throw new ArgumentException( $"--{name} must be a whole number above 0, not '{value}'." );
   }

   /// <summary>
   /// Parses any whole number that fits an int (a seed may be zero or negative).
   /// </summary>
   /// <param name="name">Option name, for the message.</param>
   /// <param name="value">Text.</param>
   /// <returns>The number.</returns>
   private static int WholeNumber( string name, string value )
   {
      return int.TryParse( value, NumberStyles.Integer, CultureInfo.InvariantCulture, out int number )
         ? number : throw new ArgumentException( $"--{name} must be a whole number between {int.MinValue} and {int.MaxValue}, not '{value}'." );
   }

   /// <summary>
   /// Parses a whole number of zero or more.
   /// </summary>
   /// <param name="name">Option name, for the message.</param>
   /// <param name="value">Text.</param>
   /// <returns>The number.</returns>
   private static int NonNegative( string name, string value )
   {
      return int.TryParse( value, NumberStyles.Integer, CultureInfo.InvariantCulture, out int number ) && number >= 0
         ? number : throw new ArgumentException( $"--{name} must be a whole number, 0 or more, not '{value}'." );
   }

   #endregion Private Methods
}
