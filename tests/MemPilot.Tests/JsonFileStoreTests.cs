using MemPilot.Helpers;
using Xunit;

namespace MemPilot.Tests;

/// <summary>
/// JsonFileStore（v0.3.2）：把 6 个 Store 复制的落盘模板收敛到一处。
/// 守住三件事：① 原子写（不留 .tmp、不写半截文件）；② 读取损坏文件返回 null 而不是抛；
/// ③ 失败必须进诊断计数——静默吞异常正是上轮棱镜点名的风险。
/// </summary>
public class JsonFileStoreTests : IDisposable
{
    private readonly string _dir;

    public JsonFileStoreTests()
    {
        _dir = Path.Combine(TestTmp.Dir, "mt-tests-jsonstore-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);
        StoreDiagnostics.Reset();
        StoreDiagnostics.LogPath = null;
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, true); } catch { }
    }

    private string PathOf(string name) => Path.Combine(_dir, name);

    public class Payload
    {
        public string Name { get; set; } = "";
        public int Value { get; set; }
    }

    [Fact]
    public void SaveThenLoad_RoundTrips_AndLeavesNoTempFile()
    {
        var path = PathOf("a.json");
        Assert.True(JsonFileStore.Save(path, new List<Payload> { new() { Name = "x", Value = 7 } }));

        var back = JsonFileStore.Load<List<Payload>>(path);
        Assert.NotNull(back);
        Assert.Single(back!);
        Assert.Equal("x", back![0].Name);
        Assert.Equal(7, back[0].Value);
        Assert.False(File.Exists(path + ".tmp"));
        Assert.Equal(0, StoreDiagnostics.Total);
    }

    [Fact]
    public void SaveOverExisting_Overwrites()
    {
        var path = PathOf("b.json");
        JsonFileStore.Save(path, new List<Payload> { new() { Name = "old" } });
        JsonFileStore.Save(path, new List<Payload> { new() { Name = "new" } });
        Assert.Equal("new", JsonFileStore.Load<List<Payload>>(path)![0].Name);
    }

    [Fact]
    public void LoadMissingFile_ReturnsNull()
    {
        Assert.Null(JsonFileStore.Load<List<Payload>>(PathOf("nope.json")));
    }

    [Fact]
    public void LoadCorruptedFile_ReturnsNull_AndRecordsDiagnostic()
    {
        var path = PathOf("broken.json");
        File.WriteAllText(path, "{ this is not json");
        Assert.Null(JsonFileStore.Load<List<Payload>>(path));
        Assert.True(StoreDiagnostics.Total >= 1);
        Assert.Contains("load:broken.json", StoreDiagnostics.Summary());
    }

    [Fact]
    public void LoadEmptyFile_ReturnsNull_WithoutDiagnostic()
    {
        var path = PathOf("empty.json");
        File.WriteAllText(path, "   ");
        Assert.Null(JsonFileStore.Load<List<Payload>>(path));
        Assert.Equal(0, StoreDiagnostics.Total); // 空文件是"没有数据"，不是故障
    }

    [Fact]
    public void Diagnostics_SummaryIsEmptyWhenClean()
    {
        Assert.Contains("无失败记录", StoreDiagnostics.Summary());
        Assert.Empty(StoreDiagnostics.Recent());
    }

    [Fact]
    public void SaveFailure_IsRecorded_NotSilent()
    {
        // 用非法路径触发保存失败（目录名里有 \0 在 Windows 上必然失败）
        var bad = Path.Combine(_dir, "bad\0name.json");
        Assert.False(JsonFileStore.Save(bad, new List<Payload> { new() { Name = "x" } }));
        Assert.True(StoreDiagnostics.Total >= 1);
        Assert.Contains("save:", StoreDiagnostics.Summary());
    }
}
