using FluentValidation;
using MediatR;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using SearchService.Application.Abstractions;
using SearchService.Application.Behaviors;
using SearchService.Application.Validators;

namespace SearchService.Application;

/// <summary>
/// Composition root of the Application layer.
/// </summary>
/// <remarks>
/// Every host that needs the search workflow calls <see cref="AddSearchApplication"/> and then
/// supplies the Infrastructure implementations of the abstractions this layer declares. Keeping
/// the registrations here means a host never has to know which handlers, validators or pipeline
/// behaviours exist.
/// </remarks>
public static class DependencyInjection
{
    /// <summary>
    /// Registers the MediatR handlers, the request pipeline and the validators of the
    /// Application layer.
    /// </summary>
    /// <param name="services">The service collection to add the registrations to.</param>
    /// <returns>The same <paramref name="services"/> instance, so calls can be chained.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="services"/> is <see langword="null"/>.</exception>
    public static IServiceCollection AddSearchApplication(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);

        var applicationAssembly = typeof(DependencyInjection).Assembly;

        services.AddMediatR(cfg => cfg.RegisterServicesFromAssembly(applicationAssembly));

        // MediatR nests the behaviours in registration order, so the first one registered is the
        // outermost. Validation goes first: an invalid request is rejected before anything else
        // runs, and the handler can assume every request it receives is already valid. Logging
        // sits inside it and therefore times the handler itself rather than the validation pass.
        services.AddTransient(typeof(IPipelineBehavior<,>), typeof(ValidationBehavior<,>));
        services.AddTransient(typeof(IPipelineBehavior<,>), typeof(LoggingBehavior<,>));

        // Registered as singletons rather than the default scoped lifetime: validators hold no
        // state, and a scoped registration would make the whole pipeline unresolvable from the
        // root provider, which is exactly how the background execution engine sends its commands.
        services.AddValidatorsFromAssemblyContaining<StartSearchCommandValidator>(ServiceLifetime.Singleton);

        // TryAdd so a host or a test can substitute a deterministic clock by registering its own
        // IClock before this call.
        services.TryAddSingleton<IClock, SystemClock>();

        return services;
    }
}
