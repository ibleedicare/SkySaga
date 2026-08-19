#define WIN32_LEAN_AND_MEAN

#include <cstdarg>
#include <cstdlib>
#include <cstring>

#include "Patches.h"

// The injected console shows up as a Wine console window, which is fine when a human is
// watching but invisible to anything scripting the run. So every message also goes to a
// log file, flushed per line. Path from SKYSAGA_PATCH_LOG (a Windows path), else
// patches.log in the client's working directory.
static FILE* g_log = nullptr;

static void OpenLog()
{
	const char* path = getenv("SKYSAGA_PATCH_LOG");

	if (path == nullptr || path[0] == '\0')
		path = "patches.log";

	// Append, not truncate: a run can spawn several client processes (relaunch, frontend
	// retries), and truncating would let a later one wipe the session we care about.
	g_log = fopen(path, "a");

	if (g_log != nullptr)
		fprintf(g_log, "\n==== patches attached (pid %lu) ====\n", (unsigned long)GetCurrentProcessId());
}

static void PatchLog(const char* fmt, ...)
{
	va_list args;

	va_start(args, fmt);
	vprintf(fmt, args);
	va_end(args);

	if (g_log != nullptr)
	{
		va_start(args, fmt);
		vfprintf(g_log, fmt, args);
		va_end(args);

		fflush(g_log);
	}
}

void ShowConsole()
{
	AllocConsole();

	FILE* file;
	freopen_s(&file, "CONOUT$", "w", stdout);
}

// Comma-separated substrings from SKYSAGA_LOG_FILTER; if set, only log lines whose
// category or message contains one of them. Keeps in-world volume low (full logging
// floods the file and freezes the client), while still catching e.g. "Photo".
static bool LogMatchesFilter(const char* category, const char* message)
{
	const char* filter = getenv("SKYSAGA_LOG_FILTER");

	if (filter == nullptr || filter[0] == '\0')
		return true; // no filter -> log everything

	char buf[256];
	size_t n = 0;

	for (const char* p = filter; ; ++p)
	{
		if (*p == ',' || *p == '\0')
		{
			buf[n] = '\0';

			if (n > 0)
			{
				if (category != nullptr && strstr(category, buf) != nullptr) return true;
				if (message != nullptr && strstr(message, buf) != nullptr) return true;
			}

			n = 0;

			if (*p == '\0')
				break;
		}
		else if (n < sizeof(buf) - 1)
		{
			buf[n++] = *p;
		}
	}

	return false;
}

void __cdecl hk_LogInternal(void* self, const char* type, const char* category, int a, int b, int c, const char* message)
{
	// type/category are short tags (e.g. "Error", "Inventory"); prefix them so the log is
	// filterable, then pass the message through verbatim.
	if (LogMatchesFilter(category, message))
		PatchLog("[log] %s/%s %s", type ? type : "?", category ? category : "?", message ? message : "");

	// By default forward to the real logger so the client behaves normally (and so a crash
	// inside it still reproduces for diagnosis). SKYSAGA_LOG_BYPASS=1 skips the original -
	// the workaround that dodges the equip crash but leaves the game's own log silent.
	const char* bypass = getenv("SKYSAGA_LOG_BYPASS");

	if (o_LogInternal != nullptr && !(bypass != nullptr && bypass[0] == '1'))
		o_LogInternal(self, type, category, a, b, c, message);
}

// Passive crash catcher. Fires on any exception; on an access violation it records the
// faulting instruction, the registers, and an EBP-chain backtrace. The client is built
// with frame pointers (every function opens PUSH EBP; MOV EBP,ESP), so walking [EBP] and
// reading [EBP+4] recovers the return-address chain - a real backtrace, which is what
// identifies the subsystem behind the equip crash. This is a Win32 API, not a debugger,
// so DeltaMAX's anti-debug is unaffected.
static LONG WINAPI VehHandler(EXCEPTION_POINTERS* ep)
{
	if (ep->ExceptionRecord->ExceptionCode != EXCEPTION_ACCESS_VIOLATION)
		return EXCEPTION_CONTINUE_SEARCH;

	CONTEXT* ctx = ep->ContextRecord;

	PatchLog("\n[VEH] access violation at eip=%08x %s addr=%08x\n",
		(unsigned)ctx->Eip,
		ep->ExceptionRecord->ExceptionInformation[0] ? "writing" : "reading",
		(unsigned)ep->ExceptionRecord->ExceptionInformation[1]);

	PatchLog("[VEH] eax=%08x ebx=%08x ecx=%08x edx=%08x esi=%08x edi=%08x ebp=%08x esp=%08x\n",
		(unsigned)ctx->Eax, (unsigned)ctx->Ebx, (unsigned)ctx->Ecx, (unsigned)ctx->Edx,
		(unsigned)ctx->Esi, (unsigned)ctx->Edi, (unsigned)ctx->Ebp, (unsigned)ctx->Esp);

	// Frame 0's return address is at [EBP+4] because EBP is already set at the fault point.
	unsigned ebp = ctx->Ebp;

	for (int i = 0; i < 16 && ebp != 0 && !IsBadReadPtr((void*)ebp, 8); i++)
	{
		unsigned ret = *(unsigned*)(ebp + 4);

		PatchLog("[VEH]   #%02d ret=%08x\n", i, ret);

		unsigned next = *(unsigned*)ebp;

		if (next <= ebp)
			break;

		ebp = next;
	}

	if (g_log != nullptr)
		fflush(g_log);

	return EXCEPTION_CONTINUE_SEARCH;
}

// Detour for FUN_008661a0. Only logs when the container pointer is obviously invalid
// (a real one is a heap/stack address, never a tiny integer), which is exactly the
// about-to-fault case, so the hot path stays a single compare. __builtin_return_address
// is the caller inside the game - the whole reason for this hook, since the faulting
// helper has 30+ call sites and only the caller identifies which subsystem broke.
static int g_binSearchLogged = 0;

void* __fastcall hk_BinSearch(void* thisPtr, void* edx, void* key)
{
	if ((uintptr_t)thisPtr < 0x10000 && g_binSearchLogged < 32)
	{
		g_binSearchLogged++;

		void* caller = __builtin_return_address(0);

		unsigned int keyVal = 0;
		if ((uintptr_t)key >= 0x10000)
			keyVal = *(unsigned int*)key;

		PatchLog("[hook] binsearch on null container: this=%p key=%p (*key=0x%08x) caller=%p\n",
			thisPtr, key, keyVal, caller);
	}

	return o_BinSearch(thisPtr, edx, key);
}

// Detour for FUN_007354a0 (JSON get-member-by-name). Logs the field name every time the
// client looks up a JSON member, which spells out the schema it expects from any response.
// This is hot (every field of every parsed response), so writes go straight to the file
// buffer with a flush only every 256 calls - no vprintf, no per-call fflush, or the sheer
// volume during a response burst would stall the client (the LogInternal freeze lesson).
// Off unless SKYSAGA_HOOK_JSON=1. Open the album with it on, then grep the log for the
// fields read right after the photos/_search POST to recover the exact per-photo schema.
static unsigned g_jsonCount = 0;

void* __fastcall hk_JsonGetMember(void* thisPtr, void* edx, const char* name)
{
	if (g_log != nullptr && name != nullptr && !IsBadStringPtrA(name, 128))
	{
		fprintf(g_log, "[json] %s\n", name);

		if ((++g_jsonCount & 0xFF) == 0)
			fflush(g_log);
	}

	return o_JsonGetMember(thisPtr, edx, name);
}

// Detour for FUN_0087a630, the interaction gate. The HUD (FUN_007fe280) only offers
// InteractMode when this returns non-zero AND the component's byte at +0x38 is set; otherwise
// it falls back to the pickup prompt ("Your inventory is full"). Dumping the component's live
// fields here says which of our synced flags actually landed and what the interaction angles
// are - the thing a static decompile of this function could not tell us.
//
// Rate-limited: this runs every frame the player looks at an interactable.
HANDLE WINAPI hk_CreateMutexA(LPSECURITY_ATTRIBUTES lpMutexAttributes, BOOL bInitialOwner, LPCSTR lpName)
{
	HANDLE hResult = o_CreateMutexA(lpMutexAttributes, bInitialOwner, lpName);

	if (_stricmp(lpName, "BlitzTechAppInstanceMutex") == 0)
	{
		// The mutex is created just after the packer handed control to the real image
		// (FUN_00412060), so this is the first safe point to patch and hook the unpacked
		// code. The 10-byte NOP at 0x412252 disables the log-file-init the game would
		// otherwise do; hooking its internal logger replaces it.
		OpenLog();

		// Fires first so an access violation is captured even if it happens before/around
		// the hooks. FIRST_HANDLER (1) so we see it before the game's own handlers.
		AddVectoredExceptionHandler(1, VehHandler);

		PatchLog("[patches] attached; VEH installed; hooking internal logger\n");

		WriteProcessMemory(GetCurrentProcess(), LPVOID(0x412252), "\x90\x90\x90\x90\x90\x90\x90\x90\x90\x90", 10, nullptr);

		MH_STATUS status;

		if ((status = MH_CreateHook((LPVOID)0x8547F0, hk_LogInternal, (LPVOID*)&o_LogInternal)) != MH_OK)
			PatchLog("[patches] LogInternal hook failed - %s\n", MH_StatusToString(status));

		MH_EnableHook((LPVOID)0x8547F0);

		// The binary-search helper (0x8661A0) is on the hot hash-lookup path, called
		// thousands of times a second; hooking it destabilises startup. Off unless
		// SKYSAGA_HOOK_BINSEARCH=1 - only worth it if the engine log doesn't already name
		// what's null at the equip crash.
		const char* hookBinSearch = getenv("SKYSAGA_HOOK_BINSEARCH");

		if (hookBinSearch != nullptr && hookBinSearch[0] == '1')
		{
			if ((status = MH_CreateHook((LPVOID)0x8661A0, (LPVOID)hk_BinSearch, (LPVOID*)&o_BinSearch)) != MH_OK)
				PatchLog("[patches] BinSearch hook failed - %s\n", MH_StatusToString(status));

			MH_EnableHook((LPVOID)0x8661A0);

			PatchLog("[patches] BinSearch hook enabled (experimental)\n");
		}

		// The JSON member-accessor hook (0x7354A0) dumps every field name the client reads
		// from parsed responses - the ground-truth schema for whatever RPC we are chasing
		// (currently the photos/_search album response). Also hot, so opt-in via
		// SKYSAGA_HOOK_JSON=1; leave off for normal play.
		const char* hookJson = getenv("SKYSAGA_HOOK_JSON");

		if (hookJson != nullptr && hookJson[0] == '1')
		{
			if ((status = MH_CreateHook((LPVOID)0x7354A0, (LPVOID)hk_JsonGetMember, (LPVOID*)&o_JsonGetMember)) != MH_OK)
				PatchLog("[patches] JsonGetMember hook failed - %s\n", MH_StatusToString(status));

			MH_EnableHook((LPVOID)0x7354A0);

			PatchLog("[patches] JsonGetMember hook enabled (schema capture)\n");
		}

		// NOTE: hooking the interaction gate FUN_0087a630 (0x87A630) was tried and REMOVED.
		// It crashed the client both as a typed __fastcall detour and as a register-preserving
		// naked detour, so the problem is the target rather than the calling convention: it is
		// a very hot function (runs per frame per targeted entity) and its prologue does not
		// survive a 5-byte patch. Read it statically with capstone instead.
	}

	return hResult;
}

BOOL APIENTRY DllMain(HMODULE hModule, DWORD ul_reason_for_call, LPVOID lpReserved)
{
	if (ul_reason_for_call == DLL_PROCESS_ATTACH)
	{
		DisableThreadLibraryCalls(hModule);

		ShowConsole();

		MH_Initialize();

		MH_STATUS status;

		if ((status = MH_CreateHookApi("kernel32.dll", "CreateMutexA", hk_CreateMutexA, (LPVOID*)&o_CreateMutexA)) != MH_OK)
			printf("Create Hook Failed! - %s\n", MH_StatusToString(status));

		MH_EnableHook(MH_ALL_HOOKS);

		return TRUE;
	}

	if (ul_reason_for_call == DLL_PROCESS_DETACH)
	{
		// system("pause");

		// FreeConsole();

		return TRUE;
	}

	return FALSE;
}