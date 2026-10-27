namespace MemoryTool.Tests;

/// <summary>
/// Test data directory. Resolution order: MT_TEST_TMP env var, then the repository's
/// .test-tmp (found by walking up from the test bin folder), then the system temp path.
/// The workspace default matters: security software transiently locks first writes in
/// %TEMP%, which made fresh-machine test runs fail (wharf issue 2026-10-28) — workspace
/// writes are never intercepted.
/// </summary>
public static class TestTmp
{
    public static readonly string Dir =
        Environment.GetEnvironmentVariable("MT_TEST_TMP") is { Length: > 0 } env ? env
        : FindRepoRoot() is { } repo ? Path.Combine(repo, ".test-tmp")
        : Path.Combine(Path.GetTempPath(), "memorytool-tests");

    private static string? FindRepoRoot()
    {
        try
        {
            var dir = new DirectoryInfo(AppContext.BaseDirectory);
            for (int i = 0; i < 8 && dir != null; i++, dir = dir.Parent)
            {
                if (Directory.Exists(Path.Combine(dir.FullName, ".git")) ||
                    File.Exists(Path.Combine(dir.FullName, "wharf.project.json")))
                    return dir.FullName;
            }
        }
        catch { }
        return null;
    }
}
