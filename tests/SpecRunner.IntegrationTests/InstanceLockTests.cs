using SpecRunner.Ticking;
using Xunit;

namespace SpecRunner.IntegrationTests;

/// <summary>Tier 2 — the per-instance exclusive lock: two simultaneous ticks, exactly one works (FR-002).</summary>
public class InstanceLockTests
{
    [Fact]
    public void Second_acquirer_is_denied_while_first_holds()
    {
        var tmp = Directory.CreateTempSubdirectory("lock");
        try
        {
            var path = Path.Combine(tmp.FullName, "inst.lock");

            using var first = InstanceLock.TryAcquire(path);
            Assert.NotNull(first);

            var second = InstanceLock.TryAcquire(path);
            Assert.Null(second); // held — a concurrent tick must back off
        }
        finally
        {
            tmp.Delete(recursive: true);
        }
    }

    [Fact]
    public void Lock_is_reacquirable_after_release()
    {
        var tmp = Directory.CreateTempSubdirectory("lock2");
        try
        {
            var path = Path.Combine(tmp.FullName, "inst.lock");

            using (var first = InstanceLock.TryAcquire(path))
            {
                Assert.NotNull(first);
            } // released by using

            using var again = InstanceLock.TryAcquire(path);
            Assert.NotNull(again); // the next tick gets it
        }
        finally
        {
            tmp.Delete(recursive: true);
        }
    }
}
