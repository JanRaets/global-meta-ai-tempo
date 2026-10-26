namespace Tempo.Tests;

public static class TestPaths
{
    public static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && dir.GetFiles("Tempo.sln").Length == 0)
            dir = dir.Parent;
        return dir?.FullName ?? throw new InvalidOperationException("Tempo.sln not found above test directory");
    }
}
