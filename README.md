# 你杨叔叔的学习通桌面助手 · Course Study Desktop

这是你杨叔叔做的一款 Windows 11 x64 学习辅助工具。它支持读取题目、截图识字和通过 DeepSeek 查询解析。项目由个人独立开发，与超星及学校无隶属关系。

在允许使用辅助工具的练习中，可以点击「开始答题」，让助手填写并逐题核对。填完后，你可以自己检查、修改，再决定是否提交。

## 下载你杨叔叔的学习通桌面助手（Windows 便携版）

前往 [Releases](https://github.com/3269890146/course-study-desktop/releases)，在对应版本的 **Assets** 里寻找文件名带 `windows-x64.zip` 的便携包。下载后解压，双击 `学习通桌面助手.exe` 即可启动。请保留同目录的 DLL、`x64/` 和 `tessdata/`。

## 从源码构建

如果你想自己编译你杨叔叔的学习通桌面助手，请准备 Windows 11 x64、系统 .NET Framework 编译器、WebView2 Evergreen Runtime，以及 Tesseract 所需的 Microsoft Visual C++ x64 运行库。

在仓库根目录打开 PowerShell，运行：

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File .\build.ps1
```

构建脚本会从 NuGet 和 Tesseract 官方仓库下载固定版本的依赖。构建后打开 `build\学习通桌面助手.exe`。有离线依赖时，可用 `-PackageDirectory` 指定装有 `webview2.nupkg` 和 `tesseract.nupkg` 的目录，用 `-ModelsDirectory` 指定装有两种 `.traineddata` 文件的目录。

想验证程序是否能读全题目，可以运行 `powershell -NoProfile -ExecutionPolicy Bypass -File .\build.ps1 -Test`。测试会用自编的 40 道题检查识题效果，并在独立浏览器目录中验证 WebView2 嵌入页的来源检查。

## 使用说明

- **阅读页自动翻页（1.1）。** 开启「连续播放」后，程序在确认当前是教材、导读或符合条件的编号正文页时，滚到页底并点击一次「下一节」。真实课程的页面样式仍需复测。

- **课程播放。** 课程视频在开始播放、暂停后继续或切到下一节时，默认使用 2 倍速。
- **题目读取。** 内置浏览器采用 WebView2 默认的网页安全与站点隔离设置。部分嵌套或跨域页面可能无法完整识别，可以改用截图 OCR 或手动粘贴题目。
- **截图和解析。** 截图 OCR 在本地运行。点击「查询答案与解析」或「开始答题」时，题目文字会发送给 DeepSeek。请使用自己的 API 密钥。密钥由 Windows 当前用户凭据加密保存在本机，调用接口可能产生费用。
- **填写和核对。** 「开始答题」结束后，程序会显示「已填写并核对，未提交」。核对只检查网页填写结果是否与 AI 返回一致。请自行判断答案，并遵守学校或课程规定。

## 给你杨叔叔反馈

如果遇到漏读或识别不准，可以在 [Issues](https://github.com/3269890146/course-study-desktop/issues) 告诉你杨叔叔：你用的是哪个版本、遇到了什么题型、页面有几题、助手读到了几题。请勿附上登录缓存、API 密钥或包含个人信息的截图。

## 开源许可

「你杨叔叔的学习通桌面助手」的源码许可见 [LICENSE](LICENSE)。依赖有各自的授权和再分发条件，详见 [第三方依赖](THIRD_PARTY.md)。
