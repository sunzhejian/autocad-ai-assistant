# 安装 AutoCAD AI 助手

适用：Windows x64、AutoCAD 2024。其他 CAD 版本尚未验证。

1. 从本项目 GitHub Releases 下载 `AutoCAD-AI-Assistant-v版本号-win64.zip` 和对应 `.sha256` 文件。
2. 在 PowerShell 中用 `Get-FileHash -Algorithm SHA256 '下载的文件.zip'` 核对校验值。
3. 解压整个安装包，再双击 `Install.cmd`。安装到当前用户目录，不包含 AutoCAD 软件或 Autodesk SDK。
4. 保存正在编辑的图纸，重启 AutoCAD 2024。启动会显示完整工具区及“AI 制图”选项卡。
5. 点击“AI 助手 / 图纸复刻”或输入 `AI`。配置模型地址、模型 ID 和密钥后才可发送 AI 请求。

只有 PDF 功能需要本地 Python 与 PyMuPDF。先安装 Python，再运行 `python -m pip install PyMuPDF==1.27.2`；如有多个 Python，可设置 `CADAI_PYTHON` 为所选 `python.exe` 的完整路径。单纯文字/图片 AI 绘图不需要 Python。矢量 PDF 的原生导入无需调用模型。

安装器首先检查包内文件哈希，再复制插件；最后原子发布加载清单。已被 CAD 加载的旧 DLL 保留，下次启动使用新版本。仅为 AutoCAD 2024 当前用户配置登记插件目录的受信任路径，不修改其他 CAD 版本。

## 常用命令

| 命令 | 用途 |
| --- | --- |
| `AI` | 打开助手，选择图片或 PDF |
| `AIUI` | 恢复完整界面并启用下次启动整理 |
| `AIUIOFF` | 关闭下次启动整理，保留当前界面 |
| `AIUIRESTORE` | 恢复首次备份的六项启动偏好，并关闭启动整理 |
| `AIPERF` | 减少悬停、命令、填充等实时预览 |
| `AIPERFRESTORE` | 恢复原来的预览设置 |

`AIUIRESTORE` 保留当前图纸与功能区；它恢复启动模式、起始页、文件/命令对话框、菜单栏和状态栏的原值，不重置整个 CAD 配置。

## 更新与配置恢复

直接安装新版即可。模型配置保存在本地 `%LOCALAPPDATA%\AutoCAD.AI.Assistant\models.json`，API 密钥由 Windows 当前用户 DPAPI 加密。更新不打包、不覆盖该配置；配置不能作为明文密钥跨电脑使用。

配置更新通过原子替换完成。第二次成功保存起保留上一版 `models.json.bak`；主文件损坏时尝试备份并在面板提示。重新保存时，将损坏文件另存为 `models.json.corrupt-*`。主文件和备份均无效时保留原文件并报告错误。

启动偏好备份为同目录下的 `startup-ui-original.json`。备份损坏时停止修改设置并报告错误；保留文件以便核查。`AIUIRESTORE` 只接受六个已知变量及有效整数值。

## 常见问题

- **界面仍然很空**：输入 `AIUI`，确认打开了至少一张图形；完整原生工具也在“默认 / 插入 / 注释”等选项卡。
- **没有毫米模板**：先通过 AutoCAD 安装/配置恢复 `acadiso.dwt`，插件不会静默使用英制模板。
- **更新后还是旧版**：保存图纸后退出所有 AutoCAD 会话，再打开；助手面板名称包含已加载版本。
- **PDF 无法处理**：检查 Python 是否能导入 `pymupdf`，以及 `CADAI_PYTHON` 是否指向正确解释器。
- **绘图操作卡顿**：可尝试 `AIPERF`，需恢复时输入 `AIPERFRESTORE`。AI 面板不使用时可关闭。
- **识图结果有误**：优先导入矢量 PDF；图片先框选单视图，注明单位并核对标注和填充。执行成功不等于已经与原图完全一致。

PyMuPDF 为单独安装的第三方依赖，许可说明见 https://pymupdf.readthedocs.io/en/latest/about.html 。本安装包不附带其二进制。
