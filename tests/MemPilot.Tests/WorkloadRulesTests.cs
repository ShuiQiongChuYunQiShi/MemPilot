using System.Text.RegularExpressions;
using MemPilot.Models;
using MemPilot.Services;
using Xunit;

namespace MemPilot.Tests;

/// <summary>
/// 用途判定外置与人工纠正（v0.4.0，棱镜 product/classification-quality）：
/// ① 名单可被 workload-rules.json 覆盖，缺失/空数组回落内置默认（手滑不会清空名单）；
/// ② 用户标记优先于一切规则，且可撤销（撤销后回到规则判定）。
/// </summary>
public class WorkloadRulesTests : IDisposable
{
    private readonly string _dir;

    public WorkloadRulesTests()
    {
        _dir = Path.Combine(TestTmp.Dir, "mt-tests-wlrules-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, true); } catch { }
    }

    [Fact]
    public void LoadOrCreate_WritesDefaultFile()
    {
        var rules = WorkloadRules.LoadOrCreate(_dir);
        Assert.NotEmpty(rules.EntertainmentNames);
        Assert.NotEmpty(rules.WorkExactNames);

        var path = Path.Combine(_dir, "workload-rules.json");
        Assert.True(File.Exists(path));
        Assert.Contains("steam", File.ReadAllText(path));
    }

    [Fact]
    public void CustomFile_OverridesOnlyProvidedLists()
    {
        var path = Path.Combine(_dir, "workload-rules.json");
        File.WriteAllText(path, """
        {
          // 只改娱乐名单，其余沿用默认
          "EntertainmentNames": ["myplayer"]
        }
        """);

        var rules = WorkloadRules.LoadOrCreate(_dir);
        Assert.Equal(new[] { "myplayer" }, rules.EntertainmentNames);
        Assert.Contains("winword", rules.WorkNames);      // 未覆盖 → 默认仍在
        Assert.Contains("et", rules.WorkExactNames);
    }

    [Fact]
    public void EmptyArrays_FallBackToDefaults()
    {
        var path = Path.Combine(_dir, "workload-rules.json");
        File.WriteAllText(path, """{ "EntertainmentNames": [], "WorkNames": [] }""");
        var rules = WorkloadRules.LoadOrCreate(_dir);
        Assert.Contains("steam", rules.EntertainmentNames); // 空数组不算"清空"
        Assert.Contains("winword", rules.WorkNames);
    }

    [Fact]
    public void CorruptFile_FallsBackToDefaults()
    {
        File.WriteAllText(Path.Combine(_dir, "workload-rules.json"), "{ not json");
        var rules = WorkloadRules.LoadOrCreate(_dir);
        Assert.Contains("steam", rules.EntertainmentNames);
    }

    [Fact]
    public void CustomRules_AreApplied()
    {
        var rules = WorkloadRules.Defaults();
        rules.EntertainmentNames.Insert(0, "myplayer");
        var p = new ProcessInfo { Pid = 1, Name = "myplayer.exe", Category = ProcessCategory.ThirdPartyApp };
        WorkloadClassifier.Apply(p, rules, null);
        Assert.Equal(WorkloadKind.Entertainment, p.Workload);
        Assert.Equal("name:entertainment", p.WorkloadReason);
    }

    [Fact]
    public void UserMark_BeatsEveryRule()
    {
        // wps 默认在 WorkExactNames 里 → 工作；人工标成娱乐必须覆盖
        var p = new ProcessInfo { Pid = 1, Name = "wps", Category = ProcessCategory.ThirdPartyApp };
        WorkloadClassifier.Apply(p, null, _ => WorkloadKind.Entertainment);
        Assert.Equal(WorkloadKind.Entertainment, p.Workload);
        Assert.Equal("user_mark", p.WorkloadReason);
    }

    [Fact]
    public void NoUserMark_FallsBackToRules()
    {
        var p = new ProcessInfo { Pid = 1, Name = "wps", Category = ProcessCategory.ThirdPartyApp };
        WorkloadClassifier.Apply(p, null, _ => null);
        Assert.Equal(WorkloadKind.Work, p.Workload);
    }

    [Fact]
    public void FeedbackStore_WorkloadMark_RoundTrips_AndReplaces()
    {
        var store = new FeedbackStore(_dir);
        Assert.Null(store.FindWorkload("wps", ""));

        store.SetWorkload("wps", "", WorkloadKind.Entertainment);
        Assert.Equal(WorkloadKind.Entertainment, store.FindWorkload("wps", ""));

        store.SetWorkload("wps", "", WorkloadKind.Other);      // 重复标记 = 替换
        Assert.Equal(WorkloadKind.Other, store.FindWorkload("wps", ""));
        Assert.Single(store.All());

        store.SetWorkload("wps", "", null);                     // 清除
        Assert.Null(store.FindWorkload("wps", ""));
        Assert.Empty(store.All());
    }

    [Fact]
    public void ContextMenuTags_AreValidWorkloadKinds()
    {
        // 右键菜单的 Tag 是枚举名字符串：写错一个字母就会静默变成"清除标记"
        var xaml = File.ReadAllText(Path.Combine(
            Directory.GetParent(TestTmp.Dir)!.FullName, "src", "MemPilot.App", "MainWindow.xaml"));
        var tags = Regex.Matches(xaml, "<MenuItem Header=\"[^\"]+\" Tag=\"([^\"]*)\" Click=\"CtxWorkload_Click\"")
                        .Select(m => m.Groups[1].Value).ToList();

        Assert.NotEmpty(tags);
        foreach (var tag in tags.Where(t => t.Length > 0))
            Assert.True(Enum.TryParse<WorkloadKind>(tag, out _), "非法用途标记: " + tag);
        Assert.Contains(tags, t => t.Length == 0); // 必须留一个"清除标记"
    }

    [Fact]
    public void FeedbackStore_WorkloadMark_MatchesByPath()
    {
        var store = new FeedbackStore(_dir);
        store.SetWorkload("anything", @"D:\apps\a\tool.exe", WorkloadKind.Work);
        Assert.Equal(WorkloadKind.Work, store.FindWorkload("renamed", @"D:\apps\a\tool.exe"));
        Assert.Null(store.FindWorkload("renamed", @"D:\apps\b\tool.exe"));
    }
}
