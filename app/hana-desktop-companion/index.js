import { defineApp } from "./sdk/app-contract/server-client.js";
import fs from "node:fs/promises";
import path from "node:path";
import { runHelper } from "./lib/helper.js";
import {
  CONFIRM_PHRASE,
  resolveConfig,
  evaluateRealAction,
  hasTargetBinding,
} from "./lib/gates.js";

// Hana Desktop Companion — v2 App.
//
// Supplements Hana's native `computer` tool. It does NOT re-implement ordinary
// click / type / invoke (computer does those under the host's own lease
// approval). It adds the two things computer cannot do reliably:
//   - PrintWindow capture of a SPECIFIC window, even when occluded, bound to
//     HWND+PID, delivered as a session image;
//   - read-only UIA tree/find and per-monitor hit-testing for diagnosis;
//   - guarded advanced input (drag / wheel) and window management (move /
//     resize / minimize / maximize / restore / graceful close).
//
// Safety posture (defence in depth, host review is always the outer layer):
//   - Observation tools are declared readOnly. Capture is NOT, because its
//     wakeIfNeeded path can momentarily reveal a hidden window.
//   - No mutating tool claims auto:"allow" or readOnly — the host reviews every
//     one of them.
//   - Real input additionally requires the App's own allowRealInput switch ON
//     (default OFF), an actionPolicy tier that admits the risk, a fresh
//     HWND+expectedPid binding, and the model echoing the confirmation phrase.
//   - The native helper defaults to dryRun preview; execute=true is forwarded
//     only when every gate above passed. A confirmation string typed by the
//     model is never treated as user approval — it is only an acknowledgement;
//     the host's per-call review remains mandatory.

export const name = "hana-desktop-companion";

const text = (t) => ({ content: [{ type: "text", text: t }] });
const json = (o) => ({ content: [{ type: "text", text: JSON.stringify(o, null, 2) }] });

// ---- observation tool schemas ----
const targetSelectProps = {
  hwnd: { type: "string", description: "十进制窗口句柄，来自 windows_list。优先于 title。" },
  expectedPid: { type: "integer", description: "windows_list 一并返回的 PID。句柄被系统复用时用于拒绝误截/误操作。" },
  title: { type: "string", description: "窗口标题子串（仅当唯一匹配时才用；歧义会拒绝，请改用 hwnd）。" },
  foreground: { type: "boolean", description: "定位当前前台窗口（仅当你已知其身份时使用）。" },
};

export default defineApp(async (sdk) => {
  await sdk.logger.info("hana-desktop-companion loaded");

  // 1) windows_list — read-only
  await sdk.tools.register({
    name: "desktop_windows_list",
    description:
      "列出 Windows 顶层应用窗口，返回十进制 hwnd、pid、标题、进程名与可见/前台/最小化状态。截图或高级动作前先调用它取得 hwnd+expectedPid。",
    parameters: {
      type: "object",
      properties: {
        query: { type: "string", description: "按标题或进程名过滤（可选）。" },
        limit: { type: "integer", minimum: 1, maximum: 100, description: "最多返回窗口数，默认 40。" },
        includeMinimized: { type: "boolean", description: "是否包含最小化窗口，默认 false。" },
        includeHidden: { type: "boolean", description: "是否包含隐藏窗口（需 query 收窄），默认 false。" },
      },
    },
    sessionPermission: { readOnly: true },
    execute: async (args = {}) => json(await runHelper("list-windows", pick(args, ["query", "limit", "includeMinimized", "includeHidden"]))),
  });

  // 2) capture — NOT readOnly: wakeIfNeeded can briefly reveal a hidden window
  await sdk.tools.register({
    name: "desktop_window_capture",
    description:
      "对指定的单个窗口用 PrintWindow(PW_RENDERFULLCONTENT) 截图，即使它被其他窗口遮挡也能拿到该窗口自身像素；绝不静默全屏回退。返回图片会话文件与证据（hwnd/pid 绑定、method、blackRatio）。minimized/hidden 需显式 wakeIfNeeded=true，并在采集后恢复原状态。",
    parameters: {
      type: "object",
      properties: {
        ...targetSelectProps,
        maxDimension: { type: "integer", minimum: 640, maximum: 4096, description: "最长边上限像素，默认 2560。" },
        wakeIfNeeded: { type: "boolean", description: "默认 false。为 true 时临时唤醒最小化/隐藏窗口采集后复位。" },
        allowVisibleFallback: { type: "boolean", description: "PrintWindow 空且该窗口正是前台时，允许屏幕像素回退。默认 false。" },
        disableAutoCrop: { type: "boolean", description: "保留原始画布，不做黑边透明化/裁剪。默认 false。" },
      },
    },
    execute: async (args = {}) => {
      if (!args.hwnd && !args.title && !args.foreground) {
        return json({ ok: false, error: "需先用 desktop_windows_list 取得 hwnd，或提供唯一 title / foreground。" });
      }
      const outDir = path.join(sdk.dataDir, "captures");
      await fs.mkdir(outDir, { recursive: true });
      const stamp = new Date().toISOString().replace(/[:.]/g, "-");
      const output = path.join(outDir, `capture-${stamp}.png`);
      const opts = pick(args, ["hwnd", "expectedPid", "title", "foreground", "maxDimension", "wakeIfNeeded", "allowVisibleFallback", "disableAutoCrop"]);
      opts.output = output;
      const result = await runHelper("capture-window", opts);
      if (!result || result.ok !== true || !result.capture) {
        return json({ ok: false, stage: "not-attempted", helper: result });
      }
      let delivery = null;
      try {
        delivery = await sdk.resources.stage({ path: output, name: path.basename(output), mime: "image/png", deliverAs: "media" });
      } catch (err) {
        return json({ ok: true, imageDelivered: false, reason: err?.message || String(err), window: result.window, capture: redactCapture(result.capture) });
      } finally {
        fs.rm(output, { force: true }).catch(() => {});
      }
      return json({
        ok: true,
        imageDelivered: true,
        fileId: delivery?.resource?.fileId ?? null,
        window: result.window,
        capture: redactCapture(result.capture),
      });
    },
  });

  // 3) uia-tree — read-only
  await sdk.tools.register({
    name: "desktop_uia_tree",
    description:
      "读取目标窗口的 Windows UI Automation 元素摘要（角色、名称、className、automationId、可用 pattern、边界），只读不注入。用于诊断原生 computer 读不到的控件；日常交互仍优先 computer。",
    parameters: {
      type: "object",
      properties: {
        ...targetSelectProps,
        maxElements: { type: "integer", minimum: 1, maximum: 300, description: "最多读取元素数，默认 80。" },
        includeOffscreen: { type: "boolean", description: "是否包含屏幕外元素，默认 false。" },
      },
    },
    sessionPermission: { readOnly: true },
    execute: async (args = {}) => json(await runHelper("uia-tree", pick(args, ["hwnd", "expectedPid", "title", "foreground", "maxElements", "includeOffscreen"]))),
  });

  // 4) uia-find — read-only
  await sdk.tools.register({
    name: "desktop_uia_find",
    description:
      "在目标窗口的 UIA 树中按关键词查找控件，返回候选与边界，只读。用于确认某个按钮/输入框是否真实存在及其物理像素位置。",
    parameters: {
      type: "object",
      properties: {
        ...targetSelectProps,
        query: { type: "string", description: "要查找的关键词，匹配 name / automationId / className / role。" },
        role: { type: "string", description: "可选角色过滤，如 Button、Edit、ListItem。" },
        maxElements: { type: "integer", minimum: 1, maximum: 300, description: "最多读取元素数，默认 80。" },
      },
      required: ["query"],
    },
    sessionPermission: { readOnly: true },
    execute: async (args = {}) => json(await runHelper("uia-find", pick(args, ["hwnd", "expectedPid", "title", "foreground", "query", "role", "maxElements"]))),
  });

  // 5) hit-probe — read-only
  await sdk.tools.register({
    name: "desktop_hit_probe",
    description:
      "只读命中测试：给定物理像素坐标，返回该点 Z 序最顶的顶层窗口与当前前台窗口。不移动光标、不注入。用于高级动作前判断坐标当下落在谁身上（遮挡时尤其重要）。",
    parameters: {
      type: "object",
      properties: {
        x: { type: "integer", description: "物理像素 X。" },
        y: { type: "integer", description: "物理像素 Y。" },
      },
      required: ["x", "y"],
    },
    sessionPermission: { readOnly: true },
    execute: async (args = {}) => json(await runHelper("hit-test", pick(args, ["x", "y"]))),
  });

  // 6) window-manage — mutating, default preview
  await sdk.tools.register({
    name: "desktop_window_manage",
    description:
      `管理窗口状态：maximize/minimize/restore/move/resize/close，走 ShowWindow/SetWindowPos/WM_CLOSE，不猜坐标。默认 dryRun 只返回计划；须 execute=true 且通过 App 真实输入开关与宿主审阅才实际执行。close 是不可逆动作，强制显式 hwnd。move/resize 用物理像素。`,
    parameters: {
      type: "object",
      properties: {
        action: { type: "string", enum: ["maximize", "minimize", "restore", "move", "resize", "close"], description: "窗口操作。close 必须给显式 hwnd。" },
        ...targetSelectProps,
        x: { type: "integer", description: "move 的目标左上角 X（物理像素）。" },
        y: { type: "integer", description: "move 的目标左上角 Y（物理像素）。" },
        width: { type: "integer", description: "resize 的目标宽度（物理像素）。" },
        height: { type: "integer", description: "resize 的目标高度（物理像素）。" },
        execute: { type: "boolean", description: "默认 false=仅预演。true 才实际改动窗口。" },
        confirmation: { type: "string", description: `实际执行须回填确认短语：${CONFIRM_PHRASE}（这是模型的知悉回执，不替代宿主审阅）。` },
      },
      required: ["action"],
    },
    execute: (args) => runMutation(sdk, "manage-window", args, ["action", "hwnd", "expectedPid", "title", "foreground", "x", "y", "width", "height"], { action: args.action }),
  });

  // 7) mouse-drag — mutating, default preview
  await sdk.tools.register({
    name: "desktop_mouse_drag",
    description:
      "真实鼠标从起点拖到终点（会真的按住并移动系统指针），用于拖滑块/排序/画布平移等 UIA 无法表达的操作。默认 dryRun 仅跑命中守卫并返回计划；须 execute=true + App 开关 + 目标 hwnd+expectedPid 绑定 + 宿主审阅才注入。起点坐标若当下不落在此 hwnd，会被拒绝。",
    parameters: {
      type: "object",
      properties: {
        fromX: { type: "integer", description: "起点 X（物理屏幕像素）。" },
        fromY: { type: "integer", description: "起点 Y（物理屏幕像素）。" },
        toX: { type: "integer", description: "终点 X（物理屏幕像素）。" },
        toY: { type: "integer", description: "终点 Y（物理屏幕像素）。" },
        button: { type: "string", enum: ["left", "right", "middle"], description: "拖动按键，默认 left。" },
        ...targetSelectProps,
        execute: { type: "boolean", description: "默认 false=仅预演。" },
        confirmation: { type: "string", description: `实际注入须回填：${CONFIRM_PHRASE}。` },
      },
      required: ["fromX", "fromY", "toX", "toY"],
    },
    execute: (args) => runMutation(sdk, "mouse-drag", args, ["fromX", "fromY", "toX", "toY", "button", "steps", "hwnd", "expectedPid"]),
  });

  // 8) mouse-wheel — mutating, default preview
  await sdk.tools.register({
    name: "desktop_mouse_wheel",
    description:
      "在指定物理坐标用真实滚轮滚动（会先把系统指针移到该点，因为 Windows 按光标位置路由滚轮）。默认 dryRun 仅跑命中守卫并返回计划；须 execute=true + App 开关 + 目标绑定 + 宿主审阅才注入。notches 正=上/右，负=下/左。",
    parameters: {
      type: "object",
      properties: {
        x: { type: "integer", description: "滚动点 X（物理像素）。" },
        y: { type: "integer", description: "滚动点 Y（物理像素）。" },
        notches: { type: "integer", description: "滚动格数，±，默认 -3。" },
        axis: { type: "string", enum: ["vertical", "horizontal"], description: "滚动轴，默认 vertical。" },
        ...targetSelectProps,
        execute: { type: "boolean", description: "默认 false=仅预演。" },
        confirmation: { type: "string", description: `实际注入须回填：${CONFIRM_PHRASE}。` },
      },
      required: ["x", "y"],
    },
    execute: (args) => runMutation(sdk, "mouse-wheel", args, ["x", "y", "notches", "axis", "hwnd", "expectedPid"]),
  });
});

// Shared flow for the three mutating verbs.
async function runMutation(sdk, verb, args, fields, gateCtx = {}) {
  const wantsExecute = args.execute === true;
  const config = await resolveConfig(sdk);
  const binding = hasTargetBinding(args);

  // Preview path (execute not requested): zero side effects, still runs the guard.
  if (!wantsExecute) {
    const opts = pick(args, fields);
    const result = await runHelper(verb, opts);
    return json({ requestedExecute: false, forwarded: result });
  }

  // Execute requested — every App gate must pass before forwarding execute=true.
  const verdict = evaluateRealAction({
    config,
    verb,
    action: gateCtx.action,
    confirmation: args.confirmation,
    requiresTarget: binding,
  });
  if (!verdict.allowed) {
    return json({
      requestedExecute: true,
      executed: false,
      blockedBy: verdict.reason,
      risk: verdict.risk,
      note: "已停在 App 门之前，未注入。需要真实执行请先在应用设置开启 allowRealInput 并绑定 hwnd+expectedPid。",
    });
  }
  const opts = pick(args, fields);
  opts.execute = true;
  const result = await runHelper(verb, opts);
  return json({ requestedExecute: true, risk: verdict.risk, gate: verdict.reason, forwarded: result });
}

function pick(args, fields) {
  const out = {};
  for (const f of fields) if (args[f] !== undefined) out[f] = args[f];
  return out;
}

function redactCapture(c) {
  // Drop the on-disk absolute path; the session fileId is the deliverable.
  const { path: _path, ...rest } = c || {};
  return rest;
}
