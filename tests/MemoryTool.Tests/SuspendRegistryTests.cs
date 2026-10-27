using System.Diagnostics;
using MemoryTool.Models;
using MemoryTool.Services;
using Xunit;

namespace MemoryTool.Tests;

public class SuspendRegistryTests : IDisposable
{
    private readonly string _dir;

    public SuspendRegistryTests()
    {
        _dir = Path.Combine(TestTmp.Dir, "mt-tests-susp-" + Guid.NewGuid().ToString("N"));
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, true); } catch { }
    }

    [Fact]
    public void AddRemove_RoundTripsThroughFile()
    {
        using var self = Process.GetCurrentProcess();
        var reg = new SuspendRegistry(_dir);
        Assert.False(reg.IsSuspended(self.Id));

        reg.Add(self.Id, self.StartTime, "self", "test");
        Assert.True(reg.IsSuspended(self.Id));
        Assert.True(File.Exists(Path.Combine(_dir, "suspended.json")), "registry must persist on mutation");
        Assert.Contains(reg.LiveRecords(), r => r.Pid == self.Id);

        // a brand-new registry over the same directory must reload the record (still alive, same pid+startTime)
        var reloaded = new SuspendRegistry(_dir);
        Assert.True(reloaded.IsSuspended(self.Id));
        Assert.Contains(reloaded.LiveRecords(), r => r.Pid == self.Id && r.Name == "self");

        Assert.True(reloaded.Remove(self.Id));
        var afterRemove = new SuspendRegistry(_dir);
        Assert.False(afterRemove.IsSuspended(self.Id));
    }

    [Fact]
    public void DeadOrReusedPid_IsDroppedOnLoad()
    {
        var reg = new SuspendRegistry(_dir);
        reg.Add(999999, DateTime.Now, "ghost", "test"); // pid almost certainly not running

        var reloaded = new SuspendRegistry(_dir);
        Assert.False(reloaded.IsSuspended(999999));
        Assert.Empty(reloaded.LiveRecords());
    }

    [Fact]
    public void StartTimeMismatch_IsRejected()
    {
        // register the real current process but with a wrong start time (simulating pid reuse)
        using var self = Process.GetCurrentProcess();
        var reg = new SuspendRegistry(_dir);
        reg.Add(self.Id, self.StartTime.AddHours(-5), "imposter", "test");

        var reloaded = new SuspendRegistry(_dir);
        Assert.False(reloaded.IsSuspended(self.Id));
    }
}
