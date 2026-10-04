namespace GenericVectorBuilder.Web.Runs;

/// <summary>
/// The password typed on the page for one database run, kept apart from the request.
/// Why: the registry keeps every run, request included, until the service restarts. A password
/// inside the request would live that long, could be reached by anything that reads a job, and
/// would print with it. Held here instead, it is handed to the worker once and then forgotten,
/// so it lives in memory for the run and no longer. It is never written to disk, a log or a
/// progress snapshot.
/// </summary>
public sealed class RunSecret
{
   #region Data Members

   private string? _password;

   #endregion Data Members

   #region Constructor

   /// <summary>
   /// Holds a password.
   /// </summary>
   /// <param name="password">The password.</param>
   public RunSecret( string? password )
   {
      _password = password;
   }

   #endregion Constructor

   #region Public Methods

   /// <summary>
   /// Hands the password out once and forgets it. A second call, or a call after
   /// <see cref="Clear"/>, gets null.
   /// </summary>
   /// <returns>The password, or null when it was already taken or cleared.</returns>
   public string? Take()
   {
      return Interlocked.Exchange( ref _password, null );
   }

   /// <summary>
   /// Forgets the password without using it, for a run that was cancelled before it started.
   /// </summary>
   public void Clear()
   {
      Interlocked.Exchange( ref _password, null );
   }

   /// <summary>
   /// Describes the secret without revealing it.
   /// </summary>
   /// <returns>A fixed text.</returns>
   public override string ToString()
   {
      return "RunSecret { ***** }";
   }

   #endregion Public Methods
}
