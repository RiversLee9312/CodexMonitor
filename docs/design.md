# 界面约定

主任务是选会话、读对话、在确实连接到运行时时进行干预。界面不使用宣传语、指标卡墙、彩色装饰徽章或自定义窗口控制按钮。

Windows 的 Fluent 负责视觉与交互：Mica 作为底层、LayerFillColorDefaultBrush 作为连续内容层、系统 Segoe 字体、原生列表/菜单/按钮/对话框、4/8/12/16/24 DIP 间距、8 DIP 内容圆角。颜色来自 ThemeResource，状态同时用文本表达。对比主题关闭 Mica，改用系统不透明背景。已核对全部应用图标的语义；已有准确对应的动作使用 Segoe Fluent Symbols，十种语义缺口使用内置 image_gen 生成的单色透明蒙版，清理过程与提示词见 [图标记录](icon-generation.json)。

Apple HIG 的 Writing 指南用于文案原则：按钮使用简短动词、错误说明下一步、避免口号、避免无意义的“我的/您的”。布局遵循 Windows，而不模拟 macOS 控件。

列表宽 320 DIP，900 DIP 以下切为覆盖抽屉。消息区最大阅读宽度 920 DIP；交互按钮至少 40×40 DIP。文字允许伸长和换行；标题仅在列表/页头省略，并提供完整标题 tooltip。设置和详情可滚动。输入框上限 200 DIP，Enter 换行，Ctrl+Enter 提交；提交失败保留文字；切换会话分别保留草稿。

本地化资源均在 Core 的 Localization 目录，使用语义键，后备为英语；UI、辅助功能名称、工具提示均从资源取值。语言切换重建展示模型；阿拉伯语/希伯来语使用 RTL，路径、代码与技术标识保持 LTR。数字和日期使用 CultureInfo。`{0}` 形式的格式字符串必须保持占位符一致。

使用原生键盘焦点、按钮自动化名称、输入辅助名称、菜单标签和消息摘要。新增内容通过 UIA 通知适度播报，不抢焦点；流式内容更新保留展开状态与滚动位置。详情见 [无障碍适配与验证边界](accessibility.md)，不据此宣称无障碍认证。

参考文档：

* [Microsoft Mica material](https://learn.microsoft.com/en-us/windows/apps/design/style/mica)
* [Microsoft accessibility overview](https://learn.microsoft.com/en-us/windows/apps/design/accessibility/accessibility-overview)
* [Microsoft WinUI testing guidance](https://learn.microsoft.com/en-us/windows/apps/develop/testing/)
* [Apple HIG Writing](https://developer.apple.com/design/human-interface-guidelines/writing)
* [Codex app-server protocol](https://learn.chatgpt.com/docs/app-server)
* [OpenAI UI guidelines](https://developers.openai.com/plugins/concepts/ui-guidelines)

工具活动采用 OpenAI 文档中的系统色、系统字体、有限字号层级和一致网格间距原则，并以 Windows 原生按钮实现渐进展开。这里借用交互原则，不将 Web 的 Apps SDK UI 直接移植到 WinUI。每行最小 40 DIP；左侧为 16 DIP 类型图标和动作摘要，右侧为可用耗时与展开箭头。推理摘要直接显示。默认不显示重复的“工具”、时间戳或复制栏；详细命令和输出各有复制入口。文件增删计数来自补丁或协议 diff，不凭模型文案估算。自定义原生按钮通过 ExpandCollapse 自动化模式暴露状态，键盘、焦点和主题反馈由 WinUI 提供。

工具组最多占 480 DIP，长输出最多占 320 DIP，各层允许滚动链传递。在组内回看会暂停整段对话的自动跟随；新回复出现时保留位置，点击“转到最新内容”后再恢复。新会话不会触发全列表动画。

摘要按协议标记直接显示；明文思考以“思考”单行入口默认折叠，展开后可阅读、滚动并保留展开状态。加密字段不进入界面。向下箭头为消息区域内的 40 DIP 圆形悬浮按钮，不占布局行；离开底部显示，到底部隐藏。过渡为 120 ms 淡入淡出与 6 DIP 位移，关闭系统动画时立即切换。隐藏时从 Tab 顺序移除，若正持有键盘焦点则返回消息列表。

“开源许可证”位于设置内，通过同一原生对话框进入。组件选择器显示名称与版本，原文独立滚动、可选择，法律原文不翻译且保持 LTR。返回时保留设置草稿与滚动位置，不嵌套第二个模态窗口。新增文案与辅助名称共用 22 种语言的语义资源。

图标母版为 1024 像素，使用 20/24 安全关键线；运行时导出 100/125/150/200/250/300/400% 七档资源，由 BitmapIcon 按主题着色。Python 批处理检查透明边界、连通部件、空洞与表观线宽，并输出深浅背景检查图；用户指出的白色雾状低 alpha 噪点已去除。字体图标不随系统文字大小放大，文字保持可缩放。

# 应用图标

内置 imagegen 工具生成，透明 PNG 保存于 `src/CodexMonitor.App/Assets/AppIcon.png`，ICO 为同图的多尺寸编码，包含 16/24/32/48/64/128/256 像素。原始母版保存在 `design/app-icon/AppIcon.generated.png`。仅清除接近透明的背景杂点，保留彩色材质与有意的阴影，未使用代码重绘图案。

最终提示词：

> Use case: logo-brand. Asset type: production Windows 11 desktop app icon for Codex Monitor, a quiet native tool for reading agent conversation transcripts. Create ONE icon, centered, on a truly transparent background, square 1024 composition. An elegant simple Fluent Design symbol: two slim upright rounded transcript panes slightly offset, front pane formed from softly rounded pale porcelain material with three dark charcoal horizontal message strokes; one small restrained azure accent at the lower edge suggesting live activity. Clear silhouette, crisp geometry, tiny amount of soft dimensional lighting in Microsoft Fluent app icon tradition, polished and understated, legible at 24 pixels, balanced generous transparent padding, symbol occupies 78% of image. No text, no letters, no OpenAI logo, no badge, no outer square tile, no decorative sparkles, no background, no tiny details. Strong edge separation on both light and dark backgrounds.

工具组默认展开，标题可收起为工具类别及数量概括；仅正在进行的组在收起时显示进度环。可观测的非消息、非推理生成阶段使用同一“工作中”占位，生成完成后原位替换，不显示半截参数。每条 Codex 回复标题旁显示实际可用的模型记录，缺失时留空。

历史记录从尾部开始，本地每页约 300 条 JSONL 记录（通常不超过 2 MiB），服务器使用 thread/items/list 的 100 项游标页，避免巨型单轮被整体水合。向上滚动接近已加载范围起点时自动预取；按稳定消息 ID 合并，保留外层和工具组内第一条可见内容的像素位置。没有额外的“加载更早内容”按钮。

运行时尚未提供轮次时，底部显示说明与“重新连接”按钮。Codex 提问采用原生对话框，无预选答案，显式提交；保密字段使用 PasswordBox，失效或断线自动关闭，不提交陈旧回答。所有新文案覆盖 22 种语言。
