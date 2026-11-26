# M3 性能基线与 `v0.1-alpha` 便携包实测报告

> **记录日期**：2026-11-26  
> **测量环境**：Windows 11 25H2 x64（`Build 26200.9457`, `zh-CN`），普通非管理员权限（`IsAdmin=False`），`.NET 10.0.301`

---

## 1. 20 次重复扫描性能基线（默认 2 小时窗口）

通过 `WindowsScanCoordinator.ScanAsync(ScanWindowPreset.Last2Hours)` 在主机连续执行 **20 次**完整扫描（并行读取 `Application` 1000/1001/1002、`System` Kernel-Power 41 与 7 项中性环境事实探针）：

| 指标 | 实测耗时 | 目标门槛（4C/8G/SSD VM） | 状态 |
| :--- | :--- | :--- | :--- |
| **Min** | `26.8 ms` | — | — |
| **P50** | `28.0 ms` | — | — |
| **P95** | `35.0 ms` | `≤ 5,000 ms` | 本机远低于阈值（待 M4 隔离 VM 复测） |
| **Max** | `54.4 ms` | — | — |

## 2. PNG 急诊单离屏渲染实测

使用 `EmergencyCardPngRenderer.RenderToPngBytes`（纯 WPF `RenderTargetBitmap`，强制锁定 `900 px` 逻辑宽度与 `96 DPI`）在本机 7 天窗口真实事故样本上实测：
* **渲染耗时**：`437.0 ms`（STA 首帧冷渲染；单元测试含测试夹具与解码校验 `877 ms`）
* **输出尺寸**：`900 × 777 px`（`96 DPI`）
* **文件体积**：`69.0 KB`

## 3. `v0.1-alpha` 自包含绿色便携包构建产物

* **构建命令**：
  `dotnet publish src/GameCrashKit.App/GameCrashKit.App.csproj -c Release -r win-x64 --self-contained true -p:PublishTrimmed=false -o publish/GameCrashKit-v0.1-alpha-win-x64`
* **便携包附件**：已随包附带 `LICENSE-MIT`、`LICENSE-APACHE`、`README.md`、`PRIVACY.md`、`SUPPORT_MATRIX.md`
* **压缩产物**：`publish/GameCrashKit-v0.1-alpha-win-x64.zip`（`62.32 MB`）
* **SHA-256**：`06bd5c3fed34ac8a6d443dfc75ff7c32afcb890979836daed2b60b3670da41fc`

> 说明：`publish/` 目录已在 `.gitignore` 中忽略，不纳入 Git 源码仓库跟踪。

## 4. 移交 M4 阶段的正式发版待验项（AC-01 ~ AC-08）

* [ ] 在无预装 `.NET` 的干净断网 **Windows 10 22H2** 与 **Windows 11 x64**（中/英语言，4C/8G/SSD）虚拟机完成解压启动、ProcMon 零写零出站 I/O 审计及 20 次扫描 P95 复测。
* [ ] 在真实系统 100% / 125% / 150% / 200% DPI 缩放环境及真机微信 / QQ / 贴吧 / NGA 压缩回读测试 PNG 急诊单字迹可读性。
* [ ] 招募 `≥ 10` 名真实玩家覆盖普通闪退、Unity/Unreal 引擎日志与证据不足三类场景完成盲测并修复阻断问题。
