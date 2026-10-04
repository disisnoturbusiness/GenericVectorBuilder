namespace GenericVectorBuilder.Core.Configuration;

/// <summary>
/// A pipeline reset removed what it could but at least one destination could not be reached.
/// The pipeline stays listed so the reset can be pressed again once the destination is back.
/// </summary>
public sealed class ResetIncompleteException : Exception
{
   #region Constructor

   /// <summary>
   /// Creates the exception.
   /// </summary>
   /// <param name="message">What was not removed, in plain English.</param>
   public ResetIncompleteException( string message )
      : base( message )
   {
   }

   #endregion Constructor
}
