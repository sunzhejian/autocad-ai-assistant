# 标注端点几何引用：v1.6.0

状态：已实现可选引用、预检、原生结果核验和修正覆盖检查。依据第六届真实失败补充局部几何关系检查；引用通过仍不能证明AI选定的目标符合原图。

## 实际缺陷

v1.5.1默认预算新样本的内孔宽度标注挂在外轮廓附近；v1.5.2低预算首轮的内孔高度800cm虽读数正确，却用x=1000cm外墙边上的两个点表达，实际内孔右边是x=850cm。现有投影/距离检查只能核数值，不能判断标注目标是否选对。

## 当前可选协议

几何命令可增加稳定字符串`id`，如`outer`、`opening`；线性/对齐标注（`dimlinear`、兼容别名`dim`、`dimaligned`）可增加`reference`，指向同一作用域内明确的几何ID。ID区分大小写、去除首尾空格、1—64字符，不允许制表和换行。被引用ID重复、缺失或跨块时拒绝。块内ID局部作用域，不借用另一个块的轮廓。

目前支持`line`（一条命令创建的多段有限直线）、无圆弧`polyline`、`rect/rectangle`、`circle`。端点到实际边界的距离容差为0.001mm（极大跨度时加相对数值容差）；源单位cm/m归一化前后保持同一物理容差。合法边中点可用，不强迫只能标四角；有限直线的延长线不算边界。非XY平面、含非零bulges的多段线、HATCH环、半径/直径等引用尚未实现，显式报错，不近似判过。

```json
{"sourceUnit":"cm","coordinateUnit":"source","commands":[
  {"type":"rect","id":"outer","p1":[0,0],"p2":[1000,1100]},
  {"type":"rect","id":"opening","p1":[150,150],"p2":[850,950]},
  {"type":"dimlinear","reference":"opening","p1":[850,150],"p2":[850,950],"position":[1050,550],"rotation":90,"value":800}
]}
```

上述端点改成x=1000时，读数虽仍为800，但不在`opening`轮廓上，预检拒绝并将目标、端点字段和最近距离交给已有的一次AI修正。修正可更换错误reference目标和坐标；同一块作用域已声明的线性引用数量不得减少，避免删掉引用后把“未核对”冒充“修好”。覆盖数量不是逐个尺寸身份或来源证明，不排除AI绑定到错误但几何自洽的轮廓。

旧计划没有引用时保持兼容，未引用的尺寸没有新增几何认证。图片协议建议能定位被测轮廓的线性尺寸声明引用，暂不作为全部旧图片计划的必填字段；看不到对应几何时保留未知状态，不虚构一个几何对象来通过检查。协议变化会使图片缓存自然失效，已有用户模型配置不变。

## 原生结果核验

### v1.6.2：矩形端点由本地计算

完整矩形边长可以简写为`dimlinear{reference,side,position,value,confidence}`，`side`为世界XY方向的`top/bottom/left/right`。本地从同作用域的`rect/rectangle`精确取两个边端点和0/90度方向，保留原始`value`；读数与几何冲突时拒绝，不修改读数。不从任意多段线包围框或其它视图猜矩形，不计算局部长度、斜边、圆弧或半径。

简写省略`p1/p2`；可省略`rotation`，若冗余提供则必须与side一致，避免为一个正确的90度字段再请求AI。显式端点和side混用、方向冲突、缺少/重复/跨作用域目标都拒绝。原始AI回答仍保留；预检后的有效计划展开成明确端点再统一换算单位，回放不重复展开或缩放，生成点同样计入预算。

真实AI多次将矩形写成`rect{points:[两个对角点]}`。现在可在本地原样转换为p1/p2，保留坐标和其它属性；不接受任意多点的包围框推断，也不混用两种输入。非0矩形rotation明确拒绝，旋转轮廓必须给polyline顶点。参见[本轮试验与来源差距](accuracy-1.6.2-2026-11-11.md)。

事务提交前按命令路径解析同作用域目标，使用该命令实际创建且属于同一BlockTableRecord的Line/Polyline/Circle，与原生RotatedDimension/AlignedDimension的XLine1Point/XLine2Point核对。调用[`Curve.GetClosestPointTo(point, false)`](https://help.autodesk.com/cloudhelp/2022/ENU/OARX-ManagedRefGuide/files/OARX-ManagedRefGuide-Autodesk_AutoCAD_DatabaseServices_Curve_GetClosestPointTo_Point3d__MarshalAsUnmanagedType_U1__bool.html)，不延伸曲线。缺实体、不同空间或距离超限时整批事务回滚。

回执逐对象记录`geometry_reference`、`reference_command`、`reference_handles`、两端距离与容差、`reference_checked_at_execution=true`、`persistent_association_created=false`。这是一时核验，不创建AutoCAD持久关联约束，不保证后续手动改图时尺寸随动；`source_accuracy_verified`继续为false。

## 验收范围

- 纯回归覆盖缺ID/重ID/跨块、别名、错轮廓、合法中点、有限线段、圆弦、厘米/米物理容差、3D/圆弧明确拒绝、单次修正及禁止删除引用。
- 原生集成新增8项：厘米轮廓、回执距离、错边不落图、两个块重复局部ID、圆弦、多段直线、故意让实际目标偏离合法计划、失败事务无残留。不是仅重复JSON计算。
- 旧夹具、原生尺寸与填充、400条批处理继续纳入回归。完整版本与真实auto结果见[本轮记录](geometry-1.6.0-2026-11-11.md)。
