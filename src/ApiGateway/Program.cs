using ApiGateway.Endpoints;
using ApiGateway.GrpcClients;
using ApiGateway.Middleware;
using ApiGateway.Validators;
using FluentValidation;
using Microsoft.OpenApi.Models;
using Serilog;
using Shared.GrpcContracts;

const string OutputTemplate =
    "[{Timestamp:HH:mm:ss} {Level:u3}] {Message:lj}{NewLine}{Exception}";

const string ApiTitle = "Async Search API Gateway";
const string ApiVersion = "v1";

// Bootstrap logger: covers failures that happen before the host — and therefore the configured
// logger — exists, so a bad configuration is never silent.
Log.Logger = new LoggerConfiguration()
    .MinimumLevel.Information()
    .Enrich.FromLogContext()
    .WriteTo.Console(outputTemplate: OutputTemplate)
    .CreateBootstrapLogger();

try
{
    Log.Information("Starting API Gateway.");

    var builder = WebApplication.CreateBuilder(args);

    builder.Host.UseSerilog((context, services, loggerConfiguration) => loggerConfiguration
        .ReadFrom.Configuration(context.Configuration)
        .ReadFrom.Services(services)
        .Enrich.FromLogContext()
        .Enrich.WithMachineName()
        .WriteTo.Console(outputTemplate: OutputTemplate));

    builder.Services.AddValidatorsFromAssemblyContaining<StartSearchRequestValidator>();
    builder.Services.AddProblemDetails();
    builder.Services.AddScoped<ExceptionHandlingMiddleware>();

    // Body binding failures (absent, truncated or invalid JSON) otherwise short-circuit with a
    // bodiless 400 outside Development. Throwing routes them through the error boundary instead,
    // so every environment answers with the same problem document.
    builder.Services.Configure<RouteHandlerOptions>(options => options.ThrowOnBadRequest = true);

    // The address of the only downstream dependency. Failing fast here beats discovering the
    // misconfiguration on the first request.
    var searchServiceAddress = builder.Configuration["Grpc:SearchService"]
        ?? throw new InvalidOperationException("Grpc:SearchService is not configured.");

    builder.Services.AddGrpcClient<SearchGrpcService.SearchGrpcServiceClient>(options =>
        options.Address = new Uri(searchServiceAddress));

    builder.Services.AddScoped<ISearchServiceClient, SearchServiceClient>();

    builder.Services.AddEndpointsApiExplorer();
    builder.Services.AddSwaggerGen(options => options.SwaggerDoc(ApiVersion, new OpenApiInfo
    {
        Title = ApiTitle,
        Version = ApiVersion,
        Description =
            "Starts asynchronous hotel searches and polls them for results. "
            + "Every call is forwarded to the Search Service over gRPC.",
    }));

    var app = builder.Build();

    // Request logging first, so the error boundary below runs inside its enrichment scope and the
    // translated status code is the one attributed to the request.
    app.UseSerilogRequestLogging();
    app.UseMiddleware<ExceptionHandlingMiddleware>();

    // Gives the responses nothing threw for — an unmatched route, for example — the same
    // RFC 7807 body as the ones the error boundary produces.
    app.UseStatusCodePages();

    if (app.Environment.IsDevelopment())
    {
        app.UseSwagger();
        app.UseSwaggerUI(options =>
            options.SwaggerEndpoint($"/swagger/{ApiVersion}/swagger.json", $"{ApiTitle} {ApiVersion}"));
    }

    app.MapSearchEndpoints();

    app.MapGet("/health", () => Results.Ok(new { status = "healthy" }))
        .WithName("Health")
        .WithSummary("Reports whether the gateway process is up.")
        .WithDescription("Liveness probe used by Docker Compose and by the integration tests.")
        .WithTags("Health")
        .Produces(StatusCodes.Status200OK);

    Log.Information("API Gateway started. SearchServiceAddress={SearchServiceAddress}", searchServiceAddress);

    await app.RunAsync();

    return 0;
}
catch (Exception exception)
{
    Log.Fatal(exception, "API Gateway terminated unexpectedly.");
    return 1;
}
finally
{
    await Log.CloseAndFlushAsync();
}

namespace ApiGateway
{
    /// <summary>
    /// Entry point marker for the API Gateway host.
    /// </summary>
    /// <remarks>
    /// The executable is built from top-level statements; this declaration gives the integration
    /// tests an unambiguous, namespace-qualified type to hand to
    /// <c>WebApplicationFactory&lt;T&gt;</c> when both hosts are referenced from one test project.
    /// </remarks>
    public partial class Program
    {
    }
}
