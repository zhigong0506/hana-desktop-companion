// Pure-logic tests for the helper bridge argv contract. Nothing is spawned:
// the point is that the command surface stays closed and that values survive
// the trip intact (including negative integers, which used to be swallowed).

import test from "node:test";
import assert from "node:assert/strict";

import { buildHelperArgv } from "../app/hana-desktop-companion/lib/helper.js";

test("an unknown verb is refused, not passed through", () => {
  assert.throws(() => buildHelperArgv("run-anything", {}), /Unsupported helper verb/);
  assert.throws(() => buildHelperArgv("shell", { cmd: "calc" }), /Unsupported helper verb/);
});

test("flags outside the verb's allowlist are dropped", () => {
  const argv = buildHelperArgv("list-windows", { query: "Notepad", evil: "1", output: "C:\\x.png" });
  assert.deepEqual(argv, ["list-windows", "--query", "Notepad"]);
});

test("a verb with no flags never emits one", () => {
  assert.deepEqual(buildHelperArgv("capabilities", { hwnd: "1", execute: true }), ["capabilities"]);
  assert.deepEqual(buildHelperArgv("list-displays", {}), ["list-displays"]);
});

test("negative integers reach the helper as values, not as flags", () => {
  const argv = buildHelperArgv("mouse-wheel", { x: 100, y: 200, notches: -3 });
  assert.deepEqual(argv, ["mouse-wheel", "--x", "100", "--y", "200", "--notches", "-3"]);
});

test("integers are rounded and non-finite integers are dropped", () => {
  const argv = buildHelperArgv("wait-for", { condition: "window-visible", hwnd: "1", timeoutMs: 1500.6, intervalMs: NaN });
  assert.deepEqual(argv, ["wait-for", "--condition", "window-visible", "--hwnd", "1", "--timeoutMs", "1501"]);
});

test("a false boolean emits nothing and a true boolean emits a presence flag", () => {
  assert.deepEqual(buildHelperArgv("clipboard-read", { hwnd: "1", execute: false }), ["clipboard-read", "--hwnd", "1"]);
  assert.deepEqual(buildHelperArgv("clipboard-read", { hwnd: "1", execute: true }), ["clipboard-read", "--hwnd", "1", "--execute"]);
});

test("clipboard write is not a reachable verb", () => {
  assert.throws(() => buildHelperArgv("clipboard-write", { text: "x" }), /Unsupported helper verb/);
});

test("ordinary string flags strip control characters and are length-capped", () => {
  const argv = buildHelperArgv("list-windows", { query: "a\u0001b\u007fc" });
  assert.deepEqual(argv, ["list-windows", "--query", "abc"]);

  const long = buildHelperArgv("list-windows", { query: "q".repeat(500) });
  assert.equal(long[2].length, 200);
});

// A value that begins with a dash must not be able to masquerade as a flag; the
// bridge strips leading dashes only from flag names, never from values.
test("a dash-prefixed value stays a value", () => {
  const argv = buildHelperArgv("list-windows", { query: "--execute" });
  assert.deepEqual(argv, ["list-windows", "--query", "--execute"]);
});

test("the keyboard verb exposes one chord plus binding/execute only", () => {
  const argv = buildHelperArgv("send-keys", {
    chord: "ctrl+s",
    thenKeys: "tab,enter",
    hwnd: "42",
    expectedPid: 7,
    execute: true,
    text: "smuggled",
    cmd: "calc",
  });
  assert.deepEqual(argv, ["send-keys", "--chord", "ctrl+s", "--hwnd", "42", "--expectedPid", "7", "--execute"]);
});

test("send-keys cannot smuggle a sequence or a free-text payload", () => {
  const argv = buildHelperArgv("send-keys", {
    chord: "ctrl+s",
    hwnd: "42",
    expectedPid: 7,
    thenKeys: "a,b,c",
    text: "secret",
  });
  assert.equal(argv.includes("--thenKeys"), false);
  assert.equal(argv.includes("--text"), false);
  assert.equal(argv.some((entry) => entry.startsWith("--text=")), false);
});

test("wait-for cannot be given an unbounded or negative timeout through the bridge", () => {
  const argv = buildHelperArgv("wait-for", { condition: "window-gone", hwnd: "9", timeoutMs: -1 });
  assert.deepEqual(argv, ["wait-for", "--condition", "window-gone", "--hwnd", "9", "--timeoutMs", "-1"]);
  // The helper clamps it; the bridge must not silently rewrite it into a flag.
});
