# 会话连接能力核实

2026-11-03 查阅 OpenAI 官方文档。不能笼统地说“独立 CLI 进程无法连接”。

* 共享服务器：CLI 支持后台服务器和 `--remote`，客户端可使用同一 WebSocket 或本地控制套接字。`--no-daemon` 则绕过共享后台服务器。[CLI 命令与选项](https://learn.chatgpt.com/docs/developer-commands?surface=cli)
* 运行时归属：`thread/list` 包含存储的会话，`thread/loaded/list` 才列出该服务器内存中加载的 ID。`thread/read` 不加载或订阅会话。相同主目录或历史 ID 不足以证明当前轮次由该服务器承载。[App Server](https://learn.chatgpt.com/docs/app-server#threads)
* 恢复历史：`thread/resume` 可继续存储的会话；这不等于接管另一进程正在执行的轮次。文档没有提供按 PID 热接入任意运行中的 CLI 进程的接口。这是对已公开协议能力的判断，而非声称所有 CLI 模式永远不能连接。

应用因此在打开活动或状态未确认的会话时进行只读预检。确认归属才提供连接入口，点击后再次核对。未发现时提示“未找到此会话的应用服务器”，不根据日志来源直接断言 CLI 不支持连接。读取历史和扫描不会开始推理回合。

界面映射依据同一文档的 Items、Item deltas、Warning events、Turn events 与 Errors：可读推理摘要、计划、命令、文件差异、MCP/自定义工具、协作、网页、图像、审查、上下文压缩，以及错误/重试/中断、钩子、模型、MCP 启动、Token 和用量事件。最终 `item/completed` 为权威结果；不展示原始或加密推理。
