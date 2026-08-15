using ChromiumProcessExplorer.Core.Discovery;

namespace ChromiumProcessExplorer.Core.Tests;

public sealed class ChromiumCommandLineTests
{
    [Fact]
    public void ParseReadsChromiumSwitchValues()
    {
        ChromiumCommandLine commandLine = ChromiumCommandLine.Parse(
            "\"C:\\Program Files\\Chromium\\chrome.exe\" --type=renderer "
            + "--user-data-dir=\"C:\\Profiles\\Test User\"");

        Assert.Equal("renderer", commandLine.GetSwitchValue("type"));
        Assert.Equal("C:\\Profiles\\Test User", commandLine.GetSwitchValue("user-data-dir"));
        Assert.True(commandLine.HasSwitch("--type"));
    }

    [Fact]
    public void ParseHandlesMissingCommandLine()
    {
        ChromiumCommandLine commandLine = ChromiumCommandLine.Parse(null);

        Assert.Empty(commandLine.Arguments);
        Assert.False(commandLine.HasSwitch("type"));
    }
}
