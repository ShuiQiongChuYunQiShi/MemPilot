# Changelog

All notable changes to MemPilot are documented here.
Format follows [Keep a Changelog](https://keepachangelog.com/en/1.1.0/);
versioning is `0.y.z` while the project is pre-1.0.

## [0.12.0] — UX overhaul round 2 (polish)

### Changed
- **Theme tokens**: the 10 hardcoded hex colors scattered in MainWindow.xaml
  (nav hover, grid header, gridlines, notice banner, chart fills, timestamps)
  are now semantic brushes in App.xaml — dark theme becomes a token-swap instead
  of a hunt. Dead/duplicate tokens removed. One guard test caught a real crash
  during this change (`DropShadowEffect.Color` cannot take a brush — it needs a
  `<Color>` resource).
- **Context menu regrouped**: the process grid's 17 flat items are now 4 groups
  (处置 / 内存策略 / 标记 / 线程) with disabled group headers, using the unified
  terminology.
- **Column settings menu grouped**: 12 flat items → 标识 / 内存 / 显存 / 证据.
- **Navigation regrouped**: 反馈记录 moved from 观测 to 高级 (it is user data +
  rule maintenance, not observation); emoji separators normalized; dead comment
  removed.
- **Decisions page Kind column** is now Chinese (`GrowthWatch` → 内存增长观察,
  15 kinds mapped in `UiTerms.OfKind`); the process toolbar wraps instead of
  overflowing; the home page no longer shows the same two counts twice
  (日常维护 card now explains what the action does instead).

## [0.11.0] — UX overhaul round 1

A full UI/interaction/notification audit produced this batch: 10 layout/logic
defects fixed, terminology unified, first progress indication, non-modal
safety confirmation, keyboard support, and empty states.

### Fixed (layout & logic)
- **Action feedback no longer vanishes.** The refresh loop overwrote the status
  bar twice per tick (≤10 s), erasing every action result ("marked", "restored
  N processes", errors). The status bar is now two-track: action feedback is
  kept for 60 s and shown emphasized; refresh noise is secondary.
- **Home hero card clipped at 1280 px** (52 px percent figure in a fixed
  horizontal row + fixed 240 px GPU column). Detail text now wraps, GPU column
  is bounded `220–300 px`.
- **Make-room controls overflowed** (2 inputs + 3 checkboxes + 2 labels ≈
  750 px in one row). Split into target / options / actions rows.
- Settings-page "select all" skipped `ChkCombinedMemory` (checked 8 of 9).
- Settings page had a card nested inside a card (program catalog inside online
  lookup) — separated.
- `DgHeader` style set `BorderBrush` twice (hardcoded value silently won).
- `PlanGrid ItemsSource="{Binding}"` was a dead binding.
- One-click trim wrote its result into a control on a *different* page.
- Window has `MinWidth=1000 MinHeight=640` (was unbounded — every fixed-width
  layout broke when resized small).
- Raw UTC timestamps on the suspend/decisions/audit pages now render local
  time (`TimeText`).

### Changed
- **Terminology unified** (`UiTerms`): one name per concept — 腾内存 (page),
  回收工作集 (L1) / 挂起 (L2) / 优雅关闭 (L3) / 强制结束 (L4),
  提交余量 (commit free), 保留 vs 保护. Retired: 腾挪, 一键优化, 压缩工作集,
  提交空闲, 需保留, KeepPatterns/CleanPatterns as visible labels.
  Guard tests fail the build if a retired term reappears in UI text.
- **Hotkey (CTRL+SHIFT+M) no longer auto-builds an 8 GB plan** — it shows the
  page and focuses the input. Plan generation is explicit.
- **Make-room results are kept** after execution: each row gets a 结果
  (result) column; the table is only cleared by the 清空 button. Previously
  the plan grid was wiped on completion — reviewing what ran required the
  audit page.
- **Status bar progress bar** (the first in the app): shown only during long
  operations (make-room execution, sweeps, batch actions) with item counts
  ("腾内存 3/12") — not during the 10-second refresh, which would have been
  new flicker.

### Added
- **Non-modal commit-relief confirmation card** on the home page replaces the
  modal MessageBox that could pop at any moment when commit free dropped below
  5%. Same decision (graceful close of idle background processes, never
  forced), same safety — but it no longer steals focus, has a 30-minute
  cooldown, and when the user is away (tray mode) the request is declined and
  logged instead of blocking the engine.
- **Notification policy**: tray balloons are rate-limited per title (30 min
  cooldown); "settings saved" and "trim finished" balloons removed (results
  are visible in the status bar / audit). Balloons remain for memory alerts,
  lease erosion alerts, mode switches, and user-initiated long operations.
- **Keyboard support**: `Esc` cancels make-room execution / clears search,
  `F5` refreshes, `Ctrl+F` jumps to the process search box, `Enter` in the
  make-room input builds the plan.
- **Empty states** on the process / cleanup / decisions / audit grids — cold
  start now explains what is happening and what is normal ("observation period
  not full yet is expected") instead of a blank grid.

## [0.10.0] — open-source preparation

### Fixed
- **VRAM numbers contradicted each other.** `IDXGIAdapter3::QueryVideoMemoryInfo`
  returns the *calling process'* usage, not the adapter total. The home card
  showed e.g. "0.2 GB / 12 GB (2%)" next to the real PDH figure "9.4 GB (46%)".
  Adapter usage now comes only from PDH; the DXGI value is kept as a labelled
  diagnostic and never displayed as adapter-wide usage.
- **The decision log claimed actions that never ran.** Without admin rights
  `NtSetSystemInformation` fails with `STATUS_PRIVILEGE_NOT_HELD`, but the old
  code discarded the result and still logged `执行` (executed) with "freed 0 MB".
  It now checks `IsAdmin()` first and logs `跳过` (skipped) with the reason.
- **A busy port crashed first launch.** The lease API's `TcpListener.Start()`
  was uncaught; a SocketException propagated into `async void Loaded` and raised
  a modal "unhandled exception" dialog for a feature the user never enabled.
  `Start()` now returns `false` and records `LastError`; the settings page shows
  why the listener is not running.
- **User feedback could strip service protection.** A feedback hit returned
  early, skipping the service/driver checks — marking a Windows service
  "cleanable" set `IsService=false` and defeated the service exclusions in the
  unused detector. Hard-safety classification now runs before feedback; marks
  are recorded but cannot override protection.
- **Dead UI.** The "enable system memory auto-optimization" checkboxes were
  wired to a method that was never called (`AutoOptimize` had been superseded by
  commit relief). The placebo controls and the dead method are removed.

### Changed
- **Safer defaults for new installs**: lease API listener and automatic commit
  relief now default to **off** (existing `config.json` files are unaffected —
  the keys already exist). The tool observes by default; you opt into actions.
- **First-run notice** on the home page: what runs on its own (nothing by
  default), what needs admin, where data lives. Dismissible, non-modal.
- **Tray icon promotion** (writing `NotifyIconSettings\IsPromoted`) now only
  happens when tray memory display is actually enabled.
- **Persisted settings use stable ids.** `ProcessHiddenColumns` and
  `WorkloadFilter` stored Chinese display text; adding a UI language would have
  silently reset them. Keys are now `memory_growth` / `work` / … with an
  idempotent one-time migration that preserves unknown values.

### Added
- `LICENSE` (Apache-2.0), `NOTICE`, `SECURITY.md`, `CONTRIBUTING.md`,
  `CODE_OF_CONDUCT.md`, `docs/ARCHITECTURE.md`.
- Guard tests for the three correctness fixes above plus the conservative
  defaults (`OpenSourceReadinessTests`).

## [0.9.2] — 2026-11-04

### Fixed
- Safety consolidation: six copies of the trim-safety check collapsed into one
  (`TrimSelection`); the weakest copy (`IsTrimmableSelection`, which ignored
  workload/audio/whitelist/min-age) is deleted, and batch trim now requires a
  pre-filtered candidate set.
- `DecisionLog` FIFO trimming destroyed the records it was supposed to keep
  (oldest-first cut the head, and the head *was* the important records —
  10 lease entries were evicted by 5,200 growth-watch entries). Trimming is now
  quota-first per kind, noise evicted before important.

### Changed
- `ProcessService` split (577 → 434 + 167) by responsibility: collection vs
  disposition primitives.
- Decision log raised to 5,000 entries with batched (1.5 s) writes and an
  explicit `Flush()` on shutdown.

## [0.9.1] — 2026-11-04

- Non-blocking commit-relief dialog (a modal `MessageBox` inside the engine tick
  could stall the whole refresh pipeline unattended).
- Batched audit writes (was: full 2,000-entry rewrite per action).
- Fail-safe window/audio enumeration: on failure, processes are treated as
  having windows/audio — never as "unused".

## [0.9.0] — 2026-11-04

Three-round retrospective: 8 real bugs fixed, noise reduction, first real
lease validation. Highlights:

- Route regression fixed: hotkey CTRL+SHIFT+M and the home "generate plan"
  button crashed 100% of the time since v0.6.0 (emoji route key vs stable tag).
- Junk sweep no longer silently force-kills windowless processes (was L4, now L3).
- Second-instance activation actually activates (`OpenExisting`).
- Lease API: atomic reserve → relief → complete, so a concurrent POST can no
  longer leak suspended processes that nobody tracks.
- `(pid, startTime)` recheck before every kill/suspend.
- `CleanWorkingSet` is fail-closed: requires an explicit PID allow-list.
- Disabling GPU sampling now ends the AI session instead of leaving it open forever.
- `ConfigStore.Save` snapshots instead of mutating live config under the UI.

## [0.8.0] — 2026-11-04

- Lease M2: **hard policy** — when the soft relief is short of the requested
  amount, suspend up to 8 "idle enough" processes (no window or idle ≥10 min);
  restore all of them on release/expiry. Verified end-to-end: 8 suspended →
  8/8 restored, guard alert fired on erosion.

## [0.7.0] — 2026-11-03

- Lease API M1: local HTTP (127.0.0.1 + Bearer token), single active lease,
  TTL, measured achievement (`achievedAvailableGb`), floor-breach alerts.

## [0.6.0] — 2026-11-03

- Navigation rebuilt: grouped sidebar, stable `Tag` routing, feedback demoted
  to a per-process action, threads page removed.

## [0.5.0] — 2026-11-02

- Commit-pressure relief (the insight that commit, not physical memory, is the
  real constraint — measured, not assumed).
- VRAM observation: per-process dedicated/shared via PDH, 30 s JSONL history,
  AI-session detection with N-of-M hysteresis.

## Earlier

See `git tag` / release notes. The project started as an internal tool
("MemoryTool") in October 2026 and was renamed to MemPilot at v0.3.x.
