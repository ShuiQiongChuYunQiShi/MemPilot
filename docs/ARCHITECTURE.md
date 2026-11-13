# Architecture

MemPilot is two .NET 10 projects with a one-way dependency:

```
MemPilot.App  (WPF shell, tray, 9 visible pages + 3 hidden-but-retained)
     │  only references
     ▼
MemPilot.Core (engine: classification, detection, disposition, observation)
```

`MemPilot.Core` has **zero UI dependencies** — it could be driven from a
console host, a service, or tests. That is deliberate: the engine's rules are
the dangerous part, and rules you cannot run headless are rules you cannot test.

There is no `.sln` in this repository. Every command names its `.csproj`
explicitly. This is intentional — it keeps the dependency graph honest
(`App → Core` and nothing else).

## Core services (the ones that matter)

| Service | Responsibility | Notable constraint |
|---|---|---|
| `ClassificationEngine` | Identity: exact-name rules, path fragments, WinVerifyTrust publisher rules, Steam library, program-catalog evidence | Hard-safety blocks (service/driver/anti-cheat/system-critical/security) run **before** user feedback, so a user mark can never strip protection |
| `UnusedDetector` | "Is this actually unused?" — age, N observations, window, CPU, IO, audio | Idle state is keyed by `(pid, startTime)`; enumeration failure is fail-safe (treated as "has window / has audio") |
| `TrimSelection` | The single source of truth for "who may be trimmed" | 12 exclusion dimensions; every batch action goes through it |
| `CleanupEngine` | Executes L1 trim / L2 suspend / L3 graceful close / L4 kill | Re-checks `(pid, startTime)` before any state change — PID reuse must not cause a wrong kill |
| `ResidentEngine` | The tick: scene scoring, auto actions, commit pressure, growth watch | Automatic paths never reach L4; fullscreen/AI-session gates |
| `CommitReliefPolicy` | Pure function deciding the commit-pressure response | v0.13: a failed purge escalates to the trim immediately (no cooldown for failures), plus the independent FloorTrim trigger (avail < 3 GB or commit margin < 10% → batch trim, logged as Target=FloorTrim) |
| `LeaseManager` / `LeaseApiService` | The lease contract over local HTTP | Atomic `TryReserve` → relief → `CompleteReservation`; single active lease; restore happens before the HTTP response |
| `GpuMonitor` / `GpuMemoryService` | VRAM observation and AI-session detection | Adapter usage comes **only** from PDH — DXGI's `QueryVideoMemoryInfo` is per-process and is stored separately as a diagnostic |
| `DecisionLog` | Every automatic decision, with reasons | Kind-bucketed quotas so `Lease`/`AiSession` records cannot be evicted by noise |
| `ConfigStore` | JSON config with DPAPI-protected API key | Saves a deep-copied snapshot under a lock; persisted keys use stable ids (`PersistedIds`); user-visible strings go through `UiTerms` |

## The action ladder

```
L1 trim working set      EmptyWorkingSet          safe, reversible by OS
L2 suspend process       NtSuspendProcess         atomic, registered, restorable
L3 graceful close        WM_CLOSE → wait          may escalate to L4 on timeout (manual paths only)
L4 force kill            TerminateProcess         manual only, double confirmation
```

Automatic paths cap at L1/L2. L3 requires per-item confirmation. L4 is never
automatic anywhere in the codebase, and a source-level test fails the build if
`JunkSweeper` ever emits an L4 again.

## Why some tests read source code

`tests/MemPilot.Tests/RegressionGuardTests.cs` contains assertions like
"this file must contain `IsStillSameProcess`". This is not elegant; it exists
because several real incidents shipped with green unit tests — the code
compiled, the tests passed, and the feature was broken in a way only visible
on a real machine (a route key that no longer matched a tab tag, a silently
swallowed enumeration failure, a purge whose result was discarded). Where a
behaviour cannot be triggered deterministically in CI, the guard asserts the
pattern that caused the incident cannot come back.

## Data layout

Everything lives under `%APPDATA%\MemPilot`:

```
config.json            settings (API key DPAPI-protected, stable-id keys)
decisions.json         decision log (5,000 cap, kind quotas)
audit.json             action audit with before/after measurements
history/samples-*.jsonl  5-minute system snapshots, 30-day retention
history/gpu-*.jsonl    30-second VRAM snapshots, 30-day retention
ai-session.json        open AI session state (survives restart; deleted on session end)
behavior.json          per-process behaviour ledger (capped at 1,000 names)
feedback.json          user marks (keep/cleanable/workload) — copy-on-write store
killed.json            recently disposed processes (for one-click relaunch)
lease-token.txt        lease API bearer token (same-user boundary, documented)
programs.json          program catalog cache (24 h TTL)
suspended.json         currently suspended processes (survives restart;
                       v0.15 adds ResumeAtUtc for timed suspend with auto-resume)
junk-advice.json       junk-suggestion learning ledger (dismiss/accept counts)
```

Nothing is written outside that directory (the exception is the optional
Windows event log audit).

## Release pipeline

`tools/publish-release.ps1` is the only path to a release: clean tree →
test gate → interactive confirmation → tag → `gh workflow run release`.
Tag pushes alone never publish — a gate with a bypass is not a gate.
`release.yml` builds both artifacts, optionally signs, writes `SHA256SUMS.txt`,
and appends verification instructions to the release notes.
