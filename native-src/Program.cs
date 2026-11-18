using System.Diagnostics;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Windows.Automation;

namespace HanaDesktopCompanion;

// Unified, closed-verb native helper for the hana-desktop-companion v2 App.
//
// It merges the two things that genuinely cannot be done from a sandboxed Node
// process and are NOT already reliably covered by Hana's native `computer`:
//   1. PrintWindow(PW_RENDERFULLCONTENT) capture of a SPECIFIC top-level window,
//      including occluded ones, with PID/HWND binding and no silent full-screen
//      fallback (from hana-window-capture).
//   2. Read-only UIA tree/find, per-monitor-DPI hit-testing, and the guarded
//      real-input verbs (drag / wheel / window management) that UIA cannot
//      express (from desktop-orchestrator).
//
// It deliberately does NOT re-implement the v1 plugins' bespoke approval store,
// control-session ledger, glowing-cursor overlay, or vision-API routing. Under
// v2 those boundaries are owned by the Hana host capability ledger and the
// App's sessionPermission review. The helper is a mechanical executor only.
//
// SECURITY CONTRACT enforced here:
//   - Fixed verb whitelist; nothing is passed to a shell, and no path is ever
//     taken from the caller as an executable.
//   - Every input/window verb binds to a decimal HWND and a PID resolved at call
//     time; a mismatch aborts before any injection. wait-for re-verifies that
//     binding on every poll and aborts (rather than timing out) when the handle
//     has been reused by another process.
//   - Blind coordinate actions re-run WindowFromPoint (root owner) immediately
//     before injection and refuse if the pixel no longer resolves to the
//     intended target window.
//   - Process is set per-monitor DPI aware (V2) at startup so every coordinate
//     read and written is in PHYSICAL pixels, matching UIA and SetCursorPos.
//
// 0.2.0 additions, all inside the same closed surface:
//   - list-displays: pure observation (virtual desktop + per-monitor bounds,
//     work area, effective DPI). No injection, no window touched.
//   - wait-for: bounded polling over UIA / window / hit-test conditions. It
//     returns an observation and stops; it never performs an action and never
//     continues a plan on its own.
//   - send-keys: fixed key table + chord denylist. The bound HWND must be the
//     foreground window, re-verified immediately before SendInput. Any chord
//     carrying the Win modifier is refused outright.
//   - clipboard-read/write: gated operations. The bound window is an audit
//     anchor, not an enforcement boundary: the Windows clipboard is process-global.
//     Read text is returned to the caller; write text arrives over stdin, never
//     argv or disk, and only a size/sequence receipt is returned.
//   - capabilities: a receipt naming the verb surface and what is deliberately
//     not implemented.
//   - ParseFlags no longer mistakes a negative number ("-3") for a flag, and an
//     unknown flag is rejected instead of being silently dropped.
internal static class Program
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    [STAThread]
    private static int Main(string[] args)
    {
        try
        {
            Native.SetProcessDpiAwarenessContext(new IntPtr(-4)); // PER_MONITOR_AWARE_V2
            var verb = args.Length > 0 ? args[0].ToLowerInvariant() : "";
            var flags = ParseFlags(verb, args);
            object result = verb switch
            {
                "list-windows" => ListWindows(flags),
                "capture-window" => CaptureWindow(flags),
                "uia-tree" => UiaTree(flags),
                "uia-find" => UiaFind(flags),
                "hit-test" => HitTest(flags),
                "verify-target" => VerifyTarget(flags),
                "manage-window" => ManageWindow(flags),
                "mouse-drag" => MouseDrag(flags),
                "mouse-wheel" => MouseWheel(flags),
                "list-displays" => ListDisplays(),
                "wait-for" => WaitFor(flags),
                "send-keys" => SendKeys(flags),
                "clipboard-read" => ClipboardRead(flags),
                "clipboard-write" => ClipboardWrite(flags),
                "capabilities" => Capabilities(),
                _ => throw new ArgumentException($"Unsupported verb: {verb}")
            };
            Console.OutputEncoding = new UTF8Encoding(false);
            Console.WriteLine(JsonSerializer.Serialize(result, JsonOptions));
            return 0;
        }
        catch (Exception error)
        {
            var stack = (error.GetType().FullName + " @ " + (error.StackTrace ?? "no-stack")).Replace("\n", " | ");
            Console.Error.WriteLine(JsonSerializer.Serialize(new { ok = false, error = error.Message, stack }, JsonOptions));
            return 1;
        }
    }

    private const string HelperVersion = "0.2.1";

    // Per-verb closed flag surface, mirroring the JS bridge allowlist. An
    // unlisted flag is rejected here too, so a bridge bug cannot widen the
    // helper's surface on its own.
    private static readonly Dictionary<string, string[]> AllowedFlags = new(StringComparer.OrdinalIgnoreCase)
    {
        ["list-windows"] = new[] { "query", "limit", "includeMinimized", "includeHidden" },
        ["capture-window"] = new[] { "hwnd", "title", "foreground", "expectedPid", "output", "maxDimension", "disableAutoCrop", "allowVisibleFallback", "wakeIfNeeded" },
        ["uia-tree"] = new[] { "hwnd", "title", "foreground", "expectedPid", "maxElements", "includeOffscreen" },
        ["uia-find"] = new[] { "hwnd", "title", "foreground", "expectedPid", "query", "role", "maxElements" },
        ["hit-test"] = new[] { "x", "y" },
        ["verify-target"] = new[] { "hwnd", "title", "foreground", "expectedPid", "x", "y" },
        ["manage-window"] = new[] { "action", "hwnd", "title", "foreground", "expectedPid", "x", "y", "width", "height", "execute" },
        ["mouse-drag"] = new[] { "fromX", "fromY", "toX", "toY", "button", "steps", "hwnd", "expectedPid", "execute" },
        ["mouse-wheel"] = new[] { "x", "y", "notches", "axis", "hwnd", "expectedPid", "execute" },
        ["list-displays"] = Array.Empty<string>(),
        ["wait-for"] = new[] { "condition", "hwnd", "title", "foreground", "expectedPid", "query", "role", "x", "y", "timeoutMs", "intervalMs" },
        ["send-keys"] = new[] { "chord", "holdMs", "hwnd", "expectedPid", "execute" },
        ["clipboard-read"] = new[] { "hwnd", "expectedPid", "maxChars", "execute" },
        ["clipboard-write"] = new[] { "hwnd", "expectedPid", "maxChars", "execute" },
        ["capabilities"] = Array.Empty<string>(),
    };

    // A token starts a new flag only when it is a dash followed by a letter
    // ("--x", "-x"). A negative number ("-3") is therefore a VALUE. The old
    // rule ("any leading dash") silently swallowed "--notches -3" and turned
    // "-3" into a stray flag, so the caller's negative value never arrived.
    private static bool IsFlagToken(string token)
    {
        if (token.Length < 2 || token[0] != '-') return false;
        var index = token[1] == '-' ? 2 : 1;
        return index < token.Length && char.IsLetter(token[index]);
    }

    private static Dictionary<string, string?> ParseFlags(string verb, string[] args)
    {
        if (!AllowedFlags.TryGetValue(verb, out var allowed))
            throw new ArgumentException($"Unsupported verb: {verb}");

        var result = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
        for (var i = 1; i < args.Length; i++)
        {
            var token = args[i];
            if (!IsFlagToken(token))
                throw new ArgumentException($"Unexpected positional argument: {token}");

            var body = token.TrimStart('-');
            string key;
            string? value = null;
            var separator = body.IndexOf('=');
            if (separator >= 0)
            {
                // --text=value keeps a value that itself begins with a dash
                // unambiguous (used for free-form clipboard text).
                key = body.Substring(0, separator);
                value = body.Substring(separator + 1);
            }
            else
            {
                key = body;
                if (i + 1 < args.Length && !IsFlagToken(args[i + 1])) value = args[++i];
            }

            if (!allowed.Contains(key, StringComparer.OrdinalIgnoreCase))
                throw new ArgumentException($"Unsupported flag for '{verb}': --{key}");

            result[key.ToLowerInvariant()] = value ?? "true";
        }
        return result;
    }

    private static string Require(Dictionary<string, string?> o, string key) =>
        o.TryGetValue(key, out var v) && !string.IsNullOrWhiteSpace(v)
            ? v
            : throw new ArgumentException($"Missing required argument: --{key}");

    private static bool Flag(Dictionary<string, string?> o, string key) =>
        o.TryGetValue(key, out var v) && !string.Equals(v, "false", StringComparison.OrdinalIgnoreCase);

    private static int Int(Dictionary<string, string?> o, string key, int fallback) =>
        o.TryGetValue(key, out var v) && int.TryParse(v, out var p) ? p : fallback;

    // HWND + optional expected-PID resolution, shared by every target-bound verb.
    private static WindowInfo ResolveTarget(Dictionary<string, string?> o)
    {
        var selectors = new[] { o.ContainsKey("hwnd"), o.ContainsKey("title"), Flag(o, "foreground") }.Count(x => x);
        if (selectors != 1)
            throw new ArgumentException("Provide exactly one selector: --hwnd, --title, or --foreground.");

        WindowInfo target;
        if (o.TryGetValue("hwnd", out var hwndText))
        {
            if (!long.TryParse(hwndText, out var value) || !Native.IsWindow(new IntPtr(value)))
                throw new InvalidOperationException($"Window handle is unavailable: {hwndText}");
            target = DescribeWindow(new IntPtr(value));
        }
        else if (Flag(o, "foreground"))
        {
            target = DescribeWindow(Native.GetForegroundWindow());
        }
        else
        {
            var query = Require(o, "title");
            var matches = EnumerateWindows(false)
                .Where(w => w.Title.Contains(query, StringComparison.OrdinalIgnoreCase))
                .ToList();
            if (matches.Count == 0) throw new InvalidOperationException($"No visible top-level window matched title: {query}");
            if (matches.Count > 1) throw new InvalidOperationException($"Window title is ambiguous ({matches.Count} matches). Use hwnd.");
            target = matches[0];
        }

        if (o.TryGetValue("expectedPid", out var pidText) && int.TryParse(pidText, out var expectedPid) && target.Pid != expectedPid)
            throw new InvalidOperationException($"Window PID changed for HWND {target.Hwnd}: expected {expectedPid}, found {target.Pid}. Re-list windows before acting.");

        return target;
    }

    // ---------------- list-windows ----------------

    private static object ListWindows(Dictionary<string, string?> o)
    {
        var includeMinimized = Flag(o, "includeMinimized");
        var includeHidden = Flag(o, "includeHidden");
        var query = o.TryGetValue("query", out var q) ? q : null;
        var limit = Math.Clamp(Int(o, "limit", 40), 1, 100);
        var windows = EnumerateWindows(includeHidden)
            .Where(w => IsCaptureCandidate(w) || (includeMinimized && IsRestorableCandidate(w)) || (includeHidden && IsHiddenCandidate(w)))
            .Where(w => string.IsNullOrWhiteSpace(query)
                || w.Title.Contains(query, StringComparison.OrdinalIgnoreCase)
                || w.ProcessName.Contains(query, StringComparison.OrdinalIgnoreCase))
            .Take(limit)
            .ToList();
        return new { ok = true, count = windows.Count, windows };
    }

    // ---------------- capture-window ----------------

    private static object CaptureWindow(Dictionary<string, string?> o)
    {
        var output = Path.GetFullPath(Require(o, "output"));
        Directory.CreateDirectory(Path.GetDirectoryName(output)!);
        var maxDimension = Math.Clamp(Int(o, "maxDimension", 2560), 640, 4096);
        var disableAutoCrop = Flag(o, "disableAutoCrop");
        var allowVisibleFallback = Flag(o, "allowVisibleFallback");
        var wakeIfNeeded = Flag(o, "wakeIfNeeded");
        var target = ResolveTarget(o);

        if (target.Title == "Program Manager" && target.ProcessName.Equals("explorer.exe", StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("The Program Manager desktop layer is excluded from window capture.");
        if (target.Cloaked)
            throw new InvalidOperationException($"Window is cloaked and cannot provide reliable visual evidence: {target.Hwnd}");

        var hwnd = new IntPtr(long.Parse(target.Hwnd));
        var wasVisible = Native.IsWindowVisible(hwnd);
        var wasMinimized = Native.IsIconic(hwnd);
        var originalForeground = Native.GetForegroundWindow();
        var originalPlacement = Native.GetPlacement(hwnd);
        var awakened = false;

        try
        {
            if (!wasVisible || wasMinimized)
            {
                if (!wakeIfNeeded)
                    throw new InvalidOperationException(wasMinimized
                        ? $"Window is minimized. Pass wakeIfNeeded=true to temporarily restore it for capture: {target.Hwnd}"
                        : $"Window is hidden. Pass wakeIfNeeded=true to attempt a temporary show for capture: {target.Hwnd}");
                Native.ShowWindowAsync(hwnd, Native.SwShowNoActivate);
                awakened = true;
                WaitForWindow(hwnd, 1200);
                if (!Native.IsWindowVisible(hwnd) || Native.IsIconic(hwnd))
                    throw new InvalidOperationException($"Window did not become visible after the wake request: {target.Hwnd}");
            }

            target = DescribeWindow(hwnd);
            if (target.Bounds.Width < 64 || target.Bounds.Height < 48)
                throw new InvalidOperationException($"Window is too small to provide reliable visual evidence: {target.Hwnd}");

            var capture = CaptureBitmap(hwnd, target.Bounds, output);
            var evidence = MeasureEvidence(output);
            var method = "printwindow-fullcontent";
            if ((!capture || evidence.SuspectedBlank) && allowVisibleFallback && Native.GetForegroundWindow() == hwnd)
            {
                CaptureVisiblePixels(target.Bounds, output);
                evidence = MeasureEvidence(output);
                method = "visible-copy";
            }
            if (!capture && method != "visible-copy") throw new InvalidOperationException("PrintWindow failed.");
            if (evidence.SuspectedBlank)
            {
                File.Delete(output);
                throw new InvalidOperationException("Window capture produced a blank image and was rejected as unreliable visual evidence.");
            }

            var overlay = !disableAutoCrop && evidence.BlackRatio >= 0.45 ? ProcessOverlay(output) : (false, false);
            var resized = ResizeIfNeeded(output, maxDimension);
            using var finalImage = LoadDetached(output);
            return new
            {
                ok = true,
                window = target,
                capture = new
                {
                    method,
                    blackRatio = Math.Round(evidence.BlackRatio, 4),
                    transparentBackground = overlay.Item1,
                    cropped = overlay.Item2,
                    resized,
                    width = finalImage.Width,
                    height = finalImage.Height,
                    path = output,
                    bytes = new FileInfo(output).Length,
                    awakened,
                    originalState = wasMinimized ? "minimized" : wasVisible ? "visible" : "hidden",
                    foregroundPreserved = Native.GetForegroundWindow() == originalForeground
                }
            };
        }
        finally
        {
            if (awakened && Native.IsWindow(hwnd))
            {
                var restored = wasMinimized
                    ? Native.SetWindowPlacement(hwnd, ref originalPlacement)
                    : Native.ShowWindowAsync(hwnd, Native.SwHide);
                if (!restored && wasMinimized) Native.ShowWindowAsync(hwnd, Native.SwMinimize);
                Thread.Sleep(80);
            }
        }
    }

    // ---------------- uia-tree / uia-find (READ-ONLY) ----------------

    private static object UiaTree(Dictionary<string, string?> o)
    {
        var target = ResolveTarget(o);
        var max = Math.Clamp(Int(o, "maxElements", 80), 1, 300);
        var includeOffscreen = Flag(o, "includeOffscreen");
        var root = TryGetRoot(long.Parse(target.Hwnd));
        if (root == null) return new { ok = false, error = "window-not-found", window = target };
        var results = new List<Dictionary<string, object>>();
        var count = 0;
        Walk(root, 0, ref count, max, includeOffscreen, results);
        return new { ok = true, window = target, snapshot = Guid.NewGuid().ToString(), count = results.Count, elements = results };
    }

    private static object UiaFind(Dictionary<string, string?> o)
    {
        var target = ResolveTarget(o);
        var query = Require(o, "query").ToLowerInvariant();
        var max = Math.Clamp(Int(o, "maxElements", 80), 1, 300);
        var roleFilter = o.TryGetValue("role", out var r) ? r : null;
        var root = TryGetRoot(long.Parse(target.Hwnd));
        if (root is null) return new { ok = false, error = "window-not-found", window = target };
        var matches = new List<Dictionary<string, object>>();
        var count = 0;
        FindWalk(root, 0, ref count, max, query, roleFilter, matches);
        return new { ok = true, window = target, query, count = matches.Count, matches };
    }

    // ---------------- hit-test / verify-target (OBSERVE-ONLY, no injection) ----------------

    private static object HitTest(Dictionary<string, string?> o)
    {
        var px = Int(o, "x", int.MinValue);
        var py = Int(o, "y", int.MinValue);
        if (px == int.MinValue || py == int.MinValue) throw new ArgumentException("hit-test requires --x and --y (physical pixels).");
        var pt = new Native.Point { X = px, Y = py };
        var leaf = Native.WindowFromPoint(pt);
        var root = leaf != IntPtr.Zero ? Native.GetAncestor(leaf, Native.GaRoot) : IntPtr.Zero;
        var fg = Native.GetForegroundWindow();
        return new
        {
            ok = true,
            point = new { x = px, y = py },
            hitWindow = root != IntPtr.Zero ? DescribeWindow(root) : null,
            foregroundWindow = fg != IntPtr.Zero ? DescribeWindow(fg) : null
        };
    }

    private static object VerifyTarget(Dictionary<string, string?> o)
    {
        var target = ResolveTarget(o);
        var result = new Dictionary<string, object> { ["ok"] = true, ["window"] = target };
        var stillValid = Native.IsWindow(new IntPtr(long.Parse(target.Hwnd)));
        result["stillValid"] = stillValid;
        if (o.ContainsKey("x") && o.ContainsKey("y"))
        {
            var pt = new Native.Point { X = Int(o, "x", 0), Y = Int(o, "y", 0) };
            var leaf = Native.WindowFromPoint(pt);
            var root = leaf != IntPtr.Zero ? Native.GetAncestor(leaf, Native.GaRoot) : IntPtr.Zero;
            var hit = root != IntPtr.Zero ? DescribeWindow(root) : null;
            var matches = hit != null && string.Equals(hit.Hwnd, target.Hwnd, StringComparison.Ordinal);
            result["pointHitTarget"] = matches;
            if (hit != null) result["hitWindow"] = hit;
        }
        return result;
    }

    // ---------------- list-displays (READ-ONLY) ----------------

    private static object ListDisplays()
    {
        var monitors = new List<object>();
        var primaryIndex = -1;
        var index = 0;
        Native.EnumDisplayMonitors(IntPtr.Zero, IntPtr.Zero, (IntPtr hMonitor, IntPtr hdc, ref Native.Rect rect, IntPtr data) =>
        {
            var info = new Native.MonitorInfo { Size = Marshal.SizeOf<Native.MonitorInfo>() };
            if (!Native.GetMonitorInfo(hMonitor, ref info)) return true;
            uint dpiX = 96, dpiY = 96;
            try { Native.GetDpiForMonitor(hMonitor, Native.MdtEffectiveDpi, out dpiX, out dpiY); } catch { /* pre-8.1 or unavailable */ }
            var primary = (info.Flags & Native.MonitorinfofPrimary) != 0;
            if (primary) primaryIndex = index;
            monitors.Add(new
            {
                index = index++,
                primary,
                bounds = new
                {
                    left = info.Monitor.Left,
                    top = info.Monitor.Top,
                    right = info.Monitor.Right,
                    bottom = info.Monitor.Bottom,
                    width = Math.Max(0, info.Monitor.Right - info.Monitor.Left),
                    height = Math.Max(0, info.Monitor.Bottom - info.Monitor.Top)
                },
                workArea = new
                {
                    left = info.Work.Left,
                    top = info.Work.Top,
                    right = info.Work.Right,
                    bottom = info.Work.Bottom,
                    width = Math.Max(0, info.Work.Right - info.Work.Left),
                    height = Math.Max(0, info.Work.Bottom - info.Work.Top)
                },
                dpi = new { x = (int)dpiX, y = (int)dpiY },
                scalePercent = (int)Math.Round(dpiX * 100.0 / 96.0)
            });
            return true;
        }, IntPtr.Zero);

        return new
        {
            ok = true,
            coordinateSpace = "physical-pixels",
            dpiAwareness = "per-monitor-v2",
            virtualScreen = new
            {
                left = Native.GetSystemMetrics(Native.SmXVirtualScreen),
                top = Native.GetSystemMetrics(Native.SmYVirtualScreen),
                width = Native.GetSystemMetrics(Native.SmCxVirtualScreen),
                height = Native.GetSystemMetrics(Native.SmCyVirtualScreen)
            },
            monitorCount = monitors.Count,
            primaryIndex,
            monitors,
            note = "Observation only. Bounds are physical pixels in the virtual-desktop space; a window can straddle monitors."
        };
    }

    // ---------------- wait-for (READ-ONLY, bounded polling) ----------------

    private static readonly HashSet<string> WaitConditions = new(StringComparer.OrdinalIgnoreCase)
    {
        "window-gone", "window-visible", "window-minimized", "window-foreground",
        "title-contains", "element-exists", "element-gone", "point-hits-target"
    };

    private static object WaitFor(Dictionary<string, string?> o)
    {
        var condition = Require(o, "condition").ToLowerInvariant();
        if (!WaitConditions.Contains(condition))
            throw new ArgumentException($"Unsupported wait condition: {condition}. Allowed: {string.Join(", ", WaitConditions)}");

        // Bounded on purpose: a wait is an observation, so it must never become
        // an unbounded blocking call the caller cannot reason about.
        var timeoutMs = Math.Clamp(Int(o, "timeoutMs", 5000), 200, 30000);
        var intervalMs = Math.Clamp(Int(o, "intervalMs", 250), 100, 2000);

        var query = o.TryGetValue("query", out var q) ? q : null;
        var role = o.TryGetValue("role", out var r) ? r : null;
        if ((condition == "title-contains" || condition == "element-exists" || condition == "element-gone")
            && string.IsNullOrWhiteSpace(query))
            throw new ArgumentException($"{condition} requires --query.");

        var px = Int(o, "x", int.MinValue);
        var py = Int(o, "y", int.MinValue);
        if (condition == "point-hits-target" && (px == int.MinValue || py == int.MinValue))
            throw new ArgumentException("point-hits-target requires --x and --y (physical pixels).");

        IntPtr hwnd;
        int? boundPid;
        if (condition == "window-gone")
        {
            // An already-dead handle is the normal case here, so this branch must
            // not go through the throwing ResolveTarget path. A caller-supplied
            // expectedPid is what lets us tell "the original window is gone"
            // apart from "the handle was reused by another process".
            if (!long.TryParse(Require(o, "hwnd"), out var raw))
                throw new ArgumentException("window-gone requires a decimal --hwnd.");
            hwnd = new IntPtr(raw);
            boundPid = o.TryGetValue("expectedPid", out var gonePid) && int.TryParse(gonePid, out var parsedGonePid)
                ? parsedGonePid
                : null;
        }
        else
        {
            var target = ResolveTarget(o);
            hwnd = new IntPtr(long.Parse(target.Hwnd));
            // Always bind the PID resolved here, even when the caller omitted
            // expectedPid: every poll re-verifies HWND+PID, so a reused handle
            // fails closed instead of silently observing another process.
            boundPid = target.Pid;
        }

        var stopwatch = Stopwatch.StartNew();
        var polls = 0;
        object? last = null;
        while (true)
        {
            polls++;
            var (satisfied, observation, failure) = PollWait(condition, hwnd, boundPid, query, role, px, py);
            // A handle that now belongs to a different process aborts the wait:
            // reporting a timeout would look like "nothing happened" while the
            // observation target had already changed underneath us.
            if (failure is not null) return failure;
            last = observation;
            if (satisfied)
                return new
                {
                    ok = true,
                    condition,
                    satisfied = true,
                    timedOut = false,
                    polls,
                    elapsedMs = stopwatch.ElapsedMilliseconds,
                    timeoutMs,
                    intervalMs,
                    observation
                };
            if (stopwatch.ElapsedMilliseconds >= timeoutMs)
                return new
                {
                    ok = true,
                    condition,
                    satisfied = false,
                    timedOut = true,
                    polls,
                    elapsedMs = stopwatch.ElapsedMilliseconds,
                    timeoutMs,
                    intervalMs,
                    observation = last,
                    note = "Observation only: the condition did not hold within the timeout. No action was taken and no plan is continued."
                };
            var remaining = timeoutMs - (int)stopwatch.ElapsedMilliseconds;
            Thread.Sleep(Math.Max(1, Math.Min(intervalMs, remaining)));
        }
    }

    private static (bool Satisfied, object? Observation, object? Failure) PollWait(
        string condition, IntPtr hwnd, int? boundPid, string? query, string? role, int px, int py)
    {
        var hwndText = hwnd.ToInt64().ToString();

        if (condition == "window-gone")
        {
            if (!Native.IsWindow(hwnd))
                return (true, new { hwnd = hwndText, windowExists = false, originalTargetGone = true }, null);

            Native.GetWindowThreadProcessId(hwnd, out var livePid);
            var foundPid = (int)livePid;
            if (boundPid is not null && foundPid != boundPid.Value)
                return (true, new
                {
                    hwnd = hwndText,
                    windowExists = true,
                    originalTargetGone = true,
                    handleReused = true,
                    expectedPid = boundPid.Value,
                    foundPid,
                    note = "The original target is gone; this handle now belongs to a different process."
                }, null);

            return (false, new
            {
                hwnd = hwndText,
                windowExists = true,
                originalTargetGone = false,
                foundPid,
                ambiguous = boundPid is null,
                note = boundPid is null
                    ? "The handle is still alive and no expectedPid was supplied, so the original target cannot be reported as gone."
                    : "The bound window is still alive."
            }, null);
        }

        // Every other condition observes a specific window, so a vanished handle is
        // simply "not satisfied yet" and a reused handle aborts.
        if (!Native.IsWindow(hwnd))
            return (false, new { hwnd = hwndText, windowExists = false }, null);

        Native.GetWindowThreadProcessId(hwnd, out var currentPid);
        var pid = (int)currentPid;
        if (boundPid is not null && pid != boundPid.Value)
            return (false, null, new
            {
                ok = false,
                condition,
                blockedBy = "handle-reused",
                expectedPid = boundPid.Value,
                foundPid = pid,
                note = "The bound handle now belongs to a different process. The wait is aborted rather than observing the wrong window."
            });

        switch (condition)
        {
            case "window-visible":
            case "window-minimized":
            case "window-foreground":
                {
                    var window = DescribeWindow(hwnd);
                    var pidMatched = boundPid is null || window.Pid == boundPid.Value;
                    var satisfied = condition switch
                    {
                        "window-visible" => window.Visible && !window.Minimized,
                        "window-minimized" => window.Minimized,
                        _ => window.Foreground && pidMatched
                    };
                    return (satisfied, new { window, pidMatched }, null);
                }
            case "title-contains":
                {
                    var window = DescribeWindow(hwnd);
                    var matched = window.Title.Contains(query!, StringComparison.OrdinalIgnoreCase);
                    return (matched, new { hwnd = window.Hwnd, title = window.Title, pid = window.Pid, titleMatched = matched }, null);
                }
            case "element-exists":
            case "element-gone":
                {
                    var root = TryGetRoot(hwnd.ToInt64());
                    if (root is null)
                        // UI Automation could not read the window at all. That is
                        // "unknown", never "the element is gone".
                        return (false, new
                        {
                            hwnd = hwndText,
                            uiaRootAvailable = false,
                            uiaComplete = false,
                            note = "UI Automation could not read this window, so the element state is unknown."
                        }, null);

                    var outcome = FindFirstMatch(root, query!, role);
                    var observation = new
                    {
                        hwnd = hwndText,
                        uiaRootAvailable = true,
                        uiaComplete = outcome.Complete,
                        matched = outcome.Match
                    };
                    if (condition == "element-exists") return (outcome.Found, observation, null);
                    // element-gone is only satisfied by a *complete* walk that
                    // found nothing. An exhausted node budget means the tree was
                    // not fully searched, so the state is unknown.
                    return (!outcome.Found && outcome.Complete, observation, null);
                }
            default: // point-hits-target
                {
                    var point = new Native.Point { X = px, Y = py };
                    var leaf = Native.WindowFromPoint(point);
                    var root = leaf != IntPtr.Zero ? Native.GetAncestor(leaf, Native.GaRoot) : IntPtr.Zero;
                    var hit = root != IntPtr.Zero ? DescribeWindow(root) : null;
                    var matches = hit is not null
                        && string.Equals(hit.Hwnd, hwndText, StringComparison.Ordinal)
                        && (boundPid is null || hit.Pid == boundPid.Value);
                    return (matches, new { point = new { x = px, y = py }, hitWindow = hit }, null);
                }
        }
    }

    // Iterative UIA search with an early exit and a hard node budget. The result
    // separates "searched and found nothing" from "could not search completely":
    // a caller must never turn an unreadable tree or an exhausted budget into
    // "the element is gone".
    private sealed record UiaSearchOutcome(bool Found, bool Complete, Dictionary<string, object>? Match);

    private const int UiaNodeBudget = 4000;

    private static UiaSearchOutcome FindFirstMatch(AutomationElement root, string query, string? roleFilter)
    {
        var needle = query.ToLowerInvariant();
        var pending = new Stack<AutomationElement>();
        pending.Push(root);
        var visited = 0;
        var degraded = false;

        while (pending.Count > 0)
        {
            if (visited >= UiaNodeBudget)
                return new UiaSearchOutcome(false, false, null); // budget exhausted: state unknown
            visited++;

            var parent = pending.Pop();
            AutomationElementCollection children;
            try { children = parent.FindAll(TreeScope.Children, Condition.TrueCondition); }
            catch { degraded = true; continue; }

            foreach (AutomationElement child in children)
            {
                try
                {
                    var name = (child.Current.Name ?? "").ToLowerInvariant();
                    var automationId = (child.Current.AutomationId ?? "").ToLowerInvariant();
                    var className = SafeClassName(child).ToLowerInvariant();
                    var role = child.Current.LocalizedControlType ?? "";
                    var roleOk = string.IsNullOrEmpty(roleFilter) || role.Contains(roleFilter, StringComparison.OrdinalIgnoreCase);
                    if (roleOk && (name.Contains(needle) || automationId.Contains(needle) || className.Contains(needle)))
                        return new UiaSearchOutcome(true, true, DescribeElement(child, 0, "el-match"));
                }
                catch { degraded = true; /* one unreadable node makes the walk incomplete */ }
                pending.Push(child);
            }
        }

        return new UiaSearchOutcome(false, !degraded, null);
    }

    // ---------------- manage-window ----------------

    private static object ManageWindow(Dictionary<string, string?> o)
    {
        // Title-based selection is allowed for benign state changes but NEVER for close.
        var action = Require(o, "action").ToLowerInvariant();
        if (action == "close" && !o.ContainsKey("hwnd"))
            throw new InvalidOperationException("close is irreversible and requires an explicit --hwnd (title/foreground matching is rejected).");
        var target = ResolveTarget(o);
        var hwnd = new IntPtr(long.Parse(target.Hwnd));

        // Fail-safe default: mutating verbs only act when execute=true is passed.
        // Without it, return the resolved target + planned action so the guard can
        // be exercised with zero side effects.
        if (!Flag(o, "execute"))
            return new { ok = true, dryRun = true, action, window = target, planned = DescribeManagePlan(action, o) };

        bool ok;
        string detail;
        switch (action)
        {
            case "maximize": ok = Native.ShowWindow(hwnd, Native.SwMaximize); detail = "SW_MAXIMIZE"; break;
            case "minimize": ok = Native.ShowWindow(hwnd, Native.SwMinimize); detail = "SW_MINIMIZE"; break;
            case "restore": ok = Native.ShowWindow(hwnd, Native.SwRestore); detail = "SW_RESTORE"; break;
            case "close":
                ok = Native.PostMessage(hwnd, Native.WmClose, IntPtr.Zero, IntPtr.Zero);
                detail = "WM_CLOSE (graceful; the app may still prompt and the user may cancel)";
                break;
            case "move":
                {
                    var x = Int(o, "x", int.MinValue);
                    var y = Int(o, "y", int.MinValue);
                    if (x == int.MinValue || y == int.MinValue) throw new ArgumentException("move requires --x and --y (physical pixels).");
                    ok = Native.SetWindowPos(hwnd, IntPtr.Zero, x, y, 0, 0, Native.SwpNoSize | Native.SwpNoActivate | Native.SwpNoZorder);
                    detail = $"SetWindowPos move ({x},{y})";
                    break;
                }
            case "resize":
                {
                    var w = Int(o, "width", -1);
                    var h = Int(o, "height", -1);
                    if (w <= 0 || h <= 0) throw new ArgumentException("resize requires positive --width and --height (physical pixels).");
                    ok = Native.SetWindowPos(hwnd, IntPtr.Zero, 0, 0, w, h, Native.SwpNoMove | Native.SwpNoActivate | Native.SwpNoZorder);
                    detail = $"SetWindowPos resize ({w}x{h})";
                    break;
                }
            default:
                throw new ArgumentException($"Unsupported manage action: {action}");
        }
        return new { ok = ok, action, window = target, detail };
    }

    // ---------------- mouse-drag / mouse-wheel (REAL INPUT, guarded) ----------------

    private static object MouseDrag(Dictionary<string, string?> o)
    {
        var fromX = Int(o, "fromX", int.MinValue);
        var fromY = Int(o, "fromY", int.MinValue);
        var toX = Int(o, "toX", int.MinValue);
        var toY = Int(o, "toY", int.MinValue);
        if (new[] { fromX, fromY, toX, toY }.Any(v => v == int.MinValue))
            throw new ArgumentException("mouse-drag requires fromX/fromY/toX/toY (physical pixels).");
        var buttonName = o.TryGetValue("button", out var b) ? b : "left";
        var button = ButtonFlags(buttonName);

        // Fail-safe default: only inject when execute=true is explicitly passed.
        // In preview we still run the hit-window guard so the decision is testable
        // without ever moving the real cursor.
        var preview = !Flag(o, "execute");

        // Guard: if an expected target is supplied, the start pixel must currently
        // resolve to that window; otherwise the drag is blind and is refused.
        object? guardResult = null;
        if (o.ContainsKey("hwnd") || o.ContainsKey("expectedPid") || o.ContainsKey("expectedProcess"))
        {
            var expected = ResolveTarget(o);
            var guard = GuardPoint(fromX, fromY, expected.Hwnd);
            guardResult = new { allowed = guard.Allowed, reason = guard.Reason, expectedHwnd = expected.Hwnd };
            if (!guard.Allowed) return new { ok = false, blockedBy = guard.Reason, guard = guardResult };
        }

        var steps = Math.Clamp(Int(o, "steps", 24), 2, 60);
        if (preview)
            return new { ok = true, dryRun = true, action = "mouse-drag", from = new { x = fromX, y = fromY }, to = new { x = toX, y = toY }, button = buttonName, steps, guard = guardResult };

        Native.SetCursorPos(fromX, fromY);
        Thread.Sleep(60);
        Native.mouse_event(button.Down, 0, 0, 0, IntPtr.Zero);
        try
        {
            Thread.Sleep(80);
            for (var i = 1; i <= steps; i++)
            {
                var px = (int)Math.Round(fromX + (toX - fromX) * (double)i / steps);
                var py = (int)Math.Round(fromY + (toY - fromY) * (double)i / steps);
                Native.SetCursorPos(px, py);
                Thread.Sleep(12);
            }
            Thread.Sleep(80);
        }
        finally
        {
            Native.mouse_event(button.Up, 0, 0, 0, IntPtr.Zero); // always release, even on error
        }
        return new { ok = true, action = "mouse-drag", from = new { x = fromX, y = fromY }, to = new { x = toX, y = toY }, button = buttonName, steps };
    }

    private static object MouseWheel(Dictionary<string, string?> o)
    {
        var x = Int(o, "x", int.MinValue);
        var y = Int(o, "y", int.MinValue);
        if (x == int.MinValue || y == int.MinValue) throw new ArgumentException("mouse-wheel requires --x and --y (physical pixels).");
        var notches = Int(o, "notches", -3);
        var axis = o.TryGetValue("axis", out var ax) && (ax ?? "").Equals("horizontal", StringComparison.OrdinalIgnoreCase) ? "horizontal" : "vertical";
        var preview = !Flag(o, "execute");

        object? guardResult = null;
        if (o.ContainsKey("hwnd") || o.ContainsKey("expectedPid") || o.ContainsKey("expectedProcess"))
        {
            var expected = ResolveTarget(o);
            var guard = GuardPoint(x, y, expected.Hwnd);
            guardResult = new { allowed = guard.Allowed, reason = guard.Reason, expectedHwnd = expected.Hwnd };
            if (!guard.Allowed) return new { ok = false, blockedBy = guard.Reason, guard = guardResult };
        }

        var flag = axis == "horizontal" ? Native.MouseEventHwHEEL : Native.MouseEventWheel;
        var n = Math.Clamp(Math.Abs(notches), 1, 30);

        if (preview)
            return new { ok = true, dryRun = true, action = "mouse-wheel", point = new { x, y }, notches, axis, guard = guardResult };

        var dir = notches < 0 ? -1 : 1;
        var delta = dir * 120;
        var dword = delta < 0 ? unchecked((uint)(4294967296L + delta)) : (uint)delta;
        Native.SetCursorPos(x, y);
        Thread.Sleep(50);
        for (var i = 0; i < n; i++)
        {
            Native.mouse_event(flag, 0, 0, dword, IntPtr.Zero);
            Thread.Sleep(30);
        }
        return new { ok = true, action = "mouse-wheel", point = new { x, y }, notches, axis };
    }

    private static (uint Down, uint Up) ButtonFlags(string? button) => button?.ToLowerInvariant() switch
    {
        "right" => (Native.MouseEventRightDown, Native.MouseEventRightUp),
        "middle" => (Native.MouseEventMiddleDown, Native.MouseEventMiddleUp),
        _ => (Native.MouseEventLeftDown, Native.MouseEventLeftUp)
    };

    // ---------------- send-keys (REAL INPUT, target-bound, chord-screened) ----------------

    private static readonly Dictionary<string, ushort> KeyTable = BuildKeyTable();

    // Canonical (order-insensitive) chords that are never sent. The Win-modifier
    // rule below already refuses every Win chord; these entries stay explicit so
    // the refusal is auditable and mirrored 1:1 by the JS pre-screen.
    private static readonly HashSet<string> DeniedChords = new(StringComparer.Ordinal)
    {
        "alt+f4", "alt+tab", "alt+escape", "alt+space",
        "ctrl+escape", "ctrl+shift+escape", "ctrl+alt+delete", "ctrl+alt+tab", "ctrl+alt+escape",
        "win+l", "win+r", "win+x", "win+e", "win+d", "win+u", "win+p", "win+s", "win+v", "win+a", "win+i",
        "win+tab", "win+space", "win+ctrl+d", "win+ctrl+f4"
    };

    private static Dictionary<string, ushort> BuildKeyTable()
    {
        var table = new Dictionary<string, ushort>(StringComparer.OrdinalIgnoreCase)
        {
            ["enter"] = 0x0D, ["return"] = 0x0D,
            ["tab"] = 0x09,
            ["escape"] = 0x1B, ["esc"] = 0x1B,
            ["space"] = 0x20,
            ["backspace"] = 0x08,
            ["delete"] = 0x2E, ["del"] = 0x2E,
            ["insert"] = 0x2D, ["ins"] = 0x2D,
            ["home"] = 0x24, ["end"] = 0x23,
            ["pageup"] = 0x21, ["pagedown"] = 0x22,
            ["left"] = 0x25, ["up"] = 0x26, ["right"] = 0x27, ["down"] = 0x28
        };
        for (var c = 'a'; c <= 'z'; c++) table[c.ToString()] = (ushort)(0x41 + (c - 'a'));
        for (var d = '0'; d <= '9'; d++) table[d.ToString()] = (ushort)(0x30 + (d - '0'));
        for (var f = 1; f <= 12; f++) table[$"f{f}"] = (ushort)(0x70 + f - 1);
        return table;
    }

    private static string CanonicalKeyName(string key) => key.ToLowerInvariant() switch
    {
        "return" => "enter",
        "esc" => "escape",
        "del" => "delete",
        "ins" => "insert",
        _ => key.ToLowerInvariant()
    };

    private static (bool Ok, ushort[] Modifiers, ushort Key, string? Canonical, string? Reason) ParseChord(string chord)
    {
        var raw = chord.Trim().ToLowerInvariant();
        if (raw.Length == 0) return (false, Array.Empty<ushort>(), 0, null, "Empty key chord.");

        var parts = raw.Split('+', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (parts.Length == 0) return (false, Array.Empty<ushort>(), 0, null, "Empty key chord.");

        var modifiers = new List<ushort>();
        for (var i = 0; i < parts.Length - 1; i++)
        {
            switch (parts[i])
            {
                case "ctrl":
                case "control":
                    if (modifiers.Contains(0x11)) return (false, Array.Empty<ushort>(), 0, null, "Duplicate modifier: ctrl");
                    modifiers.Add(0x11);
                    break;
                case "alt":
                    if (modifiers.Contains(0x12)) return (false, Array.Empty<ushort>(), 0, null, "Duplicate modifier: alt");
                    modifiers.Add(0x12);
                    break;
                case "shift":
                    if (modifiers.Contains(0x10)) return (false, Array.Empty<ushort>(), 0, null, "Duplicate modifier: shift");
                    modifiers.Add(0x10);
                    break;
                case "win":
                case "meta":
                case "cmd":
                    return (false, Array.Empty<ushort>(), 0, null, "The 'win' modifier is refused: no Windows-key chord can be sent through this App.");
                default:
                    return (false, Array.Empty<ushort>(), 0, null, $"Unsupported modifier: {parts[i]}");
            }
        }

        var keyName = parts[^1];
        if (!KeyTable.TryGetValue(keyName, out var virtualKey))
            return (false, Array.Empty<ushort>(), 0, null, $"Unsupported key: {keyName}");

        var canonicalParts = new List<string>();
        if (modifiers.Contains(0x11)) canonicalParts.Add("ctrl");
        if (modifiers.Contains(0x12)) canonicalParts.Add("alt");
        if (modifiers.Contains(0x10)) canonicalParts.Add("shift");
        canonicalParts.Add(CanonicalKeyName(keyName));
        var canonical = string.Join("+", canonicalParts);

        if (DeniedChords.Contains(canonical))
            return (false, Array.Empty<ushort>(), 0, null, $"Chord is on the system-level denylist and is never sent: {canonical}");

        return (true, modifiers.ToArray(), virtualKey, canonical, null);
    }

    private static object SendKeys(Dictionary<string, string?> o)
    {
        var chordText = Require(o, "chord");
        var parsedChord = ParseChord(chordText);
        if (!parsedChord.Ok) throw new ArgumentException(parsedChord.Reason!);
        var plan = (parsedChord.Modifiers, parsedChord.Key, parsedChord.Canonical!);

        var holdMs = Math.Clamp(Int(o, "holdMs", 40), 0, 500);
        var preview = !Flag(o, "execute");

        // A keystroke goes to whatever holds the foreground, so the target must be
        // bound and must actually be that window. Title/foreground matching alone
        // is not enough for real input.
        if (!o.ContainsKey("hwnd") || !o.ContainsKey("expectedPid"))
            throw new ArgumentException("send-keys requires --hwnd and --expectedPid: a keystroke is delivered to the foreground window, so the target must be bound.");

        var target = ResolveTarget(o);
        var hwnd = new IntPtr(long.Parse(target.Hwnd));
        var foregroundNow = Native.GetForegroundWindow();
        var foregroundOk = foregroundNow == hwnd;
        var chord = plan.Item3;

        if (preview)
            return new
            {
                ok = true,
                dryRun = true,
                action = "send-keys",
                chord,
                target = new { target.Hwnd, target.Pid, target.Title },
                targetIsForeground = foregroundOk,
                holdMs,
                note = "No key was injected."
            };

        if (IsSecureDesktopActive())
            return new
            {
                ok = false,
                blockedBy = "secure-desktop",
                chord,
                note = "Keystrokes are refused while a secure desktop (lock screen / UAC prompt) is in front."
            };
        if (!foregroundOk)
            return new
            {
                ok = false,
                blockedBy = "target-not-foreground",
                chord,
                target = new { target.Hwnd, target.Title },
                foregroundWindow = foregroundNow != IntPtr.Zero ? DescribeWindow(foregroundNow) : null,
                note = "The bound window is not the foreground window; refusing to inject keys into an unknown target."
            };

        Native.GetWindowThreadProcessId(hwnd, out var livePid);
        if ((int)livePid != target.Pid)
            return new { ok = false, blockedBy = "pid-changed", expectedPid = target.Pid, foundPid = (int)livePid };

        // Re-check foreground and PID immediately before injection to narrow the
        // focus-stealing race between target resolution and SendInput.
        foregroundNow = Native.GetForegroundWindow();
        if (foregroundNow != hwnd)
            return new { ok = false, blockedBy = "target-not-foreground", chord };
        Native.GetWindowThreadProcessId(hwnd, out livePid);
        if ((int)livePid != target.Pid)
            return new { ok = false, blockedBy = "pid-changed", expectedPid = target.Pid, foundPid = (int)livePid };

        InjectChord(plan.Modifiers, plan.Key, holdMs);
        return new
        {
            ok = true,
            action = "send-keys",
            chord,
            target = new { target.Hwnd, target.Pid, target.Title },
            holdMs
        };
    }

    private static void InjectChord(ushort[] modifiers, ushort key, int holdMs)
    {
        var size = Marshal.SizeOf<Native.Input>();
        if (size != 40)
            throw new InvalidOperationException($"Unexpected INPUT size {size}; refusing to inject keyboard input.");

        var downs = new List<Native.Input>();
        foreach (var modifier in modifiers) downs.Add(Native.KeyInput(modifier, false));
        downs.Add(Native.KeyInput(key, false));
        var ups = new List<Native.Input> { Native.KeyInput(key, true) };
        for (var i = modifiers.Length - 1; i >= 0; i--) ups.Add(Native.KeyInput(modifiers[i], true));

        var downSent = false;
        try
        {
            var sent = Native.SendInput((uint)downs.Count, downs.ToArray(), size);
            downSent = sent > 0;
            if (sent != downs.Count)
                throw new InvalidOperationException($"SendInput accepted {sent} of {downs.Count} key-down events.");
            Thread.Sleep(Math.Max(10, holdMs));
        }
        finally
        {
            // Always try to release every key that could have been pressed, even
            // when SendInput partially succeeds or an exception interrupts hold.
            if (downSent)
            {
                var released = Native.SendInput((uint)ups.Count, ups.ToArray(), size);
                if (released != ups.Count)
                    throw new InvalidOperationException($"SendInput accepted {released} of {ups.Count} key-up events.");
            }
        }
        Thread.Sleep(15);
    }

    // ---------------- clipboard (GATED, TRUNCATED, NEVER ECHOED) ----------------

    private const int ClipboardReadDefaultChars = 2000;
    private const int ClipboardReadMaxChars = 4000;

    private static object ClipboardRead(Dictionary<string, string?> o)
    {
        // The bound window is an audit anchor ("which context is this text for"),
        // not an enforcement boundary: the Windows clipboard is process-global.
        var target = ResolveTarget(o);
        var maxChars = Math.Clamp(Int(o, "maxChars", ClipboardReadDefaultChars), 1, ClipboardReadMaxChars);
        if (!Flag(o, "execute"))
            return new
            {
                ok = true,
                dryRun = true,
                action = "clipboard-read",
                maxChars,
                target = new { target.Hwnd, target.Pid, target.Title },
                note = "The clipboard was not read. Pass execute=true after the App gate and host review."
            };

        if (IsSecureDesktopActive())
            return new { ok = false, blockedBy = "secure-desktop", action = "clipboard-read" };

        string? content = null;
        for (var attempt = 0; attempt < 3 && content is null; attempt++)
        {
            try
            {
                content = System.Windows.Forms.Clipboard.ContainsText()
                    ? System.Windows.Forms.Clipboard.GetText()
                    : string.Empty;
            }
            catch
            {
                Thread.Sleep(60); // clipboard held by another process; retry briefly
            }
        }
        if (content is null)
            return new { ok = false, error = "clipboard-unavailable", note = "The clipboard is held by another process. Nothing was read." };

        var truncated = content.Length > maxChars;
        var slice = truncated ? content.Substring(0, maxChars) : content;
        return new
        {
            ok = true,
            action = "clipboard-read",
            target = new { target.Hwnd, target.Pid, target.Title },
            chars = content.Length,
            returnedChars = slice.Length,
            truncated,
            text = slice
        };
    }

    private static bool IsSecureDesktopActive()
    {
        // Opening the input desktop is not proof of anything by itself: it only
        // says we could open *a* desktop. Compare its identity with the desktop
        // this thread is attached to, and refuse whenever either identity cannot
        // be resolved (fail closed rather than guess).
        var input = Native.OpenInputDesktop(0, false, Native.DesktopReadObjects);
        if (input == IntPtr.Zero) return true;

        try
        {
            var thread = Native.GetThreadDesktop(Native.GetCurrentThreadId());
            if (thread == IntPtr.Zero) return true;

            try
            {
                var inputName = Native.GetUserObjectName(input);
                var threadName = Native.GetUserObjectName(thread);
                if (string.IsNullOrEmpty(inputName) || string.IsNullOrEmpty(threadName)) return true; // cannot verify
                return !string.Equals(inputName, threadName, StringComparison.OrdinalIgnoreCase);
            }
            finally
            {
                Native.CloseDesktop(thread);
            }
        }
        finally
        {
            Native.CloseDesktop(input);
        }
    }

    private const int ClipboardWriteMaxBytes = 64 * 1024;
    private const int ClipboardWriteMaxChars = 8192;

    private static object ClipboardWrite(Dictionary<string, string?> o)
    {
        var target = ResolveTarget(o);
        var maxChars = Math.Clamp(Int(o, "maxChars", ClipboardWriteMaxChars), 1, ClipboardWriteMaxChars);
        if (!Flag(o, "execute"))
            return new { ok = true, dryRun = true, action = "clipboard-write", maxChars, target = new { target.Hwnd, target.Pid, target.Title }, note = "Clipboard unchanged; stdin was not read." };

        if (IsSecureDesktopActive())
            return new { ok = false, blockedBy = "secure-desktop", action = "clipboard-write" };

        byte[] payload;
        try
        {
            using var buffer = new MemoryStream();
            var input = Console.OpenStandardInput();
            var chunk = new byte[4096];
            while (true)
            {
                var read = input.Read(chunk, 0, chunk.Length);
                if (read == 0) break;
                if (buffer.Length + read > ClipboardWriteMaxBytes)
                    return new { ok = false, error = "payload-too-large", maxBytes = ClipboardWriteMaxBytes };
                buffer.Write(chunk, 0, read);
            }
            payload = buffer.ToArray();
        }
        catch
        {
            return new { ok = false, error = "stdin-unavailable" };
        }

        try
        {
            var encoding = new UTF8Encoding(false, true);
            var offset = payload.Length >= 3 && payload[0] == 0xEF && payload[1] == 0xBB && payload[2] == 0xBF ? 3 : 0;
            var text = encoding.GetString(payload, offset, payload.Length - offset);
            if (text.Length == 0) return new { ok = false, error = "text-empty" };
            if (text.Length > maxChars || text.Length > ClipboardWriteMaxChars)
                return new { ok = false, error = "payload-too-large", maxChars = ClipboardWriteMaxChars, maxBytes = ClipboardWriteMaxBytes };

            var seqBefore = Native.GetClipboardSequenceNumber();
            try
            {
                System.Windows.Forms.Clipboard.SetText(text, System.Windows.Forms.TextDataFormat.UnicodeText);
            }
            catch (System.Runtime.InteropServices.ExternalException)
            {
                Thread.Sleep(60);
                try { System.Windows.Forms.Clipboard.SetText(text, System.Windows.Forms.TextDataFormat.UnicodeText); }
                catch (System.Runtime.InteropServices.ExternalException)
                {
                    Thread.Sleep(60);
                    try { System.Windows.Forms.Clipboard.SetText(text, System.Windows.Forms.TextDataFormat.UnicodeText); }
                    catch (System.Runtime.InteropServices.ExternalException) { return new { ok = false, error = "clipboard-unavailable" }; }
                }
            }
            var seqAfter = Native.GetClipboardSequenceNumber();
            return new { ok = true, action = "clipboard-write", target = new { target.Hwnd, target.Pid, target.Title }, chars = text.Length, bytes = payload.Length - offset, seqBefore, seqAfter, changed = seqAfter != seqBefore };
        }
        catch (DecoderFallbackException)
        {
            return new { ok = false, error = "invalid-utf8" };
        }
        catch
        {
            return new { ok = false, error = "clipboard-write-failed" };
        }
    }

    // ---------------- capabilities (READ-ONLY receipt) ----------------

    private static object Capabilities() => new
    {
        ok = true,
        helper = "hana-desktop-companion-helper",
        version = HelperVersion,
        framework = RuntimeInformation.FrameworkDescription,
        os = RuntimeInformation.OSDescription,
        arch = RuntimeInformation.ProcessArchitecture.ToString().ToLowerInvariant(),
        dpiAwareness = "per-monitor-v2",
        coordinateSpace = "physical-pixels",
        verbs = AllowedFlags.Keys.OrderBy(k => k, StringComparer.Ordinal).ToArray(),
        readOnlyVerbs = new[] { "capabilities", "hit-test", "list-displays", "list-windows", "uia-find", "uia-tree", "verify-target", "wait-for" },
        gatedVerbs = new[] { "capture-window", "clipboard-read", "clipboard-write", "manage-window", "mouse-drag", "mouse-wheel", "send-keys" },
        unsupported = new[]
        {
            "ocr", "app-launch", "process-list", "registry", "filesystem",
            "shell", "batch-plan", "full-screen-capture"
        }
    };

    private static object DescribeManagePlan(string action, Dictionary<string, string?> o) => action switch
    {
        "move" => new { action, geometry = new { x = Int(o, "x", 0), y = Int(o, "y", 0) }, mechanism = "SetWindowPos" },
        "resize" => new { action, geometry = new { width = Int(o, "width", 0), height = Int(o, "height", 0) }, mechanism = "SetWindowPos" },
        "close" => new { action, mechanism = "WM_CLOSE (graceful, user may cancel)" },
        _ => new { action, mechanism = "ShowWindow" }
    };

    private static (bool Allowed, string Reason) GuardPoint(int x, int y, string expectedHwnd)
    {
        var pt = new Native.Point { X = x, Y = y };
        var leaf = Native.WindowFromPoint(pt);
        var root = leaf != IntPtr.Zero ? Native.GetAncestor(leaf, Native.GaRoot) : IntPtr.Zero;
        if (root == IntPtr.Zero) return (false, "point-hits-no-window");
        var hit = DescribeWindow(root);
        return string.Equals(hit.Hwnd, expectedHwnd, StringComparison.Ordinal)
            ? (true, "hit-window-matches-expected-target")
            : (false, $"hit-window-mismatch:{hit.Hwnd}!={expectedHwnd}");
    }

    // ---------------- shared window helpers ----------------

    private static List<WindowInfo> EnumerateWindows(bool includeHidden)
    {
        var handles = new List<IntPtr>();
        Native.EnumWindows((hwnd, _) => { handles.Add(hwnd); return true; }, IntPtr.Zero);
        return handles
            .Where(hwnd => includeHidden || Native.IsWindowVisible(hwnd))
            .Select(DescribeWindow)
            .Where(w => !string.IsNullOrWhiteSpace(w.Title))
            .OrderByDescending(w => w.Foreground)
            .ToList();
    }

    private static WindowInfo DescribeWindow(IntPtr hwnd)
    {
        var len = Native.GetWindowTextLength(hwnd);
        var sb = new StringBuilder(len + 1);
        Native.GetWindowText(hwnd, sb, sb.Capacity);
        Native.GetWindowThreadProcessId(hwnd, out var pid);
        string processName = "";
        try { processName = Process.GetProcessById((int)pid).ProcessName; } catch { }
        var bounds = Native.GetBounds(hwnd);
        return new WindowInfo
        {
            Hwnd = hwnd.ToInt64().ToString(),
            Title = sb.ToString(),
            Pid = (int)pid,
            ProcessName = processName,
            Visible = Native.IsWindowVisible(hwnd),
            Minimized = Native.IsIconic(hwnd),
            Foreground = Native.GetForegroundWindow() == hwnd,
            Cloaked = Native.IsCloaked(hwnd),
            Bounds = new RectInfo { Left = bounds.Left, Top = bounds.Top, Right = bounds.Right, Bottom = bounds.Bottom, Width = Math.Max(0, bounds.Right - bounds.Left), Height = Math.Max(0, bounds.Bottom - bounds.Top) }
        };
    }

    private static bool IsCaptureCandidate(WindowInfo w) => IsRestorableCandidate(w) && !w.Minimized;
    private static bool IsRestorableCandidate(WindowInfo w) =>
        w.Visible && !w.Cloaked &&
        !(w.Title == "Program Manager" && w.ProcessName.Equals("explorer.exe", StringComparison.OrdinalIgnoreCase)) &&
        (w.Minimized || (w.Bounds.Width >= 64 && w.Bounds.Height >= 48));
    private static bool IsHiddenCandidate(WindowInfo w) =>
        !w.Visible && !w.Cloaked && !string.IsNullOrWhiteSpace(w.Title);

    private static void WaitForWindow(IntPtr hwnd, int timeoutMs)
    {
        var sw = Stopwatch.StartNew();
        while (sw.ElapsedMilliseconds < timeoutMs)
        {
            if (Native.IsWindowVisible(hwnd) && !Native.IsIconic(hwnd)) { Thread.Sleep(180); return; }
            Thread.Sleep(50);
        }
    }

    // Renders the window into a bitmap via PrintWindow(PW_RENDERFULLCONTENT) and, only
    // when that succeeds, persists it as PNG. A GPU-rendered or occluded window still
    // renders because PrintWindow asks the window itself to paint into our DC.
    private static bool CaptureBitmap(IntPtr hwnd, RectInfo bounds, string output)
    {
        using var bitmap = new Bitmap(bounds.Width, bounds.Height, PixelFormat.Format32bppArgb);
        using (var g = Graphics.FromImage(bitmap))
        {
            var hdc = g.GetHdc();
            bool rendered;
            try
            {
                rendered = Native.PrintWindow(hwnd, hdc, Native.PwRenderFullContent);
            }
            finally
            {
                g.ReleaseHdc(hdc);
            }
            if (!rendered) return false;
        }
        bitmap.Save(output, ImageFormat.Png);
        return true;
    }

    // GDI+ locks the source file for the lifetime of a Bitmap/Image created from a
    // path (Bitmap(path) / Image.FromFile). Anything that reads the capture and then
    // writes back to the same path MUST load detached, otherwise Save throws
    // "A generic error occurred in GDI+". Reads bytes into memory, decodes, and
    // copies into a fresh 32bpp bitmap fully independent of the file.
    private static Bitmap LoadDetached(string path)
    {
        var bytes = File.ReadAllBytes(path);
        using var ms = new MemoryStream(bytes);
        using var decoded = Image.FromStream(ms);
        var copy = new Bitmap(decoded.Width, decoded.Height, PixelFormat.Format32bppArgb);
        using (var g = Graphics.FromImage(copy))
            g.DrawImage(decoded, 0, 0, decoded.Width, decoded.Height);
        return copy;
    }

    private static EvidenceResult MeasureEvidence(string path)
    {
        using var bmp = LoadDetached(path);
        const int grid = 12;
        var black = 0;
        var total = 0;
        var stepX = Math.Max(1, bmp.Width / grid);
        var stepY = Math.Max(1, bmp.Height / grid);
        for (var y = 0; y < bmp.Height; y += stepY)
            for (var x = 0; x < bmp.Width; x += stepX)
            {
                var c = bmp.GetPixel(x, y);
                total++;
                if (c.R < 8 && c.G < 8 && c.B < 8) black++;
            }
        var ratio = total == 0 ? 1.0 : (double)black / total;
        return new EvidenceResult(ratio > 0.9, ratio);
    }

    private static (bool, bool) ProcessOverlay(string path)
    {
        using var bmp = LoadDetached(path);
        var transparent = false;
        using (var clone = (Bitmap)bmp.Clone())
        {
            for (var x = 0; x < clone.Width; x++)
                for (var y = 0; y < clone.Height; y++)
                {
                    var c = clone.GetPixel(x, y);
                    if (c.A == 0) { transparent = true; break; }
                }
            if (transparent)
            {
                for (var x = 0; x < clone.Width; x++)
                    for (var y = 0; y < clone.Height; y++)
                    {
                        var c = clone.GetPixel(x, y);
                        if (c.R < 8 && c.G < 8 && c.B < 8 && c.A == 255) clone.SetPixel(x, y, Color.FromArgb(0, 0, 0, 0));
                    }
                clone.Save(path, ImageFormat.Png);
            }
        }
        return (transparent, transparent);
    }

    private static bool ResizeIfNeeded(string path, int maxDimension)
    {
        using var img = LoadDetached(path);
        if (img.Width <= maxDimension && img.Height <= maxDimension) return false;
        var scale = Math.Min((double)maxDimension / img.Width, (double)maxDimension / img.Height);
        var w = Math.Max(1, (int)Math.Round(img.Width * scale));
        var h = Math.Max(1, (int)Math.Round(img.Height * scale));
        using var resized = new Bitmap(w, h, PixelFormat.Format32bppArgb);
        using (var g = Graphics.FromImage(resized))
            g.DrawImage(img, 0, 0, w, h);
        resized.Save(path, ImageFormat.Png);
        return true;
    }

    private static void CaptureVisiblePixels(RectInfo bounds, string output)
    {
        using var bmp = new Bitmap(bounds.Width, bounds.Height);
        using var g = Graphics.FromImage(bmp);
        g.CopyFromScreen(bounds.Left, bounds.Top, 0, 0, bmp.Size);
        bmp.Save(output, ImageFormat.Png);
    }

    // ---------------- UIA traversal (READ-ONLY) ----------------

    private static AutomationElement? TryGetRoot(long hwnd)
    {
        try { return AutomationElement.FromHandle(new IntPtr(hwnd)); } catch { return null; }
    }

    private static void Walk(AutomationElement parent, int depth, ref int count, int max, bool includeOffscreen, List<Dictionary<string, object>> outList)
    {
        if (count >= max) return;
        AutomationElementCollection children;
        try { children = parent.FindAll(TreeScope.Children, Condition.TrueCondition); } catch { return; }
        foreach (AutomationElement child in children)
        {
            if (count >= max) return;
            Visit(child, depth, ref count, includeOffscreen, outList);
            Walk(child, depth + 1, ref count, max, includeOffscreen, outList);
        }
    }

    private static void Visit(AutomationElement el, int depth, ref int count, bool includeOffscreen, List<Dictionary<string, object>> outList)
    {
        try
        {
            var offscreen = el.Current.IsOffscreen;
            if (offscreen && !includeOffscreen) return;
            var info = DescribeElement(el, depth, $"el-{count}");
            outList.Add(info);
            count++;
        }
        catch { /* a single unreadable node never aborts the traversal */ }
    }

    private static void FindWalk(AutomationElement parent, int depth, ref int count, int max, string query, string? roleFilter, List<Dictionary<string, object>> outList)
    {
        if (count >= max) return;
        AutomationElementCollection children;
        try { children = parent.FindAll(TreeScope.Children, Condition.TrueCondition); } catch { return; }
        foreach (AutomationElement child in children)
        {
            if (count >= max) return;
            try
            {
                var name = (child.Current.Name ?? "").ToLowerInvariant();
                var aid = (child.Current.AutomationId ?? "").ToLowerInvariant();
                var cls = SafeClassName(child);
                var role = child.Current.LocalizedControlType ?? "";
                var matchesQuery = name.Contains(query) || aid.Contains(query) || cls.ToLowerInvariant().Contains(query);
                var matchesRole = string.IsNullOrEmpty(roleFilter) || role.Contains(roleFilter, StringComparison.OrdinalIgnoreCase);
                if (matchesQuery && matchesRole)
                {
                    outList.Add(DescribeElement(child, depth, $"el-{count}"));
                    count++;
                }
            }
            catch { }
            FindWalk(child, depth + 1, ref count, max, query, roleFilter, outList);
        }
    }

    private static Dictionary<string, object> DescribeElement(AutomationElement el, int depth, string elementId)
    {
        var dict = new Dictionary<string, object>
        {
            ["id"] = elementId,
            ["depth"] = depth,
            ["role"] = el.Current.LocalizedControlType ?? el.Current.ControlType?.ProgrammaticName ?? "",
            ["name"] = el.Current.Name ?? "",
            ["className"] = SafeClassName(el),
            ["automationId"] = el.Current.AutomationId ?? "",
            ["isEnabled"] = el.Current.IsEnabled,
            ["isOffscreen"] = el.Current.IsOffscreen,
        };
        var patterns = new List<string>();
        foreach (var p in el.GetSupportedPatterns())
        {
            try { patterns.Add(p.ProgrammaticName.Replace("PatternIdentifiers", "").Replace(".", "").Replace("Pattern", "")); } catch { }
        }
        dict["patterns"] = patterns;
        try
        {
            var r = el.Current.BoundingRectangle;
            if (!r.IsEmpty) dict["bounds"] = new { left = (int)r.Left, top = (int)r.Top, right = (int)r.Right, bottom = (int)r.Bottom };
        }
        catch { }
        return dict;
    }

    private static string SafeClassName(AutomationElement el)
    {
        try { return el.Current.ClassName ?? ""; } catch { return ""; }
    }

    private record EvidenceResult(bool SuspectedBlank, double BlackRatio);
}

internal sealed class WindowInfo
{
    public string Hwnd { get; set; } = "";
    public string Title { get; set; } = "";
    public int Pid { get; set; }
    public string ProcessName { get; set; } = "";
    public bool Visible { get; set; }
    public bool Minimized { get; set; }
    public bool Foreground { get; set; }
    public bool Cloaked { get; set; }
    public RectInfo Bounds { get; set; } = new();
}

internal sealed class RectInfo
{
    public int Left { get; set; }
    public int Top { get; set; }
    public int Right { get; set; }
    public int Bottom { get; set; }
    public int Width { get; set; }
    public int Height { get; set; }
}

internal static class Native
{
    public const int SwHide = 0;
    public const int SwShowNoActivate = 4;
    public const int SwMinimize = 6;
    public const int SwMaximize = 3;
    public const int SwRestore = 9;
    public const uint PwRenderFullContent = 2;
    public const int GaRoot = 2;
    public const uint WmClose = 0x0010;
    public const uint SwpNoSize = 0x0001;
    public const uint SwpNoMove = 0x0002;
    public const uint SwpNoActivate = 0x0010;
    public const uint SwpNoZorder = 0x0004;
    public const uint MouseEventLeftDown = 0x0002;
    public const uint MouseEventLeftUp = 0x0004;
    public const uint MouseEventRightDown = 0x0008;
    public const uint MouseEventRightUp = 0x0010;
    public const uint MouseEventMiddleDown = 0x0020;
    public const uint MouseEventMiddleUp = 0x0040;
    public const uint MouseEventWheel = 0x0800;
    public const uint MouseEventHwHEEL = 0x1000;

    public const int SmXVirtualScreen = 76;
    public const int SmYVirtualScreen = 77;
    public const int SmCxVirtualScreen = 78;
    public const int SmCyVirtualScreen = 79;
    public const int MdtEffectiveDpi = 0;
    public const int MonitorinfofPrimary = 0x00000001;
    public const uint DesktopReadObjects = 0x0001;
    public const int UoiName = 2;
    public const uint InputKeyboard = 1;
    public const uint KeyeventfKeyup = 0x0002;

    private const int DwmaExtendedFrameBounds = 9;
    private const int DwmaCloaked = 14;

    [StructLayout(LayoutKind.Sequential)] public struct Rect { public int Left, Top, Right, Bottom; }
    [StructLayout(LayoutKind.Sequential)] public struct Point { public int X, Y; }
    [StructLayout(LayoutKind.Sequential)] public struct MonitorInfo
    {
        public int Size;
        public Rect Monitor;
        public Rect Work;
        public int Flags;
    }

    // INPUT must marshal to exactly 40 bytes on x64, which requires the union to
    // be sized by MOUSEINPUT (32 bytes). Declaring only KEYBDINPUT yields a
    // short struct and SendInput then fails with ERROR_INVALID_PARAMETER.
    [StructLayout(LayoutKind.Sequential)] public struct MouseInput
    {
        public int Dx;
        public int Dy;
        public uint MouseData;
        public uint Flags;
        public uint Time;
        public IntPtr ExtraInfo;
    }

    [StructLayout(LayoutKind.Sequential)] public struct KeybdInput
    {
        public ushort VirtualKey;
        public ushort ScanCode;
        public uint Flags;
        public uint Time;
        public IntPtr ExtraInfo;
    }

    [StructLayout(LayoutKind.Sequential)] public struct HardwareInput
    {
        public uint Msg;
        public ushort ParamL;
        public ushort ParamH;
    }

    [StructLayout(LayoutKind.Explicit)] public struct InputUnion
    {
        [FieldOffset(0)] public MouseInput Mouse;
        [FieldOffset(0)] public KeybdInput Keyboard;
        [FieldOffset(0)] public HardwareInput Hardware;
    }

    [StructLayout(LayoutKind.Sequential)] public struct Input
    {
        public uint Type;
        public InputUnion Data;
    }

    public static Input KeyInput(ushort virtualKey, bool up) => new()
    {
        Type = InputKeyboard,
        Data = new InputUnion
        {
            Keyboard = new KeybdInput
            {
                VirtualKey = virtualKey,
                Flags = up ? KeyeventfKeyup : 0
            }
        }
    };
    [StructLayout(LayoutKind.Sequential)] public struct WindowPlacement
    {
        public int Length;
        public int Flags;
        public int ShowCmd;
        public Point MinPosition;
        public Point MaxPosition;
        public Rect NormalPosition;
    }

    [DllImport("user32.dll")] public static extern bool SetProcessDpiAwarenessContext(IntPtr value);
    public delegate bool EnumWindowsProc(IntPtr hwnd, IntPtr lParam);
    [DllImport("user32.dll")] public static extern bool EnumWindows(EnumWindowsProc cb, IntPtr lParam);
    [DllImport("user32.dll")] public static extern bool IsWindow(IntPtr hwnd);
    [DllImport("user32.dll")] public static extern bool IsWindowVisible(IntPtr hwnd);
    [DllImport("user32.dll")] public static extern bool IsIconic(IntPtr hwnd);
    [DllImport("user32.dll")] public static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] public static extern int GetWindowText(IntPtr hwnd, StringBuilder text, int count);
    [DllImport("user32.dll")] public static extern int GetWindowTextLength(IntPtr hwnd);
    [DllImport("user32.dll")] public static extern uint GetWindowThreadProcessId(IntPtr hwnd, out uint pid);
    [DllImport("user32.dll")] public static extern bool GetWindowRect(IntPtr hwnd, out Rect rect);
    [DllImport("user32.dll")] public static extern bool PrintWindow(IntPtr hwnd, IntPtr hdc, uint flags);
    [DllImport("user32.dll")] public static extern bool ShowWindowAsync(IntPtr hwnd, int cmd);
    [DllImport("user32.dll")] public static extern bool ShowWindow(IntPtr hwnd, int cmd);
    [DllImport("user32.dll")] public static extern bool GetWindowPlacement(IntPtr hwnd, ref WindowPlacement placement);
    [DllImport("user32.dll")] public static extern bool SetWindowPlacement(IntPtr hwnd, ref WindowPlacement placement);
    [DllImport("user32.dll")] public static extern bool SetWindowPos(IntPtr hwnd, IntPtr after, int x, int y, int cx, int cy, uint flags);
    [DllImport("user32.dll")] public static extern bool PostMessage(IntPtr hwnd, uint msg, IntPtr wParam, IntPtr lParam);
    [DllImport("user32.dll")] public static extern IntPtr WindowFromPoint(Point pt);
    [DllImport("user32.dll")] public static extern IntPtr GetAncestor(IntPtr hwnd, uint flags);
    [DllImport("user32.dll")] public static extern bool SetCursorPos(int x, int y);
    [DllImport("user32.dll")] public static extern void mouse_event(uint dwFlags, uint dx, uint dy, uint dwData, IntPtr dwExtraInfo);
    public delegate bool MonitorEnumProc(IntPtr hMonitor, IntPtr hdc, ref Rect rect, IntPtr data);
    [DllImport("user32.dll")] public static extern bool EnumDisplayMonitors(IntPtr hdc, IntPtr clip, MonitorEnumProc callback, IntPtr data);
    [DllImport("user32.dll")] public static extern bool GetMonitorInfo(IntPtr hMonitor, ref MonitorInfo info);
    [DllImport("user32.dll")] public static extern int GetSystemMetrics(int index);
    [DllImport("user32.dll")] public static extern uint GetClipboardSequenceNumber();
    [DllImport("shcore.dll")] public static extern int GetDpiForMonitor(IntPtr hMonitor, int dpiType, out uint dpiX, out uint dpiY);
    [DllImport("user32.dll", SetLastError = true)] public static extern IntPtr OpenInputDesktop(uint flags, bool inherit, uint access);
    [DllImport("user32.dll", SetLastError = true)] public static extern bool CloseDesktop(IntPtr desktop);
    [DllImport("user32.dll", SetLastError = true)] public static extern IntPtr GetThreadDesktop(uint threadId);
    [DllImport("kernel32.dll")] public static extern uint GetCurrentThreadId();
    [DllImport("user32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern bool GetUserObjectInformation(IntPtr hObj, int nIndex, StringBuilder? pvInfo, int nLength, out int lpnLengthNeeded);

    // The desktop's own name ("Default", "Winlogon", ...). Returns null when the
    // handle cannot be queried, which callers must treat as "cannot verify".
    public static string? GetUserObjectName(IntPtr handle)
    {
        if (handle == IntPtr.Zero) return null;
        if (GetUserObjectInformation(handle, UoiName, null, 0, out var needed) || needed <= 0) return null;
        var buffer = new StringBuilder(needed / 2 + 1);
        return GetUserObjectInformation(handle, UoiName, buffer, buffer.Capacity * 2, out _) ? buffer.ToString() : null;
    }
    [DllImport("user32.dll", SetLastError = true)] public static extern uint SendInput(uint count, Input[] inputs, int size);
    [DllImport("dwmapi.dll")] private static extern int DwmGetWindowAttribute(IntPtr hwnd, int attr, out Rect value, int size);
    [DllImport("dwmapi.dll")] private static extern int DwmGetWindowAttribute(IntPtr hwnd, int attr, out int value, int size);

    public static WindowPlacement GetPlacement(IntPtr hwnd)
    {
        var placement = new WindowPlacement { Length = Marshal.SizeOf<WindowPlacement>() };
        if (!GetWindowPlacement(hwnd, ref placement)) throw new InvalidOperationException("Unable to read window placement.");
        return placement;
    }

    public static Rect GetBounds(IntPtr hwnd)
    {
        if (DwmGetWindowAttribute(hwnd, DwmaExtendedFrameBounds, out Rect rect, Marshal.SizeOf<Rect>()) != 0 && !GetWindowRect(hwnd, out rect))
            throw new InvalidOperationException("Unable to read window bounds.");
        return rect;
    }

    public static bool IsCloaked(IntPtr hwnd) => DwmGetWindowAttribute(hwnd, DwmaCloaked, out int value, sizeof(int)) == 0 && value != 0;
}
