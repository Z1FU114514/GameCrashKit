# 安全政策与代码签名说明 (Security Policy)

## 支持的版本

| 版本分支 | 状态 | 说明 |
| :--- | :--- | :--- |
| `v0.2.x-alpha` | **当前维护** | 活跃迭代与安全修复 |
| `v0.1.x-alpha` | **安全归档** | 关键红队边界已回合至 `v0.2.x` |

## 核心安全边界（已纳入自动化构建审计）

**GameCrash Kit** 坚持五项不可协商的本地安全与反作弊零侵入红线：

1. **零提权（`asInvoker`）**：应用程序清单永久锁定 `requestedExecutionLevel="asInvoker"`，永不索要管理员权限或注册内核/后台服务。
2. **纯本地零联网**：Core / Windows / App 源码不发起任何 HTTP/TCP/UDP 网络请求、遥测或自动更新。
3. **只读取证与防覆盖**：除用户显式点击导出保存报告外不修改系统状态；保存文件前通过 Win32 `GetFileInformationByHandle` 校验底层卷序列号与文件索引，禁止覆盖当前已加载的取证源日志。
4. **零进程/转储接触**：严禁调用 `OpenProcess`、`ReadProcessMemory`、`EnumProcessModules` 或附加调试器，入口即直接拒绝 `.dmp` / `.mdmp` / `.hdmp` 内存转储。
5. **本地文件系统先验防护**：在任何路径存在性检查前优先校验证祖先重解析点、UNC 与本地固定盘，禁止通过重定向共享触发隐藏网络访问。

## 漏洞报告渠道

若发现隐私脱敏逃逸、路径重解析绕过或安全边界缺陷：

* 请勿先公开利用细节或提交含真实个人敏感日志的路径片段。
* 请通过 GitHub 仓库的 **Security Advisories**（Private vulnerability reporting）或仅含脱敏合成复现步骤的 GitHub Issue 提交报告，维护者将在 72 小时内响应。

## 代码签名与首发可信政策（SignPath Foundation 就绪规范）

* **当前状态**：`v0.2.0-alpha.1` 便携发行包暂未附加商业或开源 Authenticode 证书，因此 Windows Defender SmartScreen 首次运行从浏览器解压出的程序时可能提示“未知发布者”。请用户核对官方 Releases 页面提供的 `.SHA256SUMS.txt` 校验值后再运行。
* **开源签名规划（SignPath Foundation）**：本软件所有发行产物均严格遵守 `MIT OR Apache-2.0` 双重开源许可，不包含任何闭源或商业二元组件，且所有官方 ZIP 均仅由 GitHub 托管的 `windows-latest` runner 基于公开源码与 Tag 自动构建。我们已完成免费开源代码签名所需的工程治理准备，一旦完成 SignPath Foundation 审查接入，Release 流水线将直接为二进制可执行文件附加可验证的 SignPath Foundation 开源签名。
