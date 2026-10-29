using MemPilot.Services;
using Xunit;

namespace MemPilot.Tests;

public class HabitProfileTests
{
    private const long Gb = 1024L * 1024 * 1024;

    private static UsageSample S(int hour, int minute, double availGb, int day = 28)
        => new()
        {
            T = new DateTime(2026, 10, day, hour, minute, 0, DateTimeKind.Local).ToUniversalTime(),
            Total = 32 * Gb,
            Avail = (long)(availGb * Gb),
        };

    [Fact]
    public void HoursAreClassifiedOnP25()
    {
        // 19/20 点长期紧张（4~6GB），14 点宽松（14~16GB），16 点偏紧（7~9GB）
        var samples = new List<UsageSample>();
        for (int day = 20; day <= 27; day++)
        {
            samples.Add(S(19, 5, 4 + day % 3, day));
            samples.Add(S(20, 5, 5, day));
            samples.Add(S(14, 5, 14 + day % 3, day));
            samples.Add(S(16, 5, 7 + day % 3, day));
        }

        var profile = HabitProfile.Build(samples);

        Assert.Equal(PressureLevel.Critical, profile.ForHour(19)!.Level);
        Assert.Equal(PressureLevel.Critical, profile.ForHour(20)!.Level);
        Assert.Equal(PressureLevel.Roomy, profile.ForHour(14)!.Level);
        Assert.Equal(PressureLevel.Tight, profile.ForHour(16)!.Level);
        Assert.Null(profile.ForHour(3)); // 无样本的小时不编造
        Assert.Equal(32, profile.SampleCount);
    }

    [Fact]
    public void Describe_GroupsConsecutiveTightHours()
    {
        var samples = new List<UsageSample>();
        for (int day = 20; day <= 27; day++)
        {
            samples.Add(S(19, 5, 4, day));
            samples.Add(S(20, 5, 4, day));
        }
        var text = HabitProfile.Build(samples).Describe();
        Assert.Contains("19-21 点", text);
        Assert.Contains("常态偏紧", text);
    }

    [Fact]
    public void EmptyHistory_ProducesEmptyProfile()
    {
        var profile = HabitProfile.Build(Array.Empty<UsageSample>());
        Assert.Empty(profile.Hours);
        Assert.Equal("", profile.Describe());
    }
}
