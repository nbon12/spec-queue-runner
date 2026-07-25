using SpecRunner.Adapters;
using SpecRunner.Adapters.GitHub;
using SpecRunner.Configuration;
using SpecRunner.Ticking;

namespace SpecRunner.Cli;

/// <summary>The launchd entry point: run one tick from a config file (FR-001).</summary>
public static class TickCommand
{
    public static async Task<int> RunAsync(string configPath)
    {
        if (!File.Exists(configPath))
        {
            Console.Error.WriteLine($"config not found: {configPath}");
            return (int)ExitCode.ConfigInvalid;
        }

        InstanceConfig config;
        try
        {
            config = ConfigLoader.Load(configPath);
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"config parse failed: {ex.Message}");
            return (int)ExitCode.ConfigInvalid;
        }

        var validation = ConfigValidation.Validate(config);
        if (!validation.Ok)
        {
            Console.Error.WriteLine("config invalid:");
            foreach (var error in validation.Errors)
            {
                Console.Error.WriteLine($"  - {error}");
            }

            return (int)ExitCode.ConfigInvalid;
        }

        // Secret: the PAT from its mounted file (FR-052), or GH_TOKEN as the demo fallback.
        var token = File.Exists(config.GitHubPatFile)
            ? (await File.ReadAllTextAsync(config.GitHubPatFile).ConfigureAwait(false)).Trim()
            : Environment.GetEnvironmentVariable("GH_TOKEN");
        if (string.IsNullOrWhiteSpace(token))
        {
            Console.Error.WriteLine("no GitHub token (mounted secret missing and GH_TOKEN unset).");
            return (int)ExitCode.EnvironmentFailure;
        }

        var github = new GitHubClient(config.Slug, token);
        var processes = new ProcessRunner();
        var tick = new Tick(config, github, processes, Console.Out);
        return await tick.RunAsync().ConfigureAwait(false);
    }
}
