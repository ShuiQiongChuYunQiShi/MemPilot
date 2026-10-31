using MemPilot.Models;
using MemPilot.Services;
using Xunit;

namespace MemPilot.Tests;

/// <summary>用途维度（娱乐/工作/AI/系统/其他）：强类别优先，名称/清单/发布者次之，歧义不硬猜。</summary>
public class WorkloadClassifierTests
{
    private static ProcessInfo P(string name, ProcessCategory cat, string program = "", string publisher = "")
        => new() { Pid = 1, Name = name, Category = cat, ProgramDisplayName = program, ProgramPublisher = publisher };

    [Theory]
    [InlineData(ProcessCategory.Game)]
    [InlineData(ProcessCategory.GameLauncher)]
    [InlineData(ProcessCategory.AntiCheat)]
    public void GameCategories_AreEntertainment(ProcessCategory cat)
    {
        var p = P("something", cat);
        WorkloadClassifier.Apply(p);
        Assert.Equal(WorkloadKind.Entertainment, p.Workload);
    }

    [Fact]
    public void AiCategory_IsAi()
    {
        var p = P("llama-server", ProcessCategory.AiImageVideo);
        WorkloadClassifier.Apply(p);
        Assert.Equal(WorkloadKind.Ai, p.Workload);
    }

    [Theory]
    [InlineData(ProcessCategory.DevTool)]
    [InlineData(ProcessCategory.ContainerVm)]
    public void DevAndContainer_AreWork(ProcessCategory cat)
    {
        var p = P("devenv", cat);
        WorkloadClassifier.Apply(p);
        Assert.Equal(WorkloadKind.Work, p.Workload);
    }

    [Theory]
    [InlineData("winword", ProcessCategory.ThirdPartyApp)]
    [InlineData("mysqld", ProcessCategory.ThirdPartyApp)]
    [InlineData("bilibili", ProcessCategory.ThirdPartyApp)]
    [InlineData("qqmusic", ProcessCategory.ThirdPartyApp)]
    public void KnownNames_MapToWorkOrEntertainment(string name, ProcessCategory cat)
    {
        var p = P(name, cat);
        WorkloadClassifier.Apply(p);
        var expectEnt = name is "bilibili" or "qqmusic";
        Assert.Equal(expectEnt ? WorkloadKind.Entertainment : WorkloadKind.Work, p.Workload);
    }

    [Fact]
    public void ProgramCatalogPublisher_IsUsedWhenSignatureMissing()
    {
        var work = P("vendorhelper", ProcessCategory.ThirdPartyApp, program: "JetBrains Toolbox", publisher: "JetBrains");
        WorkloadClassifier.Apply(work);
        Assert.Equal(WorkloadKind.Work, work.Workload);

        var ent = P("launcherx", ProcessCategory.ThirdPartyApp, program: "Some Game", publisher: "Valve Corporation");
        WorkloadClassifier.Apply(ent);
        Assert.Equal(WorkloadKind.Entertainment, ent.Workload);
    }

    [Fact]
    public void AmbiguousProcesses_StayUnknownOrOther()
    {
        var browser = P("msedge", ProcessCategory.ThirdPartyApp);
        WorkloadClassifier.Apply(browser);
        Assert.Equal(WorkloadKind.Other, browser.Workload); // 浏览器：其它，不硬猜

        var mystery = P("mystery", ProcessCategory.Unknown);
        WorkloadClassifier.Apply(mystery);
        Assert.Equal(WorkloadKind.Unknown, mystery.Workload);
    }
}

public class WorkloadSubstringGuardTests
{
    [Theory]
    [InlineData("wetype_service")]   // 曾因 "et" 子串被误判工作
    [InlineData("ArmourySocketServer")] // 同上（Sock-et）
    [InlineData("wpscenter")]        // Bloatware：不参与用途判定
    public void ShortNameSubstrings_DoNotMisfire(string name)
    {
        var p = new MemPilot.Models.ProcessInfo
        {
            Pid = 1,
            Name = name,
            Category = name == "wpscenter" ? MemPilot.Models.ProcessCategory.Bloatware : MemPilot.Models.ProcessCategory.ThirdPartyApp
        };
        MemPilot.Services.WorkloadClassifier.Apply(p);
        Assert.NotEqual(MemPilot.Models.WorkloadKind.Work, p.Workload);
    }

    [Fact]
    public void ExactShortNames_StillWork()
    {
        var p = new MemPilot.Models.ProcessInfo { Pid = 1, Name = "wps", Category = MemPilot.Models.ProcessCategory.ThirdPartyApp };
        MemPilot.Services.WorkloadClassifier.Apply(p);
        Assert.Equal(MemPilot.Models.WorkloadKind.Work, p.Workload);
    }
}
