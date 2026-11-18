// lib/clipboard.js — clipboard-read bounds and the redaction rule.
//
// The clipboard is user data. Two things are enforced here so they are testable
// without reading anyone's clipboard:
//   1. Reads are truncated to a small, explicit character budget.
//   2. Any value that might reach a log line or an error detail goes through
//      `redactForLog`, which replaces text-bearing fields with a size.
//
// The reply text itself is the deliverable and does go to the caller (and thus
// into the host/model context). This module makes no claim about host-side
// session storage — only that this App never writes the content into a log line
// or an error message.
//
// There is no write path: a plaintext payload would have to cross the helper's
// command line, where any local process can read it.

export const CLIPBOARD_READ_DEFAULT_CHARS = 2000;
export const CLIPBOARD_READ_MAX_CHARS = 4000;

const SENSITIVE_KEYS = new Set(["text", "content", "clipboard", "clipboardtext", "clipboard_text"]);

export function normalizeClipboardRead(args = {}) {
  const raw = Number(args.maxChars);
  const maxChars = Number.isFinite(raw)
    ? Math.min(CLIPBOARD_READ_MAX_CHARS, Math.max(1, Math.round(raw)))
    : CLIPBOARD_READ_DEFAULT_CHARS;
  return { ok: true, maxChars };
}

/** Receipt for a clipboard read: sizes only, never the content. */
export function clipboardReceipt(result = {}) {
  return {
    chars: Number.isFinite(result.chars) ? result.chars : 0,
    returnedChars: Number.isFinite(result.returnedChars) ? result.returnedChars : 0,
    truncated: result.truncated === true,
    maxChars: Number.isFinite(result.maxChars) ? result.maxChars : CLIPBOARD_READ_DEFAULT_CHARS,
  };
}

/**
 * Deep-copy `value`, replacing any text-bearing field with its size. Used
 * before anything from the clipboard could be written to a log, an error
 * detail, or a receipt. Never mutates the input.
 */
export function redactForLog(value) {
  if (typeof value === "string") return value;
  if (Array.isArray(value)) return value.map((entry) => redactForLog(entry));
  if (value && typeof value === "object") {
    const out = {};
    for (const [key, entry] of Object.entries(value)) {
      if (SENSITIVE_KEYS.has(key.toLowerCase())) {
        out[key] = { redacted: true, chars: typeof entry === "string" ? entry.length : 0 };
        continue;
      }
      out[key] = redactForLog(entry);
    }
    return out;
  }
  return value;
}
