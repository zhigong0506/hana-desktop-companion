// lib/shortcuts.js — pure screening for the single-chord keyboard verb.
// The native helper repeats these checks before SendInput; this copy refuses
// denied chords before spawning a helper process.

export const ALLOWED_KEYS = new Set([
  ...Array.from({ length: 26 }, (_, i) => String.fromCharCode(97 + i)),
  ...Array.from({ length: 10 }, (_, i) => String(i)),
  ...Array.from({ length: 12 }, (_, i) => `f${i + 1}`),
  "enter", "return", "tab", "escape", "esc", "space", "backspace", "delete", "del",
  "insert", "ins", "home", "end", "pageup", "pagedown", "left", "up", "right", "down",
]);

export const DENIED_CHORDS = new Set([
  "alt+f4", "alt+tab", "alt+escape", "alt+space", "ctrl+escape", "ctrl+shift+escape",
  "ctrl+alt+delete", "ctrl+alt+tab", "ctrl+alt+escape", "win+l", "win+r", "win+x",
  "win+e", "win+d", "win+u", "win+p", "win+s", "win+v", "win+a", "win+i",
  "win+tab", "win+space", "win+ctrl+d", "win+ctrl+f4",
]);

const MODIFIER_ALIASES = new Map([
  ["ctrl", "ctrl"], ["control", "ctrl"], ["alt", "alt"], ["shift", "shift"],
]);
const WIN_MODIFIERS = new Set(["win", "meta", "cmd"]);
const KEY_ALIASES = new Map([["return", "enter"], ["esc", "escape"], ["del", "delete"], ["ins", "insert"]]);
const MODIFIER_ORDER = ["ctrl", "alt", "shift"];

export function canonicalKey(key) {
  const lower = String(key).toLowerCase();
  return KEY_ALIASES.get(lower) ?? lower;
}

export function parseChord(input) {
  const raw = String(input ?? "").trim().toLowerCase();
  if (!raw) return { ok: false, reason: "empty-chord" };
  const parts = raw.split("+").map((part) => part.trim()).filter(Boolean);
  const modifiers = [];
  for (const part of parts.slice(0, -1)) {
    if (WIN_MODIFIERS.has(part)) return { ok: false, reason: "win-modifier-refused" };
    const normalized = MODIFIER_ALIASES.get(part);
    if (!normalized) return { ok: false, reason: `unsupported-modifier:${part}` };
    if (modifiers.includes(normalized)) return { ok: false, reason: `duplicate-modifier:${normalized}` };
    modifiers.push(normalized);
  }
  const keyName = parts.at(-1);
  if (!keyName || !ALLOWED_KEYS.has(keyName)) return { ok: false, reason: `unsupported-key:${keyName ?? ""}` };
  const key = canonicalKey(keyName);
  const canonical = [...MODIFIER_ORDER.filter((modifier) => modifiers.includes(modifier)), key].join("+");
  if (DENIED_CHORDS.has(canonical)) return { ok: false, reason: `denied-chord:${canonical}` };
  return { ok: true, modifiers, key, canonical };
}

export function screenShortcut({ chord } = {}) {
  if (typeof chord !== "string" || chord.trim() === "") return { allowed: false, reason: "no-key-requested", steps: [] };
  const parsed = parseChord(chord);
  if (!parsed.ok) return { allowed: false, reason: parsed.reason, steps: [] };
  return { allowed: true, reason: "screened", steps: [parsed.canonical] };
}
