using System.Text.RegularExpressions;
using GenericVectorBuilder.Bench.Report;

namespace GenericVectorBuilder.Bench.Stats;

/// <summary>
/// The parts of the text a run recorded for a data-folder reset (dataFolder.reset): how many tables were truncated, ClickHouse's own active
/// bytes of them before and after, and whether the folder had stopped changing when its size was read. Each part is a verbatim token of the
/// recorded text, so a sentence can cite it as a token of that field and the audit finds it again.
/// Why read the text and not rely on its wording: the recorded text is one sentence the program wrote for a reader, and it says "(the folder was steady)"
/// beside a folder that fell from 4.25 GiB to 14.01 MiB. What it records is that the size was read after the folder stopped changing, not that the reset
/// left the folder as it was, so the report states the byte fields and this one fact and never quotes the text whole.
/// Why it refuses a text it cannot read: a changed wording would otherwise print a sentence whose tokens nobody checked.
/// </summary>
public sealed record ResetText( string Truncated, string TruncatedCount, string ActiveBefore, string ActiveAfter, string Settled, bool Stopped )
{
   #region Data Members

   /// <summary>The words the recorded text uses when the folder had stopped changing.</summary>
   public const string STOPPED_TOKEN = "the folder was steady";

   /// <summary>The words the recorded text uses when the folder was still changing at the deadline.</summary>
   public const string STILL_TOKEN = "the folder was STILL CHANGING at the deadline";

   private static readonly Regex TRUNCATED = new( @"truncated (?<count>\d+) MergeTree log tables in database system", RegexOptions.Compiled | RegexOptions.CultureInvariant, TimeSpan.FromSeconds( 5 ) );
   private static readonly Regex ACTIVE = new( @"active bytes of those tables (?<before>\d+(\.\d+)? [KMGT]?i?B) before and (?<after>\d+(\.\d+)? [KMGT]?i?B) after", RegexOptions.Compiled | RegexOptions.CultureInvariant, TimeSpan.FromSeconds( 5 ) );

   #endregion Data Members

   #region Public Methods

   /// <summary>
   /// Reads a recorded reset text.
   /// </summary>
   /// <param name="run">The run folder, for the message.</param>
   /// <param name="text">The text, dataFolder.reset.</param>
   /// <returns>The parts.</returns>
   /// <exception cref="ConsolidateRefusal">The text does not hold the table count, both active-byte figures and one of the two folder-state phrases.</exception>
   public static ResetText Read( string run, string text )
   {
      Match truncated = TRUNCATED.Match( text );
      Match active = ACTIVE.Match( text );
      bool stopped = text.Contains( STOPPED_TOKEN, StringComparison.Ordinal );
      bool still = text.Contains( STILL_TOKEN, StringComparison.Ordinal );
      if( !truncated.Success || !active.Success || stopped == still )
      {
         throw new ConsolidateRefusal( $"{run}: the recorded data-folder reset text is not in the form this report reads (a truncated-table count, active bytes before and after, and '{STOPPED_TOKEN}' or '{STILL_TOKEN}'): {text}" );
      }

      return new ResetText( truncated.Value, truncated.Groups["count"].Value, active.Groups["before"].Value, active.Groups["after"].Value, stopped ? STOPPED_TOKEN : STILL_TOKEN, stopped );
   }

   #endregion Public Methods
}
