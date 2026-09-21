# 观察、动作与轨迹协议

[English](PROTOCOL.md) | 简体中文 | [Linux 使用说明](README.zh-CN.md)

Linux 接口通过文件桥暴露原游戏决策，自带 Python 控制器在文件桥外接入 JSONL 策略进程。本页说明控制器、数据处理和回放适配使用的协议。

| 数据 | Schema |
| --- | --- |
| Linux 观察 | `sts2-observation-v1` |
| Linux 动作日志 | `sts2-action-log-v1` |
| Mac 语义事件 | `sts2-gui-semantic-v1` |
| 导入回放包 | `sts2-replay-bundle-v1` |

## 观察与策略响应

观察包含以下字段：

| 字段 | 含义 |
| --- | --- |
| `schema` | 观察 schema 版本。 |
| `processRunId`、`decisionId` | 当前进程与决策身份。 |
| `state` | 当前阶段及玩家可见状态，包含该阶段的卡牌、目标、选项和资源信息。 |
| `actions` | 当前可用选择，每项包含 `id`、`kind`、`label` 和 `detail`。 |

策略从 stdin 每行接收一个观察，通过 stdout 返回含 `actionId` 的 JSON 对象，其值从当前 `actions[].id` 中选择。示例策略还使用 `controller`、`reason` 等字段，控制器读取 `actionId`。见[策略接入](README.zh-CN.md#接入自己的策略)。

`actions[].detail` 保留选择对应的卡牌、目标 CombatId、选项、费用等身份。卡牌 `instance` 在进程内稳定；跨进程需要一一对应映射，卡牌显示位置可能随观察变化。

策略观察包含玩家可见状态。回放种子和起点存档属于初始化元数据，与策略观察分开。

## 文件桥

会话 `runtime.json` 指向 `evidenceRoot`，文件桥位于 `evidenceRoot/runtime-work/bridge/`。引擎原子写入观察和状态文件。

1. 读取 `observation.json`，记住其 `(processRunId, decisionId)`。
2. 从该观察中选择动作。
3. 在文件桥目录写入临时文件，原子重命名为 `action.json`，携带观察的进程/决策身份和所选动作 ID。
4. 检查 `heartbeat.json` 中增加的 `submittedCount`，以及 `rejection.json` 中是否有匹配的拒绝请求。
5. 读取具有新身份的下一决策，或从 `terminal.json` 取得游戏结束/动作预算结束结果。

以下请求使用示意身份值：

```json
{"processRunId":"example-process","decisionId":1,"actionId":"example-action"}
```

引擎拒绝过期、重复、进程不符或不可用的动作。游戏状态变化可能使观察失效，因此按最新决策提交。使用单一控制器，每次仅发送一个请求。自建控制器可替换 [sts2_play.py](scripts/sts2_play.py) 的决策/提交循环，或复用其启动流程后接入自己的文件桥读取程序。

`heartbeat.json` 提供 `decisionId`、`phase`、`waiting`、`elapsedSeconds` 和 `submittedCount`；`waiting` 区分等待控制器输入和等待游戏推进。匹配的 `rejection.json` 包含请求及原因，`terminal.json` 包含 `outcome`、`submittedCount` 和终点状态。

## 决策边界

每个玩家输入都是一个决策，包括嵌套选牌中的选择、取消选择和确认。`completed` 表示该输入到达了实际后继边界。如果后继阶段是 `hand_selection`、`grid_selection` 或 `choose_card`，继续执行该阶段提供的选择以完成交互。

自动抽牌、伤害、敌方行动和遗物触发反映在后继状态中。选牌结果描述输入序列之后引擎接受的内容。数据处理时保留父动作、嵌套输入和最终结果之间的关联。

## 导出动作日志

[schemas/action-log-v1.json](schemas/action-log-v1.json) 与 [scripts/protocol.py](scripts/protocol.py) 定义每行 JSONL：

| 字段 | 含义 |
| --- | --- |
| `schema` | `sts2-action-log-v1`。 |
| `rootRunId`、`processRunId` | 根运行和实际引擎进程身份；引擎未到达观察阶段时保留启动尝试身份。 |
| `slAttempt`、`recovery` | 本版本为 `0` 和 `null`。 |
| `sequence` | 输入序号；启动失败/中断可为 `0`，多行生命周期记录共享一个序号。 |
| `status` | `initiated`、`accepted`、`delivered`、`completed`、`rejected`、`failed` 或 `interrupted`。 |
| `action` | 动作 ID 和决策 ID；已接受动作包含观察中的 kind、label 和 detail。 |
| `observation` | 发起/接受时的动作前观察，或完成时的后继；其他状态可为 null。 |
| `error` | 拒绝、失败或中断原因，其余为 null。 |

`initiated` 记录控制器提交，`accepted` 记录文件桥校验通过，`delivered` 记录原回调已调用，`completed` 需要实际后继。导出按序号和生命周期状态排序，原始控制器/引擎日志保留事件实际顺序。

整理训练转移时，按 `(processRunId, sequence)` 分组，将 accepted 中的观察/动作与 completed 后继配对。completed 后继包含 `state` 和 `actions`；动作前观察还包含 schema 与决策身份。选择数据集样本时，单独保留拒绝、失败和中断标签。

## 时间预算与连续性

Linux 人工控制器允许等待输入 900 秒。已接受动作有 40 秒到达稳定后继。引擎监督器的墙钟预算为 1200 秒，外层控制器包含启动和退出的预算为 1500 秒。策略进程具有自己的响应与资源限制，见 [Linux 使用说明](README.zh-CN.md#接入自己的策略)。

动作预算结束导出为 `interrupted`，原因为 `budget_truncated`。退菜单、读档和重启产生连续性边界。Continue 回放从提供的存档开始，携带 `initial_resume_unverified`；将该边界视为 episode 起点。模型 checkpoint resume 恢复训练状态。

## Mac 输入映射

[import_trace.py](scripts/import_trace.py) 将原始 Mac 语义事件转换为回放包，选取 `--actions N` 指定的完整前缀，从 `actionSequence=1` 开始，保存原始文件哈希和记录终点。每个输入都需要接受/回调证据与后继，事件应连续且属于同一进程/运行身份。

回放包包含记录中的命令与观察、种子、起点模式，以及 Continue 的初始存档哈希。材料准备见 [Mac 到 Linux 回放](../docs/REPLAY.zh-CN.md)。不支持的事件和未绑定的 `next_act` 输入会产生导入错误，标识需要适配的部分。

回放包保留 `sourceRaw` 原始字节的 UTF-8 表示、`sourceSha256`、实际 `sourceEventRange`、`sourceActionCount` 和逐项 `transformations`。命令含 `macActionSequence`、`macEventSequence`、`parentActionSequence`、`role` 和 `conversionReason`；`role` 区分 `player_input` 与 `nested_input`。内部 `engine_notification` 只作为证据，不生成 submit。通知必须有 `notificationSequence`、`parentActionSequence`、`attributionRevision=callback-scope-v1`、`relation=synchronous_callback` 及配对的 `callback_entered/callback_returned`，且完整位于原 Proceed 回调内。旧轨迹只允许显式来源绑定的专项适配，见[回放指南](../docs/REPLAY.zh-CN.md#既有跨幕轨迹的专项适配)。

最终比较从实际 `action_response` 读取最后一个后继，验证序号、唯一性及其状态与 `terminal.json` 一致，再比较终点的完整状态和合法动作。Mac 导出的 v2 报告继续区分结构完整性、动作适配要求和未执行的 Linux 回放；导入成功不写入实测通过结论。

## 回放比较

比较器保留事件选项、奖励、费用、资源、卡牌升级/效果和转场状态。跨进程映射卡牌实例，按身份匹配重排的网格；新出现的奖励卡允许按唯一的完整机械属性匹配，候选有歧义时比较失败。

已定义的展示规范化包含 Neow 问候、Tezcatara 问候前缀、火堆同义展示文本、完整网格卡牌标签排列，以及缺失的 NCard 展示节点。原始观察保留不变，具体转换见 [compare_gui.py](scripts/compare_gui.py)。

Mac 拖牌期间 `end_turn` 可用性差异单列为时机差异。回放默认拒绝；`--allow-timing-differences` 允许这一情况，并单独保留 `strictDecisionTimingMatched: false` 结果。其他状态/动作差异停止比较，输出字段见[回放说明](../docs/REPLAY.zh-CN.md#读取结果)。
