# Changelog

All notable changes to this project are documented here.
The format follows Keep a Changelog; this project uses Semantic Versioning.

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
