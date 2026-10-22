using Xunit.Abstractions;

namespace Nova.Compiler.Tests;

/// <summary>Records which C configurations this run used, so test logs show what was really exercised.</summary>
public class ToolchainReportTests(ITestOutputHelper output)
{
    [Fact]
    public void Report_c_configurations()
    {
        foreach (var config in CToolchains.Configs)
        {
            output.WriteLine($"C configuration: {config.Name} ({string.Join(' ', config.Flags)})");
        }
        foreach (var missing in CToolchains.Missing)
        {
            output.WriteLine($"not available: {missing}");
        }
        Assert.NotEmpty(CToolchains.Configs);
    }
}
