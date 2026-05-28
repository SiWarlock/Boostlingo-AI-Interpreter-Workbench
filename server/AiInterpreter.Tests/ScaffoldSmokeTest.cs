namespace AiInterpreter.Tests;

// Harness-proof only (A.1): confirms `dotnet test` discovers + runs a green xUnit target,
// de-risking the Phase B test investment. Not a behavior pin. (`global using Xunit;` lives
// in GlobalUsings.cs.)
public class ScaffoldSmokeTest
{
    [Fact]
    public void Harness_runs_a_green_target()
    {
        Assert.Equal(2, 1 + 1);
    }
}
