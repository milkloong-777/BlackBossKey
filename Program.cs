// BlackBossKey - 老板键屏幕遮罩工具
// Ctrl+Alt+F12  : 开启 / 关闭黑屏遮罩（所有显示器）
// Ctrl+Alt+Esc  : 强制恢复（即使黑屏状态下也能救回）
//
// 纯本地工具：无网络、无监控、无键盘记录。

using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows.Forms;
using Microsoft.Win32;

namespace BlackBossKey;

// ============================ 入口 ============================

internal static class Program
{
    // 全局唯一的黑屏管理器：异常兜底、退出兜底都要能触达它做强制清理
    internal static readonly BlackoutManager Manager = new();

    [STAThread]
    static void Main(string[] args)
    {
        ApplicationConfiguration.Initialize();

        // 紧急还原模式：BlackBossKey.exe --restore-cursors
        // 万一程序异常退出且光标未还原，运行此命令立即恢复系统光标
        if (args is not null && args.Length > 0 &&
             (args.Contains("--restore-cursors") || args.Contains("/restore-cursors")))
        {
            SystemCursorHider.Restore();
            Native.MessageBoxW(IntPtr.Zero,
                "已发送系统光标还原指令。\r\n如果鼠标仍不可见，请注销并重新登录 Windows 即可完全还原。",
                "BlackBossKey 紧急还原",
                Native.MB_OK | Native.MB_ICONINFORMATION | Native.MB_TOPMOST | Native.MB_SETFOREGROUND);
            return;
        }

        // 探测模式：BlackBossKey.exe --probe
        // 检测 Ctrl+Alt+F12 / Ctrl+Alt+B 是否被其他程序占用，弹窗报告
        if (args is not null && args.Length > 0 &&
             (args.Contains("--probe") || args.Contains("/probe") || args.Contains("-probe")))
        {
            RunProbe();
            return;
        }

        // 单实例：已在运行则直接退出
        using var mutex = new Mutex(true, @"Local\BlackBossKey_SingleInstance", out bool createdNew);
        if (!createdNew)
        {
            MessageBox.Show("BlackBossKey 已经在运行中（托盘图标）。", "BlackBossKey",
                MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }

        // 全局异常兜底：不要静默崩溃，给出提示
        Application.SetUnhandledExceptionMode(UnhandledExceptionMode.CatchException);
        Application.ThreadException += (s, e) => Report(e.Exception);
        AppDomain.CurrentDomain.UnhandledException += (s, e) => Report(e.ExceptionObject as Exception);

        // 进程退出兜底：无论从哪条路径退出，都强制还原光标等全部状态
        AppDomain.CurrentDomain.ProcessExit += (s, e) =>
        {
            try { Manager.ForceCleanup(); } catch { }
            try { StateFile.Clear(); } catch { }   // 清理残留状态标记
        };

        // 启动时检测上次崩溃残留：如果上次被强杀，音频可能仍处于静音
        CrashRecovery.CheckAndRestore();

        Logger.Write("=== BlackBossKey 启动 ===");
        Application.Run(new HotkeyContext());
        try { Manager.ForceCleanup(); } catch { }
        Logger.Write("=== BlackBossKey 退出 ===");
    }

    static void RunProbe()
    {
        var win = new MessageWindow(_ => { });

        var results = new List<string>();
        foreach (var (vk, name) in new[] { ((uint)0x7B, "Ctrl+Alt+F12"), ((uint)0x42, "Ctrl+Alt+B"), ((uint)0x1B, "Ctrl+Alt+Esc") })
        {
            bool ok = Native.RegisterHotKey(win.Handle, 99, 0x0002 | 0x0001, vk);
            if (ok) { Native.UnregisterHotKey(win.Handle, 99); }
            results.Add($"{name} : {(ok ? "可用（无冲突）" : "被其他程序占用")}");
            Logger.Write($"[probe] {name} = {(ok ? "OK" : "OCCUPIED")}");
        }

        MessageBox.Show(
            "全局热键占用检测结果：\r\n\r\n" +
            string.Join(Environment.NewLine, results),
            "BlackBossKey 热键探测", MessageBoxButtons.OK, MessageBoxIcon.Information);
    }

    /// <summary>
    /// 全局报错：先无条件强制清理（关遮罩、还原光标/音频/钩子），
    /// 再弹系统级置顶错误框（永远在遮罩之上、永远可点击关闭）。
    /// 保证"报错时用户被锁在黑屏里"的情况在结构上不可能发生。
    /// </summary>
    static void Report(Exception? ex)
    {
        Logger.Write("异常: " + ex);
        try { Manager.ForceCleanup(); } catch { }
        try
        {
            Native.MessageBoxW(IntPtr.Zero,
                "BlackBossKey 发生错误，已自动恢复屏幕显示：\r\n\r\n" + ex?.Message,
                "BlackBossKey",
                Native.MB_OK | Native.MB_ICONERROR | Native.MB_TOPMOST | Native.MB_SETFOREGROUND);
        }
        catch { /* 忽略：错误提示本身不能崩 */ }
    }
}

// ============================ 日志 ============================

internal static class Logger
{
    private static readonly string LogPath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "BlackBossKey", "log.txt");

    public static void Write(string msg)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(LogPath)!);
            File.AppendAllText(LogPath,
                $"{DateTime.Now:yyyy-MM-dd HH:mm:ss}  {msg}\r\n");
        }
        catch { }
    }
}

// ============================ 崩溃恢复（残留状态标记） ============================
// taskkill /F 等强杀方式不等 ProcessExit 回调，音频/光标可能残留。
// 用临时文件标记"老板模式设了静音"，下次启动时检测并还原。

internal static class StateFile
{
    private static readonly string Dir = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "BlackBossKey");
    private static readonly string MuteFlagPath = Path.Combine(Dir, "mute_was_set.flag");
    private static readonly string CursorFlagPath = Path.Combine(Dir, "cursor_was_hidden.flag");

    public static void SetMuteFlag() { try { File.WriteAllText(MuteFlagPath, "1"); } catch { } }
    public static bool Exists() { try { return File.Exists(MuteFlagPath); } catch { return false; } }

    public static void SetCursorFlag() { try { File.WriteAllText(CursorFlagPath, "1"); } catch { } }
    public static bool CursorFlagExists() { try { return File.Exists(CursorFlagPath); } catch { return false; } }

    public static void Clear()
    {
        try { if (File.Exists(MuteFlagPath)) File.Delete(MuteFlagPath); } catch { }
        try { if (File.Exists(CursorFlagPath)) File.Delete(CursorFlagPath); } catch { }
    }
}

internal static class CrashRecovery
{
    public static void CheckAndRestore()
    {
        // 如果上次老板模式设了静音标记，且进程已重启（标记还在 = 上次没正常退出）
        if (StateFile.Exists())
        {
            Logger.Write("检测到上次崩溃残留（静音标记存在），正在还原音频…");
            try { AudioController.SetMute(false); } catch { }
            StateFile.Clear();
        }

        // 光标隐藏残留：强杀时系统光标可能还是空白替换状态
        if (StateFile.CursorFlagExists())
        {
            Logger.Write("检测到光标隐藏标记，正在还原系统光标…");
            SystemCursorHider.ForceRestore();
            StateFile.Clear();
        }
    }
}

// ============================ 托盘 + 全局热键 ============================

internal sealed class HotkeyContext : ApplicationContext
{
    private const int HOTKEY_TOGGLE     = 1;   // Ctrl+Alt+F12
    private const int HOTKEY_TOGGLE_ALT = 3;   // Ctrl+Alt+B（F12 被占用时的备用热键）
    private const int HOTKEY_FORCE      = 2;   // Ctrl+Alt+Esc
    private const int HOTKEY_REMOTE     = 4;   // Ctrl+Alt+F11：远程隐身模式

    private const uint MOD_ALT = 0x0001, MOD_CONTROL = 0x0002;
    private const uint VK_F12 = 0x7B, VK_ESCAPE = 0x1B, VK_B = 0x42, VK_F11 = 0x7A;

    private readonly MessageWindow _window;
    private readonly NotifyIcon _tray = new();
    private readonly BlackoutManager _manager = Program.Manager;   // 全局单例：异常兜底可触达
    private readonly Icon _icon;
    private string _toggleKeyName = "";

    public HotkeyContext()
    {
        _icon = IconFactory.Create();

        _window = new MessageWindow(OnHotKey);

        // 注册全局热键；Ctrl+Alt+F12 失败则自动尝试备用 Ctrl+Alt+B
        bool toggleOk = Native.RegisterHotKey(_window.Handle, HOTKEY_TOGGLE, MOD_CONTROL | MOD_ALT, VK_F12);
        if (toggleOk)
        {
            _toggleKeyName = "Ctrl+Alt+F12";
        }
        else
        {
            toggleOk = Native.RegisterHotKey(_window.Handle, HOTKEY_TOGGLE_ALT, MOD_CONTROL | MOD_ALT, VK_B);
            _toggleKeyName = "Ctrl+Alt+B";
            Logger.Write("Ctrl+Alt+F12 注册失败（被占用），已回退到 Ctrl+Alt+B");
        }

        bool forceOk = Native.RegisterHotKey(_window.Handle, HOTKEY_FORCE, MOD_CONTROL | MOD_ALT, VK_ESCAPE);
        bool remoteOk = Native.RegisterHotKey(_window.Handle, HOTKEY_REMOTE, MOD_CONTROL | MOD_ALT, VK_F11);

        Logger.Write($"开关热键 {_toggleKeyName}: {(toggleOk ? "OK" : "FAIL")}");
        Logger.Write($"强制恢复热键 Ctrl+Alt+Esc: {(forceOk ? "OK" : "FAIL")}");
        Logger.Write($"远程隐身热键 Ctrl+Alt+F11: {(remoteOk ? "OK" : "FAIL")}");

        if (!toggleOk)
            MessageBox.Show(
                "注册快捷键 Ctrl+Alt+F12 与备用 Ctrl+Alt+B 均失败，可能被其他程序占用。\n" +
                "程序仍将常驻托盘，可通过托盘菜单开启/关闭黑屏。",
                "BlackBossKey", MessageBoxButtons.OK, MessageBoxIcon.Warning);
        if (!forceOk)
            MessageBox.Show(
                "注册强制恢复快捷键 Ctrl+Alt+Esc 失败。\n" +
                "仍可通过托盘菜单强制恢复。",
                "BlackBossKey", MessageBoxButtons.OK, MessageBoxIcon.Warning);

        // 托盘菜单
        var toggleItem = new ToolStripMenuItem($"开启/关闭黑屏 ({_toggleKeyName})");
        toggleItem.Click += (s, e) => _manager.Toggle();

        var remoteItem = new ToolStripMenuItem("远程隐身模式 (Ctrl+Alt+F11)");
        remoteItem.Click += (s, e) => _manager.ToggleRemote();

        var forceItem = new ToolStripMenuItem("强制恢复 (Ctrl+Alt+Esc)");
        forceItem.Click += (s, e) => _manager.Restore();

        var exitItem = new ToolStripMenuItem("退出");
        exitItem.Click += (s, e) => ExitApp();

        var menu = new ContextMenuStrip();
        menu.Items.AddRange(new ToolStripItem[] { toggleItem, remoteItem, forceItem, new ToolStripSeparator(), exitItem });

        _tray.Icon = _icon;
        _tray.Text = $"BlackBossKey - {_toggleKeyName} 黑屏";
        _tray.Visible = true;
        _tray.ContextMenuStrip = menu;
        _tray.DoubleClick += (s, e) => _manager.Toggle();

        // 启动即显示常驻状态灯（绿 = 黑幕关闭，红 = 黑幕开启）
        _manager.EnsureLights();
        _manager.SetLights(false);

        // 启动提示：让用户明确知道程序已在运行、热键是什么
        _tray.ShowBalloonTip(3000, "BlackBossKey 已启动",
            $"黑屏开关：{_toggleKeyName}\n远程隐身：Ctrl+Alt+F11\n强制恢复：Ctrl+Alt+Esc",
            ToolTipIcon.Info);
    }

    private void OnHotKey(int id)
    {
        try
        {
            if (id == HOTKEY_TOGGLE || id == HOTKEY_TOGGLE_ALT)
            {
                Logger.Write(_manager.IsActive ? "热键触发：恢复屏幕" : "热键触发：开启黑屏（老板模式）");
                _manager.Toggle();
            }
            else if (id == HOTKEY_REMOTE)
            {
                Logger.Write(_manager.IsActive ? "热键触发：恢复屏幕" : "热键触发：开启远程隐身模式");
                _manager.ToggleRemote();
            }
            else if (id == HOTKEY_FORCE)
            {
                Logger.Write("强制恢复热键触发");
                _manager.Restore();
            }
        }
        catch (Exception ex)
        {
            // 热键处理出错时无条件强制清理（关遮罩、还原光标/音频/钩子），绝不能把用户困在黑屏里
            try { _manager.ForceCleanup(); } catch { }
            Logger.Write("热键处理异常: " + ex);
            // 系统级置顶弹窗：永远在遮罩之上、永远可点击关闭
            Native.MessageBoxW(IntPtr.Zero,
                "操作失败，已自动恢复屏幕显示：\r\n\r\n" + ex.Message,
                "BlackBossKey",
                Native.MB_OK | Native.MB_ICONERROR | Native.MB_TOPMOST | Native.MB_SETFOREGROUND);
        }
    }

    private void ExitApp()
    {
        try { _manager.Restore(); } catch { }
        try
        {
            Native.UnregisterHotKey(_window.Handle, HOTKEY_TOGGLE);
            Native.UnregisterHotKey(_window.Handle, HOTKEY_TOGGLE_ALT);
            Native.UnregisterHotKey(_window.Handle, HOTKEY_FORCE);
            Native.UnregisterHotKey(_window.Handle, HOTKEY_REMOTE);
        }
        catch { }
        _tray.Visible = false;
        _tray.Dispose();
        Application.Exit();
    }
}

// ============================ 系统级光标隐藏（远程模式用） ============================
// ShowCursor 只对本线程窗口生效；远程隐身模式下遮罩点击穿透、光标悬停在桌面窗口上，
// 必须把整个系统光标替换成空白光标才能真正隐藏。退出时用 SPI_SETCURSORS 从注册表还原。

internal static class SystemCursorHider
{
    private static bool _hidden;

    public static void Hide()
    {
        if (_hidden) return;
        try
        {
            IntPtr hInst = Native.GetModuleHandle(null);
            // AND 掩码全 1（全透明）+ XOR 全 0 → 完全不可见的光标
            byte[] andPlane = new byte[128];
            Array.Fill(andPlane, (byte)0xFF);
            byte[] xorPlane = new byte[128];
            IntPtr blank = Native.CreateCursor(hInst, 0, 0, 32, 32, andPlane, xorPlane);
            if (blank == IntPtr.Zero) return;

            foreach (uint id in Native.SystemCursorIds)
            {
                // SetSystemCursor 会销毁传入的句柄，每次传副本
                IntPtr copy = Native.CopyIcon(blank);
                if (copy != IntPtr.Zero) Native.SetSystemCursor(copy, id);
            }
            Native.DestroyIcon(blank);
            _hidden = true;
        }
        catch { }
    }

    public static void Restore()
    {
        if (!_hidden) return;
        try { Native.SystemParametersInfo(Native.SPI_SETCURSORS, 0, IntPtr.Zero, 0); } catch { }
        _hidden = false;
    }

    // 无条件还原：供崩溃恢复使用（新进程里 _hidden 恒为 false，需要绕过守卫）
    public static void ForceRestore()
    {
        try { Native.SystemParametersInfo(Native.SPI_SETCURSORS, 0, IntPtr.Zero, 0); } catch { }
        _hidden = false;
    }
}

// ============================ 键盘拦截（黑屏期间） ============================
// 黑屏时按 Win 会弹出开始菜单（置顶窗口，会和遮罩抢层级）。
// 用 WH_KEYBOARD_LL 低级钩子在黑屏期间吞掉会弹出系统界面的按键：
//   - LWin / RWin 及所有 Win 组合键
//   - Ctrl+Esc（同样会打开开始菜单）
//   - Alt+Tab / Alt+Esc（任务切换器）
// 注意：Ctrl+Alt+F12 / Ctrl+Alt+Esc / Ctrl+Alt+F11 热键不受影响（钩子会放行）。
// Ctrl+Alt+Del 和 Win+L 属于系统安全序列，任何用户态程序都无法拦截。

internal sealed class KeyboardBlocker
{
    private IntPtr _hook = IntPtr.Zero;
    private bool _active;
    private readonly Native.LowLevelKeyboardProc _proc;

    public KeyboardBlocker()
    {
        // 持有委托引用，防止被 GC 回收导致钩子失效
        _proc = HookProc;
    }

    public void Start()
    {
        if (_active) return;
        _hook = Native.SetWindowsHookEx(Native.WH_KEYBOARD_LL, _proc, Native.GetModuleHandle(null), 0);
        _active = _hook != IntPtr.Zero;
        if (!_active) Logger.Write("键盘钩子安装失败（黑屏期间 Win 键将无法拦截）");
    }

    public void Stop()
    {
        if (!_active) return;
        try { Native.UnhookWindowsHookEx(_hook); } catch { }
        _hook = IntPtr.Zero;
        _active = false;
    }

    private static bool KeyDown(int vk) => (Native.GetAsyncKeyState(vk) & 0x8000) != 0;

    private IntPtr HookProc(int nCode, IntPtr wParam, IntPtr lParam)
    {
        const int WM_KEYDOWN = 0x0100, WM_KEYUP = 0x0101, WM_SYSKEYDOWN = 0x0104, WM_SYSKEYUP = 0x0105;

        if (nCode >= 0 && _active)
        {
            int msg = wParam.ToInt32();
            var info = Marshal.PtrToStructure<Native.KBDLLHOOKSTRUCT>(lParam);
            uint vk = info.vkCode;
            bool isDown = msg == WM_KEYDOWN || msg == WM_SYSKEYDOWN;

            bool winDown = KeyDown(Native.VK_LWIN) || KeyDown(Native.VK_RWIN);
            bool ctrlDown = KeyDown(Native.VK_CONTROL);
            bool altDown = (info.flags & Native.LLKHF_ALTDOWN) != 0 || KeyDown(Native.VK_MENU);

            // 1. Win 键本身：按下和抬起都吞掉（开始菜单是在 Win 抬起时弹出的）
            if (vk == Native.VK_LWIN || vk == Native.VK_RWIN)
                return (IntPtr)1;

            // 2. Win + 任意键的组合
            if (winDown)
                return (IntPtr)1;

            // 3. Ctrl+Esc 打开开始菜单（但要放行 Ctrl+Alt+Esc 强制恢复热键）
            if (vk == Native.VK_ESCAPE && ctrlDown && !altDown)
                return (IntPtr)1;

            // 4. Alt+Tab / Alt+Esc 任务切换器（同样放行带 Ctrl 的强制恢复热键）
            if (altDown && !ctrlDown &&
                (vk == Native.VK_TAB || vk == Native.VK_ESCAPE))
                return (IntPtr)1;
        }

        return Native.CallNextHookEx(_hook, nCode, wParam, lParam);
    }
}

// ============================ 黑屏管理 ============================

internal sealed class BlackoutManager
{
    private readonly List<BlackoutForm> _overlays = new();
    private readonly List<IndicatorForm> _lights = new();   // 常驻状态灯：红=开启 绿=关闭
    private System.Windows.Forms.Timer? _topmostTimer;
    private bool _active;
    private bool _remoteMode;      // 远程隐身模式：物理屏黑、屏幕捕获看到真实桌面
    private Native.POINT _savedCursor;
    private bool _cursorHidden;
    private bool? _audioWasMute;   // null = 音频操作失败，恢复时跳过
    private readonly KeyboardBlocker _kbBlocker = new();

    public bool IsActive => _active;

    public void Toggle()
    {
        if (_active) Restore();
        else Blackout(remoteMode: false);
    }

    public void ToggleRemote()
    {
        if (_active) Restore();
        else Blackout(remoteMode: true);
    }

    public void Blackout(bool remoteMode = false)
    {
        if (_active) return;
        _remoteMode = remoteMode;

        // ===== 原子化初始化：任何一步失败，立即无条件回滚全部状态并重新抛出 =====
        // 这样"遮罩在屏幕上但状态未记录"的半初始化状态不可能存在（V5 事故的根因）。
        try
        {
            // 1. 键盘钩子最先装（历史上唯一抛过异常的步骤，失败就发生在碰任何状态之前）
            _kbBlocker.Start();

            // 2. 保存鼠标位置（远程模式不隐藏/移动鼠标，手机端需要看到光标）
            if (!remoteMode)
            {
                try { Native.GetCursorPos(out _savedCursor); } catch { }

                // 3. 保存音频静音状态并静音（远程模式不能静音，否则手机也听不到）
                try
                {
                    _audioWasMute = AudioController.GetMute();
                    if (_audioWasMute.HasValue) { AudioController.SetMute(true); StateFile.SetMuteFlag(); }
                }
                catch { _audioWasMute = null; }

                // 4. 系统级隐藏光标（黑幕盖在鼠标之上）：遮罩点击穿透后光标悬停在
                //    其他窗口上，ShowCursor（按线程）无效，必须替换系统光标才能隐身。
                //    鼠标本身继续工作：移动、点击全部正常作用到下层窗口。
                try
                {
                    SystemCursorHider.Hide();
                    _cursorHidden = true;
                    StateFile.SetCursorFlag();
                }
                catch { _cursorHidden = false; }
            }
            else
            {
                _audioWasMute = null;
                _cursorHidden = false;
                // 远程模式：不隐藏鼠标。隐藏系统光标会让手机端也看不到光标、无法操作
                // （V6.1 按用户反馈撤销）。物理屏上会有一个光标在黑屏上移动，属可接受代价。
                // SystemCursorHider 类保留，仅在 --restore-cursors 紧急还原时使用。
            }

            // 5. 为每块显示器创建纯黑遮罩（两种模式都点击穿透——鼠标保持可用）
            foreach (var screen in Screen.AllScreens)
            {
                var form = new BlackoutForm(screen, clickThrough: true);
                _overlays.Add(form);
            }
            foreach (var form in _overlays) form.Show();

            // 5.5 状态灯切红：黑幕开启（常驻灯，红=开 绿=关，远程画面可见）
            EnsureLights();
            SetLights(true);

            // 5.6 隐藏桌面悬浮球（DesktopDock 置顶窗口会浮在遮罩上）
            DockHider.Hide();

            // 6. 高频维护：无损重申置顶 + 无条件重设捕获排除标志
            _topmostTimer = new System.Windows.Forms.Timer { Interval = 250 };
            _topmostTimer.Tick += (s, e) =>
            {
                foreach (var form in _overlays)
                {
                    if (form.IsDisposed) continue;
                    form.AssertTopmost();
                    form.RealignIfNecessary();
                    if (remoteMode) form.ApplyCaptureExclusion();
                }
                // 状态灯要保持压在遮罩之上
                foreach (var light in _lights)
                {
                    if (!light.IsDisposed) light.AssertTopmost();
                }
                // 悬浮球若被看门狗拉起则再次隐藏
                DockHider.EnsureHidden();
            };
            _topmostTimer.Start();

            // 7. 显示器连接/断开或分辨率变化时重新覆盖
            SystemEvents.DisplaySettingsChanged += OnDisplaySettingsChanged;

            _active = true;
            Logger.Write(remoteMode ? "已进入远程隐身模式（物理屏黑，远程捕获可见真实桌面）"
                                    : "已进入老板模式（全屏纯黑 + 静音）");
        }
        catch (Exception ex)
        {
            Logger.Write("Blackout 初始化失败，已自动回滚全部状态: " + ex);
            ForceCleanup();   // 无论进行到哪一步，全部回滚，屏幕必然恢复
            throw;            // 上层会弹出置顶报错框（此时屏幕已恢复正常）
        }
    }

    /// <summary>
    /// 无条件、幂等的全面清理：不管状态标志是什么，只要存在任何已生效的黑屏副作用
    /// （遮罩窗口 / 钩子 / 定时器 / 音频 / 光标）都全部还原。强制恢复热键与异常回滚
    /// 都走这里——它永不因状态判断而跳过。
    /// </summary>
    public void ForceCleanup()
    {
        bool wasActive = _active;
        _active = false;

        try { SystemEvents.DisplaySettingsChanged -= OnDisplaySettingsChanged; } catch { }
        _kbBlocker.Stop();

        try { _topmostTimer?.Stop(); _topmostTimer?.Dispose(); } catch { }
        _topmostTimer = null;

        foreach (var form in _overlays)
        {
            try { form.Close(); } catch { }
            try { form.Dispose(); } catch { }
        }
        _overlays.Clear();

        // 状态灯是常驻的：不销毁，只变色（绿 = 关闭）
        SetLights(false);

        // 音频还原（不区分模式，有记录就还原）
        if (_audioWasMute.HasValue)
        {
            try { AudioController.SetMute(_audioWasMute.Value); } catch { }
            StateFile.Clear();   // 正常还原后清除崩溃标记
            _audioWasMute = null;
        }

        // 光标还原：系统级替换还原（黑幕盖在鼠标上的隐藏方式）
        _cursorHidden = false;
        SystemCursorHider.Restore();
        StateFile.Clear();

        // 悬浮球还原显示
        DockHider.Restore();

        // 老板模式黑屏过：光标位置还原
        if (wasActive && !_remoteMode)
        {
            try { Native.SetCursorPos(_savedCursor.X, _savedCursor.Y); } catch { }
        }

        if (wasActive)
        {
            Logger.Write("已恢复屏幕显示（模式: " + (_remoteMode ? "远程隐身" : "老板") + "）");
        }
        _remoteMode = false;
    }

    /// <summary>恢复屏幕显示（幂等：非黑屏状态调用也是安全的全面清理）。</summary>
    public void Restore() => ForceCleanup();

    /// <summary>
    /// 确保每块屏幕都有一个状态灯；数量/布局不匹配时按当前显示器重建。
    /// 状态灯是常驻的（红=黑幕开启，绿=关闭），不随黑屏清理销毁。
    /// </summary>
    public void EnsureLights()
    {
        var screens = Screen.AllScreens;
        bool needRebuild = _lights.Count != screens.Length ||
                           _lights.Any(l => l.IsDisposed);

        if (needRebuild)
        {
            foreach (var l in _lights)
            {
                try { l.Close(); } catch { }
                try { l.Dispose(); } catch { }
            }
            _lights.Clear();
            foreach (var screen in screens)
            {
                _lights.Add(new IndicatorForm(screen, _active));
            }
            foreach (var l in _lights) l.Show();
        }
        else
        {
            // 已有灯：校准位置（显示器排列可能变化）
            for (int i = 0; i < screens.Length && i < _lights.Count; i++)
            {
                if (!_lights[i].IsDisposed) _lights[i].RebindScreen(screens[i]);
            }
        }
    }

    /// <summary>设置状态灯颜色：true=红（黑幕开启），false=绿（关闭）。</summary>
    public void SetLights(bool on)
    {
        EnsureLights();
        foreach (var l in _lights)
        {
            if (!l.IsDisposed) l.SetOn(on);
        }
    }

    private void OnDisplaySettingsChanged(object? sender, EventArgs e)
    {
        // 黑屏中：遮罩跟随新布局重建
        if (_active)
        {
            foreach (var form in _overlays)
            {
                try { form.Close(); } catch { }
                try { form.Dispose(); } catch { }
            }
            _overlays.Clear();

            foreach (var screen in Screen.AllScreens)
            {
                var form = new BlackoutForm(screen, clickThrough: true);
                _overlays.Add(form);
            }
            foreach (var form in _overlays) form.Show();
        }

        // 状态灯是常驻的：无论是否黑屏都要跟随新显示器布局
        EnsureLights();
        SetLights(_active);
    }
}

// 状态灯：每块屏右上角一个小圆点，显示黑幕开关状态。
// 红点 = 黑幕开启（老板/远程模式）→ 常驻，随时可辨（手机远程画面也可见）。
// 绿点 = 关闭 → 只显示 5 秒后淡出，桌面不留常驻点。
// 不做捕获排除——远程画面里也能看到。点击穿透，不挡任何操作。
internal sealed class IndicatorForm : Form
{
    private bool _on;
    private readonly System.Windows.Forms.Timer _fadeTimer = new() { Interval = 200 };
    private const int HoldTicks = 25;      // 25 × 200ms = 5 秒保持
    private int _tick;

    public IndicatorForm(Screen screen, bool on)
    {
        FormBorderStyle = FormBorderStyle.None;
        StartPosition = FormStartPosition.Manual;
        Size = new Size(28, 28);
        Location = new Point(screen.Bounds.Right - 36, screen.Bounds.Top + 8);
        ShowInTaskbar = false;
        TopMost = true;
        BackColor = Color.Fuchsia;            // 品红作为透明键
        TransparencyKey = Color.Fuchsia;      // 方形背景完全透明，只剩圆点
        _fadeTimer.Tick += (s, e) => OnFadeTick();
        _on = on;
        if (!on) { _fadeTimer.Start(); }      // 启动时绿灯：5 秒后淡出
    }

    protected override void Dispose(bool disposing)
    {
        try { _fadeTimer.Stop(); _fadeTimer.Dispose(); } catch { }
        base.Dispose(disposing);
    }

    public void SetOn(bool on)
    {
        if (_on == on) return;
        _on = on;
        if (on)
        {
            // 红灯：黑幕开启，常驻显示
            _fadeTimer.Stop();
            Opacity = 1.0;
            Show();
        }
        else
        {
            // 绿灯：黑幕关闭，显示 5 秒后淡出
            Opacity = 1.0;
            Show();
            _tick = 0;
            _fadeTimer.Start();
        }
        try { Invalidate(); } catch { }
    }

    private void OnFadeTick()
    {
        _tick++;
        if (_tick <= HoldTicks) return;                 // 前 5 秒保持全亮
        Opacity = Math.Max(0.0, Opacity - 0.1);         // 之后每 200ms 降 0.1 → 约 1 秒淡出
        if (Opacity <= 0.01)
        {
            _fadeTimer.Stop();
            try { Hide(); } catch { }
        }
    }

    public void RebindScreen(Screen screen)
    {
        Location = new Point(screen.Bounds.Right - 36, screen.Bounds.Top + 8);
    }

    protected override bool ShowWithoutActivation => true;

    protected override CreateParams CreateParams
    {
        get
        {
            var cp = base.CreateParams;
            cp.ExStyle |= 0x00000080 /*TOOLWINDOW*/ | 0x00000008 /*TOPMOST*/
                        | 0x08000000 /*NOACTIVATE*/ | 0x00000020 /*TRANSPARENT 点击穿透*/;
            return cp;
        }
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        base.OnPaint(e);
        e.Graphics.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
        if (_on)
        {
            using var glow = new SolidBrush(Color.FromArgb(90, 255, 40, 40));
            using var dot = new SolidBrush(Color.FromArgb(235, 25, 25));
            e.Graphics.FillEllipse(glow, 3, 3, 22, 22);   // 外圈微光
            e.Graphics.FillEllipse(dot, 7, 7, 14, 14);    // 红点 = 黑幕开启
        }
        else
        {
            using var glow = new SolidBrush(Color.FromArgb(80, 40, 200, 90));
            using var dot = new SolidBrush(Color.FromArgb(30, 190, 90));
            e.Graphics.FillEllipse(glow, 3, 3, 22, 22);   // 外圈微光
            e.Graphics.FillEllipse(dot, 7, 7, 14, 14);    // 绿点 = 黑幕关闭
        }
    }

    // 无损重申置顶（遮罩每 250ms 也会重申，指示灯必须保持在其之上）
    public void AssertTopmost()
    {
        try
        {
            if (IsDisposed || !IsHandleCreated) return;
            Native.SetWindowPos(Handle, Native.HWND_TOPMOST, 0, 0, 0, 0,
                Native.SWP_NOMOVE | Native.SWP_NOSIZE | Native.SWP_NOACTIVATE | Native.SWP_SHOWWINDOW);
        }
        catch { }
    }
}

// ============================ 悬浮球适配（DesktopDock） ============================
// 桌面悬浮球（DesktopDock）是置顶 WPF 窗口，黑屏时会浮在纯黑遮罩上面，
// 远程隐身模式下还会被手机端看到。黑屏期间用 ShowWindow(SW_HIDE) 隐藏它，
// 恢复时还原。不补丁悬浮球本身——它怎么升级都不受影响。
// 若黑屏期间悬浮球被看门狗拉起，250ms 定时器会再次隐藏。

internal static class DockHider
{
    private const string DockProcessName = "DesktopDock";
    private const int SW_HIDE = 0, SW_SHOW = 5;

    private static readonly List<IntPtr> _hiddenHwnds = new();
    private static bool _active;

    public static void Hide()
    {
        _active = true;
        HideOnce();
    }

    // 定时器调用：处理黑屏期间悬浮球被重启/新窗口出现的情况
    public static void EnsureHidden()
    {
        if (_active) HideOnce();
    }

    private static void HideOnce()
    {
        try
        {
            var pids = Process.GetProcessesByName(DockProcessName)
                              .Select(p => (uint)p.Id).ToHashSet();
            if (pids.Count == 0) return;

            Native.EnumWindows((h, l) =>
            {
                Native.GetWindowThreadProcessId(h, out uint pid);
                if (pids.Contains(pid) && Native.IsWindowVisible(h))
                {
                    Native.ShowWindow(h, SW_HIDE);
                    if (!_hiddenHwnds.Contains(h)) _hiddenHwnds.Add(h);
                }
                return true;
            }, IntPtr.Zero);
        }
        catch { }
    }

    public static void Restore()
    {
        _active = false;
        foreach (var h in _hiddenHwnds)
        {
            try { if (Native.IsWindow(h)) Native.ShowWindow(h, SW_SHOW); } catch { }
        }
        _hiddenHwnds.Clear();
    }
}

// 纯黑置顶遮罩窗口：无激活、不在 Alt-Tab 出现
// clickThrough=true（远程隐身模式）时：
//   - WS_EX_TRANSPARENT 点击穿透，远程输入直达下层窗口
//   - WDA_EXCLUDEFROMCAPTURE 从屏幕捕获中排除
//   → 物理显示器纯黑，但 Chrome 远程桌面 / ToDesk / 向日葵 / RustDesk / OBS 捕获到的是真实桌面
internal sealed class BlackoutForm : Form
{
    private const int WS_EX_TOOLWINDOW  = 0x00000080;
    private const int WS_EX_TOPMOST     = 0x00000008;
    private const int WS_EX_NOACTIVATE  = 0x08000000;
    private const int WS_EX_TRANSPARENT = 0x00000020;
    private const int WS_EX_LAYERED     = 0x00080000;

    private const uint LWA_ALPHA = 0x00000002;
    private const uint WDA_EXCLUDEFROMCAPTURE = 0x00000011;

    private Rectangle _targetBounds;
    private readonly bool _clickThrough;

    public BlackoutForm(Screen screen, bool clickThrough = false)
    {
        _targetBounds = screen.Bounds;
        _clickThrough = clickThrough;
        FormBorderStyle = FormBorderStyle.None;
        StartPosition = FormStartPosition.Manual;
        Bounds = _targetBounds;
        BackColor = Color.Black;              // RGB(0,0,0) 纯黑
        ShowInTaskbar = false;
        TopMost = true;
        KeyPreview = false;
        DoubleBuffered = true;
    }

    protected override bool ShowWithoutActivation => true;

    protected override CreateParams CreateParams
    {
        get
        {
            var cp = base.CreateParams;
            cp.ExStyle |= WS_EX_TOOLWINDOW | WS_EX_TOPMOST | WS_EX_NOACTIVATE;
            if (_clickThrough)
            {
                // 经典点击穿透组合：LAYERED + TRANSPARENT（仅 TRANSPARENT 在部分
                // 场合下穿透不可靠，会导致鼠标点击被遮罩吞掉——V6.1 用户实测）
                cp.ExStyle |= WS_EX_TRANSPARENT | WS_EX_LAYERED;
            }
            return cp;
        }
    }

    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);
        if (_clickThrough && IsHandleCreated)
        {
            // LAYERED 窗口必须设置属性才会渲染；alpha=255 = 完全不透明的纯黑
            try { Native.SetLayeredWindowAttributes(Handle, 0, 255, LWA_ALPHA); } catch { }
        }
    }

    // 无损重申置顶：直接 SetWindowPos(HWND_TOPMOST)，不经历"先降级再置顶"的中间态
    public void AssertTopmost()
    {
        try
        {
            if (IsDisposed || !IsHandleCreated) return;
            Native.SetWindowPos(Handle, Native.HWND_TOPMOST, 0, 0, 0, 0,
                Native.SWP_NOMOVE | Native.SWP_NOSIZE | Native.SWP_NOACTIVATE | Native.SWP_SHOWWINDOW);
        }
        catch { }
    }

    // 从屏幕捕获中排除本窗口（需在窗口句柄创建后调用；DWM 重启会失效，由定时器周期性重设）
    public void ApplyCaptureExclusion()
    {
        try
        {
            if (!IsHandleCreated || IsDisposed) return;
            Native.SetWindowDisplayAffinity(Handle, WDA_EXCLUDEFROMCAPTURE);
        }
        catch { }
    }

    protected override void OnShown(EventArgs e)
    {
        base.OnShown(e);
        ApplyCaptureExclusion();
    }

    // 分辨率/布局变化后纠正覆盖范围
    public void RealignIfNecessary()
    {
        var screen = Screen.FromHandle(Handle);
        if (screen.Bounds != _targetBounds)
        {
            _targetBounds = screen.Bounds;
            Bounds = _targetBounds;
        }
    }

    // 老板模式：吞掉鼠标命中（遮罩不接受任何交互痕迹）
    // 远程模式（点击穿透）：必须走默认处理，否则命中测试会把点击留在遮罩上，
    //   导致物理鼠标和手机远程的点击全部失效（V6.1 用户实测的"鼠标用不了"根因）
    protected override void WndProc(ref Message m)
    {
        const int WM_NCHITTEST = 0x0084;
        if (m.Msg == WM_NCHITTEST && !_clickThrough) { m.Result = (IntPtr)1; return; } // HTCLIENT
        base.WndProc(ref m);
    }
}

// ============================ 音频（WASAPI / Core Audio） ============================

internal static class AudioController
{
    private static readonly Guid IID_IAudioEndpointVolume = new("5CDF2C82-841E-4546-9722-0CF74078229A");
    private const int CLSCTX_ALL = 0x17;
    private const int ERENDER = 0;      // eRender（扬声器等输出设备）
    private const int EMULTIMEDIA = 1;  // eMultimedia

    /// <summary>获取默认输出设备的静音状态；失败返回 null。</summary>
    public static bool? GetMute()
    {
        var vol = GetEndpointVolume();
        if (vol == null) return null;
        return vol.GetMuteSafe();
    }

    /// <summary>设置默认输出设备静音状态。</summary>
    public static void SetMute(bool mute)
    {
        var vol = GetEndpointVolume();
        vol?.SetMuteSafe(mute);
    }

    private static IAudioEndpointVolume? GetEndpointVolume()
    {
        try
        {
            Type? comType = Type.GetTypeFromCLSID(new Guid("BCDE0395-E52F-467C-8E3D-C4579291692E"));
            if (comType == null) return null;
            var instance = Activator.CreateInstance(comType);
            if (instance == null) return null;
            var enumerator = (IMMDeviceEnumerator)instance;
            if (enumerator.GetDefaultAudioEndpoint(ERENDER, EMULTIMEDIA, out IMMDevice device) != 0)
                return null;

            Guid iid = IID_IAudioEndpointVolume;
            if (device.Activate(ref iid, CLSCTX_ALL, IntPtr.Zero, out object? obj) != 0)
                return null;

            return obj as IAudioEndpointVolume;
        }
        catch { return null; }
    }
}

// ---------- Core Audio COM interop ----------

[ComImport, Guid("BCDE0395-E52F-467C-8E3D-C4579291692E")]
internal sealed class MMDeviceEnumeratorComObject { }

[ComImport, Guid("A95664D2-9614-4F35-A746-DE8DB63617E6"),
 InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IMMDeviceEnumerator
{
    [PreserveSig] int EnumAudioEndpoints(int dataFlow, int stateMask, out IntPtr devices);
    [PreserveSig] int GetDefaultAudioEndpoint(int dataFlow, int role, out IMMDevice device);
    [PreserveSig] int GetDevice(string id, out IMMDevice device);
    [PreserveSig] int RegisterEndpointNotificationCallback(IntPtr client);
    [PreserveSig] int UnregisterEndpointNotificationCallback(IntPtr client);
}

[ComImport, Guid("D666063F-1587-4E43-81F1-B948E807363F"),
 InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IMMDevice
{
    [PreserveSig] int Activate(ref Guid iid, int dwClsCtx, IntPtr pActivationParams,
        [MarshalAs(UnmanagedType.IUnknown)] out object? ppInterface);
    [PreserveSig] int OpenPropertyStore(int stgmAccess, out IntPtr propertyStore);
    [PreserveSig] int GetId([MarshalAs(UnmanagedType.LPWStr)] out string id);
    [PreserveSig] int GetState(out int state);
}

[ComImport, Guid("5CDF2C82-841E-4546-9722-0CF74078229A"),
 InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IAudioEndpointVolume
{
    [PreserveSig] int RegisterControlChangeNotify(IntPtr notify);
    [PreserveSig] int UnregisterControlChangeNotify(IntPtr notify);
    [PreserveSig] int GetChannelCount(out uint channelCount);
    [PreserveSig] int SetMasterVolumeLevel(float levelDB, ref Guid eventContext);
    [PreserveSig] int SetMasterVolumeLevelScalar(float level, ref Guid eventContext);
    [PreserveSig] int GetMasterVolumeLevel(out float levelDB);
    [PreserveSig] int GetMasterVolumeLevelScalar(out float level);
    [PreserveSig] int SetChannelVolumeLevel(uint channel, float levelDB, ref Guid eventContext);
    [PreserveSig] int SetChannelVolumeLevelScalar(uint channel, float level, ref Guid eventContext);
    [PreserveSig] int GetChannelVolumeLevel(uint channel, out float levelDB);
    [PreserveSig] int GetChannelVolumeLevelScalar(uint channel, out float level);
    [PreserveSig] int SetMute(bool mute, ref Guid eventContext);
    [PreserveSig] int GetMute(out bool mute);
    [PreserveSig] int GetVolumeStepInfo(out uint step, out uint stepCount);
    [PreserveSig] int VolumeStepUp(ref Guid eventContext);
    [PreserveSig] int VolumeStepDown(ref Guid eventContext);
    [PreserveSig] int QueryHardwareSupport(out uint hardwareSupport);
    [PreserveSig] int GetVolumeRange(out float minDB, out float maxDB, out float incrementDB);
}

internal static class AudioEndpointVolumeExtensions
{
    internal static bool GetMuteSafe(this IAudioEndpointVolume vol)
    {
        bool mute = false;
        try { vol.GetMute(out mute); } catch { }
        return mute;
    }

    internal static void SetMuteSafe(this IAudioEndpointVolume vol, bool mute)
    {
        try
        {
            Guid ctx = Guid.Empty;
            vol.SetMute(mute, ref ctx);
        }
        catch { }
    }
}

// ============================ Win32 API ============================

internal static class Native
{
    [DllImport("user32.dll", SetLastError = true)]
    internal static extern bool RegisterHotKey(IntPtr hWnd, int id, uint fsModifiers, uint vk);

    [DllImport("user32.dll", SetLastError = true)]
    internal static extern bool UnregisterHotKey(IntPtr hWnd, int id);

    [DllImport("user32.dll")]
    internal static extern bool GetCursorPos(out POINT lpPoint);

    [DllImport("user32.dll")]
    internal static extern bool SetCursorPos(int x, int y);

    [DllImport("user32.dll")]
    internal static extern bool SetWindowDisplayAffinity(IntPtr hWnd, uint dwAffinity);

    [DllImport("user32.dll")]
    internal static extern bool SetLayeredWindowAttributes(IntPtr hWnd, uint crKey, byte bAlpha, uint dwFlags);

    [DllImport("user32.dll")]
    internal static extern bool EnumWindows(EnumWindowsProc lpEnumFunc, IntPtr lParam);

    internal delegate bool EnumWindowsProc(IntPtr hWnd, IntPtr lParam);

    [DllImport("user32.dll")]
    internal static extern bool IsWindowVisible(IntPtr hWnd);

    [DllImport("user32.dll")]
    internal static extern bool IsWindow(IntPtr hWnd);

    [DllImport("user32.dll")]
    internal static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);

    [DllImport("user32.dll")]
    internal static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint processId);

    [DllImport("user32.dll")]
    internal static extern bool SetWindowPos(IntPtr hWnd, IntPtr hWndInsertAfter,
        int x, int y, int cx, int cy, uint flags);

    internal static readonly IntPtr HWND_TOPMOST = new(-1);
    internal const uint SWP_NOMOVE = 0x0002, SWP_NOSIZE = 0x0001,
        SWP_NOACTIVATE = 0x0010, SWP_SHOWWINDOW = 0x0040;

    [DllImport("kernel32.dll", SetLastError = true)]
    internal static extern IntPtr GetModuleHandle(string? lpModuleName);

    // 系统级置顶弹窗：永远显示在遮罩之上、永远可以点击关闭
    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    internal static extern int MessageBoxW(IntPtr hWnd, string text, string caption, uint type);

    internal const uint MB_OK = 0x0, MB_ICONERROR = 0x10, MB_ICONINFORMATION = 0x40,
        MB_TOPMOST = 0x40000, MB_SETFOREGROUND = 0x10000, MB_SYSTEMMODAL = 0x1000;

    [DllImport("user32.dll")]
    internal static extern IntPtr CreateCursor(IntPtr hInst, int xHotspot, int yHotspot,
        int nWidth, int nHeight, byte[] pvANDPlane, byte[] pvXORPlane);

    [DllImport("user32.dll")]
    internal static extern bool SetSystemCursor(IntPtr hcur, uint id);

    [DllImport("user32.dll")]
    internal static extern IntPtr CopyIcon(IntPtr hIcon);

    [DllImport("user32.dll")]
    internal static extern bool SystemParametersInfo(uint uiAction, uint uiParam, IntPtr pvParam, uint fWinIni);

    [DllImport("user32.dll")]
    internal static extern bool DestroyIcon(IntPtr hIcon);

    // ---- 低级键盘钩子（黑屏期间拦截 Win 键等系统界面按键） ----

    internal delegate IntPtr LowLevelKeyboardProc(int nCode, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll", SetLastError = true)]
    internal static extern IntPtr SetWindowsHookEx(int idHook, LowLevelKeyboardProc lpfn, IntPtr hMod, uint dwThreadId);

    [DllImport("user32.dll", SetLastError = true)]
    internal static extern bool UnhookWindowsHookEx(IntPtr hhk);

    [DllImport("user32.dll")]
    internal static extern IntPtr CallNextHookEx(IntPtr hhk, int nCode, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll")]
    internal static extern short GetAsyncKeyState(int vKey);

    internal const int WH_KEYBOARD_LL = 13;
    internal const int VK_LWIN = 0x5B, VK_RWIN = 0x5C, VK_TAB = 0x09, VK_ESCAPE = 0x1B,
        VK_CONTROL = 0x11, VK_MENU = 0x12;
    internal const int LLKHF_ALTDOWN = 0x20;

    [StructLayout(LayoutKind.Sequential)]
    internal struct KBDLLHOOKSTRUCT
    {
        public uint vkCode, scanCode, flags, time;
        public IntPtr dwExtraInfo;
    }

    internal const uint SPI_SETCURSORS = 0x0057;

    // 需要替换成空白光标的系统光标 ID
    internal static readonly uint[] SystemCursorIds =
        { 32512, 32513, 32514, 32515, 32516, 32642, 32643, 32644, 32645, 32646, 32648, 32649, 32650 };

    [DllImport("user32.dll")]
    private static extern int ShowCursor(bool bShow);

    internal static void HideCursorGlobal()
    {
        try { int count; do { count = ShowCursor(false); } while (count >= 0); } catch { }
    }

    internal static void ShowCursorGlobal()
    {
        try { int count; do { count = ShowCursor(true); } while (count < 0); } catch { }
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct POINT { public int X, Y; }
}

// 接收 WM_HOTKEY 的消息窗口
internal sealed class MessageWindow : NativeWindow
{
    private const uint WM_HOTKEY = 0x0312;
    private readonly Action<int> _onHotKey;

    public MessageWindow(Action<int> onHotKey)
    {
        _onHotKey = onHotKey;
        CreateHandle(new CreateParams());
    }

    protected override void WndProc(ref Message m)
    {
        if (m.Msg == WM_HOTKEY)
        {
            _onHotKey(m.WParam.ToInt32());
            return;
        }
        base.WndProc(ref m);
    }
}

// ============================ 图标（程序内生成，无需 .ico 文件） ============================

internal static class IconFactory
{
    public static Icon Create()
    {
        using var bmp = new Bitmap(32, 32);
        using (var g = Graphics.FromImage(bmp))
        {
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.Clear(Color.Transparent);
            using var back = new SolidBrush(Color.FromArgb(30, 30, 30));
            g.FillEllipse(back, 1, 1, 30, 30);
            // 黑色"屏幕" + 白色边框
            using var white = new Pen(Color.White, 2.5f);
            g.DrawRectangle(white, 6, 7, 20, 14);
            using var black = new SolidBrush(Color.Black);
            g.FillRectangle(black, 8, 9, 16, 10);
            using var gray = new Pen(Color.FromArgb(200, 200, 200), 2f);
            g.DrawLine(gray, 12, 25, 20, 25);
        }
        IntPtr hIcon = bmp.GetHicon();
        var icon = Icon.FromHandle(hIcon);
        return (Icon)icon.Clone(); // Clone 以脱离位图生命周期
    }
}
