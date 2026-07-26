using SpecRunner.Configuration;
using SpecRunner.Ticking;
using Xunit;

namespace SpecRunner.UnitTests;

/// <summary>
/// The supervisor's job is to loop without becoming stateful (constitution §3, v5.0.0). These
/// pin the invariants that the removed process boundary used to guarantee for free.
/// </summary>
public class SupervisorTests
{
    private static InstanceConfig Config(int interval = 300) => new()
    {
        Slug = "op/repo", Path = "/clone", WorktreesRoot = "/work",
        OperatorLogin = "operator", TickInterval = interval,
        GitHubPatFile = "/run/secrets/pat",
    };

    // No real waiting in tests: the sleep is a seam.
    private static Func<TimeSpan, CancellationToken, Task> NoSleep => (_, _) => Task.CompletedTask;

    [Fact]
    public async Task Runs_a_tick_each_iteration()
    {
        var runs = 0;
        var sup = new Supervisor(Config(), _ => { runs++; return Task.FromResult(0); },
            TextWriter.Null, NoSleep);

        await sup.RunAsync(maxIterations: 3);

        Assert.Equal(3, runs);
    }

    [Fact]
    public async Task A_faulted_iteration_does_not_end_the_loop()
    {
        // The core of "one bad iteration never wedges the supervisor" (§3).
        var runs = 0;
        var sup = new Supervisor(Config(), _ =>
        {
            runs++;
            if (runs == 2)
            {
                throw new InvalidOperationException("boom");
            }

            return Task.FromResult(0);
        }, TextWriter.Null, NoSleep);

        await sup.RunAsync(maxIterations: 4);

        Assert.Equal(4, runs); // kept going past the fault
    }

    [Fact]
    public async Task A_faulted_iteration_is_reported_not_swallowed()
    {
        var log = new StringWriter();
        var sup = new Supervisor(Config(), _ => throw new InvalidOperationException("boom"),
            log, NoSleep);

        await sup.RunAsync(maxIterations: 1);

        Assert.Contains("faulted", log.ToString(), StringComparison.Ordinal);
        Assert.Contains("boom", log.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_non_ok_exit_code_is_reported()
    {
        var log = new StringWriter();
        var sup = new Supervisor(Config(), _ => Task.FromResult(2), log, NoSleep);

        await sup.RunAsync(maxIterations: 1);

        Assert.Contains("exit 2", log.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Cancellation_stops_the_loop_promptly()
    {
        using var cts = new CancellationTokenSource();
        var runs = 0;
        var sup = new Supervisor(Config(), _ =>
        {
            runs++;
            cts.Cancel(); // operator runs `docker stop` during the first iteration
            return Task.FromResult(0);
        }, TextWriter.Null, NoSleep);

        await sup.RunAsync(ct: cts.Token);

        Assert.Equal(1, runs);
    }

    [Fact]
    public async Task Cancellation_during_a_tick_is_not_reported_as_a_fault()
    {
        // Shutting down mid-work is normal (`docker stop`), not an error to alarm about.
        using var cts = new CancellationTokenSource();
        var log = new StringWriter();
        var sup = new Supervisor(Config(), ct =>
        {
            cts.Cancel();
            ct.ThrowIfCancellationRequested();
            return Task.FromResult(0);
        }, log, NoSleep);

        await sup.RunAsync(ct: cts.Token);

        Assert.DoesNotContain("faulted", log.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Sleeps_the_configured_interval_between_iterations()
    {
        var slept = new List<TimeSpan>();
        var sup = new Supervisor(Config(interval: 42), _ => Task.FromResult(0), TextWriter.Null,
            (d, _) => { slept.Add(d); return Task.CompletedTask; });

        await sup.RunAsync(maxIterations: 2);

        // Sleeps between iterations, not after the last one.
        Assert.Single(slept);
        Assert.Equal(TimeSpan.FromSeconds(42), slept[0]);
    }

    [Fact]
    public async Task Carries_nothing_between_iterations()
    {
        // §3: a supervisor that has looped for a month must behave like one just started. The
        // structural guarantee is that the tick factory is re-invoked every iteration and the
        // supervisor exposes no state to it.
        var seen = new List<int>();
        var counter = 0;
        var sup = new Supervisor(Config(), _ =>
        {
            seen.Add(++counter);
            return Task.FromResult(0);
        }, TextWriter.Null, NoSleep);

        await sup.RunAsync(maxIterations: 3);

        Assert.Equal([1, 2, 3], seen); // one fresh invocation per iteration, none skipped or reused
    }
}
