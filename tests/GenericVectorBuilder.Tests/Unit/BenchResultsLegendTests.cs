using System.Text.RegularExpressions;
using GenericVectorBuilder.Web.BenchPages;

namespace GenericVectorBuilder.Tests.Unit;

/// <summary>
/// The fixed strings the benchmark pages print themselves (headings, legends, banners, notices, flag descriptions).
/// The pages state no fact of their own, so each fixed string must hold no number, no engine name, no cause, no
/// purpose, no default and no "only"; this test walks every one of them, and a string added to the list is
/// walked without anyone remembering to add it here.
/// The banned list is the one the consolidate command's sentence audit uses (copied here, since the web project
/// cannot reference the benchmark), plus the words the design bans by rule but no list held.
/// </summary>
public class BenchResultsLegendTests
{
   #region Data Members

   private static readonly string[] BANNED_BY_LIST = { "because", "due to", "since", "bottleneck", "overhead", "waiting", "share of", "accounts for", "explains", "percent of the latency", "thanks to", "leads to" };

   private static readonly string[] BANNED_BY_RULE = { "only", "default", "in order to", "so that", "designed to", "meant to", "usually", "typically", "tied", "band", "bands", "fastest", "slowest", "winner" };

   private const int MAX_WORDS = 35;

   #endregion Data Members

   #region Public Methods

   /// <summary>
   /// The list is not empty, so the walk below checks something.
   /// </summary>
   [Fact]
   public void TheAllowList_HoldsTheFixedStrings_AndTheFlagLegends()
   {
      Assert.True( BenchLegends.All.Count >= 50, $"only {BenchLegends.All.Count} fixed strings" );
      Assert.Contains( "H_THRESHOLD", BenchLegends.All.Keys );
      Assert.Contains( "FLAG:clock-off", BenchLegends.All.Keys );
      Assert.Equal( BenchLegends.KnownFlagCodes.Count, BenchLegends.All.Keys.Count( k => k.StartsWith( "FLAG:", StringComparison.Ordinal ) ) );
   }

   /// <summary>
   /// No fixed string holds a number: a digit that is not part of a word such as p50.
   /// </summary>
   [Fact]
   public void NoFixedString_HoldsANumber()
   {
      foreach( ( string name, string text ) in BenchLegends.All )
      {
         Assert.False( Regex.IsMatch( text, @"\b\d" ), $"{name} holds a number: {text}" );
      }
   }

   /// <summary>
   /// No fixed string names an engine or a target.
   /// </summary>
   [Fact]
   public void NoFixedString_NamesAnEngine()
   {
      IEnumerable<string> engines = BenchNames.KnownNames.Concat( BenchNames.KnownTargets ).Select( n => n.Split( ' ' )[0] ).Distinct( StringComparer.OrdinalIgnoreCase );
      foreach( ( string name, string text ) in BenchLegends.All )
      {
         foreach( string engine in engines )
         {
            Assert.False( Regex.IsMatch( text, $@"(?<![A-Za-z0-9-]){Regex.Escape( engine )}(?![A-Za-z0-9-])", RegexOptions.IgnoreCase ), $"{name} names {engine}: {text}" );
         }
      }
   }

   /// <summary>
   /// No fixed string holds a word of the consolidate command's banned list or of the rule list (cause, purpose, default, only).
   /// </summary>
   [Fact]
   public void NoFixedString_HoldsABannedWord()
   {
      foreach( ( string name, string text ) in BenchLegends.All )
      {
         foreach( string word in BANNED_BY_LIST.Concat( BANNED_BY_RULE ) )
         {
            Assert.False( Regex.IsMatch( text, $@"\b{Regex.Escape( word )}\b", RegexOptions.IgnoreCase ), $"{name} holds \"{word}\": {text}" );
         }
      }
   }

   /// <summary>
   /// Each fixed string is short, free of dashes that read as em-dashes, and free of markup characters, so it can be
   /// printed as it is.
   /// </summary>
   [Fact]
   public void EveryFixedString_IsShort_PlainAndSafe()
   {
      foreach( ( string name, string text ) in BenchLegends.All )
      {
         Assert.True( WordCount( text ) <= MAX_WORDS, $"{name} holds more than {MAX_WORDS} words" );
         Assert.DoesNotContain( (char)0x2014, text );
         Assert.DoesNotContain( (char)0x2013, text );
         Assert.True( text.IndexOfAny( new[] { '<', '>', '&', '"' } ) < 0, $"{name} holds a markup character" );
         Assert.Equal( text.Trim(), text );
         Assert.NotEmpty( text );
      }
   }

   /// <summary>
   /// The busy legend says the busy time is during the timed pass, and the clock legends say what was off.
   /// </summary>
   [Fact]
   public void TheBusyLegend_NamesTheTimedPass()
   {
      Assert.Contains( "during the timed pass", BenchLegends.Legend( "busy-box" ) );
      Assert.Contains( "pinned value", BenchLegends.Legend( "clock-off" ) );
   }

   /// <summary>
   /// Every known flag code has a two-letter marker, an unknown code has "??" and the unknown legend, and the order puts
   /// known codes in table order and unknown ones last.
   /// </summary>
   [Fact]
   public void FlagCodes_HaveMarkers_AndUnknownCodesAreVisible()
   {
      Assert.All( BenchLegends.KnownFlagCodes, code => Assert.Equal( 2, BenchLegends.Marker( code ).Length ) );
      Assert.Equal( "??", BenchLegends.Marker( "nonesuch" ) );
      Assert.Equal( BenchLegends.FLAG_UNKNOWN, BenchLegends.Legend( "nonesuch" ) );
      Assert.True( BenchLegends.Order( "spread" ) < BenchLegends.Order( "clock-off" ) );
      Assert.Equal( int.MaxValue, BenchLegends.Order( "nonesuch" ) );
   }

   /// <summary>
   /// The legend of a code that means the same as another (index not ready after the load, after the searches) is the same text, so
   /// the flags list prints it once.
   /// </summary>
   [Fact]
   public void CodesThatMeanTheSame_ShareOneLegend()
   {
      Assert.Equal( BenchLegends.Legend( "index-not-ready-after-load" ), BenchLegends.Legend( "index-not-ready-after-search" ) );
      Assert.Equal( BenchLegends.Legend( "warmup-errors" ), BenchLegends.Legend( "search-errors" ) );
   }

   #endregion Public Methods

   #region Private Methods

   /// <summary>
   /// Words in a text, split on white space.
   /// </summary>
   private static int WordCount( string text )
   {
      return text.Split( new[] { ' ', '\t', '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries ).Length;
   }

   #endregion Private Methods
}
