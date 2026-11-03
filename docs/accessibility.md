# 无障碍适配

依据 Microsoft 的 [WinUI 检查清单](https://learn.microsoft.com/en-us/windows/apps/design/accessibility/accessibility-checklist)、[测试指南](https://learn.microsoft.com/en-us/windows/apps/design/accessibility/accessibility-testing)、[文字缩放](https://learn.microsoft.com/en-us/windows/apps/develop/input/text-scaling)、[键盘交互](https://learn.microsoft.com/en-us/windows/apps/design/accessibility/keyboard-interactions)与[对比主题](https://learn.microsoft.com/en-us/windows/apps/design/accessibility/high-contrast-themes)指南实施。

## 已实现

* 原生按钮、列表、菜单、输入框与对话框；键盘焦点使用系统视觉，不移除焦点边框。
* 每个动作都有本地化名称与必要的帮助文本；会话筛选、查找、消息输入、列表有名称，主要区域具有标题层级。
* 工具展开按钮提供 Invoke 和 ExpandCollapse UIA 模式、状态改变事件、Enter/Space 及 RTL 方向键支持。禁用时拒绝执行模式调用。
* 适配后的工具条目以用途、摘要和状态提供可访问名称，避免朗读整块 JSON。命令、路径、URL 保持 LTR，可选择和复制。
* 新会话和连接结果采用适度 UIA 通知；常规流式更新不反复播报数量、不抢焦点。关闭查找与对话框返回原来的有效控件。
* 内容区、侧栏和长详情允许键盘滚动；组内回看暂停自动跟随。更新保留阅读位置、展开状态和工具按钮焦点。
* 文本继承系统缩放，不固定行高；窄窗口可换行，侧栏转为覆盖抽屉。图标保持固定 DIP 大小，按钮目标至少 40 DIP。
* 系统语义色随深浅与对比主题变化；状态同时有文字或形状。监听 ThemeSettings.Changed，对比主题改为不透明系统背景并关闭 Mica。原生标题栏按钮颜色由 Windows 管理。
* 22 种语言共享语义资源，阿拉伯语与希伯来语适配 RTL。位图图标通过 BitmapIcon 蒙版按当前前景色着色，覆盖 100–400% DPI。
* “转到最新内容”悬浮按钮具有本地化名称与工具提示，隐藏时不可点击或 Tab 到达；过渡尊重系统动画偏好。摘要直接可读，折叠的明文思考不会作为列表项名称被整段朗读。
* 许可证页使用原生组件选择器、可选择原文与独立滚动区域，返回保留设置草稿及焦点；RTL 界面中的许可原文保持 LTR。

## 本机验证

Windows 11 x64、200% 显示缩放，2026-11-03–04。

MSTest 在生产 XAML 上验证控件名称、Invoke/ExpandCollapse、禁用状态、焦点返回、图标缩放和阅读位置。另以 225% 字号压力验证 560 DIP 窄窗口的中文、德语与阿拉伯语排版。字号压力通过测试宿主逐控件放大，不等同于更改 Windows 的全局 Text size 设置。

微软 Axe.Windows 2.4.2 由独立只读扫描进程检查合成测试窗口，覆盖浅色中文、深色德语、深色 RTL 、许可证页及服务器状态五种场景。报告保存在本地 `artifacts/accessibility`。GDI 屏幕截图在当前桌面会话可能返回句柄错误，此时重新运行完整规则扫描并保留 JSON 报告，明确记录 ScreenshotUnavailable；MSTest 同时附上 WinUI RenderTargetBitmap 截图。不会用旧报告代替失败的扫描。扫描进程不上传数据。

真实窗口已检查窄屏与宽屏、深浅标题栏及查找的键盘操作。系统“对比主题”设置页在本机未成功加载，因此未完成系统对比主题的人工切换核验；测试没有更改系统显示设置。Narrator 的语音全流程、实际全局 Text size、Windows 10 与 ARM64 真机仍需独立验证。自动扫描与布局断言不能代表无障碍认证。

## 重现

运行 `scripts/Test-Layout.ps1`。如需人工查看合成测试数据：

```powershell
dotnet run --project tests/CodexMonitor.UiTests -p:Platform=x64 -- --manual-review
```

该模式不会连接 CLI、读取真实 transcript 或保存设置。不要在同一测试输出目录同时运行自动套件和人工窗口。
