# 界面约定

主任务是选会话、读对话、在确实连接到运行时时进行干预。界面不使用宣传语、指标卡墙、彩色装饰徽章或自定义窗口控制按钮。

Windows 的 Fluent 负责视觉与交互：Mica 作为底层、LayerFillColorDefaultBrush 作为连续内容层、系统 Segoe 字体、原生列表/菜单/按钮/对话框、4/8/12/16/24 DIP 间距、8 DIP 内容圆角。颜色来自 ThemeResource，状态同时用文本表达。应用图标来自图像生成工具；应用内图标全部使用原生 Segoe Fluent Symbols，无需额外生成位图按钮。

Apple HIG 的 Writing 指南用于文案原则：按钮使用简短动词、错误说明下一步、避免口号、避免无意义的“我的/您的”。布局遵循 Windows，而不模拟 macOS 控件。

列表宽 320 DIP，900 DIP 以下切为覆盖抽屉。消息区最大阅读宽度 920 DIP；按钮最小高度 40 DIP，消息复制按钮 36×40 DIP。文字允许伸长和换行；标题仅在列表/页头省略，并提供完整标题 tooltip。设置和详情可滚动。输入框上限 200 DIP，Enter 换行，Ctrl+Enter 提交；提交失败保留文字；切换会话分别保留草稿。

本地化资源均在 Core 的 Localization 目录，使用语义键，后备为英语；UI、辅助功能名称、工具提示均从资源取值。语言切换重建展示模型；阿拉伯语/希伯来语使用 RTL，路径、代码与技术标识保持 LTR。数字和日期使用 CultureInfo。`{0}` 形式的格式字符串必须保持占位符一致。

使用原生键盘焦点、按钮自动化名称、输入辅助名称、菜单标签和消息摘要。原生主题资源负责对比主题。旁白完整性、200% 文本缩放、系统高对比及真机 ARM64 仍需要独立验证，不据此宣称无障碍认证。

参考文档：

* [Microsoft Mica material](https://learn.microsoft.com/en-us/windows/apps/design/style/mica)
* [Microsoft accessibility overview](https://learn.microsoft.com/en-us/windows/apps/design/accessibility/accessibility-overview)
* [Microsoft WinUI testing guidance](https://learn.microsoft.com/en-us/windows/apps/develop/testing/)
* [Apple HIG Writing](https://developer.apple.com/design/human-interface-guidelines/writing)
* [Codex app-server protocol](https://learn.chatgpt.com/docs/app-server)
* [OpenAI UI guidelines](https://developers.openai.com/plugins/concepts/ui-guidelines)

工具活动采用 OpenAI 文档中的系统色、系统字体、有限字号层级和一致网格间距原则，并以 Windows 原生按钮实现渐进展开。这里借用交互原则，不将 Web 的 Apps SDK UI 直接移植到 WinUI。每行 36 DIP；左侧为类型图标和动作摘要，右侧为可用耗时与展开箭头。默认不显示重复的“工具”、时间戳或复制栏；详细命令和输出各有复制入口。文件增删计数来自补丁或协议 diff，不凭模型文案估算。自定义原生按钮通过 ExpandCollapse 自动化模式暴露状态，键盘、焦点和主题反馈由 WinUI 提供。

# 应用图标

内置 imagegen 工具生成，透明 PNG 原图保存于 `src/CodexMonitor.App/Assets/AppIcon.png`，ICO 为同图的多尺寸编码，包含 16/24/32/48/64/128/256 像素。生成后保留透明度，未使用代码重绘图案。

最终提示词：

> Use case: logo-brand. Asset type: production Windows 11 desktop app icon for Codex Monitor, a quiet native tool for reading agent conversation transcripts. Create ONE icon, centered, on a truly transparent background, square 1024 composition. An elegant simple Fluent Design symbol: two slim upright rounded transcript panes slightly offset, front pane formed from softly rounded pale porcelain material with three dark charcoal horizontal message strokes; one small restrained azure accent at the lower edge suggesting live activity. Clear silhouette, crisp geometry, tiny amount of soft dimensional lighting in Microsoft Fluent app icon tradition, polished and understated, legible at 24 pixels, balanced generous transparent padding, symbol occupies 78% of image. No text, no letters, no OpenAI logo, no badge, no outer square tile, no decorative sparkles, no background, no tiny details. Strong edge separation on both light and dark backgrounds.
