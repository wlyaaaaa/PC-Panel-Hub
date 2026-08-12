# PC Panel Hub

面向 Windows 的本地副屏方案，包含两套职责明确、彼此独立的显示界面：

- **TURZX 480×1920 USB 机箱屏**：显示 CPU/GPU 温度、核心平均频率与电压、FPS、天气、物理磁盘 I/O、网络质量、前台应用和进程排行等密集遥测信息。
- **LIAN LI HS2 2288×1048 曲面 OLED**：可选的事件型透明叠加层，用于速览、媒体信息、Steam 会话、手机状态、运维、任务和可操作告警；它不是另一块密集遥测面板。

HS2 的设计、数据来源、配置方法和明确限制见 [docs/hs2-crystal-overlay.md](docs/hs2-crystal-overlay.md)。

## 主要组成

- Python 指标代理：采集硬件、网络、磁盘、天气、FPS、前台应用和进程排行。
- C# / GDI+ 渲染器：生成 `480x1920` 仪表盘并发送到 TURZX 屏幕。
- 两种受控的 `COM7` 传输模式：
  - 已验证的 command `200` 全帧路径，默认周期为 3 秒；
  - 显式启用的 1 Hz 混合候选路径：按厂商行为完成 command `200` 启动/恢复基线，再发送有界的 command `204` 差分数据。
- 睡眠与关机协调：睡眠时 HS2 使用原生离线时钟，TURZX 使用已验证的亮度关闭命令；关机或重启时关闭两块屏幕输出。
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
- 硬件指标建议使用 NVIDIA NVML 和 LibreHardwareMonitor。
- FPS 来自可选的 TimeAudit/PresentMon 链，通过 `TIMEAUDIT_DSN` 启用，不需要 RTSS 集成；仓库不保存数据库密码。
- 新鲜但全为零的 FPS 样本表示“等待游戏帧”，不等于采集故障；连接中、陈旧和错误状态会单独显示。
- DPC 显示值来自 Windows `Processor Information(_Total)\% DPC Time`，不是合成的调度延迟指标。
- 物理磁盘会按其盘符合并；名称为 `RECOVER` 的卷、虚拟盘、RAM 盘，以及小于 `32,000,000,000` 字节的 USB/可移动介质会被排除。

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
powershell -NoProfile -ExecutionPolicy Bypass -File .\scripts\start.ps1 -Port COM7 -IntervalMs 3000
```

个人启动和开机任务默认使用 command `200` 全帧模式，每约 3 秒更新一次。现场长期运行已证明 command `204` 可能在主机心跳仍正常时让实体屏静默停止刷新，因此 1 Hz 混合模式仅保留为显式候选；需要 1 秒时钟时可传入 `-HybridRefresh`。该模式启动时保留厂商式双全帧基线，之后每 900 帧重建一次串口会话、重新 prime/恢复亮度并发送一次完整 command `200` 基线；正常每秒刷新，周期恢复边界预计短暂停顿约 2.5 秒。

看门狗在连续 3 次子进程退出或心跳故障后进入 30 秒有界熔断，再确认旧流进程已经释放 COM 后重新启动；不会再以成功退出的方式永久停在“paused”状态。睡眠/关机即使遇到旧流退出证明失败，也会继续执行 HS2 电源策略；而正常启动仍拒绝在旧串口写入者未退出时创建第二个流。

HS2 水冷屏的恢复入口先核对已绑定专用 Hub 下的控制器身份。正常 `A068` 或 `AD23` 端点出现后才调用 L-Connect；端点缺失或仅出现 `A108:EAEF` Boot ROM 身份时只读等待，不发送模式命令，也不重启 Hub、删除设备或扫描 PnP。[LIAN LI 官方说明](https://lian-li.com/product/hs2-oled-curved/)要求 OLED Curved 的 USB 主线直连主板 USB 2.0 9-pin 排针（或官方 EDGE HUB），随附 1 分 2 Hub 只给非 LCD 设备使用；主板 USB 供电不足时按[官方接线手册](https://drive.google.com/file/d/100nRyDLIbXY8mkVBAG5gv92xSe4A7tpN/view?usp=sharing)使用随附 SATA 辅助供电。若纠正接线使 8091 的 Windows 实例号变化，看门狗只接受唯一的 8091 + port 2 控制器 + port 3 LED 拓扑，待 AD23/MI_00 完整验证后原子替换旧绑定。端点恢复后会自动重新进入保留模式、L-Connect 绑定和浮层验收，无需重启 watchdog。内部 USB 排针不得带电插拔。

为兼容既有安装，Windows 计划任务、快捷方式及本机脚本中的内部标识仍保留 `TURZX SideScreen`；这不再是公开项目名称，也无需为改名迁移现有运行路径。

安装开机启动任务：

```text
install-startup.cmd
```

或从管理员 PowerShell 安装：

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File .\scripts\install-startup-admin.ps1 -Port COM7 -IntervalMs 3000
```

卸载开机启动任务：

```text
uninstall-startup.cmd
```

或从管理员 PowerShell 卸载：

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File .\scripts\uninstall-startup-admin.ps1
```

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

测试通过只能证明代码和主机侧契约满足预期；涉及串流、断电、睡眠、恢复或画面刷新的结论，仍需另做实体验收。

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
