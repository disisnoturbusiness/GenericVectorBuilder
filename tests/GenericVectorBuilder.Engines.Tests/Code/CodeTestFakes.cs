using System.Security.Cryptography;
using System.Text;
using GenericVectorBuilder.Core.Contracts;

namespace GenericVectorBuilder.Engines.Tests.Code;

// Copies of the unit-test fakes in tests/GenericVectorBuilder.Tests/Fakes/TestFakes.cs. This
// test project does not reference that one, so the code tests carry their own copies.

/// <summary>
/// A throwaway folder under the system temp directory, deleted on dispose.
/// </summary>
public sealed class CodeTempFolder : IDisposable
{
   #region Constructor

   /// <summary>
   /// Creates an empty temp folder.
   /// </summary>
   public CodeTempFolder()
   {
      Path = System.IO.Path.Combine( System.IO.Path.GetTempPath(), "gvb-codetest-" + Guid.NewGuid().ToString( "N" )[..10] );
      Directory.CreateDirectory( Path );
   }

   #endregion Constructor

   #region Public Methods

   /// <summary>Absolute path of the folder.</summary>
   public string Path { get; }

   /// <summary>
   /// Writes a text file (UTF-8, no BOM) relative to the folder, creating sub-folders.
   /// </summary>
   /// <param name="relative">Relative path.</param>
   /// <param name="content">File content.</param>
   /// <returns>The full path.</returns>
   public string Write( string relative, string content )
   {
      return WriteBytes( relative, new UTF8Encoding( false ).GetBytes( content ) );
   }

   /// <summary>
   /// Writes raw bytes relative to the folder, creating sub-folders.
   /// </summary>
   /// <param name="relative">Relative path.</param>
   /// <param name="bytes">File bytes.</param>
   /// <returns>The full path.</returns>
   public string WriteBytes( string relative, byte[] bytes )
   {
      string full = System.IO.Path.Combine( Path, relative );
      Directory.CreateDirectory( System.IO.Path.GetDirectoryName( full )! );
      File.WriteAllBytes( full, bytes );
      return full;
   }

   #endregion Public Methods

   #region IDisposable

   /// <summary>
   /// Deletes the folder and everything in it.
   /// </summary>
   public void Dispose()
   {
      try
      {
         Directory.Delete( Path, recursive: true );
      }
      catch( Exception ex ) when( ex is IOException or UnauthorizedAccessException )
      {
         // Best effort; temp space is cleaned by the OS eventually.
      }
   }

   #endregion IDisposable
}

/// <summary>
/// Deterministic embedder for unit tests: the vector is derived from a SHA-256 of the text,
/// so identical text gives identical vectors and no GPU is needed.
/// </summary>
public sealed class CodeFakeEmbedder : IEmbedder
{
   #region Public Methods

   /// <summary>Texts embedded so far, to assert what was (re)embedded.</summary>
   public List<string> Embedded { get; } = new();

   /// <summary>Fingerprint; tests change it to simulate a model swap.</summary>
   public string Fingerprint { get; set; } = "fake|v1";

   /// <inheritdoc />
   public int Dimension => 8;

   /// <inheritdoc />
   public Task PreflightAsync( CancellationToken ct ) => Task.CompletedTask;

   /// <inheritdoc />
   public Task<float[][]> EmbedDocumentsAsync( IReadOnlyList<string> texts, CancellationToken ct )
   {
      Embedded.AddRange( texts );
      return Task.FromResult( texts.Select( Vector ).ToArray() );
   }

   /// <inheritdoc />
   public Task<float[]> EmbedQueryAsync( string query, CancellationToken ct ) => Task.FromResult( Vector( query ) );

   #endregion Public Methods

   #region Private Methods

   /// <summary>
   /// Builds a non-zero vector from the text's hash.
   /// </summary>
   /// <param name="text">Text.</param>
   /// <returns>An 8-dim vector.</returns>
   private static float[] Vector( string text )
   {
      byte[] hash = SHA256.HashData( Encoding.UTF8.GetBytes( text ) );
      return Enumerable.Range( 0, 8 ).Select( i => hash[i] / 255f + 0.01f ).ToArray();
   }

   #endregion Private Methods
}

/// <summary>
/// In-memory sink that can be told to fail, for testing the runner's per-sink isolation.
/// It behaves like a real sink about its collection: the first EnsureCollectionAsync creates
/// it (and says so), and DropCollectionAsync removes it, so tests can simulate a collection
/// that was wiped behind the pipeline's back.
/// </summary>
public sealed class CodeMemorySink : ISink
{
   #region Data Members

   private bool _exists;

   #endregion Data Members

   #region Constructor

   /// <summary>
   /// Creates the sink.
   /// </summary>
   /// <param name="name">Sink name.</param>
   public CodeMemorySink( string name )
   {
      Name = name;
   }

   #endregion Constructor

   #region Public Methods

   /// <inheritdoc />
   public string Name { get; }

   /// <summary>Stored records by chunk id.</summary>
   public Dictionary<Guid, VectorRecord> Records { get; } = new();

   /// <summary>When true, every upsert throws.</summary>
   public bool FailUpserts { get; set; }

   /// <summary>When true, every delete throws before removing anything.</summary>
   public bool FailDeletes { get; set; }

   /// <summary>Runs after each successful delete, e.g. to cancel the run at that exact point.</summary>
   public Action? AfterDelete { get; set; }

   /// <summary>Chunk ids deleted so far.</summary>
   public List<Guid> Deleted { get; } = new();

   /// <inheritdoc />
   public Task<bool> EnsureCollectionAsync( string collection, int dimension, CancellationToken ct )
   {
      bool created = !_exists;
      _exists = true;
      return Task.FromResult( created );
   }

   /// <inheritdoc />
   public Task UpsertAsync( string collection, IReadOnlyList<VectorRecord> records, CancellationToken ct )
   {
      ct.ThrowIfCancellationRequested();
      if( FailUpserts )
      {
         throw new InvalidOperationException( $"{Name} is down" );
      }

      foreach( VectorRecord r in records )
      {
         Records[r.Chunk.ChunkId] = r;
      }

      return Task.CompletedTask;
   }

   /// <inheritdoc />
   public Task DeleteAsync( string collection, IReadOnlyList<Guid> chunkIds, CancellationToken ct )
   {
      ct.ThrowIfCancellationRequested();
      if( FailDeletes )
      {
         throw new InvalidOperationException( $"{Name} refused the delete" );
      }

      foreach( Guid id in chunkIds )
      {
         Records.Remove( id );
         Deleted.Add( id );
      }

      AfterDelete?.Invoke();
      return Task.CompletedTask;
   }

   /// <inheritdoc />
   public Task<long> CountAsync( string collection, CancellationToken ct ) => Task.FromResult( (long)Records.Count );

   /// <inheritdoc />
   public Task<IReadOnlyList<SearchHit>> SearchAsync( string collection, float[] vector, int top, CancellationToken ct )
   {
      return Task.FromResult<IReadOnlyList<SearchHit>>( Array.Empty<SearchHit>() );
   }

   /// <inheritdoc />
   public Task DropCollectionAsync( string collection, CancellationToken ct )
   {
      Records.Clear();
      _exists = false;
      return Task.CompletedTask;
   }

   /// <summary>
   /// Texts currently stored, for readable assertions.
   /// </summary>
   /// <returns>Chunk texts.</returns>
   public List<string> Texts() => Records.Values.Select( r => r.Chunk.Text ).OrderBy( t => t, StringComparer.Ordinal ).ToList();

   #endregion Public Methods
}
