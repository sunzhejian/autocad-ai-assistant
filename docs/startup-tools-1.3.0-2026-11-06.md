# v1.3.0 启动工具验收

日期：2026-11-06；主机：Windows / 简体中文 AutoCAD 2024；目标是解决启动灰屏、工具区折叠及常用入口不足。

## 变化

- 原会话读取到 `STARTUP=3`、`STARTMODE=0`、`MENUBAR=0`，未打开图形。手动从 `acadiso.dwt` 新建后 `INSUNITS=4`，但功能区仍折叠。此组合与官方 [STARTUP](https://help.autodesk.com/cloudhelp/2024/ENU/AutoCAD-LT/files/GUID-941B7406-2342-47EA-9881-BAFF3A554310.htm)、[STARTMODE](https://help.autodesk.com/cloudhelp/2024/ENU/AutoCAD-Core/files/GUID-143F3607-7D7F-4CC9-9BE4-2A221DF2D776.htm) 的行为相符。
- 增加轻量启动初始化。没有文档时从本地毫米模板创建；有文档时保留原文档与单位。设置应用启动偏好，并排队显示完整 Ribbon、菜单、命令行、文件选项卡和状态栏。首次六项设置备份在本地 `startup-ui-original.json`，不上传仓库。
- 增加“AI 制图”原生 Ribbon 选项卡，五组共 26 个按钮，调用现有 CAD 或插件命令。图标使用冻结的 WPF 矢量图形，无网络下载、鼠标轮询、聊天初始化或自动模型调用。
- `AIUI` 重新显示并启用启动整理；`AIUIOFF` 通过本地标志关闭下次启动整理，当前界面和应用偏好保留。切换工作空间若移除自定义选项卡，可运行 `AIUI`。
- 空闲回调在创建文档、排队命令之前解绑，防止文档创建消息泵重入；忙碌时最多等待 20 次回调。失败后解绑并记本地日志。CoreConsole 以进程名提前排除，不访问 UI 包装器。

## 验证

- `tools/Test.ps1 -BuildPlugin`：87 项纯回归通过；最终 Release 编译通过。环境仍有 .NET 4.8 Targeting Pack 缺失、回退 GAC 的既有 MSB3644 警告。
- `tools/Test-Cad.ps1`：最终 DLL 的 30 项原生检查通过，新增无界面宿主不保留 Idle 订阅的检查；原有几何、标注、填充、撤销、预览恢复和修订保护检查均通过。
- 安装器验证 DLL 和 PDF worker 哈希相同；清单为 1.3.0 且 `LoadOnAutoCADStartup=True`，按版本及哈希并存安装。
- 真实桌面重启后，无需输入命令即出现 Drawing1、完整菜单/功能区/命令行/文件标签/状态栏与带图标的 AI 制图选项卡。读取 `DBMOD=0`、`INSUNITS=4`、`STARTUP=0`、`STARTMODE=0`。空的图纸集面板已收起。
- 点击“圆”显示原生 `CIRCLE` 圆心提示，随后取消，未创建测试图元。实测中发现 AdWindows 向 ICommand 传入 RibbonButton，已修复为读取该按钮的 CommandParameter；修复后通过上述点击验证。
- 第二次专门恢复原先的 `STARTUP=3 / STARTMODE=0` 再关闭无改动的空白会话。用最终 DLL 重启后仍自动创建 Drawing1 并展开全部工具；再次读取为 `INSUNITS=4 / STARTUP=0 / STARTMODE=0 / DBMOD=0`，确认覆盖无图形启动路径且没有残留测试图元。

本次验证覆盖启动与工具入口，不声称 AI 识别准确率或鼠标帧率提升，也没有以真实用户赛题调用模型。
