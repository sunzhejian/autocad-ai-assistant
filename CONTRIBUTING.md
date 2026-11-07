# 参与开发

代码面向 AutoCAD 2024 / Windows x64 / .NET Framework 4.8，C# 语言版本为 5。仓库不含 Autodesk SDK DLL，构建时引用开发者自己的 AutoCAD 安装。

## 开发与测试

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File tools\Test.ps1
python -m pip install -r requirements-pdf.txt
python -m unittest discover -s tests -p test_pdf_worker.py -v
python tools\audit_public.py --history
```

以上是 GitHub Actions 验证范围，不需要 AutoCAD 许可。构建插件还需 .NET Framework 4.8 Developer Pack 和 AutoCAD 2024；可用 Visual Studio MSBuild：

```powershell
MSBuild.exe AutoCAD.AI.Assistant.csproj /p:Configuration=Release /p:Platform=x64 /p:AutoCAD2024Path="C:\Program Files\Autodesk\AutoCAD 2024"
```

默认 AutoCAD 路径为 `D:\AutoCAD 2024`。路径相符时，可运行 `tools\Test.ps1 -BuildPlugin` 一并编译。原生验证命令包括 `tools\Test-Cad.ps1 -AutoCADPath '实际路径'`，以及 PDF、面板和大图专用脚本。它们使用 CoreConsole `/isolate`，不使用日常 CAD 配置。

## 代码约定

- CAD 数据库读写留在 CAD 线程；图像处理、请求、JSON 预检和文件处理尽量离开 UI 线程。
- 绘图应能整批撤销，原生失败应回滚。不要通过覆盖标注文字掩盖测量错误。
- 控制填充密度，保留内孔，对未知材料和单位明确报错。
- 默认不监听鼠标、不轮询文档，不在空闲时统计所有操作。
- 保留真实来源状态。通过本地校验不能把 `source_accuracy_verified` 改成 `true`。
- 修改配置文件时保留加密、原子替换和异常恢复行为；不得在日志或测试输出中打印配置内容、令牌或密钥。

提交缺陷修复时附合适的回归证据。测试用小型、自己构造的图形或经授权的计划；不要提交原始赛题 PDF、真实 DWG、用户模型配置、SDK DLL 或运行日志。

## 打包

完成 Release 构建后：

```powershell
powershell -File tools\Package.ps1
powershell -File tools\Test-Package.ps1 -Archive 'releases\AutoCAD-AI-Assistant-v版本号-win64.zip'
```

打包脚本使用固定文件白名单，逐项检查 ZIP 内容和哈希，输出 `.zip` 与 `.sha256`。发布包只含自编译插件、PDF worker 源码、安装器与安装说明；不包含模型配置或第三方 SDK。
