#pragma once

#include <cstdio>
// <iostream> was here but nothing uses std streams (only printf), and pulling it in
// drags in libstdc++'s threading layer, which needs mcfgthread headers MinGW-on-Nix
// does not ship. Kept out so the DLL cross-compiles.
#include <Windows.h>
#include <MinHook.h>

typedef void(__cdecl* t_LogInternal)(void*, const char* type, const char* category, int, int, int, const char* message);
typedef HANDLE(WINAPI* t_CreateMutexA)(LPSECURITY_ATTRIBUTES lpMutexAttributes, BOOL bInitialOwner, LPCSTR lpName);

// FUN_008661a0: a __thiscall binary search over a sorted {hash, ptr} table (this in
// ECX, key pointer on the stack). It faults when called on a null container - the crash
// we are chasing. Modelled as __fastcall so the detour receives ECX as its first arg
// (EDX is an unused filler that __fastcall also passes in a register).
typedef void*(__fastcall* t_BinSearch)(void* thisPtr, void* edx, void* key);

// FUN_007354a0: JSON "get member by name". __thiscall(this=object, name=field name). It
// walks the object's member list looking for a member named `name` and returns it (or 0).
// Every field the client reads from a parsed JSON response passes through here, so hooking
// it and logging `name` reveals the exact schema the client expects (e.g. the album's
// photos/_search response fields). Modelled __fastcall so the detour receives ECX (this)
// and the stack arg (name).
typedef void*(__fastcall* t_JsonGetMember)(void* thisPtr, void* edx, const char* name);

t_LogInternal o_LogInternal = nullptr;
t_CreateMutexA o_CreateMutexA = nullptr;
t_BinSearch o_BinSearch = nullptr;
t_JsonGetMember o_JsonGetMember = nullptr;