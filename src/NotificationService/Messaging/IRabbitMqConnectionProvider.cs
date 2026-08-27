using RabbitMQ.Client;

namespace NotificationService.Messaging;

/// <summary>
/// Owns the single RabbitMQ connection shared by every consumer in this worker.
/// </summary>
/// <remarks>
/// AMQP connections are expensive and are designed to be long lived, with cheap channels
/// multiplexed over them. Hiding that behind an abstraction also keeps the consumers
/// testable: they depend on this interface, not on a concrete broker client.
/// </remarks>
public interface IRabbitMqConnectionProvider : IAsyncDisposable
{
    /// <summary>
    /// Gets the shared connection, opening it on first use and retrying while the broker
    /// is still unreachable.
    /// </summary>
    /// <param name="cancellationToken">Token used to abandon the connection attempts.</param>
    /// <returns>An open connection to the broker.</returns>
    /// <exception cref="ObjectDisposedException">The provider has already been disposed.</exception>
    /// <exception cref="OperationCanceledException">
    /// <paramref name="cancellationToken"/> was cancelled before a connection was opened.
    /// </exception>
    ValueTask<IConnection> GetConnectionAsync(CancellationToken cancellationToken = default);
}
