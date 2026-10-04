using System.Text.Json.Serialization;
using GenericVectorBuilder.Core.Configuration;
using GenericVectorBuilder.Core.Contracts;
using GenericVectorBuilder.Core.Sources.Odbc;
using GenericVectorBuilder.Engines.Common;
using GenericVectorBuilder.Web.Destinations;
using GenericVectorBuilder.Web.Endpoints;
using GenericVectorBuilder.Web.Git;
using GenericVectorBuilder.Web.Runs;
using Microsoft.AspNetCore.Diagnostics;
using Microsoft.AspNetCore.Http.Features;

// GenericVectorBuilder web host: serves the single-page UI from wwwroot, the JSON API under
// /api, and one background worker that executes runs one at a time on the GPU box.
// Runs as a systemd service on linus7795 (UseSystemd is a no-op when started by hand).
// Destinations are SQL Server and Qdrant plus every engine sink in the engine catalog. Core
// cannot see the engines, so they are created here and handed to the services as optional
// destinations; creating them opens no connection.

const long MAX_UPLOAD_BYTES = 1L * 1024 * 1024 * 1024;

var builder = WebApplication.CreateBuilder( args );
builder.Host.UseSystemd();

GvbSettings settings = ( builder.Configuration.GetSection( "Gvb" ).Get<GvbSettings>() ?? new GvbSettings() ).Normalize();
builder.Services.AddSingleton( settings );
IReadOnlyList<ISink> engines = EngineCatalog.CreateAll().Values.ToList();
builder.Services.AddSingleton( _ => new GvbServices( settings, engines ) );
builder.Services.AddSingleton( _ => DestinationCatalog.Build( engines ) );
builder.Services.AddSingleton( _ => new OdbcCatalog( settings ) );
builder.Services.AddSingleton( _ => new GitWorkspace( settings ) );
builder.Services.AddSingleton<RunRegistry>();
builder.Services.AddHostedService<RunWorker>();
builder.Services.ConfigureHttpJsonOptions( o => o.SerializerOptions.Converters.Add( new JsonStringEnumConverter() ) );
builder.Services.Configure<FormOptions>( o => o.MultipartBodyLengthLimit = MAX_UPLOAD_BYTES );
builder.WebHost.ConfigureKestrel( k => k.Limits.MaxRequestBodySize = MAX_UPLOAD_BYTES );

WebApplication app = builder.Build();

// Unhandled failures answer JSON with a plain message (the page shows it as is) instead of a
// bare 500. The exception handler middleware writes the details to the log.
app.UseExceptionHandler( errors => errors.Run( async context =>
{
   Exception? failure = context.Features.Get<IExceptionHandlerFeature>()?.Error;
   context.Response.StatusCode = failure is BadHttpRequestException bad ? bad.StatusCode : StatusCodes.Status500InternalServerError;
   await context.Response.WriteAsJsonAsync( new { error = failure is BadHttpRequestException ? "That request could not be read." : "Something went wrong on the server. The details are in the service log." } );
} ) );

// The page has no login, so a POST that a browser says came from another website is refused
// (see OriginPolicy). Reads are left alone.
app.Use( async ( context, next ) =>
{
   HttpRequest request = context.Request;
   bool readOnly = HttpMethods.IsGet( request.Method ) || HttpMethods.IsHead( request.Method ) || HttpMethods.IsOptions( request.Method );
   if( !readOnly && OriginPolicy.IsCrossSite( request.Headers.Origin.ToString(), request.Host.Value ) )
   {
      context.Response.StatusCode = StatusCodes.Status403Forbidden;
      await context.Response.WriteAsJsonAsync( new { error = "That request came from another website, so it was refused." } );
      return;
   }

   await next();
} );

app.UseDefaultFiles();
app.UseStaticFiles();
ScanEndpoints.Map( app );
OdbcEndpoints.Map( app );
GitEndpoints.Map( app );
RunEndpoints.Map( app );
app.Run();
