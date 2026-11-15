# MemPilot

<p align="center">
  <a href="#download"><img src="https://img.shields.io/github/v/release/ShuiQiongChuYunQiShi/MemPilot?style=flat-square&label=release" /></a>
  <a href="LICENSE"><img src="https://img.shields.io/badge/license-Apache--2.0-blue?style=flat-square" /></a>
  <a href="https://github.com/ShuiQiongChuYunQiShi/MemPilot/actions/workflows/ci.yml"><img src="https://img.shields.io/github/actions/workflow/status/ShuiQiongChuYunQiShi/MemPilot/ci.yml?branch=main&style=flat-square&label=ci" /></a>
</p>

**Process-level memory scheduling for Windows — built for machines that run
AI image/video generation, games, and development at the same time.**

MemPilot does not "clean your RAM". It identifies what each process actually
is, decides what is genuinely unused, and moves or releases memory **per
process** — so your ComfyUI job gets the memory it needs without your
browser tabs, IDE, or game becoming collateral damage.

<p align="center">
  <img src="docs/screenshots/en/home.png" width="760" alt="MemPilot main window" />
</p>

> 🇨🇳 中文说明：见 [docs/README-zh.md](docs/README-zh.md)。

## Download

| Channel | File | Size | Needs |
|---|---|---|---|
| **Portable (recommended)** | `MemPilot-<ver>-portable.zip` | **~440 KB** | [.NET 10 Desktop Runtime](https://dotnet.microsoft.com/download/dotnet/10.0) |
| Self-contained | `MemPilot-<ver>-win-x64.exe` | ~62 MB | nothing — runtime is bundled |

Both are on the [Releases page](https://github.com/ShuiQiongChuYunQiShi/MemPilot/releases/latest).
Every release ships a `SHA256SUMS.txt`; verify before running:

```powershell
(Get-FileHash .\MemPilot-<ver>-win-x64.exe -Algorithm SHA256).Hash.ToLower()
Get-Content .\SHA256SUMS.txt
# the two values must match
```

Releases are **unsigned** — SmartScreen or your antivirus may warn on first run.
That is expected for a new project, not a sign the file is broken. See
[SECURITY.md](SECURITY.md) for the trust model and [docs/PRIVACY.md](docs/PRIVACY.md)
for what data is stored (and what never leaves your machine).

## Two problems, not one

Most memory utilities treat RAM as one number and flush it. Measured on a real
machine over 9 days (1,392 samples):

```
Physical RAM        31.7 GB
Commit limit        44.7 GB  (pagefile 13 GB in use)
Committed (median)  30.1 GB  = 95% of physical
Committed (peak)    68.3 GB
Commit ≥ 90%        11.2% of all samples
Physical < 4 GB     only 3.2% of samples
```

**The real bottleneck on a working machine is commit, not physical memory.**
A standby-list purge does nothing about commit — which is why tools that only
show "freed 2 GB" rarely make your next AI generation any less likely to fail.

So MemPilot does both, and keeps them apart:

| You want | Action | What it actually changes |
|---|---|---|
| The "available" number back | Purge standby lists, flush file/registry cache, trim working sets | Physical occupancy only — **commit does not move** |
| The next AI job not to fail | Gracefully close idle background processes | Commit drops by their private bytes |

Both live in the UI. The cache tools are on the 「清缓存与工作集」 page: admin
required, explicit item selection, with a risk warning. The commit-relieving
action asks for confirmation per candidate and is written to the audit log.
Every result says which of the two it changed — "released 2 GB, commit
unchanged" is a valid and honest outcome, and it is never sold as an OOM fix.

## What it does

### 1. Classifies before it acts

Every process gets an identity before it can become a candidate:
exact-name rules, path fragments, **verified publisher signatures**
(WinVerifyTrust — a self-signed cert claiming "Microsoft" does not pass),
Steam library parsing, registry uninstall evidence, and service/startup/
scheduled-task membership. Unrecognized (`Unknown`) processes are **never
actioned automatically**.

### 2. Detects "unused" honestly

A candidate needs all of: minimum age (default 120 s), N consecutive idle
observations, no visible window, low CPU and I/O, **and no audio activity**
(WASAPI session peak detection — a process playing music in the background is
not unused). Idle state is tracked per `(pid, startTime)`, so PID reuse cannot
poison the observation.

### 3. Escalates in steps, never jumps

| Level | Action | Automatic? |
|---|---|---|
| L1 | Trim working set (`EmptyWorkingSet`) | yes (opt-in) |
| L2 | Suspend process (`NtSuspendProcess`, atomic, restorable) | yes (opt-in, hard lease only) |
| L3 | Graceful close (`WM_CLOSE` → wait → optional escalate) | only with confirmation |
| L4 | Force kill | **never automatic** — manual, double-confirm |

### 4. Explains itself

Every automatic decision — why a mode switched, who was executed or skipped
and exactly which guard skipped them — is persisted to `decisions.json`
(5,000 entries, important kinds like `Lease`/`AiSession` get a protected
quota so noise cannot evict them). There is a UI page for it. If the tool
cannot get admin rights, the log says **"skipped: needs admin"**, not
"executed, freed 0 MB".

### 5. The Lease API — ask for memory like you ask for a lock

This is the part none of the classic memory cleaners have. An AI task that
knows it needs 12 GB can ask before loading its model, and give it back after:

```python
status, lease = post("/api/lease", {
    "targetAvailableGb": 12,
    "ttlSeconds": 3600,
    "requester": "comfyui-job-42",
    "policy": "soft",          # or "hard": also suspends sufficiently-idle processes
})
# lease["achievedAvailableGb"] — what was ACTUALLY freed, measured, not promised
try:
    run_generation()
finally:
    delete(f"/api/lease/{lease['id']}")   # suspended processes are restored automatically
```

- Binds `127.0.0.1` only, Bearer-token authenticated, **disabled by default**
- Single active lease; TTL clamped 30 s–24 h; breach of the granted floor raises an alert
- `hard` policy suspends at most 8 "idle enough" processes and **restores every one** on release
- Working / gaming / foreground / audio / protected processes are never touched by either policy
- Full reference: [docs/LEASE-API.md](docs/LEASE-API.md) · working example: [`tools/lease-example.py`](tools/lease-example.py) ·
  load simulation that asserts the whole chain: [`tools/lease-load-test.py`](tools/lease-load-test.py)
- Notification behaviour (what pings you and how often): [docs/NOTIFICATION-POLICY.md](docs/NOTIFICATION-POLICY.md)

### 6. Suggests action for junk you never used

A daily prompt (non-modal card, one balloon max) lists junk processes that got
**zero window/audio/CPU activity all day**, offering: suspend 2 h / suspend 8 h
(auto-resume at expiry) / close (with the standard confirmation flow) / ignore.
Your choices are learned: ignoring the same process repeatedly stretches the
next suggestion from 7 to 30 to 90 days — never an automatic whitelist entry.
See [docs/NOTIFICATION-POLICY.md](docs/NOTIFICATION-POLICY.md).

### 7. Watches VRAM too

Per-process dedicated/shared GPU memory via PDH (`\GPU Process Memory(*)`),
adapter totals via PDH (`\GPU Adapter Memory(*)`) — sampled every 30 s to a
local JSONL with 30-day retention, with an AI-session detector
(N-of-M + hysteresis, so a single-frame VRAM dip does not split one generation
into three sessions). When a video generation eats 15 GB, you can see who, when,
and for how long — afterwards.

<p align="center">
  <img src="docs/screenshots/en/processes.png" width="760" alt="process table with VRAM column" />
</p>

## Proof of concept — verify it yourself

You should not trust a RAM utility on faith; most of them are snake oil.
Here is a check you can run in five minutes with Windows' own tools:

1. Open **Resource Monitor** (`resmon.exe`) → Memory tab. Note "Committed".
2. Run any tool that only purges the standby list. Watch "Free" go up —
   and "Committed" **not move at all**.
3. Now start an AI generation (or open a big project) until commit is near the
   limit. No cache purge will help; only closing a process will.
4. In MemPilot, open **决策日志 (Decisions)** and read what it did and —
   more importantly — what it *refused* to do and why.

That last step is the actual product: a memory tool whose log you can audit.

## Safety model

| Guard | Behaviour |
|---|---|
| Category protection | System-critical / security / anti-cheat / driver / service / startup / scheduled task never candidates |
| Unknown exemption | Unrecognized processes are never auto-disposed |
| Minimum age | Default 120 s |
| Observation period | N consecutive idle refreshes before candidacy |
| Audio exemption | WASAPI-active processes are "in use" |
| Foreground exemption | Foreground/fullscreen processes are never touched |
| PID-reuse recheck | `(pid, startTime)` verified before every state change |
| L4 double confirm | Force kill is manual only, twice |
| Fail-safe enumeration | If window/audio enumeration fails, processes are treated as **having** windows/audio — never as "unused" |
| Honest logs | Missing admin rights ⇒ "skipped", with the reason |

These invariants are enforced in one place (`TrimSelection`) and guarded by
tests — including source-level guards that fail the build if the pattern that
caused a past incident reappears (`tests/MemPilot.Tests/RegressionGuardTests.cs`).

## Engineering

- **.NET 10 WPF**, two projects: `MemPilot.Core` (engine, zero UI dependency)
  + `MemPilot.App` (shell). No `.sln` — pass the `.csproj` path explicitly.
- **One runtime dependency**: `System.Diagnostics.EventLog` 9.0.0 (optional
  Windows-event-log audit). Everything else is P/Invoke.
- **463 tests**, CI gates `MemPilot.Core` line coverage at 60% and produces
  TRX + cobertura artifacts on every push.
- Reproducible builds: `packages.lock.json` committed, CI restores with
  `--locked-mode`.
- Persistent settings use **stable ids** (`memory_growth`, `work`), never
  display text — so localization cannot silently reset your column layout.
- **Chinese + English UI**, switchable at runtime in Settings → UI language
  (`ResourceDictionary` based, no restart). Engine logs (`decisions.json` /
  `audit.json`) are still generated in Chinese; translating free-form reasons
  needs a separate refactor to templates.

## Build

```bash
git clone https://github.com/ShuiQiongChuYunQiShi/MemPilot.git
cd MemPilot
dotnet build src/MemPilot.App/MemPilot.App.csproj -c Release
dotnet test  tests/MemPilot.Tests/MemPilot.Tests.csproj
```

Requires the .NET 10 SDK and Windows 10/11. On a fresh machine the first run
writes config/history/decisions under `%APPDATA%\MemPilot` — nothing outside it.

## Known limits

- Elevated processes: memory/command-line reads may be access-denied
  (flagged, does not break classification)
- UWP windows belong to `ApplicationFrameHost`; L3 has no effect on windowless UWP
- Hybrid laptops: adapter usage is matched per LUID; if the mapping is missing,
  MemPilot shows "usage unavailable" rather than guessing
- Engine logs (`decisions.json` / `audit.json`) are generated in Chinese; the
  UI is bilingual, but free-form log reasons are not localized yet

## License

[Apache-2.0](LICENSE). See [NOTICE](NOTICE) for third-party components.
