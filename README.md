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
