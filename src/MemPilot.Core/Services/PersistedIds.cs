namespace MemPilot.Services;

/// <summary>
/// 稳定标识（v0.10.0）：持久化配置里**绝不能存显示文本**。
///
/// 为什么（开源前必须解决的问题）：旧实现把「内存增长」「线程数」这些**中文表头**直接当
/// `ProcessHiddenColumns` 的 key，把「工作/娱乐」当 `WorkloadFilter` 的 key 落进 config.json。
/// 一旦加英文界面，这些 key 全部对不上 → 用户辛苦调好的列设置和筛选被静默重置。
///
/// 现在：config 里只存这里的 id；显示文本由 UI 层按当前语言渲染。
/// 迁移在 <see cref="ConfigStore"/> 加载时一次性完成，解析不出来的原值保留不丢。
/// </summary>
public static class PersistedIds
{
    /// <summary>进程页可隐藏列的稳定 id（与 DataGrid 表头一一对应）。</summary>
    public static class Column
    {
        public const string MemoryGrowth = "memory_growth";
        public const string ChildCount = "child_count";
        public const string Threads = "threads";
        public const string WindowTitle = "window_title";
        public const string Vendor = "vendor";
        public const string Audio = "audio";
        public const string ModeKeep = "mode_keep";
        public const string IsProtected = "protected";
        public const string Signature = "signature";
        public const string CategoryReason = "category_reason";
        public const string GpuMemory = "gpu_memory";
        public const string Commit = "commit";
        public const string PrivateWorkingSet = "private_working_set";
        public const string Handles = "handles";
        public const string TcpConnections = "tcp";
        public const string Program = "program";
        public const string Workload = "workload";
        public const string Category = "category";
        public const string Cpu = "cpu";
        public const string WorkingSet = "working_set";

        /// <summary>旧版（中文表头）→ 稳定 id。只用于一次性迁移。</summary>
        public static readonly IReadOnlyDictionary<string, string> LegacyMap =
            new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["内存增长"] = MemoryGrowth,
                ["子进程"] = ChildCount,
                ["线程数"] = Threads,
                ["窗口标题"] = WindowTitle,
                ["厂商"] = Vendor,
                ["音频"] = Audio,
                ["模式保留"] = ModeKeep,
                ["签名"] = Signature,
                ["分类依据"] = CategoryReason,
                ["保护"] = IsProtected,
                ["显存"] = GpuMemory,
                ["提交"] = Commit,
                ["私有工作集"] = PrivateWorkingSet,
                ["句柄"] = Handles,
                ["TCP"] = TcpConnections,
                ["所属程序"] = Program,
                ["用途"] = Workload,
                ["分类"] = Category,
                ["CPU"] = Cpu,
                ["内存"] = WorkingSet,
            };

        public static readonly ISet<string> All = new HashSet<string>(StringComparer.Ordinal)
        {
            MemoryGrowth, ChildCount, Threads, WindowTitle, Vendor, Audio, ModeKeep, IsProtected,
            Signature, CategoryReason, GpuMemory, Commit, PrivateWorkingSet, Handles,
            TcpConnections, Program, Workload, Category, Cpu, WorkingSet
        };
    }

    /// <summary>用途筛选的稳定 id。</summary>
    public static class Workload
    {
        public const string Work = "work";
        public const string Entertainment = "entertainment";
        public const string Ai = "ai";
        public const string System = "system";
        public const string Other = "other";
        public const string Unknown = "unknown";

        public static readonly IReadOnlyDictionary<string, string> LegacyMap =
            new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["工作"] = Work,
                ["娱乐"] = Entertainment,
                ["AI"] = Ai,
                ["系统"] = System,
                ["其他"] = Other,
                ["未知"] = Unknown,
            };

        public static readonly ISet<string> All = new HashSet<string>(StringComparer.Ordinal)
        { Work, Entertainment, Ai, System, Other, Unknown };
    }

    /// <summary>
    /// 迁移列名列表：已知中文 → id；已经是 id 的原样保留；无法识别的**保留原值**（不丢）。
    /// 返回 (新列表, 是否发生变化)。
    /// </summary>
    public static (List<string> columns, bool changed) MigrateColumns(IEnumerable<string>? legacy)
    {
        var result = new List<string>();
        bool changed = false;
        foreach (var raw in legacy ?? Enumerable.Empty<string>())
        {
            if (string.IsNullOrWhiteSpace(raw)) continue;
            if (Column.All.Contains(raw)) { result.Add(raw); continue; }
            if (Column.LegacyMap.TryGetValue(raw, out var id)) { result.Add(id); changed = true; continue; }
            result.Add(raw);   // 未知值保留，绝不因为迁移而丢掉用户的设置
        }
        return (result.Distinct(StringComparer.Ordinal).ToList(), changed);
    }

    /// <summary>
    /// 迁移用途筛选值：中文 → id；空串（全部）保持不变；无法识别的返回 null 交给调用方保留原值。
    /// </summary>
    public static (string value, bool changed) MigrateWorkloadFilter(string? legacy)
    {
        var raw = (legacy ?? "").Trim();
        if (raw.Length == 0) return ("", false);
        if (Workload.All.Contains(raw)) return (raw, false);
        if (Workload.LegacyMap.TryGetValue(raw, out var id)) return (id, true);
        return (raw, false);   // 未知值保留
    }
}
