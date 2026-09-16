# Mac → Linux 回放

## 两端分别准备游戏

Mac 需要 Mac arm64 游戏，Linux 需要 Linux x86_64 游戏，均为 v0.111.0 / 41cef1ea / build 24724944。不能把 Mac app 当作 Linux 游戏运行，也不要求两平台 DLL 哈希一致。

Mac mod 负责记录输入，Linux 环境负责执行输入和比较决策边界。两端都必须自行提供正版游戏，仓库没有游戏资源、progression 或实际轨迹。

## 先取得可复现的起点

最清楚的方式是使用 [Mac 独立副本](../mac/README.md#可选独立游戏和-profile) 的显式 Continue 输入；启动器在日志 `initial-profile/` 中保存实际启动前的文件。

普通 mod 不自动复制 Steam 存档或创建完整恢复点。若希望比较 Continue，必须在录制前自行保留那份初始 current_run、selector、progression/prefs；结束后的 current_run 通常已被游戏改写，不能代替起点。

新局回放还要求相同角色、A0、模式、progression 和已记录种子。接口支持新局，但本版新增加的跨平台验收使用 Continue 短段；不要据此宣称任意新局一致。

## 私下转移最小材料

只向你控制且已授权的 Linux 主机转移：

- 原始 `actions.jsonl`；不要改写事件或填补缺失动作。
- `replay-input.private.json` 中的 seed 和版本信息，供验证入口使用。
- 起点的 profile 输入；Continue 必须附上那份初始 current_run。

这些文件只放 `.private`（0700），不发到 GitHub issue、公共 CI 或本仓库。玩家观察与隐藏验证资料保持分离。

## Linux 命令

按 [Linux README](../linux/README.md) 准备依赖和新 workspace，`prepare.py` 传入匹配的 profile 与 `--current-run`。随后在仓库的 `linux/` 目录运行：

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

这里的12是示例上限，必须换成轨迹中从动作1开始连续完整的一段，`--decisions` 必须匹配导入包输入数。不要为了让导入通过删除失败/重启/未知输入事件。

## 什么算通过

输入送达、原引擎接受、动作后继、机械观察一致和完整进程验收分别记录。只有已审查的纯展示差异允许规范化；卡牌状态、选项、奖励和结算差异不能忽略。

当前通用导入会拒绝旧 `next_act`、未知 UI 输入和缺失后继。157输入跨幕证据使用过专门审查的转换，不是通用导入器已覆盖所有轨迹的证明。鼠标拖牌造成的按钮时机差异有独立开关和报告，不能标成严格一致。

回到菜单、读档或重启之后的衔接仍未验证；保留片段和中断，不能直接拼接。详细协议见 [linux/PROTOCOL.md](../linux/PROTOCOL.md)。
