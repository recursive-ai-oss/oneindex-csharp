using OneIndex.Durability;

namespace OneIndex.Tests;

public class SelfCommandTests
{
    [Theory]
    [InlineData("/usr/share/dotnet/dotnet")]
    [InlineData("C:\\Program Files\\dotnet\\dotnet.exe")]
    public void UnderTheDotnetHostTheChildIsTheHostRunningTheEntryAssembly(string host)
        => Assert.Equal([host, "/out/Durability.dll"], SelfCommand.Resolve(host, "/out/Durability.dll"));

    [Fact]
    public void AnApplicationHostIsItsOwnChildWithNoStrayArgument()
        => Assert.Equal(["/out/Durability"], SelfCommand.Resolve("/out/Durability", "/out/Durability.dll"));

    [Fact]
    public void TheDotnetHostWithoutAnEntryAssemblyIsAnError()
        => Assert.Throws<InvalidOperationException>(() => SelfCommand.Resolve("/usr/bin/dotnet", null));

    [Fact]
    public void TheRunningTestHostGivesACommandThatNamesAnExistingProgram()
        => Assert.True(File.Exists(SelfCommand.Get()[0]));
}
