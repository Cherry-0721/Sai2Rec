# Sai2Rec
An unofficial recorder for PaintTool SAI 2, supporting window capture, hotkeys, and MP4 export.

这是一个用于录制 PaintTool SAI Ver.2 绘画过程的 Windows 工具。
程序只读取 SAI2 的画布内存，不写入 SAI2 内存，不注入 DLL，也不会修改原始 .sai2 文件。录制数据保存在程序目录中，完成后可通过 FFmpeg 导出 MP4 视频。

普通用户使用方法
1. 下载并运行“Sai2Rec-Setup.exe”。
2. 打开 64 位 PaintTool SAI Ver.2 和需要录制的画布。
3. 启动 Sai2Rec，点击“连接画布”。
4. 选择尺寸正确的画布，然后点击 Start。
5. 绘画完成后点击 Stop。
6. 设置视频时长和 FPS，点击“导出 MP4”。
录制文件位于程序目录的 others\recordings 文件夹。
导出视频位于程序目录的 output 文件夹。
只有点击“清除缓存”才会删除录制数据。

从源码编译
系统要求：Windows x64 和 .NET Framework 4.x。
在 PowerShell 中运行：
    powershell.exe -NoProfile -ExecutionPolicy Bypass -File .\build.ps1
编译结果位于 build\Sai2Rec.exe。
导出视频需要 ffmpeg.exe。安装包已经附带 FFmpeg；自行编译时，可将 ffmpeg.exe 放在程序旁边或 others 文件夹中。

兼容性说明
当前画布扫描逻辑按 PaintTool SAI Ver.2 x64 2024.08.14 验证。SAI2 更新后内存结构可能变化，需要重新测试。

程序目前按画布名称和尺寸识别录制目录。重命名画布后，可能生成新的录制目录。
本项目源码使用 GNU General Public License Version 3，详见 LICENSE.txt。
PaintTool SAI 和 FFmpeg 不属于本项目，分别遵守其自身许可证。
