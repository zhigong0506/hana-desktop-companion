import { defineApp } from "./sdk/app-contract/server-client.js";
import fs from "node:fs/promises";
import path from "node:path";
import { runHelper, runHelperWithInput } from "./lib/helper.js";
import {
  CONFIRM_PHRASE,
  resolveConfig,
  evaluateRealAction,
  hasTargetBinding,
  allowedRiskTiers,
} from "./lib/gates.js";
import { screenShortcut } from "./lib/shortcuts.js";
import { normalizeWaitFor, WAIT_FOR_FIELDS, WAIT_CONDITIONS } from "./lib/waitfor.js";
import {
  normalizeClipboardRead,
  normalizeClipboardWrite,
  clipboardReceipt,
  redactForLog,
  CLIPBOARD_READ_MAX_CHARS,
  CLIPBOARD_WRITE_MAX_BYTES,
  CLIPBOARD_WRITE_MAX_CHARS,
} from "./lib/clipboard.js";

// Hana Desktop Companion — v2 App.
//
// Supplements Hana's native `computer` tool. It does NOT re-implement ordinary
// click / type / invoke (computer does those under the host's own lease
// approval). It adds the two things computer cannot do reliably:
//   - PrintWindow capture of a SPECIFIC window, even when occluded, bound to
//     HWND+PID, delivered as a session image;
//   - read-only UIA tree/find and per-monitor hit-testing for diagnosis;
//   - guarded advanced input (drag / wheel / keyboard chords) and window
//     management (move / resize / minimize / maximize / restore / graceful
//     close);
//   - multi-monitor inventory, bounded wait-for observation, and gated
//     clipboard read/write. Write text travels over helper stdin, not argv or
//     disk; the tool call itself may be retained by Hana's host.
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

// Verbs that require an explicit HWND+PID binding expose only those two
// selectors: a title/foreground match is not an identity for real input.
const boundTargetProps = {
  hwnd: { type: "string", description: "十进制窗口句柄，来自 desktop_windows_list。必填。" },
  expectedPid: { type: "integer", description: "该窗口的 PID。必填；句柄复用或 PID 变化会被拒绝。" },
};

export default defineApp(async (sdk) => {
  await sdk.logger.info("hana-desktop-companion loaded");

  // Version is reported by desktop_capabilities; it is informational only, so a
  // read failure must never block tool registration.
  let appVersion = "unknown";
  try {
    const manifest = JSON.parse(await fs.readFile(new URL("./manifest.json", import.meta.url), "utf8"));
    appVersion = manifest.version ?? "unknown";
  } catch {
    /* keep "unknown" */
  }

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

  // 9) displays — read-only
  await sdk.tools.register({
    name: "desktop_displays_list",
    description:
      "列出显示器与虚拟桌面几何：每屏 bounds / workArea（物理像素）、是否主屏、有效 DPI 与缩放百分比，以及虚拟桌面总边界。纯观察，不注入、不改窗口。用于多屏定位、判断坐标落在哪一屏。",
    parameters: { type: "object", properties: {} },
    sessionPermission: { readOnly: true },
    execute: async () => json(await runHelper("list-displays")),
  });

  // 10) wait-for — read-only bounded observation
  await sdk.tools.register({
    name: "desktop_wait_for",
    description:
      "有界等待一个观察条件成立：窗口消失/可见/最小化/成为前台、标题包含关键词、UIA 元素出现或消失、某坐标命中目标窗口。最长 30000ms（默认 5000ms），到点返回 timedOut=true。它只返回观察结果：不执行动作、不自动继续任何计划，也不改变焦点或窗口状态。每次轮询都重新核验绑定窗口的 HWND+PID：句柄被其他进程复用时直接以 blockedBy=handle-reused 中止（不会静默超时）；element-gone 只在整个 UIA 遍历完整且未命中时才算成立，UIA 读不到窗口或节点预算耗尽时一律视为状态未知。",
    parameters: {
      type: "object",
      properties: {
        condition: { type: "string", enum: WAIT_CONDITIONS, description: "等待的条件。" },
        ...targetSelectProps,
        query: { type: "string", description: "title-contains / element-exists / element-gone 必需的关键词。" },
        role: { type: "string", description: "可选 UIA 角色过滤，如 Button、Edit。" },
        x: { type: "integer", description: "point-hits-target 的物理像素 X。" },
        y: { type: "integer", description: "point-hits-target 的物理像素 Y。" },
        timeoutMs: { type: "integer", minimum: 200, maximum: 30000, description: "最长等待毫秒，默认 5000，上限 30000。" },
        intervalMs: { type: "integer", minimum: 100, maximum: 2000, description: "轮询间隔毫秒，默认 250。" },
      },
      required: ["condition"],
    },
    sessionPermission: { readOnly: true },
    execute: async (args = {}) => {
      const plan = normalizeWaitFor(args);
      if (!plan.ok) return json({ ok: false, blockedBy: plan.reason, note: "参数未通过校验，未启动等待。" });
      return json(await runHelper("wait-for", pick(args, WAIT_FOR_FIELDS)));
    },
  });

  // 11) send-keys — mutating (sensitive), default preview
  await sdk.tools.register({
    name: "desktop_send_keys",
    description:
      `向已绑定且当前处于前台的窗口发送一个受限快捷键（chord，如 ctrl+shift+s）。每次调用仅允许一个 chord，避免一条宿主审阅触发多步输入。默认 execute=false 只返回计划；真实执行需 allowRealInput 开启 + actionPolicy 允许 sensitive + hwnd+expectedPid 绑定 + 确认短语 + 宿主逐次审阅。含 Win 修饰键或系统级组合（Win+L、Alt+F4、Ctrl+Shift+Esc、Ctrl+Alt+Delete 等）一律拒绝。不做自由文本输入（那属于原生 computer）。`,
    parameters: {
      type: "object",
      properties: {
        chord: { type: "string", description: "唯一允许的按键动作，如 ctrl+shift+s；修饰键仅限 ctrl/alt/shift，末位为键名。" },
        holdMs: { type: "integer", minimum: 0, maximum: 500, description: "按下保持毫秒，默认 40。" },
        ...boundTargetProps,
        execute: { type: "boolean", description: "默认 false=仅预演。" },
        confirmation: { type: "string", description: `实际注入须回填：${CONFIRM_PHRASE}（知悉回执，不替代宿主审阅）。` },
      },
      required: ["chord", "hwnd", "expectedPid"],
    },
    execute: (args) =>
      runMutation(sdk, "send-keys", args, ["chord", "holdMs", "hwnd", "expectedPid"], {
        screen: () => screenShortcut(args),
      }),
  });

  // 12) clipboard-read — sensitive, default preview, truncated
  await sdk.tools.register({
    name: "desktop_clipboard_read",
    description:
      `读取当前剪贴板纯文本（按 sensitive 处理：默认 execute=false 只返回计划；真实读取需 allowRealInput + actionPolicy 允许 sensitive + 绑定 hwnd+expectedPid + 确认短语 + 宿主审阅）。返回内容严格截断（默认 2000，上限 ${CLIPBOARD_READ_MAX_CHARS} 字符），并给出原始长度与是否截断。注意：读取到的文本会作为本次工具结果返回给调用方，因而会进入宿主/模型上下文；本 App 只保证它不被写进日志或错误详情，不宣称它不会出现在宿主的会话记录里。剪贴板是进程全局的，绑定窗口只是审计锚点。`,
    parameters: {
      type: "object",
      properties: {
        ...boundTargetProps,
        maxChars: { type: "integer", minimum: 1, maximum: CLIPBOARD_READ_MAX_CHARS, description: "最多返回字符数，默认 2000。" },
        execute: { type: "boolean", description: "默认 false=不读取。" },
        confirmation: { type: "string", description: `实际读取须回填：${CONFIRM_PHRASE}。` },
      },
      required: ["hwnd", "expectedPid"],
    },
    execute: async (args = {}) => {
      const bounded = normalizeClipboardRead(args);
      return runMutation(sdk, "clipboard-read", { ...args, maxChars: bounded.maxChars }, ["hwnd", "expectedPid", "maxChars"], {
        // The text is the deliverable, so it stays in the reply; everything else
        // (including any future field) is redacted and a size receipt is added.
        shape: (result) => {
          if (!result || result.ok !== true || typeof result.text !== "string") return redactForLog(result);
          const { text: clipboardText, ...rest } = result;
          return { ...rest, receipt: clipboardReceipt(result), text: clipboardText };
        },
      });
    },
  });

  // 13) clipboard-write — destructive, stdin-only payload, receipt only
  await sdk.tools.register({
    name: "desktop_clipboard_write",
    description:
      `将非凭据纯文本写入系统剪贴板（destructive，默认 execute=false 只做计划；真实写入需 allowRealInput + actionPolicy 允许 destructive + HWND/PID 绑定 + 确认短语 + 宿主逐次审阅）。正文经 stdin 传给固定 helper，不进进程命令行或临时文件；但正文作为工具参数发送给 Hana，可能进入宿主会话记录。仅接受 1–${CLIPBOARD_WRITE_MAX_CHARS} 字符且 UTF-8 不超过 ${CLIPBOARD_WRITE_MAX_BYTES} 字节的非空文本，超限拒绝、不截断；返回长度与剪贴板序号，不回显正文。`,
    parameters: {
      type: "object",
      properties: {
        ...boundTargetProps,
        text: { type: "string", minLength: 1, maxLength: CLIPBOARD_WRITE_MAX_CHARS, description: "非凭据纯文本；会进入本次工具调用，可能被宿主记录。" },
        execute: { type: "boolean", description: "默认 false=只预览，不接触剪贴板。" },
        confirmation: { type: "string", description: `真实写入须回填：${CONFIRM_PHRASE}。` },
      },
      required: ["hwnd", "expectedPid", "text"],
    },
    execute: async (args = {}) => {
      const normalized = normalizeClipboardWrite(args);
      if (!normalized.ok) return json({ requestedExecute: args.execute === true, executed: false, blockedBy: normalized.error, ...(normalized.chars === undefined ? {} : { chars: normalized.chars, bytes: normalized.bytes, maxChars: normalized.maxChars, maxBytes: normalized.maxBytes }) });
      const config = await resolveConfig(sdk);
      const verdict = evaluateRealAction({ config, verb: "clipboard-write", confirmation: args.confirmation, requiresTarget: hasTargetBinding(args) });
      if (args.execute !== true) return json({ requestedExecute: false, executed: false, plannedChars: normalized.chars, plannedBytes: normalized.bytes, targetBound: hasTargetBinding(args), note: "Clipboard unchanged; text was not sent to the helper." });
      if (!verdict.allowed) return json({ requestedExecute: true, executed: false, blockedBy: verdict.reason, risk: verdict.risk });
      const forwarded = await runHelperWithInput("clipboard-write", { hwnd: args.hwnd, expectedPid: args.expectedPid, maxChars: CLIPBOARD_WRITE_MAX_CHARS, execute: true }, args.text);
      const receipt = forwarded?.ok === true ? { ok: true, action: "clipboard-write", target: forwarded.target, chars: forwarded.chars, bytes: forwarded.bytes, seqBefore: forwarded.seqBefore, seqAfter: forwarded.seqAfter, changed: forwarded.changed } : redactForLog(forwarded);
      return json({ requestedExecute: true, risk: verdict.risk, gate: verdict.reason, forwarded: receipt });
    },
  });

  // 14) capabilities — read-only status receipt
  await sdk.tools.register({
    name: "desktop_capabilities",
    description:
      "返回能力与控制状态回执：App/helper 版本、固定动词清单与只读/受控分组、当前 allowRealInput 与 actionPolicy 实际允许的风险档，以及明确未实现的能力（OCR、应用启动、进程/注册表/文件系统、任意 shell、批量动作计划、全屏截图）。纯观察。",
    parameters: { type: "object", properties: {} },
    sessionPermission: { readOnly: true },
    execute: async () => {
      const config = await resolveConfig(sdk);
      const tiers = allowedRiskTiers(config.actionPolicy);
      let helper = null;
      try {
        helper = await runHelper("capabilities");
      } catch (err) {
        helper = { ok: false, error: err?.message || String(err) };
      }
      return json({
        ok: true,
        app: { name, version: appVersion, platform: process.platform, arch: process.arch },
        gates: {
          allowRealInput: config.allowRealInput,
          actionPolicy: config.actionPolicy,
          riskTiersAllowed: tiers,
          sensitiveActionsEnabled: config.allowRealInput && tiers.includes("sensitive"),
          destructiveActionsEnabled: config.allowRealInput && tiers.includes("destructive"),
        },
        helper: redactForLog(helper),
        appTools: [
          "desktop_windows_list",
          "desktop_window_capture",
          "desktop_uia_tree",
          "desktop_uia_find",
          "desktop_hit_probe",
          "desktop_window_manage",
          "desktop_mouse_drag",
          "desktop_mouse_wheel",
          "desktop_displays_list",
          "desktop_wait_for",
          "desktop_send_keys",
          "desktop_clipboard_read",
          "desktop_clipboard_write",
          "desktop_capabilities",
        ],
        notImplemented: [
          "ocr (capture returns a session image; hand it to the host or an external OCR step — no OCR engine is bundled and no arbitrary image path is accepted)",
          "app launch",
          "process / registry / filesystem inventory",
          "arbitrary shell or executable",
          "batch action plans",
          "full-screen capture",
        ],
        securityNotes: [
          "Clipboard write plus a separately reviewed Ctrl+V can paste tool-supplied text into the foreground app; use only non-credential text and review each call.",
        ],
      });
    },
  });
});

// Shared flow for the mutating verbs.
async function runMutation(sdk, verb, args, fields, gateCtx = {}) {
  const wantsExecute = args.execute === true;
  const config = await resolveConfig(sdk);
  const binding = hasTargetBinding(args);

  // Fixed key/chord screening runs before any helper call, preview included: a
  // denied chord is never worth resolving a target for.
  const screen = typeof gateCtx.screen === "function" ? gateCtx.screen() : null;
  if (screen && !screen.allowed) {
    return json({
      requestedExecute: wantsExecute,
      executed: false,
      blockedBy: `screened:${screen.reason}`,
      note: "被固定键位筛查表拒绝，未调用 helper、未注入。",
    });
  }

  const shape = typeof gateCtx.shape === "function" ? gateCtx.shape : (result) => result;

  // Preview path (execute not requested): zero side effects, still runs the guard.
  if (!wantsExecute) {
    const opts = pick(args, fields);
    const result = await runHelper(verb, opts);
    return json({ requestedExecute: false, screen, forwarded: shape(result) });
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
      screen,
      note: "已停在 App 门之前，未注入。需要真实执行请先在应用设置开启 allowRealInput 并绑定 hwnd+expectedPid。",
    });
  }
  const opts = pick(args, fields);
  opts.execute = true;
  const result = await runHelper(verb, opts);
  return json({ requestedExecute: true, risk: verdict.risk, gate: verdict.reason, screen, forwarded: shape(result) });
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
