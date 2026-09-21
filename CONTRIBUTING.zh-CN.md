# 参与贡献

[English](CONTRIBUTING.md) | 简体中文

欢迎改进策略接入、录制、回放、安装和文档。

## 报告问题

创建 [Issue](https://github.com/Jtshieh/STS2-Tools/issues/new/choose)，提供：

- 仓库 commit 或 release、游戏版本、操作系统和 CPU 架构。
- 使用入口与命令，个人路径替换为占位符。
- 复现步骤、预期行为和实际错误。
- 回放差异对应的事件/动作序号及不同的字段名。

提供最小合成示例或脱敏错误片段。游戏二进制、资源、存档、完整游玩日志、账号身份和凭据保留在本地。

## 提交修改

使用分支，每个 PR 聚焦一项行为，说明触发条件、修改后的行为及验证方式。修改未影响相关执行路径时，复用已有运行证据。

中英文使用文档保持一致。新增源码文件加入 `RELEASE_FILES.txt`；位于 `linux/` 下的文件还需加入该目录的发布清单与 `SOURCE_ORIGINS.json`。保留上游归属。

根据改动选择相关检查。在仓库根目录：

```bash
python3 -B scripts/audit_source.py
python3 -B -m unittest discover -s mac/tests -v
```

在 Linux 主机的 `linux/` 目录：

```bash
python3 -B -m unittest discover -s tests -v
python3 -B scripts/package_source.py
```

协议样例使用合成数据。运行或回放逻辑的改动可能还需要使用自己的兼容游戏与 profile 做一次有界检查。报告实际覆盖内容，并保留失败和中断结果。

贡献采用项目的 [MIT 许可](LICENSE)，保留既有[第三方归属](NOTICE.md)。
