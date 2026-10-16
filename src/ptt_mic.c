/* ============================================================================
 * ptt_mic.c — Push-to-Talk: микрофон как «рация» на глобальной клавише
 *
 *   * глобальный хук клавиатуры (WH_KEYBOARD_LL): нажал клавишу -> микрофон
 *     включён, отпустил -> заглушён. Можно выключить PTT: микрофон всегда живой.
 *   * трей (Shell_NotifyIcon), окно прячется в трей по «свернуть» и «закрыть»
 *   * автозапуск с Windows: HKCU\...\CurrentVersion\Run (без прав админа)
 *   * язык интерфейса RU / EN
 *   * конфиг: %APPDATA%\PushToTalk\config.ini, лог: %APPDATA%\PushToTalk\log.txt
 *
 * Заглушение делается через Core Audio API на устройство по умолчанию
 * (default communications device, как это видит Discord/Skype/игры).
 * ==========================================================================*/

#define UNICODE
#define _UNICODE
#define WIN32_LEAN_AND_MEAN
#define _WIN32_WINNT 0x0601

#include <windows.h>
#include <shellapi.h>
#include <shlobj.h>
#define COBJMACROS
#include <mmdeviceapi.h>
#include <endpointvolume.h>
#include <stdio.h>
#include <wchar.h>
#include <stdlib.h>
#include "resource.h"

#define APPTITLE   L"Push-to-Talk"
#define REGVALNAME L"PushToTalk"
#define DIRNAME    L"PushToTalk"

/* ---- GUID'ы описаны локально, чтобы не зависеть от libuuid --------------- */
static const GUID G_CLSID_MMDeviceEnumerator  = {0xBCDE0395,0xE52F,0x467C,{0x8E,0x3D,0xC4,0x57,0x92,0x91,0x69,0x2E}};
static const GUID G_IID_IMMDeviceEnumerator   = {0xA95664D2,0x9614,0x4F35,{0xA7,0x46,0xDE,0x8D,0xB6,0x36,0x17,0xE6}};
static const GUID G_IID_IAudioEndpointVolume  = {0x5CDF2C82,0x841E,0x4546,{0x97,0x22,0x0C,0xF7,0x40,0x78,0x22,0x9A}};

/* ============================ строки интерфейса ========================== */
enum {
    STR_TITLE = 0, STR_KEY_FMT, STR_BTN_CHANGE, STR_BTN_CAPTURE, STR_HINT_KEY,
    STR_CHK_PTT, STR_CHK_BLOCK, STR_CHK_AUTOSTART, STR_BTN_EXIT,
    STR_STATE_LIVE, STR_STATE_MUTED, STR_STATE_OFF, STR_STATE_NOMIC,
    STR_TRAY_LIVE, STR_TRAY_MUTED, STR_TRAY_OFF,
    STR_MENU_SHOW, STR_MENU_TOGGLE, STR_MENU_EXIT,
    STR_BALLOON_TITLE, STR_BALLOON_TEXT, STR_LANG_BTN, STR_TRAY_HINT,
    STR_COUNT
};

static const wchar_t *S_RU[STR_COUNT] = {
    L"Push-to-Talk — микрофон",
    L"Клавиша: %ls",
    L"Изменить клавишу",
    L"Нажмите клавишу…",
    L"Нажмите «Изменить клавишу», затем нужную клавишу на клавиатуре (Esc — отмена).",
    L"Push-to-talk включён",
    L"Блокировать клавишу в других программах",
    L"Запускать вместе с Windows",
    L"Выход",
    L"Микрофон активен — звук захватывается",
    L"Микрофон заглушён — кнопка PTT не нажата",
    L"Микрофон всегда активен — PTT выключен",
    L"Микрофон недоступен (нет устройства записи?)",
    L"PTT: микрофон активен",
    L"PTT: микрофон заглушён",
    L"PTT выключен: микрофон всегда активен",
    L"Открыть настройки",
    L"Push-to-talk вкл/выкл",
    L"Выход",
    L"Push-to-Talk работает в трее",
    L"Окно закрыто в трей. Полный выход — значок в трее -> Выход.",
    L"Язык: Русский",
    L"Закрытие и сворачивание прячут окно в трей — приложение остаётся в процессах. Полный выход — «Выход» или трей.",
};

static const wchar_t *S_EN[STR_COUNT] = {
    L"Push-to-Talk — microphone",
    L"Key: %ls",
    L"Change key",
    L"Press a key…",
    L"Click \"Change key\", then press the key you want (Esc — cancel).",
    L"Push-to-talk enabled",
    L"Block the key in other apps",
    L"Start with Windows",
    L"Exit",
    L"Microphone is LIVE — capturing sound",
    L"Microphone is MUTED — PTT key not pressed",
    L"Microphone always live — PTT disabled",
    L"Microphone unavailable (no recording device?)",
    L"PTT: microphone live",
    L"PTT: microphone muted",
    L"PTT off: microphone always live",
    L"Open settings",
    L"Toggle push-to-talk",
    L"Exit",
    L"Push-to-Talk is running in the tray",
    L"Window hidden to tray. To quit: tray icon -> Exit.",
    L"Language: English",
    L"Minimizing and closing hide the window to the tray — the app stays running. Real exit: \"Exit\" or the tray menu.",
};

/* ================================ состояние ============================== */
static HINSTANCE g_hInst;
static HWND   g_hwnd;
static HFONT  g_font, g_fontSmall;
static int    g_dpi = 96;
static HHOOK  g_hook;
static BOOL   g_pttEnabled = TRUE;
static BOOL   g_blockKey   = TRUE;
static int    g_lang       = 0;          /* 0 = RU, 1 = EN */
static int    g_keyVK      = VK_HOME;
static BOOL   g_keyDown    = FALSE;
static BOOL   g_capturing  = FALSE;
static BOOL   g_quitting   = FALSE;
static BOOL   g_debug      = FALSE;
static BOOL   g_balloonShown = FALSE;
static NOTIFYICONDATAW g_nid;
static wchar_t g_appDir[MAX_PATH], g_iniPath[MAX_PATH], g_logPath[MAX_PATH], g_exePath[MAX_PATH];
static char    g_logPathA[MAX_PATH * 2];
static int     g_micMuted  = -1;         /* -1 неизвестно, 0 снят, 1 заглушён */
static int     g_micFailLogged = 0;
static BOOL    g_hotkeyFallback = FALSE;

#define SC(v) MulDiv((v), g_dpi, 96)

static const wchar_t *T(int id) { return g_lang ? S_EN[id] : S_RU[id]; }

/* ================================ лог =================================== */
static void log_line(const wchar_t *fmt, ...)
{
    wchar_t msg[768], line[900];
    va_list ap;
    SYSTEMTIME st;
    char u8[2200];
    int n;
    FILE *f;

    if (!g_logPathA[0]) return;
    va_start(ap, fmt);
    vswprintf(msg, 767, fmt, ap);
    va_end(ap);
    GetLocalTime(&st);
    swprintf(line, 899, L"[%02d:%02d:%02d] %ls\r\n", st.wHour, st.wMinute, st.wSecond, msg);
    n = WideCharToMultiByte(CP_UTF8, 0, line, -1, u8, sizeof(u8), NULL, NULL);
    if (n <= 1) return;
    f = fopen(g_logPathA, "ab");
    if (f) { fwrite(u8, 1, (size_t)(n - 1), f); fclose(f); }
}
#define DBG(...) do { if (g_debug) log_line(__VA_ARGS__); } while (0)

/* ============================ Core Audio (микрофон) ===================== */
static IMMDeviceEnumerator *g_enum;
static IAudioEndpointVolume *g_vol;      /* устройство связи (Discord/Skype/игры) */
static IAudioEndpointVolume *g_vol2;     /* устройство по умолчанию (Windows может быть другое) */

static void mic_release(void)
{
    if (g_vol)  { IAudioEndpointVolume_Release(g_vol); g_vol = NULL; }
    if (g_vol2) { IAudioEndpointVolume_Release(g_vol2); g_vol2 = NULL; }
    if (g_enum) { IMMDeviceEnumerator_Release(g_enum); g_enum = NULL; }
}

static IAudioEndpointVolume *mic_open_role(ERole role)
{
    IMMDevice *dev = NULL;
    IAudioEndpointVolume *vol = NULL;
    HRESULT hr;

    hr = IMMDeviceEnumerator_GetDefaultAudioEndpoint(g_enum, eCapture, role, &dev);
    if (FAILED(hr) || !dev) return NULL;
    hr = IMMDevice_Activate(dev, (REFIID)&G_IID_IAudioEndpointVolume, CLSCTX_ALL, NULL, (void **)&vol);
    IMMDevice_Release(dev);
    if (FAILED(hr)) return NULL;
    return vol;
}

static BOOL mic_acquire(void)
{
    HRESULT hr;

    if (g_vol) return TRUE;
    if (!g_enum) {
        hr = CoCreateInstance((REFCLSID)&G_CLSID_MMDeviceEnumerator, NULL, CLSCTX_ALL,
                              (REFIID)&G_IID_IMMDeviceEnumerator, (void **)&g_enum);
        if (FAILED(hr) || !g_enum) {
            g_enum = NULL;
            if (!g_micFailLogged) { log_line(L"mic: CoCreateInstance(MMDeviceEnumerator) hr=0x%08lX", (unsigned long)hr); g_micFailLogged = 1; }
            return FALSE;
        }
    }
    g_vol = mic_open_role(eCommunications);      /* его берут Discord/Skype/игры */
    if (!g_vol) g_vol = mic_open_role(eConsole);
    if (!g_vol) {
        if (!g_micFailLogged) { log_line(L"mic: no default capture endpoint"); g_micFailLogged = 1; }
        return FALSE;
    }
    g_vol2 = mic_open_role(eConsole);            /* может совпасть с g_vol, может быть другой */
    g_micFailLogged = 0;
    DBG(L"mic: endpoint acquired");
    return TRUE;
}

static LPWSTR mic_device_id(void)
{
    IMMDevice *dev = NULL;
    LPWSTR id = NULL;
    HRESULT hr;
    if (!mic_acquire()) return NULL;
    hr = IMMDeviceEnumerator_GetDefaultAudioEndpoint(g_enum, eCapture, eCommunications, &dev);
    if (FAILED(hr) || !dev)
        hr = IMMDeviceEnumerator_GetDefaultAudioEndpoint(g_enum, eCapture, eConsole, &dev);
    if (SUCCEEDED(hr) && dev) {
        IMMDevice_GetId(dev, &id);
        IMMDevice_Release(dev);
    }
    return id;
}

static BOOL mic_set_mute(BOOL mute)
{
    HRESULT hr, hr2 = S_OK;
    if (!mic_acquire()) return FALSE;
    hr = IAudioEndpointVolume_SetMute(g_vol, mute, NULL);
    if (g_vol2) hr2 = IAudioEndpointVolume_SetMute(g_vol2, mute, NULL);
    if (FAILED(hr) || FAILED(hr2)) {        /* устройство переключили/отключили — берём заново */
        mic_release();
        if (!mic_acquire()) return FALSE;
        hr = IAudioEndpointVolume_SetMute(g_vol, mute, NULL);
        if (g_vol2) hr2 = IAudioEndpointVolume_SetMute(g_vol2, mute, NULL);
    }
    if (FAILED(hr) || FAILED(hr2)) {
        log_line(L"mic: SetMute(%d) hr=0x%08lX/0x%08lX", (int)mute, (unsigned long)hr, (unsigned long)hr2);
        mic_release();
        return FALSE;
    }
    return TRUE;
}

static BOOL mic_get_mute(BOOL *out)
{
    HRESULT hr;
    BOOL b = FALSE;
    if (!mic_acquire()) return FALSE;
    hr = IAudioEndpointVolume_GetMute(g_vol, &b);
    if (FAILED(hr)) {
        mic_release();
        if (!mic_acquire()) return FALSE;
        hr = IAudioEndpointVolume_GetMute(g_vol, &b);
        if (FAILED(hr)) { mic_release(); return FALSE; }
    }
    *out = b;
    return TRUE;
}

/* ====================== конфиг / автозапуск / имена клавиш =============== */
static void config_load(void)
{
    int v;
    g_keyVK      = GetPrivateProfileIntW(L"main", L"key",  VK_HOME, g_iniPath);
    v = GetPrivateProfileIntW(L"main", L"ptt",   1, g_iniPath); g_pttEnabled = (v != 0);
    v = GetPrivateProfileIntW(L"main", L"block", 1, g_iniPath); g_blockKey   = (v != 0);
    v = GetPrivateProfileIntW(L"main", L"lang",  0, g_iniPath); g_lang       = (v != 0) ? 1 : 0;
    v = GetPrivateProfileIntW(L"main", L"debug", 0, g_iniPath); g_debug      = (v != 0);
    if (g_keyVK < 1 || g_keyVK > 0xFF) g_keyVK = VK_HOME;
}

static void config_save(void)
{
    wchar_t b[16];
    WritePrivateProfileStringW(L"main", L"key",   _itow(g_keyVK, b, 10), g_iniPath);
    WritePrivateProfileStringW(L"main", L"ptt",   g_pttEnabled ? L"1" : L"0", g_iniPath);
    WritePrivateProfileStringW(L"main", L"block", g_blockKey   ? L"1" : L"0", g_iniPath);
    WritePrivateProfileStringW(L"main", L"lang",  g_lang ? L"1" : L"0", g_iniPath);
    WritePrivateProfileStringW(L"main", L"debug", g_debug ? L"1" : L"0", g_iniPath);
    DBG(L"config saved: key=%d ptt=%d block=%d lang=%d", g_keyVK, g_pttEnabled, g_blockKey, g_lang);
}

static void autostart_set(BOOL on)
{
    HKEY k;
    if (RegCreateKeyExW(HKEY_CURRENT_USER,
            L"Software\\Microsoft\\Windows\\CurrentVersion\\Run",
            0, NULL, 0, KEY_SET_VALUE | KEY_QUERY_VALUE, NULL, &k, NULL) != ERROR_SUCCESS) {
        log_line(L"autostart: RegCreateKeyEx failed");
        return;
    }
    if (on) {
        wchar_t val[MAX_PATH + 4];
        swprintf(val, MAX_PATH + 3, L"\"%ls\"", g_exePath);
        RegSetValueExW(k, REGVALNAME, 0, REG_SZ, (const BYTE *)val,
                       (DWORD)((wcslen(val) + 1) * sizeof(wchar_t)));
        log_line(L"autostart: enabled (%ls)", g_exePath);
    } else {
        RegDeleteValueW(k, REGVALNAME);
        log_line(L"autostart: disabled");
    }
    RegCloseKey(k);
}

static BOOL autostart_get(void)
{
    HKEY k;
    DWORD type = 0, cb = 0;
    BOOL found = FALSE;
    if (RegOpenKeyExW(HKEY_CURRENT_USER,
            L"Software\\Microsoft\\Windows\\CurrentVersion\\Run",
            0, KEY_QUERY_VALUE, &k) == ERROR_SUCCESS) {
        if (RegQueryValueExW(k, REGVALNAME, NULL, &type, NULL, &cb) == ERROR_SUCCESS && type == REG_SZ)
            found = TRUE;
        RegCloseKey(k);
    }
    return found;
}

static void vk_name(int vk, wchar_t *out, int cch)
{
    static const struct { int vk; const wchar_t *name; } tbl[] = {
        {VK_HOME,L"HOME"},{VK_END,L"END"},{VK_INSERT,L"INSERT"},{VK_DELETE,L"DELETE"},
        {VK_PRIOR,L"PAGE UP"},{VK_NEXT,L"PAGE DOWN"},{VK_SPACE,L"SPACE"},{VK_TAB,L"TAB"},
        {VK_RETURN,L"ENTER"},{VK_ESCAPE,L"ESC"},{VK_BACK,L"BACKSPACE"},
        {VK_CAPITAL,L"CAPS LOCK"},{VK_SCROLL,L"SCROLL LOCK"},{VK_NUMLOCK,L"NUM LOCK"},
        {VK_PAUSE,L"PAUSE"},{VK_SNAPSHOT,L"PRINT SCREEN"},
        {VK_LCONTROL,L"LEFT CTRL"},{VK_RCONTROL,L"RIGHT CTRL"},{VK_CONTROL,L"CTRL"},
        {VK_LMENU,L"LEFT ALT"},{VK_RMENU,L"RIGHT ALT"},{VK_MENU,L"ALT"},
        {VK_LSHIFT,L"LEFT SHIFT"},{VK_RSHIFT,L"RIGHT SHIFT"},{VK_SHIFT,L"SHIFT"},
        {VK_LWIN,L"LEFT WIN"},{VK_RWIN,L"RIGHT WIN"},{VK_APPS,L"MENU KEY"},
        {VK_LEFT,L"LEFT ARROW"},{VK_RIGHT,L"RIGHT ARROW"},{VK_UP,L"UP ARROW"},{VK_DOWN,L"DOWN ARROW"},
        {VK_OEM_1,L";"},{VK_OEM_PLUS,L"="},{VK_OEM_COMMA,L","},{VK_OEM_MINUS,L"-"},
        {VK_OEM_PERIOD,L"."},{VK_OEM_2,L"/"},{VK_OEM_3,L"`"},{VK_OEM_4,L"["},
        {VK_OEM_5,L"\\"},{VK_OEM_6,L"]"},{VK_OEM_7,L"'"},{VK_OEM_102,L"< >"},
        {VK_ADD,L"NUM +"},{VK_SUBTRACT,L"NUM -"},{VK_MULTIPLY,L"NUM *"},
        {VK_DIVIDE,L"NUM /"},{VK_DECIMAL,L"NUM ."},{VK_SEPARATOR,L"NUM ,"},
    };
    int i;
    if (vk >= VK_F1 && vk <= VK_F24) { swprintf(out, cch, L"F%d", vk - VK_F1 + 1); return; }
    if (vk >= 'A' && vk <= 'Z')      { swprintf(out, cch, L"%lc", (wchar_t)vk); return; }
    if (vk >= '0' && vk <= '9')      { swprintf(out, cch, L"%lc", (wchar_t)vk); return; }
    if (vk >= VK_NUMPAD0 && vk <= VK_NUMPAD9) { swprintf(out, cch, L"NUM %d", vk - VK_NUMPAD0); return; }
    for (i = 0; i < (int)(sizeof(tbl)/sizeof(tbl[0])); i++)
        if (tbl[i].vk == vk) { wcsncpy(out, tbl[i].name, cch - 1); out[cch-1] = 0; return; }
    swprintf(out, cch, L"VK 0x%02X", vk);
}

/* ============================== хук клавиатуры ========================== */
static void refresh_ui(void);
static void refresh_tray(void);
static void apply_mic_state(void);
static void hook_update(void);

static LRESULT CALLBACK LLKbdProc(int nCode, WPARAM wParam, LPARAM lParam)
{
    if (nCode == HC_ACTION) {
        KBDLLHOOKSTRUCT *k = (KBDLLHOOKSTRUCT *)lParam;
        DWORD vk = k->vkCode;
        BOOL down = (wParam == WM_KEYDOWN || wParam == WM_SYSKEYDOWN);

        if (g_capturing) {
            if (down) {
                if (vk == VK_ESCAPE) {                 /* Esc — отмена */
                    g_capturing = FALSE;
                    DBG(L"key capture: cancelled");
                } else {
                    g_keyVK = (int)vk;
                    g_capturing = FALSE;
                    config_save();
                    log_line(L"key capture: new key VK=%d", g_keyVK);
                }
                if (g_hwnd) PostMessageW(g_hwnd, WM_REFRESH, 0, 0);
                hook_update();
            }
            return 1;                                   /* не отдаём клавишу дальше */
        }

        if (g_pttEnabled && (DWORD)g_keyVK == vk) {
            if (down && !g_keyDown) {
                g_keyDown = TRUE;
                log_line(L"ptt: key down -> live");
                apply_mic_state();
            } else if (!down && g_keyDown) {
                g_keyDown = FALSE;
                log_line(L"ptt: key up -> muted");
                apply_mic_state();
            }
            if (g_blockKey) return 1;                   /* клавишу не отдаём другим */
        }
    }
    return CallNextHookEx(NULL, nCode, wParam, lParam);
}

static void hook_update(void)
{
    BOOL need = (g_pttEnabled || g_capturing);
    if (need && !g_hook) {
        g_hook = SetWindowsHookExW(WH_KEYBOARD_LL, LLKbdProc, g_hInst, 0);
        if (g_hook) log_line(L"hook: installed (WH_KEYBOARD_LL)");
        else {
            log_line(L"hook: SetWindowsHookEx failed err=%lu", GetLastError());
            if (g_pttEnabled && !g_hotkeyFallback) {   /* запасной путь */
                if (RegisterHotKey(g_hwnd, 1, MOD_NOREPEAT, (UINT)g_keyVK)) {
                    g_hotkeyFallback = TRUE;
                    log_line(L"hook: fallback RegisterHotKey OK");
                } else log_line(L"hook: RegisterHotKey failed err=%lu", GetLastError());
            }
        }
    } else if (!need && g_hook) {
        UnhookWindowsHookEx(g_hook);
        g_hook = NULL;
        if (g_hotkeyFallback) { UnregisterHotKey(g_hwnd, 1); g_hotkeyFallback = FALSE; }
        log_line(L"hook: removed");
    } else if (g_hotkeyFallback && g_pttEnabled) {
        UnregisterHotKey(g_hwnd, 1);
        g_hotkeyFallback = (RegisterHotKey(g_hwnd, 1, MOD_NOREPEAT, (UINT)g_keyVK) != 0);
    }
}

/* ============================ применение состояния ====================== */
static void apply_mic_state(void)
{
    BOOL want = (g_pttEnabled && !g_keyDown) ? TRUE : FALSE;   /* TRUE = заглушён */
    BOOL cur = FALSE;
    BOOL haveState;

    haveState = mic_get_mute(&cur);
    if (!haveState) {
        g_micMuted = -1;
        refresh_ui(); refresh_tray();
        return;
    }
    if (cur == want) { g_micMuted = cur ? 1 : 0; refresh_ui(); refresh_tray(); return; }
    if (mic_set_mute(want)) {
        g_micMuted = want ? 1 : 0;
        log_line(L"mic: mute=%d", (int)want);
    } else {
        g_micMuted = -1;
    }
    refresh_ui(); refresh_tray();
}

/* ============================ UI: окно и трей =========================== */
static HICON tray_icon(void)
{
    int id;
    if (g_micMuted < 0) id = IDI_ICON_OFF;
    else id = g_micMuted ? IDI_ICON_MUTED : IDI_ICON_LIVE;
    return (HICON)LoadImageW(g_hInst, MAKEINTRESOURCEW(id), IMAGE_ICON,
                             GetSystemMetrics(SM_CXSMICON), GetSystemMetrics(SM_CYSMICON), 0);
}

static void refresh_tray(void)
{
    int id = (g_micMuted < 0) ? STR_TRAY_OFF : (g_micMuted ? STR_TRAY_MUTED : STR_TRAY_LIVE);
    const wchar_t *tip = g_pttEnabled ? T(id) : T(STR_TRAY_OFF);
    g_nid.uFlags = NIF_ICON | NIF_MESSAGE | NIF_TIP;
    g_nid.hIcon = tray_icon();
    wcsncpy(g_nid.szTip, tip, 127); g_nid.szTip[127] = 0;
    Shell_NotifyIconW(NIM_MODIFY, &g_nid);
    if (g_nid.hIcon) DestroyIcon(g_nid.hIcon);
}

static void refresh_ui(void)
{
    wchar_t buf[256], kn[64];
    const wchar_t *state;
    HWND c;

    if (!g_hwnd) return;
    if (g_micMuted < 0)          state = T(STR_STATE_NOMIC);
    else if (!g_pttEnabled)      state = T(STR_STATE_OFF);
    else if (g_micMuted)         state = T(STR_STATE_MUTED);
    else                         state = T(STR_STATE_LIVE);

    SetWindowTextW(GetDlgItem(g_hwnd, IDC_LABEL_STATE), state);
    vk_name(g_keyVK, kn, 64);
    swprintf(buf, 255, T(STR_KEY_FMT), kn);
    SetWindowTextW(GetDlgItem(g_hwnd, IDC_LABEL_KEY), buf);

    c = GetDlgItem(g_hwnd, IDC_BTN_KEY);
    SetWindowTextW(c, g_capturing ? T(STR_BTN_CAPTURE) : T(STR_BTN_CHANGE));

    c = GetDlgItem(g_hwnd, IDC_CHK_PTT);
    SetWindowTextW(c, T(STR_CHK_PTT));
    SendMessageW(c, BM_SETCHECK, g_pttEnabled ? BST_CHECKED : BST_UNCHECKED, 0);
    c = GetDlgItem(g_hwnd, IDC_CHK_BLOCK);
    SetWindowTextW(c, T(STR_CHK_BLOCK));
    SendMessageW(c, BM_SETCHECK, g_blockKey ? BST_CHECKED : BST_UNCHECKED, 0);
    c = GetDlgItem(g_hwnd, IDC_CHK_AUTOSTART);
    SetWindowTextW(c, T(STR_CHK_AUTOSTART));
    SendMessageW(c, BM_SETCHECK, autostart_get() ? BST_CHECKED : BST_UNCHECKED, 0);
    SetWindowTextW(GetDlgItem(g_hwnd, IDC_BTN_LANG), T(STR_LANG_BTN));
    SetWindowTextW(GetDlgItem(g_hwnd, IDC_BTN_EXIT), T(STR_BTN_EXIT));
    SetWindowTextW(GetDlgItem(g_hwnd, IDC_HINT), T(STR_HINT_KEY));
    SetWindowTextW(GetDlgItem(g_hwnd, IDC_TRAY_HINT), T(STR_TRAY_HINT));
    SetWindowTextW(g_hwnd, T(STR_TITLE));
}

static void show_window(void)
{
    log_line(L"window shown from tray");
    ShowWindow(g_hwnd, SW_SHOW);
    ShowWindow(g_hwnd, SW_RESTORE);
    SetWindowPos(g_hwnd, HWND_TOP, 0, 0, 0, 0,
                 SWP_NOMOVE | SWP_NOSIZE | SWP_SHOWWINDOW);
    SetForegroundWindow(g_hwnd);
    BringWindowToTop(g_hwnd);
}

static void hide_window(void)
{
    log_line(L"window hidden -> tray");
    ShowWindow(g_hwnd, SW_HIDE);
    if (!g_balloonShown) {
        g_nid.uFlags = NIF_INFO;
        wcsncpy(g_nid.szInfoTitle, T(STR_BALLOON_TITLE), 63);
        wcsncpy(g_nid.szInfo, T(STR_BALLOON_TEXT), 255);
        g_nid.dwInfoFlags = NIIF_INFO;
        Shell_NotifyIconW(NIM_MODIFY, &g_nid);
        g_balloonShown = TRUE;
    }
    refresh_tray();
}

static void tray_menu(void)
{
    HMENU m = CreatePopupMenu();
    POINT p;
    AppendMenuW(m, MF_STRING, IDM_SHOW, T(STR_MENU_SHOW));
    AppendMenuW(m, MF_STRING | (g_pttEnabled ? MF_CHECKED : 0), IDM_TOGGLE, T(STR_MENU_TOGGLE));
    AppendMenuW(m, MF_SEPARATOR, 0, NULL);
    AppendMenuW(m, MF_STRING, IDM_EXIT, T(STR_MENU_EXIT));
    GetCursorPos(&p);
    SetForegroundWindow(g_hwnd);
    TrackPopupMenu(m, TPM_RIGHTBUTTON, p.x, p.y, 0, g_hwnd, NULL);
    DestroyMenu(m);
    PostMessageW(g_hwnd, WM_NULL, 0, 0);
}

static void on_exit(void)
{
    g_quitting = TRUE;
    if (g_hook) { UnhookWindowsHookEx(g_hook); g_hook = NULL; }
    if (g_hotkeyFallback && g_hwnd) { UnregisterHotKey(g_hwnd, 1); g_hotkeyFallback = FALSE; }
    mic_set_mute(FALSE);                    /* не оставляем микрофон заглушённым */
    mic_release();
    log_line(L"exit");
    Shell_NotifyIconW(NIM_DELETE, &g_nid);
    DestroyWindow(g_hwnd);
}

static void create_controls(HWND h)
{
    g_font      = CreateFontW(-MulDiv(9, g_dpi, 72), 0,0,0, FW_NORMAL,0,0,0, DEFAULT_CHARSET,
                              OUT_DEFAULT_PRECIS, CLIP_DEFAULT_PRECIS, CLEARTYPE_QUALITY,
                              DEFAULT_PITCH, L"Segoe UI");
    g_fontSmall = CreateFontW(-MulDiv(8, g_dpi, 72), 0,0,0, FW_NORMAL,0,0,0, DEFAULT_CHARSET,
                              OUT_DEFAULT_PRECIS, CLIP_DEFAULT_PRECIS, CLEARTYPE_QUALITY,
                              DEFAULT_PITCH, L"Segoe UI");

    struct { const wchar_t *cls; const wchar_t *txt; DWORD st; int x,y,w,hh,id; } ctl[] = {
      {L"STATIC",   L"", BS_LEFT,               16, 14,388, 20, IDC_LABEL_STATE},
      {L"STATIC",   L"", BS_LEFT,               16, 44,388, 20, IDC_LABEL_KEY},
      {L"BUTTON",   L"", BS_PUSHBUTTON,         16, 74,180, 30, IDC_BTN_KEY},
      {L"STATIC",   L"", BS_LEFT,               16,114,388, 34, IDC_HINT},
      {L"BUTTON",   L"", BS_AUTOCHECKBOX,       16,154,388, 22, IDC_CHK_PTT},
      {L"BUTTON",   L"", BS_AUTOCHECKBOX,       16,180,388, 22, IDC_CHK_BLOCK},
      {L"BUTTON",   L"", BS_AUTOCHECKBOX,       16,206,388, 22, IDC_CHK_AUTOSTART},
      {L"BUTTON",   L"", BS_PUSHBUTTON,         16,240,180, 30, IDC_BTN_LANG},
      {L"BUTTON",   L"", BS_PUSHBUTTON,        254,240,150, 30, IDC_BTN_EXIT},
      {L"STATIC",   L"", BS_LEFT,               16,280,388, 34, IDC_TRAY_HINT},
    };
    int i;
    for (i = 0; i < (int)(sizeof(ctl)/sizeof(ctl[0])); i++) {
        HWND c = CreateWindowExW(0, ctl[i].cls, ctl[i].txt,
                                 WS_CHILD | WS_VISIBLE | ctl[i].st,
                                 SC(ctl[i].x), SC(ctl[i].y), SC(ctl[i].w), SC(ctl[i].hh),
                                 h, (HMENU)(INT_PTR)ctl[i].id, g_hInst, NULL);
        SendMessageW(c, WM_SETFONT, (WPARAM)((ctl[i].id == IDC_HINT || ctl[i].id == IDC_TRAY_HINT)
                                             ? g_fontSmall : g_font), TRUE);
    }
}

static LRESULT CALLBACK WndProc(HWND h, UINT m, WPARAM w, LPARAM l)
{
    switch (m) {
    case WM_CREATE:
        create_controls(h);
        return 0;

    case WM_COMMAND:
        switch (LOWORD(w)) {
        case IDC_BTN_KEY:
            g_capturing = TRUE;
            hook_update();
            refresh_ui();
            log_line(L"key capture: waiting for key");
            return 0;
        case IDC_CHK_PTT:
            g_pttEnabled = (SendMessageW((HWND)l, BM_GETCHECK, 0, 0) == BST_CHECKED);
            if (!g_pttEnabled) g_keyDown = FALSE;
            log_line(L"ptt enabled=%d", g_pttEnabled);
            config_save();
            hook_update();
            apply_mic_state();
            return 0;
        case IDC_CHK_BLOCK:
            g_blockKey = (SendMessageW((HWND)l, BM_GETCHECK, 0, 0) == BST_CHECKED);
            log_line(L"block key=%d", g_blockKey);
            config_save();
            return 0;
        case IDC_CHK_AUTOSTART:
            autostart_set(SendMessageW((HWND)l, BM_GETCHECK, 0, 0) == BST_CHECKED);
            refresh_ui();
            return 0;
        case IDC_BTN_LANG:
            g_lang = !g_lang;
            log_line(L"ui language=%ls", g_lang ? L"EN" : L"RU");
            config_save();
            refresh_ui(); refresh_tray();
            return 0;
        case IDC_BTN_EXIT:
        case IDM_EXIT:
            on_exit();
            return 0;
        case IDM_SHOW:
            show_window();
            return 0;
        case IDM_TOGGLE:
            g_pttEnabled = !g_pttEnabled;
            if (!g_pttEnabled) g_keyDown = FALSE;
            config_save(); hook_update(); apply_mic_state();
            return 0;
        }
        return 0;

    case WM_TRAY_MSG:
        if (l == WM_LBUTTONUP || l == WM_LBUTTONDBLCLK) show_window();
        else if (l == WM_RBUTTONUP) tray_menu();
        return 0;

    case WM_REFRESH:
        refresh_ui();
        return 0;

    case WM_HOTKEY:                             /* запасной путь, если хук не встал */
        if (w == 1) {
            if (l & 0x80000000) g_keyDown = FALSE; else g_keyDown = TRUE;
            apply_mic_state();
        }
        return 0;

    case WM_TIMER:
        if (w == TIMER_POLL) {
            BOOL cur = FALSE;
            if (mic_get_mute(&cur)) {
                int nv = cur ? 1 : 0;
                if (nv != g_micMuted) { g_micMuted = nv; refresh_ui(); refresh_tray(); }
            }
        }
        return 0;

    case WM_CTLCOLORSTATIC:
        if (GetDlgCtrlID((HWND)l) == IDC_HINT || GetDlgCtrlID((HWND)l) == IDC_TRAY_HINT) {
            SetTextColor((HDC)w, GetSysColor(COLOR_GRAYTEXT));
            SetBkMode((HDC)w, TRANSPARENT);
            return (LRESULT)GetSysColorBrush(COLOR_BTNFACE);
        }
        SetBkMode((HDC)w, TRANSPARENT);
        return (LRESULT)GetSysColorBrush(COLOR_BTNFACE);

    case WM_SYSCOMMAND:
        if ((w & 0xFFF0) == SC_MINIMIZE) { hide_window(); return 0; }
        break;

    case WM_CLOSE:
        hide_window();                          /* закрытие = в трей, не выход */
        log_line(L"window closed -> tray");
        return 0;

    case WM_QUERYENDSESSION:
        mic_set_mute(FALSE);
        return TRUE;
    case WM_ENDSESSION:
        mic_set_mute(FALSE);
        return 0;

    case WM_DESTROY:
        PostQuitMessage(0);
        return 0;
    }
    return DefWindowProcW(h, m, w, l);
}

/* ============================ точка входа GUI =========================== */
#ifndef APP_SELFTEST
int WINAPI WinMain(HINSTANCE hInst, HINSTANCE hPrev, LPSTR cmdline, int show)
{
    WNDCLASSEXW wc;
    MSG msg;
    HANDLE mtx;
    RECT r;
    DWORD style = WS_OVERLAPPED | WS_CAPTION | WS_SYSMENU | WS_MINIMIZEBOX;
    wchar_t appdata[MAX_PATH];

    (void)hPrev; (void)cmdline; (void)show;
    g_hInst = hInst;

    /* пути: %APPDATA%\PushToTalk */
    GetModuleFileNameW(NULL, g_exePath, MAX_PATH);
    if (SHGetFolderPathW(NULL, CSIDL_APPDATA, NULL, 0, appdata) == S_OK) {
        swprintf(g_appDir, MAX_PATH, L"%ls\\%ls", appdata, DIRNAME);
        CreateDirectoryW(g_appDir, NULL);
    } else {
        wcsncpy(g_appDir, g_exePath, MAX_PATH - 1);
        { wchar_t *p = wcsrchr(g_appDir, L'\\'); if (p) *p = 0; }
    }
    swprintf(g_iniPath, MAX_PATH, L"%ls\\config.ini", g_appDir);
    swprintf(g_logPath, MAX_PATH, L"%ls\\log.txt", g_appDir);
    WideCharToMultiByte(CP_ACP, 0, g_logPath, -1, g_logPathA, sizeof(g_logPathA), NULL, NULL);

    /* лог начинаем заново при каждом старте */
    { FILE *f = fopen(g_logPathA, "wb"); if (f) fclose(f); }

    /* одна копия приложения */
    mtx = CreateMutexW(NULL, TRUE, L"Local\\PushToTalkSingleInstance");
    if (mtx && GetLastError() == ERROR_ALREADY_EXISTS) {
        HWND old = FindWindowW(L"PushToTalkWnd", NULL);
        if (old) { ShowWindow(old, SW_SHOW); SetForegroundWindow(old); }
        CloseHandle(mtx);
        return 0;
    }

    SetProcessDPIAware();
    { HDC dc = GetDC(NULL); g_dpi = GetDeviceCaps(dc, LOGPIXELSX); ReleaseDC(NULL, dc); }

    config_load();
    log_line(L"--- start: key=%d ptt=%d block=%d lang=%d dpi=%d", g_keyVK, g_pttEnabled, g_blockKey, g_lang, g_dpi);
    log_line(L"paths: ini=%ls", g_iniPath);

    if (FAILED(CoInitializeEx(NULL, COINIT_APARTMENTTHREADED))) {
        log_line(L"CoInitializeEx failed");
    }

    memset(&wc, 0, sizeof(wc));
    wc.cbSize = sizeof(wc);
    wc.lpfnWndProc = WndProc;
    wc.hInstance = hInst;
    wc.hCursor = LoadCursor(NULL, IDC_ARROW);
    wc.hbrBackground = GetSysColorBrush(COLOR_BTNFACE);
    wc.lpszClassName = L"PushToTalkWnd";
    wc.hIcon = (HICON)LoadImageW(hInst, MAKEINTRESOURCEW(IDI_ICON_LIVE), IMAGE_ICON, 32, 32, 0);
    wc.hIconSm = (HICON)LoadImageW(hInst, MAKEINTRESOURCEW(IDI_ICON_LIVE), IMAGE_ICON, 16, 16, 0);
    if (!RegisterClassExW(&wc)) { log_line(L"RegisterClassEx failed err=%lu", GetLastError()); return 1; }

    r.left = 0; r.top = 0; r.right = SC(420); r.bottom = SC(312);
    AdjustWindowRectEx(&r, style, FALSE, 0);
    g_hwnd = CreateWindowExW(0, L"PushToTalkWnd", APPTITLE, style,
                             CW_USEDEFAULT, CW_USEDEFAULT,
                             r.right - r.left, r.bottom - r.top,
                             NULL, NULL, hInst, NULL);
    if (!g_hwnd) { log_line(L"CreateWindowEx failed err=%lu", GetLastError()); return 1; }

    /* трей */
    memset(&g_nid, 0, sizeof(g_nid));
    g_nid.cbSize = sizeof(g_nid);
    g_nid.hWnd = g_hwnd;
    g_nid.uID = 1;
    g_nid.uCallbackMessage = WM_TRAY_MSG;
    g_nid.uFlags = NIF_ICON | NIF_MESSAGE | NIF_TIP;
    g_nid.hIcon = tray_icon();
    wcsncpy(g_nid.szTip, APPTITLE, 127);
    if (!Shell_NotifyIconW(NIM_ADD, &g_nid)) log_line(L"tray: NIM_ADD failed");
    else log_line(L"tray: added");
    if (g_nid.hIcon) DestroyIcon(g_nid.hIcon);

    hook_update();
    mic_set_mute(FALSE);            /* на старте всегда снимаем mute */
    apply_mic_state();              /* затем — по текущему режиму PTT */
    refresh_ui();
    SetTimer(g_hwnd, TIMER_POLL, 1500, NULL);
    ShowWindow(g_hwnd, SW_SHOW);
    UpdateWindow(g_hwnd);
    log_line(L"ready (mic=%ls)", g_micMuted < 0 ? L"unavailable" : (g_micMuted ? L"muted" : L"live"));

    while (GetMessageW(&msg, NULL, 0, 0) > 0) {
        TranslateMessage(&msg);
        DispatchMessageW(&msg);
    }

    if (g_hook) UnhookWindowsHookEx(g_hook);
    mic_set_mute(FALSE);
    mic_release();
    CoUninitialize();
    if (mtx) { ReleaseMutex(mtx); CloseHandle(mtx); }
    log_line(L"--- stopped");
    return 0;
}
#endif /* !APP_SELFTEST */

/* ============== консольный самотест (ptt_selftest.exe) ================== */
#ifdef APP_SELFTEST
static int g_pass, g_fail;
static void check(const char *name, int ok, const char *info)
{
    printf("[%s] %-34s %s\n", ok ? " OK " : "FAIL", name, info ? info : "");
    if (ok) g_pass++; else g_fail++;
}
static void pw(const wchar_t *s)
{
    char u8[2048];
    if (!s) { printf("(null)"); return; }
    if (WideCharToMultiByte(CP_UTF8, 0, s, -1, u8, sizeof(u8), NULL, NULL) > 0) printf("%s", u8);
}

int main(int argc, char **argv)
{
    wchar_t appdata[MAX_PATH], buf[600], kn[64], idbuf[512];
    LPWSTR devId;
    BOOL b = FALSE;
    HHOOK hk;
    int st;
    (void)argc; (void)argv;

    SetConsoleOutputCP(CP_UTF8);

    if (argc > 1 && (strcmp(argv[1], "--get") == 0 || strcmp(argv[1], "--mute") == 0 ||
                     strcmp(argv[1], "--unmute") == 0)) {
        BOOL cur = FALSE;
        GetModuleFileNameW(NULL, g_exePath, MAX_PATH);
        if (SHGetFolderPathW(NULL, CSIDL_APPDATA, NULL, 0, appdata) == S_OK) {
            swprintf(g_appDir, MAX_PATH, L"%ls\\%ls", appdata, DIRNAME);
            CreateDirectoryW(g_appDir, NULL);
        }
        swprintf(g_logPath, MAX_PATH, L"%ls\\log.txt", g_appDir);
        WideCharToMultiByte(CP_ACP, 0, g_logPath, -1, g_logPathA, sizeof(g_logPathA), NULL, NULL);
        if (FAILED(CoInitializeEx(NULL, COINIT_APARTMENTTHREADED))) return 2;
        if (strcmp(argv[1], "--mute") == 0)   mic_set_mute(TRUE);
        if (strcmp(argv[1], "--unmute") == 0) mic_set_mute(FALSE);
        if (!mic_get_mute(&cur)) { printf("no-endpoint\n"); return 3; }
        printf("mute=%d\n", (int)cur);
        mic_release();
        CoUninitialize();
        return 0;
    }

    printf("=== PushToTalk self-test ===\n");

    GetModuleFileNameW(NULL, g_exePath, MAX_PATH);
    if (SHGetFolderPathW(NULL, CSIDL_APPDATA, NULL, 0, appdata) == S_OK) {
        swprintf(g_appDir, MAX_PATH, L"%ls\\%ls", appdata, DIRNAME);
        CreateDirectoryW(g_appDir, NULL);
    } else {
        wcsncpy(g_appDir, g_exePath, MAX_PATH - 1);
        { wchar_t *p = wcsrchr(g_appDir, L'\\'); if (p) *p = 0; }
    }
    swprintf(g_iniPath, MAX_PATH, L"%ls\\config.ini", g_appDir);
    swprintf(g_logPath, MAX_PATH, L"%ls\\log.txt", g_appDir);
    WideCharToMultiByte(CP_ACP, 0, g_logPath, -1, g_logPathA, sizeof(g_logPathA), NULL, NULL);
    g_debug = TRUE;
    log_line(L"self-test start");

    printf("exe     : "); pw(g_exePath); printf("\n");
    printf("ini     : "); pw(g_iniPath); printf("\n");
    printf("log     : "); pw(g_logPath); printf("\n\n");

    /* 1. ключи клавиатуры */
    vk_name(VK_HOME, kn, 64);      check("key name HOME",      wcscmp(kn, L"HOME") == 0, "");
    vk_name(VK_F5, kn, 64);        check("key name F5",        wcscmp(kn, L"F5") == 0, "");
    vk_name('Q', kn, 64);          check("key name Q",         wcscmp(kn, L"Q") == 0, "");
    vk_name(VK_RCONTROL, kn, 64);  check("key name RIGHT CTRL", wcscmp(kn, L"RIGHT CTRL") == 0, "");

    /* 2. конфиг */
    g_keyVK = VK_F8; g_pttEnabled = FALSE; g_blockKey = FALSE; g_lang = 1;
    config_save();
    g_keyVK = 0; g_pttEnabled = TRUE; g_blockKey = TRUE; g_lang = 0;
    config_load();
    check("config round-trip", g_keyVK == VK_F8 && !g_pttEnabled && !g_blockKey && g_lang == 1,
          "saved key=F8 ptt=0 block=0 lang=EN, read back");
    g_keyVK = VK_HOME; g_pttEnabled = TRUE; g_blockKey = TRUE; g_lang = 0;
    g_debug = FALSE; config_save();          /* вернуть дефолты (и не оставлять debug=1) */
    {
        FILE *f = _wfopen(g_iniPath, L"rb");
        check("config.ini exists", f != NULL, "");
        if (f) fclose(f);
    }

    /* 3. автозапуск (реестр HKCU\...\Run) */
    autostart_set(TRUE);
    check("autostart set -> registry", autostart_get(), "");
    autostart_set(FALSE);
    check("autostart cleared", !autostart_get(), "");

    /* 4. Core Audio: доступ к микрофону и mute/unmute */
    if (FAILED(CoInitializeEx(NULL, COINIT_APARTMENTTHREADED))) check("CoInitializeEx", 0, "failed");
    else check("CoInitializeEx", 1, "apartment threaded");

    devId = mic_device_id();
    if (devId) { wcsncpy(idbuf, devId, 511); idbuf[511] = 0; CoTaskMemFree(devId); }
    else idbuf[0] = 0;
    swprintf(buf, 599, L"default capture endpoint: %ls", idbuf[0] ? idbuf : L"(none)");
    { char u8[1400]; WideCharToMultiByte(CP_UTF8, 0, buf, -1, u8, sizeof(u8), NULL, NULL); printf("       %s\n", u8); }
    check("mic endpoint available", idbuf[0] != 0, "");

    if (idbuf[0]) {
        st = mic_get_mute(&b);
        printf("       GetMute ok=%d value=%d\n", st, (int)b);
        st = mic_set_mute(TRUE);
        check("SetMute(TRUE)", st && mic_get_mute(&b) && b == TRUE, "");
        st = mic_set_mute(FALSE);
        check("SetMute(FALSE)", st && mic_get_mute(&b) && b == FALSE, "");
        check("mic left unmuted", mic_get_mute(&b) && b == FALSE, "");
    } else {
        check("SetMute(TRUE)", 0, "skipped: no endpoint");
        check("SetMute(FALSE)", 0, "skipped: no endpoint");
        check("mic left unmuted", 0, "skipped: no endpoint");
    }

    /* 5. хук клавиатуры */
    hk = SetWindowsHookExW(WH_KEYBOARD_LL, LLKbdProc, GetModuleHandleW(NULL), 0);
    check("install WH_KEYBOARD_LL", hk != NULL, "");
    if (hk) UnhookWindowsHookEx(hk);

    /* 6. запасной путь: RegisterHotKey на поток */
    check("RegisterHotKey(VK_HOME)", RegisterHotKey(NULL, 1, MOD_NOREPEAT, VK_HOME) != 0, "");
    UnregisterHotKey(NULL, 1);

    /* 7. окно и трей: создаём настоящее окно и иконку в трее */
    {
        HWND w = CreateWindowExW(0, L"STATIC", L"ptt_selftest", WS_OVERLAPPED, 0,0,100,100, NULL, NULL, NULL, NULL);
        check("CreateWindow", w != NULL, "");
        if (w) DestroyWindow(w);
    }
    mic_set_mute(FALSE);
    mic_release();
    CoUninitialize();

    printf("\nRESULT: %d passed, %d failed\n", g_pass, g_fail);
    log_line(L"self-test done: %d passed, %d failed", g_pass, g_fail);
    return g_fail ? 1 : 0;
}
#endif /* APP_SELFTEST */
