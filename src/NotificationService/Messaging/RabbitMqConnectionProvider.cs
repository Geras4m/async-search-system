using System.Net.Sockets;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using RabbitMQ.Client;
using RabbitMQ.Client.Exceptions;

namespace NotificationService.Messaging;

/// <summary>
/// Default <see cref="IRabbitMqConnectionProvider"/>: opens one connection lazily, retries
/// while the broker is still starting, and hands the same instance to every caller.
/// </summary>
/// <param name="options">Broker connection settings.</param>
/// <param name="logger">Logger used to report connection attempts and failures.</param>
public sealed partial class RabbitMqConnectionProvider(
    IOptions<RabbitMqOptions> options,
    ILogger<RabbitMqConnectionProvider> logger) : IRabbitMqConnectionProvider
{
    private readonly RabbitMqOptions _options = options.Value;

    /// <summary>
    /// Serialises lazy initialisation. A <see cref="SemaphoreSlim"/> rather than a lock so the
    /// wait itself can be awaited: opening a connection is an I/O operation, and blocking a
    /// thread pool thread on it would defeat the point of an asynchronous worker.
    /// </summary>
    private readonly SemaphoreSlim _connectionGate = new(1, 1);

    private IConnection? _connection;
    private bool _disposed;

    /// <inheritdoc />
    public async ValueTask<IConnection> GetConnectionAsync(CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        // Fast path: by far the most common case is an already open connection.
        if (_connection is { IsOpen: true } established)
        {
            return established;
        }

        await _connectionGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ObjectDisposedException.ThrowIf(_disposed, this);

            if (_connection is { IsOpen: true } current)
            {
                return current;
            }

            if (_connection is not null)
            {
                // A previously opened connection that has since dropped. Release it before
                // replacing it so its socket and heartbeat timer do not leak.
                await _connection.DisposeAsync().ConfigureAwait(false);
                _connection = null;
            }

            _connection = await ConnectWithRetriesAsync(cancellationToken).ConfigureAwait(false);
            return _connection;
        }
        finally
        {
            _connectionGate.Release();
        }
    }

    /// <summary>
    /// Releases the shared connection.
    /// </summary>
    /// <returns>A task that completes once the connection has been closed.</returns>
    public async ValueTask DisposeAsync()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;

        await _connectionGate.WaitAsync().ConfigureAwait(false);
        try
        {
            if (_connection is not null)
            {
                await _connection.DisposeAsync().ConfigureAwait(false);
                _connection = null;
                LogConnectionClosed(logger);
            }
        }
        finally
        {
            _connectionGate.Release();
            _connectionGate.Dispose();
        }
    }

    /// <summary>
    /// Attempts to open a connection, backing off between attempts.
    /// </summary>
    /// <param name="cancellationToken">Token used to abandon the attempts.</param>
    /// <returns>The freshly opened connection.</returns>
    private async Task<IConnection> ConnectWithRetriesAsync(CancellationToken cancellationToken)
    {
        var factory = new ConnectionFactory
        {
            HostName = _options.Host,
            Port = _options.Port,
            UserName = _options.UserName,
            Password = _options.Password,
            VirtualHost = _options.VirtualHost,
            ClientProvidedName = "notification-service",
        };

        var maxAttempts = _options.MaxConnectRetries;

        // Unbounded loop shape on purpose: every exit is either a successful return or the
        // final attempt's exception escaping the filter, so there is no unreachable tail.
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                var connection = await factory.CreateConnectionAsync(cancellationToken).ConfigureAwait(false);
                LogConnectionEstablished(logger, _options.Host, _options.Port, attempt);
                return connection;
            }
            catch (Exception ex) when (attempt < maxAttempts && ex is BrokerUnreachableException or SocketException)
            {
                // Expected while the broker container is still booting: compose starts every
                // service at once, so the first few attempts routinely fail.
                LogConnectionAttemptFailed(
                    logger,
                    ex,
                    _options.Host,
                    _options.Port,
                    attempt,
                    maxAttempts,
                    _options.RetryDelay);

                await Task.Delay(_options.RetryDelay, cancellationToken).ConfigureAwait(false);
            }
        }
    }

    [LoggerMessage(
        EventId = 1000,
        Level = LogLevel.Information,
        Message = "RabbitMQ connection established. Host={Host} Port={Port} Attempt={Attempt}")]
    private static partial void LogConnectionEstablished(ILogger logger, string host, int port, int attempt);

    [LoggerMessage(
        EventId = 1001,
        Level = LogLevel.Warning,
        Message = "RabbitMQ connection attempt failed. Host={Host} Port={Port} Attempt={Attempt} MaxAttempts={MaxAttempts} RetryDelay={RetryDelay}")]
    private static partial void LogConnectionAttemptFailed(
        ILogger logger,
        Exception exception,
        string host,
        int port,
        int attempt,
        int maxAttempts,
        TimeSpan retryDelay);

    [LoggerMessage(
        EventId = 1002,
        Level = LogLevel.Information,
        Message = "RabbitMQ connection closed.")]
    private static partial void LogConnectionClosed(ILogger logger);
}
