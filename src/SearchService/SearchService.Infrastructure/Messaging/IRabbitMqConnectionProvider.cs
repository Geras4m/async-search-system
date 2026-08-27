using RabbitMQ.Client;

namespace SearchService.Infrastructure.Messaging;

/// <summary>
/// Owns the process-wide connection to the RabbitMQ broker.
/// </summary>
/// <remarks>
/// <para>
/// An AMQP connection is an expensive, long-lived TCP resource that is designed to be shared
/// and multiplexed by lightweight channels. Handing that single connection out through an
/// abstraction keeps its lifetime, its retry policy and its disposal in one place, and keeps
/// publishers from each opening one of their own.
/// </para>
/// <para>
/// Implementations are expected to be safe for concurrent use and to connect lazily, so that
/// an unreachable broker delays the first publish rather than preventing the service from
/// starting and serving gRPC traffic.
/// </para>
/// </remarks>
public interface IRabbitMqConnectionProvider : IAsyncDisposable
{
    /// <summary>
    /// Returns the shared broker connection, opening it on first use.
    /// </summary>
    /// <param name="cancellationToken">Token used to cancel the connection attempt.</param>
    /// <returns>An open connection to the broker.</returns>
    /// <exception cref="OperationCanceledException">The operation was cancelled.</exception>
    /// <exception cref="ObjectDisposedException">The provider has already been disposed.</exception>
    Task<IConnection> GetConnectionAsync(CancellationToken cancellationToken);
}
