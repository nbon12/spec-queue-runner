namespace SpecRunner.Domain;

/// <summary>
/// The seam T039a informed: how a pipeline stage maps to a Claude Code invocation. The probe
/// proved headless `claude -p` dispatches custom project slash commands with `$ARGUMENTS`
/// (research R16), so the live mapping is the slash-command string — the inline-markdown
/// fallback is not needed. Pure, so the mapping is unit-testable without a process.
/// </summary>
public static class StageCommand
{
    /// <summary>
    /// The SpecKit slash command a stage runs, or <c>null</c> for stages the runner drives
    /// itself rather than via a SpecKit command (intake infers in-process; review runs the
    /// project's own review prompt).
    /// </summary>
    public static string? SlashCommandFor(Stage stage) => stage switch
    {
        Stage.Specify => "/speckit.specify",
        Stage.Clarify => "/speckit.clarify",
        Stage.Plan => "/speckit.plan",
        Stage.Tasks => "/speckit.tasks",
        Stage.Analyze => "/speckit.analyze",
        Stage.Implement => "/speckit.implement",
        Stage.Intake or Stage.Review => null,
        _ => throw new ArgumentOutOfRangeException(nameof(stage), stage, "unknown stage"),
    };

    /// <summary>
    /// The full print-mode prompt for a stage: the slash command plus any args. Returns
    /// <c>null</c> when the stage has no SpecKit command.
    /// </summary>
    public static string? PromptFor(Stage stage, string? args = null)
    {
        var slash = SlashCommandFor(stage);
        if (slash is null)
        {
            return null;
        }

        return string.IsNullOrWhiteSpace(args) ? slash : $"{slash} {args}";
    }
}
