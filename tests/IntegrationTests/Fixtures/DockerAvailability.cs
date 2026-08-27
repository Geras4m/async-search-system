using System.IO.Pipes;
using System.Net.Sockets;

namespace IntegrationTests.Fixtures;

/// <summary>
/// Detects, exactly once per test run, whether a Docker daemon is reachable from this machine.
/// The integration suite needs a real broker; where none can be started the affected tests are
/// skipped with an explicit reason instead of failing, so the suite stays usable on a machine
/// that has no container runtime installed.
/// </summary>
internal static class DockerAvailability
{
    /// <summary>How long the probe waits for the daemon endpoint to accept a connection.</summary>
    private static readonly TimeSpan ProbeTimeout = TimeSpan.FromSeconds(2);

    /// <summary>Endpoint Docker Desktop exposes on Windows when DOCKER_HOST is not set.</summary>
    private const string WindowsPipeName = "docker_engine";

    /// <summary>Endpoint the daemon exposes on Linux and macOS when DOCKER_HOST is not set.</summary>
    private const string UnixSocketPath = "/var/run/docker.sock";

    private static readonly Lazy<ProbeResult> Probe =
        new(Detect, LazyThreadSafetyMode.ExecutionAndPublication);

    /// <summary>Gets a value indicating whether a Docker daemon answered the probe.</summary>
    public static bool IsAvailable => Probe.Value.IsAvailable;

    /// <summary>
    /// Gets the reason to report when Docker is not available. Empty when it is.
    /// </summary>
    public static string SkipReason => Probe.Value.Reason;

    /// <summary>
    /// Probes the daemon endpoint. Only the transport is checked: if the endpoint accepts a
    /// connection the fixture goes on to start a container, and a failure there is reported with
    /// the broker's own message rather than being guessed at here.
    /// </summary>
    /// <returns>The outcome of the probe.</returns>
    private static ProbeResult Detect()
    {
        var dockerHost = Environment.GetEnvironmentVariable("DOCKER_HOST");

        try
        {
            if (!string.IsNullOrWhiteSpace(dockerHost))
            {
                return ProbeConfiguredHost(dockerHost);
            }

            if (OperatingSystem.IsWindows())
            {
                ProbeNamedPipe(WindowsPipeName);
                return ProbeResult.Available;
            }

            ProbeUnixSocket(UnixSocketPath);
            return ProbeResult.Available;
        }
        catch (Exception exception) when (exception is IOException
                                              or SocketException
                                              or TimeoutException
                                              or UnauthorizedAccessException
                                              or PlatformNotSupportedException
                                              or UriFormatException
                                              or NotSupportedException)
        {
            var endpoint = string.IsNullOrWhiteSpace(dockerHost) ? "the default endpoint" : dockerHost;

            return ProbeResult.Unavailable(
                $"Docker is required for this test but no daemon answered on {endpoint} "
                + $"({exception.GetType().Name}: {exception.Message}). "
                + "Start Docker Desktop, or the Docker service, and run the suite again.");
        }
    }

    /// <summary>
    /// Probes the endpoint named by the <c>DOCKER_HOST</c> environment variable.
    /// </summary>
    /// <param name="dockerHost">Value of <c>DOCKER_HOST</c>.</param>
    /// <returns>The outcome of the probe.</returns>
    private static ProbeResult ProbeConfiguredHost(string dockerHost)
    {
        var endpoint = new Uri(dockerHost);

        switch (endpoint.Scheme)
        {
            case "npipe":
                ProbeNamedPipe(endpoint.Segments[^1].Trim('/'));
                return ProbeResult.Available;

            case "unix":
                ProbeUnixSocket(endpoint.LocalPath);
                return ProbeResult.Available;

            case "tcp":
            case "http":
            case "https":
                ProbeTcp(endpoint);
                return ProbeResult.Available;

            default:
                return ProbeResult.Unavailable(
                    $"Docker is required for this test but the DOCKER_HOST endpoint '{dockerHost}' "
                    + "uses a scheme this probe does not understand.");
        }
    }

    /// <summary>Connects to a named pipe, throwing when the daemon is not listening.</summary>
    /// <param name="pipeName">Name of the pipe to connect to.</param>
    private static void ProbeNamedPipe(string pipeName)
    {
        using var pipe = new NamedPipeClientStream(".", pipeName, PipeDirection.InOut);

        pipe.Connect((int)ProbeTimeout.TotalMilliseconds);
    }

    /// <summary>Connects to a Unix domain socket, throwing when the daemon is not listening.</summary>
    /// <param name="socketPath">Path of the socket to connect to.</param>
    private static void ProbeUnixSocket(string socketPath)
    {
        if (!File.Exists(socketPath))
        {
            throw new FileNotFoundException("The Docker socket does not exist.", socketPath);
        }

        using var socket = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);

        socket.Connect(new UnixDomainSocketEndPoint(socketPath));
    }

    /// <summary>Connects to a TCP endpoint, throwing when the daemon is not listening.</summary>
    /// <param name="endpoint">Endpoint to connect to.</param>
    private static void ProbeTcp(Uri endpoint)
    {
        using var client = new TcpClient();

        client.Connect(endpoint.Host, endpoint.Port);
    }

    /// <summary>Outcome of the one-off probe.</summary>
    /// <param name="IsAvailable">Whether a daemon answered.</param>
    /// <param name="Reason">Why it did not, when it did not.</param>
    private sealed record ProbeResult(bool IsAvailable, string Reason)
    {
        /// <summary>A successful probe.</summary>
        public static ProbeResult Available { get; } = new(true, string.Empty);

        /// <summary>Builds a failed probe carrying an explicit, actionable reason.</summary>
        /// <param name="reason">Why Docker cannot be used.</param>
        /// <returns>The failed outcome.</returns>
        public static ProbeResult Unavailable(string reason) => new(false, reason);
    }
}
