using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using RabbitMQ.Client;

namespace SearchService.Infrastructure.Messaging;

/// <summary>
/// Opens and owns the single shared <see cref="IConnection"/> used by this service.
/// </summary>
/// <remarks>
/// <para>
/// <b>Lazy, not eager.</b> The connection is opened on first use rather than at start-up, so
/// a broker that is unavailable delays the first published event instead of preventing the
/// Search Service from starting. Searches keep running and gRPC keeps serving either way.
/// </para>
/// <para>
/// <b>Retry.</b> Under Docker Compose the broker and the services start together, and
/// RabbitMQ needs several seconds before it accepts connections. The connection is therefore
/// attempted up to <c>MaxConnectRetries</c> times, spaced by <c>RetryDelay</c>, with each
/// failure logged at warning level and the eventual success at information level. The last
/// failure is rethrown so the caller still sees a real error instead of a silent no-op.
/// </para>
/// <para>
/// <b>Why a semaphore and not a lock.</b> Opening a connection is asynchronous, and
/// <see langword="await"/> is not allowed inside a <see langword="lock"/>. A
/// <see cref="SemaphoreSlim"/> gives the same "only one initialiser at a time" guarantee
/// while letting the losing callers yield their thread instead of blocking it. Callers that
/// arrive once the connection is already open take a lock-free fast path and never touch the
/// semaphore at all.
/// </para>
/// </remarks>
public sealed partial class RabbitMqConnectionProvider : IRabbitMqConnectionProvider
{
    private readonly SemaphoreSlim _connectionGate = new(1, 1);
    private readonly RabbitMqOptions _options;
    private readonly ILogger<RabbitMqConnectionProvider> _logger;

    private IConnection? _connection;
    private bool _disposed;

    /// <summary>
    /// Initializes a new instance of the <see cref="RabbitMqConnectionProvider"/> class.
    /// </summary>
    /// <param name="options">Broker connection and retry settings.</param>
    /// <param name="logger">Logger for connection attempts and outcomes.</param>
    /// <exception cref="ArgumentNullException">Any argument is <see langword="null"/>.</exception>
    public RabbitMqConnectionProvider(
        IOptions<RabbitMqOptions> options,
        ILogger<RabbitMqConnectionProvider> logger)
    {
        ArgumentNullException.ThrowIfNull(options);

        _options = options.Value;
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    /// <summary>
    /// Returns the shared broker connection, opening it on first use.
    /// </summary>
    /// <param name="cancellationToken">Token used to cancel the connection attempt.</param>
    /// <returns>An open connection to the broker.</returns>
    /// <exception cref="OperationCanceledException">The operation was cancelled.</exception>
    /// <exception cref="ObjectDisposedException">The provider has already been disposed.</exception>
    /// <remarks>
    /// A connection that is no longer open is discarded and replaced, so a broker restart
    /// that outlives the client's own automatic recovery heals on the next publish.
    /// </remarks>
    public async Task<IConnection> GetConnectionAsync(CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        IConnection? existing = _connection;

        if (existing is { IsOpen: true })
        {
            return existing;
        }

        await _connectionGate.WaitAsync(cancellationToken).ConfigureAwait(false);

        try
        {
            ObjectDisposedException.ThrowIf(_disposed, this);

            if (_connection is { IsOpen: true })
            {
                return _connection;
            }

            await DiscardConnectionAsync().ConfigureAwait(false);

            _connection = await ConnectWithRetryAsync(cancellationToken).ConfigureAwait(false);

            return _connection;
        }
        finally
        {
            _connectionGate.Release();
        }
    }

    /// <summary>
    /// Closes the shared connection and releases the resources owned by the provider.
    /// </summary>
    /// <returns>A task that completes once the connection has been closed.</returns>
    public async ValueTask DisposeAsync()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;

        await DiscardConnectionAsync().ConfigureAwait(false);

        _connectionGate.Dispose();
    }

    /// <summary>
    /// Attempts to connect, retrying while the broker refuses connections.
    /// </summary>
    /// <param name="cancellationToken">Token used to cancel the connection attempt.</param>
    /// <returns>An open connection to the broker.</returns>
    private async Task<IConnection> ConnectWithRetryAsync(CancellationToken cancellationToken)
    {
        ConnectionFactory factory = new()
        {
            HostName = _options.Host,
            Port = _options.Port,
            UserName = _options.UserName,
            Password = _options.Password,
            VirtualHost = _options.VirtualHost,
            ClientProvidedName = "search-service",
        };

        int maxAttempts = _options.MaxConnectRetries;

        for (int attempt = 1; ; attempt++)
        {
            try
            {
                IConnection connection = await factory.CreateConnectionAsync(cancellationToken).ConfigureAwait(false);

                LogConnected(_options.Host, _options.Port, _options.VirtualHost, attempt);

                return connection;
            }
            catch (Exception ex) when (ex is not OperationCanceledException && attempt < maxAttempts)
            {
                LogConnectAttemptFailed(ex, _options.Host, _options.Port, attempt, maxAttempts, _options.RetryDelay);
            }

            // Only reached when the attempt failed and another one is left: a successful
            // attempt returns, and the final failure is rethrown by the filter above.
            await Task.Delay(_options.RetryDelay, cancellationToken).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Closes and forgets the current connection, if there is one.
    /// </summary>
    /// <returns>A task that completes once the connection has been released.</returns>
    private async ValueTask DiscardConnectionAsync()
    {
        IConnection? connection = _connection;

        if (connection is null)
        {
            return;
        }

        _connection = null;

        try
        {
            await connection.DisposeAsync().ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // A broker that has already gone away often faults on close. The connection is
            // being thrown away regardless, so this must not surface to the caller.
            LogConnectionCloseFailed(ex);
        }
    }

    [LoggerMessage(
        EventId = 3000,
        Level = LogLevel.Information,
        Message = "Connected to RabbitMQ. Host={Host} Port={Port} VirtualHost={VirtualHost} Attempt={Attempt}")]
    private partial void LogConnected(string host, int port, string virtualHost, int attempt);

    [LoggerMessage(
        EventId = 3001,
        Level = LogLevel.Warning,
        Message = "RabbitMQ connection attempt failed, retrying. Host={Host} Port={Port} Attempt={Attempt} MaxAttempts={MaxAttempts} RetryDelay={RetryDelay}")]
    private partial void LogConnectAttemptFailed(
        Exception exception,
        string host,
        int port,
        int attempt,
        int maxAttempts,
        TimeSpan retryDelay);

    [LoggerMessage(
        EventId = 3002,
        Level = LogLevel.Debug,
        Message = "Ignored a failure while closing a RabbitMQ connection.")]
    private partial void LogConnectionCloseFailed(Exception exception);
}
