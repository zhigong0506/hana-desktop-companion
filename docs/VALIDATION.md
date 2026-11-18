# Validation report — hana-desktop-companion v0.1.1 / v0.2.0

Sections 1–7 record the v0.1.1 acceptance run. Section 8 records the v0.2.0
increment and, importantly, what it does **not** cover.

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

## 8. v0.2.0 increment (current tree)

Scope of the increment: `list-displays`, `wait-for`, `send-keys`,
`clipboard-read`, `capabilities`, plus the `ParseFlags` fix. Clipboard **write**
was investigated and removed: it would have to carry plaintext on the helper's
process command line, where any local process can read it. No plugin-side
approval ledger, control session, global keyboard hook or overlay was added.

This section was rewritten after the safety patch. It reports only checks run
against the **current** source tree and the freshly rebuilt helper; the package
hash and packaging rows from the earlier draft were removed because this task
does not pack or change generated packages.

### 8.1 Logic level [B] — executed on this tree

| Check | Command | Result |
|---|---|---|
| Helper compiles | `dotnet build native-src/HanaDesktopCompanion.Helper.csproj -c Release` | success, 0 warnings, 0 errors |
| JS syntax | `node --check` × 6 files | 6/6 OK |
| Pure-logic suite | `node --test "tests/*.test.mjs"` | **57 pass, 0 fail** |
| JS/native parity | `tests/native-mirror.test.mjs` | pass — parses `Program.cs`; also guards the removed clipboard-write verb and the wait / secure-desktop fail-closed branches |
| Official static validation | `validate-app.mjs --dir app/hana-desktop-companion` | **0 error(s), 0 warning(s)** |

The suite pins: default refusal with real input off; action-policy tiering;
missing target binding; the confirmation phrase as acknowledgement only; the
fixed chord denylist and single-chord-per-call rule; bounded wait timeouts; the
closed argv surface (including dropping `thenKeys` and `--text`); size-only read
receipts and text redaction. The removal guards are **source-shape assertions**,
not runtime proof — they catch reintroduction and dropped branches, not Win32
behaviour.

### 8.2 Read-only live checks [B] — executed against the rebuilt helper

The rebuilt helper copied into `app/hana-desktop-companion/native/` was invoked
directly. Only observation verbs and dryRun/refusal paths were used: no window
was moved, no key was injected, and the clipboard was not read or written.

| Verb / call | Actual result | Result |
|---|---|---|
| `capabilities` | version `0.2.0`, 14 verbs; `clipboard-write` absent from `verbs` and listed in `unsupported` | pass |
| `list-displays` | 1 monitor, 2560×1440 physical, DPI 120 (125%), primary index 0 | pass |
| `wait-for window-gone`, dead handle, no PID | `satisfied:true`, `originalTargetGone:true`, 1 poll | pass |
| `wait-for window-gone`, dead handle, with PID | `satisfied:true`, `originalTargetGone:true`, 1 poll | pass (no reuse detected on a dead handle) |
| `wait-for element-gone`, impossible query on a live bound window, 250 ms | `satisfied:true`, `uiaComplete:true` in 1 poll — a complete walk with no match | pass |
| `wait-for --condition nope` | `Unsupported wait condition …`, non-zero exit | pass |
| `list-windows --evil 1` | `Unsupported flag for 'list-windows': --evil` | pass |
| `mouse-wheel --notches -3` (dryRun) | `notches:-3` in the plan | pass (ParseFlags negative-value regression) |
| `send-keys --chord win+l` | `The 'win' modifier is refused …` | pass |
| `send-keys --thenKeys tab,enter` | `Unsupported flag for 'send-keys': --thenKeys` — the flag no longer exists; the earlier draft's “requires --chord” wording was wrong | pass (corrected) |
| `send-keys --text hello` | `Unsupported flag for 'send-keys': --text` — no plaintext payload path | pass |
| `send-keys --chord ctrl+shift+s` (dryRun, bound) | `dryRun:true`, single `chord`, `targetIsForeground` reported, zero injection | pass |
| `clipboard-read` (dryRun, bound) | `dryRun:true`, `maxChars:2000`, no clipboard access | pass |
| `clipboard-write --text …` | `Unsupported verb: clipboard-write` | pass (removal confirmed) |

### 8.3 Not tested / cannot be claimed — stated plainly

- **No real key injection.** `send-keys` with `execute=true` was not run. The
  `SendInput` path, the finally-release behaviour, the foreground re-verification
  race and the secure-desktop refusal have not been exercised on a live desktop.
- **No real clipboard read.** Only the dryRun plan was observed. Actual
  truncation against a real clipboard and the clipboard-busy retry path are
  unproven. Clipboard write does not exist.
- **WaitFor handle reuse is not runtime-proven.** No fixture was built in which
  a bound HWND was recycled into another PID while a wait was in flight. The
  binding (`boundPid = target.Pid`), the per-poll re-check and the
  `handle-reused` abort are covered by source-shape guards only.
- **UIA incompleteness is not runtime-proven.** `element-gone` was observed
  returning satisfied only on a complete walk. A tree exceeding the 4000-node
  budget, or a partially unreadable tree, was not constructed; the
  `uiaComplete:false` path is a source-shape guard, not an observed outcome.
- **Secure desktop identity comparison is not runtime-proven.** No lock screen
  or UAC prompt was created. Only the source shape (input-desktop name compared
  with thread-desktop name, refuse when unverifiable) is pinned.
- **No end-to-end [A] host-channel run on this tree.** Tool registration, the
  readOnly declaration taking effect and the host's review prompt for the new
  sensitive tools were not exercised in a live Hana session.
- **Official runtime smoke was unavailable**: `SMOKE_NODE_RUNTIME_UNAVAILABLE`
  (smoke host requires Node 26+, this machine has 25.2.1 and no cached platform
  runtime).
- **Package verification:** after the safety fixes, the main agent rebuilt the
  helper, repacked the App and ran official static directory and archive
  validation (0 errors, 0 warnings). The ZIP SHA-256 matches the entry record:
  `edd627318320cac2cebfd97352824a2bc5e1926224e347b62b5e73dc41ce9f1e`.
  The packaged helper DLL's SHA-256 matches the current App DLL. These checks
  do not replace isolated startup or live host-channel validation.
- **OCR is not supported and was not attempted.** The hand-off point is the
  session image from `desktop_window_capture`; no arbitrary image path or
  full-screen OCR exists.
- **Multi-monitor geometry was observed on a single-monitor machine only.**
  Cross-monitor coordinates, mixed DPI and a negative-origin virtual desktop
  are untested.

### 8.4 Build artifacts

The rebuilt helper was copied into `app/hana-desktop-companion/native/`;
`hana-desktop-companion-helper.dll` is 119296 bytes (pre-patch build was
113664). `native-src/publish/` is an untracked local publish directory produced
by the main agent; it was not modified or deleted and must not be committed.
No signing was performed and no private key was read, used or stored.
