# Hana 桌面观察补充（hana-desktop-companion）

v2 App，补充 Hana 原生 `computer` 工具。**不替代**常规点击/输入/Invoke——那些仍优先用 `computer`（在宿主自带的 lease 审批下运行）。本 App 只做 `computer` 目前不能可靠覆盖的事：

1. **定向截图**：对指定单个窗口用 `PrintWindow(PW_RENDERFULLCONTENT)` 采集，即使被其他窗口遮挡也能拿到该窗口自身像素；绝不静默全屏回退；HWND+PID 绑定，采集为会话图片交付。
2. **UIA 只读诊断**：读取目标窗口 UI Automation 元素树 / 按关键词查找控件，用于原生 `computer` 读不到语义时的补充观察。只读、不注入。
3. **高级动作**：拖拽、滚轮、窗口管理（最大化/最小化/还原/移动/缩放/优雅关闭）。这些 UIA 无法表达或需要真实指针操作。
4. **多显示器清单与有界 wait-for**：虚拟桌面/每屏几何与 DPI；按条件轮询等待（上限 30s），只返回观察结果。
5. **受限全局快捷键**：每次调用仅允许一个 chord，固定白名单，Win 组合与系统级组合拒绝，必须绑定当前前台窗口。
6. **剪贴板读取**：按 sensitive 处理，默认不读；严格截断。读取到的文本会作为工具结果返回给调用方并进入宿主/模型上下文，本 App 只保证它不进入日志或错误详情。**剪贴板写入未实现**：明文必须走 helper 命令行，本机任何进程都能读取。

## 工具（13 个）

| 工具名 | 风险档 | sessionPermission | 默认行为 |
|--------|--------|-------------------|----------|
| `desktop_windows_list` | 只读 | readOnly | 直接返回窗口列表 |
| `desktop_window_capture` | 观察 | 未标只读 | 截图并交付会话文件；wakeIfNeeded 会临时唤醒后复位 |
| `desktop_uia_tree` | 只读 | readOnly | 返回元素摘要 |
| `desktop_uia_find` | 只读 | readOnly | 返回匹配控件 |
| `desktop_hit_probe` | 只读 | readOnly | 返回坐标当下命中的顶层窗口 |
| `desktop_displays_list` | 只读 | readOnly | 返回虚拟桌面与每屏几何/DPI |
| `desktop_wait_for` | 只读 | readOnly | 有界轮询，只返回观察结果 |
| `desktop_capabilities` | 只读 | readOnly | 能力与控制状态回执 |
| `desktop_window_manage` | sensitive/destructive | 逐次审阅 | **默认 dryRun**，execute=true 且过闸才改窗口 |
| `desktop_mouse_drag` | destructive | 逐次审阅 | **默认 dryRun**，execute=true 且过闸才注入 |
| `desktop_mouse_wheel` | destructive | 逐次审阅 | **默认 dryRun**，execute=true 且过闸才注入 |
| `desktop_send_keys` | sensitive | 逐次审阅 | **默认 dryRun**，须绑定前台 hwnd+expectedPid |
| `desktop_clipboard_read` | sensitive | 逐次审阅 | **默认 dryRun**，读取截断、回执只给长度 |

## 安全姿态（多层，宿主审阅始终是最外层）

- 三个动作类工具都不声明 `readOnly`、不声明 `auto:"allow"` → 宿主对**每一次调用**都会走审阅。
- App 自己的 `allowRealInput` 开关默认**关闭**：关闭时动作类工具只返回预演计划，绝不移动系统指针或改动窗口。开启只是解除 App 内部这道闸，**不能**绕过宿主逐次审阅。
- `actionPolicy` 进一步限制可执行的风险档。
- 真实注入要求：新鲜的 `hwnd` + `expectedPid` 绑定 + 模型回填 `confirmation=I_UNDERSTAND_DESKTOP_INPUT`。该确认串是模型的知悉回执，**不是**用户批准，不替代宿主审阅。
- 原生 helper 默认 dryRun；`execute=true` 仅在上述全部门通过时才转发。拖拽/滚轮在注入前即时重跑 `WindowFromPoint` 命中守卫，起点坐标若当下不落在此 hwnd 即拒绝。
- 关闭为不可逆动作：强制显式 `hwnd`，拒绝 title/foreground 匹配；用 `WM_CLOSE`，应用仍可弹未保存提示、用户可取消。
- 截图的唤醒/复位用 `SetWindowPlacement` 精确恢复，不猜坐标；密码类控件不读取值；动作日志不落输入文本或截图内容。
- 键盘：固定键位白名单（字母/数字/F1-F12/导航键）+ 固定修饰键（ctrl/alt/shift）。**含 Win 修饰键一律拒绝**，另有系统级组合拒绝表（Win+L、Win+R、Alt+F4、Alt+Tab、Ctrl+Shift+Esc、Ctrl+Alt+Delete、Ctrl+Esc、Alt+Space 等）；每次调用限制为一个 chord。执行前校验绑定窗口确实是前台窗口并复核 PID，安全桌面下直接拒绝。
- 剪贴板：只有读取，按 sensitive、默认不读（默认 2000 / 上限 4000 字符）；回执只给字符数与是否截断，正文作为工具结果返回给调用方，**会进入宿主/模型上下文**。本 App 不把它写入日志或错误详情，但不宣称它不会出现在宿主的会话记录里。绑定的 HWND 只是审计锚点（剪贴板是进程全局的），不是投递目标。
- `actionPolicy` 默认档 `destructive-only` **不放行 sensitive**，所以快捷键与剪贴板读取即使开了 `allowRealInput` 也要显式选到 `sensitive-and-destructive`。
- **不做**：OCR（未内置引擎，也不接受任意图片路径）、应用启动、进程/注册表/文件系统清单、任意 shell 或 exe、批量动作计划、全屏截图、自由文本输入。`desktop_capabilities` 会如实回报这些未实现。

## 能力声明

- `app/tools.expose-to-model`：把工具暴露进模型循环。
- `app/process.spawn`：启动随包的原生 helper（这是“起进程时的闸”，改授权需重启 App 进程生效）。
- `app/session.stage-file`：把截图复制进会话并登记为产物文件。

三项默认关闭，须用户在安装审查或“设置 → 应用 → 应用能力”逐项授予。

## 部署前提

原生 helper 为 **.NET 8 Desktop / win-x64，framework-dependent**（约 207KB，需目标机装有 Microsoft.WindowsDesktop.App 8.0 运行时；本机 .NET 8 SDK 8.0.422 已满足）。这与两个旧插件一致，换取极小包体。

- 只支持 Windows x64；其他平台工具调用明确报错，无静默降级。
- Node 子进程默认无 `--allow-child-process`，必须授予 `app/process.spawn` 才能运行 helper；否则工具返回可行动的提示。

## 复现构建

```
dotnet build native-src/HanaDesktopCompanion.Helper.csproj -c Release
# 产物拷入 app/hana-desktop-companion/native/
node --test "tests/*.test.mjs"   # 纯逻辑测试，无副作用
```

## 来源与归因

合并自本机两个 v1 插件的不可替代能力，剥离其自建审批/会话/发光光标体系（v2 由宿主权限账本接管）：

- `desktop-orchestrator`（manifest 声明 MIT，作者 "Ganlin & ChatGPT"；仓库内无单独 LICENSE 文件）：UIA 只读树、per-monitor DPI 命中守卫、真实鼠标注入、窗口管理动词。
- `hana-window-capture`（MIT，版权人 zhigong0506，见其 LICENSE）：`PrintWindow(PW_RENDERFULLCONTENT)` 定向截图、PID/HWND 复用防护、隐藏/最小化显式唤醒与 placement 复位、空白证据拒绝、限速。

见 `LICENSE.upstream.md`。本 App 不含这两个插件的运行时路径依赖，也不读取它们的私有配置或数据。

## 验收状态

v0.1.1：已通过 [A]（经宿主工具通道调用已装 App，server 重启 + reseat 后）——8 工具全部注册，windows_list / uia_tree / uia_find / hit_probe 返回真实数据；window_capture 对自建 fixture 与被完全遮挡的非前台窗口都取到窗口自身像素，缩放 GDI+ 锁文件 bug 已修复并回归；真实注入端到端（用户开启 allowRealInput 后，仅对自建 fixture）move / minimize / restore / close 均有回读状态硬证据；默认安全门在 allowRealInput 关时拦下全部动作（零注入）。

v0.2.0：新增工具只做到**逻辑层 + 只读实测**：`list-displays` / `wait-for` / `capabilities` 已在真机跑通；`send-keys` 与 `clipboard-read` 只验证了默认阻断、参数校验、键位筛查与 dryRun 计划，**未做真实按键注入，也未真实读取剪贴板**。剪贴板写入未实现。尚未经宿主通道 [A] 验收。详见仓库 `docs/VALIDATION.md` §8。

已知边界（非缺陷）：

- mouse_wheel 未单独取到一次成功渲染。原因：Windows 前台锁不允许后台派生进程稳定
  置顶 fixture，坐标漂移时命中守卫正确拒绝盲注入。其注入栈与已验证的 drag 同构。
- 只支持 Windows x64；依赖 .NET 8 Windows Desktop 运行时（本机已具备）。
- OCR 未支持：需要识别文字时，把 `desktop_window_capture` 交付的会话图片交给宿主或外部 OCR。

市场安装/覆盖仍须宿主审批，本目录为源码与候选包。
