using SpecRunner.Domain;
using SpecRunner.Ports;
using Xunit;

namespace SpecRunner.UnitTests;

public class StageOutcomeTests
{
    private static ProcessResult Result(int exit, string stdout = "", string stderr = "") =>
        new(exit, stdout, stderr);

    [Fact]
    public void Exit_zero_succeeds()
    {
        Assert.Equal(StageResult.Succeeded, StageOutcome.From(Result(0, "all good")));
    }

    [Fact]
    public void Non_zero_fails()
    {
        Assert.Equal(StageResult.Failed, StageOutcome.From(Result(1, "", "segfault")));
    }

    [Theory]
    [InlineData("Claude usage limit reached")]
    [InlineData("rate limit exceeded, try again later")]
    [InlineData("RATE  LIMIT")]
    public void Usage_limits_are_retries_not_failures(string message)
    {
        // FR-043: routine, not an error — the item reverts to ready and a later tick resumes.
        Assert.Equal(StageResult.RateLimited, StageOutcome.From(Result(1, "", message)));
    }

    [Fact]
    public void A_rate_limit_message_on_a_SUCCESSFUL_run_is_not_a_rate_limit()
    {
        // e.g. a review whose output discusses rate limiting in the code under review.
        Assert.Equal(StageResult.Succeeded,
            StageOutcome.From(Result(0, "The handler should respect the API rate limit.")));
    }

    [Fact]
    public void Excerpt_reports_no_output_rather_than_an_empty_block()
    {
        Assert.Contains("no output", StageOutcome.Excerpt("", "Run output"), StringComparison.Ordinal);
        Assert.Contains("no output", StageOutcome.Excerpt(null, "Run output"), StringComparison.Ordinal);
    }

    [Fact]
    public void Excerpt_includes_the_output_and_the_summary_label()
    {
        var excerpt = StageOutcome.Excerpt("findings: drift in section 3", "Findings");
        Assert.Contains("findings: drift in section 3", excerpt, StringComparison.Ordinal);
        Assert.Contains("<summary>Findings</summary>", excerpt, StringComparison.Ordinal);
    }

    [Fact]
    public void Truncation_is_disclosed_not_silent()
    {
        // A silently cut report reads as a complete one — the reader must be told.
        var excerpt = StageOutcome.Excerpt(new string('x', 5000), "Run output", maxChars: 100);
        Assert.Contains("truncated", excerpt, StringComparison.Ordinal);
        Assert.Contains("5,000", excerpt, StringComparison.Ordinal);
    }

    [Fact]
    public void Output_containing_a_code_fence_cannot_break_out_of_the_block()
    {
        // Claude's output routinely contains ``` fences; a 3-backtick wrapper would be escaped
        // by them and spill raw model output into the issue body.
        var excerpt = StageOutcome.Excerpt("```\nrm -rf /\n```", "Run output");
        Assert.Contains("````", excerpt, StringComparison.Ordinal);
    }
}
