# Security Policy

## Supported versions

MemPilot ships from `main` only; the latest release is the supported version.
Older releases are not patched.

## Reporting a vulnerability

**Please do not open a public issue for a security problem.**

Use GitHub's private vulnerability reporting:
<https://github.com/ShuiQiongChuYunQiShi/MemPilot/security/advisories/new>

(If the repository has moved, use `Security` → `Report a vulnerability` on the
repository page.)

Please include:

- MemPilot version (Settings → 关于 shows it, or `MemPilot.exe` file properties)
- Windows version and whether the process ran elevated
- What you expected vs. what happened
- Reproduction steps, and any relevant `decisions.json` / `audit.json` excerpt
  from `%APPDATA%\MemPilot` (redact anything personal)

I aim to acknowledge within 7 days. This is a single-maintainer hobby project,
so please allow realistic time for a fix.

## Threat model — what MemPilot is allowed to do

MemPilot can terminate, suspend and trim processes. That makes it a
**dual-use tool**, so the safety boundaries are part of the design and not
configurable:

| Boundary | Behaviour |
|---|---|
| Unknown processes | Never disposed of automatically, ever |
| Protected categories | System-critical, security software, anti-cheat, drivers, Windows services: never candidates |
| Foreground / fullscreen | Never touched |
| Processes playing audio | Treated as in-use (WASAPI session peak detection) |
| Young processes | Below `MinProcessAgeSeconds` (default 120 s) never candidates |
| Automatic actions | Never reach L4 (force kill). Only manual, doubly-confirmed |
| PID reuse | Every state-changing action re-checks `(pid, startTime)` before acting |
| Lease API | Binds 127.0.0.1 only, requires a Bearer token, **disabled by default** |
| Elevation | Never requests admin unless the user explicitly clicks the elevation path |

If you find a way to make MemPilot act on a process outside these boundaries,
that is a security bug and I want to hear about it.

## Known non-issues (by design)

- **Unsigned binaries.** Releases are currently unsigned, so SmartScreen/360 may
  warn on first run. Verify `SHA256SUMS.txt` instead; see README.
- **Reading other processes' paths.** MemPilot calls `QueryFullProcessImageName`,
  which is a documented, unprivileged API. It does not inject code, does not
  load a kernel driver, and does not bypass any access control.
- **The lease token file** (`%APPDATA%\MemPilot\lease-token.txt`) is readable by
  any process running as the same user. It is a same-user boundary, not a
  cross-user one — that is intentional and documented in the code. It stops
  web pages and other-machine scripts, not local code you already run.
- **Undocumented NT APIs** (`NtSetSystemInformation`, `NtSuspendProcess`) are
  used for system memory-list purging and whole-process suspend. These require
  administrator privileges, are the same calls other memory tools use, and fail
  safely (`STATUS_PRIVILEGE_NOT_HELD`) without them.
