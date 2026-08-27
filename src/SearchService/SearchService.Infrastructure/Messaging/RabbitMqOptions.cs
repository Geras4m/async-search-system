using System.ComponentModel.DataAnnotations;

namespace SearchService.Infrastructure.Messaging;

/// <summary>
/// Connection settings for the RabbitMQ broker, bound from the <c>RabbitMq</c>
/// configuration section.
/// </summary>
/// <remarks>
/// <para>
/// The defaults describe a broker running on the developer machine. Under Docker Compose
/// the host is overridden to the compose service name, either through
/// <c>appsettings.json</c> or through the <c>RabbitMq__Host</c> environment variable.
/// </para>
/// <para>
/// The retry settings exist because of start-up ordering: Compose starts the broker and the
/// services together, and RabbitMQ needs several seconds before it accepts connections. The
/// service retries rather than crash-looping, so a cold <c>docker compose up</c> converges
/// on its own.
/// </para>
/// </remarks>
public sealed class RabbitMqOptions
{
    /// <summary>Name of the configuration section these options bind to.</summary>
    public const string SectionName = "RabbitMq";

    /// <summary>Broker host name.</summary>
    [Required(AllowEmptyStrings = false)]
    public string Host { get; set; } = "localhost";

    /// <summary>Broker AMQP port.</summary>
    [Range(1, 65535)]
    public int Port { get; set; } = 5672;

    /// <summary>Virtual host to connect to.</summary>
    [Required(AllowEmptyStrings = false)]
    public string VirtualHost { get; set; } = "/";

    /// <summary>User name used to authenticate against the broker.</summary>
    [Required(AllowEmptyStrings = false)]
    public string UserName { get; set; } = "guest";

    /// <summary>Password used to authenticate against the broker.</summary>
    [Required(AllowEmptyStrings = false)]
    public string Password { get; set; } = "guest";

    /// <summary>Maximum number of connection attempts before the connection is reported as failed.</summary>
    [Range(1, 100)]
    public int MaxConnectRetries { get; set; } = 10;

    /// <summary>Delay between connection attempts.</summary>
    [Required]
    public TimeSpan RetryDelay { get; set; } = TimeSpan.FromSeconds(2);
}
