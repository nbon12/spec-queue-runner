using Tomlyn;
using Tomlyn.Model;

namespace SpecRunner.Configuration;

/// <summary>Parses an instance's TOML config (research R2: Tomlyn, for line/column diagnostics).</summary>
public static class ConfigLoader
{
    public static InstanceConfig Load(string tomlPath)
    {
        var text = File.ReadAllText(tomlPath);
        var model = Toml.ToModel(text);

        string Str(string key, string fallback = "") =>
            model.TryGetValue(key, out var v) && v is string s ? s : fallback;

        int Int(string key, int fallback) =>
            model.TryGetValue(key, out var v) && v is long l ? (int)l : fallback;

        bool Bool(string key, bool fallback) =>
            model.TryGetValue(key, out var v) && v is bool b ? b : fallback;

        // Nested [secret] table → github_pat_file.
        var patFile = string.Empty;
        if (model.TryGetValue("secret", out var secretObj) && secretObj is TomlTable secret &&
            secret.TryGetValue("github_pat_file", out var pf) && pf is string pfs)
        {
            patFile = pfs;
        }

        return new InstanceConfig
        {
            Slug = Str("slug"),
            Path = Str("path"),
            WorktreesRoot = Str("worktrees_root"),
            OperatorLogin = Str("operator_login"),
            BaseBranch = Str("base_branch", "main"),
            TickInterval = Int("tick_interval", 300),
            WakingHours = Str("waking_hours", "08:00-23:00"),
            StaleHours = Int("stale_hours", 2),
            DecisionCap = Int("decision_cap", 5),
            PermissionMode = Str("permission_mode", "acceptEdits"),
            ReviewPrompt = Str("review_prompt", ".specify/prompts/code-review.md"),
            Verify = Str("verify"),
            AutoMerge = Bool("auto_merge", true),
            SpendCap = Int("spend_cap", 100),
            GitHubPatFile = patFile,
            Log = Str("log"),
            Lock = Str("lock"),
            ClaudeConfigPath = Str("claude_config_path"),
        };
    }
}
