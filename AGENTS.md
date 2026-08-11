# PC-Panel-Hub 项目规则

本文件只补充本项目特有边界；通用规则沿用上级指令，不在此重复。

## 设备与协议安全

- `COM7` 只是当前默认值，不是可泛化的设备事实。任何实机写入前先确认目标串口和 TURZX 设备身份，并确保只有一个进程持有串口。
- 默认保守传输是 command `200` 全帧路径。command `204` 混合刷新属于显式、设备特定的候选能力，必须保持有界、可关闭和可回退；不得宣称其为通用已验证协议或实体像素 ACK。
- 不为测试随意重置 USB root hub、整棵 USB 树或无关设备。计划任务和普通 watchdog 默认不得重启 Hub、删除设备或执行 PnP 扫描；HS2 Code 43 硬件恢复只能在显式人工 opt-in 下使用健康状态已记录且唯一匹配的专用 hub、端口 2 子节点及 LIAN LI sibling 拓扑，缺失或歧义时失败关闭。
- 只有 L-Connect 回读结果明确为 `Verified=true`，且唯一的 hub、AD23 Windows 显示设备与 LED sibling 拓扑连续两次满足物理存在、`Status=OK`、`ProblemCode=0`，并成功保存同一绑定，才能把 HS2 记为 Active，并据此启动叠加层或窗口保护。
- 睡眠恢复只有长驻主 watchdog 一个 owner；不得另注册会强杀 watchdog、重启 USB 设备或扫描 PnP 的并行 Resume task。旧 `TURZX SideScreen Resume` 任务必须保持禁用。

## 工作区与公开边界

- 这是可能包含既有未提交改动的共享工作树。先看 `git status`，只改任务明确涉及的文件；不得撤销、覆盖、格式化或暂存其他人的改动。
- 公开仓库只收源码、脚本和文档。不得提交厂商二进制、原版 TURZX 程序、大型厂商资源、本地配置、设备拓扑、机器标识、日志正文、截图中的私密信息、凭据或数据库连接秘密。
- 引用厂商行为时写清证据来源和可信度，不把黑盒观察、主机写入成功或历史现场结果描述为官方保证。

## 测试与构建

- 常规回归入口：`powershell -NoProfile -ExecutionPolicy Bypass -File .\scripts\test.ps1`。
- 源码发布包入口：`powershell -NoProfile -ExecutionPolicy Bypass -File .\scripts\build-release.ps1`。
- 优先运行不触碰串口的纯逻辑、编码和渲染测试。任何会打开 `COM7`、切换屏幕模式、重启服务或操作 USB 的测试都必须由当前任务明确授权，并在执行前确认目标。
- 不把构建成功、单元测试通过、计划任务 Running、进程存活、串口写入成功或心跳递增当成实体屏幕验收。

## 后台运行与部署

- 计划任务、watchdog 和其他非交互后台进程必须由父启动器采用 `wscript.exe`、`CREATE_NO_WINDOW`、`windowsHide` 或等价的真正无窗口方式启动；仅设置任务 `Hidden` 或内层 `-WindowStyle Hidden` 不构成充分证明。
- 本地编辑和测试不自动授权安装计划任务、复制部署文件、重启 L-Connect、切换显示模式或写入实体设备。部署需单独明确授权，并记录具体目标与版本/哈希。
- 实体验收单独报告：至少区分主机侧测试、部署状态、TURZX 实际刷新、HS2 模式回读、睡眠/恢复以及异常断电后的自然启动。未观察的项目保持“未验证”，不得用日志推断补齐。
