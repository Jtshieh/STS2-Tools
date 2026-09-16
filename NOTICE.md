# 来源与许可

自有代码采用 [MIT](LICENSE)，Copyright (c) 2026 Jtshieh and contributors。上游部分保留原版权与许可：

| 来源 | 本仓库用途 | 许可 |
| --- | --- | --- |
| [RunReplays](https://github.com/boardengineer/RunReplays/tree/b0d2302ee69bf2ad735e0b6b51aea02408e9ef62) | Mac 被动 hook 的设计与有限派生；剔除自动决策、解锁、种子/遭遇改写等功能 | [MIT · boardengineer](linux/LICENSES/RunReplays.txt) |
| [divine-sts2](https://github.com/favet/divine-sts2/tree/7cb715916fc9abfb0281c0adf4939c74468e05a4) | Linux 加载器路线与有限派生，不分发完整模拟/搜索后端 | [MIT · divine-sts2 contributors](linux/LICENSES/divine-sts2.txt) |
| [Godot 4.5.1](https://github.com/godotengine/godot/tree/4.5.1-stable) | SDK 初始化结构与本地工具接口 | [MIT · Godot contributors](linux/LICENSES/Godot.txt) |

Mac hook 的细节见 [ATTRIBUTION.md](mac/src/ATTRIBUTION.md)，Linux 逐文件来源见 [SOURCE_ORIGINS.json](linux/SOURCE_ORIGINS.json)。API名称、版本兼容元数据和本地适配调用不等于分发游戏实现；未纳入游戏方法体、机制实现或资源文本库。

游戏、Godot/.NET运行时、Harmony及系统依赖由用户本地提供，源码仓库和预编译mod不附带这些程序集。Mac可选隔离准备器对用户自己Harmony副本做同长度临时路径替换，原文件不改，修改后的依赖不分发。

MIT仅适用于有权授权的工具代码及对应上游MIT部分。游戏资源、反编译输出、存档/progression、账号信息、真实轨迹和特权日志不属于本项目许可或分发内容。本项目不代表或隶属于 Mega Crit。
