namespace SpecRunner.Configuration;

/// <summary>The outcome of validating config — pure, so it is unit-testable (constitution §7).</summary>
public sealed record ValidationResult(bool Ok, IReadOnlyList<string> Errors)
{
    public static ValidationResult Success { get; } = new(true, []);

    public static ValidationResult Fail(IReadOnlyList<string> errors) => new(false, errors);
}

/// <summary>
/// Fail-fast startup validation (constitution §7): the tick refuses to run on invalid config
/// rather than deferring failures to mid-tick. Pure — takes a config, returns errors — so every
/// rule in contracts/config-schema.md is testable without touching disk or the network.
/// </summary>
public static class ConfigValidation
{
    public static ValidationResult Validate(InstanceConfig c)
    {
        var errors = new List<string>();

        if (!IsOwnerRepo(c.Slug))
        {
            errors.Add($"slug must be owner/repo, got '{c.Slug}'");
        }

        if (string.IsNullOrWhiteSpace(c.Path))
        {
            errors.Add("path is required");
        }

        if (string.IsNullOrWhiteSpace(c.WorktreesRoot))
        {
            errors.Add("worktrees_root is required");
        }

        if (string.IsNullOrWhiteSpace(c.OperatorLogin))
        {
            errors.Add("operator_login is required");
        }

        if (c.TickInterval < 60)
        {
            errors.Add($"tick_interval must be >= 60, got {c.TickInterval}");
        }

        if (!IsWakingHours(c.WakingHours))
        {
            errors.Add($"waking_hours must be HH:MM-HH:MM, got '{c.WakingHours}'");
        }

        if (c.StaleHours <= 0)
        {
            errors.Add($"stale_hours must be > 0, got {c.StaleHours}");
        }

        if (c.DecisionCap < 1)
        {
            errors.Add($"decision_cap must be >= 1, got {c.DecisionCap}");
        }

        if (string.IsNullOrWhiteSpace(c.GitHubPatFile))
        {
            errors.Add("[secret].github_pat_file is required");
        }

        if (c.AutoMerge && c.SpendCap <= 0)
        {
            errors.Add($"spend_cap must be > 0 when auto_merge is true, got {c.SpendCap}");
        }

        return errors.Count == 0 ? ValidationResult.Success : ValidationResult.Fail(errors);
    }

    private static bool IsOwnerRepo(string? slug)
    {
        if (string.IsNullOrWhiteSpace(slug))
        {
            return false;
        }

        var parts = slug.Split('/');
        return parts.Length == 2 && parts[0].Length > 0 && parts[1].Length > 0;
    }

    private static bool IsWakingHours(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return false;
        }

        var halves = value.Split('-');
        return halves.Length == 2 && IsHhmm(halves[0]) && IsHhmm(halves[1]);
    }

    private static bool IsHhmm(string s) =>
        TimeOnly.TryParseExact(s, "HH:mm", out _);
}
