# Validation report — hana-desktop-companion v0.1.1

Environment: HanaAgent `1.0.6-beta`, Windows 11 x64, .NET 8 Windows Desktop Runtime x64.

Two validation modes are used and must not be confused:

- **[A] host tool channel** — the installed App is called through Hana's tool
  registry, exercising the real production path (capability ledger → App RPC →
  tool `execute` → bundled helper).
- **[B] logic level** — the compiled helper is invoked directly, to pin down
  Win32 / GDI behavior and negative cases.

Personal window images, machine-local paths, live HWND/PID values and test
fixtures are **not** reproduced here; only conclusions and evidence classes.

## 1. Installation integrity

| Check | Expected | Actual | Result |
|---|---|---|---|
| Shipped logic binary hash | equals the built, fixed binary | identical | pass |
| `manifest.json` version | `0.1.1` | `0.1.1` | pass |
| App load + spawn capability | loaded, `app/process.spawn` compiled into the App-process argv | loaded; `--allow-child-process` present | pass |
| Tool registration | 8 `desktop_*` tools | all 8 published to the model catalog | pass |
| Settings contribution | 2 fields | 2 fields | pass |

Note: with a framework-dependent .NET publish, the application logic lives in
`<name>.dll`; `<name>.exe` is only an apphost launcher. The `.exe` hash is
therefore expected to stay constant across code changes — identity must be
verified on the `.dll`. This was checked directly during the update.

## 2. Core function [A]

| Check | Expected | Actual | Result |
|---|---|---|---|
| `desktop_windows_list` | real window list with handle + pid + state | returned | pass |
| `desktop_window_capture` on a self-built Notepad fixture, `maxDimension` below window size | capture must not error; deliver a session image | `resized`, valid PNG, `blackRatio` 0, session file id returned; image content matched the fixture verbatim | pass |
| `desktop_window_capture` on an **occluded, non-foreground** window | target's own pixels, no full-desktop fallback | `method=printwindow-fullcontent`, target content rendered (fresh timestamp visible), `foregroundPreserved=true` | pass |
| `desktop_uia_tree` | element summary | role / name / className / patterns / bounds | pass |
| `desktop_uia_find` | keyword matches with bounds | matched the expected button and text nodes; missing selector correctly rejected | pass |
| `desktop_hit_probe` | the window actually under a pixel | correctly reported the topmost (occluding) window | pass |

The capture resize case is a **regression test** for the 0.1.0 defect fixed in
0.1.1: at 0.1.0 this exact call always failed with `A generic error occurred in
GDI+`, because GDI+ locks a file opened via `Image.FromFile` while the code then
writes back to the same path. 0.1.1 loads bytes into memory instead, and the
resize branch now succeeds.

Why the resize branch was not caught earlier: the first pass of logic-level
tests only used the default `maxDimension`, which never entered the resize path.
Lesson recorded — boundary parameters that select a different code branch must
be part of fixture cases; testing only defaults leaves branches unverified.

## 3. Safety gates and negative cases

| Check | Expected | Actual | Result |
|---|---|---|---|
| `allowRealInput` OFF (default) + `execute=true` + model-echoed confirmation phrase, for `window_manage` and `mouse_drag` | refuse, zero injection | `executed:false`, `blockedBy: app-disabled`, risk tier reported | pass |
| Handle reused by another PID | refuse before any action | structured rejection, no capture performed | pass |
| Start pixel no longer resolves to the declared target window | refuse blind injection | `blockedBy: hit-window-mismatch`, no cursor motion | pass |
| `close` without an explicit handle | refuse | rejected; title/foreground matching disallowed | pass |
| Mutating verb without `execute` | plan only | `dryRun:true`, zero side effects | pass |

The first row is the key property: a confirmation string supplied by the model
is treated as an acknowledgement only and can never substitute for the user's
App-level switch or the host's per-call review.

## 4. Real-action end to end [A]

Performed against a **self-built** Notepad fixture opened by the test only. No
user application was clicked, typed into, dragged or closed.

| Action (`execute=true`) | Evidence class | Result |
|---|---|---|
| `window_manage` move | re-read bounds changed from the pre-move position to the requested one | pass (state-evidenced) |
| `window_manage` minimize | re-read state showed minimized, bounds collapsed off-screen | pass (state-evidenced) |
| `window_manage` restore | re-read state showed restored | pass (state-evidenced) |
| `window_manage` close | process no longer present after the call | pass (state-evidenced; irreversible action executed and confirmed) |
| `mouse_drag` | call returned `ok:true` with the hit guard admitting it; a subsequent capture of the same window returned live content | **partially** evidenced |
| `mouse_wheel` | hit guard refused at drifted coordinates | not separately evidenced |

Honest reading of the last two rows:

- **drag** — the injection path definitely ran and the pre-injection guard
  passed, but the *visible* selection effect was not independently confirmed:
  the rendered evidence after scaling was not sharp enough to prove a selection,
  and unlike window state there is no clean re-readable signal for "a drag
  happened". Do not read this as a confirmed visual effect.
- **wheel** — the Windows foreground lock does not let a background-spawned
  process reliably raise the fixture above other windows, so the coordinate
  resolved to a different window and the hit guard refused. That refusal is the
  guard behaving **exactly as designed** (it exists to stop blind injection
  hitting the wrong window after focus drifts). A successful render was
  therefore never captured through [A].
- Both verbs share the same bundled-helper `mouse_event` injection stack as the
  actions that were evidenced, and both pass at the logic level [B]. This is
  strong circumstantial confidence, not a live-session capture. It is called out
  as the single remaining caveat in the READMEs.

## 5. Official toolchain

All run with the `hana-app-creator` scripts shipped with Hana `1.0.6-beta`:

| Step | Result |
|---|---|
| `validate_app.mjs --dir <app>` | ok, 0 errors 0 warnings, identity icon normalized without warning |
| `validate_app.mjs --dir <app> --smoke` | isolated AppHost: backend `loaded`; a tools-only App legitimately reports no surfaces and document `not-tested` |
| `pack_app.mjs` | install ZIP + matching `.entry.json` produced |
| `validate_app.mjs --archive <zip> --smoke` | ok; archive SHA-256 matches the entry record; bundled native artifacts verified inside the final ZIP |
| Deliverable binary vs installed binary | byte-identical, so the documented/verified behavior corresponds to the shipped package |

App-side logging showed no crash or residual error; the only warning was the
expected `[PERM0002]` notice that accompanies a granted `--allow-child-process`.

## 6. Verdict

Observation, targeted capture (including occluded windows), UIA diagnosis, the
default-off real-input gate, negative/identity checks and the window-mutation
actions with before/after state evidence all **pass** through the host tool
channel, and the official packaging chain is clean.

Single outstanding caveat, disclosed in the READMEs: a live-session, separately
captured **visual** success for `mouse_drag` and a successful `mouse_wheel`
render were not obtained, for the environment and design reasons described in §4.

## 7. Housekeeping after testing

- The test fixture was closed via the verified graceful close path; no stray
  window or screenshot artifact remains, and no personal window image was kept.
- `allowRealInput` was turned on only for the real-action pass and is the
  operator's safety switch; it should be returned to its default **off** state
  when testing is finished. This App never edits that preference by itself.
- The two upstream v1 plugins were left completely unmodified throughout, so a
  rollback is always available.
