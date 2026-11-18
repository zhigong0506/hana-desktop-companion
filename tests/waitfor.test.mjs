// Pure-logic tests for the bounded wait request. A wait is an observation, so
// the only things worth pinning are: it cannot be unbounded, it cannot be
// ambiguous about its target, and it refuses conditions it does not implement.

import test from "node:test";
import assert from "node:assert/strict";

import {
  WAIT_CONDITIONS,
  WAIT_INTERVAL_MAX,
  WAIT_INTERVAL_MIN,
  WAIT_TIMEOUT_DEFAULT,
  WAIT_TIMEOUT_MAX,
  WAIT_TIMEOUT_MIN,
  normalizeWaitFor,
} from "../app/hana-desktop-companion/lib/waitfor.js";

test("a missing or unknown condition is refused", () => {
  assert.equal(normalizeWaitFor({}).reason, "missing-condition");
  assert.equal(normalizeWaitFor({ condition: "eventually" }).reason, "unsupported-condition:eventually");
});

test("the timeout is clamped to a bounded window and can never be unbounded", () => {
  const big = normalizeWaitFor({ condition: "window-visible", hwnd: "1", timeoutMs: 10 * 60 * 1000 });
  assert.equal(big.timeoutMs, WAIT_TIMEOUT_MAX);

  const tiny = normalizeWaitFor({ condition: "window-visible", hwnd: "1", timeoutMs: 1 });
  assert.equal(tiny.timeoutMs, WAIT_TIMEOUT_MIN);

  const missing = normalizeWaitFor({ condition: "window-visible", hwnd: "1" });
  assert.equal(missing.timeoutMs, WAIT_TIMEOUT_DEFAULT);

  const negative = normalizeWaitFor({ condition: "window-visible", hwnd: "1", timeoutMs: -5000 });
  assert.equal(negative.timeoutMs, WAIT_TIMEOUT_MIN);

  const notANumber = normalizeWaitFor({ condition: "window-visible", hwnd: "1", timeoutMs: "soon" });
  assert.equal(notANumber.timeoutMs, WAIT_TIMEOUT_DEFAULT);
});

test("the poll interval is clamped too", () => {
  assert.equal(normalizeWaitFor({ condition: "window-visible", hwnd: "1", intervalMs: 1 }).intervalMs, WAIT_INTERVAL_MIN);
  assert.equal(normalizeWaitFor({ condition: "window-visible", hwnd: "1", intervalMs: 99999 }).intervalMs, WAIT_INTERVAL_MAX);
});

test("exactly one target selector is required", () => {
  assert.equal(normalizeWaitFor({ condition: "window-visible" }).reason, "need-exactly-one-target-selector");
  assert.equal(
    normalizeWaitFor({ condition: "window-visible", hwnd: "1", title: "Notepad" }).reason,
    "need-exactly-one-target-selector",
  );
  assert.equal(normalizeWaitFor({ condition: "window-visible", hwnd: "1" }).ok, true);
  assert.equal(normalizeWaitFor({ condition: "window-visible", foreground: true }).ok, true);
});

test("window-gone tolerates a dead handle but still needs one", () => {
  assert.equal(normalizeWaitFor({ condition: "window-gone" }).reason, "window-gone-requires-hwnd");
  assert.equal(normalizeWaitFor({ condition: "window-gone", hwnd: "123456" }).ok, true);
});

test("query-based conditions require a query", () => {
  for (const condition of ["title-contains", "element-exists", "element-gone"]) {
    assert.equal(normalizeWaitFor({ condition, hwnd: "1" }).reason, `${condition}-requires-query`);
    assert.equal(normalizeWaitFor({ condition, hwnd: "1", query: "Save" }).ok, true);
  }
});

test("point-hits-target requires a coordinate pair", () => {
  assert.equal(normalizeWaitFor({ condition: "point-hits-target", hwnd: "1" }).reason, "point-hits-target-requires-x-and-y");
  assert.equal(normalizeWaitFor({ condition: "point-hits-target", hwnd: "1", x: 10 }).reason, "point-hits-target-requires-x-and-y");
  assert.equal(normalizeWaitFor({ condition: "point-hits-target", hwnd: "1", x: 10, y: 20 }).ok, true);
});

test("the published condition list is the whole surface", () => {
  assert.deepEqual(
    [...WAIT_CONDITIONS].sort(),
    [
      "element-exists",
      "element-gone",
      "point-hits-target",
      "title-contains",
      "window-foreground",
      "window-gone",
      "window-minimized",
      "window-visible",
    ],
  );
});
