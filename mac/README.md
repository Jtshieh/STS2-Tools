# Mac 语义动作录制器

支持 **macOS arm64 / STS2 v0.111.0 / 41cef1ea / build 24724944**。这是被动 mod：观察玩家输入、原引擎接受结果与下一决策点，不自动决策、不解锁、不强制遭遇、不设置种子或999格挡。

## 推荐：直接安装 mod

1. 退出游戏。从仓库 Releases 下载 Mac mod ZIP，或按下节从源码构建。
2. Steam 的“浏览本地文件”找到 `SlayTheSpire2.app`；Finder 中“显示包内容”，打开 `Contents/MacOS/mods`。没有 `mods` 时新建该目录。
3. 把 ZIP 中的 **Sts2Recorder.dll** 和 **Sts2Recorder.json** 放进去。许可文本请与安装包一起保留。不要复制任何游戏 DLL。
4. 正常启动游戏，完成游戏自己的 mod 加载提示。为了使用本项目已审查的范围，禁用其他 mod。
5. 主菜单显示 `RECORDER READY` 和日志路径后，选择自己的 profile，开始 Silent A0，正常鼠标游玩。游戏中显示 `RECORDER ON`；`INCOMPLETE` 表示日志存在问题，不能当成完整轨迹。

日志写入 Godot `user://sts2-recorder/logs/<process-id>/`。**具体绝对位置由游戏决定，并显示在主菜单和游戏日志中**，不硬编码 Steam 账号路径。每次进程独立一个目录，核心文件是 `actions.jsonl`；`replay-input.private.json` 的种子只供验证，不属于策略观察。退出/重开不会自动拼成连续轨迹。

直接安装模式使用游戏自己的保存位置和 modded profile。录制器不复制或合成解锁；若 modded profile 尚无你的进度，可使用下面的独立副本准备流程。原生 mod 的完整保存位置/Steam 行为尚未作为本版本新验收，工具不会替你修改云同步。

卸载时先退出游戏，再移除这两个 mod 文件。日志和正常游戏存档保留。不要在游戏运行中替换 DLL。

### 从源码构建 mod ZIP

需要 Python 3.12 和本机 arm64 .NET 9 SDK（实测 9.0.303）。游戏随附的 `sts2.dll`、`GodotSharp.dll`、`0Harmony.dll` 仅作为本地编译引用；它们不会被打包。

在仓库根目录执行：

```bash
python3 -B mac/build.py \
  --game-app '/absolute/path/to/SlayTheSpire2.app' \
  --dotnet '/absolute/path/to/dotnet'
```

读取原 app，输出 `mac/.private/dist/Sts2Recorder-macos-arm64-v0.1.0-alpha.zip`，不会向原 app 自动部署。构建离线运行，禁用开发证书生成与遥测，不安装系统依赖。游戏版本/架构不匹配会停止。

## 可选：独立游戏和 profile

此方式用普通独立文件复制 app 和自己的 profile，写入仅允许工作副本与日志目录，游戏网络关闭。原 Steam 安装、云设置和原存档不变。需要 macOS 现有 `/usr/bin/sandbox-exec`；不可用时明确失败，不自动降级隔离。

从自己游戏的账户保存根目录找到 `profile.save`（选择器）与对应 `profileN/saves/` 中的 `progress.save`（长期解锁）、`prefs.save`。选中的 profile id 必须一致。日志和 Steam userdata 可能帮助定位，但请用实际文件核实，不猜账号目录。

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

需要 Continue 时，prepare 显式增加 `--current-run '/absolute/path/to/current_run.save'`；默认不导入当前局。可用 `--settings '/absolute/path/to/settings.save'` 原样复制自己的现有设置。首次出现原游戏的 mod 提示时手动同意，正常退出并重新启动；录制器不替你点击提示或更改同意状态。

准备会把同一份原 selector/progression 复制到独立普通和 modded profile 中，不合成解锁。禁止覆盖已准备目录；需要新的初始化条件时选择新 workspace。工作副本允许正常保存。

准确输出：

```text
$STS2_MAC_WORK/.private/work/SlayTheSpire2.app
$STS2_MAC_WORK/.private/work/profile/default/1/modded/profileN/saves/
$STS2_MAC_WORK/.private/profile-input/           准备时原始profile副本
$STS2_MAC_WORK/.private/logs/<process-id>/
  actions.jsonl
  initial-profile/                              该次启动前的精确存档副本
  profile-before-launch.json
  launch.json / launcher-exit.json
```

隔离启动器只在工作副本中调整随附 Harmony 的临时辅助库路径，使其写入工作目录 `tmp/`；没有游戏规则补丁，原依赖不修改。普通 drop-in mod 不需要此路径调整。

## 导出与 Linux

退出游戏后，从主菜单显示的路径或独立启动器输出取得会话目录：

```bash
python3 -B mac/export_trace.py \
  --session '/absolute/path/to/session' \
  --output '/absolute/path/to/new-private-export'
```

导出保留原始记录、失败及后继；`commands.proposed.jsonl` 是待验证映射，不能等同于已接受回放。导出报告保守地标出需要逐段审查的绑定。Linux 通用入口直接读取原始 `actions.jsonl`，步骤见 [跨平台回放](../docs/REPLAY.md)。不要将任何私人导出提交 Git。

## 已知限制

- 普通 mod 自动初始化和原生启动会单独验证；有设置差异的首次 mod 提示可能需要手动处理。
- 旧的 `next_act` 父子归属仍未修复，通用回放在此明确拒绝；不宣称整局自动跨幕回放。
- 占卜界面明确不支持。卡牌状态投影支持升级、附魔、污染、锁牌等字段，但不保证所有机制分支已实测。
- 多药水弹窗查询现在过滤已标记移除的对象；真正多活动对象仍报错，不随便取第一个。
- 无人工思考超时。未知观察会标记日志不完整，不主动终止玩家游戏。
- 版本范围、原生测试结果及跨平台结论见 [RELEASE.md](../RELEASE.md)。
