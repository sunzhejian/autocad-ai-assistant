# AutoCAD AI 助手

AutoCAD 2024 的按需加载插件，支持兼容 OpenAI Chat Completions 的模型、图纸图片复刻和结构化几何绘制。当前版本 **1.1.1**，.NET Framework 4.8 / Windows x64。

## 使用

1. 编译 Release x64，运行 `Install.ps1`，重启 AutoCAD，输入 `AI`。
2. 配置支持视觉输入的模型；密钥使用当前 Windows 用户的 DPAPI 加密保存在本地。
3. 粘贴或选择图纸图片。默认发送一张最长边 1600px 的增强图；单视图优先“框选”，并在输入中保留源图单位说明。
4. 复杂大图可勾选“精细分块”，同时发送整图和四张重叠高清图，模型处理时间可能增加。
5. 绘图前自动校验 JSON、尺寸与几何、块依赖及指令预算；可修正一次，仍不通过则停止。绘图期间任一原生命令失败，整批回滚。完成后一次 `U` 撤销整批。
6. “复用已执行计划”默认开启。相同图片内容、提示、模型配置、协议与分块模式可复用七天内的计划；取消勾选可重新识别。该缓存只证明计划曾成功执行，源图识别仍需核对。

`AIPLAN` 可读取本地 JSON 计划；`AIDRAW` 为面板的绘图队列命令。请求中“发送”变为“取消”，图片请求总预算 240 秒、文本 120 秒，包含重试和自动修正。切换/关闭原图形后不会把结果画入另一张图。

### 恢复 CAD 界面

功能区折叠时双击当前选项卡，或点击选项卡右侧的最小化按钮展开完整面板；`Ctrl+9` 显示/隐藏命令行。打开文件变成输入路径时，将 `FILEDIA` 设为 `1`。

也可用 `APPLOAD` 加载 [Restore-Cad-UI.lsp](tools/Restore-Cad-UI.lsp)，输入 `CADAIUI`，恢复菜单栏、功能区、命令行、文件选项卡、状态栏和文件对话框；它不重置配置或改动图纸，折叠的功能区仍需双击展开。

## 准确性机制

图片计划声明 `sourceUnit: mm/cm/m`、`coordinateUnit: source` 和可见 `views`。模型按源图单位给出坐标，插件在本地统一换算为毫米。兼容旧的毫米坐标计划（省略 `coordinateUnit`）。高程与普通尺寸使用不同单位时，高程作为文字保留。

尺寸标注附源图 `value` 与 `confidence: read/inferred/unknown`。测量值必须与坐标一致；固定文字不能覆盖真实测量值。推断尺寸以橙色和“（估）”显示。图框、字高及标注样式分别处理纸面比例与实体单位；空心截面可通过 `hatch.holes` 保留内孔。

本地检查能拦截单位、几何及协议错误，不能证明所有像素都被正确理解。复杂全图、模糊小字和扭面仍需人工核对。[实测记录](docs/verification-2026-11-05.md) 包含成功样本、失败尝试和局限。

填充可指定 `material: riprap/gravel/sand/earth/clay/concrete`，分别表达块石、碎石、砂、土、粘土和混凝土。预设按当前 DIMSCALE 控制纸面纹理大小，薄长区域与短区域保持同样疏密；`spacing` 可直接指定源坐标单位下的纹理特征尺寸，`scale` 调整疏密。填充过密时整批停止并回滚。复杂 CAD 图案的预览以 AutoCAD 原生 PDF 为准，通用 DXF 渲染器可能漏绘填充线。

## 编译与验证

依赖 AutoCAD 2024 安装目录中的 `acmgd.dll`、`acdbmgd.dll`、`accoremgd.dll`，以及 .NET Framework 4.8 开发工具。默认目录 `D:\AutoCAD 2024`，可通过 MSBuild 的 `AutoCAD2024Path` 属性修改。

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File tools\Test.ps1
powershell -NoProfile -ExecutionPolicy Bypass -File tools\Test.ps1 -BuildPlugin
powershell -NoProfile -ExecutionPolicy Bypass -File tools\Test-Cad.ps1
.\Install.ps1
```

纯逻辑和本地 HTTP 回归不依赖 CAD 许可，在 GitHub Actions 的 Windows 环境执行。原生集成验证需要本机 AutoCAD，使用新建的临时图形。后台验证和复刻统一通过 `run_console.py` 的 `/isolate` 独立配置运行，防止脚本的 `FILEDIA=0` 等设置影响日常 CAD 配置，异常退出也保持隔离。该开关的用途见 [Autodesk 官方说明](https://blog.autodesk.io/getting-started-with-accoreconsole/)。2026-11-05 已在本机 2024 控制台确认隔离日志，13 项原生检查通过。

## 本地题库复刻

```powershell
python tools\prepare_corpus.py 'D:\AutoCAD 2024\高教杯水利赛道'
powershell -File tools\Reproduce-Cad.ps1 -Plan tests\fixtures\sixth-section33.json -Name sixth-section33
python tools\render_reproductions.py
powershell -File tools\Reproduce-Cad.ps1 -Plan tests\fixtures\hebei-earth-dam-reviewed.json -Name hebei-earth-dam -PlotPdf
```

PDF 工具依赖 `pymupdf`；预览工具依赖 `ezdxf` 和 `matplotlib`。`ModelBenchmark.cs` 使用实际插件 DLL 与已有本地模型配置发送真实请求；生成可执行文件后，其参数为 `插件.dll 图片.png 结果.json [提示]`。真实请求会使用所配置服务的额度。

已索引题库目录内 17 份 PDF、637 页，包含通知、标准、真题和答案；已选取第五届整图、第六届 3-3 断面、第七届扭面大样及河北样题土坝图做复刻。原始 PDF、图片、密钥、本地模型配置和运行产物均不上传。仓库保留三个模型生成计划及一个人工核对后的土坝计划，作为可重复的回归样本。[土坝复刻记录](docs/hebei-earth-dam-2026-11-05.md) 区分了模型输出与人工修订。本次工作不涉及远程模型权重微调。
