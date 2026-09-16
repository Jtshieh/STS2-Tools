# STS2 Tools — v0.1.0-alpha

**ver0.1 系列的 Linux 组件。** 为自备正版兼容 STS2 的用户提供 Linux 原引擎连续动作接口、玩家观察、显式错误/中断、已有 Mac 语义轨迹导入及机械状态比较。游戏自行结算，工具只接入原流程；不是独立游戏引擎。

本地 Linux 构建和 12 输入短轨迹复现已通过。自有代码采用 MIT；Mac 录制器位于同仓库的 [mac/](../mac/README.md)。详见 [来源与许可](NOTICE.md)、[验证与限制](RELEASE.md)、[协议](PROTOCOL.md)。

## 支持范围

| 项目 | 本版范围 |
| --- | --- |
| 游戏 | **v0.111.0 / 41cef1ea / build 24724944**；不静默升级 |
| Linux | x86_64，实测 Ubuntu 24.04.5；运行隔离用户态为 Ubuntu 22.04.5 / glibc 2.35 |
| 玩法 | 单人 Silent A0，用户自己的 progression；本轮 profile 2 实测，profile 1/3 参数化但未实测 |
| 入口 | 原菜单新局；显式提供 current run 时走原 Continue。两者不混用 |
| 本轮真实复现 | Continue 后 12 输入：商店购买/退出 → 地图 → 火堆 → 网格选牌/取消；12 个后继和终点一致 |
| 历史有限证据 | 连续 Combat/Macro/嵌套动作；157 输入跨幕机械比较；最小学习闭环，见 RELEASE.md |
| Mac | 导入 `sts2-gui-semantic-v1` 记录；录制器安装见 [Mac 文档](../mac/README.md) |

不承诺整局严格保真、全机制覆盖、胜率提升、稳定 RL、完整 SL、Windows 宿主或 HPC 部署。

## 安装与本地输入

源码包解压后直接使用 Python 脚本，不需要 pip 安装。本包**不下载游戏、存档或依赖，不执行系统安装或创建开发证书**。工具链和普通依赖由用户自行准备：

- Python 3.12、git、bubblewrap、zstd；Linux 用户 namespace 必须已能使用。失败时检查系统策略，不自动修改安全设置。
- GCC **13.3.0**（实测 Ubuntu `13.3.0-6ubuntu2~24.04.1`）、binutils **2.42**。guard 输出需符合固定摘要；其他编译器输出未经审查会明确拒绝。
- .NET SDK **9.0.303**（含运行时 9.0.7），Godot **4.5.1 stable mono Linux x86_64** 完整目录（含 `GodotSharp/Tools/nupkgs`）。Godot 编辑器只供离线构建包，游戏实际运行用用户的对应 release executable。
- 两个 NuGet 离线包：`microsoft.netcore.app.runtime.linux-x64.9.0.7.nupkg`、`microsoft.aspnetcore.app.runtime.linux-x64.9.0.7.nupkg`。构建器核对固定大小/哈希，禁止联网 restore。
- [config/userland-archives.json](config/userland-archives.json) 列出的 18 个官方公开归档；自行按其 URL/哈希准备。这些不是游戏材料，解包不执行维护脚本。
- 对应 Linux 游戏目录，自己的 `prefs.save`、`progress.save` 及原账户 `profile.save`。后者选择的 profile id 必须与参数一致，不合成解锁或改写原 selector。

请预留至少 **8 GiB** 空闲空间用于首次普通复制和一条运行。构建/运行入口在创建工作材料前要求至少 **4 GiB** 可用，每个普通文件复制还须留下 **1 GiB** 余量；不足则明确停止。原安装、存档和不可变基线始终只读。不要将项目放进公开同步目录。

每次运行仍创建普通独立副本。监督进程退出、原件/宿主保护及进程退出检查完成后，入口自动回收游戏资源副本和可由保留 NuGet 包重建的二进制缓存。回收按名称、大小及复制记录核对来源，不重算清理哈希；正常身份及复制哈希检查保持原样。源码、配置、日志、动作/观察、结果、失败、profile 设置/进度/存档及唯一宿主构建产物继续保留，因此证据仍会增长。

显式回收已结束会话（使用同一个运行锁，不启动游戏）：

```bash
python3 -B scripts/work_materials.py --config "$STS2_WORK/.private/config.json"
```

正常结束和游戏失败均按同一保护条件处理，不按成功/失败挑选。异常强杀、退出/保护检查缺失或失败、活动进程/挂载、来源不明时保留材料并提示；没有无条件 `finally` 删除。先诊断并完成必要退出核验，再用显式入口处理；检查仍不完整时继续保留。每个会话的 `work-materials-cleanup.jsonl` 在删除前记录具体文件与保留来源，删除后记录结果/错误；重复执行不重复删除。回收后该目录只保留证据，不能直接重启，须从保留输入生成新独立副本。基线不会自动删除。

以下命令从源码根运行。先将大写变量设为本机已有材料的**绝对路径**；变量值是用户输入，不是隐藏的私人路径依赖：

```bash
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

归档目录内应有 `archive-manifest.json`，其内容与本包 `config/userland-archives.json` 一致。仅在自己新建的归档目录复制该清单，不改别人的不可变缓存。

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
```

准备步骤只读检查游戏版本/架构/文件哈希，创建普通独立基线、复制原 progression，在本机提取必要配置/元数据，并编译 guard。不会部署到原 Steam 安装、修改云同步或安全设置。`--readonly-game` 仅用于用户已有的普通不可变完整基线，可免第二份基线复制；实际游戏运行仍使用新的普通独立副本。软链接/硬链接输入拒绝。

`progress.save` 表示长期解锁/进度；`current_run.save` 是某个当前局。默认不导入当前局。若准备的是录制起点的 Continue 夹具，在上面的 prepare 命令加 `--current-run '/absolute/path/to/current_run.save'`。**必须是与轨迹起点哈希一致的那份文件，不能用同 seed 的其他档替代。** `prepared` 已存在时拒绝覆盖；另选新 workspace 保留先前结果。

## 构建、运行、导出

```bash
# 独立离线构建；不启动游戏
python3 -B scripts/build.py --config "$STS2_WORK/.private/config.json"

# 新局配置：人工选择显示的动作编号；教程选择也是明确参数
python3 -B scripts/sts2_play.py --config "$STS2_WORK/.private/config.json" \
  --mode manual --seed STS2DEMO01 --root-run demo-1 --decisions 30 --tutorials no

# 同一接口的规则示例；只有显式 --checkpoint 才使用用户提供的小模型
python3 -B scripts/sts2_play.py --config "$STS2_WORK/.private/config.json" \
  --mode auto --seed STS2DEMO01 --root-run demo-2 --decisions 30 --tutorials no
```

每次运行独立构建并记录源码，不读取日期命名的实验实现。`seed` 是显式调试输入：新局在 Embark 前设置原 `DebugSeedOverride`，不宣称操作过 GUI 种子框；Continue 只核对存档种子。生产流程对工作副本 Settings/偏好/progression 的合法写入正常保留。

输出首行给出 `launchEvidence`。会话目录含 `exit.json`、`lineage.json`、`runtime.json`、`controller.jsonl`、`action-log.jsonl`；`runtime.json` 指向游戏原始证据、工作 profile、日志、47 项主机检查和最终身份结果。默认都在 workspace 的 `.private` 内。成功预算截断仍输出明确 `interrupted`，不假装整局完成。

```bash
python3 -B scripts/export_log.py --launch '/absolute/path/from/launchEvidence' \
  --output "$STS2_WORK/.private/export.jsonl"
```

外层退出非零时首先查看该会话的 `exit.json` 和 `launcher.log`。观察失败、非法/过期输入、缺少后继、版本/架构不同、缺依赖都会明确保留错误。不会默认选第一项、静默跳过或重抽 seed。不要将私人日志直接提交 issue。

## 已有 Mac 轨迹导入与 Linux 回放

使用 [Mac 录制器](../mac/README.md) 得到兼容原始语义记录后，先在单独 workspace 用匹配的原 current run 按上节准备 Continue 配置，然后：

```bash
python3 -B scripts/import_trace.py --input '/absolute/path/to/actions.jsonl' \
  --output "$STS2_WORK/.private/replay.json" --seed 'RECORDED_SEED' \
  --start continue --current-run '/absolute/path/to/initial/current_run.save' --actions 12

python3 -B scripts/sts2_play.py --config "$STS2_WORK/.private/config.json" \
  --mode replay --trace "$STS2_WORK/.private/replay.json" --seed 'RECORDED_SEED' \
  --root-run replay-1 --decisions 12 --tutorials no
```

输入数必须与回放包完全一致；短段终点来自最后一个记录输入的实际后继。缺输入、不完整轨迹、未知 UI 输入和旧 hook 的未归属 `next_act` 会拒绝。比较规则及显式时机差异选项见 [PROTOCOL.md](PROTOCOL.md)。Mac/Linux 分别记录平台、架构、游戏版本和 DLL 哈希，不要求跨平台 DLL 相同。

## 最小学习实验与无游戏检查

保留原 64 参数 masked-softmax 模仿学习/Adam 示例。它只处理战斗示范，宏观/嵌套选择仍是规则；不含训练数据、模型权重或胜率提升承诺。`collect` 需要用户审查过的观察边界 gate（指定实际 Bridge.cs 哈希），不能伪造 gate 为通过。本次未重跑训练。

```bash
# 使用你自己已经审查的玩家观察样本；每个输出文件必须不存在
python3 -B scripts/sts2_train.py update --data '/absolute/path/to/samples.json' \
  --output "$STS2_WORK/.private/checkpoint-4.json" --updates 4
python3 -B scripts/sts2_train.py resume --data '/absolute/path/to/samples.json' \
  --checkpoint "$STS2_WORK/.private/checkpoint-4.json" \
  --output "$STS2_WORK/.private/checkpoint-8.json" --updates 4

# 无需游戏、存档、网络或 SDK
python3 -B -m unittest discover -s tests -v
python3 -B scripts/package_source.py
```

公开 CI 仅执行后两项合成/清单检查，不获取游戏或私人存档，不依赖私人服务器。真实集成验证由拥有合法材料的用户显式执行；运行记录、游戏材料和构建产物不得提交。
