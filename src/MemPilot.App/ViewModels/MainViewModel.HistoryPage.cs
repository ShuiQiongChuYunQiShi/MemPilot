using System;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using MemPilot.Helpers;
using MemPilot.Models;
using MemPilot.Services;

namespace MemPilot.ViewModels;

/// <summary>历史页：采样聚合、趋势点集与三个统计行类型（读取放后台，10s 内复用）。</summary>
public partial class MainViewModel
{
    // ---- usage history (历史统计) ----
    public ObservableCollection<ModeUsageRow> ModeUsageItems { get; } = new();
    public ObservableCollection<TopUsageRow> TopUsageItems { get; } = new();
    public ObservableCollection<ActionUsageRow> ActionUsageItems { get; } = new();
    public ObservableCollection<GpuHistoryRow> GpuUsageItems { get; } = new();
    public ObservableCollection<AiSessionRow> AiSessionItems { get; } = new();
    private string _historySummary = "";
    public string HistorySummary { get => _historySummary; private set { _historySummary = value; OnPropertyChanged(); } }

    private string _gpuHistorySummary = "（还没有显存采样——右侧状态确认采集是否可用）";
    public string GpuHistorySummary { get => _gpuHistorySummary; private set { _gpuHistorySummary = value; OnPropertyChanged(); } }

    public sealed class ModeUsageRow
    {
        public string Mode { get; set; } = "";
        public double Hours { get; set; }
        public string Display => $"{Mode}    {Hours:F1} 小时";
    }
    public sealed class TopUsageRow
    {
        public string Name { get; set; } = "";
        public double AvgWsMb { get; set; }
        public double MaxWsMb { get; set; }
        public double AvgCpu { get; set; }
        public double Threads { get; set; }
        public int Seen { get; set; }
    }
    public sealed class ActionUsageRow
    {
        public string Action { get; set; } = "";
        public int Count { get; set; }
    }
    /// <summary>AI 会话段（v0.5.0，历史页显存卡片下方）。</summary>
    public sealed class AiSessionRow
    {
        public string StartText { get; set; } = "";
        public string DurationText { get; set; } = "";
        public string PeakText { get; set; } = "";
        public string DominantText { get; set; } = "";
        public int PidCount { get; set; }
    }

    private DateTime _lastHistoryLoadUtc = DateTime.MinValue;

    public void LoadHistory()
    {
        // 页签切换不能读盘算图：数据读取放后台，UI 只做点集/行的落位。
        // 只保留 0.8 秒防连击（TabControl 可能连发两次 SelectionChanged）；
        // 原来是 10 秒节流——实测会静默跳过，用户看到的是"点了没反应"，已去掉。
        if ((DateTime.UtcNow - _lastHistoryLoadUtc).TotalMilliseconds < 800) return;
        _lastHistoryLoadUtc = DateTime.UtcNow;
        if (string.IsNullOrEmpty(_historySummary)) HistorySummary = "正在统计历史数据…";
        Task.Run(() =>
        {
            var day = Usage.ReadWindow(TimeSpan.FromHours(24));
            var week = Usage.ReadWindow(TimeSpan.FromDays(7));
            var habit = HabitProfile.Build(week);
            var gpu = GpuMonitor.ReadWindow(TimeSpan.FromHours(24)); // 显存采样（30 秒一条，单独文件）
            var since = DateTime.UtcNow - TimeSpan.FromDays(7);
            var actions = Audit.Recent(2000).Where(a => a.TimestampUtc >= since)
                               .GroupBy(a => a.Action).OrderByDescending(g => g.Count())
                               .Select(g => new ActionUsageRow { Action = g.Key, Count = g.Count() }).ToList();
            return (day, week, habit, actions, gpu);
        }).ContinueWith(t =>
        {
            // 失败必须说出来：之前是静默 return，用户只能看到一张空图
            if (t.IsFaulted)
            {
                var ex = t.Exception?.GetBaseException();
                System.Windows.Application.Current.Dispatcher.InvokeAsync(() =>
                    HistorySummary = "历史统计失败：" + (ex?.Message ?? "未知错误"));
                return;
            }
            if (t.Result.day == null) return;
            var (day, week, habit, actions, gpu) = t.Result;
            System.Windows.Application.Current.Dispatcher.InvokeAsync(() =>
        {
            var memPts = new System.Windows.Media.PointCollection();
            var memArea = new System.Windows.Media.PointCollection();
            if (day.Count > 1 && day[0].Total > 0)
            {
                for (int i = 0; i < day.Count; i++)
                {
                    double frac = (double)(day[i].Total - day[i].Avail) / day[i].Total; // v0.3.5：占用率（越高越紧张）
                    double x = i * (640.0 / Math.Max(1, day.Count - 1));
                    double y = 150 - frac * 140;
                    memPts.Add(new System.Windows.Point(x, y));
                    memArea.Add(new System.Windows.Point(x, y));
                }
                if (memPts.Count > 1)
                {
                    memArea.Add(new System.Windows.Point(640, 160));
                    memArea.Add(new System.Windows.Point(0, 160));
                }
            }
            HistoryPoints = memPts;          // 整体替换（见属性注释：Clear+Add 不会让 Shape 重建几何）
            HistoryAreaPoints = memArea;
            // mode usage over 7 days（week 已在后台线程读好：之前这里又同步读一次盘，与"页签切换不读盘"矛盾）
            ModeUsageItems.Clear();
            foreach (var g in week.GroupBy(s => s.Mode).OrderByDescending(g => g.Sum(s => (double)s.Interval)))
            {
                ModeUsageItems.Add(new ModeUsageRow { Mode = g.Key, Hours = g.Sum(s => (double)s.Interval) / 3600.0 });
            }
            // top processes over 24h
            TopUsageItems.Clear();
            var byName = new Dictionary<string, (long sumWs, long maxWs, double sumCpu, long sumThreads, int n)>();
            foreach (var s in day)
                foreach (var tp in s.Top)
                {
                    var cur = byName.TryGetValue(tp.N, out var v) ? v : (0, 0, 0.0, 0L, 0);
                    byName[tp.N] = (cur.sumWs + tp.W, Math.Max(cur.maxWs, tp.W), cur.sumCpu + tp.C, cur.sumThreads + tp.T, cur.n + 1);
                }
            foreach (var kv in byName.OrderByDescending(kv => kv.Value.sumWs).Take(12))
            {
                var v = kv.Value;
                TopUsageItems.Add(new TopUsageRow
                {
                    Name = kv.Key,
                    AvgWsMb = v.sumWs / v.n / 1048576.0,
                    MaxWsMb = v.maxWs / 1048576.0,
                    AvgCpu = Math.Round(v.sumCpu / v.n, 1),
                    Threads = Math.Round((double)v.sumThreads / v.n, 0),
                    Seen = v.n
                });
            }
            // action counts over 7 days from the audit trail (read off-thread in LoadHistory)
            ActionUsageItems.Clear();
            foreach (var a in actions) ActionUsageItems.Add(a);

            // ---- 显存（v0.3.4）：曲线 + 大户表 + 摘要 ----
            var gpuHist = GpuHistory.Build(gpu);
            var gpuPts = new System.Windows.Media.PointCollection();
            var gpuArea = new System.Windows.Media.PointCollection();
            if (gpuHist.Points.Count > 1)
            {
                for (int i = 0; i < gpuHist.Points.Count; i++)
                {
                    double frac = Math.Clamp(gpuHist.Points[i].Percent / 100.0, 0, 1);
                    double x = i * (640.0 / Math.Max(1, gpuHist.Points.Count - 1));
                    double y = 150 - frac * 140;
                    gpuPts.Add(new System.Windows.Point(x, y));
                    gpuArea.Add(new System.Windows.Point(x, y));
                }
                gpuArea.Add(new System.Windows.Point(640, 160));
                gpuArea.Add(new System.Windows.Point(0, 160));
            }
            GpuHistoryPoints = gpuPts;
            GpuHistoryAreaPoints = gpuArea;
            GpuUsageItems.Clear();
            foreach (var row in gpuHist.Top) GpuUsageItems.Add(row);

            // 会话段：最近 10 段，新→旧；最后一段若无 EndUtc 标「进行中」
            AiSessionItems.Clear();
            foreach (var session in gpuHist.Sessions.AsEnumerable().Reverse().Take(10))
            {
                var minutes = (session.EndUtc ?? DateTime.UtcNow) - session.StartUtc;
                AiSessionItems.Add(new AiSessionRow
                {
                    StartText = session.StartUtc.ToLocalTime().ToString("MM-dd HH:mm"),
                    DurationText = $"{minutes.TotalMinutes:F0} 分钟" + (session.EndUtc.HasValue ? "" : "（进行中）"),
                    PeakText = $"{session.PeakUsedBytes / 1073741824.0:F1} GB",
                    DominantText = session.DominantName,
                    PidCount = session.PidCount
                });
            }
            GpuHistorySummary = gpuHist.SampleCount == 0
                ? "（还没有显存采样——工具运行 30 秒后出现第一条；若一直为空说明 GPU 计数器不可用）"
                : $"24 小时 {gpuHist.SampleCount} 条采样"
                  + (gpuHist.PeakUsed.HasValue ? $" · 峰值 {ByteFormat.Human(gpuHist.PeakUsed.Value)}" : "")
                  + (gpuHist.LastUsed.HasValue && gpuHist.Total > 0
                        ? $" · 最近 {ByteFormat.Human(gpuHist.LastUsed.Value)} / {ByteFormat.Human(gpuHist.Total)}（{gpuHist.LastUsed.Value * 100.0 / gpuHist.Total:F0}%）"
                        : "")
                  + (gpuHist.SpikeCount > 0 ? $" · 跳增 {gpuHist.SpikeCount} 次" : "")
                  + (string.IsNullOrEmpty(gpuHist.Source) ? "" : $" · 来源 {gpuHist.Source}");
            int maxGapMin = 0;
            for (int i = 1; i < day.Count; i++)
                maxGapMin = Math.Max(maxGapMin, (int)(day[i].T - day[i - 1].T).TotalMinutes);
            HistorySummary = (day.Count == 0
                ? "（还没有采样数据——工具运行 5 分钟后会出现第一个采样点）"
                : $"24 小时 {day.Count} 个采样 · 最早 {day[0].T.ToLocalTime():HH:mm} · 进程数 {day[0].Procs} → {day[^1].Procs} · 最近可用 {ByteFormat.Human(day[^1].Avail)}")
                + (maxGapMin >= 30 ? $" · 最长 {maxGapMin} 分钟未运行（睡眠/退出）" : "")
                + (habit.Hours.Count > 0 ? " ｜ " + habit.Describe() : "");
            });
        });
    }

    // v0.3.6：图表点集必须**整体替换**（new PointCollection）而不是 Clear+Add。
    // 实测踩坑：共享 PointCollection 变异后 Polyline 的 RenderedGeometry 仍是空的（ActualWidth=0），
    // 曲线点集有 231 个点却画不出来；换成新实例 + PropertyChanged 后立即正常。
    // 这与项目既有规则「集合必须整体替换，不许 Clear+Add」同源。
    private System.Windows.Media.PointCollection _memoryTrendPoints = new();
    public System.Windows.Media.PointCollection MemoryTrendPoints
    {
        get => _memoryTrendPoints;
        private set { _memoryTrendPoints = value; OnPropertyChanged(); }
    }
    private System.Windows.Media.PointCollection _memoryTrendAreaPoints = new();
    public System.Windows.Media.PointCollection MemoryTrendAreaPoints
    {
        get => _memoryTrendAreaPoints;
        private set { _memoryTrendAreaPoints = value; OnPropertyChanged(); }
    }
    private System.Windows.Media.PointCollection _historyPoints = new();
    public System.Windows.Media.PointCollection HistoryPoints
    {
        get => _historyPoints;
        private set { _historyPoints = value; OnPropertyChanged(); }
    }
    private System.Windows.Media.PointCollection _historyAreaPoints = new();
    public System.Windows.Media.PointCollection HistoryAreaPoints
    {
        get => _historyAreaPoints;
        private set { _historyAreaPoints = value; OnPropertyChanged(); }
    }
    private System.Windows.Media.PointCollection _gpuHistoryPoints = new();
    public System.Windows.Media.PointCollection GpuHistoryPoints
    {
        get => _gpuHistoryPoints;
        private set { _gpuHistoryPoints = value; OnPropertyChanged(); }
    }
    private System.Windows.Media.PointCollection _gpuHistoryAreaPoints = new();
    public System.Windows.Media.PointCollection GpuHistoryAreaPoints
    {
        get => _gpuHistoryAreaPoints;
        private set { _gpuHistoryAreaPoints = value; OnPropertyChanged(); }
    }
    private readonly List<(DateTime t, double usedFrac)> _memSamples = new();

    private string _searchText = "";
    public string SearchText { get => _searchText; set { _searchText = value; OnPropertyChanged(); ApplyFilter(); } }

    private void UpdateTrend(SystemMemoryInfo mem)
    {
        // v0.3.5：曲线口径统一为"占用率"（越高越紧张）——与进度条、显存曲线、任务管理器一致。
        // 可用内存仍是摘要里的可执行数字，只是不再当曲线纵轴。
        double usedFrac = mem.TotalPhysicalBytes <= 0
            ? 0
            : (double)(mem.TotalPhysicalBytes - mem.AvailablePhysicalBytes) / mem.TotalPhysicalBytes;
        _memSamples.Add((DateTime.UtcNow, usedFrac));
        if (_memSamples.Count > 60) _memSamples.RemoveAt(0);
        var trendPts = new System.Windows.Media.PointCollection();
        var trendArea = new System.Windows.Media.PointCollection();
        if (_memSamples.Count > 1)
        {
            double maxV = _memSamples.Max(s => s.usedFrac);
            double minV = _memSamples.Min(s => s.usedFrac);
            double range = Math.Max(0.0001, maxV - minV);
            for (int i = 0; i < _memSamples.Count; i++)
            {
                // canvas on the home page is 260x64 — scale to it, or the line gets clipped
                double x = i * (260.0 / Math.Max(1, _memSamples.Count - 1));
                double y = 60 - (_memSamples[i].usedFrac - minV) / range * 54;
                trendPts.Add(new System.Windows.Point(x, y));
                trendArea.Add(new System.Windows.Point(x, y));
            }
            if (trendPts.Count > 1)
            {
                trendArea.Add(new System.Windows.Point(260, 64));
                trendArea.Add(new System.Windows.Point(0, 64));
            }
        }
        MemoryTrendPoints = trendPts;       // 整体替换（同图表点集规则）
        MemoryTrendAreaPoints = trendArea;
    }
}
