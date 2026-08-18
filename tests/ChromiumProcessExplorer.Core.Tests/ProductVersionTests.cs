using ChromiumProcessExplorer.Core;

namespace ChromiumProcessExplorer.Core.Tests;

public sealed class ProductVersionTests
{
    [Fact]
    public void VersionMetadataIsAvailableToApiConsumers()
    {
        Assert.Matches(@"^\d+\.\d+\.\d+(?:[-.][0-9A-Za-z.-]+)?$",
            ProductVersion.Version);
        Assert.StartsWith(
            ProductVersion.Version,
            ProductVersion.InformationalVersion,
            StringComparison.Ordinal);
    }
}
