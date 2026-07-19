namespace AiKnowledgeAssistant.UnitTests;

/// <summary>
/// Smoke test: confirms the unit test project builds, runs and can use Moq.
/// Real domain/application tests arrive with the logic in later specs.
/// </summary>
public class SmokeTests
{
    [Fact]
    public void Smoke_TestInfrastructure_Works()
    {
        var comparer = Moq.Mock.Of<System.Collections.Generic.IComparer<int>>();

        Assert.NotNull(comparer);
        Assert.True(true);
    }
}
