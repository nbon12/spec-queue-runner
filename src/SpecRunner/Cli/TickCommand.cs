using SpecRunner.Adapters;
using SpecRunner.Adapters.GitHub;
using SpecRunner.Logging;
using SpecRunner.Ticking;

namespace SpecRunner.Cli;

/// <summary>
/// Run exactly one tick and exit. The supervisor (`serve`) is what runs continuously in
/// production; this stays supported for diagnosis, which the constitution requires (§2, v5.0.0) —
/// being able to run a single tick by hand is how you tell a stuck loop from a stuck item.
/// </summary>
public static class TickCommand
{
    public static async Task<int> RunAsync(string configPath)
    {
        var (config, token, failure) = await InstanceStartup.LoadAsync(configPath).ConfigureAwait(false);
        if (failure is not null)
        {
            return failure.Value;
        }

        var github = new GitHubClient(config!.Slug, token!);
        var processes = new ProcessRunner();
        var tick = new Tick(config, github, processes, TickLog.For(config, Console.Out));
        return await tick.RunAsync().ConfigureAwait(false);
    }
}
