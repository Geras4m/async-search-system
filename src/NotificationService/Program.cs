using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using NotificationService.Consumers;
using NotificationService.Messaging;
using Serilog;

const string outputTemplate =
    "[{Timestamp:HH:mm:ss} {Level:u3}] {Message:lj}{NewLine}{Exception}";

// A bootstrap logger captures anything that goes wrong before the host, and therefore the
// configured logger, exists. It is replaced by the fully configured pipeline on Build().
Log.Logger = new LoggerConfiguration()
    .WriteTo.Console(outputTemplate: outputTemplate)
    .CreateBootstrapLogger();

try
{
    var builder = Host.CreateApplicationBuilder(args);

    builder.Services.AddSerilog((services, configuration) => configuration
        .ReadFrom.Configuration(builder.Configuration)
        .ReadFrom.Services(services)
        .Enrich.FromLogContext()
        .Enrich.WithMachineName()
        .WriteTo.Console(outputTemplate: outputTemplate));

    // Validated on start: a worker whose broker settings are wrong should fail loudly at
    // launch rather than silently never consuming anything.
    builder.Services
        .AddOptions<RabbitMqOptions>()
        .Bind(builder.Configuration.GetSection(RabbitMqOptions.SectionName))
        .ValidateDataAnnotations()

        // Data annotations cannot express this one, and it matters for the same reason it does
        // on the publishing side: a negative delay throws out of Task.Delay inside the connection
        // retry ladder, turning a recoverable broker outage into a hard start-up failure.
        .Validate(
            options => options.RetryDelay >= TimeSpan.Zero,
            $"{RabbitMqOptions.SectionName}:RetryDelay must not be negative.")
        .ValidateOnStart();

    // One connection for the whole process; the consumer multiplexes a channel over it.
    builder.Services.AddSingleton<IRabbitMqConnectionProvider, RabbitMqConnectionProvider>();
    builder.Services.AddHostedService<SearchCompletedConsumer>();

    var host = builder.Build();

    Log.Information("Notification Service starting.");

    await host.RunAsync().ConfigureAwait(false);

    return 0;
}
catch (OperationCanceledException)
{
    // Shutdown was requested while the host was still starting. Not a failure.
    Log.Information("Notification Service start-up was cancelled.");
    return 0;
}
catch (Exception ex) when (ex is not OperationCanceledException)
{
    Log.Fatal(ex, "Notification Service terminated unexpectedly.");
    return 1;
}
finally
{
    await Log.CloseAndFlushAsync().ConfigureAwait(false);
}

namespace NotificationService
{
    /// <summary>
    /// Entry point marker for the Notification Service worker host. Exposed so test projects
    /// can reference this assembly through a stable, unambiguous type.
    /// </summary>
    public sealed partial class Program
    {
    }
}
