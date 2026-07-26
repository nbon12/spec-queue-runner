using SpecRunner.Configuration;
using SpecRunner.Ports;

namespace SpecRunner.Ticking;

/// <summary>
/// The long-lived loop that replaces per-tick containers (constitution §2, v5.0.0): sleep, run
/// one tick, discard everything, repeat. It exists because an ephemeral container cannot host a
/// live session — tmux dies with the container — and cannot be inspected while running.
///
/// It is deliberately thin, because §3 now makes statelessness a discipline rather than a
/// consequence of the process exiting:
///
///   * <b>Nothing is carried between iterations.</b> Every collaborator is constructed inside
///     the loop body from the factory, so a supervisor that has looped for a month behaves
///     exactly like one started a second ago. There are no fields holding queue state.
///   * <b>One faulted iteration never wedges the loop.</b> An unhandled fault is logged and the
///     next iteration proceeds; the tick is convergent, so the failed unit of work is simply
///     reconciled later.
///   * <b>It is killable at any moment.</b> Cancellation is observed while sleeping and passed
///     into the tick; nothing needs unwinding because nothing is held.
/// </summary>
public sealed class Supervisor(
    InstanceConfig config,
    Func<CancellationToken, Task<int>> runTick,
    TextWriter log,
    Func<TimeSpan, CancellationToken, Task>? sleep = null)
{
    private readonly Func<TimeSpan, CancellationToken, Task> _sleep =
        sleep ?? Task.Delay;

    /// <summary>
    /// Loops until cancelled. <paramref name="maxIterations"/> bounds the loop for tests; in
    /// production it is null and the loop ends only on cancellation.
    /// </summary>
    public async Task<int> RunAsync(int? maxIterations = null, CancellationToken ct = default)
    {
        var interval = TimeSpan.FromSeconds(Math.Max(1, config.TickInterval));
        log.WriteLine($"supervisor: starting for {config.Slug}; tick every {interval.TotalSeconds:0}s.");

        var iteration = 0;
        while (!ct.IsCancellationRequested && (maxIterations is null || iteration < maxIterations))
        {
            iteration++;
            try
            {
                // Construct-and-discard: the tick reads every fact it acts on from GitHub and the
                // filesystem at the start of THIS iteration (§3, v5.0.0).
                var exitCode = await runTick(ct).ConfigureAwait(false);
                if (exitCode != (int)Cli.ExitCode.Ok)
                {
                    log.WriteLine($"supervisor: iteration {iteration} reported exit {exitCode}.");
                }
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                break; // shutting down, not a fault
            }
            catch (Exception ex)
            {
                // Never let one bad iteration end the supervisor (§3). The tick is convergent, so
                // whatever was interrupted is reconciled by a later iteration.
                log.WriteLine($"supervisor: iteration {iteration} faulted — {ex.GetType().Name}: {ex.Message}");
            }

            if (maxIterations is not null && iteration >= maxIterations)
            {
                break;
            }

            try
            {
                await _sleep(interval, ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                break;
            }
        }

        log.WriteLine($"supervisor: stopped after {iteration} iteration(s).");
        return (int)Cli.ExitCode.Ok;
    }
}
