namespace SpecRunner.Configuration;

/// <summary>
/// One instance's configuration — exactly one repo pair plus settings (contracts/config-schema.md).
/// Secrets never live here; the PAT is a mounted file path, the Claude credential a named volume.
/// </summary>
public sealed record InstanceConfig
{
    public required string Slug { get; init; }
    public required string Path { get; init; }
    public required string WorktreesRoot { get; init; }
    public required string OperatorLogin { get; init; }
    public string BaseBranch { get; init; } = "main";
    public int TickInterval { get; init; } = 300;
    public string WakingHours { get; init; } = "08:00-23:00";
    public int StaleHours { get; init; } = 2;
    public int DecisionCap { get; init; } = 5;
    public string PermissionMode { get; init; } = "acceptEdits";
    public string ReviewPrompt { get; init; } = ".specify/prompts/code-review.md";
    public bool AutoMerge { get; init; } = true;
    public int SpendCap { get; init; } = 100;
    public required string GitHubPatFile { get; init; }
    public string Log { get; init; } = string.Empty;
    public string Lock { get; init; } = string.Empty;
    public string ClaudeConfigPath { get; init; } = string.Empty;
}
