# AppLock 应用锁

给指定的 Windows 程序加密码：被锁程序启动时立即被挂起并弹出密码框，密码正确才能继续，错误/取消则结束进程。

## 架构

```
┌──────────────────────────────┐   命名管道   ┌──────────────────────────────┐
│  AppLock.Service.exe          │◄──────────►│  AppLock.exe（托盘程序）        │
│  Windows 服务，SYSTEM 权限     │  \\.\pipe\  │  用户会话，管理员权限           │
│  · 轮询进程、挂起/恢复/结束     │  AppLock.v1 │  · 解锁弹窗                    │
│  · 校验密码、保存配置和日志     │             │  · 管理界面（程序列表/日志/设置） │
└──────────────────────────────┘             └──────────────────────────────┘
```

| 项目 | 说明 |
|---|---|
| `Lock.Core` | 共享库：配置模型、PBKDF2 密码、恢复密钥、管道协议、Win32 封装、服务安装器 |
| `Lock.Service` | Windows 服务（`Microsoft.Extensions.Hosting.WindowsServices`） |
| `Lock` | WPF 托盘程序（统一主题在 `Themes/Theme.xaml`，所有窗口用 `Style="{StaticResource AppWindow}"`） |

三个项目输出到同一目录 `build\<Configuration>\`，托盘程序据此找到同目录的服务程序。

## 打包

在项目目录打开 PowerShell：

```powershell
cd E:\Jason\MyGit\Lock
.\publish.ps1                  # 框架依赖版，约 3 MB（本机已有 .NET 10，直接可用）
.\publish.ps1 -SelfContained   # 自包含版，约 150 MB，给没装 .NET 的电脑用
```

输出在 `publish\`，里面 `AppLock.exe`（托盘程序）和 `AppLock.Service.exe`（服务）必须放在**同一目录**。

也可以手动执行等价命令：

```powershell
dotnet publish Lock.Service/Lock.Service.csproj -c Release -r win-x64 --no-self-contained -o publish
dotnet publish Lock/Lock.csproj            -c Release -r win-x64 --no-self-contained -o publish
```

## 首次启动（3 步）

1. **把 `publish` 目录整个复制到一个固定位置**，比如 `C:\Program Files\AppLock\`。
   服务和计划任务会记录这个路径，之后不要移动，否则要重新安装服务。

2. **双击 `AppLock.exe`**（会弹 UAC，点“是”）。
   因为服务还没装，它会直接打开管理界面的“服务”页 → 点 **安装并启动服务**。
   这一步会做三件事：
   - 创建并启动服务 `AppLockService`（自动启动、崩溃后 5 秒自动重启）
   - 创建 `C:\ProgramData\AppLock\` 并收紧 ACL（只有 SYSTEM 和管理员可访问）
   - 注册计划任务 `AppLock Agent`：任意用户登录时以最高权限启动托盘程序（`requireAdministrator` 的程序放在 Run 键会被 UAC 静默拦截，所以用计划任务）

3. 服务启动后 2 秒内托盘程序会自动连上，弹出**设置密码**窗口 → 设好后会显示一次**恢复密钥**，务必抄下来。
   然后自动进入管理界面 → “被锁程序”页添加要锁的程序 → **保存**。

之后每次开机登录，计划任务会自动拉起托盘程序，不需要手动做任何事。托盘图标右键可打开管理界面（需密码）。

### 验证是否生效

添加一个程序（比如从“运行中的程序”里选微信）并保存后，关闭该程序再打开——应该看到密码框，程序窗口不会出现。也可以在管理界面“解锁日志”页看到记录。

### 命令行方式（可选）

```powershell
# 管理员 PowerShell
cd "C:\Program Files\AppLock"
.\AppLock.Service.exe install      # 安装并启动服务 + 计划任务
.\AppLock.Service.exe uninstall    # 卸载（保留配置）；加 --purge 连配置一起删
sc query AppLockService            # 查看服务状态
```

出问题时看 `C:\ProgramData\AppLock\service.log`（需要管理员权限打开）。

卸载：管理界面“服务”页 → **停止并卸载服务**，或上面的 `uninstall` 命令。

## 使用

- **被锁程序**：三种添加方式
  - **从已安装的程序添加**：列出控制面板“程序和功能”+ 开始菜单里的程序。控制面板不记录主程序路径，主程序是推断的（优先开始菜单快捷方式的目标，其次注册表 `DisplayIcon`，最后扫描安装目录），列表里“来源”一列说明推断依据，灰色的是没推断出主程序的（运行库、驱动等）。
  - **从运行中的程序添加**：拿到的是真实进程路径，最准确；商店应用（如 ChatGPT）只能用这种方式。
  - **选择程序文件**：手动选 exe。

  每项可选匹配方式：
  - **完整路径**（默认）：只拦截这一个文件，改名的副本不受影响。
  - **文件名**：不区分目录。系统目录下的程序（如 Win11 的 `notepad.exe` 实际是商店应用的跳板）会自动改为此模式。
- **宽限期**：解锁后 N 秒内同一程序再次启动免密码；0 表示每次都要。
- **多进程程序**（Chrome、企业微信等）：只有**父进程是已解锁实例**的子进程才放行（用创建时间校验，防 PID 复用）。从桌面/启动器点开的永远算新启动，需要密码——即使该程序还有残留进程没退干净。
- **已在运行的程序**：添加锁或服务启动时，已在运行的匹配进程会被登记为已解锁，它们派生的子进程不受影响；程序完全退出后再打开才需要密码。
- **解锁日志**：记录每次解锁成功/失败/取消/结束、设置修改、密码修改等，含用户名与会话号。
- **忘记密码**：任何密码框都有“忘记密码？”，输入恢复密钥即可重置；重置和修改密码都会生成新的恢复密钥，旧密钥立即失效。

## 安全边界

- 拦截与密码校验在 SYSTEM 服务里完成；托盘程序即使被篡改也拿不到密码哈希（配置目录普通用户不可读）。
- 托盘程序被关闭时，被锁程序启动会被服务 **直接结束**（不会因为没人弹窗而放行）。
- 管理员仍可停止服务（`sc stop AppLockService`）——这是任何用户态方案都无法阻止的，本项目的目标是防止普通使用者/其他账户误开或窥探，不是对抗本机管理员。
- 服务以 SYSTEM 运行，能挂起以管理员身份运行的程序。
- 密码 / 恢复密钥校验有暴力破解防护：连续错 5 次后开始锁定，10 s 起每次翻倍，封顶 10 分钟（锁定期内正确密码也拒绝）。
- 服务只把解锁请求发给与它同目录的正版 `AppLock.exe`；其他程序即使连上管道也拿不到请求。
- 挂起 / 恢复 / 结束进程前都会校验进程创建时间，PID 被系统复用时不会误伤无关进程。

## 数据位置

| 文件 | 内容 |
|---|---|
| `C:\ProgramData\AppLock\config.json` | 密码/恢复密钥哈希（PBKDF2-SHA256, 10 万次）、被锁程序列表、设置 |
| `C:\ProgramData\AppLock\unlock.log` | 解锁日志（JSON Lines，超过 5 MB 滚动） |
| `C:\ProgramData\AppLock\service.log` | 服务诊断日志 |

## 开发调试

不安装服务、前台运行（不需要管理员，但拦截不了管理员权限的程序）：

```powershell
dotnet build Lock.sln
# 已安装正式服务时，用独立的管道名和数据目录跑开发实例，互不干扰
$env:APPLOCK_PIPE = "AppLock.dev"; $env:APPLOCK_DATA = "$env:TEMP\applockdev"
dotnet build\Debug\AppLock.Service.dll run     # 服务前台运行
dotnet build\Debug\AppLock.dll                 # 托盘程序（绕过 requireAdministrator 清单）
python tools\pipe_test.py                      # 协议端到端测试（模拟托盘程序，锁定 charmap.exe）
```

更新已安装的版本：以管理员运行 `tools\update.ps1`（停服务 → 复制 `publish\` → 启服务 → 启托盘 → 导出日志到 `logs\`）。
# Lock
