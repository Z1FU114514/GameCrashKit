# GameCrash Kit（游戏闪退急诊室）

面向 Windows PC 玩家与轻度 Mod 玩家的**开源、纯本地、零提权、只读式事后游戏闪退取证与脱敏急诊单工具**。

> **当前版本**：`v0.2.0-alpha.1`（公开预览版 · 绿色免安装，纯本地只读运行）。详见 [更新日志](docs/CHANGELOG.md) 与 [支持范围](docs/SUPPORT_MATRIX.md)。

## 核心功能与设计亮点

* **闪退后 30 秒事后取证（支持冷启动仅日志独立诊断）**：无需常驻后台。支持 `F5` 扫描近 `2 小时`（可切 `24 小时` / `7 天`）Windows 应用崩溃（1000）、无响应卡死（1002）、WER 报告（1001）与系统异常关机背景（Kernel-Power 41），疑似游戏路径自动置顶；**未按 `F5` 冷启动时**也可直接拖入或 `Ctrl + O` 打开单份日志文件立即生成独立急诊单，已有候选事故时支持在「独立查看」与「关联当前事故」之间切换。
* **诚实双刻度与单次分享信息完备**：独立标注「归属强度」与「证据强度」，区分致命崩溃 / Unreal 非致命 `Ensure` / 未知状态；急诊单同步附带 Windows 扫描时间窗、过滤模式与采集受限/截断边界说明，不虚构根因或诱导危险修复操作。
* **7 项中性环境事实快照**：只读核验显卡驱动包日期、目标路径非 ASCII/超长特征、OneDrive 文档同步（探测前先校验本地固定盘，不触碰远程重定向共享）、x64/x86 VC++ 运行库、当前已分配页面文件总量、扫描瞬间运行中的常见覆盖层与系统区域/HVCI 状态，仅作客观参照、不越权归因。
* **通用日志信号与引擎日志安全读取**：支持 Unity `Player.log`、Unreal `CrashContext.runtime-xml` 与普通 `.log`/`.txt`（末尾 2 MiB 只读沙箱，支持 UTF-8 / UTF-16 BOM，入口优先校验重解析点并直接拒绝 `.dmp`/`.mdmp`/`.hdmp`、截断 XML、UNC 路径与符号链接/目录联接）。
* **所见即所得两层脱敏急诊单**：默认仅导出严格字段白名单与安全“安装来源”分类（绝不导出私人目录名、原始异常细节或原始日志正文），支持一键隐藏程序名（含同名故障模块）/精确时间戳，或勾选附带**固定结构化日志信号摘要**，同时导出**浅底高对比 900px PNG 急诊长图**与 **Markdown 纯文本**（复制自带禁用云剪贴板/历史标记，保存通过文件句柄唯一标识禁止覆盖当前源日志）。

## 安全与隐私铁律

* **零提权（`asInvoker`）**：永不弹窗索要管理员权限，不访问受限 `C:\Windows\Minidump` 或读取 `.dmp` 内存转储。
* **反作弊零侵入**：严禁调用 `OpenProcess`、`ReadProcessMemory`、`EnumProcessModules` 或附加调试器（已纳入自动化静态审计）。
* **100% 只读、零网络、零持久化**：无网络出站、无遥测、无自动更新、无本地历史数据库，关闭窗口即释放全部内存。详见 [隐私规范](docs/PRIVACY.md)。

## 下载与免安装使用

1. 前往仓库 **Releases** 页面下载便携版压缩包（`GameCrashKit-v0.2.0-alpha.1-win-x64.zip`）与校验文件 `SHA256SUMS.txt`；程序已自包含运行环境，无需预装 .NET。
2. 建议先核对 ZIP 文件的 SHA-256 校验值，解压到任意本地目录后直接双击 `GameCrashKit.App.exe` 运行（无需安装、不弹出 UAC）。
3. **关于 Windows SmartScreen 提示**：`v0.2.0-alpha.1` 暂未购买商业代码签名证书，部分机器初次运行时可能看到“未知发布者”拦截提示；核对官方 SHA-256 无误后点「更多信息 → 仍要运行」即可，无需关闭系统防护软件。

## 快捷键

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
pwsh -File scripts/Publish-Portable.ps1 -VersionLabel "v0.2.0-alpha.1"

# 运行自动化发布检查（含只读哈希不变校验与扫描耗时基准）
pwsh -File scripts/Verify-ReleaseGate.ps1 -PortableDir "publish\GameCrashKit-v0.2.0-alpha.1-win-x64"
```

## 相关文档

* [docs/PRIVACY.md](docs/PRIVACY.md) — 隐私保护与脱敏白名单说明
* [docs/SUPPORT_MATRIX.md](docs/SUPPORT_MATRIX.md) — 支持的系统环境与崩溃日志格式
* [docs/CHANGELOG.md](docs/CHANGELOG.md) — 版本更新日志

## 开源协议

本项目采用 **`MIT OR Apache-2.0`** 双重许可，可任选 [LICENSE-MIT](LICENSE-MIT) 或 [LICENSE-APACHE](LICENSE-APACHE) 之一遵守。
