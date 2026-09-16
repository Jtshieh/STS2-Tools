# STS2 Tools · v0.1.0-alpha

为自备正版 **Slay the Spire 2** 的用户提供两种接入方式：在 Mac 正常鼠标游玩并录制语义动作，或在 Linux 通过结构化观察和动作驱动原游戏。游戏规则由原引擎执行。

**当前兼容：v0.111.0 / 41cef1ea / build 24724944，Mac Apple Silicon 与 Linux x86_64。** 初始研究范围为单人 Silent（猎手）A0。其他版本明确拒绝，不自动下载或升级游戏。

## 选择用法

| 你想做什么 | 入口 | 需要什么 |
| --- | --- | --- |
| Mac 正常玩游戏，自动保存动作 | [Mac mod 安装](mac/README.md#推荐直接安装-mod) | 对应版本 Mac 游戏、录制器 DLL 和 JSON；使用预编译包无需 SDK |
| Mac 游戏和存档与日常游玩分开 | [Mac 独立副本](mac/README.md#可选独立游戏和-profile) | 对应游戏、自己的 progression/profile、Python 3.12；从源码构建另需 .NET 9 arm64 |
| Linux 手动/程序逐步操作原游戏 | [Linux 安装与运行](linux/README.md) | 对应 Linux 游戏、自己的 profile、固定本地依赖与工具链 |
| 比较 Mac 录制与 Linux 结算 | [跨平台回放](docs/REPLAY.md) | 两平台各自的游戏、匹配初始条件、完整原始动作记录 |

普通 Mac mod 是最简单的录制入口。Linux 接口目前仍是研究工具，依赖准备比安装 mod 多；README 逐项列出固定版本及命令。仅拥有游戏本体不意味着其他工具链已经安装。

## 快速开始

从 [Releases](https://github.com/Jtshieh/STS2-Tools/releases) 获取 `Sts2Recorder-macos-arm64-v0.1.0-alpha.zip`（若该包已提供）；解压后把 `Sts2Recorder.dll` 与 `Sts2Recorder.json` 放入：

```text
SlayTheSpire2.app/Contents/MacOS/mods/
```

使用原游戏的 mod 提示启用，其他 mod 保持关闭。主菜单出现 `RECORDER READY` 后正常游玩，日志自动追加。详情和从源码构建方法见 [Mac 文档](mac/README.md)。仓库可见性为 private 时，下载需要仓库访问权限。

## 仓库结构

```text
mac/                 被动录制 mod、构建、可选独立副本启动、导出
  src/               录制 hook、玩家观察、卡牌身份与状态投影
  config/            Mac 版本及兼容哈希
linux/               原引擎动作环境、回放及最小学习示例
  src/host/          原游戏宿主接入与观察/动作适配
  src/guard/         Linux 隔离与身份检查
  scripts/           准备、构建、运行、回放、工作材料回收
  schemas/           动作日志协议
  config/            兼容版本及公开依赖清单
docs/REPLAY.md       两端如何衔接，何时必须停止比较
NOTICE.md            第三方归属与再分发范围
RELEASE.md           实测范围与已知限制
RELEASE_FILES.txt    可提交源码的精确白名单
scripts/audit_source.py  暂存内容与发布清单核对
```

`.private/`、游戏、存档、日志、构建产物和下载缓存均不跟踪。两端原工作区和私人历史不随仓库分发。预编译 mod 包只含本项目程序集、mod 描述及许可说明，不带游戏依赖。

## 数据与限制

- 被动录制不会替玩家选牌、结束回合或修改 RNG；自动伤害、抽牌、敌人行动和遗物触发记录为结果。
- 普通 mod 使用游戏自己的用户数据与 modded profile；独立副本启动器另行隔离 profile。两种方式不能混称。
- 失败、未知输入、缺失后继与重启中断均保留。完整 SL、整局严格保真和所有机制覆盖尚未通过。
- 历史 157 输入跨幕片段的机械比较通过，但 77 次拖牌存在输入时机差异。当前通用导入器仍拒绝旧 hook 未归属的 `next_act`，不借历史专项转换自动跳过。
- 小型学习示例验证过更新/恢复流程，不代表高胜率、训练收敛或完整 RL 基准。

完整范围见 [RELEASE.md](RELEASE.md)。提交 issue 前请先脱敏，不附游戏文件、存档、原始私人日志或账号信息。

## 许可

自有代码采用 [MIT](LICENSE)，Copyright © 2026 Jtshieh and contributors。保留 RunReplays、divine-sts2 和 Godot 的原许可与归属，见 [NOTICE.md](NOTICE.md)。本项目独立于 Mega Crit；MIT 不覆盖商业游戏及用户存档。

## 无游戏检查

```bash
python3 -B scripts/audit_source.py
python3 -B -m unittest discover -s mac/tests -v
# Linux 主机上执行：
(cd linux && python3 -B -m unittest discover -s tests -v)
```

真实集成验证需要用户本地材料；CI 只运行无游戏检查。
