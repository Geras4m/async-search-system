using Xunit;

namespace IntegrationTests.Fixtures;

/// <summary>
/// A <see cref="FactAttribute"/> that skips the test, with an explicit reason, when no Docker
/// daemon is reachable. Tests marked with it need the RabbitMQ container the suite starts; on a
/// machine without a container runtime they are reported as skipped rather than as failures.
/// </summary>
[AttributeUsage(AttributeTargets.Method, AllowMultiple = false)]
public sealed class DockerFactAttribute : FactAttribute
{
    /// <summary>
    /// Initializes a new instance of the <see cref="DockerFactAttribute"/> class, resolving the
    /// skip reason from the one-off daemon probe.
    /// </summary>
    public DockerFactAttribute()
    {
        if (!DockerAvailability.IsAvailable)
        {
            Skip = DockerAvailability.SkipReason;
        }
    }
}
