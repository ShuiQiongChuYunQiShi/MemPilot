using MemPilot.Models;
using MemPilot.Services;
using Xunit;

namespace MemPilot.Tests;

/// <summary>容器/虚拟机识别：Docker/WSL 全家必须归 ContainerVm，不能被 \docker\ 路径误标成开发工具。</summary>
public class ContainerClassificationTests : IDisposable
{
    private readonly string _dir;
    private readonly ClassificationEngine _engine;

    public ContainerClassificationTests()
    {
        _dir = Path.Combine(TestTmp.Dir, "mt-tests-container-" + Guid.NewGuid().ToString("N"));
        _engine = new ClassificationEngine(new FeedbackStore(_dir));
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, true); } catch { }
    }

    private static ProcessInfo Make(string name, string path, bool verified = true, string subject = "")
        => new() { Pid = 1000, Name = name, ExecutablePath = path, SignatureVerified = verified, SignatureSubject = subject };

    [Theory]
    [InlineData("Docker Desktop", @"C:\Program Files\Docker\Docker\frontend\Docker Desktop.exe")]
    [InlineData("com.docker.backend", @"C:\Program Files\Docker\Docker\resources\com.docker.backend.exe")]
    [InlineData("com.docker.build", @"C:\Program Files\Docker\Docker\resources\com.docker.build.exe")]
    [InlineData("cagent", @"C:\Program Files\Docker\Docker\resources\bin\cagent.exe")]
    [InlineData("wslservice", @"C:\Program Files\WSL\wslservice.exe")]
    [InlineData("wslhost", @"C:\Program Files\WSL\wslhost.exe")]
    public void DockerAndWsl_AreContainerVm_NotDevTool(string name, string path)
    {
        var p = Make(name, path);
        _engine.Classify(p, new(), new HashSet<string>());
        Assert.Equal(ProcessCategory.ContainerVm, p.Category);
        Assert.NotEqual("dev_tool", p.CategoryReason);
    }

    // vmmemWSL 没有可执行路径，只能按名称前缀识别
    [Fact]
    public void VmmemWithoutPath_IsContainerVm()
    {
        var p = Make("vmmemWSL", "");
        _engine.Classify(p, new(), new HashSet<string>());
        Assert.Equal(ProcessCategory.ContainerVm, p.Category);
    }

    // Docker Inc 签名覆盖未来新增的 docker 组件
    [Fact]
    public void DockerSignature_IsContainerVm()
    {
        var p = Make("some-new-docker-helper", @"D:\tools\docker-helper\helper.exe", verified: true,
            subject: "CN=Docker Inc, O=Docker Inc");
        _engine.Classify(p, new(), new HashSet<string>());
        Assert.Equal(ProcessCategory.ContainerVm, p.Category);
        Assert.Equal("publisher:docker", p.CategoryReason);
    }

    // 回归：普通开发工具仍然判 DevTool（不能因为顺序调整把 IDE 也归进容器）
    [Fact]
    public void RegularDevTool_StillDevTool()
    {
        var p = Make("devenv", @"C:\Program Files\Microsoft Visual Studio\2022\Community\Common7\IDE\devenv.exe");
        _engine.Classify(p, new(), new HashSet<string>());
        Assert.Equal(ProcessCategory.DevTool, p.Category);
    }
}
