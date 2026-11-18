// lib/waitfor.js — argument validation for the bounded wait verb.
//
// A wait is an observation, never an action: it polls a condition, returns what
// it saw, and stops. It must not be usable as an unbounded block, so the timeout
// is clamped here as well as in the helper (the helper is authoritative; this
// copy gives an immediate, testable refusal before a process is spawned).

export const WAIT_CONDITIONS = [
  "window-gone",
  "window-visible",
  "window-minimized",
  "window-foreground",
  "title-contains",
  "element-exists",
  "element-gone",
  "point-hits-target",
];

export const WAIT_TIMEOUT_MIN = 200;
export const WAIT_TIMEOUT_MAX = 30000;
export const WAIT_TIMEOUT_DEFAULT = 5000;
export const WAIT_INTERVAL_MIN = 100;
export const WAIT_INTERVAL_MAX = 2000;
export const WAIT_INTERVAL_DEFAULT = 250;

export const WAIT_FOR_FIELDS = [
  "condition",
  "hwnd",
  "title",
  "foreground",
  "expectedPid",
  "query",
  "role",
  "x",
  "y",
  "timeoutMs",
  "intervalMs",
];

function clamp(value, min, max, fallback) {
  const n = Number(value);
  if (!Number.isFinite(n)) return fallback;
  return Math.min(max, Math.max(min, Math.round(n)));
}

/**
 * Validate and normalize a wait request. Returns
 * { ok, reason?, condition, timeoutMs, intervalMs }.
 */
export function normalizeWaitFor(args = {}) {
  const condition = String(args.condition ?? "").trim().toLowerCase();
  if (!condition) return { ok: false, reason: "missing-condition" };
  if (!WAIT_CONDITIONS.includes(condition)) return { ok: false, reason: `unsupported-condition:${condition}` };

  const timeoutMs = clamp(args.timeoutMs, WAIT_TIMEOUT_MIN, WAIT_TIMEOUT_MAX, WAIT_TIMEOUT_DEFAULT);
  const intervalMs = clamp(args.intervalMs, WAIT_INTERVAL_MIN, WAIT_INTERVAL_MAX, WAIT_INTERVAL_DEFAULT);

  if (condition === "window-gone") {
    if (!args.hwnd) return { ok: false, reason: "window-gone-requires-hwnd" };
    return { ok: true, condition, timeoutMs, intervalMs };
  }

  const selectors = [Boolean(args.hwnd), Boolean(args.title), args.foreground === true].filter(Boolean).length;
  if (selectors !== 1) return { ok: false, reason: "need-exactly-one-target-selector" };

  if (["title-contains", "element-exists", "element-gone"].includes(condition) && !String(args.query ?? "").trim()) {
    return { ok: false, reason: `${condition}-requires-query` };
  }

  if (condition === "point-hits-target") {
    const x = Number(args.x);
    const y = Number(args.y);
    if (!Number.isFinite(x) || !Number.isFinite(y)) return { ok: false, reason: "point-hits-target-requires-x-and-y" };
  }

  return { ok: true, condition, timeoutMs, intervalMs };
}
