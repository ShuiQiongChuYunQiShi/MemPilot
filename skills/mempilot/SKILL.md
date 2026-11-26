---
name: mempilot
description: Reserve Windows memory before a large local job (model load, video generation, big test run) through MemPilot's lease API over MCP. Use when the task may need several GB of RAM, when a previous run died with OOM / "out of memory" / "CUDA out of memory", or when another memory-heavy process is already running on the same machine.
---

# MemPilot leases

MemPilot runs on this machine and exposes three MCP tools. A lease reserves system RAM for
the duration of a job and reports **what was actually freed**, not what was requested.

## When to use

- Before loading a model or dataset that needs more than ~4 GB of RAM.
- Before a job that may outlive `ttlSeconds` (an hour, say) — then renew instead of letting it expire.
- Right after a previous attempt failed with OOM.

Do **not** use it for GPU memory: MemPilot only manages system RAM.

## The loop

1. `mem_plan` — zero action. Returns measured current memory, an upper-bound projection,
   which processes *would* be suspended, and how many the safety rules exclude (and why).
   `projectionBasis` tells you how to read the number:
   `hard-upper-bound` (may suspend idle processes) or `current-only` (a lower bound).
2. If `shortfallGb > 0`, reduce the job's memory footprint (batch size, tile size, context
   length) and plan again. Do not acquire and hope.
3. `mem_lease { action: "acquire", targetAvailableGb, ttlSeconds, policy: "hard", requesterPid }`.
   Pass `requesterPid` = the PID of **your own task process** — it and its subtree are never
   suspended (otherwise you can freeze your own worker and wait forever).
4. Branch on `achievedAvailableGb` / `shortfallGb`, **never on HTTP 200 alone**.
5. `mem_lease { action: "release", leaseId }` when the job ends, including on failure.
   If your process dies, the TTL reclaims it — but that restores the suspended processes
   immediately, which may be exactly when your job needs the memory.

## Rules

- **Renew long jobs.** On expiry MemPilot restores every suspended process at once. If the
  job may run past `ttlSeconds`, call `mem_lease { action: "renew", ttlSeconds }` well before
  the deadline. `410 expired` means the lease is already dead — acquire again, don't retry renew.
- **Conflicts are normal.** Only one lease can be active. A `409` carries the current
  `holder` and `retryAfterSeconds`; pass `waitSeconds` (0–120) to let the tool retry for you
  rather than looping blindly.
- **`denyNames` narrows, never widens.** Use it when `mem_plan` lists a process you need
  left alone (e.g. a chat app). Never list system, security, or anti-cheat processes — they
  are already excluded and listing them changes nothing.
- **Workers are yours to protect.** If your job spawns helper processes that look idle
  (windowless, low CPU), pass `requesterPid` = their parent so they are never suspended.

## What MemPilot will never do

- Force-kill anything automatically. L4 is never automatic; `actions` here cannot express it.
- Touch processes classified as protected, system, security, anti-cheat, driver, game,
  container host, or **unknown**. `mem_plan` reports the counts under `excluded.byReason`.
- Suspend a proxy or VPN core (`FlClashCore`, `tailscaled`, …) — it would cut all networking,
  including your own API calls to the model provider.

Do not try to work around these. If a job does not fit, shrink the job.
