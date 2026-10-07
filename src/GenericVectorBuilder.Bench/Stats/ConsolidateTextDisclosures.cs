using GenericVectorBuilder.Bench.Report;
using S = GenericVectorBuilder.Bench.Stats.ConsolidateSources;
using T = GenericVectorBuilder.Bench.Stats.ConsolidateText;

namespace GenericVectorBuilder.Bench.Stats;

/// <summary>
/// The disclosures of design section 4, each a sentence with a source: governor and partition,
/// boot, images, the Redis in-memory fact, durability (quoted), engine settings, CPU caps and
/// measured scans from the fact sheet, data-folder start state, segment layouts, the observer and
/// its clock check, systemd timers, C-states, and how dockerd's CPU was counted (hole H2: stated from the
/// code lines that do it, and quoted from the rule text the run recorded, which the code generates from what it did).
/// The durability quotes, their corrections and the saved logs are in <see cref="ConsolidateTextDurability"/>.
/// </summary>
public static class ConsolidateTextDisclosures
{
   #region Data Members

   /// <summary>The P1 test that holds v8's outside-load accounting to v7's.</summary>
   private const string ACCOUNTING_TEST_FILE = "tests/GenericVectorBuilder.Engines.Tests/Bench/MachineControlV8Tests.cs";

   /// <summary>Its method name, the token cited.</summary>
   private const string ACCOUNTING_TEST = "OutsideLoad_IsTheV7AccountingBitForBit";

   /// <summary>Where a run records how engine CPU and dockerd were counted.</summary>
   private const string ENGINE_CPU_RULE = "conditions.engineCpu.rule";

   /// <summary>The file whose code line adds dockerd's cgroup to the cgroups a compose engine's turn follows.</summary>
   private const string DOCKERD_FILE = "src/GenericVectorBuilder.Bench/Running/MachineControlPinning.cs";

   /// <summary>That line.</summary>
   private const string DOCKERD_TOKEN = "AddGroup( dockerd );";

   /// <summary>The file whose code line takes the followed cgroups' CPU out of the box's busy CPU.</summary>
   private const string OUTSIDE_FILE = "src/GenericVectorBuilder.Bench/Running/MachineControlSampler.cs";

   /// <summary>That line.</summary>
   private const string OUTSIDE_TOKEN = "double outside = ( reading.Busy - _last.Busy ) - ( reading.Self - _last.Self ) - engine;";

   #endregion Data Members

   #region Public Methods

   /// <summary>
   /// Writes every disclosure.
   /// </summary>
   /// <param name="w">The writer.</param>
   public static void Write( Writer w )
   {
      Governor( w );
      Boot( w );
      Images( w );
      Memory( w );
      ConsolidateTextDurability.Write( w );
      Settings( w );
      Facts( w );
      DataFolders( w );
      Layouts( w );
      Observer( w );
      Cstates( w );
      Dockerd( w );
      Scope( w );
   }

   /// <summary>
   /// The save and appendonly settings a target's compose file sets, from its set-by-setup facts (Redis: when a docs quote says it holds its data in memory,
   /// the reader needs what the compose file sets beside it).
   /// </summary>
   /// <param name="w">The writer.</param>
   /// <param name="target">Target.</param>
   /// <returns>The facts, in file order; empty when the target has none.</returns>
   public static List<WhyFact> ComposeSettings( Writer w, string target )
   {
      return w.Why( target )?.Facts.Where( f => f.Kind == "set-by-setup" && ( f.Text.StartsWith( "save", StringComparison.Ordinal ) || f.Text.StartsWith( "appendonly", StringComparison.Ordinal ) ) ).ToList() ?? new List<WhyFact>();
   }

   #endregion Public Methods

   #region Private Methods

   /// <summary>
   /// Governor and CPU partition.
   /// </summary>
   /// <param name="w">The writer.</param>
   private static void Governor( Writer w )
   {
      RunConditions c = w.Newest.Runs[0].Conditions;
      if( c.Governor == null || c.EngineCpus == null || c.ClientCpus == null )
      {
         return;
      }

      w.Add( "disclosure.governor", T.Fill( T.DISCLOSURE_GOVERNOR, c.Governor, c.EngineCpus, c.ClientCpus ),
         S.ResultAll( "conditions.governor", c.Governor ), S.ResultAll( "conditions.engineCpus", c.EngineCpus ), S.ResultAll( "conditions.clientCpus", c.ClientCpus ) );
   }

   /// <summary>
   /// The boot the newest session recorded, against the first session's first start.
   /// </summary>
   /// <param name="w">The writer.</param>
   private static void Boot( Writer w )
   {
      List<RunResult> runs = w.Newest.Runs.ToList();
      if( !w.TwoSessions || runs.Any( r => r.Conditions.BootId == null || r.Conditions.BootTimeUtc == null )
         || runs.Select( r => r.Conditions.BootId ).Distinct().Count() != 1 || runs.Select( r => r.Conditions.BootTimeUtc ).Distinct().Count() != 1 )
      {
         return;
      }

      RunResult first = w.Sessions[0].Runs[0];
      string id = runs[0].Conditions.BootId!;
      string time = runs[0].Conditions.BootTimeUtc!;
      bool before = Consolidator.Before( time, first.StartedUtc ) == true;
      var sources = runs.Select( r => S.Result( r.Name, "conditions.boot.bootId", id ) ).Concat( runs.Select( r => S.Result( r.Name, "conditions.boot.bootTimeUtc", time ) ) ).ToList();
      sources.Add( S.Result( first.Name, "startedUtc", first.StartedUtc! ) );
      w.Add( "disclosure.boot", T.Fill( before ? T.DISCLOSURE_BOOT : T.DISCLOSURE_BOOT_LATER, w.Newest.Name, id, time, w.Sessions[0].Name, first.StartedUtc! ), sources );
   }

   /// <summary>
   /// The images: one id per target equal to its pin, and when each was last tagged.
   /// </summary>
   /// <param name="w">The writer.</param>
   private static void Images( Writer w )
   {
      if( w.Report.Images.Count == 0 )
      {
         return;
      }

      string n = S.Whole( w.Report.ImageTargets );
      w.Add( "disclosure.images", T.Fill( T.DISCLOSURE_IMAGES, n, w.Newest.Name ), new[] { S.Consolidated( "imageTargets", n ) }
         .Concat( w.Report.Images.Select( i => S.Consolidated( $"images[target={i.Target}].id", i.Id ) ) ) );
      RunResult first = w.Sessions[0].Runs[0];
      if( w.Report.Images.All( i => i.BeforeFirstV7Start == true ) )
      {
         w.Add( "disclosure.images.tagged", T.Fill( T.DISCLOSURE_IMAGES_BEFORE, w.Sessions[0].Name, first.StartedUtc! ), new[] { S.Result( first.Name, "startedUtc", first.StartedUtc! ) }
            .Concat( w.Report.Images.Select( i => S.Consolidated( $"images[target={i.Target}].beforeFirstV7Start", "true" ) ) ) );
         return;
      }

      foreach( ImageInfo image in w.Report.Images.Where( i => i.BeforeFirstV7Start != true ) )
      {
         string tagged = image.LastTagTimeUtc ?? ConsolidateIdentity.NOT_RECORDED;
         w.Add( $"disclosure.images.tagged.{image.Target}", T.Fill( T.DISCLOSURE_IMAGES_AFTER, image.Target, tagged, w.Sessions[0].Name, first.StartedUtc! ),
            S.Consolidated( $"images[target={image.Target}].lastTagTimeUtc", tagged ), S.Result( first.Name, "startedUtc", first.StartedUtc! ) );
      }
   }

   /// <summary>
   /// The in-memory disclosure of each reported target whose storage fact documents it, with its set-by-setup save and appendonly facts.
   /// </summary>
   /// <param name="w">The writer.</param>
   private static void Memory( Writer w )
   {
      foreach( string target in w.Report.Targets )
      {
         WhyFact? storage = ConsolidateHeadline.InMemoryFact( w, target );
         List<WhyFact> setup = ComposeSettings( w, target );
         if( storage == null || setup.Count != 2 )
         {
            continue;
         }

         SentenceSource doc = S.Fact( storage );
         w.Add( $"disclosure.memory.{target}", T.Fill( T.DISCLOSURE_REDIS, target, doc.Value, setup[0].Text, setup[1].Text ), new[] { doc }.Concat( setup.Select( S.Fact ) ) );
      }
   }

   /// <summary>
   /// The engine settings disclosure, when the newest session recorded settings.
   /// </summary>
   /// <param name="w">The writer.</param>
   private static void Settings( Writer w )
   {
      if( w.Report.EngineSettings.Count > 0 )
      {
         RunResult run = w.Newest.Runs[0];
         w.Add( "disclosure.settings", T.Fill( T.DISCLOSURE_SETTINGS, run.Name ), w.RunSource( run.Name ) );
      }
   }

   /// <summary>
   /// The CPU cap facts and the measured exact-search facts of the reported targets.
   /// </summary>
   /// <param name="w">The writer.</param>
   private static void Facts( Writer w )
   {
      foreach( WhyRow row in w.Report.Why )
      {
         foreach( WhyFact cap in row.Facts.Where( f => f.Kind == "cap" ) )
         {
            w.Add( $"disclosure.cap.{row.Target}", T.Fill( T.DISCLOSURE_CAP, row.Target, cap.Text ), S.Fact( cap ) );
         }

         foreach( WhyFact scan in row.Facts.Where( f => f.Kind == "search" && f.Mode == "exact" && f.Confidence == "measured" ) )
         {
            w.Add( $"disclosure.scan.{row.Target}", T.Fill( T.DISCLOSURE_SCAN, row.Target, scan.Text ), S.Fact( scan ) );
         }
      }

      ConsolidateTextGraph.Write( w );
   }

   /// <summary>
   /// Data-folder start state of every target whose folder was reset (v8: ClickHouse's log tables), per run; and the
   /// recorded disk note of those targets in runs before v8, quoted.
   /// </summary>
   /// <param name="w">The writer.</param>
   private static void DataFolders( Writer w )
   {
      List<string> reset = w.Report.Targets.Where( t => w.Sessions.SelectMany( s => s.Runs ).Any( r => r.Find( t )?.DataFolder?.Reset != null ) ).ToList();
      foreach( string target in reset )
      {
         foreach( RunResult run in w.Sessions.SelectMany( s => s.Runs ) )
         {
            TargetResult t = run.Find( target )!;
            DataFolderValue? folder = t.DataFolder;
            if( folder?.BytesAtStart != null && folder.BytesAtEnd != null )
            {
               Folder( w, run.Name, target, folder );
            }
            else if( t.DiskText != null )
            {
               w.Add( $"disclosure.disk.{target}.{run.Name}", t.DiskText, S.Quote( run.Name, $"targets[{target}].disk.text", t.DiskText ) );
            }
         }
      }
   }

   /// <summary>
   /// One run's data-folder sentence.
   /// </summary>
   /// <param name="w">The writer.</param>
   /// <param name="run">Run folder.</param>
   /// <param name="target">Target.</param>
   /// <param name="folder">Its data folder record.</param>
   private static void Folder( Writer w, string run, string target, DataFolderValue folder )
   {
      string at = $"targets[{target}].dataFolder";
      string start = S.Whole( folder.BytesAtStart!.Value );
      string end = S.Whole( folder.BytesAtEnd!.Value );
      if( folder.Reset != null && folder.BytesAfterReset != null )
      {
         string after = S.Whole( folder.BytesAfterReset.Value );
         w.Add( $"disclosure.dataFolder.{target}.{run}", T.Fill( T.DISCLOSURE_DATA_FOLDER, run, target, start, after, folder.Reset, end ), w.RunSource( run ),
            S.Result( run, at + ".bytesAtStart", start ), S.Result( run, at + ".bytesAfterReset", after ), S.Result( run, at + ".bytesAtEnd", end ), S.Token( run, at + ".reset", folder.Reset ) );
         return;
      }

      w.Add( $"disclosure.dataFolder.{target}.{run}", T.Fill( T.DISCLOSURE_DATA_FOLDER_PLAIN, run, target, start, end ), w.RunSource( run ), S.Result( run, at + ".bytesAtStart", start ), S.Result( run, at + ".bytesAtEnd", end ) );
   }

   /// <summary>
   /// The targets whose segment layout differed between runs.
   /// </summary>
   /// <param name="w">The writer.</param>
   private static void Layouts( Writer w )
   {
      List<string> differing = w.Report.Metrics.SelectMany( m => m.Rows ).Where( r => r.Flags.Any( f => f.Code == ConsolidateFlags.SEGMENT_LAYOUT ) ).Select( r => r.Target ).Distinct( StringComparer.Ordinal ).ToList();
      if( differing.Count > 0 )
      {
         w.Add( "disclosure.layouts", T.Fill( T.DISCLOSURE_LAYOUTS, S.List( differing ) ) );
      }
   }

   /// <summary>
   /// The observer, its clock check and the timers; or that no observer summary was given.
   /// </summary>
   /// <param name="w">The writer.</param>
   private static void Observer( Writer w )
   {
      ObserverInfo? o = w.Report.Observer;
      if( o == null )
      {
         w.Add( "disclosure.observer", T.DISCLOSURE_NO_OBSERVER );
         return;
      }

      if( o.CpuMax is double cpu )
      {
         string max = S.Number( cpu, 3 );
         w.Add( "disclosure.observer", T.Fill( T.DISCLOSURE_OBSERVER, w.Newest.Name, max ), S.Consolidated( "observer.cpuMax", max ) );
      }

      if( o.AperfWorstDeviationBp is double bp && o.Msr620ValuesSeen.Count > 0 )
      {
         string pct = S.Percent( bp );
         string values = S.List( o.Msr620ValuesSeen );
         w.Add( "disclosure.observer.clock", T.Fill( T.DISCLOSURE_OBSERVER_CLOCK, pct, T.Msr(), values ), new[] { S.Consolidated( "observer.aperfWorstDeviationBp", pct, "bp-pct" ), S.File( T.MSR_FILE, T.MSR_TOKEN ) }
            .Concat( o.Msr620ValuesSeen.Select( ( v, i ) => S.Consolidated( $"observer.msr620ValuesSeen[{i}]", v ) ) ) );
      }

      if( o.TimersCovered )
      {
         string fired = S.Whole( o.PassesWithTimerFired );
         w.Add( "disclosure.timers", T.Fill( T.DISCLOSURE_TIMERS, fired, w.Newest.Name ), S.Consolidated( "observer.passesWithTimerFired", fired ) );
      }
      else
      {
         w.Add( "disclosure.timers", T.Fill( T.DISCLOSURE_TIMERS_NOT_COVERED, w.Newest.Name ) );
      }
   }

   /// <summary>
   /// The C-state disclosure.
   /// </summary>
   /// <param name="w">The writer.</param>
   private static void Cstates( Writer w )
   {
      RunConditions c = w.Newest.Runs[0].Conditions;
      if( c.CpuIdleDriver != null && c.CpuIdleGovernor != null )
      {
         w.Add( "disclosure.cstates", T.Fill( T.DISCLOSURE_CSTATES, c.CpuIdleDriver, c.CpuIdleGovernor ),
            S.ResultAll( "conditions.cpuIdle.driver", c.CpuIdleDriver ), S.ResultAll( "conditions.cpuIdle.governor", c.CpuIdleGovernor ) );
      }
   }

   /// <summary>
   /// The search-settings scope of every order (checklist O13) and the routes the runs recorded (checklist O11).
   /// </summary>
   /// <param name="w">The writer.</param>
   private static void Scope( Writer w )
   {
      w.Add( "disclosure.searchSettings", T.DISCLOSURE_SEARCH_SETTINGS );
      List<string> routes = w.Sessions.SelectMany( s => s.Runs ).Select( r => r.Conditions.Routes ).Aggregate( ( a, b ) => a.Intersect( b, StringComparer.Ordinal ).ToList() );
      for( int i = 0; i < routes.Count; i++ )
      {
         w.Add( $"disclosure.route.{i}", T.Fill( T.DISCLOSURE_ROUTE, routes[i] ), S.TokenAll( "conditions.connections", routes[i] ) );
      }
   }

   /// <summary>
   /// How dockerd's CPU was counted (hole H2): the recorded rule quoted per session that recorded it, plus the parity test; or that a session did not record it.
   /// </summary>
   /// <param name="w">The writer.</param>
   private static void Dockerd( Writer w )
   {
      w.Add( "disclosure.busy", T.DISCLOSURE_BUSY, S.File( DOCKERD_FILE, DOCKERD_TOKEN ), S.File( OUTSIDE_FILE, OUTSIDE_TOKEN ) );
      foreach( ClaimSession session in w.Sessions )
      {
         RunResult run = session.Runs[0];
         string? rule = run.Conditions.EngineCpuRule;
         if( rule == null )
         {
            w.Add( $"disclosure.dockerd.{session.Name}", T.Fill( T.DISCLOSURE_DOCKERD_NONE, session.Name ) );
            continue;
         }

         w.Add( $"disclosure.dockerd.{session.Name}", rule, S.Quote( run.Name, ENGINE_CPU_RULE, rule ) );
      }

      if( w.Sessions.Any( s => s.Runs[0].Conditions.EngineCpuRule != null ) )
      {
         w.Add( "disclosure.dockerd.test", T.Fill( T.DISCLOSURE_DOCKERD_TEST, ACCOUNTING_TEST ), S.File( ACCOUNTING_TEST_FILE, ACCOUNTING_TEST ) );
      }
   }

   #endregion Private Methods
}
