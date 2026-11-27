# 首次运行可信度、SmartScreen 实测与开源代码签名指南

> 实测日期：2026-11-27 · 测试目标：GitHub Releases 公开资产 [`v0.2.0-alpha.1`](https://github.com/Z1FU114514/GameCrashKit/releases/tag/v0.2.0-alpha.1)（`GameCrashKit-v0.2.0-alpha.1-win-x64.zip`）· 自动化探测脚本：[scripts/Test-FirstRunTrust.ps1](../scripts/Test-FirstRunTrust.ps1)

---

## 一、实测环境与结果对比（非提权 Win11 宿主机 & 隔离沙盒）

为验证普通玩家从浏览器下载未签名便携包后，在不同解压方式、Mark-of-the-Web（`Zone.Identifier` NTFS 备用数据流）传播与 `ShellExecute` 启动下的真实表现，我们在两组真实环境中运行了相同的可复现探测脚本：

| 维度 / 观察项 | 环境 A：非提权 Win11 25H2 宿主机（`zh-CN`） | 环境 B：干净隔离 Win11 24H2 沙盒（`en-US`） |
| :--- | :--- | :--- |
| **系统与权限上下文** | Windows 11 专业版 `Build 26200` · `IsAdmin=False` | Windows 11 企业版 `Build 26100` · `WDAGUtilityAccount`（`IsAdmin=True` · 无预装 `.NET 10`） |
| **SmartScreen 注册表策略** | `SmartScreenEnabled=Off` / `EnableWebContentEvaluation=0` | 四项策略均 `<unset>`（Sandbox 默认未配且受宿主机 `127.0.0.1:7890` 代理继承阻断直连） |
| **云端资产与校验一致性** | 直接从 GitHub Releases 下载 `65,688,112` 字节 ZIP，与云端 `.SHA256SUMS.txt`（`ea1b95af86289e6cb9592d94a55873e2505d98b04abbacf8ce7fb19df11f96a1`）**100% 匹配** | 同份云端下载包，校验 **100% 匹配** |
| **Authenticode 签名状态** | `NotSigned`（无签名证书） | `NotSigned`（无签名证书） |
| **资源管理器解压（`Shell.Application` / 全部提取）** | **410 / 410 文件就绪**，`GameCrashKit.App.exe` 与 `GameCrashKit.Core.dll` **均继承 `ZoneId=3`** | **410 / 410 文件就绪**，`GameCrashKit.App.exe` 与 `GameCrashKit.Core.dll` **均继承 `ZoneId=3`** |
| **PowerShell `Expand-Archive` 解压** | 410 / 410 文件就绪，**`Zone.Identifier` 完全丢失（`NONE`）** | 410 / 410 文件就绪，**`Zone.Identifier` 完全丢失（`NONE`）** |
| **带 MotW 通过 `ShellExecute` 首次启动** | `smartscreen.exe` 进程拉起（`CountDuring=1`），**成功阻塞挂起 `ShellExecuteEx`（`ShellBlockedPending=True` / 进程数 `0`）** | `smartscreen.exe` 进程拉起（`CountDuring=1`），因容器未启拦截策略而直接放行至主窗口 |
| **`Unblock-File` 解除锁定后启动** | `Zone.Identifier = NONE`；**零 UAC 直接启动并显示主窗口（`MainWindowReady=True`）** | `Zone.Identifier = NONE`；**零 UAC 直接启动并显示主窗口（`MainWindowReady=True`）** |

### 实测结论与玩家建议

1. **真实消费者默认体验确认会被 SmartScreen 拦截一次**：只要用户通过浏览器下载后使用 Windows 资源管理器右键「全部提取」，所有解压出来的二进制文件均会继承 `ZoneId=3`（Mark of the Web）。在尚未购买商业证书或接入开源代码签名前，即使本地部分注册表开关为关，Windows Defender SmartScreen 服务仍会拦截未签名 `.exe`。
2. **两条零门槛、安全的自助放行路径**：
   - **方法 1（推荐）**：核对 `.SHA256SUMS.txt` 后，右键刚下载的 `.zip` 文件 → **「属性」** → 勾选底部的 **「解除锁定 (Unblock)」** → 点确定后再解压运行，可避免对 400+ 解压文件逐个放行。
   - **方法 2**：若已直接解压，双击后看到蓝底提示时点击 **「更多信息」→「仍要运行」** 即可（全程无需关闭 Windows 安全中心或以管理员运行）。

---

## 二、Windows 10 22H2 标准账户与跨语言复验手册

> **诚实边界声明**：当前开发宿主机为 Windows 11 25H2，内置沙盒镜像为 Windows 11 24H2（Build `26100`），本机未安装 Windows 10 22H2 虚拟机，因此 [SUPPORT_MATRIX.md](SUPPORT_MATRIX.md) 中 **Windows 10 22H2 仍严格保持「待验证」状态**。

若在 Windows 10 22H2（建议普通非管理员标准账户，且开启默认 Windows Defender SmartScreen）实机或 VM 中完成首发可信度验证，只需执行仓库自带的单行自测命令（Windows PowerShell 5.1 直接可用）：

```powershell
powershell -ExecutionPolicy Bypass -File scripts\Test-FirstRunTrust.ps1 -VersionLabel "v0.2.0-alpha.1"
```

验收检查点：
1. 桌面生成 `GCK-TrustOutput\firstrun-trust-report.txt`，记录 OS 构建号、`IsAdmin=False`、`SmartScreenEnabled` 实值及云端下载 SHA-256 `Match=True`。
2. 人工双击 `extracted-explorer\GameCrashKit.App.exe` 验证 Win10 蓝屏「Windows 已保护你的电脑 → 更多信息 → 仍要运行」流程可顺利到达主窗口，随后采集该机真实 `Event 1000 / 1001 / 1002` XML 补入夹具库后，方可将该行升级为「已实测支持」。

---

## 三、SignPath Foundation 免费开源代码签名准入核对清单

为从根本上消除初次运行“未知发布者”拦截，**SignPath Foundation** 为符合条件的开源项目提供免费的开源 Authenticode 代码签名（签署主体为 SignPath Foundation）。当前仓库对照状态如下：

### 3.1 仓库准入条件自查表

| 准入条件 | 当前状态 | 仓库依据 |
| :--- | :--- | :--- |
| 纯正 OSI 开源许可且无商业双授权/闭源组件 | **已满足** | 根目录 `MIT OR Apache-2.0` 双重开源协议，无闭源或商业二元组件 |
| 公开可访问代码仓与公开 Release 发行物 | **已满足** | 公开 GitHub 仓库 + Releases 已稳定产出 ZIP 与校验文件 |
| 严格非恶意软件 / 无漏洞利用工具特征 | **已满足** | `asInvoker` 纯只读本地事后取证，`Core`/`Windows`/`App` 零网络与零进程注入 |
| 全程使用 GitHub 托管 Runner 构建（非自托管构建机） | **已满足** | [.github/workflows/ci.yml](../.github/workflows/ci.yml) 与 [.github/workflows/release.yml](../.github/workflows/release.yml) 均限定 `runs-on: windows-latest` |
| 安全政策与贡献规范文档就位 | **已补齐** | 新增 [SECURITY.md](../SECURITY.md)（含漏洞响应渠道与签名政策）与 [CONTRIBUTING.md](../CONTRIBUTING.md) |
| GitHub 分支保护规则集（`main` 强制 PR 与 `CI / build-and-test` 绿灯） | **待在 GitHub 网页后台开启** | GitHub Repo `Settings -> Rules -> Rulesets`（仅仓主权限可配置） |
| SignPath Foundation 申请审批与 `SIGNPATH_API_TOKEN` | **待仓主提交申请** | 安装 SignPath GitHub App，配置 `v*` Tag 发布签名项目与 Artifact Definition |

### 3.2 待仓主在 GitHub 后台与 SignPath 控制台执行的手动步骤

1. 在 GitHub 仓库 `Settings -> Rules -> Rulesets` 为 `main` 分支开启保护：要求 Pull Request 合并且必过状态检查 `build-and-test`；为 `refs/tags/v*` 开启防删改写保护。
2. 前往 SignPath Foundation 官网提交开源项目免费签名申请（附上仓库地址、`MIT OR Apache-2.0` 协议、已建立的 `SECURITY.md` 与 `v0.2.0-alpha.1` Release 链接）。
3. 审批通过后：在 GitHub 安装官方 SignPath App，并将组织 ID / 项目 Slug / `SIGNPATH_API_TOKEN` 写入仓库 Secrets，随后在 `.github/workflows/release.yml` 于 `Compress-Archive` 前接入 `signpath/github-action-submit-signing-request` 对 `GameCrashKit.App.exe` 与自有 DLL 进行签名并重新计算 `.SHA256SUMS.txt`。
