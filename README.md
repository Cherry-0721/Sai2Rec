# Sai2Rec

**One-click drawing timelapse export for PaintTool SAI 2.**

Turn your SAI 2 drawing process into an MP4 timelapse with a custom duration and frame rate — no manual screen recording or video editing required.

Sai2Rec is an unofficial Windows tool for recording the drawing process in PaintTool SAI Ver.2. Capture your canvas, finish your artwork, choose your target video duration and FPS, and export your timelapse to MP4.

### Features

- **Custom video duration** — choose how long your final timelapse should be.
- **Custom frame rate** — export at your preferred FPS.
- **One-click MP4 export** — generate a timelapse without manually editing video speed.
- **Canvas-aware recording** — connect to a SAI 2 canvas and record the drawing process.
- **Local recording storage** — recordings are saved locally and retained until you clear the cache.
- **Windows installer** — get started without building from source.

### Quick start

1. Download and run `Sai2Rec-Setup.exe`.
2. Open the 64-bit PaintTool SAI Ver.2 and your canvas.
3. Launch Sai2Rec and click **Connect Canvas**.
4. Select the correct canvas and click **Start**.
5. Finish drawing and click **Stop**.
6. Set your target video duration and FPS, then click **Export MP4**.

### Compatibility

The current canvas scanning logic has been verified with PaintTool SAI Ver.2 x64, version `2024.08.14`. Other versions may require additional testing.

Sai2Rec reads canvas memory and does not write to SAI 2 memory, inject DLLs, or modify original `.sai2` files, according to the project's implementation.

Source code is licensed under GNU GPL v3.
