namespace SearchService.Domain.Exceptions;

/// <summary>
/// Thrown when an operation targets a search identifier that does not exist.
/// </summary>
public sealed class SearchNotFoundException : Exception
{
    /// <summary>
    /// Initializes a new instance of the <see cref="SearchNotFoundException"/> class.
    /// </summary>
    /// <param name="searchId">The identifier that could not be resolved.</param>
    public SearchNotFoundException(Guid searchId)
        : base($"Search '{searchId}' was not found.")
    {
        SearchId = searchId;
    }

    /// <summary>
    /// Initializes a new instance of the <see cref="SearchNotFoundException"/> class.
    /// </summary>
    public SearchNotFoundException()
        : base("The requested search was not found.")
    {
    }

    /// <summary>
    /// Initializes a new instance of the <see cref="SearchNotFoundException"/> class.
    /// </summary>
    /// <param name="message">The message that describes the error.</param>
    public SearchNotFoundException(string message)
        : base(message)
    {
    }

    /// <summary>
    /// Initializes a new instance of the <see cref="SearchNotFoundException"/> class.
    /// </summary>
    /// <param name="message">The message that describes the error.</param>
    /// <param name="innerException">The exception that caused this one.</param>
    public SearchNotFoundException(string message, Exception innerException)
        : base(message, innerException)
    {
    }

    /// <summary>The identifier that could not be resolved, when known.</summary>
    public Guid SearchId { get; }
}
