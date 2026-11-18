English version of [README.md](README.md).

# Hana Desktop Companion

A Windows desktop-observation and guarded-action **v2 App** for [HanaAgent](https://github.com/liliMozi/openhanako). It closes the loop of "targeted screenshot → read-only UIA diagnosis → advanced action" inside a conversation. It **supplements** rather than replaces Hana's native `computer` tool: ordinary click / type / control-invoke should still go through `computer` (under the host's own lease approval). This App does only what `computer` cannot currently do reliably.

Plugin repository: <https://github.com/zhigong0506/hana-desktop-companion> · OpenHanako project: <https://github.com/liliMozi/openhanako>

> ⚠️ Windows x64 only, and requires the **.NET 8 Windows Desktop Runtime x64** (same dependency as [hana-window-capture](https://github.com/zhigong0506/hana-window-capture)).

## Capabilities

- **Targeted capture of occluded windows**: `PrintWindow(PW_RENDERFULLCONTENT)` captures a single window's own pixels by HWND, even when fully covered and not in the foreground. Never a silent full-desktop fallback.
- **HWND+PID identity binding**: capture and actions require a fresh handle plus the expected PID; a handle reused by the system is explicitly rejected, preventing the wrong window from being grabbed or operated.
- **Read-only UIA diagnosis**: element tree and keyword find (role / name / className / patterns / bounds) for cases where the native channel exposes no semantics. Read-only, zero injection.
- **Per-monitor-DPI hit testing**: given a physical pixel, returns the topmost window in Z-order at that point — so you can confirm "whose pixel is this right now" before an advanced action.
- **Multi-monitor inventory**: virtual-desktop bounds plus each monitor's bounds / work area (physical pixels), primary flag, effective DPI and scale. Pure observation.
- **Bounded wait-for**: wait for a window to disappear / become visible / become minimized / take the foreground, for a title to contain a string, for a UIA element to appear or vanish, or for a pixel to hit the bound window. Capped at 30s and returns `timedOut`. It returns an observation only — it never acts and never continues a plan.
- **Restricted global shortcuts**: exactly one chord per call (e.g. `ctrl+shift+s`), inside a closed key/modifier allowlist. Any chord carrying the Win modifier, and a deny list of system-level chords (Win+L, Alt+F4, Ctrl+Shift+Esc, Ctrl+Alt+Delete, …), is refused; the bound window must actually be the foreground window right before injection.
- **Clipboard read**: separate from keyboard input, treated as sensitive, off by default, truncated. Be aware that the text it reads is returned as the tool result, so it does enter the host/model context — this App only guarantees it is not written into a log line or an error detail. **Clipboard write is not implemented**: it would have to carry plaintext on the helper's command line, where any local process can read it.
- **Guarded advanced actions**: drag, wheel (semantics UIA cannot express) and window management (move / resize / minimize / maximize / restore / graceful `WM_CLOSE`). **All dryRun by default**; real injection needs `execute=true` plus every gate.
- **Screenshots delivered as session files**: the PNG is copied into the session as an artifact, readable by the model and retained for the user.

## Requirements

Missing any "Required" row means the App will not work.

| Item | Requirement | Status |
|---|---|---|
| Hana host | HanaAgent `1.0.6-beta` or later (v2 App system) | Required; older versions are marked incompatible by the manifest |
| OS | Windows 10/11 x64 | Officially supported |
| Architecture | x64 / AMD64 | Required |
| .NET runtime | .NET 8 **Windows Desktop Runtime x64** | Required; plain .NET Runtime / ASP.NET Core Runtime are not substitutes |
| Capability | `app/process.spawn` | Required — the helper is a bundled external process. This is a **spawn-time** gate: grant changes take effect on the App process's next (re)start |
| Capability | `app/tools.expose-to-model` | Required, otherwise tools never enter the model loop |
| Capability | `app/session.stage-file` | Needed for screenshot delivery |
| Model | Supports tool calls | Required |
| Model | Supports image input | Strongly recommended; without it PNGs are still produced but may not be understood |
| Session | New session after install / grant change | Strongly recommended; old sessions may hold a frozen tool list |

## Pre-install self-check

```powershell
powershell -ExecutionPolicy Bypass -File .\scripts\check-environment.ps1
```

`ok` should be `true`, and `windows`, `x64Process`, `dotnet8WindowsDesktopRuntime`, `nativeHelperExists`, `nativeHelperSmokeTest` should all be `true`. You can also confirm the runtime with `dotnet --list-runtimes` (needs `Microsoft.WindowsDesktop.App 8.0.x`).

## Tools (13)

| Tool | Risk tier | Default behavior |
|---|---|---|
| `desktop_windows_list` | read (readOnly) | returns window list |
| `desktop_window_capture` | observation (not marked readOnly, because wake can briefly reveal a hidden window) | captures + delivers a session file |
| `desktop_uia_tree` / `desktop_uia_find` / `desktop_hit_probe` | read (readOnly) | diagnostic data, zero injection |
| `desktop_displays_list` | read (readOnly) | virtual desktop + per-monitor geometry and DPI |
| `desktop_wait_for` | read (readOnly) | bounded polling, observation only |
| `desktop_capabilities` | read (readOnly) | capability and gate-state receipt |
| `desktop_window_manage` | sensitive / close is irreversible | **dryRun by default**; mutates only with `execute=true` + all gates |
| `desktop_mouse_drag` / `desktop_mouse_wheel` | destructive | **dryRun by default**, as above |
| `desktop_send_keys` | sensitive | **dryRun by default**; needs a foreground hwnd+expectedPid binding and passes the chord screen first |
| `desktop_clipboard_read` | sensitive | **dryRun by default**; reads truncated, receipts carry sizes only |

## Safety and privacy boundaries (defense in depth; host review is always the outer layer)

- The three pointer/window mutating tools declare neither `readOnly` nor `auto:"allow"` — the host reviews **every call**. Observation tools (including `desktop_wait_for`, `desktop_displays_list`, `desktop_capabilities`) declare `readOnly`.
- The App's own `allowRealInput` switch defaults **OFF**: with it off, mutating tools return a preview plan and never move the system cursor, inject keys, or touch the clipboard. Enabling it only lifts this in-App gate; it cannot bypass host review.
- The default `actionPolicy` (`destructive-only`) does **not** admit sensitive actions, so keyboard and clipboard verbs need an explicit switch to `sensitive-and-destructive` even after `allowRealInput` is on.
- Keyboard input is confined to a fixed key allowlist (letters, digits, F1–F12, navigation keys) and a fixed modifier allowlist (ctrl/alt/shift). **Every chord carrying the Win modifier is refused**, plus a deny list of system-level chords (Win+L, Win+R, Alt+F4, Alt+Tab, Ctrl+Shift+Esc, Ctrl+Alt+Delete, Ctrl+Esc, Alt+Space, …). Each call is limited to a single chord. Free-text typing is not offered.
- Before injecting keys the helper verifies the bound window **is** the current foreground window and re-checks the PID; otherwise it refuses (`target-not-foreground` / `pid-changed`). On a secure desktop (lock screen, UAC) keyboard and clipboard operations are refused.
- Clipboard: **read only**, sensitive and off by default, capped at 4000 characters (default 2000). Receipts report sizes and truncation; the text itself is returned to the caller and does reach the host/model context. This App does not write it to a log or an error detail, and makes no claim about the host's own session storage. The clipboard is process-global, so the bound HWND is an audit anchor, not a delivery target.
- A confirmation phrase echoed by the model is an acknowledgement only — **never** user approval.
- Real input requires a fresh `hwnd` + `expectedPid` binding and re-runs `WindowFromPoint` immediately before injection; if focus drifts and the pixel no longer resolves to the target, it refuses (verified live).
- `close` demands an explicit `hwnd` and rejects title/foreground matching; it uses `WM_CLOSE`, so the app may still prompt and the user may cancel.
- Wake/restore for capture uses `SetWindowPlacement` for exact restoration. No full-desktop capture. Password-field values are never read. Action logs never store input text or screenshot content.
- No arbitrary execution: the helper accepts a fixed verb whitelist only. No shell, no scripts, no scheduled background screenshots, no privilege elevation, no bypassing UAC / secure desktop / DRM.

## Support scope and known limits

**Officially supported**: Win32 / Electron / Chromium / WebView2 top-level windows on Windows 10/11 x64; capture of visible windows occluded by others; explicit wake (`wakeIfNeeded=true`) of minimized/hidden windows with placement restore; drag, wheel and window management (dryRun by default, per-call review).

**Conditional**: GPU-accelerated windows depend on whether the app honors `PrintWindow` — you may get a stale frame or a black image (black evidence is rejected rather than delivered). High-integrity / elevated windows cannot be read by a normal-privilege process, and this App never auto-elevates.

**Not supported**: macOS, Linux, Windows x86 / ARM64 (unverified); UAC secure desktop, lock screen and other secure-desktop content; DRM/HDCP-protected or screenshot-blocked content; full-screen or target-less capture; persistent background observation or timed screenshots.

**Deliberately not implemented**: OCR (no engine is bundled and no arbitrary image path or full-screen image is accepted — hand the session image from `desktop_window_capture` to the host or an external OCR step); app launch; process / registry / filesystem inventory; arbitrary shell or executable; batch action plans; free-text typing; clipboard write (it would have to carry plaintext on the helper's command line). `desktop_capabilities` reports these as unsupported rather than pretending otherwise.

**Honest verification note**: drag's *visible* selection effect and wheel's *one successful render* were not separately captured in a live session — the Windows foreground lock prevented the test process from stably raising the self-built fixture, so the hit guard correctly refused blind injection at drifted coordinates (by design, not a defect). Both share the same helper Win32 injection path as the evidenced window actions and pass at the logic level. See [docs/VALIDATION.md](docs/VALIDATION.md).

## Install

1. Confirm the matrix above (especially Windows x64 and .NET 8 Windows Desktop Runtime x64).
2. Download `app-hana-desktop-companion-<version>.zip` from the repository's Releases page (<https://github.com/zhigong0506/hana-desktop-companion/releases>), or use the same file in `dist/`.
3. Hana desktop → Apps → local install, select the ZIP. The install review lists exactly the three capabilities above — confirm and grant.
4. After granting `app/process.spawn`, restart the App process (Reload on its detail page) for it to take effect.
5. Use a **new session** so the tool list refreshes.

Alternatively place `app/hana-desktop-companion/` into `<HANA_HOME>/apps/` and Reload (local-directory installs support Reload).

## Build from source

Requires Windows x64, .NET 8 SDK, Node.js (only for the official Hana App toolchain; no `npm install`).

```powershell
# 1) compile the native helper (read-only observation + dryRun by default; no arbitrary command)
dotnet build native-src/HanaDesktopCompanion.Helper.csproj -c Release
# output in native-src/bin/Release/net8.0-windows/win-x64/:
#   hana-desktop-companion-helper.{exe,dll,deps.json,runtimeconfig.json}
# copy into app/hana-desktop-companion/native/

# 2) pure-logic tests (no desktop, no clipboard, no injection)
node --test "tests/*.test.mjs"

# 3) official validation and packaging (needs hana-app-creator from an installed Hana)
#    validate_app.mjs --dir app/hana-desktop-companion  →  pack_app.mjs
# produces the install ZIP + entry.json found in dist/
```

## Repository layout

```text
hana-desktop-companion/
├─ README.md / README_EN.md
├─ LICENSE / CHANGELOG.md
├─ scripts/check-environment.ps1
├─ app/hana-desktop-companion/        # installable v2 App
│  ├─ manifest.json                   # capabilities + settings schema
│  ├─ index.js                        # 13 tool registrations + action-gate flow
│  ├─ lib/helper.js                   # fixed-verb-whitelist helper bridge
│  ├─ lib/gates.js                    # app-level real-input gate (default off)
│  ├─ lib/shortcuts.js                # key/chord screen (pure, mirrored by the helper)
│  ├─ lib/waitfor.js                  # wait-for conditions and timeout bounds (pure)
│  ├─ lib/clipboard.js                # clipboard bounds and redaction (pure)
│  ├─ native/                         # committed build output (install without building)
│  ├─ sdk/                            # bundled official Hana App SDK
│  ├─ assets/icon.svg
│  └─ LICENSE.upstream.md             # MIT attribution for the two upstream plugins
├─ native-src/                        # C# helper source (Program.cs + csproj)
├─ tests/                             # pure-logic tests (node --test, no side effects)
├─ dist/                              # official pack output: install ZIP + entry.json
└─ docs/VALIDATION.md                 # validation report (with honest caveats)
```

No `bin/`, `obj/`, test fixtures, runtime screenshots or personal config in the repo or packages.

## Provenance

Rewritten (not ported) by merging the non-substitutable techniques of two community v1 plugins, dropping their bespoke approval ledger / control-session / cursor-overlay scaffolds — in v2 those boundaries belong to the host capability ledger. No runtime dependency on the upstream directories; their private config and data are never read.

- desktop-orchestrator (MIT, author "Ganlin & ChatGPT", local plugin, not publicly published): UIA read-only tree, DPI-aware `WindowFromPoint` hit guard, `mouse_event` injection, `ShowWindow/SetWindowPos/WM_CLOSE`.
- [hana-window-capture](https://github.com/zhigong0506/hana-window-capture) (MIT, © 2026 zhigong0506): `PrintWindow(PW_RENDERFULLCONTENT)` targeted capture, PID/HWND reuse guard, explicit wake with `SetWindowPlacement` restore, blank-evidence rejection.

Full terms in the respective upstream repositories; this App is MIT — [LICENSE](LICENSE).

## License

MIT License.

## Note

This is a community plugin, independent of the official OpenHanako project; OpenHanako/HanaAgent naming and ownership follow the official repository: <https://github.com/liliMozi/openhanako>. No test screenshots, personal window images or credentials are uploaded.
