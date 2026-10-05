using System.Globalization;

namespace GenericVectorBuilder.Bench.Targets;

/// <summary>
/// Finds a process of the host's own services by its command name, leaving out the processes of
/// the same name that run inside a container.
/// Why this matters now: the benchmark runs SQL Server and Qdrant both natively (the
/// "sql-native" and "qdrant-native" comparison targets) and in containers (the default targets),
/// so "pgrep -x qdrant" finds two processes. Reading the memory or the environment of the first
/// one would silently report the container's numbers for the native server, or the other way round.
/// How a container process is recognised: its cgroup path (from /proc/PID/cgroup, which any user
/// may read) runs through a Docker or containerd scope, e.g. "0::/system.slice/docker-ID.scope".
/// </summary>
public static class HostProcess
{
   #region Data Members

   private static readonly string[] CONTAINER_MARKS = { "/docker", "docker-", "containerd", "/kubepods", "libpod-" };

   #endregion Data Members

   #region Public Methods

   /// <summary>
   /// Process ids of every process with this command name that runs outside any container, lowest first.
   /// </summary>
   /// <param name="name">Command name as /proc/PID/comm shows it, e.g. "qdrant".</param>
   /// <returns>The process ids; empty when none.</returns>
   public static IReadOnlyList<int> FindNative( string name )
   {
      var found = new List<int>();
      foreach( string folder in SafeList( "/proc" ) )
      {
         if( !int.TryParse( Path.GetFileName( folder ), NumberStyles.None, CultureInfo.InvariantCulture, out int pid ) )
         {
            continue;
         }

         if( Read( $"/proc/{pid}/comm" )?.Trim() == name && Read( $"/proc/{pid}/cgroup" ) is string cgroup && !IsContainer( cgroup ) )
         {
            found.Add( pid );
         }
      }

      found.Sort();
      return found;
   }

   /// <summary>
   /// Process ids of every process with this command name inside one container, lowest first.
   /// </summary>
   /// <param name="name">Command name, e.g. "qdrant".</param>
   /// <param name="containerId">The container's id or its first 12 characters (docker inspect).</param>
   /// <returns>The process ids; empty when none.</returns>
   public static IReadOnlyList<int> FindInContainer( string name, string containerId )
   {
      if( containerId.Length < 12 )
      {
         return Array.Empty<int>();
      }

      var found = new List<int>();
      foreach( string folder in SafeList( "/proc" ) )
      {
         if( int.TryParse( Path.GetFileName( folder ), NumberStyles.None, CultureInfo.InvariantCulture, out int pid )
            && Read( $"/proc/{pid}/comm" )?.Trim() == name && Read( $"/proc/{pid}/cgroup" ) is string cgroup && cgroup.Contains( containerId, StringComparison.Ordinal ) )
         {
            found.Add( pid );
         }
      }

      found.Sort();
      return found;
   }

   /// <summary>
   /// The names of a process's threads (/proc/PID/task/TID/comm), for counting an engine's thread
   /// pools by their names (Qdrant names its search workers "search-N").
   /// </summary>
   /// <param name="pid">Process id.</param>
   /// <returns>Thread names; empty when the process is gone or its threads cannot be read.</returns>
   public static IReadOnlyList<string> ThreadNames( int pid )
   {
      return SafeList( $"/proc/{pid}/task" ).Select( t => Read( Path.Combine( t, "comm" ) )?.Trim() ).OfType<string>().ToList();
   }

   /// <summary>
   /// True when the text of /proc/PID/cgroup puts the process inside a container.
   /// </summary>
   /// <param name="cgroup">The file's text.</param>
   /// <returns>True for a container process.</returns>
   public static bool IsContainer( string cgroup )
   {
      return CONTAINER_MARKS.Any( mark => cgroup.Contains( mark, StringComparison.Ordinal ) );
   }

   #endregion Public Methods

   #region Private Methods

   /// <summary>
   /// Lists a folder, or nothing when it cannot be listed.
   /// </summary>
   /// <param name="path">Folder.</param>
   /// <returns>Entries.</returns>
   private static IEnumerable<string> SafeList( string path )
   {
      try
      {
         return Directory.EnumerateDirectories( path ).ToList();
      }
      catch( Exception ex ) when( ex is IOException or UnauthorizedAccessException )
      {
         return Array.Empty<string>();
      }
   }

   /// <summary>
   /// Reads a small /proc file, or null when the process went away or the file is not readable.
   /// </summary>
   /// <param name="path">File.</param>
   /// <returns>The text, or null.</returns>
   private static string? Read( string path )
   {
      try
      {
         return File.ReadAllText( path );
      }
      catch( Exception ex ) when( ex is IOException or UnauthorizedAccessException )
      {
         return null;
      }
   }

   #endregion Private Methods
}
