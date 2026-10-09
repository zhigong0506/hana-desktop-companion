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
//   - Every input/window verb binds to a decimal HWND and, when supplied, an
//     expected PID; a mismatch aborts before any injection.
//   - Blind coordinate actions re-run WindowFromPoint (root owner) immediately
//     before injection and refuse if the pixel no longer resolves to the
//     intended target window.
//   - Process is set per-monitor DPI aware (V2) at startup so every coordinate
//     read and written is in PHYSICAL pixels, matching UIA and SetCursorPos.
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
            object result = verb switch
            {
                "list-windows" => ListWindows(ParseFlags(args)),
                "capture-window" => CaptureWindow(ParseFlags(args)),
                "uia-tree" => UiaTree(ParseFlags(args)),
                "uia-find" => UiaFind(ParseFlags(args)),
                "hit-test" => HitTest(ParseFlags(args)),
                "verify-target" => VerifyTarget(ParseFlags(args)),
                "manage-window" => ManageWindow(ParseFlags(args)),
                "mouse-drag" => MouseDrag(ParseFlags(args)),
                "mouse-wheel" => MouseWheel(ParseFlags(args)),
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

    private static Dictionary<string, string?> ParseFlags(string[] args)
    {
        var result = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
        for (var i = 1; i < args.Length; i++)
        {
            var key = args[i].TrimStart('-');
            if (i + 1 < args.Length && !args[i + 1].StartsWith('-')) result[key] = args[++i];
            else result[key] = "true";
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

    private const int DwmaExtendedFrameBounds = 9;
    private const int DwmaCloaked = 14;

    [StructLayout(LayoutKind.Sequential)] public struct Rect { public int Left, Top, Right, Bottom; }
    [StructLayout(LayoutKind.Sequential)] public struct Point { public int X, Y; }
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
