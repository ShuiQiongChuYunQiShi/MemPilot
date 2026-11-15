using Xunit;

namespace MemPilot.Tests;

/// <summary>
/// 本地化测试串行集合（v0.19.0）。
///
/// `UiStrings.Resolver` 是全局静态——多个测试类并行注入不同语言时互相覆盖
/// （与 v0.3.9 Diag 全局计数同一个坑）。凡是要注入/重置 Resolver 的测试类
/// 都必须挂这个 collection。
/// </summary>
[CollectionDefinition("Localization", DisableParallelization = true)]
public class LocalizationCollection
{
}
