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

    /// <summary>Сохраняет лог прошлого запуска (log.prev.txt), не давая ему расти бесконечно.</summary>
    private static void KeepPrevLog()
    {
        try
        {
            if (!File.Exists(LogPath)) return;
            if (new FileInfo(LogPath).Length > 1024 * 1024) { File.Delete(LogPath); return; }
            File.Copy(LogPath, Path.Combine(DataDir, "log.prev.txt"), true);
        }
        catch { /* логи не должны ронять приложение */ }
    }

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr FindWindow(string? cls, string title);
    [DllImport("user32.dll")] private static extern bool ShowWindow(IntPtr h, int cmd);
    [DllImport("user32.dll")] private static extern bool SetForegroundWindow(IntPtr h);

    [STAThread]
    private static void Main(string[] args)
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
        KeepPrevLog();                     // прошлый запуск не стираем — иначе «иногда не работает» не воспроизвести
        Log($"--- запуск, каталог данных {DataDir}");

        // --tray: запуск вместе с Windows — окно не показываем, только трей
        try { Application.Run(new Host(args.Contains("--tray"))); }
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
    private bool _toggled;                 // в режиме переключения: микрофон включён нажатием
    private bool? _micMuteAtStart;           // состояние микрофона до запуска — вернём при выходе
    private System.Windows.Forms.Timer? _captureTimer;

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
    [DllImport("user32.dll")] private static extern bool SetForegroundWindow(IntPtr hWnd);
    [DllImport("user32.dll")] private static extern bool BringWindowToTop(IntPtr hWnd);
    [DllImport("user32.dll")] private static extern bool AttachThreadInput(uint idAttach, uint idAttachTo, bool fAttach);
    [DllImport("kernel32.dll")] private static extern uint GetCurrentThreadId();
    [DllImport("user32.dll")] private static extern short GetAsyncKeyState(int vKey);

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
            "stToggleOn" => ru ? "Микрофон переключается одним нажатием" : "The microphone switches on a single press",
            "stToggleOff" => ru ? "Микрофон по удерживанию клавиши" : "Hold the key to talk",
            "stBlockOn" => ru ? "Клавиша блокируется в других программах" : "The key is blocked in other apps",
            "stBlockOff" => ru ? "Клавиша больше не блокируется" : "The key is no longer blocked",
            "stAutoOn" => ru ? "Автозапуск включён" : "Start with Windows enabled",
            "stAutoOff" => ru ? "Автозапуск выключен" : "Start with Windows disabled",
            "stMicFail" => ru ? "Не удалось изменить состояние микрофона" : "Could not change the microphone state",
            _ => key
        };
    }

    private readonly bool _startHidden;   // запуск с Windows: окно не показываем, живём в трее

    internal Host(bool startHidden = false)
    {
        _startHidden = startHidden;
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
        _form.ClientSize = new Size(w, h);
        _form.Location = new Point(work.Left + (work.Width - w) / 2, work.Top + Math.Max(8, (work.Height - h) / 2));
        _form.FormClosing += OnClosing;
        _form.Deactivate += (_, _) => CancelCapture("клик вне окна");
        _form.Shown += async (_, _) => await InitWebAsync();
        Program.Log($"окно {w}x{h}, масштаб {_scale:0.00}");

        SetupTray();
        SetupHook();

        _poll = new System.Windows.Forms.Timer { Interval = 100 };
        _poll.Tick += (_, _) =>
        {
            if (++_ticks % 10 == 0)
            {
                PollMic();                              // раз в секунду — состояние микрофона
                // клавишу могли отпустить на защищённом экране (UAC) — хук этого не видит
                // (в режиме переключения отпускание клавиши ничего не значит)
                if (!_cfg.Toggle && _keyDown && (GetAsyncKeyState(_cfg.Key) & 0x8000) == 0)
                {
                    Program.Log("хук: клавиша отпущена вне обычного потока — возвращаю микрофон");
                    _keyDown = false;
                    ApplyMic();
                }
            }
            if (_form.Visible) SendLevel();             // 10 раз в секунду — уровень сигнала
        };
        _poll.Start();

        _micMuteAtStart = Mic.Get();       // запоминаем, чтобы вернуть микрофон при выходе
        ApplyMic();                       // при запуске микрофон заглушён, пока не зажата клавиша
        if (_startHidden)
        {
            // Запуск с Windows: окно не появляется — но интерфейс сразу готов,
            // чтобы при открытии настроек из трея всё было на месте.
            _ = InitWebAsync();
            Program.Log("запуск скрытый (вместе с Windows) — окно только по вызову");
        }
        else _form.Show();
        AppDomain.CurrentDomain.ProcessExit += (_, _) => RestoreMicOnExit();
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
        ForceForeground(_form);
        Program.Log("окно показано");
    }

    /// <summary>Выводит окно на передний план, даже когда фокус у чужой программы:
    /// Windows сама фокус не отдаёт, поэтому на время вызова цепляемся к её потоку ввода.</summary>
    private static void ForceForeground(Form f)
    {
        IntPtr h = f.Handle;
        if (h == IntPtr.Zero) return;
        IntPtr fore = GetForegroundWindow();
        uint foreThread = GetWindowThreadProcessId(fore, out _);
        uint thisThread = GetCurrentThreadId();
        if (fore != h && foreThread != 0 && foreThread != thisThread)
        {
            AttachThreadInput(foreThread, thisThread, true);
            BringWindowToTop(h);
            SetForegroundWindow(h);
            f.Activate();
            AttachThreadInput(foreThread, thisThread, false);
        }
        else
        {
            BringWindowToTop(h);
            SetForegroundWindow(h);
            f.Activate();
        }
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
        RestoreMicOnExit();
        _web?.Dispose();
        _form.Close();
        ExitThread();
    }

    /// <summary>Возвращает микрофон в то состояние, в котором он был до запуска программы,
    /// иначе после выхода он остаётся заглушённым — и непонятно почему.</summary>
    private void RestoreMicOnExit()
    {
        if (!_micMuteAtStart.HasValue) return;
        try
        {
            Mic.Set(_micMuteAtStart.Value);
            Program.Log($"микрофон возвращён: {(_micMuteAtStart.Value ? "заглушён" : "активен")}");
        }
        catch (Exception ex) { Program.Log("возврат микрофона не удался — " + ex.Message); }
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

    /// <summary>Отменяет ожидание новой клавиши (по таймауту, по клику вне окна или по кнопке «Отмена»).</summary>
    private void CancelCapture(string why)
    {
        _captureTimer?.Stop();
        if (_hook == null || !_hook.Capturing) return;
        _hook.Capturing = false;
        SendState();
        SetStatus(T(_cfg.Lang, "stCancelled"), "warn");
        Program.Log("захват клавиши отменён: " + why);
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
                // режим переключения: одно нажатие включает микрофон, следующее — выключает
                // (хук и так срабатывает один раз на нажатие, автоповтор не долбит)
                if (down && _cfg.Toggle) _toggled = !_toggled;
                _keyDown = down;
                if (_cfg.Toggle)
                    Program.Log(down ? $"ptt: нажатие -> микрофон {(_toggled ? "активен" : "заглушён")}"
                                     : "ptt: клавиша отпущена (режим переключения — без изменений)");
                else
                    Program.Log(down ? "ptt: клавиша нажата -> микрофон активен" : "ptt: клавиша отпущена -> заглушён");
                ApplyMic();
            }),
            OnEsc = () =>
            {
                // Esc — окно в трей, но только когда окно наших программ в фокусе
                var h = GetForegroundWindow();
                GetWindowThreadProcessId(h, out uint pid);
                if (pid != (uint)Environment.ProcessId) return false;
                Post(HideWindowUi);
                return true;
            },
            OnCaptured = (vk) => Post(() =>
            {
                _captureTimer?.Stop();
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
        // в режиме переключения микрофон держит состояние до следующего нажатия
        bool want = _cfg.Ptt && (_cfg.Toggle ? _toggled : !_keyDown);
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

    private bool _webStarted;

    private async Task InitWebAsync()
    {
        if (_webStarted) return;              // второй раз (например, из Shown) не нужен
        _webStarted = true;
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
            ["toggle"] = _cfg.Toggle,
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
        Program.Log($"мост: {cmd} ({raw.Length} байт)");   // сам текст в лог не пишем

        switch (cmd)
        {
            case "ready":
                ApplyRoundedCorners();
                SendState();
                break;
            case "win":
                if (node?["act"]?.GetValue<string>() is "min" or "close") { HideWindowUi(); return; }
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
                    // страховка: если пользователь кликнул мимо, хук не должен вечно съедать весь ввод
                    _captureTimer?.Stop();
                    _captureTimer = new System.Windows.Forms.Timer { Interval = 8000 };
                    _captureTimer.Tick += (_, _) => CancelCapture("таймаут");
                    _captureTimer.Start();
                }
                break;
            case "captureCancel": CancelCapture("пользователь"); break;
            case "setPtt":
                SetPtt(node?["value"]?.GetValue<bool>() ?? !_cfg.Ptt);
                break;
            case "setToggle":
                _cfg.Toggle = node?["value"]?.GetValue<bool>() ?? !_cfg.Toggle;
                _cfg.Save();
                _toggled = false;                 // каждый раз начинаем с заглушённого микрофона
                SetStatus(T(_cfg.Lang, _cfg.Toggle ? "stToggleOn" : "stToggleOff"));
                ApplyMic();
                SendState();
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
        if (on) _toggled = false;             // включаем PTT — микрофон снова заглушён
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

    /// <summary>Путь к exe из записи автозапуска (в значении реестра: "путь" --tray).</summary>
    private static string ExeFromRunValue(string stored)
    {
        string s = stored.Trim();
        int args = s.IndexOf(" --", StringComparison.Ordinal);
        if (args > 0) s = s[..args];
        return s.Trim().Trim('"');
    }

    private static bool AutostartOn()
    {
        using var k = Registry.CurrentUser.OpenSubKey(RunKey);
        string? stored = k?.GetValue(RunValue) as string;
        if (stored == null) return false;
        // exe могли перетащить в другую папку — в реестре остался старый путь, и автозапуск молча не работает;
        // старая запись без «--tray» показывала бы окно при входе в Windows — тоже перезаписываем
        string cur = Environment.ProcessPath ?? "";
        if (!string.Equals(ExeFromRunValue(stored), cur, StringComparison.OrdinalIgnoreCase)
            || !stored.Contains("--tray"))
        {
            Program.Log($"автозапуск: запись устарела ({stored}) — перезаписываю на «{cur} --tray»");
            SetAutostart(true);
        }
        return true;
    }

    private static void SetAutostart(bool on)
    {
        using var k = Registry.CurrentUser.OpenSubKey(RunKey, true);
        if (k == null) { Program.Log("автозапуск: ключ реестра недоступен"); return; }
        if (on)
        {
            string exe = Environment.ProcessPath ?? Application.ExecutablePath;
            // --tray — при входе в Windows окно не показываем (см. Main)
            k.SetValue(RunValue, "\"" + exe + "\" --tray");
            Program.Log("автозапуск включён: " + exe + " --tray");
        }
        else
        {
            k.DeleteValue(RunValue, false);
            Program.Log("автозапуск выключен");
        }
    }
}
