// lib/helper.js — thin, closed bridge to the bundled native helper.
//
// It never accepts an executable path, a shell string, or a free-form argument
// blob. Every call goes through a fixed verb name plus a whitelisted, ordered
// flag map, and returns parsed JSON from the helper's last stdout line.
//
// The helper lives in this App's read-only install directory (native/),
// resolved from the module URL — never from a model-supplied path.

import path from "node:path";
import { fileURLToPath } from "node:url";
import { execFile } from "node:child_process";
import { promisify } from "node:util";

const execFileAsync = promisify(execFile);
const moduleDir = path.dirname(fileURLToPath(import.meta.url));
export const HELPER_PATH = path.resolve(moduleDir, "..", "native", "hana-desktop-companion-helper.exe");

// Verb -> ordered allowlist of flag names. Anything not listed is dropped.
// This is the closed command surface; there is no wildcard and no shell.
const VERB_FLAGS = {
  "list-windows": ["query", "limit", "includeMinimized", "includeHidden"],
  "capture-window": ["hwnd", "title", "foreground", "expectedPid", "output", "maxDimension", "disableAutoCrop", "allowVisibleFallback", "wakeIfNeeded"],
  "uia-tree": ["hwnd", "title", "foreground", "expectedPid", "maxElements", "includeOffscreen"],
  "uia-find": ["hwnd", "title", "foreground", "expectedPid", "query", "role", "maxElements"],
  "hit-test": ["x", "y"],
  "verify-target": ["hwnd", "title", "foreground", "expectedPid", "x", "y"],
  "manage-window": ["action", "hwnd", "title", "foreground", "expectedPid", "x", "y", "width", "height", "execute"],
  "mouse-drag": ["fromX", "fromY", "toX", "toY", "button", "steps", "hwnd", "expectedPid", "execute"],
  "mouse-wheel": ["x", "y", "notches", "axis", "hwnd", "expectedPid", "execute"],
};

// Flags whose values must be a finite integer before they reach Win32.
const INT_FLAGS = new Set(["limit", "maxElements", "maxDimension", "expectedPid", "x", "y", "fromX", "fromY", "toX", "toY", "width", "height", "notches", "steps"]);

function toArgv(verb, opts) {
  const allowed = VERB_FLAGS[verb];
  if (!allowed) throw new Error(`Unsupported helper verb: ${verb}`);
  const argv = [verb];
  for (const flag of allowed) {
    if (!(flag in opts) || opts[flag] === undefined || opts[flag] === null) continue;
    const value = opts[flag];
    if (typeof value === "boolean") {
      if (value) argv.push(`--${flag}`); // presence flags (execute, wakeIfNeeded, include*)
      continue;
    }
    if (INT_FLAGS.has(flag)) {
      const n = Math.round(Number(value));
      if (!Number.isFinite(n)) continue;
      argv.push(`--${flag}`, String(n));
      continue;
    }
    // String flags: strip control chars and any argument-injection whitespace trick.
    const s = String(value).replace(/[\u0000-\u001f\u007f]/g, "").slice(0, 200);
    if (!s) continue;
    argv.push(`--${flag}`, s);
  }
  return argv;
}

export async function runHelper(verb, opts, { timeoutMs = 20000 } = {}) {
  if (process.platform !== "win32") throw new Error("hana-desktop-companion supports Windows only.");
  if (process.arch !== "x64") throw new Error(`hana-desktop-companion requires Windows x64; current arch is ${process.arch}.`);

  const argv = toArgv(verb, opts);
  try {
    const { stdout, stderr } = await execFileAsync(HELPER_PATH, argv, {
      windowsHide: true,
      encoding: "utf8",
      timeout: timeoutMs,
      maxBuffer: 16 * 1024 * 1024,
    });
    const lines = String(stdout || "").split(/\r?\n/).filter(Boolean);
    const json = lines.at(-1);
    if (!json) throw new Error(stderr ? `helper produced no JSON: ${String(stderr).slice(0, 300)}` : "helper produced no JSON.");
    return JSON.parse(json);
  } catch (err) {
    if (err && (err.code === "ERR_ACCESS_DENIED" || /child process|allow-child-process|permission model/i.test(err.message || ""))) {
      throw new Error("Native helper execution is blocked: this App has not been granted the 'app/process.spawn' capability. Enable it in Settings → Apps → App capabilities, then reload the App.");
    }
    if (err && err.code === "ENOENT") throw new Error(`Native helper is missing from the App package at ${HELPER_PATH}.`);
    const parsed = tryParseError(err?.stdout || err?.stderr || err?.message);
    if (parsed && parsed.ok === false) return parsed; // helper's structured failure
    throw err;
  }
}

function tryParseError(text) {
  if (!text) return null;
  const line = String(text).split(/\r?\n/).filter(Boolean).at(-1);
  try {
    const o = JSON.parse(line);
    return o && o.ok === false ? o : null;
  } catch {
    return null;
  }
}
