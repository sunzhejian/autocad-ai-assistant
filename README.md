# AutoCAD AI 助手

[![回归测试](https://github.com/sunzhejian/autocad-ai-assistant/actions/workflows/regression.yml/badge.svg)](https://github.com/sunzhejian/autocad-ai-assistant/actions/workflows/regression.yml)
[![最新版本](https://img.shields.io/github/v/release/sunzhejian/autocad-ai-assistant)](https://github.com/sunzhejian/autocad-ai-assistant/releases/latest)
![AutoCAD 2024](https://img.shields.io/badge/AutoCAD-2024-red)
![Windows x64](https://img.shields.io/badge/Windows-x64-blue)

AutoCAD 2024 插件，提供启动绘图工具区和按需打开的 AI 助手，支持兼容 OpenAI Chat Completions 的模型、PDF 赛题导入、图片复刻和结构化几何绘制。当前版本 **1.3.1**，.NET Framework 4.8 / Windows x64。

**AutoCAD 2024 assistant for Windows:** native drawing tools, local vector PDF import, image-to-CAD plans, unit and hatch validation, atomic undo, and OpenAI-compatible model configuration. AI reconstruction still needs human review.

## 快速安装

从 [Releases](https://github.com/sunzhejian/autocad-ai-assistant/releases/latest) 下载 Windows 安装包及 SHA256 校验文件，解压后双击 `Install.cmd`；保存图纸并重启 AutoCAD 2024，即可看到完整工具区和“AI 制图”选项卡。二进制安装不需要编译工具；PDF 功能另需本地 Python/PyMuPDF。

[安装与故障排查](docs/INSTALL.md) · [参与开发与构建](CONTRIBUTING.md) · [更新记录](CHANGELOG.md) · [反馈问题](https://github.com/sunzhejian/autocad-ai-assistant/issues/new/choose)

| 场景 | 工作方式 |
| --- | --- |
| 文字或截图绘图 | 配置自己的兼容服务，生成并校验结构化计划 |
| 矢量 PDF 赛题 | 本地逐页处理，原生导入，无需模型请求 |
| 扫描 PDF / 局部细节 | 页预览或框选后由视觉模型重建 |
| 日常制图 | 27 个常用入口、完整原生功能区和可恢复的预览设置 |

v1.3.1 增加配置原子保存与备份恢复、启动偏好恢复命令、安装包完整性检查及公开提交检查。模型配置、密钥、赛题原文件和运行产物保留在本地，不随安装包分发。图片或文字发送到所配置的模型服务前，请确认输入适合交给该服务。

## 使用

1. 安装发布包，或编译 Release x64 后运行 `Install.ps1`；重启 AutoCAD，输入 `AI`。
2. 配置支持视觉输入的模型；密钥使用当前 Windows 用户的 DPAPI 加密保存在本地。
3. 点击“图/PDF”选择文件，或用 Ctrl+V 粘贴图片/PDF 文件。图片默认发送一张最长边 1600px 的增强图；单视图优先“框选”，并在输入中保留源图单位说明。
4. 复杂大图可勾选“精细分块”，同时发送整图和四张重叠高清图，模型处理时间可能增加。
5. 绘图前自动校验 JSON、尺寸与几何、填充边界及内孔、块依赖与指令预算；可修正一次，仍不通过则停止。生成后读取原生尺寸、填充面积与内孔数，结果不一致则整批回滚。完成后一次 `U` 撤销整批。
6. “复用已执行计划”默认开启。相同图片内容、提示、模型配置、协议与分块模式可复用七天内的计划；取消勾选可重新识别。该缓存只证明计划曾成功执行，源图识别仍需核对。

`AIPLAN` 可读取本地 JSON 计划；`AIDRAW` 为面板的绘图队列命令。请求中“发送”变为“取消”，图片请求总预算 240 秒、文本 120 秒，包含重试和自动修正。切换/关闭原图形后不会把结果画入另一张图。

### PDF 赛题

附加 PDF 后可输入页码并“切换页”。矢量页默认原生导入，不需要配置或调用模型；扫描页、混合页默认 AI 重建。空白页禁止发送。PDF 框选后按图片重建局部，保留整页时才可原生导入。

原生导入保留整页**纸面布局**，放大倍数默认 `1`。例如单一 1:100 视图可按实际需求放大 100 倍；整页含 1:500、1:100、1:200 多种比例时，不要用一个倍数冒充各视图模型 1:1。已测试毫米到厘米的显式转换，并修正原生 PDF 英寸基准带来的 25.4 倍偏差。

PDF 原生标注仍是线条和文字；字体替换可能使文字移位。混合页的位图保留为外部参照，不会自动变成线条，跨机分享需打包依赖。文字要求由 AI 重建处理；原生导入前需清空输入，以免忽略用户要求。AI 重建会收到该页预览和提取的文字证据，不能识别的 SHX 数字仍需看图核对。

PDF 处理在本地独立 Python 进程中完成，依赖另行安装的 `PyMuPDF`；本机已验证 Python / PyMuPDF 1.27.2。可用 `CADAI_PYTHON` 指向 `python.exe`。不随插件分发其第三方二进制；该依赖采用 [AGPL/商业双许可](https://pymupdf.readthedocs.io/en/latest/about.html)，发行时需按实际授权处理。文件上限 128 MB，每次处理一页，45 秒超时；切换或移除附件会取消处理。页面快照、预览和哈希缓存位于本地 `sources` 目录，不上传 GitHub。

### 原生结果校核

本地 `%LOCALAPPDATA%\AutoCAD.AI.Assistant\receipts` 保存每次执行的 JSON 回执，包含计划/来源哈希、嵌套指令编号、对象句柄、图层、尺寸测量值、填充面积、纹理及内孔。回执核对的是**实际生成结果与计划**，`source_accuracy_verified` 保持 `false`，不把可执行性当作原图识别正确率。

请求期间改动图元、单位或模型/布局，或切换/关闭图形，会停止自动落图并保留 AI 计划。v1.2.0 修复了直径标注端点导致的测量偏差，新增填充自交、回折尖刺、内孔越界、接触、重叠和嵌套检查。详见 [v1.2.0 验收记录](docs/upgrade-1.2.0-2026-11-05.md)。

### 恢复 CAD 界面

启动时显示完整功能区、菜单栏、命令行、文件选项卡、状态栏和文件对话框，并添加“AI 制图”选项卡：AI 助手/图纸复刻、本地计划、直线/多段线/矩形/圆、填充/编辑填充、移动/复制/偏移/修剪/圆角/镜像、尺寸标注/图层/特性/快速选择、全图/平移和预览性能开关。按钮调用现有原生工具，AI 模型只在发送任务时调用。

启动发现没有图形时，使用本机 `acadiso.dwt` 创建毫米制空白图；已有图形保留原单位。设置 `STARTUP=0`、`STARTMODE=0` 避免再次进入无图形的灰色界面。首次修改前将六项应用设置备份到本地 `startup-ui-original.json`。`AIUI` 可重新整理界面；`AIUIOFF` 关闭下次启动整理，保留当前界面与已设置的应用偏好。无图形、缺少毫米模板时不擅自使用英制模板。

启动操作通过一次空闲回调排队，完成或失败后解绑，等待忙碌命令最多 20 次回调；不在鼠标移动、选择或缩放时轮询。CoreConsole 跳过全部启动界面代码。AI 面板、模型设置和聊天图片仍按需初始化。

`AIUIRESTORE` 恢复首次备份的六项原始启动偏好并关闭启动整理；当前图纸和功能区保留。备份读取完整校验变量名、整数类型及范围，损坏或额外变量会阻止修改。应用设置失败时尝试还原执行前的值。

功能区被手动折叠时也可双击当前选项卡，或点击选项卡右侧的最小化按钮；`Ctrl+9` 显示/隐藏命令行。切换工作空间后自定义选项卡若被重建，可输入 `AIUI` 恢复。

也可用 `APPLOAD` 加载 [Restore-Cad-UI.lsp](tools/Restore-Cad-UI.lsp)，输入 `CADAIUI`，恢复菜单栏、功能区、命令行、文件选项卡、状态栏和文件对话框；它不重置配置或改动图纸，折叠的功能区仍需双击展开。

### 交互性能

v1.2.1 默认关闭命令频次统计；可在“指令集”勾选“统计命令使用次数”，仅在 AI 面板显示时监听，关闭面板后暂停。计数与日志采用后台合并写盘；面板刷新会合并排队，空闲时没有轮询任务。原生绘图不再强制开启线宽显示。

聊天保留最近 40 条可见消息和 12 条完整上下文消息（实际模型请求仍选最近 8 条）；长消息只展示前 8000 字符。聊天图片在后台解码，最长边 480 像素、最多同时解码 2 张；附件预览最长边 256 像素，模型仍使用原图处理流程。

鼠标移动或选择仍不顺畅时，可输入 `AIPERF` 关闭悬停、命令、填充实时预览和悬停属性提示；执行前保存原值。`AIPERFRESTORE` 恢复原值。备份为本地 `preview-settings.json`，重复启用不会覆盖最初备份。安装按内容哈希并存 DLL，保存图纸后重启 CAD，面板名称应显示 **AI 1.3.1**。详见 [基础性能验收](docs/performance-1.2.1-2026-11-05.md) 与 [大图性能验收](docs/performance-1.2.2-2026-11-06.md)。

v1.2.2 图片复刻跳过旧图上下文；文字请求最多抽样 512 个图元、读取 128 个简单范围，以 25ms 为扫描目标（单次原生调用不可中断）。返回的类型与范围明确标为抽样，CAD 命令执行中则跳过扫描。面板的缓存读取与计划预检在后台运行，执行前再次核对图形状态。

绘图后根据本次新增模型对象的范围调整视图，内容已在视野内时保持缩放；透视视图和布局不自动定位模型范围。普通新增几何使用屏幕更新，影响显示的绘图变量变化时执行重生成。等待 AI 期间平移、缩放视图可以继续，图元修改仍会停止自动落图。

## 准确性机制

图片计划声明 `sourceUnit: mm/cm/m`、`coordinateUnit: source` 和可见 `views`。模型按源图单位给出坐标，插件在本地统一换算为毫米。兼容旧的毫米坐标计划（省略 `coordinateUnit`）。高程与普通尺寸使用不同单位时，高程作为文字保留。

尺寸标注附源图 `value` 与 `confidence: read/inferred/unknown`。测量值必须与坐标一致；固定文字不能覆盖真实测量值。推断尺寸以橙色和“（估）”显示。图框、字高及标注样式分别处理纸面比例与实体单位；空心截面可通过 `hatch.holes` 保留内孔。

本地检查能拦截单位、几何及协议错误，不能证明所有像素都被正确理解。复杂全图、模糊小字和扭面仍需人工核对。[实测记录](docs/verification-2026-11-05.md) 包含成功样本、失败尝试和局限。

填充可指定 `material: riprap/gravel/sand/earth/clay/concrete`，分别表达块石、碎石、砂、土、粘土和混凝土。预设按当前 DIMSCALE 控制纸面纹理大小，薄长区域与短区域保持同样疏密；`spacing` 可直接指定源坐标单位下的纹理特征尺寸，`scale` 调整疏密。填充过密时整批停止并回滚。复杂 CAD 图案的预览以 AutoCAD 原生 PDF 为准，通用 DXF 渲染器可能漏绘填充线。

## 编译与验证

依赖 AutoCAD 2024 安装目录中的 `acmgd.dll`、`acdbmgd.dll`、`accoremgd.dll`、`AdWindows.dll`，以及 .NET Framework 4.8 开发工具。默认目录 `D:\AutoCAD 2024`，可通过 MSBuild 的 `AutoCAD2024Path` 属性修改。详细命令见 [CONTRIBUTING.md](CONTRIBUTING.md)。

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File tools\Test.ps1
powershell -NoProfile -ExecutionPolicy Bypass -File tools\Test.ps1 -BuildPlugin
powershell -NoProfile -ExecutionPolicy Bypass -File tools\Test-Cad.ps1
python -m unittest discover -s tests -p test_pdf_worker.py -v
powershell -File tools\Test-Pdf-Cad.ps1 -SourcePdf '赛题.pdf' -Page 3 -PlotPdf
.\Install.ps1
```

纯逻辑和本地 HTTP 回归不依赖 CAD 许可，在 GitHub Actions 的 Windows 环境执行。原生集成验证需要本机 AutoCAD，使用新建的临时图形。后台验证和复刻统一通过 `run_console.py` 的 `/isolate` 独立配置运行，防止脚本的 `FILEDIA=0` 等设置影响日常 CAD 配置，异常退出也保持隔离。该开关的用途见 [Autodesk 官方说明](https://blog.autodesk.io/getting-started-with-accoreconsole/)。v1.3.1 当前为 98 项纯回归和 30 项原生集成检查；GitHub CI 另执行 PDF worker 测试和提交文件检查，不声称在云端完成有许可的原生 CAD 验证。

## 本地题库复刻

```powershell
python tools\prepare_corpus.py 'D:\AutoCAD 2024\高教杯水利赛道'
powershell -File tools\Reproduce-Cad.ps1 -Plan tests\fixtures\sixth-section33.json -Name sixth-section33
python tools\render_reproductions.py
powershell -File tools\Reproduce-Cad.ps1 -Plan tests\fixtures\hebei-earth-dam-reviewed.json -Name hebei-earth-dam -PlotPdf
```

PDF 工具依赖 `pymupdf`；预览工具依赖 `ezdxf` 和 `matplotlib`。`ModelBenchmark.cs` 使用实际插件 DLL 与已有本地模型配置发送真实请求；生成可执行文件后，其参数为 `插件.dll 图片.png 结果.json [提示]`。真实请求会使用所配置服务的额度。

已索引题库目录内 17 份 PDF、637 页，包含通知、标准、真题和答案；已选取第五届整图、第六届 3-3 断面、第七届扭面大样及河北样题土坝图做复刻。原始 PDF、图片、密钥、本地模型配置和运行产物均不上传。仓库保留三个模型生成计划及一个人工核对后的土坝计划，作为可重复的回归样本。[土坝复刻记录](docs/hebei-earth-dam-2026-11-05.md) 区分了模型输出与人工修订。本次工作不涉及远程模型权重微调。
