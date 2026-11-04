using MemPilot.Models;
using MemPilot.Services;
using Xunit;

namespace MemPilot.Tests;

/// <summary>
/// 决策日志按 Kind 分桶（v0.9.2，三轮复盘 P3）。
/// 数据依据：9 天里 GrowthWatch 290 条 + AutoOptimize 334 条占了 76%，
/// 而 Lease 8 条 / AiSession 6 条是复盘真正要的证据——混在一个 FIFO 里会被挤掉。
/// </summary>
public class DecisionLogQuotaTests : IDisposable
{
    private readonly string _dir;

    public DecisionLogQuotaTests()
    {
        _dir = Path.Combine(TestTmp.Dir, "mt-tests-declog-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, true); } catch { }
    }

    private static DecisionEntry E(string kind, int minutesAgo = 0)
        => new()
        {
            Kind = kind,
            Target = "-",
            Decision = "测试",
            Reason = kind + " 记录",
            TimestampUtc = DateTime.UtcNow.AddMinutes(-minutesAgo)
        };

    [Fact]
    public void CountByKind_TracksEachCategory()
    {
        var log = new DecisionLog(_dir);
        log.Log(E("Lease"));
        log.Log(E("Lease"));
        log.Log(E("GrowthWatch"));

        log.Flush();
        var counts = log.CountByKind();
        Assert.Equal(2, counts["Lease"]);
        Assert.Equal(1, counts["GrowthWatch"]);
    }

    [Fact]
    public void NoiseIsTrimmed_ImportantRecordsSurvive()
    {
        var log = new DecisionLog(_dir);

        // 先写入少量重要记录（模拟 Lease/AiSession 早于噪声产生）
        for (int i = 0; i < 10; i++) log.Log(E("Lease", minutesAgo: 5000 - i));

        // 再灌入远超上限的噪声
        for (int i = 0; i < 5200; i++) log.Log(E("GrowthWatch", minutesAgo: 4000 - i));

        log.Flush();
        var counts = log.CountByKind();
        Assert.Equal(10, counts["Lease"]);                       // 重要记录一条不丢
        Assert.True(counts["GrowthWatch"] > 0);                  // 噪声仍保留（只是被压到上限内）
        Assert.True(log.Recent(6000).Count <= 5000);             // 总量不超过上限
    }

    [Fact]
    public void ImportantQuota_IsRespected()
    {
        var log = new DecisionLog(_dir);
        // 重要记录灌到超过配额（800）
        for (int i = 0; i < 900; i++) log.Log(E("Lease", minutesAgo: 900 - i));

        log.Flush();
        var counts = log.CountByKind();
        Assert.True(counts["Lease"] <= 800, $"重要配额未生效：{counts["Lease"]}");
        Assert.True(counts["Lease"] >= 700);   // 不是一刀切清零
    }

    [Fact]
    public void PersistsAcrossReload()
    {
        var log = new DecisionLog(_dir);
        log.Log(E("AiSession"));
        log.Log(E("GrowthWatch"));
        log.Flush();

        var reloaded = new DecisionLog(_dir);
        var counts = reloaded.CountByKind();
        Assert.Equal(1, counts["AiSession"]);
        Assert.Equal(1, counts["GrowthWatch"]);
    }
}
