using Shouldly;
using TC.CodeGraphApi.Services.Memory;

namespace TC.CodeGraphApi.Tests.Memory;

public class MemoryNormalizationServiceTests
{
    [Theory]
    [InlineData("HelloWorld", "hello_world")]
    [InlineData("ESP IDF", "esp_idf")]
    [InlineData("my-project-name", "my_project_name")]
    [InlineData("camelCaseId", "camel_case_id")]
    [InlineData("already_snake_case", "already_snake_case")]
    [InlineData("MixedCase WithSpaces", "mixed_case_with_spaces")]
    [InlineData("special!@#chars", "special_chars")]
    [InlineData("  leading_trailing  ", "leading_trailing")]
    public void ToSnakeCase_ConvertsCorrectly(string input, string expected)
    {
        var result = MemoryNormalizationService.ToSnakeCase(input);
        result.ShouldBe(expected);
    }

    [Fact]
    public void ToSnakeCase_EmptyString_ReturnsEmpty()
    {
        var result = MemoryNormalizationService.ToSnakeCase("");
        result.ShouldBe("");
    }
}
