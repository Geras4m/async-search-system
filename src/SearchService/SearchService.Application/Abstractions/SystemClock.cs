namespace SearchService.Application.Abstractions;

/// <summary>
/// Default <see cref="IClock"/> backed by <see cref="DateTime.UtcNow"/>.
/// </summary>
public sealed class SystemClock : IClock
{
    /// <inheritdoc />
    public DateTime UtcNow => DateTime.UtcNow;
}
