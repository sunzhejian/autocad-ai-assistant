# AutoCAD AI 助手

AutoCAD 2024 的按需加载插件，支持兼容 OpenAI Chat Completions 的模型、图纸图片复刻和结构化几何绘制。当前版本 **1.1.0**，.NET Framework 4.8 / Windows x64。

## 使用

1. 编译 Release x64，运行 `Install.ps1`，重启 AutoCAD，输入 `AI`。
2. 配置支持视觉输入的模型；密钥使用当前 Windows 用户的 DPAPI 加密保存在本地。
3. 粘贴或选择图纸图片。默认发送一张最长边 1600px 的增强图；单视图优先“框选”，并在输入中保留源图单位说明。
4. 复杂大图可勾选“精细分块”，同时发送整图和四张重叠高清图，模型处理时间可能增加。
5. 绘图前自动校验 JSON、尺寸与几何、块依赖及指令预算；可修正一次，仍不通过则停止。绘图期间任一原生命令失败，整批回滚。完成后一次 `U` 撤销整批。
6. “复用已执行计划”默认开启。相同图片内容、提示、模型配置、协议与分块模式可复用七天内的计划；取消勾选可重新识别。该缓存只证明计划曾成功执行，源图识别仍需核对。

`AIPLAN` 可读取本地 JSON 计划；`AIDRAW` 为面板的绘图队列命令。请求中“发送”变为“取消”，图片请求总预算 240 秒、文本 120 秒，包含重试和自动修正。切换/关闭原图形后不会把结果画入另一张图。

## 准确性机制

图片计划声明 `sourceUnit: mm/cm/m`、`coordinateUnit: source` 和可见 `views`。模型按源图单位给出坐标，插件在本地统一换算为毫米。兼容旧的毫米坐标计划（省略 `coordinateUnit`）。高程与普通尺寸使用不同单位时，高程作为文字保留。

尺寸标注附源图 `value` 与 `confidence: read/inferred/unknown`。测量值必须与坐标一致；固定文字不能覆盖真实测量值。推断尺寸以橙色和“（估）”显示。图框、字高及标注样式分别处理纸面比例与实体单位；空心截面可通过 `hatch.holes` 保留内孔。

本地检查能拦截单位、几何及协议错误，不能证明所有像素都被正确理解。复杂全图、模糊小字和扭面仍需人工核对。[实测记录](docs/verification-2026-11-05.md) 包含成功样本、失败尝试和局限。

## 编译与验证

依赖 AutoCAD 2024 安装目录中的 `acmgd.dll`、`acdbmgd.dll`、`accoremgd.dll`，以及 .NET Framework 4.8 开发工具。默认目录 `D:\AutoCAD 2024`，可通过 MSBuild 的 `AutoCAD2024Path` 属性修改。

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File tools\Test.ps1
powershell -NoProfile -ExecutionPolicy Bypass -File tools\Test.ps1 -BuildPlugin
powershell -NoProfile -ExecutionPolicy Bypass -File tools\Test-Cad.ps1
.\Install.ps1
```

纯逻辑和本地 HTTP 回归不依赖 CAD 许可，在 GitHub Actions 的 Windows 环境执行。原生集成验证需要本机 AutoCAD，使用新建的临时图形。

## 本地题库复刻

```powershell
python tools\prepare_corpus.py 'D:\AutoCAD 2024\高教杯水利赛道'
powershell -File tools\Reproduce-Cad.ps1 -Plan tests\fixtures\sixth-section33.json -Name sixth-section33
python tools\render_reproductions.py
```

PDF 工具依赖 `pymupdf`；预览工具依赖 `ezdxf` 和 `matplotlib`。`ModelBenchmark.cs` 使用实际插件 DLL 与已有本地模型配置发送真实请求；生成可执行文件后，其参数为 `插件.dll 图片.png 结果.json [提示]`。真实请求会使用所配置服务的额度。

已索引题库目录内 17 份 PDF、637 页，包含通知、标准、真题和答案；本轮仅选取第五届整图、第六届 3-3 断面、第七届扭面大样做复刻。原始 PDF、图片、密钥、本地模型配置和运行产物均不上传。仓库保留三个模型生成计划，作为可重复的回归样本。本次工作不涉及远程模型权重微调。
