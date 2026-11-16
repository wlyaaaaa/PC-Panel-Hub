# 实况数据

`live.contract.json` 交给通用读取器；来源只用别名，本机路径登记在共享后端，不进公开仓库。

- 现有 `metrics_agent.py` 每次完成原采样后，顺带写 `out/live.snapshot.json`。只投影类别计数、来源状态、既有发送心跳、窗口保护状态和预览元数据，不新增采样、任务或常驻进程。
- 刷新拆解与发送数共用最近一帧：从现有主心跳与 A/B 副本挑源时刻最新的可读副本，避开主文件替换时的共享锁，投影到同一份运行小输出。空跑数不计，主机写出不等于实体屏收到。
- 旧 `TotalMoves` 同时包含曲面屏与 VDD；新窗口保护另外输出 `HS2TotalMoves`。旧历史已截断时曲面屏总数给空值，不能把合计冒充曲面屏数。
- `python tools/turzx_side_screen/panel_live.py` 按需更新静态规模。先暂存本次新增源码；只统计 Git 跟踪的自有文档和源码（含测试），内容指纹没变就保留原 `at`。公开快照不含运行中的本人数据。
- 预览只有当前一张异步诊断图；公开给有无文件和生成时刻。原采样器按图片实际修改时刻生成本人产物，`open` 只登记 `source: panel.preview`，不填 `href`；共享后端以 `exists` 别名确认文件后给本人“在电脑上打开”的入口，访客不出这条。
- 本轮只交源码，运行件未替换。合入后由既有采样器下一次正常启动加载小输出；未加载前保留接线缺口，不把手工输出当作自动更新已生效。

相关无硬件写入检查：`python tools/turzx_side_screen/test_panel_live.py`、`pwsh -NoProfile -File scripts/test.ps1 -SkipStreamWhenRunning`。源码测试不能替代实体屏刷新、睡眠恢复或断电验收。
