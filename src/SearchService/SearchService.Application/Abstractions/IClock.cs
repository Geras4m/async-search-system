namespace SearchService.Application.Abstractions;

/// <summary>
/// Abstraction over the system clock so time-dependent behaviour stays testable.
/// </summary>
public interface IClock
{
    /// <summary>Current UTC time.</summary>
    DateTime UtcNow { get; }
}
