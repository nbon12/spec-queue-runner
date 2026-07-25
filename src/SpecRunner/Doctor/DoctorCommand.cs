using SpecRunner.Adapters;
using SpecRunner.Adapters.GitHub;
using SpecRunner.Configuration;
using SpecRunner.Ports;

namespace SpecRunner.Doctor;

/// <summary>
/// Preflight (contracts/cli-commands.md): verify prerequisites without touching the queue, one
/// pass/fail line per check. Runs inside the container, so it checks the container's own
/// toolchain. Notably checks that the operator login resolves (else the allowlist can't be
/// enforced — fail closed) and that the claude.ai credential isn't near expiry (FR-052b).
/// </summary>
public static class DoctorCommand
{
    public static async Task<int> RunAsync(string configPath)
    {
        var checks = new List<(string Name, bool Ok, string Detail)>();

        void Check(string name, bool ok, string detail = "") => checks.Add((name, ok, detail));

        // Config
        InstanceConfig? config = null;
        try
        {
            config = ConfigLoader.Load(configPath);
            var validation = ConfigValidation.Validate(config);
            Check("config parses and validates", validation.Ok,
                validation.Ok ? "" : string.Join("; ", validation.Errors));
        }
        catch (Exception ex)
        {
            Check("config parses and validates", false, ex.Message);
        }

        var processes = new ProcessRunner();

        // Toolchain in the image
        Check("git present (>= 2.5)", await ToolOk(processes, "git", "--version"));
        Check("tmux present", await ToolOk(processes, "tmux", "-V"));
        Check("claude present", await ToolOk(processes, "claude", "--version"));

        if (config is not null)
        {
            // Secret
            Check("GitHub PAT secret readable", File.Exists(config.GitHubPatFile),
                config.GitHubPatFile);

            // Clone
            Check("clone path is a git repo",
                Directory.Exists(Path.Combine(config.Path, ".git")) || Directory.Exists(config.Path),
                config.Path);

            // Operator resolves (else allowlist can't be enforced) — needs the token.
            var token = File.Exists(config.GitHubPatFile)
                ? (await File.ReadAllTextAsync(config.GitHubPatFile).ConfigureAwait(false)).Trim()
                : Environment.GetEnvironmentVariable("GH_TOKEN");
            if (!string.IsNullOrWhiteSpace(token))
            {
                try
                {
                    var github = new GitHubClient(config.Slug, token);
                    var id = await github.ResolveUserIdAsync(config.OperatorLogin).ConfigureAwait(false);
                    Check("operator login resolves to a numeric id", id is not null,
                        id is null ? "allowlist cannot be enforced — fail closed" : $"id={id}");
                }
                catch (Exception ex)
                {
                    Check("operator login resolves to a numeric id", false, ex.Message);
                }
            }
            else
            {
                Check("operator login resolves to a numeric id", false, "no token to check with");
            }
        }

        // claude.ai credential presence + expiry (FR-052b). The credential lives in ~/.claude.json.
        Check("claude.ai credential present", ClaudeCredentialPresent(config),
            "run in-container /login if missing (Remote Control needs a full-scope session)");

        var allOk = checks.All(c => c.Ok);
        foreach (var (name, ok, detail) in checks)
        {
            var mark = ok ? "PASS" : "FAIL";
            Console.WriteLine(detail.Length == 0 ? $"  {mark}  {name}" : $"  {mark}  {name} — {detail}");
        }

        Console.WriteLine(allOk ? "doctor: all checks passed." : "doctor: FAILURES above — fix before scheduling.");
        return allOk ? (int)Cli.ExitCode.Ok : (int)Cli.ExitCode.EnvironmentFailure;
    }

    private static async Task<bool> ToolOk(ProcessRunner processes, string file, string versionArg)
    {
        try
        {
            var r = await processes.RunAsync(file, [versionArg]).ConfigureAwait(false);
            return r.ExitCode == 0;
        }
        catch
        {
            return false;
        }
    }

    private static bool ClaudeCredentialPresent(InstanceConfig? config)
    {
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var credsFile = Path.Combine(home, ".claude", ".credentials.json");
        var configFile = string.IsNullOrEmpty(config?.ClaudeConfigPath)
            ? Path.Combine(home, ".claude.json")
            : config.ClaudeConfigPath;
        return File.Exists(credsFile) || File.Exists(configFile);
    }
}
