# 来源、许可与发布边界

本地候选版本 **v0.1.0-alpha**，属于 **ver0.1** 系列。所有者已授权自有代码采用 MIT，署名 Jtshieh and contributors。

| 来源 | 固定版本 / 使用范围 | 许可 |
| --- | --- | --- |
| 本项目 | `src/host` 原生宿主接入、观察与动作适配；`src/guard` 隔离；`scripts` 构建、输入准备、协议、比较及实验示例 | MIT，见 LICENSE；来源分类逐文件见 SOURCE_ORIGINS.json |
| [divine-sts2](https://github.com/favet/divine-sts2/tree/7cb715916fc9abfb0281c0adf4939c74468e05a4) | 历史加载器派生路线/设计来源；当前原 release 宿主是后续有限派生。未打包其完整模拟、搜索或 Gym 后端 | Copyright (c) 2026 divine-sts2 contributors；[MIT](LICENSES/divine-sts2.txt) |
| [RunReplays](https://github.com/boardengineer/RunReplays/tree/b0d2302ee69bf2ad735e0b6b51aea02408e9ef62) | 固定提交 `b0d2302ee69bf2ad735e0b6b51aea02408e9ef62`，语义录制/动作对应参考；历史 Mac 录制轨迹来源。录制器在同仓库 mac/；不含上游修改游戏规则的补丁 | Copyright (c) 2026 boardengineer；[MIT](LICENSES/RunReplays.txt) |
| [Godot 4.5.1](https://github.com/godotengine/godot/tree/4.5.1-stable) | `GodotPluginsInitializer.cs` 保留 SDK 初始化结构并添加本项目受控入口；构建使用用户提供的 SDK/API | Godot Engine contributors；Juan Linietsky、Ariel Manzur；[MIT 原文](LICENSES/Godot.txt) |

`sts2-cli` 仅是历史候选审查对象，没有提取其实现。系统编译器、.NET、Ubuntu 用户态包、Godot 工具链、游戏随附的第三方库均由用户本地提供；源码包不包含这些二进制。用户态组装不执行安装脚本，包内原版权文件保留在私人工作目录。不得把上游 MIT 解释为 STS2 游戏材料的再分发许可。

## 白名单审查

公开输入仅为明确列出的自有接入/隔离/工具源码、第三方许可文本、固定兼容文件哈希与公开依赖 URL/哈希。源码调用原引擎 API；没有复制游戏方法体、反编译输出、卡牌/敌人机制实现或资源文本库。没有通过改名/轻微改写把游戏代码变成项目代码。

`RELEASE_FILES.txt` 是精确发布清单；`package_source.py --staged` 同时核对真实 Git 索引与文件内容，打包后逐成员复核。`.gitignore` 不是隔离。`.private` 为 0700；游戏运行采用独立文件、只读输入挂载、网络/PID 隔离及原有 seccomp/身份检查。

明确排除：游戏 DLL/ELF/PCK、`original-project.binary`、脚本类型映射、程序集运行清单、存档/progression、真实轨迹/截图/日志/权重、凭据及主机信息。需要的游戏映射由 `prepare.py` 在用户本机生成到 `.private`。

## 组件边界

Mac 录制器源码、构建与安装入口在 [../mac/](../mac/README.md)，具体原生验证范围见根目录 RELEASE.md。控制台调试、强制遭遇、注入卡牌和999格挡均排除。游戏和随附依赖仍需用户自行提供；MIT仅覆盖有权授权的工具代码及相应第三方MIT部分。
