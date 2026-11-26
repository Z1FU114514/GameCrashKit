# M4 发布验收清单与隔离 VM 实测指南（AC-01 ~ AC-08）

> **诚实声明**：本清单严格区分「已在主机/自动化测试通过的门禁」与「仍需在干净隔离虚拟机或真人试用阶段物理完成的门禁」。在所有 `[ ]` 手工门禁全部验证通过前，正式版标签 `v0.1.0` 保持锁定，先以 `v0.1.0-alpha.1` 供内部与受邀试用。

---

## 1. AC-01 ~ AC-08 总门禁状态矩阵

| 门槛 | 自动化 / 本机已验状态 | 尚待干净 VM / 人工完成项 |
| :--- | :--- | :--- |
| **AC-01 普通玩家闭环** | `[x]` 主机非管理员 `Event 1000/1001/1002` 与 `UNKNOWN@1` 规则闭环已验证 | `[ ]` 在隔离 VM 运行 `SyntheticCrashTool.exe --mode av --confirm-crash` 实测捕获真实新崩溃，并收集 `>= 2` 组真实游戏故障样本验证 |
| **AC-02 归属、联结与去重** | `[x]` `IncidentCorrelatorTests` 13 项表驱动单测全过（ReportId 直连、路径/PID+创建时间可能联结、PID 复用防误并、同名异路径、迟到 WER、多候选歧义、跨频道同 RecordId） | `[ ]` 核对真实游戏产生 `1000+1001` 延迟抵达时的实际界面表现 |
| **AC-03 零越权误归因** | `[x]` `DiagnosisRuleEngineTests` 全过（10 条规则正/反例、邻近 4101 不判驱动根因、孤立 KP41 不生成候选、System32 `dinput8.dll` 负例、`0xc0000005` 诚实未知、零 DDU/关 HVCI 引导） | `[ ]` 试用反馈复核无误导性文案 |
| **AC-04 引擎日志与文件安全** | `[x]` 2.5 MiB 中文大文件末尾 2 MiB 截断、无效 UTF-8、UNC/设备路径、`mklink /J` NTFS 目录联接、独占锁/缺失文件降级与 `<PCallStack>` 排除全过 | `[ ]` 补齐真实 Unity 与 Unreal 完整崩溃日志样本归档 |
| **AC-05 性能、超时与交互韧性** | `[x]` 本机 20 次默认 2 小时扫描 `P95 = 35.0 ms`（远小于 `5,000 ms`），`860×600` 最小窗口无遮挡与 1/2/3 标题对齐自动化验证通过 | `[ ]` 在 **4 逻辑核 / 8 GB / SSD** 干净 VM 执行 `scripts\Verify-ReleaseGate.ps1` 确认 P95 ≤ 5 秒 |
| **AC-06 输出、隐私与只读审计** | `[x]` 白名单防泄漏与 Opt-in 脱敏单测、源文件读取前后 SHA-256 与修改时间不变测试、`src/` 零网络出站 API 与 `Core/Windows` 零写操作/写注册表静态审计全过 | `[ ]` 在 100%/125%/150%/200% DPI 缩放及微信/QQ/NGA/贴吧实测 PNG 急诊单压缩回读清晰度；ProcMon 进程级抓包审计 |
| **AC-07 双系统纯净便携与可复核性** | `[x]` 固定输入确定性一致输出、`app.manifest` 锁定 `asInvoker`、自包含便携包构建脚本就位 | `[ ]` 在**无预装 .NET、断网**的 **Win10 22H2 (zh-CN / en-US)** 与 **Win11 x64 (en-US)** 干净 VM 解压双击运行并导出双格式 |
| **AC-08 真实玩家试用** | — | `[ ]` 按 [USER_TRIAL_GUIDE.md](USER_TRIAL_GUIDE.md) 招募 `>= 10` 位不同玩家完成盲测，至少 `8` 位独立完成并准确复述下一步建议 |

---

## 2. 干净断网虚拟机实测操作手册（AC-01 / AC-05 / AC-07）

在干净快照的 **Win10 22H2 (zh-CN / en-US)** 与 **Win11 x64 (en-US)** 虚拟机（建议配置 `4 vCPU / 8 GB RAM / SSD`，**不安装 .NET Runtime**，**断开网络**）中执行：

1. **校验便携包完整性并解压**（普通标准用户账户，非管理员）：
   ```powershell
   Get-FileHash .\GameCrashKit-v0.1.0-alpha.1-win-x64.zip -Algorithm SHA256
   Expand-Archive .\GameCrashKit-v0.1.0-alpha.1-win-x64.zip -DestinationPath "$env:USERPROFILE\Desktop\GameCrashKit-Test"
   ```
2. **启动自动化门禁脚本**（需传入便携目录路径，确认在 4C/8G VM 上默认扫描 P95 ≤ 5s 且只读哈希不变）：
   ```powershell
   powershell -ExecutionPolicy Bypass -File .\Verify-ReleaseGate.ps1 -PortableDir "$env:USERPROFILE\Desktop\GameCrashKit-Test" -BenchmarkRuns 20
   ```
3. **验证隔离崩溃捕获（仅在虚拟机内运行！）**：
   ```powershell
   .\SyntheticCrashTool.exe --mode av --confirm-crash
   ```
   等待 Windows 完成错误报告写入（约 3~5 秒）后，双击桌面解压目录中的 `GameCrashKit.App.exe`：
   * 确认**无 UAC 提权弹窗**；
   * 按 `F5` 开始扫描，在左侧候选列表确认看到 `SyntheticCrashTool.exe`（`0xc0000005`）；
   * 按 `Ctrl+E` 打开预览导出，分别验证保存 PNG 长图与复制 Markdown。
4. **ProcMon（Process Monitor）只读与零出站审计（AC-06）**：
   * 过滤 `Process Name is GameCrashKit.App.exe`；
   * 确认扫描全过程 `Operation` 出现 `TCP/UDP` 连接数为 **0**；
   * 确认在用户主动点击「保存」之前，对游戏/系统目录和手选日志文件无任何 `WriteFile`、`SetDispositionInformationFile` 或 `RegSetValue` 操作。

---

## 3. DPI 与主流平台 PNG 压缩回读检查表（AC-06）

* [ ] 系统显示缩放 `100%`：主窗口与预览窗无文字截断、PNG 清晰无裁切。
* [ ] 系统显示缩放 `125%`：导出 PNG 尺寸保持固定 `900 px` 逻辑宽度（`96 DPI`）不变形。
* [ ] 系统显示缩放 `150%` / `200%`：主窗口按钮自动换行无遮挡，导出 PNG 依然为 `900 px` 清晰长图。
* [ ] 微信 PC / 手机端默认图发送（非原图）回读：标题、故障三元组、核心结论与「建议下一步」字迹清晰可辨。
* [ ] QQ 群 / 贴吧 / NGA / 小黑盒图片上传回读：长图无严重糊字或截断。
