// Pure-logic tests for the App-level real-action gate. No desktop, no helper,
// no clipboard: these assert the refusals, which is what the gate exists for.

import test from "node:test";
import assert from "node:assert/strict";

import {
  ACTION_RISK,
  CONFIRM_PHRASE,
  TARGET_REQUIRED,
  allowedRiskTiers,
  evaluateRealAction,
  hasTargetBinding,
  resolveConfig,
  riskOf,
} from "../app/hana-desktop-companion/lib/gates.js";

const configOf = (allowRealInput, actionPolicy = "destructive-only") => ({ allowRealInput, actionPolicy });
const bound = { hwnd: "123456", expectedPid: 4242 };

test("allowRealInput defaults to off and an unknown policy falls back to the strictest tier", async () => {
  const sdk = { config: { get: async () => undefined } };
  const config = await resolveConfig(sdk);
  assert.equal(config.allowRealInput, false);
  assert.equal(config.actionPolicy, "destructive-only");
});

test("resolveConfig never turns a failing config read into an allow", async () => {
  const sdk = {
    config: {
      get: async () => {
        throw new Error("rpc down");
      },
    },
  };
  const config = await resolveConfig(sdk);
  assert.equal(config.allowRealInput, false);
});

test("an unknown actionPolicy value is clamped to destructive-only", async () => {
  const sdk = { config: { get: async (key) => (key === "allowRealInput" ? true : "everything") } };
  const config = await resolveConfig(sdk);
  assert.equal(config.actionPolicy, "destructive-only");
  assert.deepEqual(allowedRiskTiers(config.actionPolicy), ["destructive"]);
});

test("every new verb is classified; keyboard and clipboard reads are at least sensitive", () => {
  assert.equal(riskOf("send-keys"), "sensitive");
  assert.equal(riskOf("clipboard-read"), "sensitive");
  assert.equal(riskOf("list-displays"), "read");
  assert.equal(riskOf("wait-for"), "read");
  assert.equal(riskOf("capabilities"), "read");
  assert.equal(riskOf("manage-window", "close"), "destructive");
  assert.equal(ACTION_RISK["send-keys"], "sensitive");
});

test("clipboard write is destructive and requires a target binding", () => {
  assert.equal(riskOf("clipboard-write"), "destructive");
  assert.equal(ACTION_RISK["clipboard-write"], "destructive");
  assert.equal(TARGET_REQUIRED.has("clipboard-write"), true);
});

test("observation verbs stay allowed with real input disabled", () => {
  for (const verb of ["wait-for", "list-displays", "capabilities"]) {
    const verdict = evaluateRealAction({ config: configOf(false), verb });
    assert.equal(verdict.allowed, true, verb);
    assert.equal(verdict.risk, "read", verb);
  }
});

test("send-keys is blocked by default even with a target binding and the confirmation phrase", () => {
  const verdict = evaluateRealAction({
    config: configOf(false),
    verb: "send-keys",
    confirmation: CONFIRM_PHRASE,
    requiresTarget: true,
  });
  assert.equal(verdict.allowed, false);
  assert.match(verdict.reason, /app-disabled/);
});

test("destructive-only policy still blocks sensitive verbs once real input is on", () => {
  const verdict = evaluateRealAction({
    config: configOf(true, "destructive-only"),
    verb: "clipboard-read",
    confirmation: CONFIRM_PHRASE,
    requiresTarget: true,
  });
  assert.equal(verdict.allowed, false);
  assert.match(verdict.reason, /action-policy/);
});

test("a sensitive verb without a fresh target binding is refused", () => {
  const verdict = evaluateRealAction({
    config: configOf(true, "sensitive-and-destructive"),
    verb: "send-keys",
    confirmation: CONFIRM_PHRASE,
    requiresTarget: false,
  });
  assert.equal(verdict.allowed, false);
  assert.match(verdict.reason, /no-target-bound/);
});

test("the model's confirmation string alone is not enough", () => {
  const verdict = evaluateRealAction({
    config: configOf(true, "sensitive-and-destructive"),
    verb: "clipboard-read",
    confirmation: "yes please do it",
    requiresTarget: true,
  });
  assert.equal(verdict.allowed, false);
  assert.match(verdict.reason, /missing-confirmation/);
});

test("only the exact acknowledgement phrase passes the last App gate", () => {
  const verdict = evaluateRealAction({
    config: configOf(true, "sensitive-and-destructive"),
    verb: "send-keys",
    confirmation: CONFIRM_PHRASE,
    requiresTarget: true,
  });
  assert.equal(verdict.allowed, true);
  assert.equal(verdict.risk, "sensitive");
});

test("target binding needs both a handle and a finite pid", () => {
  assert.equal(hasTargetBinding(bound), true);
  assert.equal(hasTargetBinding({ hwnd: "123" }), false);
  assert.equal(hasTargetBinding({ expectedPid: 10 }), false);
  assert.equal(hasTargetBinding({ hwnd: "123", expectedPid: "abc" }), false);
  for (const verb of ["send-keys", "clipboard-read"]) {
    assert.equal(TARGET_REQUIRED.has(verb), true, verb);
  }
});
