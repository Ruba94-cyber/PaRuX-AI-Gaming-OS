/*
 * PaRuX XInput shim: a driverless virtual Xbox pad for games that read XInput.
 *
 * It is a drop-in xinput1_3 / xinput1_4 / xinput9_1_0 that a game loads from its own folder. While PaRuX runs, it serves the pad
 * state PaRuX published in a shared-memory section ("Local\PaRuX.XInput.v1"). Whenever PaRuX is not running (the section is missing
 * or its heartbeat is stale) or a slot is not enabled, every call is forwarded to the real Windows XInput, so a game never loses its
 * controller because of this file. No driver, no kernel code, no hooking of other processes.
 *
 * Layout (little endian, 4-byte aligned), shared with src/PaRuX.App/Services/XInputBridgeLayout.cs:
 *   header: u32 magic 'PXI1', u32 version, u32 heartbeatMs (GetTickCount of the bridge), u32 enabledMask (bit n = slot n)
 *   4 slots of 28 bytes: u32 packet, u32 connected, u16 buttons, u8 lt, u8 rt, i16 lx, ly, rx, ry, u16 rumbleLow, u16 rumbleHigh, u32 reserved
 */
#define WIN32_LEAN_AND_MEAN
#include <windows.h>

#define PXI_MAGIC 0x31495850u /* 'PXI1' */
#define PXI_STALE_MS 2000u
#define PXI_SLOTS 4

typedef struct { DWORD packet; DWORD connected; WORD buttons; BYTE lt, rt; SHORT lx, ly, rx, ry; WORD rumbleLow, rumbleHigh; DWORD reserved; } PxiSlot;
typedef struct { DWORD magic, version, heartbeatMs, enabledMask; PxiSlot slots[PXI_SLOTS]; } PxiSection;

typedef struct { WORD wButtons; BYTE bLeftTrigger, bRightTrigger; SHORT sThumbLX, sThumbLY, sThumbRX, sThumbRY; } XPAD;
typedef struct { DWORD dwPacketNumber; XPAD Gamepad; } XSTATE;
typedef struct { WORD wLeftMotorSpeed, wRightMotorSpeed; } XVIB;
typedef struct { BYTE Type, SubType; WORD Flags; XPAD Gamepad; XVIB Vibration; } XCAPS;
typedef struct { BYTE BatteryType, BatteryLevel; } XBATT;
typedef struct { WORD VirtualKey; WCHAR Unicode; WORD Flags; BYTE UserIndex, HidCode; } XKEY;

static HANDLE g_map;
static PxiSection *g_section;
static HMODULE g_real;
static INIT_ONCE g_once = INIT_ONCE_STATIC_INIT;

typedef DWORD (WINAPI *PFN_GetState)(DWORD, XSTATE *);
typedef DWORD (WINAPI *PFN_SetState)(DWORD, XVIB *);
typedef DWORD (WINAPI *PFN_GetCaps)(DWORD, DWORD, XCAPS *);
typedef void (WINAPI *PFN_Enable)(BOOL);
typedef DWORD (WINAPI *PFN_GetBatt)(DWORD, BYTE, XBATT *);
typedef DWORD (WINAPI *PFN_GetKey)(DWORD, DWORD, XKEY *);
typedef DWORD (WINAPI *PFN_GetAudio)(DWORD, LPWSTR, UINT *, LPWSTR, UINT *);
static PFN_GetState r_GetState, r_GetStateEx;
static PFN_SetState r_SetState;
static PFN_GetCaps r_GetCaps;
static PFN_Enable r_Enable;
static PFN_GetBatt r_GetBatt;
static PFN_GetKey r_GetKey;
static PFN_GetAudio r_GetAudio;

static BOOL CALLBACK InitOnce(PINIT_ONCE once, PVOID param, PVOID *ctx)
{
    WCHAR path[MAX_PATH + 32];
    UINT n;
    (void)once; (void)param; (void)ctx;
    /* The real XInput, by full path from the system directory, so this file never loads itself. */
    n = GetSystemDirectoryW(path, MAX_PATH);
    if (n > 0 && n < MAX_PATH) {
        lstrcatW(path, L"\\xinput1_4.dll");
        g_real = LoadLibraryW(path);
        if (!g_real) { path[n] = 0; lstrcatW(path, L"\\xinput9_1_0.dll"); g_real = LoadLibraryW(path); }
    }
    if (g_real) {
        r_GetState = (PFN_GetState)GetProcAddress(g_real, "XInputGetState");
        r_GetStateEx = (PFN_GetState)GetProcAddress(g_real, (LPCSTR)100);
        r_SetState = (PFN_SetState)GetProcAddress(g_real, "XInputSetState");
        r_GetCaps = (PFN_GetCaps)GetProcAddress(g_real, "XInputGetCapabilities");
        r_Enable = (PFN_Enable)GetProcAddress(g_real, "XInputEnable");
        r_GetBatt = (PFN_GetBatt)GetProcAddress(g_real, "XInputGetBatteryInformation");
        r_GetKey = (PFN_GetKey)GetProcAddress(g_real, "XInputGetKeystroke");
        r_GetAudio = (PFN_GetAudio)GetProcAddress(g_real, "XInputGetAudioDeviceIds");
    }
    return TRUE;
}

static void Init(void) { InitOnceExecuteOnce(&g_once, InitOnce, NULL, NULL); }

/* The section as the bridge published it, or NULL when PaRuX is not running (never creates it). */
static PxiSection *Section(void)
{
    PxiSection *s;
    Init();
    if (!g_section) {
        HANDLE h = OpenFileMappingW(FILE_MAP_ALL_ACCESS, FALSE, L"Local\\PaRuX.XInput.v1");
        if (!h) return NULL;
        s = (PxiSection *)MapViewOfFile(h, FILE_MAP_ALL_ACCESS, 0, 0, sizeof(PxiSection));
        if (!s) { CloseHandle(h); return NULL; }
        g_map = h; g_section = s;
    }
    return g_section;
}

/* True when slot n is served by PaRuX right now. */
static BOOL Ours(DWORD n)
{
    PxiSection *s;
    if (n >= PXI_SLOTS) return FALSE;
    s = Section();
    if (!s || s->magic != PXI_MAGIC || s->version != 1) return FALSE;
    if ((GetTickCount() - s->heartbeatMs) > PXI_STALE_MS) return FALSE;
    return (s->enabledMask & (1u << n)) != 0;
}

static void Fill(XSTATE *out, const PxiSlot *p)
{
    out->dwPacketNumber = p->packet;
    out->Gamepad.wButtons = p->buttons;
    out->Gamepad.bLeftTrigger = p->lt; out->Gamepad.bRightTrigger = p->rt;
    out->Gamepad.sThumbLX = p->lx; out->Gamepad.sThumbLY = p->ly;
    out->Gamepad.sThumbRX = p->rx; out->Gamepad.sThumbRY = p->ry;
}

__declspec(dllexport) DWORD WINAPI XInputGetState(DWORD idx, XSTATE *st)
{
    if (!st) return ERROR_BAD_ARGUMENTS;
    if (Ours(idx)) {
        const PxiSlot *p = &g_section->slots[idx];
        if (!p->connected) return ERROR_DEVICE_NOT_CONNECTED;
        Fill(st, p);
        return ERROR_SUCCESS;
    }
    Init();
    return r_GetState ? r_GetState(idx, st) : ERROR_DEVICE_NOT_CONNECTED;
}

/* Ordinal 100: the same state with the Guide button bit (0x0400) left in. */
__declspec(dllexport) DWORD WINAPI XInputGetStateEx(DWORD idx, XSTATE *st)
{
    if (!st) return ERROR_BAD_ARGUMENTS;
    if (Ours(idx)) return XInputGetState(idx, st);
    Init();
    if (r_GetStateEx) return r_GetStateEx(idx, st);
    return r_GetState ? r_GetState(idx, st) : ERROR_DEVICE_NOT_CONNECTED;
}

__declspec(dllexport) DWORD WINAPI XInputSetState(DWORD idx, XVIB *vib)
{
    if (!vib) return ERROR_BAD_ARGUMENTS;
    if (Ours(idx)) {
        PxiSlot *p = &g_section->slots[idx];
        if (!p->connected) return ERROR_DEVICE_NOT_CONNECTED;
        p->rumbleLow = vib->wLeftMotorSpeed; p->rumbleHigh = vib->wRightMotorSpeed;   /* PaRuX forwards it to the physical pad */
        return ERROR_SUCCESS;
    }
    Init();
    return r_SetState ? r_SetState(idx, vib) : ERROR_DEVICE_NOT_CONNECTED;
}

__declspec(dllexport) DWORD WINAPI XInputGetCapabilities(DWORD idx, DWORD flags, XCAPS *caps)
{
    if (!caps) return ERROR_BAD_ARGUMENTS;
    if (Ours(idx)) {
        if (!g_section->slots[idx].connected) return ERROR_DEVICE_NOT_CONNECTED;
        ZeroMemory(caps, sizeof(*caps));
        caps->Type = 1;          /* XINPUT_DEVTYPE_GAMEPAD */
        caps->SubType = 1;       /* XINPUT_DEVSUBTYPE_GAMEPAD */
        caps->Flags = 0;
        caps->Gamepad.wButtons = 0xF3FF; caps->Gamepad.bLeftTrigger = 255; caps->Gamepad.bRightTrigger = 255;
        caps->Gamepad.sThumbLX = (SHORT)0x7FF0; caps->Gamepad.sThumbLY = (SHORT)0x7FF0; caps->Gamepad.sThumbRX = (SHORT)0x7FF0; caps->Gamepad.sThumbRY = (SHORT)0x7FF0;
        caps->Vibration.wLeftMotorSpeed = 255; caps->Vibration.wRightMotorSpeed = 255;
        return ERROR_SUCCESS;
    }
    Init();
    return r_GetCaps ? r_GetCaps(idx, flags, caps) : ERROR_DEVICE_NOT_CONNECTED;
}

__declspec(dllexport) void WINAPI XInputEnable(BOOL enable)
{
    Init();
    if (r_Enable) r_Enable(enable);
}

__declspec(dllexport) DWORD WINAPI XInputGetBatteryInformation(DWORD idx, BYTE type, XBATT *b)
{
    if (!b) return ERROR_BAD_ARGUMENTS;
    if (Ours(idx)) { b->BatteryType = 1; b->BatteryLevel = 3; return ERROR_SUCCESS; }   /* BATTERY_TYPE_WIRED, level full */
    Init();
    return r_GetBatt ? r_GetBatt(idx, type, b) : ERROR_DEVICE_NOT_CONNECTED;
}

__declspec(dllexport) DWORD WINAPI XInputGetKeystroke(DWORD idx, DWORD reserved, XKEY *k)
{
    if (Ours(idx)) return ERROR_EMPTY;
    Init();
    return r_GetKey ? r_GetKey(idx, reserved, k) : ERROR_DEVICE_NOT_CONNECTED;
}

__declspec(dllexport) DWORD WINAPI XInputGetAudioDeviceIds(DWORD idx, LPWSTR r, UINT *rn, LPWSTR c, UINT *cn)
{
    if (Ours(idx)) { if (rn) *rn = 0; if (cn) *cn = 0; return ERROR_SUCCESS; }
    Init();
    return r_GetAudio ? r_GetAudio(idx, r, rn, c, cn) : ERROR_DEVICE_NOT_CONNECTED;
}

BOOL WINAPI DllMain(HINSTANCE h, DWORD reason, LPVOID reserved)
{
    (void)reserved;
    if (reason == DLL_PROCESS_ATTACH) DisableThreadLibraryCalls(h);
    return TRUE;
}
