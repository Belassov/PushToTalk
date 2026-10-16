// Push to Talk — приложение: окно без рамки, трей, WebView2 с интерфейсом внутри exe.
// Логика: микрофон заглушён, пока не зажата клавиша push-to-talk (или PTT выключен).
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json.Nodes;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.WinForms;
using Microsoft.Win32;

namespace PushToTalk;

internal static class Program
{
    internal const string AppTitle = "Push to Talk";
    internal static string DataDir = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "PushToTalk");
    internal static string UiDir => Path.Combine(DataDir, "ui");
    internal static string LogPath => Path.Combine(DataDir, "log.txt");

    private static readonly object LogLock = new();

    internal static void Log(string msg)
    {
        try
        {
            lock (LogLock)
            {
                Directory.CreateDirectory(DataDir);
                File.AppendAllText(LogPath, $"[{DateTime.Now:HH:mm:ss}] {msg}{Environment.NewLine}", new UTF8Encoding(false));
            }
        }
        catch { /* логи не должны ронять приложение */ }
    }

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr FindWindow(string? cls, string title);
    [DllImport("user32.dll")] private static extern bool ShowWindow(IntPtr h, int cmd);
    [DllImport("user32.dll")] private static extern bool SetForegroundWindow(IntPtr h);

    [STAThread]
    private static void Main()
    {
        using var mutex = new Mutex(true, @"Local\PushToTalkWebSingle", out bool first);
        if (!first)
        {
            IntPtr h = FindWindow(null, AppTitle);
            if (h != IntPtr.Zero) { ShowWindow(h, 9 /*SW_RESTORE*/); SetForegroundWindow(h); }
            return;
        }

        Application.EnableVisualStyles();
        Application.SetCompatibleTextRenderingDefault(false);
        Application.SetHighDpiMode(HighDpiMode.PerMonitorV2);

        Directory.CreateDirectory(DataDir);
        File.WriteAllText(LogPath, "", new UTF8Encoding(false));
        Log($"--- запуск, каталог данных {DataDir}");

        try { Application.Run(new Host()); }
        catch (Exception ex) { Log("КРИТИЧЕСКАЯ ОШИБКА: " + ex); throw; }
    }
}

internal sealed class Host : ApplicationContext
{
    private readonly Form _form = new();
    private WebView2? _web;
    private NotifyIcon? _tray;
    private Icon? _icoLive, _icoMuted, _icoOff;
    private double _scale = 1.0;
    private bool _exiting, _balloonShown;
    private int _micMuted = -1;              // -1 неизвестно, 0 активен, 1 заглушён
    private bool _keyDown;

    private readonly Cfg _cfg = Cfg.Load();
    private PttHook? _hook;
    private System.Windows.Forms.Timer? _poll;
    private int _ticks;

    [DllImport("user32.dll")] private static extern bool ReleaseCapture();
    [DllImport("user32.dll")] private static extern IntPtr SendMessage(IntPtr h, int msg, IntPtr wp, IntPtr lp);
    [DllImport("dwmapi.dll")] private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int value, int size);
    [DllImport("gdi32.dll")] private static extern IntPtr CreateRoundRectRgn(int l, int t, int r, int b, int w, int h);
    [DllImport("user32.dll")] private static extern int SetWindowRgn(IntPtr hwnd, IntPtr hrgn, bool redraw);
    [DllImport("user32.dll")] private static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(IntPtr h, out uint pid);

    private const int WM_NCLBUTTONDOWN = 0x00A1, HTCAPTION = 2;
    private const int DWMWA_WINDOW_CORNER_PREFERENCE = 33, DWMWCP_ROUND = 2;

    /* ------------------------------ тексты ------------------------------ */
    private static string T(int lang, string key)
    {
        var ru = lang == 0;
        return key switch
        {
            "show" => ru ? "Открыть настройки" : "Open settings",
            "ptt" => ru ? "Push-to-talk вкл/выкл" : "Toggle push-to-talk",
            "exit" => ru ? "Выход" : "Exit",
            "live" => ru ? "PTT: микрофон активен" : "PTT: microphone live",
            "muted" => ru ? "PTT: микрофон заглушён" : "PTT: microphone muted",
            "off" => ru ? "PTT выключен: микрофон всегда активен" : "PTT off: microphone always live",
            "nomic" => ru ? "Микрофон недоступен" : "Microphone unavailable",
            "balloonT" => ru ? "Push-to-Talk работает в трее" : "Push-to-Talk is running in the tray",
            "balloonX" => ru ? "Окно скрыто в трей. Полный выход — значок в трее → Выход."
                             : "Window hidden to tray. To quit: tray icon → Exit.",
            "stCapture" => ru ? "Нажми нужную клавишу (Esc — отмена)" : "Press the key you want (Esc — cancel)",
            "stCaptured" => ru ? "Клавиша изменена: " : "Key changed: ",
            "stCancelled" => ru ? "Смена клавиши отменена" : "Key change cancelled",
            "stPttOn" => ru ? "Push-to-talk включён" : "Push-to-talk enabled",
            "stPttOff" => ru ? "Push-to-talk выключен: микрофон всегда активен" : "Push-to-talk disabled: microphone always live",
            "stBlockOn" => ru ? "Клавиша блокируется в других программах" : "The key is blocked in other apps",
            "stBlockOff" => ru ? "Клавиша больше не блокируется" : "The key is no longer blocked",
            "stAutoOn" => ru ? "Автозапуск включён" : "Start with Windows enabled",
            "stAutoOff" => ru ? "Автозапуск выключен" : "Start with Windows disabled",
            "stMicFail" => ru ? "Не удалось изменить состояние микрофона" : "Could not change the microphone state",
            _ => key
        };
    }

    internal Host()
    {
        var work = Screen.PrimaryScreen?.WorkingArea ?? new Rectangle(0, 0, 1920, 1080);
        // окно компактное, 16 к 9: программа в основном работает в трее
        _scale = Math.Clamp(work.Height / 1200.0, 0.9, 1.6);
        int w = (int)(640 * _scale), h = (int)(360 * _scale);

        _form.FormBorderStyle = FormBorderStyle.None;
        _form.StartPosition = FormStartPosition.Manual;
        _form.BackColor = Color.FromArgb(0x1e, 0x1e, 0x2e);
        _form.Text = Program.AppTitle;
        _form.ShowInTaskbar = true;
        _form.MaximizeBox = false;
        _form.MinimumSize = new Size((int)(480 * _scale), (int)(270 * _scale));
        _form.ClientSize = new Size(w, h);
        _form.Location = new Point(work.Left + (work.Width - w) / 2, work.Top + Math.Max(8, (work.Height - h) / 2));
        _form.FormClosing += OnClosing;
        _form.Shown += async (_, _) => await InitWebAsync();
        Program.Log($"окно {w}x{h}, масштаб {_scale:0.00}");

        SetupTray();
        SetupHook();

        _poll = new System.Windows.Forms.Timer { Interval = 100 };
        _poll.Tick += (_, _) =>
        {
            if (++_ticks % 10 == 0) PollMic();          // раз в секунду — состояние микрофона
            if (_form.Visible) SendLevel();             // 10 раз в секунду — уровень сигнала
        };
        _poll.Start();

        ApplyMic();                       // при запуске микрофон заглушён, пока не зажата клавиша
        _form.Show();
    }

    /* ------------------------------ трей ------------------------------ */
    private void SetupTray()
    {
        var menu = new ContextMenuStrip();
        menu.Items.Add(T(_cfg.Lang, "show"), null, (_, _) => ShowWindowUi());
        menu.Items.Add(T(_cfg.Lang, "ptt"), null, (_, _) => { SetPtt(!_cfg.Ptt); });
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add(T(_cfg.Lang, "exit"), null, (_, _) => ExitApp());

        _tray = new NotifyIcon { Visible = true, ContextMenuStrip = menu };
        _icoLive = LoadIcon("app/mic_live.ico");
        _icoMuted = LoadIcon("app/mic_muted.ico");
        _icoOff = LoadIcon("app/mic_off.ico");
        _tray.Icon = _icoOff ?? SystemIcons.Application;
        _tray.MouseClick += (_, e) => { if (e.Button == MouseButtons.Left) ShowWindowUi(); };   // один клик
        _tray.DoubleClick += (_, _) => ShowWindowUi();
        Program.Log("трей: добавлен");
        RefreshTray();
    }

    private static Icon? LoadIcon(string res)
    {
        try
        {
            using var s = typeof(Host).Assembly.GetManifestResourceStream(res);
            return s != null ? new Icon(s) : null;
        }
        catch (Exception ex) { Program.Log($"трей: иконка {res} не загрузилась — {ex.Message}"); return null; }
    }

    private void RefreshTray()
    {
        if (_tray == null) return;
        var icon = _micMuted < 0 ? _icoOff : (_micMuted == 1 ? _icoMuted : _icoLive);
        if (icon != null) _tray.Icon = icon;
        _tray.Text = _micMuted < 0 ? T(_cfg.Lang, "nomic")
            : !_cfg.Ptt ? T(_cfg.Lang, "off")
            : _micMuted == 1 ? T(_cfg.Lang, "muted") : T(_cfg.Lang, "live");
    }

    private void ShowWindowUi()
    {
        _form.Show();
        if (_form.WindowState == FormWindowState.Minimized) _form.WindowState = FormWindowState.Normal;
        _form.BringToFront();
        _form.Activate();
        Program.Log("окно показано");
    }

    private void HideWindowUi()
    {
        _form.Hide();
        Program.Log("окно скрыто в трей");
        if (!_balloonShown && _tray != null)
        {
            _tray.BalloonTipTitle = T(_cfg.Lang, "balloonT");
            _tray.BalloonTipText = T(_cfg.Lang, "balloonX");
            _tray.ShowBalloonTip(4000);
            _balloonShown = true;
        }
    }

    private void ExitApp()
    {
        Program.Log("--- выход");
        _exiting = true;
        _poll?.Stop();
        _hook?.Dispose();
        if (_tray != null) _tray.Visible = false;
        _web?.Dispose();
        _form.Close();
        ExitThread();
    }

    private void OnClosing(object? sender, FormClosingEventArgs e)
    {
        if (_exiting) return;
        if (e.CloseReason is CloseReason.UserClosing or CloseReason.ApplicationExitCall)
        {
            e.Cancel = true;
            HideWindowUi();
        }
    }

    /* --------------------------- хук и микрофон --------------------------- */
    private void SetupHook()
    {
        _hook = new PttHook
        {
            KeyVk = _cfg.Key,
            Enabled = _cfg.Ptt,
            Block = _cfg.Block,
            OnKey = (down) => Post(() =>
            {
                _keyDown = down;
                Program.Log(down ? "ptt: клавиша нажата -> микрофон активен" : "ptt: клавиша отпущена -> заглушён");
                ApplyMic();
            }),
            OnCaptured = (vk) => Post(() =>
            {
                if (vk < 0)
                {
                    SetStatus(T(_cfg.Lang, "stCancelled"), "warn");
                }
                else
                {
                    _cfg.Key = vk;
                    _cfg.Save();
                    _hook!.KeyVk = vk;
                    _hook.Reinstall();
                    Program.Log($"смена клавиши: {Cfg.VkName(vk)}");
                    SetStatus(T(_cfg.Lang, "stCaptured") + Cfg.VkName(vk));
                }
                SendState();
            })
        };
    }

    private void Post(Action a)
    {
        try { _form.BeginInvoke(a); } catch { /* окно закрыто */ }
    }

    /// <summary>Целевое состояние: заглушён, пока не зажата клавиша (или PTT выключен — всегда активен).</summary>
    private void ApplyMic()
    {
        bool want = _cfg.Ptt && !_keyDown;
        var cur = Mic.Get();
        if (cur == null) { _micMuted = -1; SendState(); RefreshTray(); return; }
        if (cur == want) { _micMuted = cur.Value ? 1 : 0; SendState(); RefreshTray(); return; }
        if (Mic.Set(want)) _micMuted = want ? 1 : 0;
        else { _micMuted = -1; SetStatus(T(_cfg.Lang, "stMicFail"), "err"); }
        SendState();
        RefreshTray();
    }

    private void PollMic()                       // ловим изменения, сделанные другими программами
    {
        var cur = Mic.Get();
        int nv = cur == null ? -1 : (cur.Value ? 1 : 0);
        if (nv != _micMuted) { _micMuted = nv; SendState(); RefreshTray(); }
    }

    /* --------------------------- интерфейс --------------------------- */
    private static void ExtractUi()
    {
        var asm = typeof(Host).Assembly;
        Directory.CreateDirectory(Program.UiDir);
        int n = 0;
        foreach (var name in asm.GetManifestResourceNames())
        {
            if (!name.StartsWith("ui/", StringComparison.Ordinal)) continue;
            string rel = name[3..].Replace('/', Path.DirectorySeparatorChar);
            string dst = Path.Combine(Program.UiDir, rel);
            Directory.CreateDirectory(Path.GetDirectoryName(dst)!);
            using var src = asm.GetManifestResourceStream(name)!;
            using var outFs = File.Create(dst);
            src.CopyTo(outFs);
            n++;
        }
        Program.Log($"интерфейс распакован: {n} файлов");
    }

    private async Task InitWebAsync()
    {
        try
        {
            ExtractUi();
            var env = await CoreWebView2Environment.CreateAsync(null, Path.Combine(Program.DataDir, "webview"));
            _web = new WebView2 { Dock = DockStyle.Fill, DefaultBackgroundColor = Color.FromArgb(0x1e, 0x1e, 0x2e) };
            _form.Controls.Add(_web);
            await _web.EnsureCoreWebView2Async(env);

            var core = _web.CoreWebView2;
            core.Settings.AreDefaultContextMenusEnabled = false;
            core.Settings.IsStatusBarEnabled = false;
            core.Settings.AreDevToolsEnabled = false;
            core.Settings.IsZoomControlEnabled = false;
            core.Settings.IsPinchZoomEnabled = false;
            core.Settings.AreBrowserAcceleratorKeysEnabled = false;
            core.WebMessageReceived += OnWebMessage;
            core.SetVirtualHostNameToFolderMapping("pushtotalk.local", Program.UiDir,
                CoreWebView2HostResourceAccessKind.Allow);
            _web.ZoomFactor = _scale;
            core.Navigate("https://pushtotalk.local/index.html");
            Program.Log("WebView2 готов, интерфейс загружен");
        }
        catch (Exception ex)
        {
            Program.Log("ОШИБКА WebView2: " + ex);
            MessageBox.Show(
                "Не удалось запустить компонент WebView2 (движок Edge).\n\n" +
                "Обычно он уже есть в Windows 10/11 вместе с Edge. Если нет — установите " +
                "\"WebView2 Runtime\" с сайта Microsoft и запустите приложение снова.\n\n" + ex.Message,
                Program.AppTitle, MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
    }

    private void ApplyRoundedCorners()
    {
        int pref = DWMWCP_ROUND;
        if (DwmSetWindowAttribute(_form.Handle, DWMWA_WINDOW_CORNER_PREFERENCE, ref pref, sizeof(int)) != 0)
        {
            int r = (int)(14 * _scale);
            SetWindowRgn(_form.Handle, CreateRoundRectRgn(0, 0, _form.Width + 1, _form.Height + 1, r, r), true);
            Program.Log("скругление углов: через область окна (Win10)");
        }
    }

    /* ---------------------------- мост ---------------------------- */
    private void Send(JsonObject msg) => _web?.CoreWebView2?.PostWebMessageAsJson(msg.ToJsonString());

    private void SendState()
    {
        var st = new JsonObject
        {
            ["key"] = Cfg.VkName(_cfg.Key),
            ["keyVk"] = _cfg.Key,
            ["ptt"] = _cfg.Ptt,
            ["block"] = _cfg.Block,
            ["autostart"] = AutostartOn(),
            ["lang"] = _cfg.Lang,
            ["capturing"] = _hook?.Capturing ?? false,
            ["micState"] = _micMuted < 0 ? "unavailable" : (_cfg.Ptt ? (_micMuted == 1 ? "muted" : "live") : "off"),
            ["device"] = Mic.DeviceName()
        };
        Send(new JsonObject { ["t"] = "state", ["state"] = st });
    }

    private void SendLevel()
    {
        float v = _cfg.Ptt ? Mic.Peak() : 0f;
        Send(new JsonObject { ["t"] = "level", ["v"] = Math.Round(v, 3) });
    }

    private void SetStatus(string text, string kind = "ok") =>
        Send(new JsonObject { ["t"] = "status", ["text"] = text, ["kind"] = kind });

    private async void OnWebMessage(object? sender, CoreWebView2WebMessageReceivedEventArgs e)
    {
        string raw = e.TryGetWebMessageAsString();
        JsonNode? node;
        try { node = JsonNode.Parse(raw); } catch { Program.Log("мост: не разобрал " + raw); return; }
        string cmd = node?["cmd"]?.GetValue<string>() ?? "";
        Program.Log("мост: " + raw);

        switch (cmd)
        {
            case "ready":
                ApplyRoundedCorners();
                SendState();
                break;
            case "win":
                switch (node?["act"]?.GetValue<string>())
                {
                    case "min" or "close": HideWindowUi(); return;
                    case "max":
                        _form.WindowState = _form.WindowState == FormWindowState.Maximized
                            ? FormWindowState.Normal : FormWindowState.Maximized;
                        return;
                }
                break;
            case "drag":
                ReleaseCapture();
                SendMessage(_form.Handle, WM_NCLBUTTONDOWN, HTCAPTION, 0);
                break;
            case "hide": HideWindowUi(); break;
            case "capture":
                if (_hook != null)
                {
                    _hook.Capturing = true;
                    SendState();
                    SetStatus(T(_cfg.Lang, "stCapture"), "warn");
                    Program.Log("захват клавиши: жду нажатия");
                }
                break;
            case "captureCancel":
                if (_hook != null) { _hook.Capturing = false; SendState(); }
                break;
            case "setPtt":
                SetPtt(node?["value"]?.GetValue<bool>() ?? !_cfg.Ptt);
                break;
            case "setBlock":
                _cfg.Block = node?["value"]?.GetValue<bool>() ?? !_cfg.Block;
                if (_hook != null) _hook.Block = _cfg.Block;
                _cfg.Save();
                SetStatus(T(_cfg.Lang, _cfg.Block ? "stBlockOn" : "stBlockOff"));
                SendState();
                break;
            case "autostart":
                SetAutostart(!AutostartOn());
                SetStatus(T(_cfg.Lang, AutostartOn() ? "stAutoOn" : "stAutoOff"));
                SendState();
                break;
            case "setLang":
                _cfg.Lang = node?["value"]?.GetValue<int>() ?? (_cfg.Lang == 0 ? 1 : 0);
                _cfg.Save();
                RebuildTrayMenu();
                RefreshTray();
                SendState();
                break;
            case "exit": ExitApp(); break;
            default: Program.Log("мост: неизвестная команда " + cmd); break;
        }
        await Task.CompletedTask;
    }

    private void SetPtt(bool on)
    {
        _cfg.Ptt = on;
        _cfg.Save();
        if (_hook != null) _hook.Enabled = on;
        if (!on) _keyDown = false;
        Program.Log("PTT " + (on ? "включён" : "выключен"));
        SetStatus(T(_cfg.Lang, on ? "stPttOn" : "stPttOff"));
        ApplyMic();
        SendState();
    }

    private void RebuildTrayMenu()
    {
        if (_tray?.ContextMenuStrip is not ContextMenuStrip menu) return;
        menu.Items[0].Text = T(_cfg.Lang, "show");
        menu.Items[1].Text = T(_cfg.Lang, "ptt");
        menu.Items[3].Text = T(_cfg.Lang, "exit");
    }

    /* -------------------------- автозапуск -------------------------- */
    private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string RunValue = "PushToTalk";

    private static bool AutostartOn()
    {
        using var k = Registry.CurrentUser.OpenSubKey(RunKey);
        return k?.GetValue(RunValue) != null;
    }

    private static void SetAutostart(bool on)
    {
        using var k = Registry.CurrentUser.OpenSubKey(RunKey, true);
        if (k == null) { Program.Log("автозапуск: ключ реестра недоступен"); return; }
        if (on)
        {
            string exe = Environment.ProcessPath ?? Application.ExecutablePath;
            k.SetValue(RunValue, "\"" + exe + "\"");
            Program.Log("автозапуск включён: " + exe);
        }
        else
        {
            k.DeleteValue(RunValue, false);
            Program.Log("автозапуск выключен");
        }
    }
}
