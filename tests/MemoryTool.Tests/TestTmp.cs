namespace MemoryTool.Tests;

/// <summary>
/// Test data directory. Defaults to the system temp path but honors MT_TEST_TMP so CI/dev
/// environments with AV filters watching %TEMP% can redirect (e.g. the workspace .test-tmp).
/// </summary>
public static class TestTmp
{
    public static readonly string Dir =
        Environment.GetEnvironmentVariable("MT_TEST_TMP") is { Length: > 0 } env
            ? env
            : Path.Combine(Path.GetTempPath(), "memorytool-tests");
}
