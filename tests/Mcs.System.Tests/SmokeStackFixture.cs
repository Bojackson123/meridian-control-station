namespace Mcs.System.Tests;

/// <summary>
/// The clients every smoke test talks through, and the one wait this suite is allowed to do.
/// </summary>
/// <remarks>
/// One <see cref="HttpClient"/> per origin for the whole suite rather than one per test: these are
/// pooled connections to a container, and a fresh client per test spends the run opening sockets
/// and leaves them in TIME_WAIT afterwards.
/// </remarks>
public sealed class SmokeStackFixture : IAsyncLifetime
{
    /// <summary>How long an origin gets to start answering before the suite stops waiting.</summary>
    private static readonly TimeSpan StartupBudget = TimeSpan.FromSeconds(30);

    private static readonly TimeSpan PollInterval = TimeSpan.FromMilliseconds(250);

    /// <summary>Bound on one probe. Short: these answer in milliseconds or not at all.</summary>
    private static readonly TimeSpan ProbeTimeout = TimeSpan.FromSeconds(5);

    public SmokeStackFixture()
    {
        Api = ClientFor(SmokeStack.ApiOrigin);
        Web = ClientFor(SmokeStack.WebOrigin);
    }

    /// <summary>The API container directly, on its published port -- no proxy in the way.</summary>
    public HttpClient Api { get; }

    /// <summary>nginx: the console's own origin, and the <c>/api</c> proxy in front of the API.</summary>
    public HttpClient Web { get; }

    /// <summary>
    /// Gives each origin a bounded chance to finish starting, then hands over regardless.
    /// </summary>
    /// <remarks>
    /// <b>This is not a retry.</b> It reads like one and is the opposite of one: no assertion in
    /// this suite is ever re-run, because a smoke test that passes on the third attempt is one
    /// people learn to re-run instead of read. This waits on a precondition -- containers finishing
    /// their startup -- once, before any assertion has been made, and never again.
    /// <para>
    /// <b>It cannot fail the run, and that is deliberate.</b> An earlier version threw here when an
    /// origin never answered, which turned a stopped API into eight identical failures naming the
    /// fixture: the console assertions went red alongside the API ones even though nginx was
    /// serving perfectly, and the suite lost exactly the discrimination that makes it worth
    /// running. Which origins are up is a finding, not a precondition, so each test is left to
    /// discover it against the origin it is actually about.
    /// </para>
    /// <para>
    /// The two origins are waited on together rather than in turn, so a stack that is half up costs
    /// one budget rather than two.
    /// </para>
    /// <para>
    /// The skip check in front of them is load-bearing, not an optimisation. xunit builds a
    /// collection fixture before it consults any test's <see cref="FactAttribute.Skip"/>, so
    /// without it a solution-wide <c>dotnet test</c> with no stack running spends the whole budget
    /// here and then skips all eight anyway -- thirty seconds of every inner-loop run, buying
    /// nothing. That is the exact cost <see cref="SmokeFactAttribute"/> exists to avoid.
    /// </para>
    /// </remarks>
    public Task InitializeAsync() =>
        SmokeStack.SkipReason is not null
            ? Task.CompletedTask
            : Task.WhenAll(
                WaitForAsync(Api, Routes.Liveness),
                WaitForAsync(Web, Routes.WebRoot));

    public Task DisposeAsync()
    {
        Api.Dispose();
        Web.Dispose();

        return Task.CompletedTask;
    }

    /// <summary>
    /// A client with no timeout of its own, because every caller brings a deadline instead.
    /// </summary>
    /// <remarks>
    /// <see cref="HttpClient.Timeout"/> looks like the right place for this and is a trap for a
    /// suite that reads a stream. It does not stop at the response headers: the timer keeps running
    /// under <see cref="HttpCompletionOption.ResponseHeadersRead"/> and tears the body out from
    /// under a stream read when it expires, so an SSE test would die of an unrelated clock at
    /// whatever the client was set to, reporting a cancellation instead of the assertion's own
    /// message. One mechanism -- a <see cref="CancellationTokenSource"/> at each call site, where
    /// the bound is visible next to what it bounds -- rather than two that race.
    /// </remarks>
    private static HttpClient ClientFor(Uri origin) =>
        new() { BaseAddress = origin, Timeout = Timeout.InfiniteTimeSpan };

    private static async Task WaitForAsync(HttpClient client, string path)
    {
        using CancellationTokenSource budget = new(StartupBudget);

        while (!budget.IsCancellationRequested)
        {
            try
            {
                using CancellationTokenSource probe =
                    CancellationTokenSource.CreateLinkedTokenSource(budget.Token);

                probe.CancelAfter(ProbeTimeout);

                using HttpResponseMessage response = await client.GetAsync(path, probe.Token);

                if (response.IsSuccessStatusCode)
                {
                    return;
                }
            }
            catch (HttpRequestException)
            {
                //  Nothing listening yet. Expected for as long as a container is starting, and the
                //  budget is what decides when it stops being expected.
            }
            catch (OperationCanceledException)
            {
                //  Either this probe's timeout or the budget's; the loop condition tells them
                //  apart on the next pass.
            }

            try
            {
                await Task.Delay(PollInterval, budget.Token);
            }
            catch (OperationCanceledException)
            {
                return;
            }
        }
    }
}

/// <summary>
/// Puts every smoke test in one collection, so they share the fixture and run one after another.
/// </summary>
/// <remarks>
/// Sequential is the point as much as sharing is: these run against one stack with one fake vehicle
/// on it, and eight tests opening streams at once would tell us about the runner's scheduling
/// rather than the station's behaviour. At 1 Hz the whole suite still lands well inside its budget.
/// </remarks>
[CollectionDefinition(Name)]
public sealed class SmokeCollection : ICollectionFixture<SmokeStackFixture>
{
    public const string Name = "compose stack";
}
