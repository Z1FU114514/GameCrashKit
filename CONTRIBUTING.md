# 贡献指南 (Contributing)

感谢关注并参与 **GameCrash Kit**！为保障玩家安全、反作弊零侵入性与社区可信度，所有 Pull Request 必须遵守以下开发与发布门禁。

## 1. 开源协议声明

* 本仓库采用 **`MIT OR Apache-2.0`** 双重开源许可（详见 [LICENSE-MIT](LICENSE-MIT) 与 [LICENSE-APACHE](LICENSE-APACHE)）。
* 所有提交的代码、脚本与测试夹具必须保持 100% 开源兼容，不得引入闭源 DLL、商业 SDK 或未审计的动态规则/插件下载器。

## 2. 架构与安全红线

1. **禁止提权与网络行为**：`app.manifest` 必须保持 `asInvoker`；`src/` 下不得引入出站网络调用、遥测或静默更新逻辑。
2. **禁止接触游戏进程或转储内存**：不得使用 `OpenProcess` / `ReadProcessMemory` / `DebugActiveProcess`，不得打开 `.dmp` / `.mdmp` / `.hdmp` 内存转储。
3. **保护隐私边界**：所有外部输出（Markdown 与 PNG）必须只走 `ShareReportProjector` 严格字段白名单，严禁把绝对路径、主机原始异常文本、未剥离分支串或堆栈帧送入共享输出。
4. **测试夹具隐私规范**：提交的所有 `.xml` / `.log` / `.txt` 测试夹具必须完全匿名化，不得包含真实个人用户名、计算机名、真实项目私有路径或密钥。

## 3. 提交前必须通过的本地验证

在推送分支或提交 Pull Request 前，请在仓库根目录执行以下检查：

```powershell
# 1. 严格零警告编译与全量测试（Core / Windows / App WPF & PNG）
dotnet build GameCrashKit.sln -c Release
dotnet test GameCrashKit.sln -c Release --no-build

# 2. 验证纯净源码归档（确保 .gitignore 未遗漏必要夹具）
$tmp = Join-Path $env:TEMP "gck-clean-check"
New-Item -ItemType Directory -Path $tmp -Force | Out-Null
git archive --format=tar HEAD | tar -x -C $tmp
Push-Location $tmp
dotnet test GameCrashKit.sln -c Release
Pop-Location
Remove-Item $tmp -Recurse -Force
```

同时请确保随 `.github/workflows/ci.yml` 的静态架构边界与清单审计检查通过。
