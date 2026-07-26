using SpecRunner.Adapters;
using SpecRunner.Adapters.GitHub;
using SpecRunner.Configuration;
using SpecRunner.Logging;
using SpecRunner.Ticking;

namespace SpecRunner.Cli;

/// <summary>
/// The container entry point (constitution §2, v5.0.0): run the supervisor loop in the
/// foreground until the container is stopped. Foreground and unbuffered is deliberate — it is
/// what makes `docker logs -f` work, which the ephemeral model could not offer.
/// </summary>
public static class ServeCommand
{
    public static async Task<int> RunAsync(string configPath)
    {
        var (config, token, failure) = await InstanceStartup.LoadAsync(configPath).ConfigureAwait(false);
        if (failure is not null)
        {
            return failure.Value;
        }

        var log = TickLog.For(config!, Console.Out);

        // Stop cleanly on SIGTERM/SIGINT (`docker stop`) rather than being killed mid-iteration.
        using var cts = new CancellationTokenSource();
        Console.CancelKeyPress += (_, e) =>
        {
            e.Cancel = true;
            log.WriteLine("signal received — finishing the current iteration, then stopping.");
            cts.Cancel();
        };
        AppDomain.CurrentDomain.ProcessExit += (_, _) => cts.Cancel();

        var supervisor = new Supervisor(
            config!,
            // Construct every collaborator per iteration — nothing crosses the boundary (§3).
            runTick: ct =>
            {
                var github = new GitHubClient(config!.Slug, token!);
                var processes = new ProcessRunner();
                return new Tick(config, github, processes, log).RunAsync(ct);
            },
            log);

        return await supervisor.RunAsync(ct: cts.Token).ConfigureAwait(false);
    }
}
