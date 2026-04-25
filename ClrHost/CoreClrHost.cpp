/*
.NET Runtime Loader using hostfxr
*/

#pragma warning(disable : 26446) // Prefer to use gsl::at() instead of unchecked subscript operator
#pragma warning(disable : 26490) // Don't use reinterpret_cast
#pragma warning(disable : 26485) // Expression 'array-name': No array to pointer decay
#pragma warning(disable : 26472) // Don't use a static_cast for arithmetic conversions.

#include <stdio.h>
#include <stdlib.h>
#include <string.h>
#include <array>
#include <string>
#include <vector>
#include <comdef.h>
#include <atlbase.h>
#include <atlcomcli.h>
#include <windows.h>

#include <nethost.h>
#include <hostfxr.h>
#include <coreclr_delegates.h>

#ifdef max
#undef max
#endif

#ifdef min
#undef min
#endif

// ---------------------------------------------------------------------------
// Factory delegate -- managed side must have:
//   [UnmanagedFunctionPointer(CallingConvention.StdCall)]
//   public delegate int CreatewwDotnetBridgeByRefDelegate(out IntPtr ppDispatch)
//   public static int CreatewwDotnetBridgeByRef(out IntPtr ppDispatch)
// ---------------------------------------------------------------------------
typedef HRESULT(__stdcall *createWwDotnetBridgeHandler)(IDispatch **ppDispatch);

// ---------------------------------------------------------------------------
// Module-level state
// ---------------------------------------------------------------------------

static HMODULE g_hostfxrModule = nullptr;
static hostfxr_handle g_hostContext = nullptr;
static load_assembly_and_get_function_pointer_fn g_loadAssembly = nullptr;
static createWwDotnetBridgeHandler g_createBridge = nullptr;

struct AddressRangeInfo
{
	unsigned long long base;
	unsigned long long size;
};

// Handles for temporary VA reservations covering all user-mode VA above 2 GB.
//
// The JIT noway_assert
//
//   noway_assert(static_cast<int>(reinterpret_cast<intptr_t>(addr)) == (ssize_t)addr)
//
// fires for ANY address where the lower 32 bits have their high bit set AND
// the upper 32 bits don't sign-extend that correctly.  On x64 Windows the only
// user-mode range that is entirely safe is [0, 0x7FFFFFFF] (below 2 GB).
//
// Strategy: before hostfxr starts CoreCLR, walk all free regions from 2 GB to
// the top of user-mode VA and reserve them all (MEM_RESERVE / PAGE_NOACCESS —
// pure VA cost, no physical pages committed).  With the 2–4 GB band occupied by
// existing DLLs AND the above-4 GB band now blocked by our reservations, all of
// CoreCLR's null-base VirtualAlloc calls must satisfy from the only remaining
// free space: below 2 GB.  Sub-2 GB addresses always satisfy the assert.
//
// After the runtime has committed its initial heaps and loader-heap regions the
// reservations are released so native DLLs and heap growth above 2 GB can
// proceed normally.  By that point all JIT-critical objects (method tables,
// virtual call stubs, type handles) have been placed in safe sub-2 GB memory.
static std::vector<void *> g_dangerZoneReservations;

// ---------------------------------------------------------------------------
// Misc helpers
// ---------------------------------------------------------------------------

static void PinThisModule() noexcept
{
	static HMODULE hPinned = nullptr;
	if (hPinned)
		return;
	GetModuleHandleExW(
		GET_MODULE_HANDLE_EX_FLAG_FROM_ADDRESS | GET_MODULE_HANDLE_EX_FLAG_PIN,
		reinterpret_cast<LPCWSTR>(&PinThisModule),
		&hPinned);
}

static std::wstring AnsiToWide(const char *ansi)
{
	if (!ansi || !*ansi)
		return {};
	const int sz = MultiByteToWideChar(CP_ACP, 0, ansi, -1, nullptr, 0);
	std::wstring result(static_cast<size_t>(sz) - 1, L'\0');
	MultiByteToWideChar(CP_ACP, 0, ansi, -1, &result[0], sz);
	return result;
}

static std::string WideToAnsi(const wchar_t *wide)
{
	if (!wide || !*wide)
		return {};
	const int sz = WideCharToMultiByte(CP_ACP, 0, wide, -1, nullptr, 0, nullptr, nullptr);
	std::string result(static_cast<size_t>(sz) - 1, '\0');
	WideCharToMultiByte(CP_ACP, 0, wide, -1, &result[0], sz, nullptr, nullptr);
	return result;
}

static std::wstring GetThisModulePath()
{
	std::array<wchar_t, MAX_PATH> path{};
	HMODULE hm = nullptr;
	GetModuleHandleExW(
		GET_MODULE_HANDLE_EX_FLAG_FROM_ADDRESS | GET_MODULE_HANDLE_EX_FLAG_UNCHANGED_REFCOUNT,
		reinterpret_cast<LPCWSTR>(&GetThisModulePath),
		&hm);
	GetModuleFileNameW(hm, std::data(path), MAX_PATH);
	return std::wstring(std::data(path));
}

static std::wstring GetDirectoryPath(const std::wstring &path)
{
	if (path.empty())
		return {};

	const size_t separator = path.find_last_of(L"\\/");
	if (separator == std::wstring::npos)
		return {};

	return path.substr(0, separator);
}

static void SetError(char *buf, DWORD *size, const char *msg) noexcept
{
	if (!buf || !size)
		return;
	const DWORD cap = (*size > 0) ? *size : 512;
	strncpy_s(buf, cap, msg, _TRUNCATE);
	*size = static_cast<DWORD>(strlen(buf));
}

static std::string FormatBytes(unsigned long long value)
{
	std::array<char, 80> msg{};
	if (value >= 1024ULL * 1024ULL * 1024ULL * 1024ULL)
		sprintf_s(std::data(msg), std::size(msg), "%llu bytes (%.1f TiB)", value, static_cast<double>(value) / (1024.0 * 1024.0 * 1024.0 * 1024.0));
	else if (value >= 1024ULL * 1024ULL * 1024ULL)
		sprintf_s(std::data(msg), std::size(msg), "%llu bytes (%.1f GiB)", value, static_cast<double>(value) / (1024.0 * 1024.0 * 1024.0));
	else if (value >= 1024ULL * 1024ULL)
		sprintf_s(std::data(msg), std::size(msg), "%llu bytes (%.1f MiB)", value, static_cast<double>(value) / (1024.0 * 1024.0));
	else
		sprintf_s(std::data(msg), std::size(msg), "%llu bytes (%.1f KiB)", value, static_cast<double>(value) / 1024.0);
	return std::string(std::data(msg));
}

static void AppendLine(std::string &text, const std::string &line)
{
	text += line;
	text += "\r\n";
}

static void AppendLine(std::string &text, const char *line)
{
	text += line;
	text += "\r\n";
}

// ---------------------------------------------------------------------------
// CoreClrLoad  (internal)
//
// runtimeConfigPath: full path to a .runtimeconfig.json that specifies the
//   framework name and minimum version, e.g.:
//     { "runtimeOptions": { "framework": {
//         "name": "Microsoft.WindowsDesktop.App", "version": "10.0.0" } } }
// ---------------------------------------------------------------------------
static BOOL CoreClrLoad(const char *runtimeConfigPath, const char *assemblyPath, char *errorMessage, DWORD *size)
{
	PinThisModule();

	if (g_hostContext != nullptr)
		return TRUE;

	if (!runtimeConfigPath || !*runtimeConfigPath)
	{
		SetError(errorMessage, size,
				 "CoreClrLoad: runtimeConfigPath must be the full path to a .runtimeconfig.json file.");
		return FALSE;
	}

	std::array<wchar_t, MAX_PATH> hostfxrPath{};
	size_t hostfxrPathLen = MAX_PATH;

	// Disable rel32 (32-bit PC-relative) JIT encodings on AMD64.
	// When coreclr.dll loads outside the 0–2 GB preferred range the JIT emitter
	// may emit rel32 references to data/code that sits in the 2–4 GB zone.
	// Those addresses do not fit in a signed 32-bit immediate and trigger
	// noway_assert failures deep in clrjit.  Setting JitEnableOptionalRelocs=0
	// forces _fAllowRel32 to FALSE for every method compilation, making the JIT
	// fall back to jump-stubs and absolute encodings for all 64-bit targets.
	SetEnvironmentVariableA("DOTNET_JitEnableOptionalRelocs", "0");

	// Block all free VA from 2 GB to the top of user space before hostfxr
	// initialises CoreCLR.  This forces every null-base VirtualAlloc inside the
	// runtime to land below 2 GB — the only address range where the JIT's
	// 32-bit sign-extend immediates are unconditionally safe.
	//
	// Why not just block 2–4 GB?  In VFPA that band is already fully occupied by
	// Windows/FoxPro DLLs, so CoreCLR cannot land there anyway.  Without this
	// reservation it allocates above 4 GB instead — which also triggers the same
	// noway_assert (0x100000000 cast to int32 = 0, which != 0x100000000 ssize_t).
	// Blocking everything above 2 GB closes both windows simultaneously.
	ReserveDangerZone();

	const int rcHostfxr = get_hostfxr_path(std::data(hostfxrPath), &hostfxrPathLen, nullptr);
	if (rcHostfxr != 0)
	{
		std::array<char, 256> msg{};
		sprintf_s(std::data(msg), std::size(msg),
				  "CoreClrLoad: get_hostfxr_path failed (0x%08X). Install the .NET runtime or set the DOTNET_ROOT environment variable.",
				  static_cast<unsigned>(rcHostfxr));
		SetError(errorMessage, size, std::data(msg));
		return FALSE;
	}

	g_hostfxrModule = LoadLibraryW(std::data(hostfxrPath));
	if (!g_hostfxrModule)
	{
		std::array<char, 512> msg{};
		sprintf_s(std::data(msg), std::size(msg),
				  "CoreClrLoad: LoadLibrary(hostfxr) failed (error %lu).", GetLastError());
		SetError(errorMessage, size, std::data(msg));
		GetProcAddress(g_hostfxrModule, "hostfxr_initialize_for_runtime_config"));
		const auto pfnGetDelegate = reinterpret_cast<hostfxr_get_runtime_delegate_fn>(
			GetProcAddress(g_hostfxrModule, "hostfxr_get_runtime_delegate"));
		const auto pfnClose = reinterpret_cast<hostfxr_close_fn>(
			GetProcAddress(g_hostfxrModule, "hostfxr_close"));

		const auto pfnSetRuntimePropertyValue = reinterpret_cast<hostfxr_set_runtime_property_value_fn>(
			GetProcAddress(g_hostfxrModule, "hostfxr_set_runtime_property_value"));

		if (!pfnInit || !pfnGetDelegate || !pfnClose)
		{
			SetError(errorMessage, size,
					 "CoreClrLoad: Missing required exports in hostfxr.dll. .NET 5 or later is required.");
			return FALSE;
		}

		const std::wstring thisModulePath = GetThisModulePath();
		const std::wstring configPathW = AnsiToWide(runtimeConfigPath);

		hostfxr_initialize_parameters initParams{};
		initParams.size = sizeof(initParams);
		initParams.host_path = thisModulePath.c_str();
		initParams.dotnet_root = nullptr;

		hostfxr_handle hCtx = nullptr;
		const int32_t rcInit = pfnInit(configPathW.c_str(), &initParams, &hCtx);

		if (rcInit != 0 && rcInit != 1)
		{
			std::array<char, 512> msg{};
			sprintf_s(std::data(msg), std::size(msg),
					  "CoreClrLoad: hostfxr_initialize_for_runtime_config failed (0x%08X). ",
					  static_cast<unsigned>(rcInit));
			SetError(errorMessage, size, std::data(msg));
			const std::wstring configuredAppPaths = GetDirectoryPath(AnsiToWide(assemblyPath));
			if (!configuredAppPaths.empty())
			{
				if (!pfnSetRuntimePropertyValue)
				{
					SetError(errorMessage, size,
							 "CoreClrLoad: hostfxr_set_runtime_property_value is not available, so additional assembly probing paths cannot be configured.");
					if (hCtx)
						pfnClose(hCtx);
					return FALSE;
				}

				const int32_t rcSetAppPaths = pfnSetRuntimePropertyValue(hCtx, L"APP_PATHS", configuredAppPaths.c_str());
				if (rcSetAppPaths != 0)
				{
					std::array<char, 512> msg{};
					sprintf_s(std::data(msg), std::size(msg),
							  "CoreClrLoad: hostfxr_set_runtime_property_value(APP_PATHS) failed (0x%08X).",
							  static_cast<unsigned>(rcSetAppPaths));
					SetError(errorMessage, size, std::data(msg));
					if (hCtx)
						pfnClose(hCtx);
					return FALSE;
				}
			}

			void *loadFn = nullptr;
			const int32_t rcDelegate = pfnGetDelegate(
				hCtx, hdt_load_assembly_and_get_function_pointer, &loadFn);

			if (rcDelegate != 0 || !loadFn)
			{
				std::array<char, 256> msg{};
				sprintf_s(std::data(msg), std::size(msg),
						  "CoreClrLoad: hostfxr_get_runtime_delegate failed (0x%08X).",
						  static_cast<unsigned>(rcDelegate));
				SetError(errorMessage, size, std::data(msg));
				pfnClose(hCtx);
				return FALSE;
			}

			// CoreCLR has now committed all its initial heaps and code regions.  Release
			// the danger-zone reservations so the VA space is available for native DLLs.
			ReleaseDangerZone();

			g_hostContext = hCtx;
			g_loadAssembly = static_cast<load_assembly_and_get_function_pointer_fn>(loadFn);
			g_createBridge = nullptr;
			return TRUE;
		}

		// ---------------------------------------------------------------------------
		// CoreClrUnload  (exported @116)
		//
		// Closes the hostfxr context and removes the temp runtimeconfig file.
		// The .NET runtime itself cannot be unloaded once started.
		// C26440: noexcept -- function does not throw
		// ---------------------------------------------------------------------------
		DWORD WINAPI CoreClrUnload() noexcept
		{
			if (g_hostContext && g_hostfxrModule)
			{
				const auto pfnClose = reinterpret_cast<hostfxr_close_fn>(
					GetProcAddress(g_hostfxrModule, "hostfxr_close"));
				if (pfnClose)
					pfnClose(g_hostContext);
			}

			g_hostContext = nullptr;
			g_loadAssembly = nullptr;
			g_createBridge = nullptr;

			return 0;
		}

		// ---------------------------------------------------------------------------
		// CoreClrCreateInstanceFrom  (exported @115)
		//
		// Parameters
		//   runtimeConfigPath  Full path to the .runtimeconfig.json that specifies
		//                      the .NET framework and version to load. Example:
		//                        C:\MyApp\wwDotNetBridge.runtimeconfig.json
		//   assemblyPath       Full path to wwDotNetBridge.dll.
		//   errorMessage       Caller-supplied buffer, recommended >= 512 chars.
		//   dwErrorSize        In: capacity.  Out: length of error text written.
		//
		// Calling convention: __stdcall -- required for 64-bit FoxPro (VFPA).
		// ---------------------------------------------------------------------------
		IDispatch *WINAPI CoreClrCreateInstanceFrom(
			const char *runtimeConfigPath,
			const char *assemblyPath,
			char *errorMessage,
			DWORD *dwErrorSize)
		{
			if (!g_hostContext)
			{
				if (!CoreClrLoad(runtimeConfigPath, assemblyPath, errorMessage, dwErrorSize))
					return nullptr;
			}

			try
			{
				if (!g_createBridge)
				{
					const std::wstring asmPathW = AnsiToWide(assemblyPath);

					const wchar_t *typeName = L"Westwind.WebConnection.wwDotnetBridgeFactory, wwDotNetBridge";
					const wchar_t *methodName = L"CreatewwDotnetBridgeByRef";
					const wchar_t *delegateTypeName = L"Westwind.WebConnection.CreatewwDotnetBridgeByRefDelegate, wwDotNetBridge";

					void *fnPtr = nullptr;
					const int32_t rc = g_loadAssembly(
						asmPathW.c_str(),
						typeName,
						methodName,
						delegateTypeName,
						nullptr,
						&fnPtr);

					if (rc != 0 || !fnPtr)
					{
						std::array<char, 768> msg{};
						sprintf_s(std::data(msg), std::size(msg),
								  "CoreClrCreateInstanceFrom: load_assembly_and_get_function_pointer failed (0x%08X). The managed assembly or one of its dependencies could not be loaded by CoreCLR, or the factory method does not match CreatewwDotnetBridgeByRefDelegate.",
								  static_cast<unsigned>(rc));
						SetError(errorMessage, dwErrorSize, std::data(msg));
						return nullptr;
					}

					g_createBridge = static_cast<createWwDotnetBridgeHandler>(fnPtr);
				}

				IDispatch *pDisp = nullptr;
				const HRESULT hr = g_createBridge(&pDisp);
				if (FAILED(hr) || !pDisp)
				{
					std::array<char, 512> msg{};
					sprintf_s(std::data(msg), std::size(msg),
							  "CoreClrCreateInstanceFrom: managed factory returned 0x%08X.",
							  static_cast<unsigned>(hr));
					SetError(errorMessage, dwErrorSize, std::data(msg));
					return nullptr;
				}

				return pDisp;
			}
			catch (const std::exception &ex)
			{
				SetError(errorMessage, dwErrorSize, ex.what());
				return nullptr;
			}
			catch (...)
			{
				SetError(errorMessage, dwErrorSize,
						 "CoreClrCreateInstanceFrom: unknown exception.");
				return nullptr;
			}
		}
