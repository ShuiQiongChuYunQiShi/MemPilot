# Contributing to MemPilot

Thanks for looking. This is a focused single-maintainer project — the fastest way
to get a change merged is to keep it small and explain the evidence behind it.

## Before you write code

**Read the safety model first** (`SECURITY.md` opens with it). MemPilot disposes of
real processes on a real machine. A change that weakens any invariant listed there
will not be merged regardless of how useful the feature is.

The invariants, in one line each:

- `Unknown` category is never actioned automatically
- System-critical / security / anti-cheat / driver / Windows-service processes are never candidates
- Foreground, fullscreen, audio-playing, and younger-than-min-age processes are never touched
- Automatic paths never reach L4 (force kill)
- Every state-changing action re-checks `(pid, startTime)` first

## How decisions get made here

This project has an unusual convention: **a change should cite data**. Most fixes
in the history came from something the tool itself recorded:

```
2026-11-04 fix: v0.9.0 — 三轮复盘整改：修 8 个真 bug + 降噪 + 租约真实验证
```

If you propose "trim working sets more aggressively", include the before/after
available-memory numbers from `%APPDATA%\MemPilot\audit.json`. If you propose a new
heuristic, include the sample count and the miss rate. Guesses that cannot be
measured are usually rejected — and a good measurement that proves a feature *does
not* work is valuable, so it gets committed as a decision note.

## Build and test

```bash
dotnet build src/MemPilot.App/MemPilot.App.csproj -c Debug
dotnet test  tests/MemPilot.Tests/MemPilot.Tests.csproj
```

There is **no `.sln`** on purpose; always pass the explicit `.csproj` path.
The test project also runs real system-enumeration integration tests — they skip
themselves in restricted contexts (CI sandbox) rather than failing.

If your machine's security software locks `%TEMP%` on first write, point the test
data directory somewhere else:

```bash
MT_TEST_TMP=D:/tmp/mt-tests dotnet test tests/MemPilot.Tests/MemPilot.Tests.csproj
```

## Adding a guard test

Every bug fix needs a test that fails before the fix and passes after. For bugs in
*behaviour that is hard to trigger* (elevation, port conflicts, PID reuse), this
project uses **source-level assertions** — see
`tests/MemPilot.Tests/RegressionGuardTests.cs`:

```csharp
[Fact]
public void LeaseApi_ReservesBeforeRelief_ToAvoidOrphanedSuspends()
{
    var api = Read("MemPilot.Core", "Services", "LeaseApiService.cs");
    Assert.Contains("TryReserve", api);
}
```

This is not elegant, but it catches the exact regression that shipped once:
the code compiled, the unit tests were green, and the feature was broken.

## Translating

The UI is Chinese-only today; **English localization is being implemented in
v0.19.0** with a `ResourceDictionary`-based string layer (runtime language
switch, no `.resx` build step).

- Strings live in `src/MemPilot.App/Resources/Strings.<culture>.xaml`
- The lookup + fallback service is `src/MemPilot.Core/Localization/UiStrings.cs`
- Engine log text (`decisions.json` / `audit.json`) is generated in Chinese and
  is **not** localized yet — translating it requires turning free-form reasons
  into templates, which is a separate refactor.
- To propose a language, open a **Translation request** issue; do not open a PR
  with 900 hand-translated strings as a first contribution.

Persisted settings use **stable ids** (`memory_growth`, `work`) rather than
display text precisely so that adding a language does not reset user settings.
Please keep it that way — never use a translated string as a key.

## What is most welcome

1. **Bug reports with data attached** — a `decisions.json` fragment beats a
   paragraph of description.
2. **Fixing wrong numbers.** If the UI shows something that contradicts itself or
   contradicts Task Manager / Resource Monitor, that is a P0.
3. **Translations** (after string extraction lands).
4. **Safety review.** Pointing out a path by which a protected process could
   become a candidate is the single most valuable contribution.

## What is explicitly out of scope

- Kernel driver / ring-0 component
- Aggressive "RAM booster" behaviour (purging standby lists by default, killing
  processes to make numbers look better)
- Any feature whose only selling point is a bigger "freed memory" counter

## Code style

Match the surrounding file. The codebase is bilingual by accident: **code comments
and UI strings are Chinese, identifiers and public API are English**. Do not
"translate" existing comments in unrelated PRs — it makes review impossible.

Commits: `type: summary` in English or Chinese, details in the body.
