# v1.4.3 请求可靠性与长期开发首批验收

## 缺陷和修复

旧代码仅在普通 JSON 响应中拒绝 `finish_reason=length`；即使服务标为 `content_filter` 或工具调用，只要 `content` 包含有效 CAD JSON，就会继续预检并交给绘图链路。SSE 已有更严格的结束判断，两个通道存在差异。

先加入真实本地 HTTP 服务回归，在 v1.4.2 代码上复现失败（`abnormal JSON finish must not release a valid-looking drawing plan`），再将两条通道统一调用 `ValidateFinishReason`。有明确结束标志时只接收 `stop`；保持历史兼容：省略、null 或空字符串仍可接收。图片响应和单次自动修正也经过同一检查。

异常结束的 JSON 不返回给执行器；流式请求收到此类 JSON 时不向预览输出内容，也不再请求一次普通响应。这避免了异常结果被当作完整计划，以及无意义的追加请求；它不提高模型本身的源图识别精度。

## 验证

| 检查 | 实际结果 |
| --- | --- |
| 纯逻辑及本地 HTTP | 176/176，通过15项新增用例；包含5种异常结束的普通/流式 JSON、正常/省略标志、图片及修正分支 |
| Release x64 | 成功；保留既有 MSB3644 / Targeting Pack 警告，尚未修复工具链环境 |
| AutoCAD 2024 原生 | 42/42，包括材料、内孔、真实尺寸、原子撤销、图形变更保护和400指令；本轮400指令46ms，仅单机单轮样本 |
| PDF worker | 2/2，通过 |
| 安装包 | 7个文件白名单及哈希校验通过；完整包可验证，篡改包在安装前被拒绝 |
| 安装 | DLL/PDF worker 与构建输出一致；v1.4.3 已写入并存安装清单；运行中CAD仍需重启才能加载 |
| 公开内容检查 | 已有规则扫描无发现，临时 `.codex-attachments/` 已排除；规则扫描不是敏感信息穷尽证明 |

## 插件 AI 实测

通过 `tools/test_drawing_suite.py --model auto` 调用实际插件 DLL 和本机已有配置；未使用其他模型代画，也未改写模型几何计划。

- 混凝土箱涵固定文字任务：120.706秒后 `TaskCanceledException`，没有生成可执行成果，0项成图检查；这轮为失败，不将 v1.4.2 的7/7成功结果挪用到本轮。
- 超时来自当前文字请求总预算120秒。没有自动加长预算、改换路由模型或把失败隐藏为成功。`auto` 的具体底层模型依然未获服务端证实。
- 基础任务另行测试：21.870秒返回，通过实际 AutoCAD 执行，直线端点、矩形顶点、圆、正六边形、点及毫米单位共6/6项检查通过；原始计划未经人工改写。不得将两次不同任务解释为相同任务的提速对照。

来源准确性标记仍为 `false`。本批没有重新完成赛题图片的源图精度验收；既有 auto 第六届9/14差距仍保留。下一批优先 EVAL-01/02：把题库、固定源图检查和插件真实请求串为可重复报告。

## 本地产物

- `artifacts/completion-before.log`：旧代码复现失败。
- `artifacts/completion-final.log`、`artifacts/native-1.4.3.log`：纯回归/构建、原生记录。
- `artifacts/long-plan-1.4.3/auto-concrete/`：失败请求报告；基础任务在同级 `auto-basic/`。
- `releases/AutoCAD-AI-Assistant-v1.4.3-win64.zip`：本地安装包，SHA256 `914831d24b089ddaa09de08bf1e13e6f5a3ab3622187d75e36253e0b284220c1`。
- DLL SHA256 `c95b4fc6d04474b7616428ef3f73bf5defed1b0340b18ca42a5b44adb926d5cb`。

GitHub仓库已通过连接器核实仍为公开。命令行 GitHub API 两次 TLS 握手超时，代码同步改用已有 GitHub 连接器；Release资产未宣称已上传。用户原始赛题、Excel作业、模型配置、密钥及本地运行产物不随源码提交。
