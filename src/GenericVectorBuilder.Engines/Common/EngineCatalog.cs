using System.Reflection;
using GenericVectorBuilder.Core.Contracts;

namespace GenericVectorBuilder.Engines.Common;

/// <summary>
/// Finds every engine sink in this assembly by reflection: any non-abstract class implementing
/// <see cref="ISink"/> with a public parameterless constructor (each sink defaults to the local
/// Docker container's address). The benchmark and the web app ask this catalog for sinks by name.
/// Why reflection: each engine lives in its own file, written independently, so no shared
/// registration list has to be edited when an engine is added.
/// </summary>
public static class EngineCatalog
{
   #region Public Methods

   /// <summary>
   /// Creates one instance of every engine sink, keyed by <see cref="ISink.Name"/>.
   /// </summary>
   /// <returns>Sinks by name.</returns>
   public static IReadOnlyDictionary<string, ISink> CreateAll()
   {
      return typeof( EngineCatalog ).Assembly.GetTypes()
         .Where( t => t is { IsClass: true, IsAbstract: false } && typeof( ISink ).IsAssignableFrom( t ) && t.GetConstructor( Type.EmptyTypes ) != null )
         .Select( t => (ISink)Activator.CreateInstance( t )! )
         .ToDictionary( s => s.Name, StringComparer.OrdinalIgnoreCase );
   }

   #endregion Public Methods
}
