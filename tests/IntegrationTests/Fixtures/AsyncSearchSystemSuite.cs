using Xunit;

namespace IntegrationTests.Fixtures;

/// <summary>
/// Groups every integration test class into one xUnit collection so they share a single broker
/// container and run one after another.
/// </summary>
/// <remarks>
/// Running them sequentially is deliberate. The assertions are about timing: batches have to be
/// observed arriving one after another inside a compressed interval, and several test classes
/// racing each other for the same CPU would make those observations flaky rather than
/// meaningful.
/// </remarks>
[CollectionDefinition(Name)]
public sealed class AsyncSearchSystemSuite : ICollectionFixture<RabbitMqFixture>
{
    /// <summary>Name test classes reference in their <c>[Collection]</c> attribute.</summary>
    public const string Name = "async-search-system";
}
