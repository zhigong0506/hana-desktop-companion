# Changelog

All notable changes to this project are documented here.
The format follows Keep a Changelog; this project uses Semantic Versioning.

## [0.2.0] - 2026-11-18

Incremental extension inside the existing v2 App. The host's per-call review,
`allowRealInput` defaulting to off, HWND+PID target binding and the fixed native
verb allowlist are all unchanged; nothing from the v1 plugins' bespoke
approval-ledger / control-session design was brought back.

### Added
- `list-displays` verb + `desktop_displays_list` tool (read-only): virtual
  desktop bounds and per-monitor bounds / work area in physical pixels, primary
  flag, effective DPI and scale percent.
- `wait-for` verb + `desktop_wait_for` tool (read-only): bounded polling over
  `window-gone` / `window-visible` / `window-minimized` / `window-foreground` /
  `title-contains` / `element-exists` / `element-gone` / `point-hits-target`.
  Timeout is clamped to 200–30000 ms, the poll interval to 100–2000 ms, and the
  result is an observation: it never acts and never continues a plan.
- `send-keys` verb + `desktop_send_keys` tool (sensitive, dryRun by default):
  exactly one chord per invocation, fixed key/modifier allowlist, an explicit
  system-level chord denylist, and a mandatory foreground HWND+PID binding that
  is re-verified immediately before `SendInput`.
- `clipboard-read` verb + `desktop_clipboard_read` tool (sensitive, dryRun by
  default): truncated reads (default 2000, cap 4000 chars), size-only receipts,
  and redaction so content cannot reach a log line or an error detail. The text
  itself is the tool result and does reach the host/model context — the docs
  say so explicitly rather than claiming it never leaves the App.
- `capabilities` verb + `desktop_capabilities` tool (read-only): helper version,
  fixed verb surface, read-only vs gated grouping, live gate state, and an
  explicit list of what is deliberately not implemented.
- `lib/shortcuts.js`, `lib/waitfor.js`, `lib/clipboard.js`: pure-logic mirrors
  of the native screening so bad input is refused before a helper process
  starts, and so the rules are testable without a desktop.
- `tests/`: 57 pure-logic assertions (`node --test "tests/*.test.mjs"`) covering
  default blocking, missing target binding, the confirmation string never being
  sufficient, the chord denylist, single-chord-per-call enforcement, timeout
  clamping, the closed argv surface, and the claim that no clipboard text can
  survive redaction. `tests/native-mirror.test.mjs` parses `Program.cs` and fails
  if the JS and native allowlists/denylists drift apart, if clipboard write is
  reintroduced, or if a fail-closed wait / secure-desktop branch disappears.

### Fixed
- **wait-for could observe the wrong window after a handle reuse.** The PID is
  now resolved and bound at call time even when the caller omitted `expectedPid`,
  and every poll re-verifies HWND+PID. A reuse aborts with
  `blockedBy=handle-reused` instead of silently timing out. For `window-gone`
  with a bound PID, a vanished original target and a reused handle are reported
  distinctly; without a PID a still-live handle is never reported as "gone".
- **element-gone could report success without ever seeing the element.** A null
  UIA root, an exhausted 4000-node budget or an unreadable subtree now yields
  `uiaComplete:false` and is treated as "state unknown", never as satisfaction.
- **The secure-desktop check only tested whether a desktop could be opened.**
  It now also compares the input desktop's name with the thread's desktop name
  and refuses whenever either identity cannot be resolved.
- **Clipboard write removed.** A plaintext payload would have to cross the
  helper's process command line, readable by any local process. The verb, flag,
  tool, tests and docs were removed rather than claiming the path was safe.
- **Validation record corrected.** The stale package hash, the old test count,
  thenKeys-based results and the clipboard-write rows were removed; §8 now
  reports only what was actually run on this tree.
- **Negative numeric arguments were silently dropped by the helper.**
  `ParseFlags` treated any token starting with `-` as a flag, so
  `--notches -3` produced `notches=true` plus a stray `-3` flag and the caller's
  negative value never arrived. A token is now a flag only when it is a dash
  followed by a letter, `--flag=value` is supported so a dash-prefixed value
  stays a value, and an unlisted flag is rejected instead of being ignored.
- The helper now also rejects flags outside each verb's own allowlist, so a
  bridge bug cannot widen the native surface on its own.

### Not implemented (deliberately)
- OCR: no engine is bundled and no arbitrary image path or full-screen image is
  accepted. The targeted capture's session image is the hand-off point.
- App launch, process / registry / filesystem inventory, arbitrary shell or
  executable, batch action plans, full-screen capture, free-text typing.
- Clipboard **write**: no verb, no tool. Plaintext on a helper command line is
  not an acceptable transport, so none was added.
- No plugin-side approval ledger, control session, global keyboard hook or
  desktop overlay.

### Verified
- Helper build: 0 warnings, 0 errors.
- Read-only verbs exercised on a real machine (`capabilities`, `list-displays`,
  `wait-for` including the timeout and bad-argument paths).
- `send-keys` and `clipboard-read` were exercised only on their refusal and
  dryRun paths — **no real key injection and no real clipboard read was
  performed**. The WaitFor handle-reuse and secure-desktop code paths are
  covered by source-shape guards and pure logic, not by a live handle-reuse or
  lock-screen fixture. See `docs/VALIDATION.md` §8.

## [0.1.1] - 2026-10-09

### Fixed
- **Targeted capture no longer fails when resizing.** `ResizeIfNeeded`,
  `MeasureEvidence`, `ProcessOverlay` and the final size read used
  `Image.FromFile(path)` / `new Bitmap(path)`, which makes GDI+ hold a lock on
  the source file for the object's lifetime. Any later write back to the same
  path threw `A generic error occurred in GDI+`. This reproduced whenever
  `maxDimension` was smaller than the window (the resize branch); captures at
  the default size appeared to work.
- Added `LoadDetached(path)`: reads bytes into memory, decodes from a
  `MemoryStream`, and copies into a fresh 32bpp ARGB bitmap with no file handle
  retained. All read-the-same-file-you-write paths now use it, so the capture
  and the in-place resize no longer conflict.
- Error diagnostics from the helper now include the exception type and a
  single-line stack, so a failure names its own cause instead of a bare message.

### Changed
- Manifest version `0.1.0` → `0.1.1`; the install ZIP and `entry.json` were
  regenerated with the official packer and re-validated as an archive.

### Verified
- Live validation against Hana `1.0.6-beta` on Windows 11 x64 after a clean
  reinstall and host restart. See `docs/VALIDATION.md`.

## [0.1.0] - 2026-10-09

Initial public candidate: a Hana **v2 App** that supplements the native
`computer` tool.

### Added
- `native-src/`: a single closed-verb C# helper (.NET 8, win-x64) merging the
  two non-substitutable capability sets —
  - `list-windows`, `capture-window`, `uia-tree`, `uia-find`, `hit-test`,
    `verify-target` (observation / diagnosis, read-only);
  - `manage-window`, `mouse-drag`, `mouse-wheel` (mutation, **dryRun by
    default**).
- `app/hana-desktop-companion/`: 8 namespaced Agent tools
  (`desktop_windows_list`, `desktop_window_capture`, `desktop_uia_tree`,
  `desktop_uia_find`, `desktop_hit_probe`, `desktop_window_manage`,
  `desktop_mouse_drag`, `desktop_mouse_wheel`).
- `lib/helper.js`: bridge with a fixed verb → flag whitelist; no shell, no
  caller-supplied executable path, control characters stripped, integers
  coerced before they reach Win32.
- `lib/gates.js`: app-level real-input gate. `allowRealInput` defaults **off**,
  `actionPolicy` risk tiering, fresh `hwnd` + `expectedPid` binding required,
  and the model must echo a confirmation phrase — which is an acknowledgement
  only and never substitutes for the host's per-call review.
- Target-window-bound `PrintWindow(PW_RENDERFULLCONTENT)` capture, delivered as
  a session file; explicit `wakeIfNeeded` for minimized/hidden windows with
  `SetWindowPlacement` restore; blank-evidence rejection; PID/HWND reuse guard;
  no silent full-desktop fallback.
- Per-monitor-DPI-aware hit testing (`WindowFromPoint` → `GA_ROOT`) re-run
  immediately before injection, refusing when focus has drifted.
- Graceful `WM_CLOSE` window close, with title/foreground matching rejected for
  this irreversible action.
- `README.md` (Chinese) and `README_EN.md`; `LICENSE` (MIT) plus
  `LICENSE.upstream.md` attribution; `scripts/check-environment.ps1`.

### Safety notes
- The three mutating tools declare neither `readOnly` nor `auto:"allow"`, so the
  host reviews every call.
- No arbitrary command execution, no scheduled/background screenshots, no
  external vision API, no reading of the upstream plugins' private config or
  data, no password-field values in output, no input text or screenshot content
  in logs.
- Ordinary click / type / invoke remain the job of the native `computer` tool;
  this App deliberately does not reimplement them.
