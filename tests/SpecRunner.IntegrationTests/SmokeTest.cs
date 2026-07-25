using Xunit;

namespace SpecRunner.IntegrationTests;

public class SmokeTest
{
    [Fact]
    public void Harness_is_wired()
    {
        Assert.True(true);
    }
}
