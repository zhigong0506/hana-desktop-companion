# 上游归因与许可

本 App 的两个不可替代能力域合并自本机两个 v1 插件的既有实现思路，重写为单一闭合 helper，不依赖它们的运行时路径，也不读取其私有配置/数据。按各自声明的许可保留归属：

## desktop-orchestrator
- manifest 声明 `license: MIT`，`author.name: "Ganlin & ChatGPT"`。
- 仓库目录内未提供单独 LICENSE 文件，此处据其 manifest 声明记录。
- 借鉴点：UIA 只读元素树、per-monitor DPI-aware 的 `WindowFromPoint` 命中守卫、真实鼠标 `mouse_event` 注入路径、`ShowWindow`/`SetWindowPos`/`WM_CLOSE` 窗口管理动词。

## hana-window-capture
- MIT，版权人 `zhigong0506`（见其 LICENSE：Copyright (c) 2026 zhigong0506）。
- 借鉴点：`PrintWindow(PW_RENDERFULLCONTENT)` 单窗口定向截图、PID/HWND 复用防护、隐藏/最小化窗口的显式唤醒与 `SetWindowPlacement` 复位、空白证据拒绝、截图限速。

## 重写与差异
- 剥离了两插件自建的审批账本、控制会话（control-session）、发光光标 overlay 与视觉 API 路由：v2 下这些边界由宿主权限账本与逐次审阅接管。
- 新增：真实动作默认 dryRun、App 级 `allowRealInput` 默认关、`actionPolicy` 风险分档、注入前即时命中重检与 `finally` 释放。

两个上游的完整 MIT 许可条款以其原始文件为准。本 App 自身在 `manifest.json` 声明 `license: MIT`。
