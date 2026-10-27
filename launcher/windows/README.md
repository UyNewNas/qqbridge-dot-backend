# Windows 管理器

可选的原生 WinForms 管理器，用于在同一窗口启停 SnowLuma、QQBridge、Dot MCP 和 Dot Tunnel，并查看日志。子进程不单独打开终端；Windows Job Object 负责停止进程树。

需要 .NET 10 SDK 构建，运行机器需要 .NET 10 Desktop Runtime；Node.js 24 必须在 PATH 中。

```powershell
.\launcher\windows\build.ps1
$env:QQB_BRIDGE_DIR = 'C:\bots\qq-bridge'
$env:QQB_DOT_BACKEND_DIR = (Get-Location).Path
& '.\launcher\windows\publish\GPTBot管理器.exe'
```

管理器也可以放在同时包含 `qq-bridge` 和本仓库的父目录；后端目录通过 package.json 的包名识别，不要求特定检出目录名。SnowLuma 目录可通过 `QQB_SNOWLUMA_DIR` 指定。

QQBridge 配置 `dot.only=true` 时，管理器展示 SnowLuma、QQ Bridge、Dot MCP、Dot Tunnel，一键启动不会等待或启动 DSH，桥接、MCP 和隧道异常退出后可自动重启。混合模式保留兼容的 DSH 服务项。

“接入状态”逐会话显示订阅覆盖情况。MCP 服务已启动不代表 Secure MCP Tunnel 已连接。Dot Tunnel 由管理器隐藏运行官方 tunnel-client，可单独启动、停止、重连和查看状态；只有官方聚合健康检查 ready=true、控制面正常且无明确 MCP 错误才显示“已连接”；stdio 通道的 MCP 子项可能显示 unknown，不能仅凭该子项判断断线。官方客户端和 state/tunnel-profiles/qq-dot.yaml 需先按 [接入说明](../../docs/dot-mcp-local.md)准备。关闭管理器会停止它启动的服务；外部实例只显示端口状态，不能由它停止。

构建后可运行 `GPTBot管理器.exe --self-test`，检查隐藏进程启动、UTF-8 输出、退出码和子进程清理；结果写入程序旁的 manager-test-result.txt，文件不提交。

主窗口以 96 DPI 为布局基准，按钮和服务行按内容计算大小，日志区使用剩余空间。窗口会适应当前屏幕可用区域（扣除任务栏），在加载、DPI 变化及拖动/调整大小结束时重新检查边界；整个窗口不使用滚动条，日志文本保留原有滚动功能。

自检还会在隔离配置中检查 80 种布局组合：1080p、2560×1440、4K 的 100%/125%/150%/175%/200%，4K 的 250%/300%，以及 1366×768 的 100%/125%/150%，各覆盖四行/五行服务和默认/最小窗口。检查按钮文字、控件边界、至少三行日志空间及窗口工作区边界。这些是当前进程 DPI 下的等效可用空间检查，不会更改系统显示设置；真实显示器的缩放及跨屏拖动仍需人工回归。

## 隧道密钥与重连

点击“隧道密钥”在本地密码框输入有 Tunnels Read / Use 权限的 OpenAI Platform 运行密钥，或在首次启动隧道时输入。密钥仅存当前管理器内存，不写入配置、日志或命令参数。不要在聊天中发送密钥；不使用系统中可能指向模型代理的 OPENAI_API_KEY。

“一键启动”按 SnowLuma → QQ Bridge → Dot MCP → Dot Tunnel 启动；混合模式还会等待 DSH。关闭管理器会停止它管理的进程树，包括隧道；再次启动管理器需重新输入密钥。可使用 `--start-all` 在管理器打开后自动启动这一流程。

勾选“服务异常退出后自动重启”时，隧道进程异常退出后按 5、10、20…最多 60 秒退避重连。手动停止后不会自动拉起；明确的无效密钥错误会停止重试，需更新密钥。网络暂时不可达但进程仍存活时，由官方客户端自身重连，界面显示等待连接或异常。

已有外部隧道进程时只显示外部实例，不强行接管或重复启动。先在它原来的启动位置停止，再点击管理器的启动。管理器直接运行官方 `run --profile-file`，进程归管理器所有；旧的 `runtimes` 别名登记不能代替管理器状态，不要同时使用两个启动入口。

隧道日志在管理器内查看；经过密钥脱敏的副本写入本仓库 state/tunnel-manager.log，单文件约 2 MiB 后轮转。连接状态按钮可打开官方客户端的回环管理后台。

管理器自检还覆盖进程存在与远端连接就绪的区分、陈旧健康地址、密钥环境隔离及界面/磁盘日志脱敏。自检使用本地替身，不需要真实隧道或密钥。
