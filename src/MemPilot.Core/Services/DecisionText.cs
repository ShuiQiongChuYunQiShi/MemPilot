using System.Globalization;
using MemPilot.Models;

namespace MemPilot.Services;

/// <summary>
/// 决策日志原因的双语渲染（v0.22.0）。
///
/// 背景：引擎写入的 Reason 一直是中文自由文本（README 的 Known limits），
/// 英文用户只能看 Kind/Target，看不懂"为什么跳过"。这里给新记录加稳定 code +
/// 参数，UI 与导出按当前语言渲染；旧记录（无 code）回退中文原文。
///
/// 约定：Args 只放**已格式化**的字符串（模板用 {0}/{1}，不做数字格式化），
/// 这样 decisions.json 里不会出现类型信息。
/// </summary>
public static class DecisionText
{
    /// <summary>code → 资源键（`junk_advice.suggested` → `Decision_junk_advice_suggested`）。</summary>
    public static string KeyOf(string code) => "Decision_" + code.Replace('.', '_').Replace('-', '_');

    /// <summary>
    /// 本地化渲染：有 code 且模板存在 → 当前语言；否则回退原 Reason。
    /// resolver 默认用 <see cref="Localization.UiStrings.Resolver"/>（App 启动时注入）；
    /// 单测可传入自己的字典来验证双语。
    /// </summary>
    public static string Render(DecisionEntry entry, Func<string, string?>? resolver = null)
    {
        if (entry == null) return "";
        if (!string.IsNullOrEmpty(entry.Code))
        {
            var r = resolver ?? Localization.UiStrings.Resolver;
            if (r != null)
            {
                string? template;
                try { template = r(KeyOf(entry.Code)); }
                catch { template = null; }

                if (!string.IsNullOrEmpty(template))
                {
                    var args = entry.Args ?? Array.Empty<string>();
                    if (args.Length == 0) return template!;
                    try { return string.Format(CultureInfo.CurrentCulture, template!, args.Cast<object?>().ToArray()); }
                    catch { return template!; }
                }
            }
        }
        return entry.Reason;
    }
}
