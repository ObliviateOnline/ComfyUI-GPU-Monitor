# ComfyUI GPU Monitor

Windows 本地 NVIDIA GPU 与 ComfyUI 监管工具。后台 PowerShell 看门狗静默轮询；桌面 EXE 提供状态查看和控制界面，不需要 Codex/API 会话。

## 使用

1. 双击 Install.bat，创建登录启动项和桌面快捷方式，并启动监控。
2. 双击桌面上的 ComfyUI GPU Monitor 打开监管界面。
3. 关闭窗口右上角的 X 只关闭界面，后台监控继续。
4. 使用界面按钮启用/继续、暂停、停止监控或取消待执行的 Windows 重启。

“停止并禁用自动启动”会停止当前监控，并让登录启动项在以后保持停用；再次按“启用/继续”可恢复。

## 重启判定

- nvidia-smi 连续失败，或近期日志/Windows 系统事件出现明确的 CUDA 设备丢失、sticky error 或显示驱动重置证据后，进入恢复等待。
- 只有 ComfyUI 在故障发生前处于运行状态且经过恢复等待仍未恢复，才会安排系统重启。
- 单独的 ComfyUI 未运行、端口未监听、普通工作流错误、WARNING 或显存不足不会触发重启。
- 监控事件写入 ComfyUI 根目录下的 logs/gpu_comfy_watch.log；正常轮询不写逐分钟状态。

默认 ComfyUI 根目录是 D:\ComfyUI\ComfyUI。若安装目录不同，可设置用户环境变量 COMFYUI_ROOT，然后重新登录。

## 构建

在 Windows PowerShell 运行：

    .\build.ps1

构建使用 Windows 自带的 .NET Framework C# 编译器生成 WinForms GUI 程序。

## 界面（2026-11-03 改版）

程序外壳改成暗色控制台风格，和光环画面统一：

- 深色底 + 24px 细网格；标题「光环守护」，下面一行等宽字体副标题（版本/端口）
- 青色渐变分隔线
- 三张状态卡（自绘）：监控状态 / NVIDIA GPU / COMFYUI，各自带一条状态色条（绿=正常，橙=警告，红=异常）
- 按钮改为自绘深色按钮，悬停时边框和底部亮条变成青色
- 标题栏用 DWM 的暗色模式（DWMWA_USE_IMMERSIVE_DARK_MODE），不再是一根白条
- 日志栏压成底部一行（最近日志：…），全文仍可点「打开监控日志」

自绘控件都在 gpu_comfy_watch_panel.cs 末尾：StatCard（状态卡）和 TechButton（按钮）。

## HALOFLUX 光环监控面板（2026-11-03 新增）

主窗口右侧内嵌了一块 WebView2，用 HALOFLUX（https://github.com/Ryder191/haloflux）的科幻光环画面显示**实时** GPU 利用率、显存、温度、CPU、内存和 ComfyUI 队列状态。原有 5 个按钮、状态行和日志区全部保留。

- 启动时自动拉起 `haloflux\bridge.py`（数据桥，只读 `nvidia-smi` 与 ComfyUI 的 HTTP 接口）；桥进程退出后 3 秒内自动重启；关窗口时一并结束。
- 画面地址 `http://127.0.0.1:8850/lab.html`，仅监听本机。
- 底部「显示装饰面板」：勾选后恢复原版那 8 个说明面板；默认不勾，光环占满画布。
- 点击画面获得焦点后：`F` 全屏、`C` 调参面板、`M` 音乐、`T` 节拍是否跟随负载。
- 节拍速度随 GPU/CPU 负载变化（空闲 1.6 → 满载 4.0）；GPU 负载瞬升 ≥30% 会补一拍，ComfyUI 任务结束会连打两拍。

运行依赖：

| 依赖 | 说明 |
|---|---|
| .NET Framework 4.8 | Windows 自带 |
| Microsoft Edge WebView2 Runtime | 本机已装（156.x）；缺失时窗口内会提示，可点「用浏览器打开」兜底 |
| Python 3 | PATH 里找，或 `C:\Python314\python.exe` 等固定位置 |
| `WebView2Loader.dll` 等 3 个 DLL | 必须和 EXE 同目录 |

重新编译：`.\build.ps1`（已改成引用本目录的 WebView2 DLL 并 `/platform:x64`）。

回退到原版：把 `ComfyUIGpuMonitor.exe.orig` 改名回 `HaloGuard.exe`，源码回退用 `gpu_comfy_watch_panel.cs.orig` 和 `build.ps1.orig`。

`haloflux\` 目录里的页面与素材按 **PolyForm Noncommercial 1.0.0** 授权，版权归 Ryder191（RYDER CODE / Ms MAIKEBAIs CAT），仅限非商业使用；`bridge.py` 与本次改动不在此列。

