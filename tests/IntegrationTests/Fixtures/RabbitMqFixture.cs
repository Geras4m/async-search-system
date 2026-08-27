using System.Globalization;
using Testcontainers.RabbitMq;
using Xunit;

namespace IntegrationTests.Fixtures;

/// <summary>
/// Starts one ephemeral RabbitMQ broker for the whole suite and exposes the endpoint it was
/// published on.
/// </summary>
/// <remarks>
/// <para>
/// The broker is real on purpose. Messaging is the half of the system a mock cannot prove: the
/// exchange type, the binding, the JSON payload and the acknowledgement handshake are all
/// broker behaviour, and only a broker can confirm the publisher and the consumer agree.
/// </para>
/// <para>
/// The fixture never throws. When no Docker daemon answers, or the container fails to start, it
/// records the reason and reports itself unavailable. Tests that need messaging are skipped by
/// <see cref="DockerFactAttribute"/> when there is no daemon at all, and fail with the recorded
/// reason through <see cref="RequireEndpoint"/> when there was one but it could not produce a
/// broker. Tests that need no messaging, such as the API Gateway error handling tests, keep
/// running against a placeholder endpoint that is deliberately unreachable, so they can never
/// silently talk to some other broker that happens to be running on this machine.
/// </para>
/// </remarks>
public sealed class RabbitMqFixture : IAsyncLifetime
{
    /// <summary>Image the suite pins. Alpine keeps the pull small; the tag keeps runs repeatable.</summary>
    private const string BrokerImage = "rabbitmq:3.13-management-alpine";

    /// <summary>AMQP port inside the container. The host port is assigned by Docker.</summary>
    private const int AmqpPort = 5672;

    /// <summary>
    /// Port of the placeholder endpoint. Nothing listens on it, which is the point: a host
    /// configured with it can start, and can never reach a broker by accident.
    /// </summary>
    private const int UnreachablePort = 1;

    /// <summary>Credentials the container is created with.</summary>
    private const string BrokerUserName = "guest";

    /// <summary>Credentials the container is created with.</summary>
    private const string BrokerPassword = "guest";

    private RabbitMqContainer? _container;

    /// <summary>
    /// Gets a value indicating whether a broker is actually running and may be used.
    /// </summary>
    public bool IsAvailable { get; private set; }

    /// <summary>
    /// Gets the reason the broker is unavailable, or an empty string when it is available.
    /// </summary>
    public string SkipReason { get; private set; } = string.Empty;

    /// <summary>
    /// Gets the endpoint services should be pointed at. Until the container has started this is
    /// a placeholder that is never reachable, which keeps the hosts constructible for the tests
    /// that do not need messaging.
    /// </summary>
    public BrokerEndpoint Endpoint { get; private set; } =
        new("localhost", UnreachablePort, BrokerUserName, BrokerPassword);

    /// <summary>
    /// Returns the endpoint of a broker that is genuinely running.
    /// </summary>
    /// <returns>The endpoint of the running broker.</returns>
    /// <exception cref="InvalidOperationException">
    /// No broker is running. The message carries the reason recorded when the container was
    /// started, so a test that needs one fails with that reason rather than with a bare
    /// connection refusal against the placeholder endpoint.
    /// </exception>
    /// <remarks>
    /// <see cref="DockerFactAttribute"/> already skips these tests when no daemon answers at all.
    /// This covers the narrower case of a daemon that answered but could not give us a broker,
    /// which is a genuine failure rather than a reason to skip.
    /// </remarks>
    public BrokerEndpoint RequireEndpoint() =>
        IsAvailable
            ? Endpoint
            : throw new InvalidOperationException(
                SkipReason.Length == 0 ? "The broker container was never started." : SkipReason);

    /// <summary>
    /// Takes the broker down, so a test can observe what the services do during an outage.
    /// </summary>
    /// <param name="cancellationToken">Token that abandons the operation.</param>
    /// <returns>A task that completes once the broker has stopped accepting connections.</returns>
    /// <exception cref="InvalidOperationException">
    /// No broker is running, or the command failed inside the container.
    /// </exception>
    /// <remarks>
    /// <para>
    /// The broker application is stopped inside the container rather than the container itself
    /// being stopped, and that distinction matters. Testcontainers publishes the AMQP port on a
    /// host port chosen by the daemon, and stopping and starting the container makes the daemon
    /// choose again: the container comes back on a different host port. Every service already
    /// configured with the old endpoint would then be pointed at nothing, which is a different
    /// failure from the one under test and one no amount of retrying can recover from.
    /// </para>
    /// <para>
    /// Stopping the application closes the AMQP listener and drops every open connection while
    /// the container, and therefore the published port, stays exactly where it was. Clients see
    /// connections refused, which is what a broker outage looks like from the outside, and the
    /// durable topology survives in the node's own storage ready for
    /// <see cref="StartBrokerAsync"/>.
    /// </para>
    /// <para>
    /// Callers must restore the broker before finishing, including on failure: the container is
    /// shared by the whole suite.
    /// </para>
    /// </remarks>
    public Task StopBrokerAsync(CancellationToken cancellationToken = default) =>
        ControlBrokerAsync("stop_app", cancellationToken);

    /// <summary>
    /// Brings the broker back up after <see cref="StopBrokerAsync"/>.
    /// </summary>
    /// <param name="cancellationToken">Token that abandons the operation.</param>
    /// <returns>A task that completes once the broker accepts connections again.</returns>
    /// <exception cref="InvalidOperationException">
    /// No broker is running, or the command failed inside the container.
    /// </exception>
    /// <remarks>
    /// The endpoint is unchanged, and durable exchanges, queues, bindings and the persistent
    /// messages they hold are all still there.
    /// </remarks>
    public Task StartBrokerAsync(CancellationToken cancellationToken = default) =>
        ControlBrokerAsync("start_app", cancellationToken);

    /// <summary>
    /// Starts the broker container, or records why it could not be started.
    /// </summary>
    /// <returns>A task that completes once the broker is ready or has been given up on.</returns>
    public async Task InitializeAsync()
    {
        if (!DockerAvailability.IsAvailable)
        {
            SkipReason = DockerAvailability.SkipReason;
            return;
        }

        var container = new RabbitMqBuilder(BrokerImage)
            .WithUsername(BrokerUserName)
            .WithPassword(BrokerPassword)
            .Build();

        try
        {
            await container.StartAsync();

            _container = container;

            Endpoint = new BrokerEndpoint(
                container.Hostname,
                container.GetMappedPublicPort(AmqpPort),
                BrokerUserName,
                BrokerPassword);

            IsAvailable = true;
        }
        catch (Exception exception)
        {
            // Reported, not rethrown: the suite still has plenty to say about the parts of the
            // system that need no broker, and the tests that do need one report this reason.
            SkipReason =
                $"The RabbitMQ container ({BrokerImage}) could not be started: "
                + $"{exception.GetType().Name}: {exception.Message}";

            await container.DisposeAsync();
        }
    }

    /// <summary>
    /// Stops and removes the broker container.
    /// </summary>
    /// <returns>A task that completes once the container has been removed.</returns>
    public async Task DisposeAsync()
    {
        if (_container is not null)
        {
            await _container.DisposeAsync();
            _container = null;
        }
    }

    /// <summary>
    /// Runs one <c>rabbitmqctl</c> subcommand inside the broker container.
    /// </summary>
    /// <param name="command">Subcommand to run.</param>
    /// <param name="cancellationToken">Token that abandons the operation.</param>
    /// <returns>A task that completes once the command has finished.</returns>
    /// <exception cref="InvalidOperationException">
    /// No broker is running, or the command reported a non-zero exit code.
    /// </exception>
    private async Task ControlBrokerAsync(string command, CancellationToken cancellationToken)
    {
        if (_container is null)
        {
            throw new InvalidOperationException(
                SkipReason.Length == 0 ? "The broker container was never started." : SkipReason);
        }

        var result = await _container.ExecAsync(["rabbitmqctl", command], cancellationToken);

        if (result.ExitCode != 0)
        {
            throw new InvalidOperationException(string.Create(
                CultureInfo.InvariantCulture,
                $"'rabbitmqctl {command}' failed inside the broker container with exit code {result.ExitCode}: {result.Stderr}"));
        }
    }
}
