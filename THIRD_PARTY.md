# 第三方依赖

| 依赖 | 构建版本 | 来源和授权 |
| --- | --- | --- |
| Microsoft.Web.WebView2 | 1.0.2903.40 | [NuGet 包](https://www.nuget.org/packages/Microsoft.Web.WebView2/1.0.2903.40)。以包内 `LICENSE.txt` 和 `NOTICE.txt` 为准。构建脚本将这两份文件复制到输出目录。 |
| Tesseract .NET 包装器及随包原生库 | 5.2.0 | [NuGet 包](https://www.nuget.org/packages/Tesseract/5.2.0)与[源码仓库](https://github.com/charlesw/tesseract)。包装器标注 Apache-2.0。原生 Tesseract 和 Leptonica 的通知需按各自上游条款整理。 |
| tessdata_fast 中文和英文模型 | 4.1.0 | [模型源码与许可](https://github.com/tesseract-ocr/tessdata_fast/tree/4.1.0)，Apache-2.0。 |

仓库通过构建脚本获取依赖，不包含第三方二进制文件。打包公开 Release 前，需核对包中实际文件与上游通知，随发布包一起提供相应许可证和版权声明。
