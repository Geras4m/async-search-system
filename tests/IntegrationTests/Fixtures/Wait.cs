using System.Diagnostics;
using System.Globalization;

namespace IntegrationTests.Fixtures;

/// <summary>
/// Deadline-bounded polling.
/// </summary>
/// <remarks>
/// The system under test is asynchronous by design, so tests have to wait for state to change.
/// Sleeping for a fixed duration would be both slower than necessary and unreliable under load;
/// every wait here re-probes until the expectation holds, returns the instant it does, and on
/// expiry fails with the expectation, the elapsed time, the number of attempts and the last
/// value observed, so a timeout is a diagnosis rather than a mystery.
/// </remarks>
internal static class Wait
{
    /// <summary>Default gap between probes: short enough to observe a 200 ms batch interval.</summary>
    public static readonly TimeSpan DefaultPollInterval = TimeSpan.FromMilliseconds(25);

    /// <summary>
    /// Default budget. Generous on purpose: it exists to fail a stuck test, not to time one, and
    /// a compressed search finishes in about a second.
    /// </summary>
    public static readonly TimeSpan DefaultTimeout = TimeSpan.FromSeconds(30);

    /// <summary>
    /// Probes until the expectation holds or the budget runs out.
    /// </summary>
    /// <typeparam name="T">Type of the observed value.</typeparam>
    /// <param name="probe">Reads the current value.</param>
    /// <param name="isSatisfied">Decides whether the observed value meets the expectation.</param>
    /// <param name="expectation">What is being waited for, phrased for a failure message.</param>
    /// <param name="cancellationToken">Token that abandons the wait.</param>
    /// <param name="timeout">Budget for the wait. Defaults to <see cref="DefaultTimeout"/>.</param>
    /// <param name="pollInterval">Gap between probes. Defaults to <see cref="DefaultPollInterval"/>.</param>
    /// <returns>The first observed value that met the expectation.</returns>
    /// <exception cref="TimeoutException">The expectation did not hold within the budget.</exception>
    public static async Task<T> UntilAsync<T>(
        Func<CancellationToken, Task<T>> probe,
        Func<T, bool> isSatisfied,
        string expectation,
        CancellationToken cancellationToken,
        TimeSpan? timeout = null,
        TimeSpan? pollInterval = null)
    {
        var budget = timeout ?? DefaultTimeout;
        var interval = pollInterval ?? DefaultPollInterval;

        var elapsed = Stopwatch.StartNew();
        var attempts = 0;
        var lastObserved = "nothing";

        while (elapsed.Elapsed < budget)
        {
            attempts++;

            var current = await probe(cancellationToken);
            lastObserved = Convert.ToString(current, CultureInfo.InvariantCulture) ?? "null";

            if (isSatisfied(current))
            {
                return current;
            }

            await Task.Delay(interval, cancellationToken);
        }

        var seconds = elapsed.Elapsed.TotalSeconds.ToString("F1", CultureInfo.InvariantCulture);

        throw new TimeoutException(
            $"Timed out after {seconds}s and {attempts} attempts waiting for {expectation}. Last observed: {lastObserved}.");
    }

    /// <summary>
    /// Waits until a condition holds, without an observed value.
    /// </summary>
    /// <param name="condition">The condition to wait for.</param>
    /// <param name="expectation">What is being waited for, phrased for a failure message.</param>
    /// <param name="cancellationToken">Token that abandons the wait.</param>
    /// <param name="timeout">Budget for the wait. Defaults to <see cref="DefaultTimeout"/>.</param>
    /// <param name="pollInterval">Gap between probes. Defaults to <see cref="DefaultPollInterval"/>.</param>
    /// <returns>A task that completes as soon as the condition holds.</returns>
    /// <exception cref="TimeoutException">The condition did not hold within the budget.</exception>
    public static Task UntilAsync(
        Func<bool> condition,
        string expectation,
        CancellationToken cancellationToken,
        TimeSpan? timeout = null,
        TimeSpan? pollInterval = null) =>
        UntilAsync(
            _ => Task.FromResult(condition()),
            satisfied => satisfied,
            expectation,
            cancellationToken,
            timeout,
            pollInterval);
}
