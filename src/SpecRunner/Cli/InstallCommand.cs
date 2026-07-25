using SpecRunner.Configuration;
using SpecRunner.Doctor;

namespace SpecRunner.Cli;

/// <summary>
/// Generates the launchd plist that fires this instance's tick via `docker run` (FR-052a, T106).
/// Prints to stdout by default, or writes to <c>~/Library/LaunchAgents</c> with <c>--write</c> so
/// the operator can `launchctl load` it. Config is the single source of slug, interval, and PAT
/// path — the plist is derived, never hand-edited.
/// </summary>
public static class InstallCommand
{
    public static int Run(string configPath, string image, bool write)
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

        var slugKey = config.Slug.Replace('/', '-');
        var plist = LaunchdInstaller.Plist(
            slug: config.Slug,
            image: image,
            configPathInContainer: "/etc/spec-runner/config.toml",
            hostConfigPath: System.IO.Path.GetFullPath(configPath),
            patSecretHostPath: System.IO.Path.GetFullPath(config.GitHubPatFile),
            claudeVolume: $"sr-{slugKey}-claude",
            worktreesVolume: $"sr-{slugKey}-work",
            intervalSeconds: config.TickInterval);

        if (!write)
        {
            Console.WriteLine(plist);
            return (int)ExitCode.Ok;
        }

        var label = "com.spec-runner." + config.Slug.Replace('/', '.');
        var target = System.IO.Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            "Library", "LaunchAgents", $"{label}.plist");
        Directory.CreateDirectory(System.IO.Path.GetDirectoryName(target)!);
        File.WriteAllText(target, plist);
        Console.WriteLine($"wrote {target}");
        Console.WriteLine($"load it with:  launchctl load {target}");
        return (int)ExitCode.Ok;
    }
}
