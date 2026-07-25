using SpecRunner.Domain;
using Xunit;

namespace SpecRunner.UnitTests;

/// <summary>
/// Tier 1: the stage → Claude-command mapping (research R16). Pure, so the slash-command
/// construction is verified without spawning a process.
/// </summary>
public class StageCommandTests
{
    [Theory]
    [InlineData(Stage.Specify, "/speckit.specify")]
    [InlineData(Stage.Clarify, "/speckit.clarify")]
    [InlineData(Stage.Plan, "/speckit.plan")]
    [InlineData(Stage.Tasks, "/speckit.tasks")]
    [InlineData(Stage.Analyze, "/speckit.analyze")]
    [InlineData(Stage.Implement, "/speckit.implement")]
    public void Speckit_stages_map_to_their_slash_command(Stage stage, string expected)
    {
        Assert.Equal(expected, StageCommand.SlashCommandFor(stage));
    }

    [Theory]
    [InlineData(Stage.Intake)]
    [InlineData(Stage.Review)]
    public void Runner_driven_stages_have_no_slash_command(Stage stage)
    {
        Assert.Null(StageCommand.SlashCommandFor(stage));
    }

    [Fact]
    public void Prompt_appends_args_when_present()
    {
        Assert.Equal("/speckit.specify add a widget", StageCommand.PromptFor(Stage.Specify, "add a widget"));
    }

    [Fact]
    public void Prompt_is_bare_command_when_no_args()
    {
        Assert.Equal("/speckit.plan", StageCommand.PromptFor(Stage.Plan));
        Assert.Equal("/speckit.plan", StageCommand.PromptFor(Stage.Plan, "   "));
    }

    [Fact]
    public void Prompt_is_null_for_runner_driven_stages()
    {
        Assert.Null(StageCommand.PromptFor(Stage.Intake, "anything"));
    }
}
