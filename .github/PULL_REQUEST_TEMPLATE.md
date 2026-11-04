<!-- 描述改了什么、为什么、怎么验证。三类信息齐了 review 最快。 -->

## What

<!-- 一句话说明改动 -->

## Why

<!-- 证据：数据/日志/复现步骤。本项目惯例是"改动要有证据"。 -->

## Safety

<!-- 是否触及安全不变式（受保护/前台/音频/未知/L4/PID 复检）？没触及写"未触及"并说明为什么。 -->

## Verification

- [ ] `dotnet test tests/MemPilot.Tests/MemPilot.Tests.csproj` 全绿
- [ ] 新行为有测试守卫（行为可触发用行为断言；不可触发的用源码守卫，参考 `RegressionGuardTests.cs`）
- [ ] 真机冒烟过（如涉及 UI：截图或描述看到什么）
