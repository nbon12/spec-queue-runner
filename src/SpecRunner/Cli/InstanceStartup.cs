using SpecRunner.Configuration;

namespace SpecRunner.Cli;

/// <summary>
/// Shared startup for the commands that act on an instance (`tick`, `serve`): parse config,
/// validate fail-fast (constitution §7 — refuse to start rather than fail mid-tick), and read the
/// mounted GitHub secret. Shared so a single tick and the supervisor cannot drift in how they
/// interpret the same config.
/// </summary>
public static class InstanceStartup
{
    /// <summary>
    /// Returns the loaded config and token, or a non-null exit code describing why startup failed.
    /// </summary>
    public static async Task<(InstanceConfig? Config, string? Token, int? Failure)> LoadAsync(string configPath)
    {
        if (!File.Exists(configPath))
        {
            Console.Error.WriteLine($"config not found: {configPath}");
            return (null, null, (int)ExitCode.ConfigInvalid);
        }

        InstanceConfig config;
        try
        {
            config = ConfigLoader.Load(configPath);
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"config parse failed: {ex.Message}");
            return (null, null, (int)ExitCode.ConfigInvalid);
        }

        var validation = ConfigValidation.Validate(config);
        if (!validation.Ok)
        {
            Console.Error.WriteLine("config invalid:");
            foreach (var error in validation.Errors)
            {
                Console.Error.WriteLine($"  - {error}");
            }

            return (null, null, (int)ExitCode.ConfigInvalid);
        }

        // Secret: the PAT from its mounted file (FR-052), or GH_TOKEN as the demo fallback.
        var token = File.Exists(config.GitHubPatFile)
            ? (await File.ReadAllTextAsync(config.GitHubPatFile).ConfigureAwait(false)).Trim()
            : Environment.GetEnvironmentVariable("GH_TOKEN");
        if (string.IsNullOrWhiteSpace(token))
        {
            Console.Error.WriteLine("no GitHub token (mounted secret missing and GH_TOKEN unset).");
            return (null, null, (int)ExitCode.EnvironmentFailure);
        }

        return (config, token, null);
    }
}
