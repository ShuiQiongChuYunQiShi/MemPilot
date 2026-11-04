# Changelog

All notable changes to MemPilot are documented here.
Format follows [Keep a Changelog](https://keepachangelog.com/en/1.1.0/);
versioning is `0.y.z` while the project is pre-1.0.

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
