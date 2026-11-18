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
import { execFile, spawn } from "node:child_process";
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
  "list-displays": [],
  "wait-for": ["condition", "hwnd", "title", "foreground", "expectedPid", "query", "role", "x", "y", "timeoutMs", "intervalMs"],
  "send-keys": ["chord", "holdMs", "hwnd", "expectedPid", "execute"],
  "clipboard-read": ["hwnd", "expectedPid", "maxChars", "execute"],
  "clipboard-write": ["hwnd", "expectedPid", "maxChars", "execute"],
  "capabilities": [],
};

// Flags whose values must be a finite integer before they reach Win32.
const INT_FLAGS = new Set([
  "limit", "maxElements", "maxDimension", "expectedPid", "x", "y",
  "fromX", "fromY", "toX", "toY", "width", "height", "notches", "steps",
  "timeoutMs", "intervalMs", "holdMs", "maxChars",
]);

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

// Exposed for the logic tests: the argv contract is what keeps the helper's
// surface closed, so it is verified without spawning anything.
export function buildHelperArgv(verb, opts = {}) {
  return toArgv(verb, opts);
}

// The verb -> flag map itself, for the test that pins it against the native
// helper's own allowlist.
export function helperVerbSurface() {
  return Object.fromEntries(Object.entries(VERB_FLAGS).map(([verb, flags]) => [verb, [...flags]]));
}

// A wait verb may legitimately hold the helper for its own timeout, so the
// child-process kill budget is derived from that timeout instead of the default.
const VERB_TIMEOUT_MS = { "wait-for": 40000 };

function timeoutFor(verb, opts) {
  const base = VERB_TIMEOUT_MS[verb] ?? 20000;
  if (verb !== "wait-for") return base;
  const requested = Number(opts?.timeoutMs);
  return Number.isFinite(requested) ? Math.min(base, Math.round(requested) + 5000) : base;
}

export async function runHelperWithInput(verb, opts, input, { timeoutMs } = {}) {
  if (verb !== "clipboard-write") throw new Error("Standard input is only supported for clipboard-write.");
  if (typeof input !== "string" || input.length === 0) throw new TypeError("Clipboard input must be a non-empty string.");
  if (input.length > 8192 || Buffer.byteLength(input, "utf8") > 64 * 1024) throw new RangeError("Clipboard input exceeds the fixed size limit.");
  if (process.platform !== "win32") throw new Error("hana-desktop-companion supports Windows only.");
  if (process.arch !== "x64") throw new Error(`hana-desktop-companion requires Windows x64; current arch is ${process.arch}.`);

  const child = spawn(HELPER_PATH, toArgv(verb, opts), {
    windowsHide: true,
    shell: false,
    stdio: ["pipe", "pipe", "pipe"],
  });
  const stdout = [];
  const stderr = [];
  let stdoutBytes = 0;
  let stderrBytes = 0;
  const maxBuffer = 16 * 1024 * 1024;
  const timer = setTimeout(() => child.kill(), timeoutMs ?? 20000);

  try {
    const result = await new Promise((resolve, reject) => {
      child.stdout.on("data", (chunk) => {
        stdoutBytes += chunk.length;
        if (stdoutBytes > maxBuffer) {
          child.kill();
          reject(new Error("Native helper output exceeded the size limit."));
          return;
        }
        stdout.push(chunk);
      });
      child.stderr.on("data", (chunk) => {
        stderrBytes += chunk.length;
        if (stderrBytes <= 64 * 1024) stderr.push(chunk);
      });
      child.once("error", reject);
      child.once("close", (code) => resolve({ code, stdout: Buffer.concat(stdout).toString("utf8"), stderr: Buffer.concat(stderr).toString("utf8") }));
      child.stdin.once("error", () => {});
      child.stdin.end(Buffer.from(input, "utf8"));
    });
    const lines = result.stdout.split(/\\r?\\n/).filter(Boolean);
    const json = lines.at(-1);
    if (!json) throw new Error(result.stderr ? "Native helper returned an error without JSON." : "Native helper produced no JSON.");
    const parsed = JSON.parse(json);
    if (result.code !== 0 && parsed?.ok !== false) throw new Error("Native clipboard write failed.");
    return parsed;
  } catch (err) {
    if (err?.code === "ERR_ACCESS_DENIED" || /child process|allow-child-process|permission model/i.test(err?.message || "")) {
      throw new Error("Native helper execution is blocked: this App has not been granted the 'app/process.spawn' capability.");
    }
    if (err?.code === "ENOENT") throw new Error("Native helper is missing from the App package.");
    throw err;
  } finally {
    clearTimeout(timer);
  }
}

export async function runHelper(verb, opts, { timeoutMs } = {}) {
  if (process.platform !== "win32") throw new Error("hana-desktop-companion supports Windows only.");
  if (process.arch !== "x64") throw new Error(`hana-desktop-companion requires Windows x64; current arch is ${process.arch}.`);

  const argv = toArgv(verb, opts);
  try {
    const { stdout, stderr } = await execFileAsync(HELPER_PATH, argv, {
      windowsHide: true,
      encoding: "utf8",
      timeout: timeoutMs ?? timeoutFor(verb, opts),
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
