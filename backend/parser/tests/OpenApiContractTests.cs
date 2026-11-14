using Microsoft.OpenApi;

namespace Parser.Tests;

public sealed class OpenApiContractTests
{
    [Fact]
    public async Task SavedContractPassesOpenApiValidation()
    {
        await using var stream = File.OpenRead(Path.Combine(AppContext.BaseDirectory, "Contracts", "parser.openapi.json"));
        var result = await OpenApiDocument.LoadAsync(stream, "json");
        Assert.NotNull(result.Diagnostic);
        Assert.Empty(result.Diagnostic.Errors);
        Assert.NotNull(result.Document);
        Assert.Empty(result.Document.Validate(ValidationRuleSet.GetDefaultRuleSet()));
    }
}
