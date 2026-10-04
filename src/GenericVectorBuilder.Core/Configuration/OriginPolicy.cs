namespace GenericVectorBuilder.Core.Configuration;

/// <summary>
/// Decides whether a browser request came from this site or from another one.
/// Why: the page has no login, so any web page open in a browser on the LAN could fire a
/// no-cors POST at the API (for example the pipeline reset) and the server would obey. A
/// browser always names the page a POST came from in the Origin header, so a POST whose Origin
/// is not this server's own address is refused. Scripts such as curl send no Origin and are
/// not affected.
/// </summary>
public static class OriginPolicy
{
   #region Public Methods

   /// <summary>
   /// True when a request carries an Origin header that does not match the Host it was sent to.
   /// A missing Origin is not cross-site (not a browser cross-site request). An Origin of
   /// "null", a malformed one, or one with a scheme other than http or https counts as
   /// cross-site, because those come from sandboxed or hostile pages.
   /// </summary>
   /// <param name="origin">Value of the Origin header, or null or empty when absent.</param>
   /// <param name="host">Value of the Host header, for example "linus7795.lan:5080".</param>
   /// <returns>True when the request must be refused.</returns>
   public static bool IsCrossSite( string? origin, string? host )
   {
      if( string.IsNullOrEmpty( origin ) )
      {
         return false;
      }

      if( string.IsNullOrWhiteSpace( host ) || !Uri.TryCreate( origin, UriKind.Absolute, out Uri? originUri ) || !IsWebScheme( originUri ) )
      {
         return true;
      }

      // Rebuilding the Host as a URL with the Origin's scheme drops default ports on both
      // sides, so "http://a" and "a:80" compare equal.
      if( !Uri.TryCreate( $"{originUri.Scheme}://{host}", UriKind.Absolute, out Uri? hostUri ) )
      {
         return true;
      }

      return !string.Equals( originUri.Authority, hostUri.Authority, StringComparison.OrdinalIgnoreCase );
   }

   #endregion Public Methods

   #region Private Methods

   /// <summary>
   /// True for http and https origins.
   /// </summary>
   /// <param name="uri">The parsed origin.</param>
   /// <returns>True for a web origin.</returns>
   private static bool IsWebScheme( Uri uri )
   {
      return uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps;
   }

   #endregion Private Methods
}
