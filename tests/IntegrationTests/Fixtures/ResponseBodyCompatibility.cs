using System.IO.Pipelines;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.Extensions.DependencyInjection;

namespace IntegrationTests.Fixtures;

/// <summary>
/// Makes the in-memory test server's response body writer report the number of buffered bytes it
/// is holding.
/// </summary>
/// <remarks>
/// <para>
/// <b>Why this exists.</b> The services target net8.0, but this machine has no .NET 8 runtime
/// installed, so the suite runs them on a newer shared framework through roll-forward. The
/// framework's JSON response writer serializes straight into the response's
/// <see cref="PipeWriter"/> and asks it how many bytes are still unflushed, while the in-memory
/// test server shipped for net8.0 predates that question and answers that it cannot tell. Every
/// JSON response written through the test server would fail with
/// <c>"does not implement PipeWriter.UnflushedBytes"</c> before a single assertion could run.
/// </para>
/// <para>
/// <b>What it does not do.</b> Nothing here changes the application. The writer below forwards
/// every buffer, flush and completion straight to the test server's own writer and only keeps a
/// count of the bytes written since the last flush, which is exactly the bookkeeping the newer
/// writers do for themselves. The bytes on the wire, and therefore everything the tests assert
/// on, are the application's own.
/// </para>
/// </remarks>
internal static class ResponseBodyCompatibility
{
    /// <summary>
    /// Registers the compatibility shim at the head of a host's request pipeline.
    /// </summary>
    /// <param name="services">Service collection of the host under test.</param>
    /// <returns>The same collection, so calls can be chained.</returns>
    public static IServiceCollection AddResponseBodyCompatibility(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);

        return services.AddSingleton<IStartupFilter, ResponseBodyCompatibilityStartupFilter>();
    }

    /// <summary>
    /// Inserts the shim before every middleware the application registers, so the response body
    /// writer is already compatible by the time anything writes to it.
    /// </summary>
    private sealed class ResponseBodyCompatibilityStartupFilter : IStartupFilter
    {
        /// <summary>
        /// Wraps the application's pipeline.
        /// </summary>
        /// <param name="next">Configures the rest of the pipeline.</param>
        /// <returns>A configuration action that installs the shim first.</returns>
        public Action<IApplicationBuilder> Configure(Action<IApplicationBuilder> next)
        {
            ArgumentNullException.ThrowIfNull(next);

            return builder =>
            {
                builder.Use(async (context, continuation) =>
                {
                    var responseBody = context.Features.Get<IHttpResponseBodyFeature>();

                    if (responseBody is null)
                    {
                        await continuation(context);
                        return;
                    }

                    context.Features.Set<IHttpResponseBodyFeature>(
                        new CountingResponseBodyFeature(responseBody));

                    try
                    {
                        await continuation(context);
                    }
                    finally
                    {
                        context.Features.Set(responseBody);
                    }
                });

                next(builder);
            };
        }
    }

    /// <summary>
    /// Delegates every part of the response body feature to the server's own implementation,
    /// substituting only the writer.
    /// </summary>
    /// <param name="inner">The server's response body feature.</param>
    private sealed class CountingResponseBodyFeature(IHttpResponseBodyFeature inner) : IHttpResponseBodyFeature
    {
        private readonly PipeWriter _writer = new CountingPipeWriter(inner.Writer);

        /// <inheritdoc />
        public Stream Stream => inner.Stream;

        /// <inheritdoc />
        public PipeWriter Writer => _writer;

        /// <inheritdoc />
        public void DisableBuffering() => inner.DisableBuffering();

        /// <inheritdoc />
        public Task StartAsync(CancellationToken cancellationToken = default) =>
            inner.StartAsync(cancellationToken);

        /// <inheritdoc />
        public Task SendFileAsync(string path, long offset, long? count, CancellationToken cancellationToken = default) =>
            inner.SendFileAsync(path, offset, count, cancellationToken);

        /// <inheritdoc />
        public Task CompleteAsync() => inner.CompleteAsync();
    }

    /// <summary>
    /// A pass-through <see cref="PipeWriter"/> that tracks how many bytes have been written since
    /// the last flush.
    /// </summary>
    /// <param name="inner">The writer every call is forwarded to.</param>
    private sealed class CountingPipeWriter(PipeWriter inner) : PipeWriter
    {
        private long _unflushedBytes;

        /// <inheritdoc />
        public override bool CanGetUnflushedBytes => true;

        /// <inheritdoc />
        public override long UnflushedBytes => _unflushedBytes;

        /// <inheritdoc />
        public override void Advance(int bytes)
        {
            _unflushedBytes += bytes;

            inner.Advance(bytes);
        }

        /// <inheritdoc />
        public override Memory<byte> GetMemory(int sizeHint = 0) => inner.GetMemory(sizeHint);

        /// <inheritdoc />
        public override Span<byte> GetSpan(int sizeHint = 0) => inner.GetSpan(sizeHint);

        /// <inheritdoc />
        public override ValueTask<FlushResult> FlushAsync(CancellationToken cancellationToken = default)
        {
            _unflushedBytes = 0;

            return inner.FlushAsync(cancellationToken);
        }

        /// <inheritdoc />
        public override void CancelPendingFlush() => inner.CancelPendingFlush();

        /// <inheritdoc />
        public override void Complete(Exception? exception = null)
        {
            _unflushedBytes = 0;

            inner.Complete(exception);
        }

        /// <inheritdoc />
        public override ValueTask CompleteAsync(Exception? exception = null)
        {
            _unflushedBytes = 0;

            return inner.CompleteAsync(exception);
        }
    }
}
