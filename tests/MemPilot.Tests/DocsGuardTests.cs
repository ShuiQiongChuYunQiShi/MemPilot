using System.Text.RegularExpressions;
using Xunit;

namespace MemPilot.Tests;

/// <summary>
/// 文档治理守卫（v0.13.1）。历史教训：README 写 "~3 MB"（实际 440KB）、
/// docs/README-zh-v0.9.md 声称"未声明开源许可"而仓库早已 Apache-2.0——
/// 文档与现实矛盾是最伤信任的一类缺陷。本守卫把已纠正过的错误数字/说法挡回去。
/// </summary>
public class DocsGuardTests
{
    private static string RepoRoot() => Directory.GetParent(TestTmp.Dir)!.FullName;
    private static string Read(params string[] parts)
        => File.ReadAllText(Path.Combine(new[] { RepoRoot() }.Concat(parts).ToArray()));

    [Fact]
    public void Readme_SizeNumbers_MatchReality()
    {
        var readme = Read("README.md");
        Assert.DoesNotContain("~3 MB", readme);        // portable 实测 ~440KB
        Assert.DoesNotContain("65 MB", readme);         // 自包含实测 62.1MB
        Assert.Contains("~440 KB", readme);
        Assert.Contains("~62 MB", readme);
    }

    [Fact]
    public void Readme_PointsToCurrentChineseDoc()
    {
        var readme = Read("README.md");
        Assert.Contains("docs/README-zh.md", readme);   // 当前中文说明
        Assert.DoesNotContain("README-zh-v0.9.md", readme);
    }

    [Fact]
    public void ArchivedZhDoc_IsMarkedHistorical_AndCurrentOneIsClean()
    {
        var archived = Read("docs", "README-zh-v0.9.md");
        Assert.Contains("历史", archived[..400]);       // 归档头在前 400 字符内

        var current = Read("docs", "README-zh.md");
        Assert.DoesNotContain("未声明开源许可", current); // 与 Apache-2.0 矛盾的旧说法
        Assert.DoesNotContain("腾挪", current);           // 术语已统一为「腾内存」
        Assert.Contains("腾内存", current);
    }

    [Fact]
    public void MissingDocs_NowExist()
    {
        Assert.True(File.Exists(Path.Combine(RepoRoot(), "docs", "PRIVACY.md")));
        Assert.True(File.Exists(Path.Combine(RepoRoot(), "docs", "LEASE-API.md")));
        Assert.True(File.Exists(Path.Combine(RepoRoot(), "docs", "NOTIFICATION-POLICY.md")));
        // 隐私文档必须回答三个核心问题
        var privacy = Read("docs", "PRIVACY.md");
        Assert.Contains("30 天", privacy);
        Assert.Contains("%APPDATA%", privacy);
        Assert.Contains("什么都不出", privacy);
    }

    [Fact]
    public void Readme_DoesNotUseRetiredWorkflowTerm()
    {
        // "腾挪工作流"曾在 README-zh 里重复出现两次——术语统一后的英文 README 不得回潮
        var readme = Read("README.md");
        Assert.DoesNotContain("腾挪", readme);
    }

    [Fact]
    public void Architecture_DataLayout_CoversNewFiles()
    {
        var arch = Read("docs", "ARCHITECTURE.md");
        foreach (var f in new[] { "ai-session.json", "feedback.json", "behavior.json", "killed.json" })
            Assert.Contains(f, arch);
        Assert.Contains("FloorTrim", arch);             // v0.13 语义
    }
}
