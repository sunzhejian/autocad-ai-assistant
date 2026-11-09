# AI 绘图能力与校核

以下命令由插件的本地执行器生成原生 AutoCAD 图元。模型负责理解要求并生成计划；执行器执行、换算单位、核对原生结果。请通过已配置的 AI 发送自然语言要求，也可用 `AIPLAN` 读取结构化计划。

## 1.4.0 新增

| 能力 | 计划字段 | 示例请求与范围 |
| --- | --- | --- |
| 真实角度标注 | `dimangular{center,p1,p2,position,value}` | 标出两射线之间的 45°；center 是顶点，position 在小角扇区内；目前支持大于 0°、小于 180°的小角 |
| 多重引线 | `leader{points,position,height,content,arrowSize}` | 指向某一点注明 C30 混凝土；points 从箭头尖开始依次列折点，position 为文字位置；生成一个 MLeader |
| 带宽圆环、圆盘 | `donut{center,innerDiameter,outerDiameter}` | 画内径 20、外径 40 的实心圆环；内径为 0 时生成圆盘，不能用两个细圆轮廓代替 |
| 二维实心区域 | `solid{points}` | 画实心三角形或凸四边形；顶点沿边界排列，执行器处理 AutoCAD 的内部顶点顺序；凹多边形需分成凸区域 |
| 多段线圆弧段 | `polyline{points,bulges,closed,width}` | 绘制直线与圆弧连成一体的多段线；bulges 与 points 等长，值为 tan(扫角/4)，正数逆时针、负数顺时针；开放线末值为 0 |

这些几何长度按 `sourceUnit/coordinateUnit` 统一换算为毫米；角度 `value` 始终用度，bulge 是无量纲量。角度标注使用原生测量值，固定文字不能伪装角度；回执同时保留原生弧度值和换算后的度数。

## 已有能力

直线、多段线、矩形、正多边形、圆、圆弧、椭圆、样条、点；单行/多行文字；线性、对齐、半径、直径尺寸；带内孔填充与材料预设；块定义及旋转/缩放插入；图层、文字样式、标准样式和 A0–A4 图框。允许的绘图变量仍使用白名单。

“AI 制图”启动选项卡新增曲线与注释面板，提供圆弧、椭圆、样条、圆环、实心区域、多重引线入口；标注面板补充角度与直径标注。总计 35 个入口，调用 CAD 原生命令。AI 只在发送任务时运行。

## 如何测试是否画对

`tools/test_drawing_suite.py` 预先定义十组文字任务，然后通过实际插件 DLL 和已有的本机模型配置请求 AI，保留原始回答与计划，不改写 AI 的几何。使用独立配置的 CoreConsole 逐组执行，保存 DWG、DXF 和原生回执，再独立读取 DXF 核对圆心、半径、顶点、弧方向、样条拟合点、原生尺寸、填充边界/内孔和块变换。

```powershell
python tools/test_drawing_suite.py --model auto --output artifacts/drawing-suite-auto
```

首次运行会调用所配置服务、消耗额度，最多同时进行两个网络请求；CAD 执行串行进行。可加 `--case angular` 只测单组；`--execute-only` 重放已有回答，报告会标明没有新请求。预期几何在代码中固定，不能按模型输出修改评分标准。模型请求成功、计划有效、CAD 能执行、实际语义正确分别记录。

模型配置、密钥、用户赛题和真实绘图结果不提交仓库。该固定文字测试不证明任意工程图像识别正确，也不证明赛题已完美复刻。API 语义参考 [Autodesk 多重引线文档](https://help.autodesk.com/cloudhelp/2024/ENU/OARX-ManagedRefGuide/files/OARX-ManagedRefGuide-Autodesk_AutoCAD_DatabaseServices_MLeader_AddLeaderLine_Point3d.html) 与 [多段线弧度因子文档](https://help.autodesk.com/cloudhelp/2024/ENU/OARX-ManagedRefGuide/files/OARX-ManagedRefGuide-Autodesk_AutoCAD_DatabaseServices_Polyline_SetBulgeAt_int_double.html)。
