using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using SearchService.Application.Abstractions;
using SearchService.Application.Options;
using SearchService.Domain.Repositories;
using SearchService.Infrastructure.BackgroundJobs;
using SearchService.Infrastructure.Generation;
using SearchService.Infrastructure.Messaging;
using SearchService.Infrastructure.Persistence;

namespace SearchService.Infrastructure;

/// <summary>
/// Composition root for the Search Service Infrastructure layer.
/// </summary>
/// <remarks>
/// Every outward-facing dependency the Application layer declares as an abstraction, the
/// store, the result generator, the execution queue and the broker publisher, is bound to a
/// concrete implementation here. The host only calls
/// <see cref="AddSearchInfrastructure"/>, so swapping any of these implementations is a
/// change to this file alone.
/// </remarks>
public static class DependencyInjection
{
    /// <summary>
    /// Registers the Search Service Infrastructure services and the background execution engine.
    /// </summary>
    /// <param name="services">The service collection to add to.</param>
    /// <param name="configuration">Configuration the options sections are bound from.</param>
    /// <returns>The same service collection, so calls can be chained.</returns>
    /// <exception cref="ArgumentNullException">Any argument is <see langword="null"/>.</exception>
    /// <remarks>
    /// <para>
    /// Both options types are validated with their data annotations and validated at start-up
    /// rather than on first use, so a mistyped batch interval or an out-of-range port stops
    /// the host immediately with a precise message instead of failing later inside a search.
    /// </para>
    /// <para>
    /// Lifetimes are deliberate. The repository is the store itself, so it must be a
    /// singleton; a scoped registration would give every request an empty dictionary. The
    /// scheduler is a singleton because the writer side, the gRPC request path, and the
    /// reader side, the background engine, have to share one queue. The connection provider
    /// and the publisher are singletons because they own a broker connection and a channel.
    /// The result generator is stateless and therefore also shared.
    /// </para>
    /// </remarks>
    public static IServiceCollection AddSearchInfrastructure(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configuration);

        services
            .AddOptions<SearchExecutionOptions>()
            .Bind(configuration.GetSection(SearchExecutionOptions.SectionName))
            .ValidateDataAnnotations()
            .ValidateOnStart();

        services
            .AddOptions<RabbitMqOptions>()
            .Bind(configuration.GetSection(RabbitMqOptions.SectionName))
            .ValidateDataAnnotations()
            .ValidateOnStart();

        services.AddSingleton<ISearchRepository, InMemorySearchRepository>();
        services.AddSingleton<IHotelResultGenerator, SequentialHotelResultGenerator>();

        // Registered once as the concrete type and then exposed through the abstraction, so
        // both sides of the hand-off resolve the very same channel.
        services.AddSingleton<ChannelSearchExecutionScheduler>();
        services.AddSingleton<ISearchExecutionScheduler>(
            static provider => provider.GetRequiredService<ChannelSearchExecutionScheduler>());

        services.AddSingleton<IRabbitMqConnectionProvider, RabbitMqConnectionProvider>();
        services.AddSingleton<ISearchEventsPublisher, RabbitMqSearchEventsPublisher>();

        services.AddHostedService<SearchExecutionBackgroundService>();

        return services;
    }
}
