# Mac 游玩录制器

[English](README.md) | 简体中文 | [项目首页](../README.zh-CN.md)

在正常鼠标游玩 STS2 时采集人类示范。录制器保存语义输入、目标、选择内容，以及下一决策点的玩家可见观察。轨迹可用于数据处理、模仿学习或 [Linux 回放](../docs/REPLAY.zh-CN.md)。

## 兼容性

| 项目 | 支持配置 |
| --- | --- |
| 游戏 | v0.111.0 / 41cef1ea / Steam build 24724944 |
| 平台 | macOS Apple Silicon / arm64 |
| 玩法 | 单人 Silent（猎手）A0，使用自己的解锁进度 |
| 发行版本 | v0.1.0-alpha；录制器 revision 0.2.3 |

平台文件哈希见 [Mac 清单](config/)。

## 需要什么

### 本仓库提供

| 内容 | 用途与获取入口 |
| --- | --- |
| mod 预编译包 | [下载 Mac ZIP](https://github.com/Jtshieh/STS2-Tools/releases/download/v0.1.0-alpha/Sts2Recorder-macos-arm64-v0.1.0-alpha.zip)。安装 `Sts2Recorder.dll` 和 `Sts2Recorder.json`，保留包内许可文件。 |
| 录制器源码与构建脚本 | [src/](src/) 和 [build.py](build.py)，也可从[源码 ZIP](https://github.com/Jtshieh/STS2-Tools/archive/refs/heads/main.zip) 获取，用于本地构建或修改 mod。 |
| 工作区与导出工具 | [prepare.py](prepare.py)、[launch.py](launch.py) 和 [export_trace.py](export_trace.py)，用于创建独立游戏/profile 工作区和整理轨迹。 |
| 版本配置 | [config/](config/) 标识兼容的 Mac 游戏文件。 |

### 用户自行准备

| 使用路径 | 要求 |
| --- | --- |
| 安装预编译 mod | 兼容的 Mac 游戏及自己的游戏 profile。游戏提供加载 mod 所需的 .NET 和 Harmony。 |
| 导出会话 | Python 3.12 和本仓库源码。 |
| 从源码构建 | Python 3.12、arm64 .NET 9 SDK（参考版本 9.0.303），以及作为本地程序集引用的兼容游戏 app。 |
| 创建独立工作区 | 源码构建所需材料、macOS `/usr/bin/sandbox-exec`，以及自己的账户 `profile.save`、`profileN/saves/prefs.save` 和 `progress.save`。Continue 还使用起点的 `current_run.save`。 |

## 安装 mod

1. 退出游戏，下载上方 mod ZIP。
2. 在 Steam 中浏览游戏本地文件。在 Finder 中对 `SlayTheSpire2.app` 选择“显示包内容”，打开 `Contents/MacOS/mods`；没有 `mods` 时新建目录。
3. 将 `Sts2Recorder.dll` 和 `Sts2Recorder.json` 复制到该目录。
4. 启动游戏，完成游戏的 mod 加载提示。若要将录制接入本环境回放，仅启用录制器。
5. 在主菜单确认 `RECORDER READY` 和日志路径，选择自己的 profile，开始 Silent A0。游玩期间指示器显示 `RECORDER ON`。

此安装方式使用游戏正常的 modded profile 和保存位置。若要在独立 profile 工作区使用已有解锁进度，请采用下方准备流程。

卸载时退出游戏，移除两个录制器文件。存档和已录会话仍会保留。

## 录制与查找日志

正常进行战斗、奖励领取、地图选择、事件、商店、火堆及嵌套选牌。每个游戏进程在 Godot 的 `user://sts2-recorder/logs/<process-id>/` 下写入独立会话。主菜单和游戏日志会显示其绝对路径。

| 文件 | 用途 |
| --- | --- |
| `actions.jsonl` | 逐步追加的语义事件：观察、输入、接受/送达状态、嵌套选择与后继。 |
| `replay-input.private.json` | 初始化回放使用的种子和版本信息，与模型观察分开保存。 |

`INCOMPLETE` 表示本次会话存在录制问题。选取训练或回放片段前先查看导出报告。退菜单、读档和重启会产生连续性边界，各片段应分别处理。

## 导出与处理示范

退出游戏后，在仓库根目录执行以下命令。会话路径使用录制器显示的位置，输出路径使用新目录：

```bash
python3 -B mac/export_trace.py \
  --session '/absolute/path/to/session' \
  --output '/absolute/path/to/new-private-export'
```

| 导出文件 | 含义 |
| --- | --- |
| `actions.raw.jsonl` | 原样保留的事件流，此文件或原始 `actions.jsonl` 均可作为 Linux 导入输入。 |
| `commands.proposed.jsonl` | 供数据处理使用的候选观察/动作对。 |
| `successors.jsonl` | 输入之后的实际观察，包含嵌套决策边界。 |
| `rejected-attempts.jsonl` | 被游戏拒绝的购买尝试。 |
| `conversion.json` | 事件数量、连续性/录制问题，以及需要审查的映射。 |
| `SHA256SUMS` | 导出文件哈希。 |

用于模仿学习时，将每个输入的观察与实际选择配对，并检查送达和后继事件。筛选或标注样本时参考 `conversion.json`。选牌输入与最终选牌结果描述同一次交互的不同阶段，应将输入计为决策。导出的候选映射和 `structuralReady` 字段用于预处理诊断；跨平台比较以 Linux 回放结果为准。

后续流程见 [Mac 到 Linux 回放](../docs/REPLAY.zh-CN.md)，自建数据处理流程可参考[轨迹协议](../linux/PROTOCOL.zh-CN.md)。

## 从源码构建 mod

在仓库根目录执行：

```bash
python3 -B mac/build.py \
  --game-app '/absolute/path/to/SlayTheSpire2.app' \
  --dotnet '/absolute/path/to/dotnet'
```

ZIP 输出到 `mac/.private/dist/Sts2Recorder-macos-arm64-v0.1.0-alpha.zip`，按上方步骤安装。构建引用游戏本地的 `sts2.dll`、`GodotSharp.dll` 和 `0Harmony.dll`，打包录制器自己的程序集。

## 可选：独立游戏与 profile 工作区

此路径使用独立游戏/profile 副本录制，并保留回放所需的精确起点存档。`profile.save` 选择 profile，`progress.save` 保存解锁进度，`current_run.save` 保存正在进行的局。`--profile-id` 应与选择器和 `profileN` 目录一致。

在仓库根目录执行：

```bash
export STS2_MAC_WORK='/absolute/path/to/recorder-workspace'

python3 -B mac/prepare.py --workspace "$STS2_MAC_WORK" \
  --game-app '/absolute/path/to/clean/SlayTheSpire2.app' \
  --selector '/absolute/path/to/account/profile.save' \
  --profile '/absolute/path/to/account/profile2/saves' --profile-id 2

python3 -B mac/build.py --workspace "$STS2_MAC_WORK" \
  --dotnet '/absolute/path/to/dotnet'

python3 -B mac/launch.py --workspace "$STS2_MAC_WORK"
```

Continue 在 `prepare.py` 命令中增加 `--current-run '/absolute/path/to/current_run.save'`。复用游戏设置可增加 `--settings '/absolute/path/to/settings.save'`。需要不同起点时使用新工作区。

准备步骤普通复制 app 和自己的 profile。启动器将写入限制在工作区和日志内，并关闭游戏网络；工作 profile 正常保存。完成游戏首次 mod 提示后，如游戏要求重启，退出并重新启动。

以下路径均位于 `$STS2_MAC_WORK/.private/`：

| 路径 | 内容 |
| --- | --- |
| `work/SlayTheSpire2.app` | 已安装构建后录制器的可游玩游戏副本。 |
| `work/profile/default/1/modded/profileN/saves/` | 工作 modded profile。 |
| `profile-input/` | 准备时复制的 profile 文件。 |
| `logs/<process-id>/` | 会话事件与启动元数据。 |
| `logs/<process-id>/initial-profile/` | 该次启动前复制的精确 profile 文件，Continue 回放需保留。 |

会话导出工具复制事件文件和元数据。准备 Linux 回放时，单独转移 `initial-profile/`。

## 许可

[MIT](../LICENSE)，[录制器归属说明](src/ATTRIBUTION.md)与[第三方归属](../NOTICE.md)。
