using MemPilot.Models;
using MemPilot.Services;
using Xunit;

namespace MemPilot.Tests;

/// <summary>
/// 持久化 key 迁移（v0.10.0）。核心命题：**加语言不能重置用户的设置**。
///
/// 旧实现把中文表头（「内存增长」）和中文用途名（「娱乐」）直接写进 config.json。
/// 一旦界面加英文，这些 key 全部对不上 → 列设置静默失效、筛选永远筛出空。
/// 迁移必须幂等、且**识别不出来的旧值原样保留**（宁可留着也不丢）。
/// </summary>
public class PersistedKeyMigrationTests : IDisposable
{
    private readonly string _dir;

    public PersistedKeyMigrationTests()
    {
        _dir = Path.Combine(TestTmp.Dir, "mig-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, true); } catch { }
    }

    [Fact]
    public void LegacyChineseColumns_BecomeStableIds()
    {
        var (cols, changed) = PersistedIds.MigrateColumns(new[] { "内存增长", "线程数", "窗口标题" });
        Assert.True(changed);
        Assert.Equal(new[]
        {
            PersistedIds.Column.MemoryGrowth,
            PersistedIds.Column.Threads,
            PersistedIds.Column.WindowTitle
        }, cols);
    }

    [Fact]
    public void AlreadyStableIds_AreLeftAlone()
    {
        var (cols, changed) = PersistedIds.MigrateColumns(new[]
        {
            PersistedIds.Column.MemoryGrowth, PersistedIds.Column.Signature
        });
        Assert.False(changed);
        Assert.Equal(2, cols.Count);
    }

    [Fact]
    public void UnknownValues_ArePreserved_NeverDropped()
    {
        // 用户手改过 config 或未来版本的列名——迁移不能把它吃掉
        var (cols, _) = PersistedIds.MigrateColumns(new[] { "某个未来列名", "内存增长" });
        Assert.Contains("某个未来列名", cols);
        Assert.Contains(PersistedIds.Column.MemoryGrowth, cols);
    }

    [Fact]
    public void MigrationIsIdempotent()
    {
        var (once, _) = PersistedIds.MigrateColumns(new[] { "显存", "签名" });
        var (twice, changed) = PersistedIds.MigrateColumns(once);
        Assert.False(changed);
        Assert.Equal(once, twice);
    }

    [Theory]
    [InlineData("工作", "work")]
    [InlineData("娱乐", "entertainment")]
    [InlineData("AI", "ai")]
    [InlineData("系统", "system")]
    [InlineData("其他", "other")]
    [InlineData("未知", "unknown")]
    public void LegacyWorkloadFilter_BecomesId(string legacy, string expected)
    {
        var (value, changed) = PersistedIds.MigrateWorkloadFilter(legacy);
        Assert.True(changed);
        Assert.Equal(expected, value);
    }

    [Fact]
    public void EmptyWorkloadFilter_StaysAll()
    {
        var (value, changed) = PersistedIds.MigrateWorkloadFilter("");
        Assert.False(changed);
        Assert.Equal("", value);
    }

    [Fact]
    public void ConfigStore_MigratesOnLoad_AndPersistsOnce()
    {
        // 写一份"旧格式"config（中文 key），然后用 ConfigStore 加载
        var legacyJson = """
        {
          "ProcessHiddenColumns": ["内存增长", "线程数", "音频"],
          "WorkloadFilter": "娱乐"
        }
        """;
        File.WriteAllText(Path.Combine(_dir, "config.json"), legacyJson);

        var store = new ConfigStore(_dir);

        Assert.Contains(PersistedIds.Column.MemoryGrowth, store.Config.ProcessHiddenColumns);
        Assert.Contains(PersistedIds.Column.Threads, store.Config.ProcessHiddenColumns);
        Assert.Contains(PersistedIds.Column.Audio, store.Config.ProcessHiddenColumns);
        Assert.Equal(PersistedIds.Workload.Entertainment, store.Config.WorkloadFilter);

        // 迁移必须立即落盘：磁盘上不能再有中文 key
        var onDisk = File.ReadAllText(Path.Combine(_dir, "config.json"));
        Assert.Contains("memory_growth", onDisk);
        Assert.Contains("entertainment", onDisk);
        Assert.DoesNotContain("\"内存增长\"", onDisk);

        // 再加载一次不应再变化（幂等）
        var again = new ConfigStore(_dir);
        Assert.Equal(store.Config.WorkloadFilter, again.Config.WorkloadFilter);
    }

    [Fact]
    public void WorkloadText_RoundTripsIdAndEnum()
    {
        foreach (var kind in Enum.GetValues<WorkloadKind>())
        {
            var id = WorkloadText.IdOf(kind);
            Assert.Equal(kind, WorkloadText.FromId(id));
        }
        Assert.Null(WorkloadText.FromId("not_a_real_id"));
    }
}
