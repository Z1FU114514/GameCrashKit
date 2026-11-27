# GameCrash Kit v0.2 支持矩阵（Support Matrix）

> **准入原则**：任何操作系统构建、系统语言或日志来源，必须在真实环境或隔离虚拟机完成普通用户权限实测并记录夹具后，方可标记为「已实测支持」。未通过门槛的来源保持「待验证 / 实验性」，不计入正式支持承诺。

## 1. 操作系统与语言支持矩阵

| 系统版本 | 架构 | 系统语言 | 当前状态 | 备注 |
| :--- | :--- | :--- | :--- | :--- |
| Windows 11 25H2 (Build 26200) | x64 | `zh-CN` | **已实测验证（普通权限只读）** | 宿主机实测通过完整取证、环境探针与导出链 |
| Windows 11 24H2 (Build 26100) | x64 | `zh-CN` | **已实测验证（断网无 .NET 10 隔离沙盒）** | 无人值守 Sandbox 跑通真实 `Event 1000` 与冷启动独立日志验收 |
| Windows 11 x64 | x64 | `en-US` | **待验证** | 后续版本在隔离 VM 完成英文系统语言验收 |
| Windows 10 22H2 | x64 | `zh-CN` / `en-US` | **待验证** | M0 遗留项，计划在隔离 VM 完成实测与 XML 版本确认 |

## 2. 证据来源准入状态

| 来源 | 预期角色 | 当前状态 | 准入条件与说明 |
| :--- | :--- | :--- | :--- |
| `Application / Application Error / 1000` | 主闪退锚点 | **已实测验证（Win11 zh-CN）** | 已核对 15 字段命名 XML 与真实崩溃捕获 |
| `Application / Application Hang / 1002` | 无响应卡死锚点 | **已验证架构（Win11 zh-CN）** | M0 已核对 10 字段命名 XML |
| `Application / Windows Error Reporting / 1001` | 联结补充 / 弱候选 | **已验证架构（Win11 zh-CN）** | 仅允许白名单 `EventName`（`APPCRASH`/`BEX64`/`AppHangB1` 等） |
| `System / Microsoft-Windows-Kernel-Power / 41` | 纯系统背景 | **已验证架构（Win11 zh-CN）** | `Ver=10`；绝不单独创建游戏事故候选 |
| 7 项中性中文环境事实探针 | 非归因环境快照 | **已实测本机可读（非管理员）** | 已修正 OS 版本判定、WOW6432Node VC++、多 GPU、多页面文件与 OneDrive 本地先验 |
| Unity `Player.log` / `Player-prev.log` | 独立/关联诊断日志 | **已实测（合成矩阵 + 沙盒只读）** | 支持冷启动独立报告、2 MiB 尾段截断检测、数值图形码（`0x887A0005`），待补充商业游戏实机样本 |
| Unreal `CrashContext.runtime-xml` | 独立/关联诊断日志 | **已实测（合成矩阵）** | 支持 DTD 禁用、区分致命 / 非致命 `Ensure`、空 XML 受控降级，待补充商业游戏实机样本 |
| 通用桌面文本日志（`.log` / `.txt`） | 冷启动独立最小报告 | **已实测（合成矩阵）** | 支持未知或通用桌面文本命中已知 GPU/OOM 固定信号或输出“未命中已知标记”最小报告 |
| `System / Display / 4101`（及 `nvlddmkm` / `amdkmdag`） | 实验性图形背景 | **未准入（实验性）** | 必须先取得真实 4101 XML 样本并验证字段语义；即使准入也仅作邻近背景 |
| 本地 `WER\ReportArchive` / `CrashDumps` 元数据 | 扩展元数据 | **未准入（延后）** | 严格不读 `.dmp` 内存转储；扩展 WER 归档元数据待后续评估 |
