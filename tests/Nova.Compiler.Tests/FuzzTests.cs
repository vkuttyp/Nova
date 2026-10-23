using Nova.Fuzz;

namespace Nova.Compiler.Tests;

/// <summary>
/// The random program generator in the test suite (docs/testing.md §5): a fixed set of seeds goes through the
/// whole oracle (checker, interpreter, every C configuration). Campaigns run more with tests/Nova.Fuzz.
/// </summary>
public class FuzzTests
{
    public static TheoryData<int> Seeds() => [.. Enumerable.Range(1, 32)];

    [Theory]
    [MemberData(nameof(Seeds))]
    public void Generated_program_agrees_everywhere(int seed)
    {
        var verdict = Oracle.Check(seed);
        Assert.True(verdict.Finding is null, $"seed {seed}: {verdict.Finding}\n{verdict.Program}");
    }

    /// <summary>The same seed gives the same program (campaign findings can be reproduced).</summary>
    [Fact]
    public void Generation_is_deterministic() =>
        Assert.Equal(ProgramGenerator.Generate(12345), ProgramGenerator.Generate(12345));
}
