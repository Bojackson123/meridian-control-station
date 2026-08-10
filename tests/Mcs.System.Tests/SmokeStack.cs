namespace Mcs.System.Tests;

/// <summary>
/// Where the running stack is, and whether there is one at all.
/// </summary>
/// <remarks>
/// The origins are environment variables with localhost defaults rather than anything read out of
/// <c>.env</c>: that file is git-ignored and generated, so parsing it would make the suite depend
/// on a file CI never writes, and Compose is already the only thing that consumes it.
/// <para>
/// The defaults match <c>MCS_WEB_PORT</c> and <c>MCS_API_PORT</c> in <c>.env.example</c>. If a host
/// port is changed there because something else already holds 8080, the same value has to be given
/// here -- nothing else in the repo can agree on it for us.
/// </para>
/// </remarks>
internal static class SmokeStack
{
    /// <summary>Origin the console is served from -- nginx, which also proxies <c>/api</c>.</summary>
    public const string WebOriginVariable = "MCS_SMOKE_BASE_URL";

    /// <summary>Origin of the API container itself, bypassing the proxy.</summary>
    public const string ApiOriginVariable = "MCS_SMOKE_API_URL";

    /// <summary>Set to <c>1</c> to turn "no stack running" from a skip into a failure.</summary>
    public const string RequiredVariable = "MCS_SMOKE_REQUIRED";

    /// <summary>
    /// How long the discovery-time probe waits before deciding nothing is listening.
    /// </summary>
    /// <remarks>
    /// Short on purpose. This runs during test discovery on every <c>dotnet test</c> in the repo,
    /// including the ones aimed at other projects, and a discovery step that pauses for several
    /// seconds is one people work around.
    /// </remarks>
    private static readonly TimeSpan ProbeTimeout = TimeSpan.FromSeconds(2);

    /// <summary>
    /// Whether a stack answered, evaluated once per discovery process: null if one did, otherwise
    /// the reason to skip.
    /// </summary>
    private static readonly Lazy<string?> Reachability =
        new(Probe, LazyThreadSafetyMode.ExecutionAndPublication);

    public static Uri WebOrigin { get; } = OriginFrom(WebOriginVariable, "http://localhost:8080");

    public static Uri ApiOrigin { get; } = OriginFrom(ApiOriginVariable, "http://localhost:8081");

    /// <summary>
    /// Whether a missing stack is a failure rather than a skip. CI sets this.
    /// </summary>
    /// <remarks>
    /// A smoke suite that silently skips in CI is worse than no smoke suite, because it reports
    /// green for a run that asserted nothing. Only <c>1</c> and <c>true</c> count, rather than any
    /// non-empty value -- <c>MCS_SMOKE_REQUIRED=0</c> should mean what it looks like it means.
    /// </remarks>
    public static bool IsRequired { get; } =
        Environment.GetEnvironmentVariable(RequiredVariable) is string value
        && (value.Equals("1", StringComparison.Ordinal)
            || value.Equals("true", StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// Why these tests should not run, or null to run them.
    /// </summary>
    /// <remarks>
    /// Under <see cref="IsRequired"/> this is always null, so the tests run and the fixture is left
    /// to fail loudly against the stack that is not there.
    /// </remarks>
    public static string? SkipReason => IsRequired ? null : Reachability.Value;

    /// <summary>
    /// Asks the API for liveness once, synchronously, and reports whether anyone answered.
    /// </summary>
    /// <remarks>
    /// Deliberately only the API. This decides "is there a stack at all?", not "is every container
    /// healthy?" -- a half-started stack should go red through the assertions, which say which part
    /// of it is missing, rather than disappear into a skip that says nothing.
    /// <para>
    /// Blocking, because it is called from an attribute constructor during discovery; there is no
    /// async surface there to await into.
    /// </para>
    /// </remarks>
    private static string? Probe()
    {
        using HttpClient client = new() { BaseAddress = ApiOrigin, Timeout = ProbeTimeout };

        try
        {
            using HttpResponseMessage response =
                client.GetAsync(Routes.Liveness).GetAwaiter().GetResult();

            return response.IsSuccessStatusCode
                ? null
                : $"no stack: {ApiOrigin}{Routes.Liveness.TrimStart('/')} answered "
                    + $"{(int)response.StatusCode}. Bring the stack up with `docker compose "
                    + $"--env-file .env -f deploy/compose/compose.yaml up -d --wait`, or set "
                    + $"{RequiredVariable}=1 to make this a failure instead of a skip.";
        }
        catch (Exception exception) when (exception is HttpRequestException or TaskCanceledException)
        {
            //  Both shapes mean the same thing here: nothing is listening (connection refused) or
            //  nothing answered in two seconds. Neither is worth telling apart in the message.
            return $"no stack listening on {ApiOrigin}. Bring it up with `docker compose "
                + $"--env-file .env -f deploy/compose/compose.yaml up -d --wait`, or set "
                + $"{RequiredVariable}=1 to make this a failure instead of a skip.";
        }
    }

    private static Uri OriginFrom(string variable, string fallback)
    {
        string configured = Environment.GetEnvironmentVariable(variable) ?? fallback;

        //  The scheme is checked separately because TryCreate alone does not reject the mistake
        //  this guard is for: 'localhost:8081' parses as absolute, with 'localhost' taken for the
        //  scheme and '8081' for the path. Left to HttpClient it surfaces during discovery as a
        //  NotSupportedException naming neither the variable nor what was wrong with it.
        return Uri.TryCreate(configured, UriKind.Absolute, out Uri? origin)
            && (origin.Scheme == Uri.UriSchemeHttp || origin.Scheme == Uri.UriSchemeHttps)
            ? origin
            : throw new InvalidOperationException(
                $"{variable} is set to '{configured}', which is not an http or https origin. "
                + $"Expected something like '{fallback}'.");
    }
}
