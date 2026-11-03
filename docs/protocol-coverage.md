# 协议显示覆盖

2026-11-04，按 OpenAI 官方文档及本机 codex-cli 0.164.0 生成的 JSON Schema 核对。版本差异以本机 schema 为合同，未知扩展保留安全的只读原文入口。清单记录于 [protocol-coverage.json](protocol-coverage.json)，可运行 `scripts/Audit-Protocol.ps1` 重新生成 schema 并比较类型集合；不启动推理或修改会话。

## 核对范围

* [App Server](https://learn.chatgpt.com/docs/app-server)：生命周期、历史分页、流式消息、推理、命令、文件、MCP、动态工具、子代理、等待、审批、会话状态及实验性实时通知。
* [Non-interactive mode](https://learn.chatgpt.com/docs/non-interactive-mode) 与 [Codex SDK](https://learn.chatgpt.com/docs/codex-sdk)：JSONL 的 thread/turn/item 生命周期、SDK 的 snake_case 条目、消息与失败记录。
* [Responses multi-agent](https://developers.openai.com/api/docs/guides/responses-multi-agent)：代理间的 `agent_message` 与调用/输出。它和 SDK 中同名的普通助手回复根据 author/recipient 字段区分；不读取 encrypted_content。
* 本地 rollout 的 `response_item`、`event_msg.item_completed` 和旧版消息/工具事件。已完成事件中的内部 PascalCase 条目先归一化，再走同一显示适配器。

## 共享条目适配

本机 schema 的全部 **19 种 ThreadItem** 均有明确投影，另兼容文档旧称 `collabToolCall`。本地与服务器通过 `TranscriptItemAdapter`、`ToolActivity` 共享语义和图标，不再各自猜类型。

消息保持用户/助手方向；摘要直接展示，明文思考折叠；命令展示命令与可读输出；文件修改展示文件名、增删行与差异；MCP 展示应用/工具、参数、文本或资源类型；动态调用先识别已知用途；原生 sleep、Extension sleep 及动态/MCP 等待使用同一时钟与状态。协作调用和子代理活动展示目标、提示、状态；代理间消息展示发送方、接收方和明文。计划、网页搜索/打开/查找、图像查看/生成、审查、压缩、hook 提示和独立工具输出均有专用类别。

全部 **18 种持久化 ResponseItem** 均登记处理，包括本身代表未知扩展的 `other`：消息、推理、函数/自定义工具调用与输出、本地 shell、工具搜索及动态工具列表、网页/图像生成、代理消息、压缩及配置记录。已知类型不直接铺开 JSON；未知类型保留折叠原文。加密块、令牌、音频和 SDP 等传输数据不会被作为对话文字显示。

## 通知路由

全部 **85 种 ServerNotification** 已逐项登记：18 种流式更新、30 种活动、18 种会话通知、19 种服务器通知。完整名称、路由和字段见 JSON 清单。

流式 reducer 按 item ID 更新同一条记录：助手文本、摘要/思考、计划、命令/补丁输出、补丁更新、MCP 进度、终端输入与实时文字。最终 item 替换草稿；保留已输入的终端文字，完成后不再接受迟到进度；中断和失败结束等待状态。无 item ID 的实验性实时文本在每段完成后分配下一段标识，避免覆盖上一句话。

计划、差异、使用量、配额、警告/重试、hook、模型切换、身份恢复、自动审批审查、缓冲、压缩及实时状态进入紧凑活动区。会话通知更新可用的名称、归档/运行状态，其余会话元数据作为结构化记录。账号、应用、插件、技能、项目、文件监听与服务器进程等通知保存在详情的“服务器状态”，按类型更新最近值，不挤占对话正文。字段有已知语义时使用本地化标签，其余字段展开为可读名称和值。

旧版消息增量可保留尚未完成的文字，完整消息替换草稿。内部 FileChange 的路径字典、Add 内容与 Update unified_diff 转成同一文件差异；MCP 根级连接器信息与 Rust 时长也统一映射。本地原始响应和内部完成镜像按 wire ID 优先合并，再逐条消费文本镜像；重复发送的真实相同消息不会被全局去重。未知旧版事件同样保留安全原文，而非静默丢弃。

## 范围边界

显示通知不等于实现所有操作 API。保留已有命令/文件审批；尚不提供完整 OAuth、动态工具执行、音频播放、SDP 连接或所有实验性交互控件。二进制和加密内容不解码。未知服务端请求明确拒绝；未知事件只读降级。协议扩展仍需运行清单比较和补充适配。

未连接时本地文件、连接该会话后 app-server 的单一来源规则保持不变。连接、主动/意外断联、重新连接、迟到文件读取与历史/事件交错均有回归测试。没有向真实用户会话发送测试指令。
