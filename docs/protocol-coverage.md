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

0.1.7 补充内部命令 argv 数组与脚本工具包装的逐条镜像匹配；修复空命令摘要及重复文件修改行，保留原始工具输出、完成耗时和失败状态。强标识匹配过的记录不再次消耗其它真实相同调用；旧版命令 begin/end 按 call_id 合并。

0.1.8：thread/resume 使用官方 excludeTurns:true，随后分页读取历史；不再传入仅适用于thread/read的includeTurns字段。批量合入服务器会话时保留侧栏可见行，并取消已被后续用户输入替代的位置恢复。

命令展开内容使用实际执行脚本；镜像匹配到原生输出时优先呈现该输出，编排包装及原始返回仍保留在记录和导出中。

## JavaScript 工具编排

按 [OpenAI Programmatic Tool Calling](https://developers.openai.com/api/docs/guides/tools-programmatic-tool-calling) 的 `tools.*`、顶层 `await` 和 `text(...)` 输出模型解析已保存的编排输入。使用 Acornima ECMAScript AST，不执行记录中的 JavaScript。支持括号、注释、单双引号、模板/插值、静态字符串拼接、对象与数组、已知值展开、变量/工具别名与解构、常量分支、async 函数与 IIFE、结果成员访问、JSON 包装、then、Promise.all/allSettled、已知数组 map/forEach 和 for-of。

单次调用复用命令、补丁、等待、MCP 等原有语义；批次在一个折叠活动内逐项显示参数，原生镜像逐条补充结果与状态。实际执行的相同调用仍逐条保留。原始输入和聚合输出保留用于导出。运行时参数、未知条件/函数、无法确定的变更、超出深度/步骤/大小限制或未完成的代码保留折叠源码，不推测执行结果；这不是通用 JavaScript 解释器，也不保证还原任意程序。当前 CLI 的18种 ResponseItem 清单与网页 Responses API 的实验性 program/program_output 契约分别管理。
0.1.9：安装版真实记录补充 clock.sleep / clock__sleep 扩展别名与 duration_ms，按同一 sleep 投影、状态和 wire ID 合并，避免多出通用事件行。
0.1.10：连续状态通知沿用尚未完成的侧栏锚点。批量导入后紧接状态更新的真实交错先复现失败，修复后16项侧栏回归通过；恢复期间继续核对布局位置，后续用户输入取消过期恢复。

## 0.1.11：模型、生成阶段、提问和分页

按本机 0.164.0 JSON Schema 核对：thread/items/list 支持跨整个会话按 item 游标分页，UI 每次仅请求 100 项；thread/turns/list 的 notLoaded 视图仅用于取得运行轮次。历史 Turn/agentMessage 不保证带模型，不能用 modelProvider 或当前设置回填旧回复。文件 turn_context、thread/settings/updated、model/rerouted 与已观察 item 模型组成展示记录。

item/tool/requestUserInput（兼容旧 tool/requestUserInput）映射选择、可选自定义回答、自由与保密输入；响应采用 answers.{questionId}.answers。无自动默认提交；serverRequest/resolved、轮次完成、断联与来源切换撤销陈旧提问。

Responses API 的 output_item.added、函数/自定义/MCP 参数、代码生成与图像生成进度统一为生成占位；直接消息和推理继续流式呈现。安装版 app-server 没有暴露这些 Responses 生成增量，原生 item/started 仍代表实际活动，不虚构前置生成阶段。补充 MCP、file_search、code_interpreter、program/program_output 的已知 Responses 结果布局。

参考：[App-server](https://learn.chatgpt.com/docs/app-server)、[Responses streaming events](https://developers.openai.com/api/reference/resources/responses/streaming-events)、[Function calling](https://developers.openai.com/api/docs/guides/function-calling)。

## 0.1.12：列表分页与时间检索

0.1.19 补充：`thread/tokenUsage/updated` 的 `turnId` 可将用量定位到轮次，但该通知没有模型字段。`last` 为最近一次模型请求，`total` 为当时的会话累计量，界面分别标注，不将其冒充轮次总量。模型仅来自明确的本地上下文、已观察的配置、响应或路由记录；配置与实际报告在详情中区分。历史轮次详情按需读取 `thread/turns/list` 的 `itemsView: notLoaded`，缓存游标并限制遍历，不水合历史 items。该能力按本机 Codex CLI 0.164.0 生成的 schema 与官方文档交叉核对。

侧栏改用 thread/list 的 50 项游标页，显式指定 updated_at/desc，分别处理活动和归档目录。后续页不被视为新会话事件。连接与恢复保留原有内容时间；本地元数据按语义记录的 timestamp 与内容指纹判断变化，排除 turn_context 等元数据写入。真实内容更新以同一行对象移动位置，不冻结时间排序，也不重复插入已有行。

日期条件与关键词在后台组合执行；查询可跨尚未展示的元数据页，取消后不得提交旧游标/结果。日期使用本地日历日，范围包含结束日。服务器对日期没有等价过滤字段时在客户端过滤，并利用更新时间下界停止不必要的翻页。[官方 thread/list 文档](https://learn.chatgpt.com/docs/app-server)；[Microsoft 数据虚拟化指南](https://learn.microsoft.com/en-us/windows/apps/develop/performance/listview-and-gridview-data-optimization)。
