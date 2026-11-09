# auto 参数修复与真实模型测试

2026-11-09，AutoCAD 2024 / Windows x64，插件 1.3.3。

## 修复

用户配置的模型 ID 为 `auto`，服务返回 HTTP 400：不支持 `temperature`。原来普通、流式和修正请求都会发送该字段。现在三条路径共用序列化方法，auto 请求完全省略该字段，不发送 null 或零。其他模型只有在 HTTP 400/422 明确表示 temperature 不受支持时才省略并重试一次，保持模型、消息、图片及流式标志原值。兼容结果按地址和模型在内存中保存；不会覆盖用户配置。

图片测试另外发现：先遇到错误的填充边界时，原来的单次修正没有同时收到缺失单位和视图的信息。现在同一次修正会同时报告几何错误和图片协议错误；仍不通过则停止，不绘制无效计划。

## 实测

均通过插件 DLL 的 `OpenAiCompatibleClient` 使用本机已有配置的 auto 发送真实请求，没有用另一个模型代画，没有修改模型原始几何计划。auto 的底层路由模型未获服务端证实。

| 尝试 | 结果 | 模型请求耗时 |
| --- | --- | --- |
| 文字：原点半径 20 mm 的圆及真实直径标注 | 生成有效计划；插件执行器创建 Circle 和 DiametricDimension，原生回读直径 40 mm，无固定文字覆盖 | 18.460 秒 |
| 第六届进水口 3-3 断面，修复元数据报告前 | 服务已接受图片；填充边界修正后仍缺单位/视图，校验阻止执行 | 27.039 秒，含一次修正 |
| 同一断面，修复元数据报告后 | 计划通过并落为 12 个图元，4 个原生标注、ANSI31 与 DOTS 双层填充，各填充保留内孔 | 119.111 秒，含本地处理 |
| 新开的 CAD 界面，选择 auto 发送圆与直径标注任务 | HTTP 200，界面显示已创建 2 个图元；回执确认直径 40 mm；CAD 执行 120 ms | 请求 14.889 秒 |

图片第二次尝试可执行，但复刻不准确：外轮廓为 1000×1100 cm、内孔大小为 700×800 cm，模型把内孔下缘设为 200 cm、上缘设为 1000 cm，导致上下壁厚与源图不同；内孔标注端点和水平中心线位置也有偏差。`verify_section_reproduction.py` 对实际 DXF 的 14 项选定语义检查通过 **9 项**。不能将计划和原生回执通过解释为源图识别正确，也不能据此宣称 auto 更快或精度更高。

本地证据（运行产物不提交仓库）：

- `artifacts/auto-model/text.json`、`text.plan.json`，`artifacts/reproductions/auto-circle.dwg`。
- `artifacts/auto-model/vision-first-attempt.json`、`vision.json`、`vision.plan.json`、`vision.source-check.json`。
- `artifacts/reproductions/auto-sixth-section33.dwg`、`.dxf`、`.native.json`，`artifacts/fidelity/auto-sixth-section33.pdf`。
- `artifacts/auto-model/gui-circle.receipt.json`；原生检查通过，`source_accuracy_verified=false`。

## 同批复刻显示改进

1.3.2 的未发布改动一并纳入 1.3.3：局部图新增几何的线型比例独立于 DIMTXT，保留已有全局 LTSCALE；竖向尺寸文字沿尺寸线；短斜线端点大小按源单位转换；允许安全的原生尺寸格式变量。源图纹理可用同一外边界和内孔的斜线、颗粒双层填充。

前一轮使用插件内已配置的 DeepSeek 对同一断面重新识别，初始请求 66.218 秒并修正一次，生成 23 条命令、12 个图元。选定语义检查由旧样本 12/14 提升到 14/14；固定同一 AI 原始计划重放后仍为 14/14。该小样本不证明字体、线距、图名位置及像素完全一致，也不代表其他赛题。此次 auto 图片样本的 9/14 保留为真实差距，不以人工修订后结果替代。

## 验证与启用

- 116 项纯逻辑/本地 HTTP 回归通过，包括 auto 文字、图片、修正、SSE 和其他模型的兼容重试与消息保留。
- 33 项原生 CAD 检查通过；2 项 PDF worker 测试通过。
- 本机 Release 编译成功；环境缺 .NET Framework 4.8 Targeting Pack，现有 MSBuild 使用 GAC 引用并产生 MSB3644 警告。已用真实 AutoCAD 原生宿主验证。

安装后保存原窗口的图纸并重启 CAD，以便加载新版 DLL；运行中的旧 .NET 插件不能卸载替换。本次新开的独立 CAD 窗口已验证新版 auto，原窗口和待发送附件保留。
