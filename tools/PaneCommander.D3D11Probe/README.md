# Standalone D3D11 presentation control

No WPF or PaneCommander assembly references. This project is intentionally outside the product solution.

Build:

```powershell
dotnet build tools/PaneCommander.D3D11Probe -c Release -m:1 -nr:false -p:UseSharedCompilation=false
```

Run the built EXE with one absolute, **nonexistent** output directory as its argument. Use a visible normal desktop window. Each run must use a new directory; the probe refuses an existing directory rather than overwriting evidence.

The 800×600 client window at (120,120) requests `D3D_DRIVER_TYPE_HARDWARE`, the default adapter, an RGBA8 UNORM windowed DXGI DISCARD swap chain with one buffer, and Present(1,0). No WARP/software fallback or settings changes. The actual device adapter, LUID, outputs, feature level, swap-chain settings, and native window/desktop states are logged.

GPU ClearRenderTargetView creates the background and a 760×270 temporary colored render target. GPU CopySubresourceRegion places red at (20,20) and blue at (20,310), matching the D3D9 rectangles. No CPU upload supplies the expected image. CopyResource to a STAGING/CPU_READ texture followed by blocking Map(READ, flags=0) synchronizes the GPU copy before reading RGB at (10,10), (40,40), and (40,340). Map row pitch is honored. The texture is non-sRGB. A mismatch stops the diagnostic frame before Present.

There are at most three explicitly bounded frames (shown, one deferred callback, and native paint), with no timer, continuous rendering, polling, or automatic retries. This is a presentation comparison, not a benchmark. Before/after Present states and device-removal HRESULTs are separate log events. Positive status codes such as DXGI_STATUS_OCCLUDED do not prove a visible image. Screenshots must be collected independently with the GUI tool.

`observations.json` contains the latest state, including the closed state after exit. Use `stages.json` for the pre-exit Present observations. Resources are released on close. Adapter capacity fields exist only to marshal the native SDK descriptor layout and are not logged or analyzed as memory usage.

Evidence: `docs/verification/d3d11-control-20261017/REPORT.md`.
