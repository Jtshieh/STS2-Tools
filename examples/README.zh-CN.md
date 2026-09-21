# 从轨迹读取一次决策

[English](README.md) | 简体中文 | [项目首页](../README.zh-CN.md)

此示例展示环境与学习流程之间的数据：观察、所选动作及实际后继。仓库内的样例为合成数据，只需要 Python 3.12。

## 试用数据格式

在仓库根目录执行：

```bash
python3 -B examples/read_trajectory.py examples/synthetic-action-log.jsonl
```

命令向 stdout 输出一个 JSON 对象，包含 `observation`、`action` 和 `nextObservation`。样例中的 `synthetic:choose` 将示意金币字段从 0 变为 1。身份与结果用于说明格式，此命令读取文件。

读取器使用仓库的日志校验器，按进程与序号配对 `accepted` 和 `completed` 生命周期记录，并向 stderr 报告缺少后继的输入，以及拒绝、失败和中断结果。选取训练样本时参考这些标签。

## 读取自己的引擎输出

1. 按 [Linux 准备与人工控制流程](../linux/README.zh-CN.md#准备工作区)配置游戏/profile，运行有动作上限的会话。
2. 在每个决策点选择显示的动作编号。控制器将对应 `actionId` 与当前进程、决策身份一起提交。
3. 引擎产生后继且会话结束后，在输出的 `launchEvidence` 目录取得 `action-log.jsonl`。
4. 使用读取器处理该文件：

```bash
python3 -B examples/read_trajectory.py '/absolute/path/from/launchEvidence/action-log.jsonl'
```

程序决策按照[策略接入说明](../linux/README.zh-CN.md#接入自己的策略)替换 `choose(obs)`。策略从当前选择中返回 `{"actionId": selected_action["id"]}`，引擎提供后继。训练代码可读取示例输出的 JSON，自行定义特征和奖励。

人类数据按照 [Mac 录制](../mac/README.zh-CN.md)与 [Linux 回放](../docs/REPLAY.zh-CN.md)取得 Linux 动作日志，再交给读取器。[协议](../linux/PROTOCOL.zh-CN.md)说明嵌套决策和状态字段。
