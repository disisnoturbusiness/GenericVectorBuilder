using System.Runtime.InteropServices;

namespace GenericVectorBuilder.Core.Sources.Odbc;

/// <summary>
/// A data source name as the ODBC driver manager reports it.
/// </summary>
/// <param name="Name">The DSN name.</param>
/// <param name="Driver">The driver it uses, or null when unknown.</param>
/// <param name="IsUser">True for a user DSN, false for a system DSN.</param>
internal sealed record OdbcDsnEntry( string Name, string? Driver, bool IsUser );

/// <summary>
/// Asks the ODBC driver manager itself which data sources and drivers exist, through
/// SQLDataSources and SQLDrivers: libodbc.so.2 (unixODBC) on Linux, odbc32.dll on Windows.
/// Why the driver manager and not the ini files or the registry: it is the component that will
/// open the connection, so its answer is the one that matches what a connection will find,
/// including environment overrides such as ODBCINI and ODBCSYSINI.
/// Why delegates instead of DllImport: the library name differs per platform, and loading it by
/// hand keeps this class self-contained (no assembly-wide import resolver another part of the app
/// could clash with) and lets a missing library become a null answer instead of a crash.
/// The wide-character entry points are used because System.Data.Odbc uses them too, so names
/// are decoded the same way the connection will see them.
/// </summary>
internal static class OdbcDriverManager
{
   #region Data Members

   private const short SQL_HANDLE_ENV = 1;
   private const int SQL_ATTR_ODBC_VERSION = 200;
   private const int SQL_OV_ODBC3 = 3;
   private const ushort SQL_FETCH_NEXT = 1;
   private const ushort SQL_FETCH_FIRST = 2;
   private const ushort SQL_FETCH_FIRST_USER = 31;
   private const ushort SQL_FETCH_FIRST_SYSTEM = 32;
   private const short SQL_SUCCESS = 0;
   private const short SQL_SUCCESS_WITH_INFO = 1;
   private const short SQL_NO_DATA = 100;
   private const short NAME_CHARS = 1024;
   private const short ATTRIBUTE_CHARS = 8192;

   /// <summary>Stops a misbehaving driver manager from looping forever.</summary>
   private const int MAX_ENTRIES = 10000;

   private static readonly string[] LIBRARY_CANDIDATES = OperatingSystem.IsWindows()
      ? new[] { "odbc32.dll" }
      : OperatingSystem.IsMacOS() ? new[] { "libodbc.2.dylib", "libiodbc.2.dylib" } : new[] { "libodbc.so.2", "libodbc.so.1", "libodbc.so" };

   #endregion Data Members

   #region Public Methods

   /// <summary>
   /// Lists user DSNs, then system DSNs.
   /// </summary>
   /// <returns>The DSNs, or null when the driver manager could not be loaded or asked.</returns>
   public static IReadOnlyList<OdbcDsnEntry>? ListDataSources()
   {
      return WithEnvironment( api =>
      {
         var entries = new List<OdbcDsnEntry>();
         foreach( (ushort first, bool isUser) in new[] { (SQL_FETCH_FIRST_USER, true), (SQL_FETCH_FIRST_SYSTEM, false) } )
         {
            foreach( (string name, string? driver) in Enumerate( api, api.DataSources, first, NAME_CHARS ) )
            {
               entries.Add( new OdbcDsnEntry( name, driver, isUser ) );
            }
         }

         return entries;
      } );
   }

   /// <summary>
   /// Lists the installed ODBC drivers by name.
   /// </summary>
   /// <returns>Driver names, or null when the driver manager could not be loaded or asked.</returns>
   public static IReadOnlyList<string>? ListDrivers()
   {
      return WithEnvironment( api => Enumerate( api, api.Drivers, SQL_FETCH_FIRST, ATTRIBUTE_CHARS ).Select( d => d.Name ).ToList() );
   }

   #endregion Public Methods

   #region Private Methods

   /// <summary>
   /// Loads the driver manager, allocates an ODBC 3 environment, runs the query and frees
   /// everything again. Any failure (no library, missing entry point, an error code) is a null
   /// answer so the caller can fall back to reading the configuration files.
   /// </summary>
   /// <typeparam name="T">Result type.</typeparam>
   /// <param name="query">What to ask, given the loaded functions and environment.</param>
   /// <returns>The answer, or null on any failure.</returns>
   private static T? WithEnvironment<T>( Func<OdbcNativeApi, T> query ) where T : class
   {
      IntPtr library = LoadLibrary();
      if( library == IntPtr.Zero )
      {
         return null;
      }

      try
      {
         var api = new OdbcNativeApi( library );
         if( !Ok( api.AllocHandle( SQL_HANDLE_ENV, IntPtr.Zero, out IntPtr environment ) ) )
         {
            return null;
         }

         try
         {
            api.Environment = environment;
            return Ok( api.SetEnvAttr( environment, SQL_ATTR_ODBC_VERSION, SQL_OV_ODBC3, 0 ) ) ? query( api ) : null;
         }
         finally
         {
            api.FreeHandle( SQL_HANDLE_ENV, environment );
         }
      }
      catch( Exception ex ) when( ex is EntryPointNotFoundException or MarshalDirectiveException or InvalidOperationException or ArgumentException )
      {
         return null;
      }
      finally
      {
         NativeLibrary.Free( library );
      }
   }

   /// <summary>
   /// Walks one SQLDataSources or SQLDrivers listing from the given first direction to the end.
   /// </summary>
   /// <param name="api">Loaded functions with an allocated environment.</param>
   /// <param name="call">SQLDataSourcesW or SQLDriversW.</param>
   /// <param name="first">The fetch direction that starts the listing.</param>
   /// <param name="secondChars">Size of the second buffer in characters (driver name or attribute list).</param>
   /// <returns>Name and second value pairs. The second value is null when empty.</returns>
   /// <exception cref="InvalidOperationException">The driver manager returned an error code.</exception>
   private static List<(string Name, string? Second)> Enumerate( OdbcNativeApi api, OdbcListFunction call, ushort first, short secondChars )
   {
      var results = new List<(string Name, string? Second)>();
      IntPtr nameBuffer = Marshal.AllocHGlobal( NAME_CHARS * sizeof( char ) );
      IntPtr secondBuffer = Marshal.AllocHGlobal( secondChars * sizeof( char ) );
      try
      {
         ushort direction = first;
         while( results.Count < MAX_ENTRIES )
         {
            short rc = call( api.Environment, direction, nameBuffer, NAME_CHARS, out short nameLength, secondBuffer, secondChars, out short secondLength );
            if( rc == SQL_NO_DATA )
            {
               break;
            }

            if( !Ok( rc ) )
            {
               throw new InvalidOperationException( $"The ODBC driver manager returned code {rc}." );
            }

            string name = ReadText( nameBuffer, nameLength, NAME_CHARS );
            string second = ReadText( secondBuffer, secondLength, secondChars );
            if( name.Length > 0 )
            {
               results.Add( (name, second.Length > 0 ? second : null) );
            }

            direction = SQL_FETCH_NEXT;
         }
      }
      finally
      {
         Marshal.FreeHGlobal( nameBuffer );
         Marshal.FreeHGlobal( secondBuffer );
      }

      return results;
   }

   /// <summary>
   /// Reads a returned wide string, clamped to the buffer in case the value was truncated.
   /// </summary>
   /// <param name="buffer">The buffer.</param>
   /// <param name="length">Length reported by the driver manager, in characters.</param>
   /// <param name="capacity">Buffer size in characters.</param>
   /// <returns>The text, without a trailing NUL.</returns>
   private static string ReadText( IntPtr buffer, short length, short capacity )
   {
      int chars = Math.Clamp( (int)length, 0, capacity - 1 );
      return ( Marshal.PtrToStringUni( buffer, chars ) ?? string.Empty ).TrimEnd( '\0' );
   }

   /// <summary>
   /// Loads the first driver manager library that exists on this platform.
   /// </summary>
   /// <returns>The library handle, or zero when none could be loaded.</returns>
   private static IntPtr LoadLibrary()
   {
      foreach( string candidate in LIBRARY_CANDIDATES )
      {
         if( NativeLibrary.TryLoad( candidate, out IntPtr handle ) )
         {
            return handle;
         }
      }

      return IntPtr.Zero;
   }

   /// <summary>
   /// True for SQL_SUCCESS and SQL_SUCCESS_WITH_INFO.
   /// </summary>
   /// <param name="rc">Return code.</param>
   /// <returns>True when the call worked.</returns>
   private static bool Ok( short rc ) => rc is SQL_SUCCESS or SQL_SUCCESS_WITH_INFO;

   #endregion Private Methods
}

/// <summary>SQLAllocHandle.</summary>
[UnmanagedFunctionPointer( CallingConvention.Winapi )]
internal delegate short OdbcAllocHandleFunction( short handleType, IntPtr inputHandle, out IntPtr outputHandle );

/// <summary>SQLSetEnvAttr with an integer value passed in the pointer slot, as ODBC expects.</summary>
[UnmanagedFunctionPointer( CallingConvention.Winapi )]
internal delegate short OdbcSetEnvAttrFunction( IntPtr environment, int attribute, IntPtr value, int stringLength );

/// <summary>SQLFreeHandle.</summary>
[UnmanagedFunctionPointer( CallingConvention.Winapi )]
internal delegate short OdbcFreeHandleFunction( short handleType, IntPtr handle );

/// <summary>SQLDataSourcesW and SQLDriversW share this shape: two wide-string out buffers with their lengths.</summary>
[UnmanagedFunctionPointer( CallingConvention.Winapi )]
internal delegate short OdbcListFunction( IntPtr environment, ushort direction, IntPtr name, short nameMax, out short nameLength,
   IntPtr second, short secondMax, out short secondLength );

/// <summary>
/// The driver manager functions <see cref="OdbcDriverManager"/> calls, bound from one loaded
/// library, plus the environment handle they share.
/// </summary>
internal sealed class OdbcNativeApi
{
   #region Constructor

   /// <summary>
   /// Binds every function. A missing export throws EntryPointNotFoundException, which the
   /// caller turns into a null answer.
   /// </summary>
   /// <param name="library">Loaded library handle.</param>
   public OdbcNativeApi( IntPtr library )
   {
      AllocHandle = Bind<OdbcAllocHandleFunction>( library, "SQLAllocHandle" );
      SetEnvAttr = Bind<OdbcSetEnvAttrFunction>( library, "SQLSetEnvAttr" );
      FreeHandle = Bind<OdbcFreeHandleFunction>( library, "SQLFreeHandle" );
      DataSources = Bind<OdbcListFunction>( library, "SQLDataSourcesW" );
      Drivers = Bind<OdbcListFunction>( library, "SQLDriversW" );
   }

   #endregion Constructor

   #region Public Methods

   /// <summary>SQLAllocHandle.</summary>
   public OdbcAllocHandleFunction AllocHandle { get; }

   /// <summary>SQLSetEnvAttr.</summary>
   public OdbcSetEnvAttrFunction SetEnvAttr { get; }

   /// <summary>SQLFreeHandle.</summary>
   public OdbcFreeHandleFunction FreeHandle { get; }

   /// <summary>SQLDataSourcesW.</summary>
   public OdbcListFunction DataSources { get; }

   /// <summary>SQLDriversW.</summary>
   public OdbcListFunction Drivers { get; }

   /// <summary>The allocated environment handle, set once SQLAllocHandle succeeded.</summary>
   public IntPtr Environment { get; set; }

   #endregion Public Methods

   #region Private Methods

   /// <summary>
   /// Binds one exported function to a delegate.
   /// </summary>
   /// <typeparam name="T">Delegate type.</typeparam>
   /// <param name="library">Loaded library.</param>
   /// <param name="name">Export name.</param>
   /// <returns>The delegate.</returns>
   private static T Bind<T>( IntPtr library, string name ) where T : Delegate
   {
      return Marshal.GetDelegateForFunctionPointer<T>( NativeLibrary.GetExport( library, name ) );
   }

   #endregion Private Methods
}
