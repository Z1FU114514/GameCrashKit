# GameCrash Kit（游戏闪退急诊室）

面向中文 Windows PC 玩家与轻度 Mod 玩家的**开源、纯本地、零提权、只读式事后游戏闪退取证与脱敏急诊单工具**。

> **当前版本**：`v0.1.0-alpha.1`（M0–M4 工程与自动化验收闭环已建成，处于隔离 VM 与真实玩家盲测准入阶段）。详见 [规格书](docs/SPEC_v0.1.md) 与 [更新日志](docs/CHANGELOG.md)。

## 核心功能与设计亮点

* **闪退后 30 秒事后取证**：无需常驻后台。自动扫描近 `2 小时`（可切 `24 小时` / `7 天`）Windows 应用崩溃（1000）、无响应卡死（1002）、WER 报告（1001）与系统异常关机背景（Kernel-Power 41），疑似游戏路径自动置顶。
* **诚实双刻度诊断**：独立标注「归属强度」与「证据强度」，证据不足时坦白告知已查与缺失范围，绝不强行编造根因或诱导危险修复操作。
* **7 项中性中文环境事实快照**：只读核验显卡驱动发布日期、目标路径非 ASCII/超长特征、OneDrive 文档同步、x64/x86 VC++ 运行库、页面文件总量、当前运行中的常见覆盖层与系统区域/HVCI 状态，仅作客观参照、不越权归因。
* **Unity / Unreal 崩溃日志安全读取**：支持窗口内候选确认关联及直接拖入单个 `.log` / `.txt` / `.xml` 文件（末尾 2 MiB 只读沙箱，阻断 UNC 与符号链接/目录联接）。
* **所见即所得脱敏急诊单**：默认严格字段白名单投影，支持一键隐藏程序名/精确时间或预览后附带受限脱敏摘要，同时导出**白底高对比 900px PNG 急诊长图**与 **Markdown 纯文本**。

## 安全与隐私铁律

* **零提权（`asInvoker`）**：永不弹窗索要管理员权限，不访问受限 `C:\Windows\Minidump` 或读取 `.dmp` 内存转储。
* **反作弊零侵入**：严禁调用 `OpenProcess`、`ReadProcessMemory`、`EnumProcessModules` 或附加调试器（已纳入自动化静态审计）。
* **100% 只读、零网络、零持久化**：无网络出站、无遥测、无自动更新、无本地历史数据库，关闭窗口即释放全部内存。详见 [隐私规范](docs/PRIVACY.md)。

## 快捷键与使用

* `F5`：开始只读诊断扫描
* `Esc`：取消正在进行的扫描（或在预览窗中关闭预览）
* `Ctrl + O`：手选单个游戏崩溃日志文件（也支持直接拖入窗口）
* `Ctrl + E`：打开所见即所得 PNG / Markdown 急诊单预览与导出

## 构建、验证与便携包打包

前置条件：`.NET 10 SDK`（Windows x64）。

```powershell
# 编译与运行全部单元测试、主机只读测试、安全审计与 UI 布局测试
dotnet build GameCrashKit.sln -c Release
dotnet test GameCrashKit.sln -c Release --no-build

# 一键构建绿色便携版 ZIP 与 SHA-256 校验文件
pwsh -File scripts/Publish-Portable.ps1 -VersionLabel "v0.1.0-alpha.1"

# 运行 M4 自动化发布门禁检查（含只读哈希不变校验与 20 次扫描 P95 基准）
pwsh -File scripts/Verify-ReleaseGate.ps1 -PortableDir "publish\GameCrashKit-v0.1.0-alpha.1-win-x64"
```

## 文档索引

* [docs/SPEC_v0.1.md](docs/SPEC_v0.1.md) — v0.1 最终项目方案与工程立项规格书
* [docs/PRIVACY.md](docs/PRIVACY.md) — 双层隐私保护与白名单字段清单
* [docs/M0_EVIDENCE_MATRIX.md](docs/M0_EVIDENCE_MATRIX.md) — 主机事件 XML 实测字段矩阵与避坑记录
* [docs/SUPPORT_MATRIX.md](docs/SUPPORT_MATRIX.md) — 系统、语言与来源实测准入矩阵
* [docs/M4_ACCEPTANCE_CHECKLIST.md](docs/M4_ACCEPTANCE_CHECKLIST.md) — AC-01~AC-08 验收门槛与隔离 VM 测试手册
* [docs/USER_TRIAL_GUIDE.md](docs/USER_TRIAL_GUIDE.md) — ≥10 位真实玩家盲测试用指南与反馈表
* [docs/CHANGELOG.md](docs/CHANGELOG.md) — 版本更新日志

## 开源协议

本项目采用 **`MIT OR Apache-2.0`** 双重许可，可任选 [LICENSE-MIT](LICENSE-MIT) 或 [LICENSE-APACHE](LICENSE-APACHE) 之一遵守。
