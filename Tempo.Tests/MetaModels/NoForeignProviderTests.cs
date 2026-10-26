namespace Tempo.Tests.MetaModels;

/// <summary>
/// meta-models spec, "Meta models only": no other model provider's client package may be
/// referenced anywhere in the solution. Scans every csproj's PackageReference ids.
/// </summary>
public sealed class NoForeignProviderTests
{
    private static readonly string[] ForbiddenPackageIdFragments =
    [
        "OpenAI", "Anthropic", "Gemini", "GoogleAI", "Google.Cloud.AIPlatform",
        "Mistral", "Cohere", "Llama", "AzureAI", "Azure.AI", "Bedrock", "Ollama",
    ];

    [Fact]
    public void No_foreign_model_provider_packages_are_referenced()
    {
        var root = TestPaths.RepoRoot();
        var offending = Directory.EnumerateFiles(root, "*.csproj", SearchOption.AllDirectories)
            .Where(p => !p.Contains("node_modules"))
            .SelectMany(path => File.ReadLines(path)
                .Where(line => line.Contains("PackageReference"))
                .Where(line => ForbiddenPackageIdFragments.Any(f => line.Contains(f, StringComparison.OrdinalIgnoreCase)))
                .Select(line => $"{Path.GetFileName(path)}: {line.Trim()}"))
            .ToList();

        Assert.Empty(offending);
    }
}
