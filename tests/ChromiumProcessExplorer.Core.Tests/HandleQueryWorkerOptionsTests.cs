using ChromiumProcessExplorer.Core.Discovery;

namespace ChromiumProcessExplorer.Core.Tests;

public sealed class HandleQueryWorkerOptionsTests
{
    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(-2)]
    public void EffectiveQueryTimeoutRejectsUnboundedDurations(int milliseconds)
    {
        HandleQueryWorkerOptions options = new(
            "cpe.exe",
            QueryTimeout: TimeSpan.FromMilliseconds(milliseconds));

        Assert.Throws<ArgumentOutOfRangeException>(
            () => options.EffectiveQueryTimeout);
    }
}
