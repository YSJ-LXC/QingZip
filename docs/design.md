# 界面与 ZIP 原理

界面使用 WPF 和 .NET Framework 4.8。半透明卡片叠加柔光渐变，在支持的 Windows 11 系统通过 DWM 请求 Desktop Acrylic 材质。接口不可用时保留应用自己的后备外观。

页面使用渐显与向上滑入，卡片按 60 毫秒间隔入场。按钮悬停会在 160 毫秒内放大并上浮，移开后恢复；任务进度采用 180 毫秒缓动。系统关闭窗口动画时，启动程序会减少动效。

静态预览由 WPF 渲染，只包含应用界面，不包含桌面的实时模糊合成。

官方参考：[DWM 背景材质](https://learn.microsoft.com/en-us/windows/win32/api/dwmapi/ne-dwmapi-dwm_systembackdrop_type)、[WindowChrome](https://learn.microsoft.com/en-us/dotnet/api/system.windows.shell.windowchrome)。原生 Desktop Acrylic 接口要求 Windows 11 build 22621 或更高。

## 为什么可以先看目录，再解压

ZIP 由文件头与压缩数据、中央目录以及目录结束记录组成。程序先在文件尾部找到目录位置，读取名称、大小和压缩方法，便能列出内容。实际解压时再读取数据、还原字节，复核长度与 CRC32，最后提交完整的输出文件夹。

Stored 方法直接存储字节；Deflate 使用重复数据的向后引用与 Huffman 编码。轻ZIP 自己解析和验证 ZIP 容器，压缩数据使用 .NET 库处理。

格式参考：[PKWARE ZIP APPNOTE](https://pkware.cachefly.net/webdocs/casestudies/APPNOTE.TXT)、[Deflate RFC 1951](https://www.rfc-editor.org/info/rfc1951/)。
