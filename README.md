# GameCrash Kit（游戏闪退急诊室）

面向中文 Windows PC 玩家与轻度 Mod 玩家的**开源、纯本地、零提权、只读式事后游戏闪退取证与脱敏急诊单工具**。

> **当前阶段**：M0 证据基线与工程骨架已建立（v0.1-alpha 研发中）。详细设计请见 [docs/SPEC_v0.1.md](docs/SPEC_v0.1.md)。

## 核心安全与隐私承诺

* **零提权（`asInvoker`）**：不弹出 UAC 权限请求，不访问受限 `C:\Windows\Minidump` 或读取 `.dmp` 内存转储。
* **100% 只读 & 零持久化**：不修改注册表、系统配置或游戏/Mod 文件；不建立本地历史数据库，关闭即释放会话内存。
* **反作弊零侵入**：严禁对任何游戏进程调用 `OpenProcess`、`ReadProcessMemory`、注入 DLL 或附加调试器。
* **零网络与零遥测**：扫描过程零出站连接，无自动更新，无使用遥测。
* **双层脱敏分享**：默认严格字段白名单生成 Markdown 与 PNG 急诊单；可选附带的报错摘要需经本地脱敏与用户预览确认。详细规则见 [docs/PRIVACY.md](docs/PRIVACY.md)。

## 解决方案结构

```text
GameCrashKit/
├── docs/
│   ├── SPEC_v0.1.md              # v0.1 最终项目方案与工程立项规格书
│   ├── PRIVACY.md                # 隐私承诺与白名单字段清单
│   ├── M0_EVIDENCE_MATRIX.md     # M0 主机实测证据矩阵与工程避坑记录
│   └── SUPPORT_MATRIX.md         # 实测支持的系统、语言与日志来源矩阵
├── src/
│   ├── GameCrashKit.Core/        # 纯领域模型、XML 结构化解析、归因规则与脱敏投影（零 UI/Win32 依赖）
│   ├── GameCrashKit.Windows/     # Windows 只读 EventLog XPath 查询与环境事实探针
│   └── GameCrashKit.App/         # WPF 单窗口桌面端（锁定 asInvoker）
└── tests/
    ├── GameCrashKit.Core.Tests/      # 领域与脱敏单元测试
    ├── GameCrashKit.Windows.Tests/   # Windows 只读采集与环境探针集成测试
    ├── Fixtures/                     # 零隐私合成/脱敏夹具集
    └── SyntheticCrashTool/           # 隔离 VM 测试用可控崩溃夹具（不随发行包分发）
```

## 构建与测试

前置条件：`.NET 10 SDK`（Windows x64）。

```powershell
dotnet build GameCrashKit.sln -c Release
dotnet test GameCrashKit.sln -c Release --no-build
```

## 开源协议

本项目采用 **`MIT OR Apache-2.0`** 双重许可。你可以任选 [LICENSE-MIT](LICENSE-MIT) 或 [LICENSE-APACHE](LICENSE-APACHE) 之一遵守。
