using ChromiumProcessExplorer.Core.Discovery;

namespace ChromiumProcessExplorer.Core.Tests;

public sealed class MojoPipeNameParserTests
{
    [Theory]
    [InlineData(@"\\.\pipe\mojo.1234.1.2", 1234)]
    [InlineData(@"\\.\pipe\(LOCAL)mojo.host_9876.10.20", 9876)]
    public void TryParseRecognizesMojoPipeNames(string path, int expectedProcessId)
    {
        bool parsed = MojoPipeNameParser.TryParse(path, out MojoPipeCandidate? candidate);

        Assert.True(parsed);
        Assert.NotNull(candidate);
        Assert.Equal(expectedProcessId, candidate.ProcessIdHint);
    }

    [Fact]
    public void TryParseRejectsUnrelatedPipe()
    {
        Assert.False(MojoPipeNameParser.TryParse(
            @"\\.\pipe\dotnet-diagnostic-1234",
            out _));
    }
}
