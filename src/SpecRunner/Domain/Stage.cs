namespace SpecRunner.Domain;

/// <summary>
/// Pipeline position. Shaping stages (Intake, Specify, Clarify) never write code (FR-018);
/// execution stages (Plan..Review) decide and report (FR-030/031). Review is an execution
/// stage that runs against the open PR before close (FR-034a).
/// </summary>
public enum Stage
{
    Intake,
    Specify,
    Clarify,
    Plan,
    Tasks,
    Analyze,
    Implement,
    Review,
}

public static class StageGroups
{
    /// <summary>Shaping stages MUST NOT write code (constitution §3, FR-018).</summary>
    public static bool IsShaping(this Stage stage) =>
        stage is Stage.Intake or Stage.Specify or Stage.Clarify;

    /// <summary>Execution stages decide-and-report; only irreversibility blocks (FR-031).</summary>
    public static bool IsExecution(this Stage stage) => !stage.IsShaping();
}
