using Microsoft.AspNetCore.Server.Kestrel.Core;
using SearchService.Api.Grpc;
using SearchService.Application;
using SearchService.Infrastructure;
using Serilog;

// Readable, container friendly console layout: instant, level, message, then the exception.
const string OutputTemplate =
    "[{Timestamp:yyyy-MM-dd HH:mm:ss.fff} {Level:u3}] {Message:lj}{NewLine}{Exception}";

// Bootstrap logger so failures raised before the host is built are still visible.
Log.Logger = new LoggerConfiguration()
    .WriteTo.Console(outputTemplate: OutputTemplate)
    .CreateBootstrapLogger();

try
{
    var builder = WebApplication.CreateBuilder(args);

    builder.Host.UseSerilog((context, configuration) => configuration
        .ReadFrom.Configuration(context.Configuration)
        .Enrich.FromLogContext()
        .Enrich.WithMachineName()
        .WriteTo.Console(outputTemplate: OutputTemplate));

    // gRPC without TLS requires HTTP/2 cleartext (h2c). Inside the compose network there is no
    // certificate to negotiate ALPN with, so every endpoint is pinned to HTTP/2 explicitly.
    builder.WebHost.ConfigureKestrel(options =>
        options.ConfigureEndpointDefaults(endpoint => endpoint.Protocols = HttpProtocols.Http2));

    builder.Services.AddGrpc(options =>
    {
        // Exception detail is useful while developing and leaks internals in production.
        options.EnableDetailedErrors = builder.Environment.IsDevelopment();
    });

    // Server reflection lets grpcurl and similar tools discover search.v1.SearchGrpcService
    // without being handed a copy of search.proto. It is registered unconditionally but only
    // mapped in Development below, because reflection advertises the full service surface and
    // that is not something to expose from a production endpoint.
    builder.Services.AddGrpcReflection();

    builder.Services.AddSearchApplication();
    builder.Services.AddSearchInfrastructure(builder.Configuration);

    var app = builder.Build();

    app.UseSerilogRequestLogging();

    app.MapGrpcService<SearchGrpcServiceImpl>();

    if (app.Environment.IsDevelopment())
    {
        app.MapGrpcReflectionService();
    }

    // Liveness only: reaching this handler proves the host is up and serving. It deliberately
    // makes no claim about the broker, because a broker outage must not take the Search Service
    // out of rotation -- searches keep running and keep being served, and completion events wait
    // in the outbox until the broker returns.
    //
    // Both this and the hint below are served over h2c like every other endpoint here, so a
    // browser cannot open them and a shell probe needs an HTTP/2 capable client.
    app.MapGet("/health", () => Results.Ok(new { status = "healthy" }));

    app.MapGet("/", () =>
        "Search Service speaks gRPC over HTTP/2 cleartext. "
        + "Use a gRPC client for search.v1.SearchGrpcService; this URL is not a REST endpoint.");

    await app.RunAsync();

    return 0;
}
catch (Exception exception) when (exception is not HostAbortedException
                                  && exception.GetType().Name is not "StopTheHostException")
{
    // "StopTheHostException" is thrown by the test host to unwind the entry point once the
    // application has been built; swallowing it would break WebApplicationFactory.
    Log.Fatal(exception, "Search Service terminated unexpectedly.");

    return 1;
}
finally
{
    await Log.CloseAndFlushAsync();
}

namespace SearchService.Api
{
    /// <summary>
    /// Entry point marker for the Search Service host.
    /// </summary>
    /// <remarks>
    /// Top level statements compile into an internal entry point class, which
    /// <c>WebApplicationFactory&lt;TEntryPoint&gt;</c> cannot reference. This public partial
    /// declaration gives the integration tests a stable, unambiguous handle on the assembly.
    /// </remarks>
    public partial class Program
    {
    }
}
