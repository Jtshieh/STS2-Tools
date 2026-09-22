# 在 Linux 回放 Mac 人类示范

[English](REPLAY.md) | 简体中文 | [项目首页](../README.zh-CN.md)

回放通过 Linux 引擎接口执行已记录的人类选择，比较决策边界的观察。可用于检查示范数据、复现交互，并取得 Linux 动作日志供后续处理。

## 准备起点状态

两端均使用 STS2 v0.111.0 / 41cef1ea / Steam build 24724944：Mac arm64 游戏用于录制，Linux x86_64 游戏用于回放。各平台使用各自的文件哈希清单。

录制前选择起点边界：

| 起点 | 需要保留的材料 |
| --- | --- |
| Continue | 初始 `current_run.save`、账户 `profile.save`，以及当前 profile 的 `progress.save` 和 `prefs.save`。 |
| 新局 | 匹配的 profile 解锁进度、Silent 角色、进阶 0、Standard 模式、教程设置和记录种子。 |

Continue 可通过 [Mac 独立工作区启动器](../mac/README.zh-CN.md#可选独立游戏与-profile-工作区) 在会话的 `initial-profile/` 中保存精确的启动前存档，从中选择当前 modded profile 的文件。使用直接安装的 mod 时，在录制前自行保留这些起点文件。游玩后写入的存档代表较晚的状态。

种子从 `replay-input.private.json` 读取，初始化元数据与策略使用的观察分开保存。

## 收集回放输入

将以下文件转移到 Linux 工作区的 `.private/` 目录，目录权限为 `0700`：

- 原始 `actions.jsonl`，或 [Mac 导出](../mac/README.zh-CN.md#导出与处理示范)中逐字节保留的 `actions.raw.jsonl`。
- 提供种子和版本的 `replay-input.private.json`。
- 上表中的起点 profile 文件。使用 Mac 启动器时，单独转移 `initial-profile/`；会话导出工具复制事件与元数据。

Linux 导入器读取原始语义事件流。`commands.proposed.jsonl` 是预处理输出，用于审查映射和构建数据集。

## 准备 Linux 并导入片段

按 [Linux 准备流程](../linux/README.zh-CN.md#准备工作区)使用起点 profile 配置工作区。Continue 在 `prepare.py` 中增加 `--current-run`，指向保留的初始局内存档。保留准备时设置的 `STS2_WORK`，在仓库 `linux/` 目录执行：

```bash
python3 -B scripts/import_trace.py \
  --input '/absolute/path/to/actions.jsonl' \
  --output "$STS2_WORK/.private/replay.json" \
  --seed 'RECORDED_SEED' --start continue \
  --current-run '/absolute/path/to/initial/current_run.save' --actions 12

python3 -B scripts/sts2_play.py \
  --config "$STS2_WORK/.private/config.json" \
  --mode replay --trace "$STS2_WORK/.private/replay.json" \
  --seed 'RECORDED_SEED' --root-run replay-1 --decisions 12 --tutorials no
```

将 `RECORDED_SEED` 替换为记录种子，将 `12` 替换为从动作 1 开始连续回放的输入数，每个输入都需要实际后继。`--decisions` 必须等于导入输入数。导入器把初始局内存档哈希和最后的记录观察存入回放包。

新局工作区使用 `--start new` 导入，省略 `--current-run`，并保持种子和其他起点条件一致；回放命令继续使用该回放包。

## 读取结果

`sts2_play.py` 输出 `launchEvidence` 目录。回放成功结束后，其中的 `replay-result.json` 包含：

| 字段 | 含义 |
| --- | --- |
| `inputs` | 已执行的记录输入数。 |
| `mechanicalMatched` | 按比较器规定的规范化处理后，记录中的决策状态与终点一致。 |
| `strictDecisionTimingMatched` | 决策可用性一致，包含按钮时机。 |

`action-log.jsonl` 包含动作生命周期和后继观察。发生导入或回放错误时，检查提示的事件/动作，以及会话的 `exit.json` 和 `launcher.log`。排查差异时保留原始记录。

比较包含卡牌身份与效果、事件选项、奖励、资源和转场状态。卡牌实例 ID 在进程间建立映射，具体展示规范化规则见[协议](../linux/PROTOCOL.zh-CN.md#回放比较)。

鼠标拖牌可能改变 `end_turn` 的可用时机。研究这一特定时机差异时，可为回放命令增加 `--allow-timing-differences`。如果借此接受了时机差异，结果会记录 `strictDecisionTimingMatched: false`，并单独报告机械状态比较。

## 处理录制边界

导入器要求所选片段具有同一进程/运行身份、连续事件、已接受输入和实际后继。真实嵌套输入保留为独立命令，父决策须在片段内闭合。未知 UI、缺失输入、失败或片段内的连续性边界仍停止导入。`claim_relic:*` 和 `deselect_hand:*` 尚无对应 Linux Bridge 绑定，导入器会明确拒绝。

源码录制器 revision 0.2.4 在 Proceed 原始同步回调内，把 `SetLocalPlayerReady` 记录为成对的 `engine_notification`，包含 `callback-scope-v1` 归属、通知序号和父动作序号。导入器验证父回调及通知进入/返回顺序，只执行父 Proceed。父关系不明的旧 `next_act` 仍拒绝。当前 Mac 安装包已包含录制器 revision 0.2.4。

退菜单、读档和重启后的片段分别处理。Continue 回放包包含 `initial_resume_unverified`，标识读档是该片段的起点边界。

## 旧录制兼容

`--legacy-adapter mac-20260915-cross-act` 是针对一份保留回归轨迹的兼容选项，要求原始文件及起始存档的哈希与[导入器](../linux/scripts/import_trace.py)内声明完全一致。它保留原始事件，逐项记录转换及父关系。自己的录制使用默认导入流程。

`--actions` 计算源 `action_initiated` 事件，`--decisions` 计算导入后可执行的命令数。显式适配将旧通知归入父 Proceed 时，两者可能不同。导入器会输出可执行输入数。导入时 `replayEvidence.status` 始终为 `not_run`；比较结果由回放流程产生。

## 接入自己的工作流

按[轨迹协议](../linux/PROTOCOL.zh-CN.md#导出动作日志)处理 accepted/completed 动作对，或使用 [Linux 战斗样本采集器](../linux/README.zh-CN.md#训练与数据集)。数据处理流程可以为每段示范保留回放结果，据此选择训练样本。
