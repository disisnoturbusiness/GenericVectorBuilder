using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;
using GenericVectorBuilder.Bench.Report;

namespace GenericVectorBuilder.Bench.Stats;

/// <summary>
/// How the CPU clock was held during one run, read from what the run recorded: the structured
/// conditions.clock block of a v8 run, or for v5 to v7 runs the machine-control note
/// ("turbo off (intel_pstate/no_turbo 0 -> 1) ... held at its ceiling of 3500 MHz ... max 3000 MHz
/// (MSR 0x620 = 0x1e1e); was ...").
/// Why one record for both: the threshold basis compares runs by their clock settings, and a v7 run
/// read from its notes must compare equal to a v8 run read from its fields when both held the same
/// clock, or no same-settings pair across the two sessions could ever form.
/// Why the first MSR value of the note: the note also holds the value put back after the run
/// (0xc1e); the one in force during the run is the first, the one before "; was".
/// </summary>
public sealed class RunClock
{
   #region Data Members

   /// <summary>The label of a run whose turbo state was not pinned or not recorded (the basis prototype's wording).</summary>
   public const string TURBO_NOT_PINNED = "turbo not pinned (no no_turbo change recorded)";

   /// <summary>The label of a run whose uncore limit was not pinned or not recorded.</summary>
   public const string UNCORE_NOT_PINNED = "uncore not pinned";

   /// <summary>The clock tolerance used when a run did not record one: 100 basis points (1%), the value machine control has used since v7.</summary>
   public const int DEFAULT_TOLERANCE_BP = 100;

   private static readonly Regex TURBO_IN_NOTE = new( @"no_turbo (?<before>\d) -> (?<during>\d)", RegexOptions.Compiled );
   private static readonly Regex UNCORE_IN_NOTE = new( @"max \d+ MHz \(MSR 0x620 = (?<msr>0x[0-9a-f]+)\); was", RegexOptions.Compiled );
   private static readonly Regex PINNED_IN_NOTE = new( @"held at its ceiling of (?<mhz>\d+) MHz", RegexOptions.Compiled );
   private static readonly Regex TOLERANCE_IN_NOTE = new( @"more than (?<p>\d+)% off the pinned", RegexOptions.Compiled );
   private static readonly Regex CEILING_IN_NOTE = new( @"\(before: turbo \w+ \(intel_pstate/no_turbo \d\), ceiling (?<mhz>\d+) MHz on CPUs", RegexOptions.Compiled );

   #endregion Data Members

   #region Public Methods

   /// <summary>True when the run held the clock: turbo off during the run.</summary>
   public bool Pinned { get; init; }

   /// <summary>The clock every CPU was held at, MHz; null when not recorded.</summary>
   public int? PinnedMhz { get; init; }

   /// <summary>Every CPU's ceiling before the run, MHz, when all CPUs agreed; null when not recorded.</summary>
   public int? CeilingBeforeMhz { get; init; }

   /// <summary>intel_pstate/no_turbo before the run (1 = turbo off); null when not recorded.</summary>
   public int? NoTurboBefore { get; init; }

   /// <summary>no_turbo during the run (1 = turbo off); null when not recorded.</summary>
   public int? NoTurboDuring { get; init; }

   /// <summary>no_turbo at the end of the run; null when not recorded.</summary>
   public int? NoTurboAtEnd { get; init; }

   /// <summary>MSR 0x620 before the run, "0x" plus lower-case hex; null when not recorded.</summary>
   public string? UncoreBefore { get; init; }

   /// <summary>MSR 0x620 during the run, "0x" plus lower-case hex; null when not pinned or not recorded.</summary>
   public string? UncoreDuring { get; init; }

   /// <summary>MSR 0x620 at the end of the run; null when not recorded.</summary>
   public string? UncoreAtEnd { get; init; }

   /// <summary>How far a pass's clock may sit from the pinned clock, in basis points of the pinned clock.</summary>
   public int ToleranceBp { get; init; } = DEFAULT_TOLERANCE_BP;

   /// <summary>The rule text the run recorded for its clock check; null for a legacy run.</summary>
   public string? Rule { get; init; }

   /// <summary>True when every value came from the machine-control note of a v5 to v7 run, not from conditions.clock.</summary>
   public bool LegacyParsed { get; init; }

   /// <summary>Where the values were read: "conditions.clock" or "notes[N]"; null when the run recorded no clock at all.</summary>
   public string? Source { get; init; }

   /// <summary>The note the legacy values were read from, verbatim; null for a structured clock.</summary>
   public string? SourceNote { get; init; }

   /// <summary>
   /// The turbo label the threshold basis uses: "turbo off (no_turbo 0 -> 1)" for a run that switched
   /// turbo off, else <see cref="TURBO_NOT_PINNED"/>.
   /// </summary>
   public string TurboLabel => NoTurboDuring == 1
      ? $"turbo off (no_turbo {( NoTurboBefore.HasValue ? NoTurboBefore.Value.ToString( CultureInfo.InvariantCulture ) : "?" )} -> 1)"
      : TURBO_NOT_PINNED;

   /// <summary>The uncore label the threshold basis uses: "uncore pinned (MSR 0x620 = 0x1e1e)", else <see cref="UNCORE_NOT_PINNED"/>.</summary>
   public string UncoreLabel => Pinned && UncoreDuring != null ? $"uncore pinned (MSR 0x620 = {UncoreDuring})" : UNCORE_NOT_PINNED;

   /// <summary>The turbo state during the run alone, as the same-settings key compares it ("turbo off" or "turbo not pinned").</summary>
   public string TurboKey => NoTurboDuring == 1 ? "turbo off" : "turbo not pinned";

   /// <summary>The uncore value during the run alone, as the same-settings key compares it.</summary>
   public string UncoreKey => Pinned && UncoreDuring != null ? "uncore " + UncoreDuring : "uncore not pinned";

   /// <summary>
   /// Reads the clock of one run: conditions.clock when present (v8), else the machine-control note.
   /// </summary>
   /// <param name="root">The results.json root.</param>
   /// <param name="notes">The run's notes, or null.</param>
   /// <returns>The clock; a run with neither reads as not pinned, every value null.</returns>
   /// <exception cref="InvalidDataException">conditions.clock is present but malformed (pinned missing, or a value of the wrong type).</exception>
   public static RunClock Read( JsonElement root, IReadOnlyList<string>? notes )
   {
      if( ResultJson.Child( root, "conditions" ) is { ValueKind: JsonValueKind.Object } conditions
         && ResultJson.Child( conditions, "clock" ) is { ValueKind: JsonValueKind.Object } clock )
      {
         return FromStructured( clock );
      }

      return FromNotes( notes );
   }

   /// <summary>
   /// Parses the legacy machine-control note of a v5 to v7 run.
   /// </summary>
   /// <param name="notes">The run's notes, or null.</param>
   /// <returns>The clock as the note states it; not pinned when no note holds a no_turbo change.</returns>
   public static RunClock FromNotes( IReadOnlyList<string>? notes )
   {
      IReadOnlyList<string> all = notes ?? Array.Empty<string>();
      for( int i = 0; i < all.Count; i++ )
      {
         Match turbo = TURBO_IN_NOTE.Match( all[i] );
         if( !turbo.Success )
         {
            continue;
         }

         Match uncore = UNCORE_IN_NOTE.Match( all[i] );
         Match pinned = PINNED_IN_NOTE.Match( all[i] );
         Match tolerance = TOLERANCE_IN_NOTE.Match( all[i] );
         Match ceiling = CEILING_IN_NOTE.Match( all[i] );
         int during = Digit( turbo, "during" );
         return new RunClock
         {
            Pinned = during == 1,
            PinnedMhz = pinned.Success ? int.Parse( pinned.Groups["mhz"].Value, CultureInfo.InvariantCulture ) : null,
            CeilingBeforeMhz = ceiling.Success ? int.Parse( ceiling.Groups["mhz"].Value, CultureInfo.InvariantCulture ) : null,
            NoTurboBefore = Digit( turbo, "before" ),
            NoTurboDuring = during,
            UncoreDuring = uncore.Success ? uncore.Groups["msr"].Value : null,
            ToleranceBp = tolerance.Success ? int.Parse( tolerance.Groups["p"].Value, CultureInfo.InvariantCulture ) * 100 : DEFAULT_TOLERANCE_BP,
            LegacyParsed = true,
            Source = $"notes[{i}]",
            SourceNote = all[i],
         };
      }

      return new RunClock { LegacyParsed = true };
   }

   /// <summary>
   /// Whether a measured clock is off the pinned clock: more than <see cref="ToleranceBp"/> of the
   /// pinned clock away, compared in whole numbers so no rounding decides it.
   /// </summary>
   /// <param name="mhz">The measured clock, MHz.</param>
   /// <returns>True when off; false when within tolerance or when the run was not pinned.</returns>
   public bool IsOff( int mhz )
   {
      return PinnedMhz is int pinned && pinned > 0 && (long)Math.Abs( mhz - pinned ) * 10000L > (long)ToleranceBp * pinned;
   }

   #endregion Public Methods

   #region Private Methods

   /// <summary>
   /// Reads conditions.clock as the contract writes it.
   /// </summary>
   /// <param name="clock">The clock object.</param>
   /// <returns>The clock.</returns>
   /// <exception cref="InvalidDataException">pinned is missing or not a true/false.</exception>
   private static RunClock FromStructured( JsonElement clock )
   {
      bool pinned = ResultJson.Bool( clock, "pinned" ) ?? throw new InvalidDataException( "conditions.clock has no pinned true/false" );
      JsonElement? noTurbo = ResultJson.Child( clock, "noTurbo" );
      JsonElement? uncore = ResultJson.Child( clock, "uncoreMsr620" );
      return new RunClock
      {
         Pinned = pinned,
         PinnedMhz = ResultJson.Int( clock, "pinnedMhz" ),
         CeilingBeforeMhz = ResultJson.Int( clock, "ceilingBeforeMhz" ),
         NoTurboBefore = Stage( noTurbo, "before" ),
         NoTurboDuring = Stage( noTurbo, "during" ),
         NoTurboAtEnd = Stage( noTurbo, "atEnd" ),
         UncoreBefore = Hex( uncore, "before" ),
         UncoreDuring = Hex( uncore, "during" ),
         UncoreAtEnd = Hex( uncore, "atEnd" ),
         ToleranceBp = ResultJson.Int( clock, "toleranceBp" ) ?? DEFAULT_TOLERANCE_BP,
         Rule = ResultJson.Text( clock, "rule" ),
         LegacyParsed = false,
         Source = "conditions.clock",
      };
   }

   /// <summary>
   /// One stage (before, during, atEnd) of a no_turbo object.
   /// </summary>
   /// <param name="holder">The noTurbo object, or null.</param>
   /// <param name="name">Stage name.</param>
   /// <returns>The value, or null when not read.</returns>
   private static int? Stage( JsonElement? holder, string name )
   {
      return holder is { ValueKind: JsonValueKind.Object } h ? ResultJson.Int( h, name ) : null;
   }

   /// <summary>
   /// One stage of an uncoreMsr620 object, normalised to "0x" plus lower-case hex.
   /// </summary>
   /// <param name="holder">The uncoreMsr620 object, or null.</param>
   /// <param name="name">Stage name.</param>
   /// <returns>The value, or null when not read.</returns>
   /// <exception cref="InvalidDataException">The value is not hex.</exception>
   private static string? Hex( JsonElement? holder, string name )
   {
      string? text = holder is { ValueKind: JsonValueKind.Object } h ? ResultJson.Text( h, name ) : null;
      if( text == null )
      {
         return null;
      }

      string digits = text.StartsWith( "0x", StringComparison.OrdinalIgnoreCase ) ? text[2..] : text;
      if( !long.TryParse( digits, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out long value ) )
      {
         throw new InvalidDataException( $"conditions.clock.uncoreMsr620.{name} is '{text}', not hex" );
      }

      return "0x" + value.ToString( "x", CultureInfo.InvariantCulture );
   }

   /// <summary>
   /// A single digit captured by a group.
   /// </summary>
   /// <param name="match">The match.</param>
   /// <param name="group">Group name.</param>
   /// <returns>The digit.</returns>
   private static int Digit( Match match, string group )
   {
      return match.Groups[group].Value[0] - '0';
   }

   #endregion Private Methods
}
