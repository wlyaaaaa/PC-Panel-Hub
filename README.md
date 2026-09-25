# PC Panel Hub

面向 Windows 的本地副屏方案，包含两套职责明确、彼此独立的显示界面：

- **TURZX 480×1920 USB 机箱屏**：显示 CPU/GPU 温度、核心平均频率与电压、FPS、天气、物理磁盘 I/O、物理公网出口的上传/下载与网络质量、前台应用和进程排行等密集遥测信息；不把 TUN、Tailscale、Hyper-V、VMware 等虚拟接口重复计入公网流量。
- **LIAN LI HS2 2288×1048 曲面 OLED**：可选的事件型透明叠加层，用于速览、媒体信息、Steam 会话、手机状态、运维、任务和可操作告警；它不是另一块密集遥测面板。

HS2 的设计、数据来源、配置方法和明确限制见 [docs/hs2-crystal-overlay.md](docs/hs2-crystal-overlay.md)。

## 主要组成

同一指标服务的 `GET /public-telemetry` 提供 `turzx.public-telemetry.v1` 窄投影，只有网络探测与 DPC 占用，不返回目标地址、网卡名、前台、进程、账号或原始错误。该入口复用现有采样器，不构建 `/snapshot`，也不另开采集循环。

公开网络字段 `latency_ms` 是最近探测 RTT，`packet_loss_percent` 是最近至多 15 次尝试的失败比例；`attempt_count`/`success_count` 和 `window_start_unix`/`window_end_unix`/`window_seconds` 给出实际样本窗。`jitter_ms` 是最近至多 15 次成功 RTT 的相邻绝对差平均，另带 `jitter_sample_count`、`jitter_window_*`、`jitter_observed_at_unix` 与 `jitter_status`；不足两次成功时公开值为 null。探测失败包括超时或工具不可用，不等同于已确定网络丢包原因。

网络仍按原 2 秒 TTL 异步单飞，响应不等待 Ping；`connecting`、`stale`、`unavailable` 不改写旧来源时间。`observed_at_unix` 是最近尝试完成时间，抖动保留最近成功样本时间。`system.dpc_usage_percent` 是真实 PDH DPC 时间占比及其读取时间，不是 DPC 延迟；计数器基线未就绪或读取失败时值和时间均为 null。原 `/snapshot` 合同保留。

- Python 指标代理：采集硬件、网络、磁盘、天气、FPS、前台应用和进程排行。
- C# / GDI+ 渲染器：生成 `480x1920` 仪表盘并发送到 TURZX 屏幕。
- 两种受控的 `COM7` 传输模式：
  - 已验证的 command `200` 全帧路径，默认周期为 3 秒；
  - 显式启用的 1 Hz 混合候选路径：按厂商行为完成 command `200` 启动/恢复基线，再发送有界的 command `204` 差分数据。
- 睡眠与关机协调：主 watchdog 收到电源事件后请求 HS2 原生离线时钟和 TURZX 亮度关闭；关机或重启时请求关闭两块屏幕输出。Windows 的异步电源通知不保证处理在挂起前完成，实际效果须按本机睡眠模式验收。
- 有界 JSONL 诊断，以及明确的数据来源、陈旧和错误状态。
- 以最高运行级别启动的 Windows 计划任务。

## 协议与证据边界

本项目的协议实现来自本地黑盒验证和厂商程序行为观察，并不是厂商发布的正式 SDK。

- command `200` 全帧发送和 command `123` 亮度控制已有本机协议验证，是保守路径。
- command `204` 仅是设备特定的混合刷新候选。它是有界、可关闭、可回退的实现，不应描述为通用且已验证的公开协议。
- 串口写入成功、进程存活、计划任务运行或心跳递增，只能证明主机侧发送链路在工作，**不等于设备 ACK，也不能单独证明实体像素已刷新或没有冻结**。
- 1 Hz、画面冻结和睡眠/恢复等最终效果仍需在实体屏幕上观察验收。
- `-AltHelper` 只保留用于隔离协议测试；现有现场证据不支持将它用于这块屏幕的日常链路。

更底层的编码说明见 [tools/turzx_side_screen/README_protocol.md](tools/turzx_side_screen/README_protocol.md)。

## 当前状态与依赖

这是从实际本地配置中整理出的早期 Windows 优先项目，协议与界面仍偏实用实现，不是成熟 SDK 抽象。

已知前提：

- TURZX 显示尺寸：`480x1920`。
- 默认串口：`COM7`；运行时需要独占对应串口。
- 操作系统：Windows。
- 建议 Python 3.11 或更高版本。
- 渲染器和串流程序需要 .NET Framework 编译器 `csc.exe`。
- 常规回归还需要 .NET 10 SDK，以运行协议编码与 HS2 核心测试。Python 可选硬件采集依赖列在 `tools/turzx_side_screen/requirements.txt`，可用 `python -m pip install -r tools/turzx_side_screen/requirements.txt` 安装；各来源缺失仍按原有能力状态报告。
- 硬件指标建议使用 NVIDIA NVML 和 LibreHardwareMonitor。
- FPS 来自可选的 TimeAudit 帧链：优先读取 RTSS 官方共享内存，顺序为精确前台、RTSS 最近前台、用户启用的 Wallpaper 桌面 renderer 和唯一新鲜帧源；RTSS 映射不可用时才回退 PresentMon。副屏仍只通过 `TIMEAUDIT_DSN`，或同时设置的本机 `TIMEAUDIT_DB_USER` 与 `TIMEAUDIT_DB_PASSWORD` 读取 PostgreSQL；代码不内置任何数据库用户名，两者缺一时不启用 FPS 数据库读取。副屏不直接依赖 RTSS，也不保存数据库密码。遗留的本机 `127.0.0.1:55432` DSN 会在内存中迁移到避开 Windows 动态端口池的 `45432`，不会回写秘密。
- RTSS 映射可用但没有新鲜帧源时显示正常等待，不把 Wallpaper 的 GPU 负载误报成采集异常；状态缺失、数据过期或 RTSS/PresentMon 均不可用时才显示异常。
- DPC 显示值来自 Windows `Processor Information(_Total)\% DPC Time`，不是合成的调度延迟指标。
- 物理磁盘会按其盘符合并；名称为 `RECOVER` 的卷、虚拟盘、RAM 盘，以及小于 `32,000,000,000` 字节的 USB/可移动介质会被排除。
- 天气适配器不内置城市或坐标。默认读取被 Git 忽略的本机 `config.json`；也可用 `TURZX_WEATHER_CONFIG` 指向外部私有文件，或同时注入 `TURZX_WEATHER_LATITUDE` 与 `TURZX_WEATHER_LONGITUDE`。公开源码包只保留无实际值的 `config.example.json`。

首次配置时复制 `tools\turzx_side_screen\config.example.json` 为同目录
`config.json`，再填写本机串口、物理公网接口和天气经纬度。`config.json`
会被 Git 忽略；`start_turzx_weatherfix.ps1` 在未显式设置
`TURZX_WEATHER_CONFIG` 时会自动使用这份本机文件。示例中的天气坐标为
`null`，未填写时天气 shim 失败关闭，不会回落到作者位置。

串口优先级为显式 `-Port`、`serial.port`、兼容默认 `COM7`。正常串流、单帧和亮度入口在打开串口前核对唯一的 `VID_0525&PID_A4A7` 设备与健康状态，并拒绝已有帧流时另开写入者；COM 号变化时不会自动猜测其他端点。新安装任务未显式传 `-Port` 时会在每次启动读取配置；旧任务若固定了不同端口，启动器会说明冲突，应重装同一任务以采用配置。

`480×1920` 几何、驱动速率及布局由当前渲染/传输实现固定，不是可配置项。示例已移除无消费者的 `serial.baudRate`、串口驱动选择、`screen.width/height` 和 `ui.*`；旧本地文件保留这些字段也不会改变画面。窗口返回策略目前针对唯一 `PHLC34B` 主屏与 `MTT1337` VDD；缺失或歧义时保持零动作并记录预期标识，不会凭分辨率将陌生显示器当成主屏。

公开仓库不包含原版 TURZX 二进制。启动串流前，需在仓库根目录旁准备：

- `RJCP.SerialPortStream.dll`
- `TURZX.exe` 或 `TURZX.weatherfix.metrics.exe`

## 快速开始

先检查本机运行依赖：

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File .\scripts\check-runtime.ps1
```

直接启动：

```text
start-side-screen.cmd
```

或从 PowerShell 启动：

```powershell
Set-Location 'C:\path\to\PC-Panel-Hub'
powershell -NoProfile -ExecutionPolicy Bypass -File .\scripts\start.ps1 -IntervalMs 3000
```

若机箱副屏已经冻结或计划任务意外退出，使用快速修复入口；它会先核对
配置串口是否仍精确绑定 `VID_0525&PID_A4A7` 且设备为 Present/OK，再以唯一
串口写入者和固定 1 Hz 混合刷新重启，并等待新心跳验收：

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File .\scripts\repair-panel.ps1
```

个人启动和开机任务默认固定为 1 Hz 混合刷新，不再以降低到 3 秒刷新作为稳定性修复。启动或看门狗重启后会在第 60、120、180 帧（`60, 120, and 180`）各重建一次串口会话、重新 prime/恢复亮度并发送完整 command `200` 基线，以纠正“主机心跳正常但实体屏没有接收新会话”的静默冻结；三分钟后恢复为连续的每秒 command `204` 增量刷新，并保留每 900 帧一次的长期全帧恢复。每次全帧纠偏预计短暂停顿约 2.5 秒，但不会把常态刷新改成 3 秒。仅在明确诊断兼容性时才用 `-HybridRefresh:$false` 进入 **3-second compatibility fallback**。

看门狗在连续 3 次子进程退出或心跳故障后进入 30 秒有界熔断，再确认旧流进程已经释放 COM 后重新启动；即使第一次重启尝试本身抛错，也只生成失败回执并留在常驻循环内继续恢复，不会再退出并让计划任务长期停在 Ready。睡眠/关机即使遇到旧流退出证明失败，也会继续执行 HS2 电源策略；而正常启动仍拒绝在旧串口写入者未退出时创建第二个流。

HS2 水冷屏的恢复入口先核对已绑定专用 Hub 下的控制器身份。正常 `A068` 或 `AD23` 端点出现后才调用 L-Connect；端点缺失或仅出现 `A108:EAEF` Boot ROM 身份时只读等待，不发送模式命令，也不重启 Hub、删除设备或扫描 PnP。[LIAN LI 官方说明](https://lian-li.com/product/hs2-oled-curved/)要求 OLED Curved 的 USB 主线直连主板 USB 2.0 9-pin 排针（或官方 EDGE HUB），随附 1 分 2 Hub 只给非 LCD 设备使用；主板 USB 供电不足时按[官方接线手册](https://drive.google.com/file/d/100nRyDLIbXY8mkVBAG5gv92xSe4A7tpN/view?usp=sharing)使用随附 SATA 辅助供电。若纠正接线使 8091 的 Windows 实例号变化，看门狗只接受唯一的 8091 + port 2 控制器 + port 3 LED 拓扑，待 AD23/MI_00 完整验证后原子替换旧绑定。端点恢复后会自动重新进入保留模式、L-Connect 绑定和浮层验收，无需重启 watchdog。内部 USB 排针不得带电插拔。

为兼容既有安装，Windows 计划任务、快捷方式及本机脚本中的内部标识仍保留 `TURZX SideScreen`；这不再是公开项目名称，也无需为改名迁移现有运行路径。

安装开机启动任务：

```text
install-startup.cmd
```

或从管理员 PowerShell 安装：

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File .\scripts\install-startup-admin.ps1 -IntervalMs 3000
```

卸载开机启动任务：

```text
uninstall-startup.cmd
```

或从管理员 PowerShell 卸载：

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File .\scripts\uninstall-startup-admin.ps1
```

HS2 Code 43 的人工恢复入口默认只检查已保存的健康拓扑绑定：

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File .\scripts\repair-hs2.ps1
```

仅在明确需要硬件恢复时，以管理员运行同一入口并加 `-Apply`。它会显示确认，再复核唯一专用 Hub、port 2 故障子节点与 LIAN LI sibling；缺失或歧义时拒绝。该路径可能重启专用 Hub、移除精确故障子节点和扫描设备，永远不接入普通 watchdog。TURZX 的既有自动恢复仅在失败阈值、退避与停流证明成立时重启自身串口端点；二者作用域不同。

## 测试、构建与状态检查

运行测试并生成渲染预览：

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File .\scripts\test.ps1
```

构建源码发布包：

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File .\scripts\build-release.ps1
```

查看启动任务状态：

```powershell
Get-ScheduledTask | Where-Object { $_.TaskName -like '*TURZX*' } |
  Select-Object TaskName,State,@{Name='RunLevel';Expression={$_.Principal.RunLevel}}
```

常规入口包含协议编码、HS2 .NET 核心、指标采集、启动策略与渲染测试，以及设计稿校验和差分探针干跑（后者要借用本机的 TURZX 厂商程序集转换帧，公开检出没有该程序集时显示 SKIP），均不接触实体串口。设计稿 PNG 预览需要本机无头浏览器，只在显式 `TestFinalDesign.ps1 -RenderPreview` 时生成。源码发布包只取 Git 已跟踪文件集合，使用当前工作树内容；未提交的已跟踪修改仍需在发布前审阅，未跟踪笔记不会进入包。

`-IntervalMs 3000` 控制全帧兼容周期；启用混合模式时生产周期固定为 1 Hz。`start.ps1` 与安装器可用 `-FullFrame` 显式选择全帧模式，兼容直接 PowerShell 调用的 `-HybridRefresh:$false`。快速修复入口维持既有 1 Hz 混合模式。

测试通过只能证明代码和主机侧契约满足预期；涉及串流、断电、睡眠、恢复或画面刷新的结论，仍需另做实体验收。先用 `powercfg /a` 确认本机支持的睡眠类型；不把未支持的 S0ix 当作失败。窗口返回的独立实机检查为 `scripts\TestDesktopWindowReturn.ps1 -Live -ResultPath <本机结果路径>`，可显式指定主屏/VDD 硬件 ID；它创建测试窗口，因此不在常规回归中自动运行。遗留 `RestartSideScreenAfterResume*` 只为旧安装诊断保留，禁止重新注册 Resume 任务。

## 运行日志

生成文件不会纳入 Git：

- `tools\turzx_side_screen\out\stream\stream-last.png`
- `tools\turzx_side_screen\out\data-trust.jsonl`
- `tools\turzx_side_screen\out\side-screen-stack.log`
- `tools\turzx_side_screen\out\top-processes.json`

日志和心跳可用于诊断主机侧状态，但不要将其中的机器标识、设备拓扑或本地路径原文提交到公开仓库。

## 目录结构

```text
scripts/                       安装、启动、测试和发布包装脚本
docs/                          项目文档
tools/turzx_side_screen/       指标代理、渲染器、串流程序和测试
tools/turzx_weather_shim/      本地天气请求使用的天气适配器
tools/hs2_crystal_overlay/     HS2 叠加层、网易云桥接和测试
```

原版 TURZX 厂商二进制和本机运行目录会被有意排除在 Git 之外。

## 许可

仓库中的源码采用 MIT License，见 [LICENSE](LICENSE)。第三方/厂商二进制及 TURZX 原版应用文件不属于本仓库的开源许可范围，不应提交或随源码发布。
