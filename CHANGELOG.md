# Changelog

All notable changes to MemPilot are documented here.
Format follows [Keep a Changelog](https://keepachangelog.com/en/1.1.0/);
versioning is `0.y.z` while the project is pre-1.0.

## [Unreleased]

### Fixed
- **The lease API returned a spurious `400 bad_json` on roughly 1 in 3 requests.**
  `ReadRequestAsync` did a single `ReadAsync` and assumed headers and body arrived in
  the same TCP segment; when the body landed in a second segment it was dropped and
  deserialization failed — callers read that as "bad parameters" and gave up. Caught
  by running `tools/lease-demo.py` repeatedly against v0.22.0 (2 of the first 3 runs
  failed). The reader now accumulates bytes until `Content-Length` is satisfied and
  decodes the body as UTF-8 only once it is complete, so a multi-byte `requester`
  split across segments is no longer mangled either.

### Added
- **`409` now carries the holder, and `mem_lease` can wait.** A conflict used to be a dead
  end: the second caller got `{"error":"lease_active"}` and had to invent its own backoff.
  The response now includes `holder` (requester, `remainingSeconds`, achieved GB) and
  `retryAfterSeconds` — a quarter of the remaining time, capped at 10s, because the holder
  may release early. `mem_lease` takes `waitSeconds` (0–120) and retries in **the client**:
  a long-polled server-side request would leave leases granted to clients that already
  timed out, while a client-side retry stops when the process dies. Verified on a real
  machine: holder A released at t+5s, waiter B acquired at t+10s ("Waited 10s").
- **`plan.samplingWarm`.** CPU/IO usage comes from differencing two enumerations; the first one
  has no baseline, so most processes look "busy" and `wouldSuspend` comes back empty (observed
  for real: `excluded.inUse` went 1 → 62 right after a restart). Plans now report `samplingWarm`,
  and the MCP tool prepends a "retry in ~30s" note instead of letting a model conclude the machine
  can free nothing.
- **Default deny list for network infrastructure.** Suspending a proxy or VPN core
  (`FlClashCore`, `tailscaled`, `mihomo`, `sing-box`, …) cuts **all** network — including the
  caller's own LLM API connection. The caller then sees request timeouts and retries, never
  suspecting the memory tool. Those processes are windowless, idle and large, i.e. a perfect
  hard-lease candidate, so this happened on the first real `plan` run. The list is always
  active (merged with the caller's `denyNames`) and reported under `denied.defaultCount`;
  it is a default *preference*, not a safety rule, so it lives in the planner and not in
  `TrimSelection`.
- **`denyNames` (caller preference).** `POST /api/lease` and `POST /api/plan` accept a list
  of process names the caller does not want touched (exact, case-insensitive, trimmed).
  `plan` reports them under `denied` — deliberately separate from `excluded`, because
  "the caller said no" is not "the safety rules said no". Without this, a plan that lists
  `Weixin` left the caller only one option: don't run the job.
- **`POST /api/lease/{id}/renew`.** Long jobs used to be punished by their own lease: on TTL
  expiry every suspended process is restored immediately — exactly when the job needs that
  memory most. Renewing extends `expiresUtc` from the renewal moment and returns
  `renewCount` + `remainingSeconds`. `410 expired` (distinct from `404` "wrong id") means the
  lease is already dead, so the caller acquires instead of retrying. Renew does not re-run
  relief and does **not** reset the guard alert — otherwise a caller could renew its way past
  a real "memory was eroded" warning.
- **MCP server (`MemPilot.exe --mcp`).** Three tools over stdio JSON-RPC
  (`initialize` / `tools/list` / `tools/call`, hand-written, no new dependencies):
  `mem_status`, `mem_plan`, `mem_lease`. The server is a **client only** — it forwards to
  the local lease API, because the lease ledger and the restore hook live in the single
  long-running process; a second process with its own ledger is how you get processes
  suspended with nobody to resume them. `--mcp` is intercepted *before* the single-instance
  check, otherwise the second instance would just wake the GUI and exit.
- **`POST /api/plan` — zero-action preview.** Returns measured current memory, an
  upper-bound projection, the processes that *would* be suspended, and
  `excluded.byReason` counts (so "L4 never automatic / Unknown never automatic" is a number
  the caller can see, not a sentence in a README). `projectionBasis` says whether the
  number is `hard-upper-bound` or `current-only`. Returns `503 plan_unavailable` when no
  planner is wired instead of a fake all-zero plan. Writes `Kind=LeasePlan` with a 30-second
  dedupe and **no** share of the important-record quota.
- `GET /api/status` gained `capabilities` and `limits` (`minTargetGb`, `maxTtlSeconds`,
  `maxSuspendCount` — the last one reads `LeaseHardPlanner.MaxSuspendCount`, so the API and
  the scheduler cannot drift).
- `TrimSelection.ExplainUnsafe` — the safety filter now reports *why* a process was
  rejected (`caller`/`self`/`protected`/`category`/`workload`/`inUse`). `IsSafe` is a
  boolean projection of the same implementation, so there is still exactly one judgement
  (v0.9.2's convergence lesson).
- **`requesterPid` (safety).** A lease can name its caller; that process and its whole
  subtree are excluded from every reclaim candidate (trim and hard-suspend). An agent's
  worker is typically windowless and idle — exactly the hard-lease suspend profile — so
  without this a caller could freeze its own worker and wait forever. This is a hard
  invariant evaluated before the protection/category rules; omitting the field (`0`)
  keeps the old behaviour.
- `tools/lease-demo.py` — zero-dependency self-check that reads the token and port
  itself and asserts four claims: reachable, measured-not-promised, caller not
  disposed, every suspension restored.
- winget and scoop manifests (`manifests/`), with `mempilot` exposed on PATH so an
  agent can spawn it directly.
- `docs/SIGNING.md` — what the existing self-signed step does *not* solve, and the
  SignPath OSS path (the only route to a signature other machines trust).

### Fixed
- **MCP server deadlocked on its first HTTP request.** It ran on the WPF `OnStartup`
  thread, which carries a `DispatcherSynchronizationContext`; waiting synchronously on
  async HTTP (`GetAwaiter().GetResult()`) posted the continuation back to the blocked
  thread. Moved to `Task.Run` + `ConfigureAwait(false)`. Symptom: no output at all, then
  a timeout — the JSON-RPC handshake had already worked, which made it look like a
  transport bug.
- **MCP HTTP requests went through the system proxy.** `HttpClient` inherits the system
  proxy by default; with a local proxy configured but not running, `mem_status` hung until
  the 60-second timeout. Loopback requests now use `UseProxy = false`.
- **The lease API returned a spurious `400 bad_json` on roughly 1 in 3 requests.**
  `ReadRequestAsync` did a single `ReadAsync` and assumed headers and body arrived in
  the same TCP segment; when the body landed in a second segment it was dropped and
  deserialization failed — callers read that as "bad parameters" and gave up. Caught
  by running `tools/lease-demo.py` repeatedly against v0.22.0 (2 of the first 3 runs
  failed). The reader now accumulates bytes until `Content-Length` is satisfied and
  decodes the body as UTF-8 only once it is complete, so a multi-byte `requester`
  split across segments is no longer mangled either.

### Changed
- `docs/LEASE-API.md` documents `requesterPid`, `POST /api/plan`, and the new
  `capabilities`/`limits` fields (the status example had also drifted: it showed a
  `commitLimitGb` field that does not exist); both READMEs gained a "60 seconds" section
  built on the demo script and an MCP setup section.

## [0.22.0] — safety fixes, WSL hint, decision export, process-page polish

### Fixed
- **Cross-page selection leak (safety).** The process page's context menu reused the
  cleanup page's L1–L4 handlers, and `GetSelectedUnused()` fell back from one grid to
  the other. Right-clicking "force kill" on a process could target the *other* page's
  stale selection. The two pages now have page-scoped selectors, and right-click
  selects the row under the cursor first.
- **`ContainerVm` was not excluded from any action path.** `vmmemWSL` could be
  working-set-trimmed (evicting the whole WSL instance to disk) and was eligible for
  make-room L4. `TrimSelection`, `UnusedDetector` and `CleanupEngine.BuildPlan` now
  exclude it.
- **Slow-tick root cause.** `self-*.jsonl` showed the 1086/1090 ms spikes all in the
  enumeration stage, on an 11-minute cadence: `schtasks.exe` ran synchronously when
  its 10-minute cache expired. `GetScheduledTasksCached()` is now
  stale-while-revalidate (old list returned, refresh on the thread pool);
  `LookupUnknowns` no longer blocks the refresh (`Task.WaitAll(3s)` removed), and
  `RefreshTimings` gained a `LookupMs` segment to confirm it.
- **Sorting was lost on every refresh** (the collection is replaced wholesale). The
  last sort is now recorded and replayed; tree mode says why sorting is unavailable
  instead of silently ignoring it.
- Double-click and the tree glyph used the previous `SelectedItem` (double-clicking
  empty space opened Explorer for an unrelated process); both now resolve the row
  under the cursor and stop propagation.
- The process grid's default visible columns (1067 px) overflowed a 1280-wide window
  (1025 px available); the name column is now `*` with a 160 px minimum.
- Empty states: filtering to zero rows said "enumerating processes", and the decision
  log said "no entries yet" even when it was merely hiding noise. Both now explain
  the real state and offer a fix (clear filters / show noise).
- `Kind_LongResident` was missing from both dictionaries (the log showed the raw enum).
- Home page "sweep junk" reported results only to the cleanup page's hint line.

### Added
- **WSL resident hint** (on by default): when `vmmemWSL` holds ≥2 GB with no WSL
  activity today, the home page suggests `wsl --shutdown` with a copy button —
  hint only, never executed. One per day, with the same 7/30/90-day learning
  cooldown, and independent of the junk/long-resident advice slots.
- **Decision log**: Kind filter dropdown; CSV/JSON export with RFC 4180 escaping;
  reason templates with stable codes (15 high-frequency writers migrated; legacy
  rows fall back to the original Chinese text and are flagged `legacy` in JSON).
- **60-second commit check** on the home page: trims the safe subset and shows
  physical vs commit before/after side by side, stating plainly that commit staying
  flat is the expected outcome.
- **Unified ComboBox styling** (min-height 30, chip radius, real dark-theme colors,
  custom chevron) — the old style only overrode `Foreground`, leaving Aero2 chrome
  that looked foreign in the toolbar and was unreadable in dark mode.
- Search box placeholder + tooltip; Esc now clears the search when it has focus.
- Column-width persistence, "restore default columns", and a duplicated "VRAM"
  group header removed from the column menu.

### Changed
- `docs/README-zh.md` updated from v0.13.1 to v0.22.0 (bilingual UI, export, WSL
  hint, commit check); portable size documented as ~500 KB zip / ~440 KB extracted;
  `DocsGuardTests` now fails if the Chinese doc drifts back to an old release or the
  "English UI pending" claim.

## [0.21.0] — long-resident advice (suspend only, off by default)

### Added
- **Long-resident advice**: processes that have run for 8 h+, had zero window /
  audio / CPU activity today, and still hold ≥512 MB. This covers the case the
  11-23 log review surfaced — `vmmemWSL`, OEM services like `ArmouryCrate`,
  `MSPC` — processes with **no junk evidence** (so `JunkAdvisor` never touched
  them) that nevertheless hold memory for nothing. The home card offers
  **suspend 2 h / 8 h only**; there is no "end process" button, because these
  are not junk.
- Settings → Automation → long-resident advice (**off by default**). "Check now"
  falls back to this pool when the junk pool is empty.
- Shares the junk ledger's 7/30/90-day learning cooldown, and the two advice
  kinds are mutually exclusive — at most one advice card per day.

### Safety
- `vmmem*` is never suggested (suspending the VM memory host freezes
  WSL/Hyper-V); services, protected/system categories and `Unknown` are excluded
  by the shared `JunkSafetyFilter`, and so are bloatware and orphan-residue
  processes (those belong to `JunkAdvisor` / `JunkSweeper`).
- No behaviour ledger entry means no advice: a freshly started process is never
  mistaken for a long-resident one.

## [0.20.1] — decision log hint row was hidden behind the grid

### Fixed
- The decision log's hint row (and the new "Show advice/growth observations"
  checkbox) was covered by the `DataGrid`: the inner wrapper grid had no
  `Grid.Row`, so it landed in row 0 on top of the hint. The hint had been
  invisible since it was introduced; the checkbox made it a real dead end —
  noise could never be unfolded. The wrapper now occupies `Grid.Row="1"`.

## [0.20.0] — log review: no more junk-advice idle churn, clean ledger, stage timings

Driven by a review of 10-30 → 11-23 real usage logs (5,000 decisions, 754 audit
entries, 12 days of samples).

### Fixed
- **Junk advice ran an empty evaluation every 60 s while the window was hidden.**
  `JunkAdvice` was 87.7% of the decision log (4,387/5,000) and 2,190 of those were
  "user not present, card not shown" pairs. The engine now skips the whole
  evaluation when the shell reports no visible window (`UiActive`), and a failed
  delivery sets a persisted 15-minute retry cooldown (`JunkAdviceStore.RetryAfterUtc`)
  instead of retrying on every tick. The "advice" decision entry is now written
  by the view-model at the moment the card is actually shown, so the log no
  longer claims deliveries that never happened.
- **`suspended.json` kept ghost records forever.** `SuspendRegistry.LoadAndValidate`
  dropped records whose process had exited but never wrote the file back — two
  11-18 entries sat there for five days. The registry now saves after pruning.
- **Slow ticks (8–12 s) were unattributable.** `history/self-*.jsonl` only had the
  total duration. `SelfCostSample` now carries per-stage means
  (`EnumMs` / `ClassifyMs` / `GpuMs` / `UiMs`) so a slow tick points at
  enumeration, signature verification, PDH sampling or UI marshalling.
- **The decision log was 87% noise.** Advice and growth observations are now
  folded by default behind a "Show advice/growth observations" checkbox.
  Real actions that share the `JunkAdvice` kind (suspend / dismiss / automatic
  resume) are always visible — see `DecisionLogFilter`.
- `EnumerationIntegrationTests.RealEnumeration_AntiCheatPathsAreNeverGame` never
  actually classified the enumerated processes, so it had been passing
  vacuously; it now runs the real classification pipeline (caught by ACE
  anti-cheat appearing on the dev machine).

### Added
- `DecisionLogFilter` (Core, pure functions + tests) and
  `RefreshTimings` (per-refresh stage durations).

## [0.19.0] — bilingual UI (zh-CN / en) + pre-open-source cleanup

### Added
- **Full Chinese + English UI** via `ResourceDictionary` (`Strings.zh-CN.xaml` /
  `Strings.en.xaml`) with **runtime switching, no restart**: Settings → UI
  language (Follow system / 简体中文 / English). ~430 keys covering the 9
  sidebar pages, home/process/cleanable/make-room/suspended/decisions/history/
  settings, tray menu, mini bar, thread window, every confirmation dialog and
  the status bar.
- `UiStrings` (Core, UI-framework-free) with `Get` / `F` (formatted) / `TryGet`
  and a missing-key fallback that records a diagnostic instead of crashing.
- `LocalizationGuardTests`: both dictionaries must have identical key sets and
  no empty values; XAML must contain no bare Chinese attribute values; every
  `UiTerms` mapping resolves in both languages and falls back to the raw enum
  for unknown values.
- Category names converged: the two duplicated mappings
  (`CategoryTextConverter` short labels + `MainViewModel` full names) now live
  in `UiTerms.OfCategory` and are driven by the dictionaries.
- Classifier evidence (`CategoryReason`, 26 stable ids) and column-setting
  labels are localized; the process grid's "evidence" column no longer shows
  raw ids such as `access_denied_no_path`.

### Fixed
- **Column visibility silently broke in English**: the header→id map was keyed
  by Chinese display text. It is now keyed by resource key and resolved through
  the current language (`IdForHeader`), with frozen columns identified by
  `DisplayIndex` instead of the literal "名称".
- **Crash on switching language**: `Run.Text` binds TwoWay by default, so
  binding it to the read-only `MemoryLabel` threw during template load and left
  the window blank. Now explicit `Mode=OneWay`.
- `AGENTS.md` and `wharf.project.json` (internal tooling) are untracked and
  git-ignored; CONTRIBUTING/HANDOFF no longer claim an i18n skeleton exists.

### Changed
- Engine log text (`decisions.json` / `audit.json` free-form reasons) stays
  Chinese and is documented as a known limit; UI chrome is 100% bilingual.
- Test count 463; one test class collection serializes localization tests
  because `UiStrings.Resolver` is global static state.

### Tests
- 463 passing (LocalizationGuard 5, dictionary/XAML guards, category
  convergence, plus the adapted docs/structure/process-page guards).

## [0.18.0] — 清缓存成为正式能力；身份复检与调度架构整改

四视角锐评后的整改版本。第 1 条定调：**改为做清缓存**——不再以「不做清缓存」作定位，
而是承认能力、讲清它降什么不降什么，并把入口前移。其余七条经代码复盘后落地，其中两个
是复盘时新发现的严重缺陷。

### Fixed
- **进程永久挂起（P0，新发现）**：`IsStillSameProcess` 在 `StartTime == DateTime.MinValue`
  时放行，而挂起登记写进 `SuspendRegistry` 的正是这个未知值；到期恢复时 `IsAliveSame`
  必然比对失败，`ResumeRecord` 随即删除记账——进程永久挂起且不再重试。现在身份未知一律
  拒绝 L2 及以上动作，并在审计里写「无法确认进程身份，已跳过」。
- **复检与动作共用同一句柄（P0）**：旧实现按 PID 复检一次、再按 PID `OpenProcess` 一次，
  两次打开之间 PID 可能被复用（TOCTOU）。`OpenVerified` 一次打开 + `GetProcessTimes`
  校验启动时间，后续 `NtSuspendProcess` / `TerminateProcess` / `EmptyWorkingSet` 全部复用
  同一句柄；L3 升级强杀也用已验证句柄，避免 8 秒后按 PID 重开时杀错。
- **tick 逐阶段异常隔离（P0）**：`AfterRefresh` 旧实现一个 try 包住整条流水线，任一阶段
  抛出都会终止同 tick 的后续阶段——而「到期恢复挂起」排在队尾。现在每阶段独立 `RunStage`，
  失败记 `Diag` 并累计；连续 3 次失败向用户告警（30 分钟冷却），避免策略静默停摆。
- **事件回调不再击穿 tick**：`CommitReliefL3Suggested` 的订阅方抛异常会被捕获并记诊断。
- **审计不再被条数上限吃掉**：「每个自动决策都可审计」原来受 5,000 / 2,000 条 FIFO 限制，
  重度使用两三周后查不到历史。现在淘汰条目写入 `archive/{decisions,audit}-yyyyMM.jsonl`，
  默认保留 12 个月（`ArchiveRetentionMonths`），热窗口仍守原上限。
- **自身进程全链路排除**：`TrimSelection.IsSafe` 与 `JunkSafetyFilter` 补上
  `Environment.ProcessId` 判断——托盘模式下 MemPilot 自己满足「无窗口、低 CPU」，
  旧实现只靠 `TrimWorkingSets` 的 self 判断兜底。

### Added
- **5 秒轻探针 + 自适应全量频率**：定时器固定 5s 只读系统内存并处理到期恢复（微秒级、
  不枚举进程）；`ShouldFullRefresh` 裁决全量刷新——压力立即提频（最快 5s）、UI 可见按配置
  间隔、托盘且无压力降到 `max(60, 3×间隔)`。实测 89% 的样本没有压力，旧实现却每 10 秒
  枚举一次全部进程。
- **自身开销可观测**：`SelfCostRecorder` 每分钟聚合 tick P50/P95/Max、自身 RSS、自身 CPU%
  与阶段失败数，写 `history/self-yyyyMMdd.jsonl`（30 天），主页可见。调度内存的工具必须
  能自证不是负担。
- **「清缓存与工作集」入口前移**：「腾内存」页新增第 0 步卡片，主页「日常维护」加入口；
  设置页标题由「系统内存区域清理」改为「清缓存与工作集」，文案明确「仅物理占用，不降提交」。
- **操作审计并入决策日志**；「反馈记录」「模式」「操作审计」三页隐藏（代码保留、可配置恢复）。

### Changed
- README / docs/README-zh.md 的产品叙事改为「清缓存与降提交是两件事，两件都做」，
  不再以「不做清缓存」作为定位声明。
- `RefreshIntervalSeconds` 语义从「tick 间隔」改为「全量刷新间隔」；
  `SetIntervalSeconds` 不再改定时器周期（壳层改用 `Engine.UiActive` 上报可见性）。
- CI 的 Core 行覆盖门槛 50% → 60%（实测 66.5%）。

### Tests
- 456 项通过（新增 21 项用例）：身份复检 10、调度裁决 5、归档守恒 3、自身排除 3。

## [0.17.0] — junk advice actually reaches the user

One day of v0.16 data settled the question: the advice fired exactly once
(11-11 00:00, 8 candidates, 850 MB) and **was skipped because the app was in
tray mode** — while the 7-day per-name cooldown was already on disk. The
feature worked technically and failed product-wise.

### Fixed
- **Never suggests at midnight.** Evaluation now starts at 19:00 local ("unused
  all day" is only meaningful in the evening; at 00:00 every process is
  trivially "unused today").
- **Tray mode no longer burns the cooldown.** The engine emits; the VM records
  "suggested" only when the card is actually shown. In tray mode the VM logs
  the skip and calls `RejectJunkAdvice()` — the day's delivered-mark is rolled
  back so the next tick (window visible) can show it.
- **Daily mark is persisted** (`junk-advice.json` now `{AdviceDayKey, Entries}`
  with backward-compatible migration from the old bare array), so restarts do
  not re-suggest the same day.
- **An open window no longer counts as "unused".** A window streak longer than
  5 minutes rolls `LastActiveUtc` forward every tick (previously it was stamped
  only when the window closed — an all-day browser could be advised for closing),
  and the advisor additionally rejects any candidate with a visible window right
  now.
- **Window streaks are keyed by (name, pid)** — a short popup from a new PID no
  longer inherits the previous PID's start time and gets misclassified as
  "long window = real use".
- **Timed-suspend resume re-checks (pid, startTime)** (`ResumeRecord`): a record
  alive for hours can no longer resume an unrelated process after PID reuse.
  A failed resume is re-queued (+5 min) instead of silently losing the record;
  a dead process drops the record with a clear log line.

### Added
- Settings → 立即检查 button (`ForceJunkAdviceNow`) to trigger the check on
  demand — useful for verification and for users who do not want to wait until
  19:00.

### Changed
- **HabitHint is now globally once per day** (was per-time-window: 15/day
  measured). This matches the original intent from the v0.13 retrospective.
- **Junk safety filters unified** into `JunkSafetyFilter` — JunkSweeper and the
  advisor's user-marked channel now share one implementation instead of two
  copies kept in sync by comment.
- Advice text distinguishes user-marked candidates ("（你标记的）") and no longer
  double-counts multiple PIDs of the same name.

## [0.16.0] — popup-junk detection + user marks feed the advisor

Real-world case that exposed both gaps: a MindManager marketing notifier
(MmMessagingNotifier) popped an ad window; the user had AI kill it and disable
its scheduled task — MemPilot had six days of ledger data on it (46
observations, 23 windowless) yet never suggested anything, and the disposal
never reached the audit.

### Fixed
- **Popup windows counted as "use".** A window appearing for ≤5 minutes with no
  audio and <2% CPU — the classic marketing-popup pattern — now lands in
  `TransientWindowObs` instead of `LastActiveUtc`, so popup-only processes stay
  "unused today" and the advisor can catch them. Long/audible/busy window
  streaks still count as real use.

### Added
- **User marks feed the advisor**: processes you right-click-marked
  "可清理" (cleanable) are now suggestion candidates directly — they no longer
  need to be in the built-in junk list. The full JunkSweeper safety filter set
  is replicated for this channel (guard-commented: change both or neither).
  The advisor's reason text notes the user mark.

## [0.15.0] — junk process advice (feature)

When a junk process goes **a whole day without any user-visible activity**, MemPilot
now asks what to do about it — instead of either silently killing it (never) or
silently ignoring it forever (the old behaviour, and the reason HabitHint's 111
unanswered prompts mattered).

### Added
- **Junk advice card** (home page, non-modal): lists junk processes with zero
  window/audio/CPU activity today, offering **suspend 2 h / suspend 8 h /
  close / ignore**. One suggestion per day, at most 5 processes, ranked by
  private commit. Tray balloon once per day (notification policy now has 5
  whitelisted classes). On by default; a settings checkbox turns it off.
- **Timed suspend with auto-resume**: `SuspendRecord.ResumeAtUtc` +
  `SuspendRegistry.TakeExpired`; the engine resumes expired suspends on every
  tick **and** at startup (covers hold periods that expired while the app was
  closed). "(pid, startTime)" recheck applies, PID reuse cannot suspend the
  wrong process.
- **Feedback learning** (`junk-advice.json`): ignoring the same process name
  stretches the next suggestion to 30 then 90 days; accepting (suspend/close)
  resets it to 7 days. Never auto-adds to any whitelist — it only tunes how
  often you are asked, and logs "已学会：X 90 天内不再提示".
- "Used today" signal in the behaviour ledger: `LastActiveUtc` /
  `DayKey` / `TodayActiveObs`, updated when a process has a window, plays
  audio, or burns ≥2% CPU; the daily counter resets automatically at the local
  day boundary.

### Safety
- All JunkSweeper safety filters are inherited untouched (protected/system/
  service/foreground/audio/whitelist/min-age); the advisor adds only the
  "unused today" + "running ≥2 h" gates.
- Close for windowless candidates still requires the standard double-confirm
  modal — the card cannot force-kill in one click.

## [0.14.0] — architecture & performance round

### Changed
- **MainViewModel split by responsibility** (prism's #1 hotspot two rounds
  running: 1,052 lines / 87 branches). The 1,150-line file is now 713 with the
  relief card / lease / collections / make-room moved to partials
  (`MainViewModel.{Relief,Lease,Collections,MakeRoom}.cs`), class name and
  public surface unchanged. Split was gated by a **golden test** that locks the
  public surface via reflection and the two-track status semantics by behaviour —
  written before the split, unchanged after (per the house rule: golden first,
  then refactor). The test project now references the App project for this.
- **Performance debt** (all previously located, none fixed until now):
  - `DecisionLog.Recent(n)` sliced the tail instead of sorting all 5,000
    entries inside the lock on every call (RuleAudit called it 3× per run).
  - `BehaviorLedger` capped at 1,000 names with oldest-observed eviction
    (previously unbounded, +41 names/day).
  - `UsageRecorder`/`GpuRecorder` retention prune runs every 24 h at runtime,
    not just once in the constructor (long-running instances never pruned).
  - `GpuMonitor` pid-name cache pruned against live pids each sample (was
    wholesale-clear at 4,096); command-line cache trim threshold 4,096 → 512.
  - One-click batch trim is deduped within 10 minutes (measured: the second
    firing reclaimed only +19–419 MB).

## [0.13.1] — blank-window fix + documentation governance

### Fixed
- **The app opened to a blank content area.** Root cause dated to v0.6.0: the
  navigation group headers are raw TextBlocks inserted as TabControl items, and
  "核心" sits at index 0 — WPF's Selector defaults to the first item, so the
  content region rendered the TextBlock itself and no page. Nothing ever set an
  initial selection (no `SelectedIndex`/`IsSelected` exists in the file's entire
  git history). The home TabItem now carries `IsSelected="True"` (immune to
  group-header position), `SelectionChanged` falls back to home when the
  selected item is not a page, and a structure guard fails the build if either
  line regresses.

### Changed
- **Documentation governance** (12 findings from a full-doc audit):
  - README: portable size "~3 MB" → ~440 KB; self-contained → ~62 MB (both were
    7×/stale vs. reality and contradicted the changelog).
  - `docs/README-zh-v0.9.md` archived as a historical snapshot — it claimed
    "未声明开源许可" while the repo is Apache-2.0, said the decision log caps at
    1,000 (now 5,000 kind-bucketed), and used the retired term 腾挪 twice.
    Replaced by a current `docs/README-zh.md`.
  - New docs: `docs/PRIVACY.md` (what is recorded, where, 30-day retention,
    what never leaves the machine), `docs/LEASE-API.md` (endpoints, fields,
    policies, error codes — the flagship feature previously had no reference),
    `docs/NOTIFICATION-POLICY.md` (balloon whitelist, cooldowns, non-modal card).
  - ARCHITECTURE data layout now lists ai-session/feedback/behavior/killed
    files and the v0.13 relief semantics; HANDOFF.md roadmap updated to reality;
    wharf.project.json notes carried forward.
  - Guard tests (`DocsGuardTests`) block the corrected errors from returning.

## [0.13.0] — fourth retrospective: engine effectiveness

A deep retrospective combined 11-day action-effectiveness audit with root-cause
analysis. Headline: the tool's best mechanism (safe-subset trim, 4/4 measured
successes, mean +2.7 GB) had been idling behind a broken purge, and the rescue
ladder could be deadlocked by a single failing action.

### Fixed (root causes)
- **Standby-list purge failed 4× in a row with `STATUS_PRIVILEGE_NOT_HELD`.**
  `IsAdmin()` checks the role; the memory-list command requires
  `SeProfileSingleProcessPrivilege` to be *enabled* in the token. The process
  now enables it via `AdjustTokenPrivileges` (idempotent, diagnostic recorded),
  and failures report the symbolic NTSTATUS name instead of a hex code.
- **A failed action consumed the cooldown.** `_lastReliefUtc` was assigned
  unconditionally, so one failing purge blocked the whole escalation ladder for
  2 minutes (and the repeat cycle for 10). Failures no longer set the cooldown;
  a failed purge immediately escalates to the safe-subset trim in the same tick.
- **JunkSweeper's L1/L3 gate was inverted** — windowed junk got a trim and
  windowless junk got graceful close, which is a guaranteed 8-second no-op
  (WM_CLOSE needs a window; measured 4/4 timeouts). Direction corrected; the
  old tests asserting the inverted behavior were updated, not kept green.
- **L3 candidates and execution both gate on visible windows now**: candidates
  are filtered to `HasVisibleWindow`, and `GracefulClose` fails fast ("进程无
  可见窗口…") instead of burning the full timeout.

### Added
- **AI session persistence**: an open session is written to `ai-session.json`
  on start, deleted on end, restored on launch — 11 unclosed sessions in the
  data were all restart artifacts; restarts no longer split one 312-minute
  session into two.
- **Low-available floor trigger**: avail < 3 GB or commit margin < 10% → run
  the proven batch trim immediately (10-minute dedup, fullscreen gate). This
  would have caught both crisis moments the broken purge path missed
  (Nov 4 509 MB, Nov 5 1.94 GB).
- Guard tests for all of the above (7 new).

### Changed
- **FeedbackStore is copy-on-write.** UI writes raced the engine's background
  classify loop over an unlocked `List` (torn reads, "collection modified").
  Reads are now lock-free over a `volatile` snapshot; writes rebuild it.
- Noise: HabitHint cooldown 6 h → 24 h (111 firings, zero measured user
  response); GrowthWatch threshold 512 → 800 MB and per-PID cooldown 1 → 2 days.
- **Honest per-process trim telemetry**: the audit recorded global available
  memory before/after a ~0.6 s window — after could be *lower* than before
  while claiming success. It now records the trimmed process's own private
  working set delta.
- Dark theme swap table extended from 17 to all 38 semantic tokens (v0.12's
  new tokens left the whole nav rail, grid headers, banners and charts light).
- Audit page's 操作/级别 columns are localized (`UiTerms.OfAction`).
- Dead code removed (`ModeManager.ShouldClean`, `AutoSwitchOnForeground`,
  `MaxCpuPercentForResident`, `TriggerMemoryPressureBytes`,
  `SkipOptimizeInFullscreen` checkbox); `sign.ps1` default path fixed
  (pointed at the pre-rename project); release now ships only zh-Hans/en
  satellite resources and drops `IncludeNativeLibrariesForSelfExtract` —
  self-contained exe 65 MB → **58.2 MB** measured.
- `docs/ITERATION-BACKLOG.md` marked as a historical archive.

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
