# Hana Desktop Companion（桌面观察补充）

为 **HanaAgent v2** 桌面端开发的 Windows 桌面观察与受控动作补充 App，让 Agent 在对话中完成"定向截图取证 → UIA 只读诊断 → 高级动作"的闭环。它**补充**而非替代 Hana 原生 `computer` 工具：常规点击、输入、控件 Invoke 请继续优先使用 `computer`（宿主自带 lease 审批）；本 App 只做 `computer` 目前不能可靠覆盖的事。

英文版本：[README_EN.md](README_EN.md)　·　插件项目仓库：<https://github.com/zhigong0506/hana-desktop-companion>　·　OpenHanako 官方项目：<https://github.com/liliMozi/openhanako>

> ⚠️ 仅支持 Windows x64，且需要 **.NET 8 Windows Desktop Runtime x64**（与 [hana-window-capture](https://github.com/zhigong0506/hana-window-capture) 相同依赖）。

## 主要能力

- **被遮挡窗口的定向截图**：`PrintWindow(PW_RENDERFULLCONTENT)` 按 HWND 采集单个窗口自身像素，目标被完全盖住、不在前台也能取到；绝不静默回退全屏截图。
- **PID/HWND 绑定校验**：截图与动作都要求新鲜句柄 + 期望 PID，系统复用句柄时明确拒绝，防止截错/操作错窗口。
- **UIA 只读诊断**：元素树与关键词查找（角色/名称/className/pattern/边界），用于原生通道读不到语义时的补充观察；只读、零注入。
- **per-monitor DPI 命中测试**：给定物理像素，返回该点 Z 序最顶的顶层窗口，高级动作前先确认"这一像素现在落在谁身上"。
- **受控高级动作**：拖拽、滚轮（UIA 无法表达的语义）与窗口管理（移动/缩放/最小化/最大化/还原/优雅关闭 `WM_CLOSE`）。**全部默认 dryRun**，须 `execute=true` 且通过多重闸门才实际注入。
- **截图作为会话文件交付**：PNG 复制进会话登记为产物，供模型理解和用户留档。

## 运行环境与兼容性

缺少任意"必须"条件时插件无法正常工作。

| 项目 | 要求 | 支持状态 |
|---|---|---|
| Hana 宿主 | HanaAgent `1.0.6-beta` 或更高（v2 App 体系） | 必须；低版本 manifest 标记不兼容 |
| 操作系统 | Windows 10/11 x64 | 正式支持 |
| 进程架构 | x64 / AMD64 | 必须 |
| .NET 运行时 | .NET 8 **Windows Desktop Runtime x64** | 必须；普通 .NET Runtime / ASP.NET Core Runtime 不能替代 |
| 能力授予 | `app/process.spawn` | 必须；helper 是随包外部进程，未授予时工具返回可行动提示。该闸在 App 进程**启动时**生效，改授权需重载/重启 App |
| 能力授予 | `app/tools.expose-to-model` | 必须；否则工具不进模型循环 |
| 能力授予 | `app/session.stage-file` | 需要截图交付时开启 |
| 对话模型 | 支持工具调用 | 必须 |
| 图片理解 | 模型支持图片输入 | 强烈建议；缺失时仍可产出 PNG，但 Agent 可能无法直接理解 |
| 会话状态 | 安装或授权变更后新建会话 | 强烈建议；旧会话可能保留旧工具清单快照 |

## 安装前自检

在仓库根目录运行：

```powershell
powershell -ExecutionPolicy Bypass -File .\scripts\check-environment.ps1
```

输出 `ok=true` 且 `windows / x64Process / dotnet8WindowsDesktopRuntime / nativeHelperExists / nativeHelperSmokeTest` 全部为 `true` 即可安装。也可用 `dotnet --list-runtimes` 确认存在 `Microsoft.WindowsDesktop.App 8.0.x`。

## 工具（8 个）

| 工具 | 风险档 | 默认行为 |
|---|---|---|
| `desktop_windows_list` | 只读（readOnly） | 返回窗口列表 |
| `desktop_window_capture` | 观察（wake 可短暂显露隐藏窗口，故未标只读） | 截图并交付会话文件 |
| `desktop_uia_tree` / `desktop_uia_find` / `desktop_hit_probe` | 只读（readOnly） | 返回诊断数据，零注入 |
| `desktop_window_manage` | sensitive / close 为不可逆 | **默认 dryRun**；`execute=true` + 全闸通过才改动窗口 |
| `desktop_mouse_drag` / `desktop_mouse_wheel` | destructive | **默认 dryRun**；同上 |

## 安全与隐私边界（多层防御，宿主逐次审阅始终是最外层）

- 三个动作类工具不声明 `readOnly`、不声明 `auto:"allow"`——宿主对**每一次调用**走审阅。
- App 自有开关 `allowRealInput` **默认关闭**：关闭时动作类工具只返回预演计划，绝不移动系统指针或改动窗口。开启它只解除 App 内部这道闸，不能绕过宿主审阅。
- 模型回填的确认串只是"知悉回执"，**不构成用户批准**。
- 真实注入要求新鲜的 `hwnd` + `expectedPid` 绑定，并在注入前即时重跑 `WindowFromPoint`；焦点漂移、坐标不再命中目标窗口时拒绝（实测有效）。
- `close` 强制显式 `hwnd`，拒绝标题/前台匹配；用 `WM_CLOSE` 优雅关闭，应用仍可弹未保存提示、用户可取消。
- 截图唤醒/复位用 `SetWindowPlacement` 精确恢复原状态；不截取整个桌面；密码控件值不读取；动作日志不记录输入文本与截图内容。
- 无任意命令执行：helper 只接受固定动词白名单；无 shell、无脚本、无后台定时截图、不请求管理员权限、不绕过 UAC / 安全桌面 / DRM。

## 支持范围与已知限制

### 正式支持

- Windows 10/11 x64 的 Win32 / Electron / Chromium / WebView2 顶层窗口观察与截图。
- 被其他窗口遮挡的可见目标窗口截图。
- 最小化/隐藏窗口的显式唤醒（`wakeIfNeeded=true`）截图与 placement 复位。
- 拖拽、滚轮、窗口管理（默认 dryRun，逐次审阅）。

### 条件支持

- GPU 加速窗口：取决于应用是否响应 `PrintWindow`，可能得到旧帧或黑图（黑图会被证据检测拒绝交付）。
- 高完整性/管理员窗口：普通权限进程无法读取，本 App 不自动提权。

### 明确不支持

- macOS、Linux、Windows x86 / ARM64（未完成验证）。
- UAC 安全桌面、锁屏及其他 secure desktop 内容。
- DRM/HDCP 受保护画面与主动禁止截图的程序。
- 全屏/无明确目标的截图；后台常驻自动观察或定时截图。

### 验证中的诚实标注

drag 的"可见拖拽效果"与 wheel 的"一次成功渲染"未在生产会话中单独取证：Windows 前台锁不允许测试进程稳定把 fixture 置顶，命中守卫因此在坐标漂移时正确拒绝了盲注入（这是设计行为，非缺陷）。二者的注入栈与已取证的窗口动作同属同一 helper 的 Win32 路径，并在逻辑层测试中通过。详见 [docs/VALIDATION.md](docs/VALIDATION.md)。

## 安装

1. 确认满足上方兼容矩阵（尤其 Windows x64 与 .NET 8 Windows Desktop Runtime x64）。
2. 从 Releases 页（本仓库右侧 Releases，建好后为 `https://github.com/zhigong0506/hana-desktop-companion/releases`）下载 `app-hana-desktop-companion-<版本>.zip`（或直接用仓库 `dist/` 内的同名包）。
3. 打开 Hana 桌面端 → 应用（Apps）→ 本地安装，选择该 ZIP。安装审查会逐项展示上面三个能力词，请确认后授予。
4. 授予 `app/process.spawn` 后需重启 App 进程生效（详情页 Reload 即可）。
5. 建议**新建会话**使用，让工具清单刷新。

也可直接把 `app/hana-desktop-companion/` 目录放入 `<HANA_HOME>/apps/` 后在详情页重新加载（本地目录安装支持 Reload）。

## 从源码构建

要求：Windows x64、.NET 8 SDK、Node.js（仅用于 Hana 官方 App 工具链，无需 npm install）。

```powershell
# 1) 编译原生 helper（默认只读观察 + dryRun；无任意命令）
dotnet build native-src/HanaDesktopCompanion.Helper.csproj -c Release
# 产物在 native-src/bin/Release/net8.0-windows/win-x64/
#   hana-desktop-companion-helper.{exe,dll,deps.json,runtimeconfig.json}
# 拷入 app/hana-desktop-companion/native/

# 2) 官方校验与打包（需要 Hana 安装版里的 hana-app-creator 工具链）
#    validate_app.mjs --dir app/hana-desktop-companion  →  pack_app.mjs
# 产物即 dist/ 中的安装 ZIP + entry.json
```

## 项目结构

```text
hana-desktop-companion/
├─ README.md / README_EN.md
├─ LICENSE / CHANGELOG.md
├─ scripts/check-environment.ps1
├─ app/hana-desktop-companion/        # 可直接安装的 v2 App
│  ├─ manifest.json                   # 能力与设置页 schema 声明
│  ├─ index.js                        # 8 个工具注册 + 动作门流程
│  ├─ lib/helper.js                   # 固定动词白名单的 helper 桥
│  ├─ lib/gates.js                    # App 级真实输入门（默认关闭）
│  ├─ native/                         # 随包编译产物（提交入库，免构建安装）
│  ├─ sdk/                            # 随包 Hana App SDK（官方产物）
│  ├─ assets/icon.svg
│  └─ LICENSE.upstream.md             # 两枚上游插件的 MIT 归因
├─ native-src/                        # C# helper 源码（Program.cs + csproj）
├─ dist/                              # 官方 pack 产出的安装 ZIP + entry.json
└─ docs/VALIDATION.md                 # 验收报告（含诚实 caveat）
```

仓库与安装包不包含 `bin/`、`obj/`、测试夹具、运行期截图或个人配置。

## 验证结论摘要

v0.1.1 在 Hana 1.0.6-beta / Windows x64 完成安装后端到端验收：观察、遮挡窗口截图（含缩放 GDI 修复回归）、UIA 诊断、默认安全门拦截、真实 `move`/`minimize`/`restore`/`close` 均有回读或截图硬证据；drag/wheel 的可见成功未单独取证（原因见上节）。官方 `validate/smoke/pack/archive` 全部 0 error 0 warning。详见 [docs/VALIDATION.md](docs/VALIDATION.md)。

## 来源与归因

本 App 由两枚社区 v1 插件的不可替代技术**重写合并**而成，并剥离其自建审批账本/控制会话/发光光标体系（v2 中这些边界由宿主权限账本接管）；对上游目录无运行时依赖，也不读取其私有配置或数据：

- desktop-orchestrator（MIT，作者 "Ganlin & ChatGPT"，本机构区插件，未公开发布）：UIA 只读树、DPI-aware `WindowFromPoint` 命中守卫、`mouse_event` 注入、`ShowWindow/SetWindowPos/WM_CLOSE`。
- [hana-window-capture](https://github.com/zhigong0506/hana-window-capture)（MIT，© 2026 zhigong0506）：`PrintWindow(PW_RENDERFULLCONTENT)` 定向截图、PID/HWND 复用防护、显式唤醒与 `SetWindowPlacement` 复位、空白证据拒绝。

完整条款见各上游仓库；本 App 同样采用 MIT，见 [LICENSE](LICENSE)。

## 许可证

MIT License。

## 说明

本项目是社区插件，与 OpenHanako 官方项目相互独立；OpenHanako/HanaAgent 的名称和项目归属以官方仓库为准：<https://github.com/liliMozi/openhanako>。本仓库同样不上传任何测试截图、个人窗口图像或凭据。
