# 协议 v1 与连续性

版本：发行 `v0.1.0-alpha` / `ver0.1`；动作日志 `sts2-action-log-v1`；玩家观察 `sts2-observation-v1`；回放包 `sts2-replay-bundle-v1`。破坏字段语义的变更必须提高 schema 版本。

## 动作与观察

运行中的文件桥在每次私有运行目录 `runtime-work/bridge/`。`observation.json` 给出 `processRunId`、`decisionId`、`state` 和 `actions`。动作请求为三个字段：

```json
{"processRunId":"synthetic-process","decisionId":1,"actionId":"synthetic:choose"}
```

这是合成格式示例，不是有效游戏动作。只能提交当次观察列出的唯一 `actionId`；重复、过期、错误进程或非法动作会拒绝，不增加执行数。`actions[].detail` 保留当时的卡牌、目标 CombatId、选项、费用等身份。卡牌 `instance` 仅在录制进程中稳定；跨进程需双向一一对应，不能把手牌/网格显示序号当成永久身份。

每次选择是一条实际输入，包括嵌套弃牌、选牌与确认。`completed` 表示获得该输入之后的稳定决策边界；后继 `state.phase` 为 `hand_selection`、`grid_selection`、`choose_card` 等时，父动作仍需这些嵌套选择才能完成整个游戏结算，不能把它误读为原引擎异步任务已完成。

自动抽牌、伤害、敌方行动、遗物触发、Spiral 自动重放属于结果，不生成新的玩家输入。观察仅投影玩家可知状态；不输出隐藏 RNG、未知抽牌顺序、完整引擎状态、未来结果或 restore handle。seed 与初始存档在启动配置/特权谱系中，策略接收的观察不包含它们。

## 导出动作日志

每行由 `schemas/action-log-v1.json` 和 `scripts/protocol.py` 定义：

| 字段 | 含义 |
| --- | --- |
| schema | 固定 `sts2-action-log-v1` |
| rootRunId / processRunId | 根运行与实际游戏进程身份；未进入游戏时保留启动尝试身份 |
| slAttempt / recovery | 本版固定 0 / null；没有实现玩家 SL 或游戏进程恢复 |
| sequence | 实际输入序号；失败/中断可为 0；不是日志行号 |
| status | initiated、accepted、delivered、completed、rejected、failed、interrupted |
| action | 动作 ID、decisionId；接受后包含当次观察的 kind、label、detail（含目标与选择） |
| observation | 发起/接受的前置玩家观察，或 completed 的稳定后继；不含特权验证日志 |
| error | 拒绝、失败、中断时必须有原因，其余为 null |

`initiated` 来源于外层实际提交；`accepted` 是引擎桥校验后的 `action_submitted`；`delivered` 表示原回调已调用；`completed` 需实际后继。导出按动作序号和生命周期排序；原日志保留实际时序。不能只数 initiated 就声称执行成功。JSON schema 和无游戏测试为合成合同检查，不能替代游戏验证。

预算结束是 `interrupted/budget_truncated`，进程异常、观察失败和输入缺失为明确失败。人工等待 900 秒、动作无稳定后继 40 秒、游戏总墙钟 1200 秒，三者含义不同；游戏 CPU 上限 1000 秒，采样聚合 RSS 上限 4 GiB。磁盘写入有监督上限，非硬配额。

退菜单、读档、进程重启及显式 Continue 的先前连接均为连续性未验证。初始 Continue 夹具允许验证从加载后开始的一段连续输入，不能证明加载前后状态无损；模型 checkpoint 也不能恢复游戏。预算、失败与已获知识不得随回滚消失。

## Mac 导入与比较

导入器读取现有 `sts2-gui-semantic-v1` 原始事件流，要求连续 eventSequence、从 actionSequence=1 开始、每个输入有接受/回调记录和后继、无跨进程/未知输入。`--actions N` 显式导出完整前 N 输入及第 N 个实际后继；不搜索未来 checkpoint 补动作。原始文件保留不变，回放包记录其哈希。

旧 Mac 0.2.2 的 `next_act` 没有可靠父子归属，通用导入会明确拒绝；历史 157 输入结论使用过有证据的专项转换，未作为通用豁免。未知 UI 输入同样拒绝；本版不猜测它是否纯展示。

比较保留事件选项、奖励、费用、资源、卡牌实例映射、升级/附魔/污染/锁牌、跨幕状态。网格显示重排按已知实例匹配；首次奖励卡仅允许完整机械属性唯一匹配，重复候选会失败。

仅规范化已审查的 Neow 问候、Tezcatara 问候前缀、火堆同义展示格式、完整网格卡牌标签排列和缺失 NCard 展示节点；原观察不修改。Mac 拖牌时的 end_turn 可用性差异单列为时机差异，默认拒绝；显式 `--allow-timing-differences` 只允许有记录的机械比较，结果中 `strictDecisionTimingMatched=false`。不可将其称作严格 GUI 输入一致或整局保真。

所有真实输出默认私有；玩家可见投影仍可能包含用户游玩数据，并非自动获得公开许可。策略在单独 namespace 中仅接收观察与可选模型，游戏、存档和特权日志不挂载给它。
