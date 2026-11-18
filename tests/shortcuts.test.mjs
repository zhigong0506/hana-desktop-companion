// Pure-logic tests for the keyboard screening list. The native helper re-checks
// everything before SendInput; these pin the JS mirror so the two cannot drift.

import test from "node:test";
import assert from "node:assert/strict";

import {
  ALLOWED_KEYS,
  DENIED_CHORDS,
  parseChord,
  screenShortcut,
} from "../app/hana-desktop-companion/lib/shortcuts.js";

test("an ordinary shortcut is admitted and canonicalised", () => {
  const result = screenShortcut({ chord: "ctrl+shift+s" });
  assert.equal(result.allowed, true);
  assert.deepEqual(result.steps, ["ctrl+shift+s"]);
});

test("modifier order does not change the canonical chord", () => {
  assert.deepEqual(screenShortcut({ chord: "shift+ctrl+s" }).steps, ["ctrl+shift+s"]);
  assert.deepEqual(screenShortcut({ chord: "shift+control+s" }).steps, ["ctrl+shift+s"]);
  assert.equal(screenShortcut({ chord: "S+CTRL" }).reason, "unsupported-modifier:s");
});

test("every Win-modifier chord is refused", () => {
  for (const chord of ["win+l", "win+r", "meta+e", "cmd+d", "win+space"]) {
    const result = screenShortcut({ chord });
    assert.equal(result.allowed, false, chord);
    assert.match(result.reason, /win-modifier-refused/, chord);
  }
});

test("system-level chords without Win are on the denylist", () => {
  for (const chord of ["alt+f4", "alt+tab", "ctrl+shift+esc", "ctrl+alt+delete", "ctrl+esc", "alt+space"]) {
    const result = screenShortcut({ chord });
    assert.equal(result.allowed, false, chord);
    assert.match(result.reason, /denied-chord/, chord);
  }
});

test("the deny list covers the documented high-impact set", () => {
  for (const chord of ["win+l", "win+r", "alt+f4", "ctrl+shift+escape", "ctrl+alt+delete"]) {
    assert.equal(DENIED_CHORDS.has(chord), true, chord);
  }
});

test("unknown keys and modifiers are refused rather than guessed", () => {
  assert.equal(screenShortcut({ chord: "ctrl+unknown" }).allowed, false);
  assert.equal(screenShortcut({ chord: "hyper+a" }).reason, "unsupported-modifier:hyper");
  assert.equal(screenShortcut({ chord: "ctrl+" }).reason, "unsupported-key:ctrl");
  assert.equal(screenShortcut({ chord: "" }).reason, "no-key-requested");
  assert.equal(screenShortcut({}).reason, "no-key-requested");
});

test("the key table is a closed allowlist", () => {
  assert.equal(ALLOWED_KEYS.has("f12"), true);
  assert.equal(ALLOWED_KEYS.has("f13"), false);
  assert.equal(ALLOWED_KEYS.has("volumeup"), false);
  assert.equal(ALLOWED_KEYS.has("printscreen"), false);
});

test("only one chord is accepted per reviewed action", () => {
  assert.deepEqual(screenShortcut({ chord: "ctrl+shift+s" }).steps, ["ctrl+shift+s"]);
  assert.equal(screenShortcut({ thenKeys: "tab,enter" }).allowed, false);
});

test("a denied chord is refused", () => {
  const result = screenShortcut({ chord: "alt+f4" });
  assert.equal(result.allowed, false);
  assert.deepEqual(result.steps, []);
});

test("parseChord reports the reason instead of throwing", () => {
  const parsed = parseChord("win+x");
  assert.equal(parsed.ok, false);
  assert.equal(parsed.reason, "win-modifier-refused");
});
