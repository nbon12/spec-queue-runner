using SpecRunner.Adapters;
using SpecRunner.Adapters.GitHub;
using SpecRunner.Configuration;
using SpecRunner.Domain;
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

        // claude.ai credential health (FR-052b). What matters is whether it is still REFRESHABLE:
        // the access token lives ~12h and Claude Code refreshes it into the mounted volume by
        // itself, so its expiry is routine. A missing refresh token is the real stall risk.
        var credential = ClaudeCredential.Parse(ReadClaudeCredential(config));
        Check("claude.ai credential refreshable", credential.Ok,
            credential.Describe(DateTimeOffset.UtcNow));

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

    /// <summary>
    /// The credential file's text, or null if absent. Its home is the directory holding Claude
    /// Code's config (<c>~/.claude.json</c> by default) — the same home the projects folder and
    /// the mounted volume share. Deliberately does NOT fall back to <c>.claude.json</c> as
    /// evidence of a credential: that file holds trust settings and is almost always present, so
    /// accepting it would report PASS with no credential at all.
    /// </summary>
    private static string? ReadClaudeCredential(InstanceConfig? config)
    {
        var configFile = string.IsNullOrEmpty(config?.ClaudeConfigPath)
            ? Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".claude.json")
            : config.ClaudeConfigPath;
        var home = Path.GetDirectoryName(configFile) ?? ".";
        var credsFile = Path.Combine(home, ".claude", ".credentials.json");

        try
        {
            return File.Exists(credsFile) ? File.ReadAllText(credsFile) : null;
        }
        catch (IOException)
        {
            return null;
        }
        catch (UnauthorizedAccessException)
        {
            return null;
        }
    }
}
