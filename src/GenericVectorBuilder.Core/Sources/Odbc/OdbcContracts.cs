namespace GenericVectorBuilder.Core.Sources.Odbc;

/// <summary>
/// One ODBC connection the user can pick: a connection configured in appsettings, or a data
/// source name (DSN) set up on the machine.
/// Why the credential flag: the page should only ask for a user name and password when the
/// connection does not already carry them, so a DSN with a stored login or Windows sign-in
/// is one click.
/// </summary>
/// <param name="Name">The name to pass back as "connection": a configured name or a DSN name.</param>
/// <param name="Kind">"configured", "user DSN" or "system DSN".</param>
/// <param name="Driver">The ODBC driver name, e.g. "ODBC Driver 18 for SQL Server", or null when unknown.</param>
/// <param name="NeedsCredentials">True when the connection carries no login of its own, so the user must supply one.</param>
public sealed record OdbcConnectionInfo( string Name, string Kind, string? Driver, bool NeedsCredentials );

/// <summary>
/// One table or view the connection exposes.
/// </summary>
/// <param name="Schema">Owning schema, or "" for databases without schemas.</param>
/// <param name="Name">Table or view name, exactly as the database reports it.</param>
/// <param name="Type">"TABLE" or "VIEW".</param>
/// <param name="RowCount">Row count when it was cheap to learn, else null. Views are always null,
/// because counting a view runs its whole query.</param>
public sealed record OdbcTableInfo( string Schema, string Name, string Type, long? RowCount );

/// <summary>
/// What the page shows before a run: the columns that will be read, ten sample rows formatted
/// exactly as the run will format them, the suggested row id, and the columns left out.
/// Why the stable id is here: mappings chosen in the preview (row id, template) are keyed by it,
/// and it is the same id the source stamps on every record.
/// </summary>
/// <param name="Schema">Owning schema, or "".</param>
/// <param name="Name">Table or view name.</param>
/// <param name="TableName">Display name used for the run, e.g. "production_product".</param>
/// <param name="TableId">Stable table id used in document keys, from the connection name, schema and name.</param>
/// <param name="Columns">Columns the run reads, in table order.</param>
/// <param name="Sample">Up to ten rows aligned with <paramref name="Columns"/>; null is an empty value.</param>
/// <param name="SuggestedKey">Column proven to identify a row (a primary key, a unique index, or an id
/// column checked to be unique), or null when none was found.</param>
/// <param name="SkippedColumns">Columns that are not read: binary data and types the builder cannot turn into text.</param>
public sealed record OdbcTablePreview( string Schema, string Name, string TableName, string TableId, IReadOnlyList<string> Columns,
   IReadOnlyList<IReadOnlyList<string?>> Sample, string? SuggestedKey, IReadOnlyList<string> SkippedColumns );

/// <summary>
/// A table or view the user picked for a run.
/// </summary>
/// <param name="Schema">Owning schema, or "".</param>
/// <param name="Name">Table or view name.</param>
public sealed record OdbcSelection( string Schema, string Name );

/// <summary>
/// A login typed into the page for one request. Never stored, logged, or put into an error.
/// Why ToString is overridden: a record prints every property by default, so one stray log line
/// or interpolated string would leak the password.
/// </summary>
/// <param name="User">User name, or null to use whatever the connection already has.</param>
/// <param name="Password">Password, or null to use whatever the connection already has.</param>
public sealed record OdbcCredentials( string? User, string? Password )
{
   /// <summary>
   /// No credentials: the connection's own login is used.
   /// </summary>
   public static OdbcCredentials None { get; } = new( null, null );

   /// <summary>
   /// Describes the credentials without the password.
   /// </summary>
   /// <returns>The user name and a masked password.</returns>
   public override string ToString()
   {
      return $"OdbcCredentials {{ User = {User ?? "(none)"}, Password = {( Password == null ? "(none)" : "***" )} }}";
   }
}
