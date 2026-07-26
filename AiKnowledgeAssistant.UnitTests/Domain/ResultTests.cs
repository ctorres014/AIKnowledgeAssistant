using AiKnowledgeAssistant.Domain.Common;

namespace AiKnowledgeAssistant.UnitTests.Domain;

public class ResultTests
{
    [Fact]
    public void Success_CarriesValue_AndNoError()
    {
        var result = Result<int>.Success(42);

        Assert.True(result.IsSuccess);
        Assert.Equal(42, result.Value);
        Assert.Null(result.Error);
        Assert.Null(result.ErrorCode);
    }

    [Fact]
    public void Failure_CarriesErrorAndCode_AndDefaultValue()
    {
        var result = Result<int>.Failure("too many files", "TooManyFiles");

        Assert.False(result.IsSuccess);
        Assert.Equal(default, result.Value);
        Assert.Equal("too many files", result.Error);
        Assert.Equal("TooManyFiles", result.ErrorCode);
    }

    [Fact]
    public void Failure_WithoutCode_LeavesErrorCodeNull()
    {
        var result = Result<string>.Failure("unreadable pdf");

        Assert.False(result.IsSuccess);
        Assert.Null(result.Value);
        Assert.Equal("unreadable pdf", result.Error);
        Assert.Null(result.ErrorCode);
    }

    [Fact]
    public void Map_OnSuccess_ProjectsTheValue()
    {
        var mapped = Result<int>.Success(21).Map(x => x * 2);

        Assert.True(mapped.IsSuccess);
        Assert.Equal(42, mapped.Value);
    }

    [Fact]
    public void Map_OnFailure_PropagatesErrorAndDoesNotInvokeMapper()
    {
        var mapperCalled = false;

        var mapped = Result<int>.Failure("boom", "BOOM").Map(x =>
        {
            mapperCalled = true;
            return x.ToString();
        });

        Assert.False(mapped.IsSuccess);
        Assert.Equal("boom", mapped.Error);
        Assert.Equal("BOOM", mapped.ErrorCode);
        Assert.False(mapperCalled);
    }

    [Fact]
    public async Task MapAsync_OnSuccess_ProjectsTheValue()
    {
        var mapped = await Result<int>.Success(21).MapAsync(x => Task.FromResult(x * 2));

        Assert.True(mapped.IsSuccess);
        Assert.Equal(42, mapped.Value);
    }

    [Fact]
    public async Task MapAsync_OnFailure_PropagatesError()
    {
        var mapped = await Result<int>.Failure("boom", "BOOM").MapAsync(x => Task.FromResult(x.ToString()));

        Assert.False(mapped.IsSuccess);
        Assert.Equal("boom", mapped.Error);
        Assert.Equal("BOOM", mapped.ErrorCode);
    }
}
