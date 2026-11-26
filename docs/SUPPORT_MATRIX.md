# GameCrash Kit v0.1 支持矩阵（Support Matrix）

> **准入原则**：任何操作系统构建、系统语言或日志来源，必须在真实环境或隔离虚拟机完成普通用户权限实测并记录夹具后，方可标记为「已实测支持」。未通过门槛的来源保持「待验证 / 实验性」，不计入正式支持承诺。

## 1. 操作系统与语言支持矩阵

| 系统版本 | 架构 | 系统语言 | 当前状态 | 备注 |
| :--- | :--- | :--- | :--- | :--- |
| Windows 11 25H2 (Build 26200) | x64 | `zh-CN` | **本机已验证（普通权限只读）** | 尚待干净断网无 .NET 运行时 VM 便携包端到端验证 |
| Windows 11 x64 | x64 | `en-US` | **待验证** | M4 阶段在隔离 VM 完成跨语言验收 |
| Windows 10 22H2 | x64 | `zh-CN` / `en-US` | **待验证** | M0 遗留项，M4 阶段前在隔离 VM 完成实测与 XML 版本确认 |

## 2. 证据来源准入状态

| 来源 | 预期角色 | 当前状态 | 准入条件与说明 |
| :--- | :--- | :--- | :--- |
| `Application / Application Error / 1000` | 主闪退锚点 | **已验证架构（Win11 zh-CN）** | M0 已核对 15 字段命名 XML；待补 Win10 22H2 样本 |
| `Application / Application Hang / 1002` | 无响应卡死锚点 | **已验证架构（Win11 zh-CN）** | M0 已核对 10 字段命名 XML |
| `Application / Windows Error Reporting / 1001` | 联结补充 / 弱候选 | **已验证架构（Win11 zh-CN）** | 仅允许白名单 `EventName`（`APPCRASH`/`BEX64`/`AppHangB1` 等） |
| `System / Microsoft-Windows-Kernel-Power / 41` | 纯系统背景 | **已验证架构（Win11 zh-CN）** | `Ver=10`；绝不单独创建游戏事故候选 |
| 7 项中性中文环境事实探针 | 非归因环境快照 | **已验证本机可读（非管理员）** | 已修正 OS 版本判定、WOW6432Node VC++、多 GPU、多页面文件问题 |
| Unity `Player.log` / `Player-prev.log` | 引擎辅助证据（手选/窗口过滤） | **待真实崩溃样本验证** | 需通过大日志截断、非 UTF-8、符号链接拦截测试 |
| Unreal `CrashContext.runtime-xml` | 引擎辅助证据（手选/窗口过滤） | **待真实崩溃样本验证** | 需通过 XML 禁 DTD 解析与脱敏测试 |
| `System / Display / 4101`（及 `nvlddmkm` / `amdkmdag`） | 实验性图形背景 | **未准入（实验性）** | 必须先取得真实 4101 XML 样本并验证字段语义；即使准入也仅作邻近背景 |
| 本地 `WER\ReportArchive` / `CrashDumps` 元数据 | 扩展元数据 | **未准入（延后）** | 暂不纳入 v0.1 默认采集范围 |
