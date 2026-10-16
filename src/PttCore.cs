// Push to Talk — рабочая логика: настройки, управление микрофоном, перехват клавиши.
// Поведение перенесено из прежней версии на C без изменений.
using System.Runtime.InteropServices;
using NAudio.CoreAudioApi;

namespace PushToTalk;

/* ============================== настройки ============================== */
internal sealed class Cfg
{
    public int Key = 0x24;          // VK_HOME
    public bool Ptt = true;
    public bool Block = true;
    public int Lang;                // 0 = RU, 1 = EN

    public static string IniPath => Path.Combine(Program.DataDir, "config.ini");

    public static Cfg Load()
    {
        var c = new Cfg();
        try
        {
            if (File.Exists(IniPath))
            {
                foreach (var line in File.ReadAllLines(IniPath))
                {
                    int eq = line.IndexOf('=');
                    if (eq <= 0) continue;
                    string k = line[..eq].Trim(), v = line[(eq + 1)..].Trim();
                    switch (k)
                    {
                        case "key":
                            if (int.TryParse(v, out int key) && key is >= 1 and <= 0xFF) c.Key = key;
                            break;
                        case "ptt": if (v is "0" or "1") c.Ptt = v == "1"; break;
                        case "block": if (v is "0" or "1") c.Block = v == "1"; break;
                        case "lang": if (v is "0" or "1") c.Lang = int.Parse(v); break;
                    }
                }
            }
        }
        catch (Exception ex) { Program.Log("конфиг: ошибка чтения — " + ex.Message); }
        Program.Log($"конфиг: key={VkName(c.Key)} ptt={c.Ptt} block={c.Block} lang={c.Lang}");
        return c;
    }

    public void Save()
    {
        try
        {
            var lines = new List<string>
            {
                "[main]",
                "key=" + Key,
                "ptt=" + (Ptt ? 1 : 0),
                "block=" + (Block ? 1 : 0),
                "lang=" + Lang
            };
            File.WriteAllLines(IniPath, lines);
        }
        catch (Exception ex) { Program.Log("конфиг: не удалось сохранить — " + ex.Message); }
    }

    /* ------------------------- имена клавиш ------------------------- */
    private static readonly (int Vk, string Name)[] Table =
    {
        (0x24, "HOME"), (0x23, "END"), (0x2D, "INSERT"), (0x2E, "DELETE"),
        (0x21, "PAGE UP"), (0x22, "PAGE DOWN"), (0x20, "SPACE"), (0x09, "TAB"),
        (0x0D, "ENTER"), (0x1B, "ESC"), (0x08, "BACKSPACE"),
        (0x14, "CAPS LOCK"), (0x91, "SCROLL LOCK"), (0x90, "NUM LOCK"), (0x13, "PAUSE"),
        (0x2C, "PRINT SCREEN"),
        (0xA2, "LEFT CTRL"), (0xA3, "RIGHT CTRL"), (0x11, "CTRL"),
        (0xA4, "LEFT ALT"), (0xA5, "RIGHT ALT"), (0x12, "ALT"),
        (0xA0, "LEFT SHIFT"), (0xA1, "RIGHT SHIFT"), (0x10, "SHIFT"),
        (0x5B, "LEFT WIN"), (0x5C, "RIGHT WIN"), (0x5D, "MENU KEY"),
        (0x25, "LEFT ARROW"), (0x27, "RIGHT ARROW"), (0x26, "UP ARROW"), (0x28, "DOWN ARROW"),
        (0xBA, ";"), (0xBB, "="), (0xBC, ","), (0xBD, "-"), (0xBE, "."), (0xBF, "/"), (0xC0, "`"),
        (0xDB, "["), (0xDC, "\\"), (0xDD, "]"), (0xDE, "'"),
        (0x6B, "NUM +"), (0x6D, "NUM -"), (0x6A, "NUM *"), (0x6F, "NUM /"), (0x6E, "NUM .")
    };

    public static string VkName(int vk)
    {
        if (vk is >= 0x70 and <= 0x87) return "F" + (vk - 0x70 + 1);
        if (vk is >= 'A' and <= 'Z') return ((char)vk).ToString();
        if (vk is >= '0' and <= '9') return ((char)vk).ToString();
        if (vk is >= 0x60 and <= 0x69) return "NUM " + (vk - 0x60);
        foreach (var (v, name) in Table)
            if (v == vk) return name;
        return "VK 0x" + vk.ToString("X2");
    }
}

/* ============================ микрофон ============================ */
internal static class Mic
{
    private static MMDeviceEnumerator? _enum;
    private static MMDevice? _comm;   // устройство связи (Discord, игры)
    private static MMDevice? _def;    // устройство по умолчанию

    private static void Open()
    {
        _enum ??= new MMDeviceEnumerator();
        _comm ??= TryOpen(Role.Communications);
        _def ??= TryOpen(Role.Multimedia);
    }

    private static MMDevice? TryOpen(Role role)
    {
        try { return _enum!.GetDefaultAudioEndpoint(DataFlow.Capture, role); }
        catch { return null; }
    }

    private static void Reset()
    {
        _comm = _def = null;
    }

    /// <summary>Состояние микрофона: true — заглушён, false — активен, null — недоступен.</summary>
    public static bool? Get()
    {
        Open();
        foreach (var d in new[] { _comm, _def })
        {
            if (d == null) continue;
            try { return d.AudioEndpointVolume.Mute; }
            catch { /* устройство сменилось — попробуем другое */ }
        }
        return null;
    }

    public static bool Set(bool mute)
    {
        Open();
        int ok = Apply(mute);
        if (ok == 0)
        {
            Reset();                    // устройство переключили/отключили — берём заново
            Open();
            ok = Apply(mute);
        }
        if (ok == 0) Program.Log($"mic: не удалось переключить (mute={mute})");
        return ok > 0;
    }

    /// <summary>Имя устройства записи (для показа в окне).</summary>
    public static string DeviceName()
    {
        Open();
        foreach (var d in new[] { _comm, _def })
            if (d != null) { try { return d.FriendlyName; } catch { } }
        return "";
    }

    /// <summary>Пиковый уровень сигнала (0..1) — видно, что микрофон реально слышит.</summary>
    public static float Peak()
    {
        Open();
        foreach (var d in new[] { _comm, _def })
            if (d != null) { try { return d.AudioMeterInformation.MasterPeakValue; } catch { } }
        return 0f;
    }

    private static int Apply(bool mute)
    {
        int n = 0;
        foreach (var d in new[] { _comm, _def })
        {
            if (d == null) continue;
            try { d.AudioEndpointVolume.Mute = mute; n++; }
            catch { /* пропускаем это устройство */ }
        }
        return n;
    }
}

/* ============================ перехват клавиши ============================ */
internal sealed class PttHook : IDisposable
{
    private const int WH_KEYBOARD_LL = 13, WM_KEYDOWN = 0x0100, WM_SYSKEYDOWN = 0x0104, VK_ESCAPE = 0x1B;
    private delegate IntPtr HookProc(int nCode, IntPtr wParam, IntPtr lParam);

    [StructLayout(LayoutKind.Sequential)]
    private struct KBDLLHOOKSTRUCT { public uint vkCode, scanCode, flags, time; public IntPtr dwExtraInfo; }

    [DllImport("user32.dll", SetLastError = true)]
    private static extern IntPtr SetWindowsHookEx(int id, HookProc fn, IntPtr mod, uint thread);
    [DllImport("user32.dll")] private static extern bool UnhookWindowsHookEx(IntPtr h);
    [DllImport("user32.dll")] private static extern IntPtr CallNextHookEx(IntPtr h, int nCode, IntPtr w, IntPtr l);
    [DllImport("kernel32.dll")] private static extern IntPtr GetModuleHandle(string? name);

    private readonly HookProc _proc;          // держим ссылку: иначе сборщик мусора её уберёт
    private IntPtr _hook;
    private bool _down;

    public int KeyVk = 0x24;
    public bool Enabled = true, Block = true, Capturing;
    public Action<bool>? OnKey;               // true — нажали, false — отпустили
    public Action<int>? OnCaptured;           // новая клавиша, -1 — отмена

    public PttHook()
    {
        _proc = Callback;
        _hook = SetWindowsHookEx(WH_KEYBOARD_LL, _proc, GetModuleHandle(null), 0);
        Program.Log(_hook != IntPtr.Zero ? "хук клавиатуры: установлен" : "хук клавиатуры: НЕ УСТАНОВЛЕН");
    }

    private IntPtr Callback(int nCode, IntPtr wParam, IntPtr lParam)
    {
        if (nCode < 0) return CallNextHookEx(_hook, nCode, wParam, lParam);
        var k = Marshal.PtrToStructure<KBDLLHOOKSTRUCT>(lParam);
        bool down = wParam == WM_KEYDOWN || wParam == WM_SYSKEYDOWN;
        int vk = (int)k.vkCode;
        try
        {
            if (Capturing)
            {
                if (down)
                {
                    Capturing = false;
                    OnCaptured?.Invoke(vk == VK_ESCAPE ? -1 : vk);
                }
                return 1;                                   // во время захвата клавишу не отдаём
            }
            if (Enabled && vk == KeyVk)
            {
                if (down && !_down) { _down = true; OnKey?.Invoke(true); }
                else if (!down && _down) { _down = false; OnKey?.Invoke(false); }
                if (Block) return 1;                        // клавиша не уходит другим программам
            }
        }
        catch (Exception ex) { Program.Log("хук: ошибка — " + ex.Message); }
        return CallNextHookEx(_hook, nCode, wParam, lParam);
    }

    public void Reinstall()                             // после смены клавиши состояние сбрасываем
    {
        _down = false;
    }

    public void Dispose()
    {
        if (_hook != IntPtr.Zero) UnhookWindowsHookEx(_hook);
        _hook = IntPtr.Zero;
    }
}
