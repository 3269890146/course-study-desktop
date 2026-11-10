# Course Study Desktop · 学习通桌面助手

面向 Windows 11 x64 的独立学习辅助工具，与超星及学校无隶属关系。支持读取可访问的题目、截图识字和通过 DeepSeek 查询解析。允许使用辅助工具的练习中，可以用「开始答题」填写并逐题核对，填写结束后由用户检查和决定是否提交。

## 构建和运行

准备 Windows 11 x64、系统 .NET Framework 编译器、WebView2 Evergreen Runtime，以及 Tesseract 所需的 Microsoft Visual C++ x64 运行库。

在仓库根目录打开 PowerShell，运行：

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File .\build.ps1
```

脚本会从 NuGet 和 Tesseract 官方仓库下载固定版本的依赖。构建后打开 `build\学习通桌面助手.exe`。请保留同目录 DLL、`x64/` 和 `tessdata/`。有离线依赖时，可用 `-PackageDirectory` 指定装有 `webview2.nupkg` 和 `tesseract.nupkg` 的目录，用 `-ModelsDirectory` 指定装有两种 `.traineddata` 文件的目录。

运行 `powershell -NoProfile -ExecutionPolicy Bypass -File .\build.ps1 -Test` 可验证本地 40 题识别和 WebView2 嵌入页来源检查。测试使用自编题目和独立浏览器目录。

## 使用边界

- 学习通课程视频开始播放、暂停后继续或切到下一节时，默认使用 2 倍速。
- 内置浏览器采用 WebView2 默认的网页安全与站点隔离设置。部分嵌套或跨域页面可能无法完整识别，可用截图 OCR 或手动粘贴题目。
- 截图 OCR 在本地运行。点击「查询答案与解析」或「开始答题」会将题目文字发送给 DeepSeek。使用自己的 API 密钥，密钥由 Windows 当前用户凭据加密保存在本机。使用接口可能产生费用。
- 「开始答题」结束后会显示「已填写并核对，未提交」。核对是检查页面上的填写结果是否与 AI 返回一致。请自行判断答案，并遵守学校或课程规定。
- 遇到识别问题，可提供版本、题型、预期题数和实际题数。请勿提交登录缓存、API 密钥或包含个人信息的截图。

## 许可证

项目源码的授权见 `LICENSE`。依赖有各自的授权和再分发条件，见 [第三方依赖](THIRD_PARTY.md)。
