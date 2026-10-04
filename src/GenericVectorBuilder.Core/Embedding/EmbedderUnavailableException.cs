namespace GenericVectorBuilder.Core.Embedding;

/// <summary>
/// The embedding service could not give a usable answer: it is down, it timed out, or it
/// answered with an error status. Carries a plain-English message so the page can show it
/// as is, and lets the web layer answer 503 instead of a bare 500.
/// </summary>
public sealed class EmbedderUnavailableException : Exception
{
   #region Constructor

   /// <summary>
   /// Creates the exception.
   /// </summary>
   /// <param name="message">What went wrong, in plain English.</param>
   public EmbedderUnavailableException( string message )
      : base( message )
   {
   }

   /// <summary>
   /// Creates the exception with the failure that caused it.
   /// </summary>
   /// <param name="message">What went wrong, in plain English.</param>
   /// <param name="inner">The underlying failure.</param>
   public EmbedderUnavailableException( string message, Exception inner )
      : base( message, inner )
   {
   }

   #endregion Constructor
}
