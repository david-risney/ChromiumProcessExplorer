using ChromiumProcessExplorer.Core.Discovery;

namespace ChromiumProcessExplorer.Core.Tests;

public sealed class MojoPipeInspectionExtensionsTests
{
    [Fact]
    public void GetRelatedProcessIdsUsesEndpointsAndFallsBackToHints()
    {
        MojoPipeInspectionResult inspection = new(
            DateTimeOffset.UtcNow,
            [
                new MojoPipeInfo(
                    "mojo.99.1.1",
                    99,
                    [
                        new NamedPipeConnection(
                            3,
                            "owner.exe",
                            1,
                            "server.exe",
                            2,
                            "client.exe",
                            "server",
                            "connected"),
                    ]),
                new MojoPipeInfo("mojo.4.1.1", 4, []),
                new MojoPipeInfo(
                    "mojo.6.1.1",
                    6,
                    [
                        new NamedPipeConnection(
                            5,
                            "owner-two.exe",
                            1,
                            "server.exe",
                            null,
                            null,
                            "server",
                            "listening"),
                    ]),
            ],
            new NamedPipeInspectionStatistics(
                3,
                0,
                0,
                0,
                0,
                0,
                0,
                0,
                0,
                TimeSpan.Zero),
            [],
            []);

        IReadOnlySet<int> processIds = inspection.GetRelatedProcessIds();

        Assert.Equal([1, 2, 3, 4, 5], processIds.Order());
        Assert.DoesNotContain(6, processIds);
        Assert.DoesNotContain(99, processIds);
    }
}
