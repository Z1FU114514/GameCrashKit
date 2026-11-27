# 更新日志 (Changelog)

## [v0.2.0-alpha.3] - 2026-11-27

对外推广前 P0/P1 红队复查整改版本（通过受保护 `main` Pull Request + 云端实包沙盒复测）：
* **探测脚本硬校验与进程隔离（P0 + P1）**：`scripts/Test-FirstRunTrust.ps1` 在任何解压/解锁/启动前强制比对目标文件名与完整 64 位 SHA-256，校验缺失或失配立即失败终止（`0` 次启动）；停止进程仅限于脚本自身派生并登记句柄的测试实例，不再误杀已有应用会话或 `smartscreen.exe`，同时去除用户名与精确时钟输出。
* **隐藏名称全字段生效（P1）**：`ShareReportProjector` 在勾选「隐藏程序名」后统一掩码独立日志格式标签与常见软件名（如 `OBS Studio` / `Blender` / `ComfyUI` / `Unity` / `Unreal`），摘要栏、规则标题与可选信号摘要均不再残留软件名。
* **收紧主进程致命性升级（P1）**：新增 `LogFatalityKind.ChildProcessExit` 与更诚实的 `进程退出未证实` 状态；OBS 设备重建、ComfyUI/Blender GPU OOM 单独出现时不升级为主进程 `Fatal` 且不给出页面文件修改建议；ComfyUI `exited with code: 3221225477` 兼容冒号写法并严格标注为子进程异常退出；Blender 信号扫描同步剔除 `Stack trace:` / `# Python backtrace` 堆栈节并优先从 `ExceptionCode` 提取访问违规，避免堆栈内字符引发误报。
* **独立日志严禁混入宿主机背景与环境（P1）**：`WindowsScanCoordinator` 在未显式关联 Windows 候选的独立日志模式下标注「已扫描，未关联」，不再将本机 7 项环境快照（如页面文件状态）或 `Kernel-Power 41` 系统背景事件带入他人或外部日志的诊断规则与导出卡。

## [v0.2.0-alpha.2] - 2026-11-27

跨软件静态适配器扩展、首轮真实格式样本自测与首发可信度治理更新：
* **跨软件静态文本适配器与真实报错词表**：新增纯 C# 静态适配器识别 **OBS Studio**（`(887A0005)` / `Device Remove/Reset!`、RHI `Direct3D 11`）、**Blender 4.2**（`EXCEPTION_ACCESS_VIOLATION` / Cycles `CUDA error: Out of memory`，并剥离 `# backtrace` 与 `# Python backtrace` 私有路径）与 **ComfyUI Desktop**（`torch.OutOfMemoryError: CUDA out of memory` 与 Python 进程 `3221225477` 退出码精确映射为 `LogSignalId.AccessViolation` 而非 OOM）；直接复用 `ENGINE-GPU-LOST@2` 与 `ENGINE-OOM@2`，零 UI / 规则重写（[跨软件评估报告](CROSS_APP_EVALUATION_ROUND1.md)）。
* **首发可信度与 SmartScreen 实测工具链**：新增可在 Win10/Win11 非管理员账户复用的下载/MotW/解压对比探测脚本（`scripts/Test-FirstRunTrust.ps1` 与 `scripts/New-FirstRunTrustSandboxKit.ps1`），并形成 [首次运行可信度与签名指南](FIRST_RUN_TRUST_AND_SIGNING.md)，补充资源管理器解锁方法与 SignPath Foundation 开源签名准入清单。
* **开源安全与贡献治理**：新增根目录 `SECURITY.md` 与 `CONTRIBUTING.md`，固化双许可、`asInvoker`、零网络与反作弊零侵入红线。
* **全量回归与离线沙盒验收**：单元测试与 WPF 端到端矩阵扩展至 **109 / 109 通过**，断网无 `.NET 10` Windows Sandbox 跑通 **11 / 11** 全自动验收。

## [v0.2.0-alpha.1] - 2026-11-27

通用信号底座最小演进、仅手选日志独立报告与单次分享信息完备性版本：
* **底座通用化演进**：扩展固定日志信号集合（`LogSignalId`、`LogSignalMatch`、`FormatDisplayLabel`）与三态报告主体（`WindowsIncident` / `StandaloneLogOnly` / `InsufficientEvidence`），保持纯 C# 静态注册与审核可控边界。
* **冷启动仅手选日志独立诊断（MVP）**：无需先按 `F5` 扫描 Windows 事件，直接拖入或 `Ctrl + O` 选择 Unity / Unreal / 通用 `.log`/`.txt` 日志即可生成并导出独立急诊卡（新增 `LOG-ONLY-SUMMARY@1` 与模式切换「独立查看 / 关联当前事故」），严格不虚构 Windows 事件号或故障模块，覆盖 Unreal 非致命 `Ensure`、2 MiB 截断日志及纯数字图形码场景。
* **单次分享信息完备性**：Markdown 与 900px 浅底 PNG 急诊单同步新增「扫描范围」（相对时间窗、过滤模式与每频道上限，不含精确时钟泄漏）与「采集受限与数据边界说明」（纯枚举模板生成，绝不透传系统原始错误文本），同时保留无 `RawErrorExcerpt` 原文出口、句柄级防覆盖与零 PII 隐私边界。
* **自动化测试与离线沙盒复验**：核心、Windows 与 WPF 端到端测试矩阵全部通过，并在断网无 `.NET 10` 预装的 Windows Sandbox 跑通 11 项全自动无人值守验收。

## [v0.1.0-alpha.2] - 2026-11-27

安全边界与归因准确度加固补丁：
* **信号摘要严格白名单（P0）**：导出摘要中的 `EngineVersion` 仅提取前导数字版本号、自动剥离私有分支或提交后缀（如 `+PrivateBranch` / `(1234567890ab)`），`RHI` 收敛为规范图形后端枚举，彻底消除自由文本出口。
* **本地先验顺序调整与 `.dmp` 拒绝（P0）**：`SafeLocalFileReader.ReadTailUtf8` 在任何路径存在性检查前优先校验祖先重解析点并直接拒绝 `.dmp` / `.mdmp` / `.hdmp` 内存转储。
* **文件唯一标识防覆盖与预览入口修复**：保存急诊单前通过 Win32 `GetFileInformationByHandle` 比对底层卷序列号与文件索引（防止别名/硬链接覆盖当前源日志）；单行数值错误码（如 `0x887A0005`）现已正常启用信号摘要附带开关。
* **关联与 OOM 规则收敛**：重复事故聚合不再借用更早一次的 WER 为 `Direct` 归因；`ENGINE-OOM@2` 修复截断日志与 `0xe0000008` 组合误升为 `ExplicitRecord` 的问题，并显式区分 Unreal 非致命 `Ensure`。
* **容错加固**：空白 `.runtime-xml` 返回受控提示而非抛出未捕获异常。

## [v0.1.0-alpha.1] - 2026-11-26

首轮公开测试候选版（完成 M0 实证基线、M1 核心引擎、M2 桌面端与日志沙箱、M3 脱敏急诊单导出及 M4 CI/自动化发布门禁建设，进入隔离 VM 与真实玩家盲测阶段）。

### 核心能力
* **纯本地零提权取证（`asInvoker`）**：通过 XPath 时间窗逆序查询 `Application`（Event `1000` / `1001` / `1002`）与 `System`（`Kernel-Power 41`）原始 XML，跨语言解析不依赖本地化文本，单扫描 10 秒软超时与 1,000 条/频道上限。
* **严格事故联结与双刻度规则**：按 `(Channel, RecordId)` 去重；严格区分「归属强度（直接/可能/手选/仅邻近/未关联）」与「证据强度（明确记录/有限线索/证据不足）」，禁止 PID 复用误并或将邻近系统背景升格为硬件/驱动根因；内置 10 条版本化纯 C# 规则。
* **7 项中性环境事实快照**：只读采集显卡发布日期、目标路径特征（非 ASCII / `> 260` 字符）、文档 OneDrive 状态、x64/x86 VC++ 运行库（含 `WOW6432Node`）、多页面文件总量、`CreateToolhelp32Snapshot` 瞬时覆盖层快照及系统区域/HVCI 状态，与崩溃归因严格隔离。
* **Unity / Unreal 日志安全沙箱**：阻断 UNC、网络盘、设备路径及重解析点/目录联接，通过 `GetFinalPathNameByHandleW` 校验句柄最终路径，仅读末尾 2 MiB UTF-8 文本，校验读取期文件未变动并排除 `<PCallStack>`。
* **双层脱敏急诊单与极夜深空桌面端**：采用「极夜深空 `#0B0E14` + 电光靛蓝 `#6366F1` + 赛博青 `#22D3EE`」响应式暗色界面与定制 `app.ico`；导出采用同一份严格字段白名单生成**所见即所得脱敏 Markdown** 与 **900px / 96-DPI 浅色白底 PNG 急诊单**，支持可选勾选附带脱敏截断报错摘要。
* **工程与开源治理**：`MIT OR Apache-2.0` 双重许可、GitHub Actions CI 与 Tag 自动便携包发布流水线、静态反作弊/零出站/只读安全审计测试。
