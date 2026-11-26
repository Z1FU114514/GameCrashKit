# M0 实证证据矩阵与工程避坑记录

> **采集日期**：2026-11-26  
> **测试环境**：Windows 11 25H2 x64（注册表 `CurrentBuildNumber=26200`, `UBR=9457`），`UICulture=zh-CN`，`ACP=936`，`OEMCP=936`  
> **执行权限**：普通非管理员用户（`IsAdmin=False`），只读扫描  
> **SDK 环境**：`.NET SDK 10.0.301`（`Microsoft.WindowsDesktop.App 10.0.9`、`Microsoft.NETCore.App 10.0.9`）

---

## 1. Windows 事件日志真实 XML 结构实测（非管理员权限）

| 频道 / Provider / EventID | 本机实测状态 | Version & Level | `<EventData>` 实测结构与关键字段 |
| :--- | :--- | :--- | :--- |
| `Application` / `Application Error` / `1000` | 可读（27 条样本） | `Ver=0`, `Level=2` | **15 个命名 `<Data Name="...">` 节点**：`AppName`, `AppVersion`, `AppTimeStamp`, `ModuleName`, `ModuleVersion`, `ModuleTimeStamp`, `ExceptionCode`, `FaultingOffset`, `ProcessId`, `ProcessCreationTime`, `AppPath`, `ModulePath`, **`IntegratorReportId`**, `PackageFullName`, `PackageRelativeAppId` |
| `Application` / `Application Hang` / `1002` | 可读（2 条样本） | `Ver=0`, `Level=2` | **10 个命名 `<Data>` 节点**：`AppName`, `AppVersion`, `ProcessId`, `StartTime`, `TerminationTime`, **`ExeFileName`**, **`ReportId`**, `PackageFullName`, `PackageRelativeAppId`, `HangType` |
| `Application` / `Windows Error Reporting` / `1001` | 可读（300+ 条样本） | `Ver=0`, `Level=4` | **23 个命名 `<Data>` 节点**：`Bucket`, `BucketType`, `EventName`, `Response`, `CabId`, `P1`..`P10`, `AttachedFiles`, `StorePath`, `AnalysisSymbol`, `Rechecking`, `ReportId`, `ReportStatus`, `HashedBucket`, `CabGuid` |
| `System` / `Microsoft-Windows-Kernel-Power` / `41` | 可读（11 条样本） | `Ver=10`, `Level=1` | **21 个命名 `<Data>` 节点**：`BugcheckCode`, `BugcheckParameter1..4`, `SleepInProgress`, `PowerButtonTimestamp`, `BootAppStatus`, `Checkpoint`, ..., `WHEABootErrorCount` |
| `System` / `Display` / `4101` | 本机 0 条记录 | 未实测到事件 | `ProviderMetadata("Display").Events.Count == 0`（无现代清单事件定义）；**保持条件准入 / 待真实样本验证** |
| `System` / `nvlddmkm` / `153` | 可读（2 条样本） | `Ver=0`, `Level=2` | `<EventID Qualifiers="...">153</EventID>`（含 XML 属性）；**2 个无 `Name` 属性的位置 `<Data>` 节点**；`ProviderMetadata("nvlddmkm").Events.Count == 0` |
| `System` / `amdkmdag` | 本机 0 条记录 | — | 非 AMD 机器调用 `ProviderMetadata("amdkmdag")` 直接抛出 `FileNotFoundException` |

### 关键实测指标
* **XPath 7 天组合查询耗时**：`Application` 频道（1000/1001/1002 联合过滤）返回 180 条记录耗时 **29 ms**。
* **`1001` 噪音比例**：抽样 300 条 `1001` 记录中，绝大多数为非游戏系统/应用后台事件（`LiveKernelEvent=139`、`StoreAgentScanForUpdatesFailure0=104`、`crashpad_log=36`、`RADAR_PRE_LEAK_64=8`、`CLR20r3=1` 等），且实测到 `1001.ReportId` 与 `1000.IntegratorReportId` GUID 精确匹配。

---

## 2. 本阶段暴露的 9 项关键工程避坑点

1. **Event 1000 报告标识字段名差异**：Win11 Event 1000 中关联 GUID 字段名为 **`IntegratorReportId`**（而非 `ReportId`），而 1001 和 1002 中名为 `ReportId`。
2. **`ExceptionCode` 无 `0x` 前缀**：Event 1000 的 `ExceptionCode` 原始值为 `c0000005`（不带 `0x`），规范化层必须统一补齐并小写化为 `0xc0000005`。
3. **`<EventID>` 可能带 XML 属性**：驱动级事件（如 `nvlddmkm 153`）会出现 `<EventID Qualifiers="...">153</EventID>`，解析时只能读取元素文本内容并做整型转换。
4. **驱动事件 `<Data>` 无 `Name` 属性**：部分老驱动事件没有 `Name` 属性，解析器必须同时保留命名字典与原始位置列表，并在缺少夹具验证的版本下拒绝臆测位置映射。
5. **严禁运行时无条件实例化 `ProviderMetadata`**：在非对应硬件机器上会直接抛 `FileNotFoundException`，或返回 `Events.Count == 0`。
6. **必须过滤 `1001.EventName` 白名单**：`LiveKernelEvent`、`StoreAgentScanForUpdatesFailure0`、`RADAR_PRE_LEAK_64` 等高频记录绝不能被当作游戏应用崩溃候选。
7. **OS 版本识别不能信 `ProductName`**：Win11 25H2 在注册表 `CurrentVersion\ProductName` 仍返回 `"Windows 10 Pro"`，必须以 `CurrentBuildNumber >= 22000` 判定 Windows 11。
8. **64 位系统上的 x86 VC++ 运行库位于 `WOW6432Node`**：本机 `HKLM\SOFTWARE\Microsoft\VisualStudio\14.0\VC\Runtimes\x86` 不存在，实际位于 32 位注册表视图（`RegistryView.Registry32`）下，必须同时探测 64 位与 32 位视图。
9. **多显卡适配器与多页面文件**：本机同时存在虚拟显示适配器与物理独立显卡，且存在 `C:\pagefile.sys` 与 `D:\pagefile.sys` 两个页面文件；探测器必须返回完整适配器列表并对页面文件容量求和，不可只取 `.First()`。

---

## 3. M0 待补齐验证项（M1~M4 期间在隔离 VM 完成）

* [ ] 干净 **Windows 10 22H2**（zh-CN 与 en-US）虚拟机事件 XML 实测与夹具核对（含位置 `<Data>` 变体确认）。
* [ ] 干净 **Windows 11 x64 en-US** 虚拟机跨语言 XML 与环境探针实测。
* [ ] 真实 `System / Display / 4101` TDR 恢复事件 XML 样本核验。
* [ ] 真实独立游戏崩溃（≥2 款）及真实 Unity `Player.log` / Unreal `CrashContext.runtime-xml` 脱敏样本收集。
* [ ] 直接竞品（`game-crash-doctor`、`CrashScope`、`CRASQ One`）同样本事后流程对照实测记录。
