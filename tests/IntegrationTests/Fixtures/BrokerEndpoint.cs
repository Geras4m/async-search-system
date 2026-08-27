using System.Globalization;

namespace IntegrationTests.Fixtures;

/// <summary>
/// Everything a service needs to reach the broker the suite runs against.
/// </summary>
/// <param name="Host">Host name the broker is reachable on.</param>
/// <param name="Port">Mapped AMQP port. Never 5672 when a container is used: Testcontainers
/// publishes the container port on a random free host port so parallel runs cannot collide.</param>
/// <param name="UserName">User name to authenticate with.</param>
/// <param name="Password">Password to authenticate with.</param>
public sealed record BrokerEndpoint(string Host, int Port, string UserName, string Password)
{
    /// <summary>
    /// Gets the port as configuration carries it, so the value can be fed straight into an
    /// in-memory configuration source.
    /// </summary>
    public string PortValue => Port.ToString(CultureInfo.InvariantCulture);

    /// <summary>Returns a display form that never contains the password.</summary>
    /// <returns>Host and port of the broker.</returns>
    public string Describe() => string.Create(CultureInfo.InvariantCulture, $"{Host}:{Port}");
}
