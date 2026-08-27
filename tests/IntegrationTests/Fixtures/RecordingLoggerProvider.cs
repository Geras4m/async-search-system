using System.Collections.Concurrent;
using Microsoft.Extensions.Logging;

namespace IntegrationTests.Fixtures;

/// <summary>
/// An <see cref="ILoggerProvider"/> that keeps every log record in memory so a test can assert on
/// what a service logged.
/// </summary>
/// <remarks>
/// The specification's last step is a logging requirement: the Notification Service must log the
/// identifier of the search it was told about. Asserting that means capturing the record itself,
/// both its rendered text and the structured state behind it, rather than scraping console
/// output.
/// </remarks>
internal sealed class RecordingLoggerProvider : ILoggerProvider
{
    private readonly ConcurrentQueue<LogRecord> _records = new();

    /// <summary>
    /// Takes a snapshot of everything logged so far.
    /// </summary>
    /// <returns>The records captured up to this moment.</returns>
    public IReadOnlyList<LogRecord> Snapshot() => [.. _records];

    /// <summary>
    /// Creates a logger that records into this provider.
    /// </summary>
    /// <param name="categoryName">Category the logger writes under.</param>
    /// <returns>The recording logger.</returns>
    public ILogger CreateLogger(string categoryName) => new RecordingLogger(categoryName, _records);

    /// <summary>Releases nothing: the records outlive the provider on purpose.</summary>
    public void Dispose()
    {
        // Nothing to release. The captured records stay readable after the host has stopped so a
        // failing assertion can still report what was logged.
    }

    /// <summary>Writes every record it is given into the shared queue.</summary>
    /// <param name="category">Category of the logger.</param>
    /// <param name="records">Queue shared with the provider.</param>
    private sealed class RecordingLogger(string category, ConcurrentQueue<LogRecord> records) : ILogger
    {
        /// <summary>Scopes are not captured; the tests assert on records, not on scopes.</summary>
        /// <typeparam name="TState">Type of the scope state.</typeparam>
        /// <param name="state">The scope state.</param>
        /// <returns>Always <see langword="null"/>.</returns>
        public IDisposable? BeginScope<TState>(TState state)
            where TState : notnull => null;

        /// <summary>Every level is captured so nothing a test might assert on is filtered out.</summary>
        /// <param name="logLevel">Level being tested.</param>
        /// <returns>Always <see langword="true"/>.</returns>
        public bool IsEnabled(LogLevel logLevel) => true;

        /// <summary>Captures one record.</summary>
        /// <typeparam name="TState">Type of the log state.</typeparam>
        /// <param name="logLevel">Level of the record.</param>
        /// <param name="eventId">Identifier of the record.</param>
        /// <param name="state">Structured state behind the message.</param>
        /// <param name="exception">Exception attached to the record, if any.</param>
        /// <param name="formatter">Renders the message.</param>
        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            ArgumentNullException.ThrowIfNull(formatter);

            var properties = new Dictionary<string, object?>(StringComparer.Ordinal);

            // Source-generated log methods pass their state as a read-only list of name/value
            // pairs, which is where the SearchId the specification asks for lives.
            if (state is IReadOnlyList<KeyValuePair<string, object?>> structuredState)
            {
                foreach (var property in structuredState)
                {
                    properties[property.Key] = property.Value;
                }
            }

            records.Enqueue(new LogRecord(
                category,
                logLevel,
                eventId,
                formatter(state, exception),
                properties,
                exception));
        }
    }
}

/// <summary>
/// One captured log record.
/// </summary>
/// <param name="Category">Category the record was written under.</param>
/// <param name="Level">Severity of the record.</param>
/// <param name="EventId">Identifier of the record.</param>
/// <param name="Message">The rendered message.</param>
/// <param name="State">The structured state behind the message template.</param>
/// <param name="Exception">The exception attached to the record, if any.</param>
internal sealed record LogRecord(
    string Category,
    LogLevel Level,
    EventId EventId,
    string Message,
    IReadOnlyDictionary<string, object?> State,
    Exception? Exception)
{
    /// <summary>
    /// Reads a structured property as text, so a test can compare it without caring whether the
    /// logging pipeline boxed a <see cref="Guid"/> or a string.
    /// </summary>
    /// <param name="name">Name of the property in the message template.</param>
    /// <returns>The property rendered as text, or <see langword="null"/> when absent.</returns>
    public string? Property(string name) =>
        State.TryGetValue(name, out var value) ? value?.ToString() : null;

    /// <summary>Renders the record for a failure message.</summary>
    /// <returns>A single line describing the record.</returns>
    public override string ToString() => $"[{Level}] {Category}: {Message}";
}
