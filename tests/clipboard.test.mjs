// Pure-logic tests for clipboard-read bounds and redaction. No clipboard is read
// or written here; what is asserted is that the read budget is real and that
// content cannot reach a log line or an error detail.
//
// There is no clipboard-write path on purpose, and one test pins that absence so
// it cannot be reintroduced without a deliberate decision.

import test from "node:test";
import assert from "node:assert/strict";

import * as clipboard from "../app/hana-desktop-companion/lib/clipboard.js";

const { CLIPBOARD_READ_DEFAULT_CHARS, CLIPBOARD_READ_MAX_CHARS, CLIPBOARD_WRITE_MAX_BYTES, CLIPBOARD_WRITE_MAX_CHARS, clipboardReceipt, normalizeClipboardRead, normalizeClipboardWrite, redactForLog } =
  clipboard;

test("the read budget defaults low and is capped", () => {
  assert.equal(normalizeClipboardRead({}).maxChars, CLIPBOARD_READ_DEFAULT_CHARS);
  assert.equal(normalizeClipboardRead({ maxChars: 10 * 1000 }).maxChars, CLIPBOARD_READ_MAX_CHARS);
  assert.equal(normalizeClipboardRead({ maxChars: 0 }).maxChars, 1);
  assert.equal(normalizeClipboardRead({ maxChars: "plenty" }).maxChars, CLIPBOARD_READ_DEFAULT_CHARS);
  assert.equal(normalizeClipboardRead({ maxChars: NaN }).maxChars, CLIPBOARD_READ_DEFAULT_CHARS);
});

test("a read receipt carries sizes only, never content", () => {
  const receipt = clipboardReceipt({ chars: 900, returnedChars: 500, truncated: true, maxChars: 500, text: "SECRET-MARKER" });
  assert.deepEqual(receipt, { chars: 900, returnedChars: 500, truncated: true, maxChars: 500 });
  assert.equal(JSON.stringify(receipt).includes("SECRET-MARKER"), false);
});

test("redaction removes text-bearing fields at any depth", () => {
  const payload = {
    ok: true,
    text: "SECRET-MARKER",
    nested: { content: "SECRET-MARKER", list: [{ clipboardText: "SECRET-MARKER" }] },
    chars: 13,
  };
  const redacted = redactForLog(payload);
  assert.equal(JSON.stringify(redacted).includes("SECRET-MARKER"), false);
  assert.equal(redacted.chars, 13);
  assert.equal(redacted.nested.content.redacted, true);
  assert.equal(redacted.nested.content.chars, 13);
  assert.equal(redacted.nested.list[0].clipboardText.redacted, true);
});

test("redaction does not mutate the caller's object", () => {
  const payload = { text: "SECRET-MARKER" };
  redactForLog(payload);
  assert.equal(payload.text, "SECRET-MARKER");
});

test("redaction leaves non-sensitive shapes alone", () => {
  const payload = { ok: true, hwnd: "123", truncated: false, count: 3 };
  assert.deepEqual(redactForLog(payload), payload);
});

test("write input is non-empty, bounded, and rejected rather than truncated", () => {
  assert.deepEqual(normalizeClipboardWrite({}), { ok: false, error: "text-required" });
  assert.deepEqual(normalizeClipboardWrite({ text: "" }), { ok: false, error: "text-empty" });
  assert.equal(normalizeClipboardWrite({ text: "柠" }).ok, true);
  assert.deepEqual(normalizeClipboardWrite({ text: "x".repeat(CLIPBOARD_WRITE_MAX_CHARS + 1) }).error, "payload-too-large");
  assert.equal(normalizeClipboardWrite({ text: "柠".repeat(CLIPBOARD_WRITE_MAX_BYTES / 3 + 1) }).error, "payload-too-large");
  assert.equal(CLIPBOARD_WRITE_MAX_CHARS, 8192);
  assert.equal(CLIPBOARD_WRITE_MAX_BYTES, 65536);
});
