using System.Text.RegularExpressions;

namespace Tempo.Tests.Domain;

/// <summary>
/// Design D17 (non-health boundary): TEMPO never uses condition or symptom language in
/// anything the user reads. This scan fails the build if a banned term shows up in UI
/// strings, prompt files or the README. The single README sentence stating that TEMPO is
/// NOT a health product is the only allowed use.
/// </summary>
public sealed partial class NonHealthBoundaryTests
{
    // "medical" is NOT banned in Tempo.Core: AppointmentKind's closed enum uses it as an
    // internal category for the planner, invisible to the user. The scan covers what the
    // user reads: the PWA source, the prompt files and the README.
    [GeneratedRegex(@"ADHD|diagnos|symptom|disorder|\btreat|therap|medical", RegexOptions.IgnoreCase)]
    private static partial Regex BannedTerms();

    private static readonly string[] AllowedLines =
    [
        // The README's non-health boundary sentence and the audience paragraph may name
        // the terms to say what TEMPO is NOT; nothing else may.
        "TEMPO is a productivity tool, not a health product: it never asks about, stores, or claims anything about a diagnosis or condition.",
        "Time blindness — not feeling how far away \"in 40 minutes\" is, or how long \"a quick stop\" takes — is daily reality for many people with ADHD and other neurodivergent people, and for plenty of neurotypical people too.",
    ];

    public static TheoryData<string> ScannedFiles()
    {
        var root = TestPaths.RepoRoot();
        var files = new List<string> { Path.Combine(root, "README.md") };
        files.AddRange(Directory.GetFiles(Path.Combine(root, "Tempo.Agent", "Prompts"), "*.md"));
        files.AddRange(Directory.GetFiles(Path.Combine(root, "Tempo.Web", "src"), "*.ts*", SearchOption.AllDirectories));
        var data = new TheoryData<string>();
        foreach (var file in files)
            data.Add(Path.GetRelativePath(root, file));
        return data;
    }

    [Theory]
    [MemberData(nameof(ScannedFiles))]
    public void User_facing_text_never_uses_condition_language(string relativePath)
    {
        var lines = File.ReadAllLines(Path.Combine(TestPaths.RepoRoot(), relativePath));
        var violations = lines
            .Select((line, i) => (Line: line, Number: i + 1))
            .Where(x => BannedTerms().IsMatch(x.Line))
            .Where(x => !AllowedLines.Any(allowed => x.Line.Contains(allowed, StringComparison.Ordinal)))
            .Select(x => $"{relativePath}:{x.Number}: {x.Line.Trim()}")
            .ToList();
        Assert.True(violations.Count == 0,
            "Banned condition/health language found (design D17):\n" + string.Join("\n", violations));
    }
}
