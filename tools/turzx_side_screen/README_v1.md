# TURZX Side Screen v1

自研 TURZX 副屏 v1 采用混合路线：

- Python `metrics_agent.py` 提供实时 JSON 快照，不直接读取 PostgreSQL。
- C# renderer 用 `System.Drawing/GDI+` 画 480x1920 bitmap。
- C# protocol 层只用 `RJCP.SerialPortStream` 推送 COM7，禁止回退普通 `SerialPort`。
- 屏幕目标 1 秒刷新；数据采集可以更快，但渲染循环会复用上一帧数据，避免慢采集阻塞 COM 推屏。

## UI

最终候选稿：

- `tools\turzx_side_screen\design\dashboard-final-candidate.svg`
- `tools\turzx_side_screen\design\dashboard-final-candidate.png`

运行时不解析 SVG；SVG 只作为布局蓝本。

## JSON Snapshot

默认接口：

```text
GET http://127.0.0.1:18765/snapshot
```

稳定顶层字段：

```text
time, weather, alert, foreground_app, cpu, gpu, fps, memory, disks, network, top_processes, health
```

所有数值单位固定在字段名或 renderer 中约定：

- 温度：摄氏度
- 功耗：W
- CPU 使用率：Windows `% Processor Utility`（任务管理器口径）；不可用时回退 `GetSystemTimes`
- 频率：MHz，UI 可显示 GHz
- 电压：V
- 网络：只统计 `network.publicInterface` 指定的物理公网出口网卡；不汇总 TUN、Tailscale、Hyper-V、VMware 等虚拟接口，避免代理流量被重复计算。单位为 B/s 或 KB/s，renderer 负责格式化。若指定接口不存在则速率失败关闭为空，不回退到全网卡总和。
- DPC：Windows `% DPC Time`

## Current Build Order

1. `metrics_agent.py`：先验证 JSON schema 和磁盘动态枚举。
2. `TURZX.SideScreen.Renderer.cs`：先生成 PNG 预览。
3. `TURZX.SideScreen.Protocol.cs`：先跑纯编码测试，不默认打开 COM7。
4. 主程序集成后再做 COM7 实机推屏测试。

## Safety

- 不修改 `E:\Projects\Tools\TimeAudit` 原项目。
- 不上传 Google Drive，直到用户明确说最终完成。
- 实机推屏前先停掉占用 COM7 的 TURZX 原程序。
