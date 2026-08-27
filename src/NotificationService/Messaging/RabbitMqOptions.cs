using System.ComponentModel.DataAnnotations;

namespace NotificationService.Messaging;

/// <summary>
/// Connection settings for the RabbitMQ broker this worker consumes completion events from.
/// Bound from the <c>RabbitMq</c> configuration section.
/// </summary>
/// <remarks>
/// The Search Service declares its own equivalent options type rather than sharing this one.
/// The duplication is deliberate: the two services are independently deployable, and a shared
/// configuration class would force them to version and ship together.
/// </remarks>
public sealed class RabbitMqOptions
{
    /// <summary>
    /// Name of the configuration section these options are bound from.
    /// </summary>
    public const string SectionName = "RabbitMq";

    /// <summary>
    /// Gets or sets the broker host name. Container deployments override this with the
    /// compose service name; the default suits a local broker.
    /// </summary>
    [Required(AllowEmptyStrings = false)]
    public string Host { get; set; } = "localhost";

    /// <summary>
    /// Gets or sets the AMQP port the broker listens on.
    /// </summary>
    [Range(1, 65535)]
    public int Port { get; set; } = 5672;

    /// <summary>
    /// Gets or sets the virtual host to connect to.
    /// </summary>
    [Required(AllowEmptyStrings = false)]
    public string VirtualHost { get; set; } = "/";

    /// <summary>
    /// Gets or sets the user name used to authenticate against the broker.
    /// </summary>
    [Required(AllowEmptyStrings = false)]
    public string UserName { get; set; } = "guest";

    /// <summary>
    /// Gets or sets the password used to authenticate against the broker. Supply it through
    /// an environment variable or a secret store in any real deployment.
    /// </summary>
    [Required(AllowEmptyStrings = false)]
    public string Password { get; set; } = "guest";

    /// <summary>
    /// Gets or sets how many times opening the connection is attempted before the worker
    /// gives up. The broker is usually still booting when this service starts, so the first
    /// few attempts are expected to fail.
    /// </summary>
    [Range(1, 100)]
    public int MaxConnectRetries { get; set; } = 10;

    /// <summary>
    /// Gets or sets the delay inserted between two consecutive connection attempts.
    /// </summary>
    public TimeSpan RetryDelay { get; set; } = TimeSpan.FromSeconds(2);
}
