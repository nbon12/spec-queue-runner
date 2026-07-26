using SpecRunner.Configuration;
using SpecRunner.Doctor;

namespace SpecRunner.Cli;

/// <summary>
/// Generates the launchd plist that fires this instance's tick via `docker run` (FR-052a, T106).
/// Prints to stdout; redirect it into <c>~/Library/LaunchAgents</c> and <c>launchctl bootstrap</c> it.
///
/// This command normally runs INSIDE the container, where host paths are invisible — a mounted
/// config resolves to its container path, not the host path launchd must use. So the host-side
/// facts are passed in explicitly via environment variables rather than guessed:
///
///   SPEC_RUNNER_HOST_CONFIG  host path of the instance config      (required)
///   SPEC_RUNNER_HOST_PAT     host path of the PAT secret file      (required)
///   SPEC_RUNNER_HOME_VOLUME  docker volume mounted at /home/runner (default: sr-&lt;owner-repo&gt;-home)
///   SPEC_RUNNER_HOST_HOME    host home directory, for the socket + log paths
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
        var hostConfig = Environment.GetEnvironmentVariable("SPEC_RUNNER_HOST_CONFIG");
        var hostPat = Environment.GetEnvironmentVariable("SPEC_RUNNER_HOST_PAT");
        if (string.IsNullOrWhiteSpace(hostConfig) || string.IsNullOrWhiteSpace(hostPat))
        {
            Console.Error.WriteLine(
                "SPEC_RUNNER_HOST_CONFIG and SPEC_RUNNER_HOST_PAT must be set to the HOST paths of\n" +
                "the config and PAT secret — launchd runs on the host, so container paths would\n" +
                "produce a plist that mounts the wrong files. See deploy/README.md.");
            return (int)ExitCode.ConfigInvalid;
        }

        var hostHome = Environment.GetEnvironmentVariable("SPEC_RUNNER_HOST_HOME") is { Length: > 0 } h
            ? h
            : System.IO.Path.GetDirectoryName(System.IO.Path.GetDirectoryName(hostPat)) ?? string.Empty;

        var homeVolume = Environment.GetEnvironmentVariable("SPEC_RUNNER_HOME_VOLUME") is { Length: > 0 } v
            ? v
            : $"sr-{slugKey}-home";

        var plist = LaunchdInstaller.Plist(
            slug: config.Slug,
            image: image,
            configPathInContainer: "/etc/spec-runner/config.toml",
            hostConfigPath: hostConfig,
            patSecretHostPath: hostPat,
            homeVolume: homeVolume,
            intervalSeconds: config.TickInterval,
            // launchd runs with a bare environment; Docker Desktop's socket must be explicit.
            dockerHost: hostHome.Length > 0
                ? $"unix://{hostHome}/.docker/run/docker.sock"
                : string.Empty,
            logPath: hostHome.Length > 0
                ? $"{hostHome}/.config/spec-runner/{slugKey}.scheduler.log"
                : string.Empty);

        if (!write)
        {
            Console.WriteLine(plist);
            return (int)ExitCode.Ok;
        }

        // --write only makes sense when running on the host itself (rare: the binary is
        // linux-arm64). In-container, redirect stdout instead.
        var label = "com.spec-runner." + config.Slug.Replace('/', '.');
        var target = System.IO.Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            "Library", "LaunchAgents", $"{label}.plist");
        Directory.CreateDirectory(System.IO.Path.GetDirectoryName(target)!);
        File.WriteAllText(target, plist);
        Console.WriteLine($"wrote {target}");
        Console.WriteLine($"load it with:  launchctl bootstrap gui/$(id -u) {target}");
        return (int)ExitCode.Ok;
    }
}
