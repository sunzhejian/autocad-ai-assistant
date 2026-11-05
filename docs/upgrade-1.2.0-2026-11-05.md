# v1.2.0 验收记录（2026-11-05）

本轮聚焦 PDF 输入路由、CAD 原生结果校核、填充拓扑和图形变更拦截。未增加远程模型训练，未把人工核对的样例称为全自动识图成果。

## 已实现

- PDF 页码切换、矢量/扫描/混合/空白分类、最多 2400px 页面预览、文字证据、内容哈希快照及重复页缓存。文件 128 MB / 单页 45 秒；本地 Python 处理可取消。
- 矢量 PDF 通过 AutoCAD 2024 `-PDFIMPORT` 导入为一个块，显式从 PDF 英寸基准换算到当前图形单位。保留线宽、连接连续线段、实心区域转为填充；混合页图像放在来源缓存中，仍为外部参照。结束后恢复 FILEDIA、导入选项、图像位置、单位制、原布局和当前层。
- AI 绘制在事务提交前读取实际 Dimension.Measurement、Hatch.Area、内孔与图案，按嵌套指令编号建立句柄回执。不一致或无法读取即回滚。原生事件延迟的问题已通过创建对象时显式记录解决。
- 新增填充环自交、回折、重复点、零面积、内孔越界/接触/重叠/嵌套检查。预检错误进入已有一次自动修正流程。
- 修复直径标注将圆心当作直径端点的问题，现以圆周相对两点构造原生直径。
- 等待模型期间跟踪图元变更、句柄种子、单位与模型/布局，执行前再核对；改变后的结果保留为本地计划，停止自动写入。

## 实测与边界

| 检查 | 结果/解释 |
| --- | --- |
| C# 纯逻辑和 HTTP 回归 | 66 项，包含填充拓扑、哈希防篡改、进程参数转义、原有请求重试/取消 |
| PDF worker | 合成矢量/扫描/混合/空白页、旋转、非法页、重复缓存与原件变更保护 |
| .NET 到 Python 的真实进程 | 河北第 3 页识别为矢量：2126 路径、0 位图、234 文字；第五届第 1 页识别为扫描页 |
| WPF 面板 | 在原生宿主中实例化并渲染空面板、PDF 原生导入、PDF AI 重建三种状态；450×720 PNG 已检查，无控件截断。此项未替代用户桌面的重启验证 |
| 原生 AI 回归 | 21 项通过：单步撤销、回滚、厘米单位、半径/直径、内孔、材料疏密、嵌套编号、图元/单位变化、已有图元跨命令修改、400 对象（约 60ms） |
| PDF 原生回归 | 9 项通过：72 PDF 点测试边放大 2 倍后为 5.08 厘米，填充保留、设置恢复、单步撤销及混合页位图依赖 |
| 河北真实第 3 页原生导入 | 2026 个对象（含外层块），一次记录 7.58 秒；仅为导入和本地统计，不含 CAD 启动、页面分析或打印，非受控速度对比 |
| 土坝人工复核计划 | 两处排水体砂/碎石薄层存在微小自交与回折尖刺，修正后 157 个图元（含块定义）通过原生校核：模型空间 24 个、三个 HB 块内部共 133 个；回执包含 12 个尺寸与 13 片填充，仍保留人工核对属性 |

原生导入结果保存为 `artifacts/pdf-native/contest-import.dwg/.dxf/.pdf`；智能几何回归结果为 `artifacts/reproductions/hebei-v120-check.*`。这些本地赛题产物不随源码上传。

已查看原生打印 PNG：原生导入保留原图线条与布局，但部分中文字体和比例文字发生移位，因此不宣称完美复刻。智能几何样例的薄层填充无原先的回折尖刺，但布局、材料符号仍是原人工复核方案。校核回执的 `source_accuracy_verified=false`。

原生模式保留纸面，不能用整页单一放大倍数恢复多个视图各自的 1:1。扫描页沿用已有视觉模型流程，本轮验证了 PDF 路由与进程，未重新计量扫描识图准确率，也未新增 OCR 引擎或几何约束求解器。

构建仍有本机缺少 .NET 4.8 Targeting Pack 的 MSB3644 提示；Release DLL 已在 AutoCAD 2024 原生宿主运行验证。

## 复现

```powershell
powershell -File tools\Test.ps1 -BuildPlugin
python -m unittest discover -s tests -p test_pdf_worker.py -v
powershell -File tools\Test-Cad.ps1
powershell -File tools\Test-Pdf-Process.ps1 -SourcePdf '赛题.pdf' -Page 3 -Kind vector
powershell -File tools\Test-Pdf-Cad.ps1 -SourcePdf '赛题.pdf' -Page 3 -PlotPdf
powershell -File tools\Test-Panel.ps1
powershell -File tools\Reproduce-Cad.ps1 -Plan tests\fixtures\hebei-earth-dam-reviewed.json -Name hebei-v120-check -PlotPdf
```

控制台测试统一使用 `/isolate`，避免修改日常 CAD 配置。安装按版本与内容哈希保留并存 DLL，校验文件后最后更新清单；不覆盖当前 CAD 正在加载的旧 DLL。安装后重启 CAD，输入 `AI`，点击“图/PDF”选择 PDF 和页码。PDF 依赖本机 Python / PyMuPDF，源码包不含第三方二进制。

## 参考的官方接口

- [AutoCAD 2024 -PDFIMPORT](https://help.autodesk.com/cloudhelp/2024/ENU/AutoCAD-Core/files/GUID-ADEE1DE4-3CEF-432D-95F2-014F326E8B2A.htm)：页面、位置、缩放、旋转；INSUNITS 不参与导入比例换算。
- [PDFIMPORTMODE](https://help.autodesk.com/cloudhelp/2024/ENU/AutoCAD-Core/files/GUID-669F39FF-972F-4C24-8841-E42185A44843.htm)：块、线宽、连续线段和实心填充选项。
- [PDFIMPORTIMAGEPATH](https://help.autodesk.com/cloudhelp/2024/ENU/AutoCAD-Core/files/GUID-4DB80DEB-CDE2-430B-9D5E-7A3828BC7832.htm)：提取图像及外部参照保存位置。
