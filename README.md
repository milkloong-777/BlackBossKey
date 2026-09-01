# BlackBossKey

> 一键黑屏，保护隐私。电脑照常运行，屏幕瞬间纯黑。

一个纯本地的 Windows 屏幕隐私工具。按快捷键让所有显示器瞬间变成纯黑遮罩，电脑本身继续正常运行——不锁屏、不休眠、不关机、不影响任何程序。

支持两种模式：**老板模式**（全黑+静音+鼠标隐藏）和**远程隐身模式**（物理屏黑，但手机远程软件看到的是真实桌面，可正常远程操作）。

🇬🇧 [English](README.en.md)

📚 **想了解实现原理？** 看这里：[docs/技术原理详解.md](docs/技术原理详解.md) —— 完整拆解每个功能用到的 Windows API、代码写法和踩坑记录。

## 截图

*(程序运行后无主窗口，仅系统托盘图标。黑屏效果为纯黑 RGB(0,0,0)，截图无意义。)*

## 快捷键

| 快捷键 | 功能 |
|---|---|
| `Ctrl + Alt + F12` | **老板模式**：全屏纯黑 + 静音 + 隐藏鼠标 |
| `Ctrl + Alt + F11` | **远程隐身模式**：物理屏黑，手机远程可操作 |
| `Ctrl + Alt + Esc` | **强制恢复**（任何模式下都能救回屏幕） |

## 两种模式对比

| | 老板模式 (`F12`) | 远程隐身模式 (`F11`) |
|---|---|---|
| 物理显示器 | 纯黑 | 纯黑 |
| 手机远程画面 | 也是纯黑 | **正常显示真实桌面** |
| 远程操作 | 不可用 | 完全可用（点击穿透） |
| 系统音频 | 静音（恢复时还原） | 不动（手机能听到声音） |
| 鼠标 | 光标隐藏（黑幕盖在鼠标上），但可正常移动点击；恢复后回原位 | 不隐藏（手机端可见光标） |
| 状态指示灯 | 状态灯：红=黑幕开启（常驻），绿=关闭（5 秒后淡出），物理屏和远程画面都可见 | 同左 |
| 程序运行 | 全部不受影响 | 全部不受影响 |

## 功能特性

### 核心
- **多显示器支持**：自动检测所有显示器的位置和尺寸（`Screen.AllScreens`），每块屏一个纯黑遮罩，支持不同分辨率、不同缩放比例、任意排列方式
- **真正的纯黑**：RGB(0,0,0)，无文字、无 Logo、无任务栏、无任何 UI 元素
- **不锁屏**：不调用 Win+L、不注销、不休眠、不关显示器电源——只是屏幕上的一层遮罩
- **程序不受影响**：视频、下载、游戏、编译、AI Agent 全部继续正常运行

### 老板模式 (`Ctrl+Alt+F12`)
- 系统默认音频输出设备静音（黑屏前状态会被记住，恢复时还原）
- 鼠标光标隐藏，恢复后回到黑屏前的位置

### 远程隐身模式 (`Ctrl+Alt+F11`)
- 使用 Windows `WDA_EXCLUDEFROMCAPTURE`（Win10 2004+）让遮罩窗口对屏幕捕获"隐身"
- Chrome 远程桌面、ToDesk、向日葵、RustDesk、OBS 等远程/录屏软件看到的都是真实桌面
- 遮罩点击穿透（`WS_EX_LAYERED | WS_EX_TRANSPARENT`），手机端的点击和键盘直达真实窗口
- 右上角小红点状态指示灯（不做捕获排除，手机端可见）
- 每 250ms 无条件重设捕获排除标志，防止 DWM 重启后失效

### 安全与稳定性
- **原子化初始化**：黑屏初始化任何一步失败，立即自动回滚全部状态，屏幕必然恢复
- **无条件强制清理**：`Ctrl+Alt+Esc` 不依赖状态标志，任何时候都能清掉一切残留
- **崩溃恢复**：强杀进程后音频静音状态残留？下次启动自动检测并还原
- **系统级置顶报错**：即使出错也不会被遮罩挡住，弹窗永远可见可关
- **紧急光标还原**：`BlackBossKey.exe --restore-cursors` 紧急还原系统光标
- **单实例保护**：重复启动不会创建第二个实例
- **键盘防护**：黑屏期间拦截 Win 键、Ctrl+Esc、Alt+Tab，开始菜单和任务切换器不会弹出
- **鼠标穿透可用**：黑幕盖在鼠标之上（光标不可见），但鼠标照常移动和点击，下层程序正常响应
- **状态灯**：每块屏右上角小圆点——黑幕开启变红（常驻，远程画面可见），关闭变绿（5 秒后淡出，桌面不常驻），开关状态一眼可辨
- **悬浮球适配**：自动隐藏桌面悬浮球（DesktopDock）等置顶小工具，恢复时原样还原，黑屏效果不被破坏
- **显示器热插拔**：黑屏状态下连接/断开显示器自动重新覆盖

### 隐私
- **完全本地运行**：无网络连接、无数据上传、无键盘记录、无截屏、无监控、不收集任何信息

## 使用方法

### 直接使用（推荐）

1. 下载 [Releases](../../releases) 中的 `BlackBossKey.exe`（自包含单文件，无需安装 .NET）
2. 双击运行，程序常驻系统托盘
3. 按快捷键操作，或右键托盘图标使用菜单

### 开机自启

将 `BlackBossKey.exe` 的快捷方式放入启动文件夹（`Win+R` → `shell:startup` → 回车）。

## 编译方法

环境要求：[.NET SDK 8.0](https://dotnet.microsoft.com/download) 及以上。

```bash
git clone https://github.com/milkloon-777/BlackBossKey.git
cd BlackBossKey
dotnet publish BlackBossKey.csproj -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true
```

产物位于 `bin\Release\net10.0-windows\win-x64\publish\BlackBossKey.exe`（约 107 MB，自包含单文件）。

## 技术栈

| 技术 | 用途 |
|---|---|
| C# / .NET 10 | 开发语言与运行时 |
| Windows Forms | 窗口与系统托盘 |
| `RegisterHotKey` | 全局热键注册 |
| `Screen.AllScreens` | 多显示器布局检测 |
| `SetWindowDisplayAffinity` (`WDA_EXCLUDEFROMCAPTURE`) | 屏幕捕获排除 |
| `WS_EX_LAYERED \| WS_EX_TRANSPARENT` | 点击穿透 |
| WASAPI / Core Audio (`IAudioEndpointVolume`) | 音频静音控制 |
| `WH_KEYBOARD_LL` | 低级键盘钩子（Win 键拦截） |
| `SetWindowPos` (`HWND_TOPMOST`) | 无损置顶重申 |
| `ShowCursor` / `GetCursorPos` / `SetCursorPos` | 鼠标控制 |

## 项目结构

```
BlackBossKey/
├── BlackBossKey.csproj    # 项目文件
├── Program.cs             # 全部源码（单文件，含注释）
├── .gitignore
├── LICENSE                # MIT
└── README.md              # 你正在看的这个
```

## 系统要求

- Windows 10 2004 (Build 19041) 及以上 / Windows 11
- x64 架构
- 远程隐身模式需要 Windows 10 2004+ 才支持 `WDA_EXCLUDEFROMCAPTURE`

## 已知限制

- `Ctrl+Alt+Del` 和 `Win+L` 是 Windows 安全序列，任何用户态程序都无法拦截
- DirectX 独占全屏模式（部分游戏）下遮罩可能被顶掉
- 远程软件自带的"隐私屏"功能如开启会与本工具冲突，二选一即可
- 极少数 GPU/驱动组合下某些捕获路径可能仍拍到遮罩（`WDA_EXCLUDEFROMCAPTURE` 的已知边界）

## 开发历程

本项目在开发过程中经历了多次迭代，解决了诸多实际问题：

| 版本 | 主要变更 |
|---|---|
| V1 | 初始版本：老板模式 + 多显示器 + 音频静音 + 鼠标隐藏 |
| V2 | 启动气泡提示 + 日志系统 + 热键冲突自动降级 |
| V3 | 远程隐身模式（`WDA_EXCLUDEFROMCAPTURE`） |
| V4 | 系统级鼠标隐藏 + 右下角频闪修复 |
| V5 | 键盘钩子（Win 键拦截）——存在严重 bug 已撤回 |
| V6 | 原子化初始化 + 无条件强制清理 + 置顶报错框（事故修复） |
| V6.1 | 远程模式撤销鼠标隐藏（手机端需要看到光标） |
| V6.2 | 修复点击穿透失效（`LAYERED + TRANSPARENT` 标准组合） |
| V6.3 | 远程隐身模式状态指示灯（小红点） |
| V6.4 | 崩溃恢复机制（强杀后音频自动还原） |

## 许可证

[MIT License](LICENSE)

## 致谢

- [LZong-tw/turn-off-screen](https://github.com/LZong-tw/turn-off-screen) — `WDA_EXCLUDEFROMCAPTURE` 与 DWM 重启重设的思路参考
- [Microsoft Win32 API 文档](https://learn.microsoft.com/windows/win32/api/) — `SetWindowDisplayAffinity`、`SetWindowPos`、Core Audio API
