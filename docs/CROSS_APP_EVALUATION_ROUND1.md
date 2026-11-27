# 首轮真实跨软件样本自测报告（OBS Studio / Blender / ComfyUI Desktop）

> 测试日期：2026-11-27 · 代码基线：`v0.2.0-alpha.1` + 静态跨软件适配器增量 · 样本来源：公开文档与开源问题报告真实日志结构匿名化复现样本

---

## 1. 自测目标与底座验证边界

本轮自测在**不引入动态 DLL/脚本插件、不增加规则 DSL、不扫描全机应用**的前提下，检验 `GameCrash Kit` 通用信号底座仅靠少量静态文本适配器与固定报错词表，能否在冷启动（不按 `F5`）下稳定接住非游戏桌面图形/AI 软件的典型崩溃日志与正常负例日志。

**关键架构边界核验**：本次增量仅改动核心信号枚举（`LogSignalId.AccessViolation`）、静态解析适配器表（`EngineLogParser.cs`）与结构化信号摘要映射（`ShareReportProjector.cs`）；**WPF 主窗与预览视图模型、`WindowsScanCoordinator`、`MarkdownReportExporter`、`EmergencyCardPngRenderer` 以及已有 `ENGINE-GPU-LOST@2` / `ENGINE-OOM@2` / `EXCEPTION-AV@1` 规则代码均零改动**。

---

## 2. 六组真实格式样本盲测前后对照矩阵

所有夹具均植入对抗性虚构隐私数据（`SecretStreamer` / `SecretArtist` / `SecretDev`、私有用例路径、私有插件路径、提交哈希 `a9326cb1a8cb`、局域网 IP `192.168.1.55`、`--api-key=secret_comfy_key`、`token='obs_secret_tok'`）：

| 样本文件 | 软件与场景 | `v0.2.0-alpha.1` 基线表现（适配前） | 红队加固后实测表现（`v0.2.0-alpha.3`） | 隐私与只读 |
| :--- | :--- | :--- | :--- | :--- |
| `obs-d3d11-device-removed.txt` | OBS `30.2.3` `device_texture_create (887A0005)` + `Device Remove/Reset!` | 落回「通用文本日志 / 进程退出未证实」，未识别括号包裹 `(887A0005)` | 识别为 `OBS Studio 日志` / **`进程退出未证实`**（避免将重建提示误判为退出）/ 版本 `30.2.3` / `Direct3D 11`，触发 **`ENGINE-GPU-LOST@2`（有限线索）** + `LOG-ONLY-SUMMARY@1` | 零泄漏（含隐藏软件名）、哈希不变 |
| `obs-normal-session.txt` | **负例对照**：OBS 正常录屏起停，0 错误 / 0 泄漏 | 通用文本日志 / 未命中 | 识别为 `OBS Studio 日志` / `进程退出未证实` / **0 误报信号**，仅输出 `LOG-ONLY-SUMMARY@1（未命中已知标记）`，`HideAppName` 开关下摘要/标题/信号栏均零泄漏软件名 | 零泄漏、哈希不变 |
| `blender-4.2-av.crash.txt` | Blender `4.2.0` `EXCEPTION_ACCESS_VIOLATION` + Python 私有插件调用栈 | 通用文本日志 / 未提取 AV 信号 | 识别为 `Blender 崩溃日志` / **`Fatal`** / 版本 `4.2.0`（分享已剥离 Hash）/ 仅从 `ExceptionCode` 提取 **`AccessViolation`**（剔除 `Stack trace` 内误导串与 `# Python backtrace` 私有路径） | 零泄漏、哈希不变 |
| `blender-4.2-cycles-oom.log` | Blender `4.2.0` Cycles `CUDA error: Out of memory` + 共享内存耗尽 | 未命中特定 CUDA/Cycles 短语 | 识别为 `Blender 崩溃日志` / **`进程退出未证实`** / `OutOfMemory`，触发 **`ENGINE-OOM@2`（有限线索，不建议修改页面文件）** | 零泄漏、哈希不变 |
| `comfyui-desktop-cuda-oom.log` | ComfyUI Desktop `v0.4.2` `torch.OutOfMemoryError: CUDA out of memory` | 未命中 PyTorch CUDA OOM 短语 | 识别为 `ComfyUI 日志` / **`进程退出未证实`** / 版本 `0.4.2` / **`CUDA out of memory`**，触发 **`ENGINE-OOM@2`（有限线索）**；且严格隔离先前本机扫描的 `Environment` 与 `Kernel-Power 41` | 零泄漏、哈希不变 |
| `comfyui-desktop-python-av.log` | ComfyUI Desktop Electron 壳报错 `Python process exited with code 3221225477`（含带冒号变体） | 无法把十进制退出码 `3221225477` 对应为 `0xc0000005` | 识别为 `ComfyUI 日志` / **`子进程异常退出 (ChildProcessExit)`** / **`Python 子进程异常退出 0xc0000005 (exit code 3221225477)`**，**严格未误判为 OOM 或主进程退出** | 零泄漏、哈希不变 |

---

## 3. 发现与经验总结

1. **游戏引擎外真实软件的关键缺口主要集中在三类词表差异**：
   - D3D11 设备丢失常带括号十六进制码 `(887A0005)` 或重置提示 `Device Remove/Reset!`，未必出现完整的 `0x` 前缀。
   - GPU 计算/渲染软件（Blender Cycles、PyTorch/ComfyUI）多使用 `CUDA out of memory`、`torch.OutOfMemoryError` 与 `System is out of GPU and shared host memory`，补充静态词表即可直接复用现有 `ENGINE-OOM@2` 规则。
   - Electron/Python 子进程宿主日志常见 Windows 原生十进制退出码 `3221225477`（`0xc0000005`），通过受控映射表可精准识别为内存访问异常且与显存不足严格隔离。
2. **负例对照健康度良好**：OBS 正常录屏会话包含 `D3D11` 初始化与 `0 errors`，适配器未产生任何图形崩溃或异常误报。
3. **当前局限与下一步**：本轮使用公开格式匿名化夹具在冷启动 + 端到端 Markdown/900px PNG 导出流程跑通 `100%`；后续仍需在真机安装对应软件的最小公开复现现场下补充真实 Windows Event `1000` 联立样本，并推进普通标准账户首次下载 SmartScreen / 开源代码签名验证。
