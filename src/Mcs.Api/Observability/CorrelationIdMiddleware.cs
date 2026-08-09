using Serilog.Context;

namespace Mcs.Api.Observability;

/// <summary>
/// Puts an inbound correlation ID onto every log line written while a request is being handled.
/// </summary>
/// <remarks>
/// Half of the correlation ID. The other half -- minting one when an operator issues a command, then
/// threading it through adapter, wire and simulator ack -- arrives with the command lifecycle. Until
/// then nothing here generates an ID: telemetry is not commanded, so stamping frames with one would
/// leave a request ID wearing the name of something more useful.
/// </remarks>
public sealed class CorrelationIdMiddleware
{
    /// <summary>Header carrying the ID inbound. The command path must send the same one.</summary>
    public const string HeaderName = "X-Correlation-Id";

    /// <summary>Log property the ID appears under.</summary>
    public const string PropertyName = "CorrelationId";

    private const int MaxLength = 64;

    private readonly RequestDelegate _next;

    public CorrelationIdMiddleware(RequestDelegate next) => _next = next;

    public async Task InvokeAsync(HttpContext context)
    {
        var correlationId = context.Request.Headers[HeaderName].ToString();

        if (!IsAcceptable(correlationId))
        {
            await _next(context);
            return;
        }

        using (LogContext.PushProperty(PropertyName, correlationId))
        {
            await _next(context);
        }
    }

    //  The value arrives from outside, so bound it: a caller does not get to choose how long a log
    //  line is, nor to put control characters in the middle of one.
    private static bool IsAcceptable(string value) =>
        value.Length is > 0 and <= MaxLength && !value.Any(char.IsControl);
}

public static class CorrelationIdMiddlewareExtensions
{
    /// <summary>
    /// Register before request logging, so the request-completed line carries the ID too.
    /// </summary>
    public static IApplicationBuilder UseCorrelationId(this IApplicationBuilder app) =>
        app.UseMiddleware<CorrelationIdMiddleware>();
}
