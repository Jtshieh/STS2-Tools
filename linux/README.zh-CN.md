# Linux 原引擎接口

[English](README.md) | 简体中文 | [项目首页](../README.zh-CN.md)

通过结构化观察和动作驱动 STS2 原游戏引擎。使用自带控制器进行人工选择，通过 JSONL 策略进程自动决策，或通过文件桥接入自己的控制器。导出轨迹用于外部训练，并将 [Mac 人类示范](../docs/REPLAY.zh-CN.md) 交给引擎回放。

## 兼容性

| 项目 | 支持配置 |
| --- | --- |
| 游戏 | v0.111.0 / 41cef1ea / Steam build 24724944 |
| 宿主 | Linux x86_64；参考宿主 Ubuntu 24.04.5 |
| 隔离运行时 | Ubuntu 22.04.5 用户态 / glibc 2.35 |
| 玩法 | 单人 Silent（猎手）A0，使用自己 profile 的解锁进度 |
| 起点 | 新局，或从显式提供的 `current_run.save` Continue |

## 需要什么

### 本仓库提供

| 内容 | 用途与获取入口 |
| --- | --- |
| Linux 源码与入口 | [下载源码 ZIP](https://github.com/Jtshieh/STS2-Tools/archive/refs/heads/main.zip) 或克隆仓库。[scripts/](scripts/) 提供准备、构建、运行、导出、导入和训练命令。 |
| 引擎适配 | [src/host/](src/host/) 包含观察/动作桥，[src/guard/](src/guard/) 包含运行隔离代码，使用自己的游戏和工具链构建。 |
| 配置 | [游戏清单](config/game-linux.json)和[公开用户态归档清单](config/userland-archives.json) 提供版本、URL 和哈希。 |
| 协议与示例 | [协议](PROTOCOL.zh-CN.md)、[日志 schema](schemas/action-log-v1.json)、[策略进程](scripts/sts2_policy.py)和[学习示例](scripts/sts2_train.py)。 |

### 用户自行准备

Linux 路径在本地构建。执行准备命令前，备齐以下输入：

| 输入 | 版本与用途 |
| --- | --- |
| 游戏 | 兼容的 Linux x86_64 安装，包含 release 可执行文件、`.pck` 和托管依赖。 |
| Profile | 自己账户的 `profile.save` 选择器，以及选定 `profileN/saves/prefs.save` 和 `progress.save`；Continue 还需要初始 `current_run.save`。 |
| 命令行运行时 | Python 3.12、git、bubblewrap、zstd；宿主需要允许 bubblewrap 使用用户 namespace。Python 脚本使用标准库。 |
| 原生构建工具 | GCC 13.3.0（参考 Ubuntu 包 `13.3.0-6ubuntu2~24.04.1`）和 binutils 2.42；guard 构建会核对固定输出哈希。 |
| .NET | SDK 9.0.303，包含 runtime 9.0.7。将安装目录传给 `--dotnet-root`。 |
| Godot 构建输入 | 完整 Godot 4.5.1 stable mono Linux x86_64 目录，包含 `GodotSharp/Tools/nupkgs`。 |
| 离线运行时包 | `microsoft.netcore.app.runtime.linux-x64.9.0.7.nupkg` 和 `microsoft.aspnetcore.app.runtime.linux-x64.9.0.7.nupkg`，放在同一目录。 |
| 运行用户态归档 | [config/userland-archives.json](config/userland-archives.json) 中列出的 18 个公开归档，按清单文件名保存。 |
| 存储 | 首次准备和一条会话至少预留 8 GiB 空闲空间。 |

## 准备工作区

从仓库根目录进入 `linux/`，设置以下路径。`STS2_WORK` 也可以指向单独的新工作区，后续命令均在 `linux/` 下执行。

```bash
cd linux
export STS2_WORK="$PWD"
export STS2_GAME='/absolute/path/to/compatible-linux-game'
export STS2_PROFILE='/absolute/path/to/profile2/saves'
export STS2_SELECTOR='/absolute/path/to/account/profile.save'
export STS2_DOTNET='/absolute/path/to/dotnet-9.0.303'
export STS2_GODOT='/absolute/path/to/Godot_v4.5.1-stable_mono_linux_x86_64'
export STS2_FEED='/absolute/path/to/two-runtime-nupkgs'
export STS2_ARCHIVES='/absolute/path/to/public-userland-archives'
mkdir -m 700 -p "$STS2_WORK/.private"
```

将 `config/userland-archives.json` 复制为 `$STS2_ARCHIVES/archive-manifest.json`，与下载的归档放在一起。构建本地用户态，准备游戏/profile 输入，再构建宿主接入代码：

```bash
python3 -B scripts/build_private_userland.py \
  --inputs "$STS2_ARCHIVES" --output "$STS2_WORK/.private/userland" \
  --zstd /usr/bin/zstd

python3 -B scripts/prepare.py --workspace "$STS2_WORK" \
  --game "$STS2_GAME" --profile "$STS2_PROFILE" \
  --selector "$STS2_SELECTOR" --profile-id 2 \
  --dotnet-root "$STS2_DOTNET" --godot-root "$STS2_GODOT" \
  --runtime-feed "$STS2_FEED" \
  --userland-root "$STS2_WORK/.private/userland/root" \
  --userland-manifest "$STS2_WORK/.private/userland/runtime-manifest.json"

python3 -B scripts/build.py --config "$STS2_WORK/.private/config.json"
```

准备过程核对游戏文件，创建游戏基线和 profile 副本，生成 `.private/config.json`。`profile.save` 选择的 profile 必须与 `--profile-id` 和 `profileN` 目录一致。`progress.save` 提供长期解锁进度，`current_run.save` 提供某一局的状态。

Continue 在 `prepare.py` 命令中增加 `--current-run '/absolute/path/to/current_run.save'`，使用目标起点的精确存档。需要不同起点时选择新工作区。如果已经维护普通文件组成的不可变游戏基线，可用 `--readonly-game` 将其复用为基线输入。

## 运行决策循环

在已准备的新局工作区尝试人工控制：

```bash
python3 -B scripts/sts2_play.py --config "$STS2_WORK/.private/config.json" \
  --mode manual --seed STS2DEMO01 --root-run demo-1 --decisions 30 --tutorials no
```

终端显示当前状态和可用动作，输入所显示的动作编号。`--decisions` 将会话限制在 1–1000 个输入，`--tutorials` 显式选择教程行为。Continue 工作区使用初始存档中的种子。

使用策略进程：

```bash
python3 -B scripts/sts2_play.py --config "$STS2_WORK/.private/config.json" \
  --mode auto --seed STS2DEMO01 --root-run demo-2 --decisions 30 --tutorials no
```

每个决策点，控制器读取观察，向策略取得动作 ID，将其提交给引擎，再等待下一决策。战斗、地图、事件、奖励、商店、火堆和嵌套选择使用相同循环。

## 接入自己的策略

`--mode auto` 运行 [scripts/sts2_policy.py](scripts/sts2_policy.py) 的副本。替换其中的 `choose(obs)` 函数即可接入自己的决策规则或模型，保留文件底部的 JSONL 输入/输出循环。例如，以下可运行的选择规则从当前可用动作中采样：

```python
import random

policy_rng = random.Random(0)

def choose(obs):
    action = policy_rng.choice(obs["actions"])
    return {"actionId": action["id"], "controller": "custom"}
```

将采样表达式替换为自己的策略评分或推理。输入包含 `schema`、`processRunId`、`decisionId`、`state` 和 `actions`；每个动作有 `id`、`kind`、`label` 和 `detail`。返回本次观察 `actions` 中的 `actionId`，控制器会补充进程与决策身份，再向引擎提交。

策略进程从 stdin 每行读取一个 JSON 对象，向 stdout 每行返回一个 JSON 对象，每次输出后 flush。诊断信息写入 stderr。默认响应超时 10 秒，地址空间上限 256 MiB，CPU 总预算 90 秒。策略在独立 namespace 中运行，可读取 Python/系统文件与策略源码；游戏文件和局内存档留在引擎进程中。

使用机器学习框架或外部服务时，修改 [scripts/sts2_play.py](scripts/sts2_play.py) 中的策略进程启动代码，提供所需模型、运行时和资源。该代码块是替换自带策略进程的扩展点。`--checkpoint` 通过 [sts2_small_policy.py](scripts/sts2_small_policy.py) 加载内置的 `sts2-small-policy-v1` 模型格式。

自建控制器可按[文件桥协议](PROTOCOL.zh-CN.md#文件桥)读取 `observation.json`，原子写入 `action.json`，再读取后继观察或终点结果。每个会话使用一个控制器。

## 结果与轨迹

启动命令输出 `launchEvidence`，指向工作区 `.private/` 下的本次会话目录。

| 文件 | 内容 |
| --- | --- |
| `controller.jsonl` | 控制器读取的观察和提交的动作。 |
| `action-log.jsonl` | 导出的动作生命周期记录，包含动作前观察与后继观察。 |
| `runtime.json` | 引擎详细输出与工作 profile 的位置。 |
| `lineage.json` | 运行身份与起点输入元数据。 |
| `exit.json`、`launcher.log` | 退出状态和启动/运行诊断。 |
| `replay-result.json` | 回放模式的比较结果。 |

重新导出会话：

```bash
python3 -B scripts/export_log.py --launch '/absolute/path/from/launchEvidence' \
  --output "$STS2_WORK/.private/export.jsonl"
```

按 `processRunId` 和 `sequence` 对动作日志分组。`accepted` 行携带动作前观察和所选动作，`completed` 行携带实际后继观察。失败与中断作为单独结果处理。动作预算结束记录为 `interrupted/budget_truncated`，在数据处理中属于被截断的 episode。嵌套决策与生命周期字段见[协议](PROTOCOL.zh-CN.md)。

## 训练与数据集

自己的训练代码可以读取动作日志，从玩家观察定义特征与奖励、更新模型，再通过策略进程或文件桥提交下一步决策。回放初始化元数据与策略输入分开使用。

仓库包含一个使用 Adam 的 64 参数 masked-softmax 战斗模仿学习示例。采集命令从 Linux 会话中提取已送达且有实际后继的战斗输入。采集前审查该会话的观察适配代码，创建审查 JSON，包含 `trainingCombatPathApproved: true` 和 `approvedBridgeSha256: ["<reviewed SHA-256>"]`。哈希应匹配 `runtime.json` 的 `evidenceRoot` 下该会话的 `input-source/Bridge.cs`。

```bash
python3 -B scripts/sts2_train.py collect \
  --launch '/absolute/path/from/launchEvidence' \
  --gate '/absolute/path/to/observation-review.json' \
  --output "$STS2_WORK/.private/samples.json"

python3 -B scripts/sts2_train.py update --data "$STS2_WORK/.private/samples.json" \
  --output "$STS2_WORK/.private/checkpoint-4.json" --updates 4

python3 -B scripts/sts2_train.py resume --data "$STS2_WORK/.private/samples.json" \
  --checkpoint "$STS2_WORK/.private/checkpoint-4.json" \
  --output "$STS2_WORK/.private/checkpoint-8.json" --updates 4
```

输出使用新文件名。`resume` 为同一数据集恢复模型、优化器、采样器和训练 RNG；示例累计支持最多 20 次更新。在新局工作区评估得到的战斗策略：

```bash
python3 -B scripts/sts2_play.py --config "$STS2_WORK/.private/config.json" \
  --mode auto --checkpoint "$STS2_WORK/.private/checkpoint-8.json" \
  --seed STS2EVAL01 --root-run eval-1 --decisions 30 --tutorials no
```

该模型处理战斗决策，自带策略进程在其他阶段使用规则。人类示范可从 [Mac 导出](../mac/README.zh-CN.md#导出与处理示范)整理成自己的数据集格式，也可先[在 Linux 回放](../docs/REPLAY.zh-CN.md)，再从回放会话采集。仓库自带 collector 读取 Linux 会话输出。

## 工作区存储

运行使用独立的游戏/profile 工作副本。会话退出且保护检查完成后，工具回收复制的游戏资源和可重建缓存，保留日志、profile、结果与输入。重试回收已结束会话：

```bash
python3 -B scripts/work_materials.py --config "$STS2_WORK/.private/config.json"
```

命令使用会话锁，将结果写入 `work-materials-cleanup.jsonl`。若提示退出检查不完整，先查看该会话的 `exit.json` 和 `launcher.log`，再重试。保留的轨迹与 profile 会继续占用空间。

## 许可

[MIT](LICENSE) 与[第三方归属](NOTICE.md)。
