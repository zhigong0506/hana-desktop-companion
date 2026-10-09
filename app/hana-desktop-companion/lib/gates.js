// lib/gates.js — the App-level real-action gate.
//
// Two independent things must both say yes before anything is injected:
//   1. The HOST has reviewed this call (it always will: no tool below declares
//      sessionPermission.readOnly, and none claims auto:"allow"). That is the
//      permission ledger's job and this App must not pretend to replace it.
//   2. The App's own opt-in switch `allowRealInput` is on (default off) and the
//      action is inside the user's declared actionPolicy.
//
// A confirmation string typed by the model is NEVER treated as user approval.

export const CONFIRM_PHRASE = "I_UNDERSTAND_DESKTOP_INPUT";

export const ACTION_RISK = {
  "capture-window": "read",
  "uia-tree": "read",
  "uia-find": "read",
  "hit-test": "read",
  "verify-target": "read",
  "manage-window:minimize": "sensitive",
  "manage-window:restore": "sensitive",
  "manage-window:maximize": "sensitive",
  "manage-window:move": "sensitive",
  "manage-window:resize": "sensitive",
  "manage-window:close": "destructive",
  "mouse-drag": "destructive",
  "mouse-wheel": "destructive",
};

const POLICY_ALLOW = {
  "all-real-actions": ["sensitive", "destructive"],
  "sensitive-and-destructive": ["sensitive", "destructive"],
  "destructive-only": ["destructive"],
  read: [],
};

// v2 App convenience members are async (they cross the App RPC boundary), so
// config.get must be awaited. Await failures fall back to the safest defaults
// (real input disabled), never to "allow".
export async function resolveConfig(sdk) {
  const read = async (key, fallback) => {
    try {
      const v = await sdk.config.get(key);
      return v === undefined || v === null ? fallback : v;
    } catch {
      return fallback;
    }
  };
  const allowRealInput = (await read("allowRealInput", false)) === true;
  const policy = await read("actionPolicy", "destructive-only");
  return {
    allowRealInput,
    actionPolicy: POLICY_ALLOW[policy] ? policy : "destructive-only",
  };
}

export function riskOf(verb, action) {
  return action ? ACTION_RISK[`${verb}:${action}`] ?? "destructive" : ACTION_RISK[verb] ?? "unknown";
}

/**
 * Decide whether an executable verb may actually run.
 * Returns { allowed, risk, reason }.
 */
export function evaluateRealAction({ config, verb, action, confirmation, requiresTarget }) {
  const risk = riskOf(verb, action);

  if (risk === "read") return { allowed: true, risk, reason: "read-only observation" };

  if (!config.allowRealInput) {
    return { allowed: false, risk, reason: "app-disabled: allowRealInput is off (default). Enable it in this App's settings only if you intend real desktop input." };
  }
  if (!POLICY_ALLOW[config.actionPolicy].includes(risk)) {
    return { allowed: false, risk, reason: `action-policy: ${config.actionPolicy} does not allow risk tier '${risk}'.` };
  }
  if (!requiresTarget) {
    return { allowed: false, risk, reason: "no-target-bound: real input requires an explicit hwnd + expectedPid from a fresh list-windows call." };
  }
  if (confirmation !== CONFIRM_PHRASE) {
    return { allowed: false, risk, reason: `missing-confirmation: the model must pass confirmation='${CONFIRM_PHRASE}' to acknowledge this is real input. This string is an acknowledgement, not a substitute for the host's review of the call.` };
  }
  return { allowed: true, risk, reason: "approved-by-app-gate" };
}

export const TARGET_REQUIRED = new Set(["capture-window", "uia-tree", "uia-find", "verify-target", "manage-window", "mouse-drag", "mouse-wheel"]);

export function hasTargetBinding(input) {
  return Boolean(input?.hwnd) && Number.isFinite(Number(input?.expectedPid));
}
