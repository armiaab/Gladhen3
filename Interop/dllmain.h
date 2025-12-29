// dllmain.h : Declaration of module class.

#include <atlbase.h>
#include <atlcom.h>
#include <shlwapi.h>
#include <ShlObj_core.h>
#include <windows.h>
#include "resource.h"

#pragma comment(lib, "shlwapi.lib")

class CShellExtensionModule : public ATL::CAtlDllModuleT< CShellExtensionModule >
{
public:
	DECLARE_LIBID(LIBID_ShellExtensionLib)
	DECLARE_REGISTRY_APPID_RESOURCEID(IDR_SHELLEXTENSION, "{748744E1-F3A0-40BA-B7B3-938A4734EC96}")
};

extern class CShellExtensionModule _AtlModule;

// Helper function to build URI - defined in ShellExtension.cpp
void BuildGladhenUri(IShellItemArray* psiItemArray, WCHAR* uri, size_t uriSize);

// Helper function to get the icon path
// Returns the path to the main executable which contains the app icon
inline HRESULT GetGladhenIconPath(LPWSTR* ppszIcon)
{
	// Get the path to this DLL
	WCHAR dllPath[MAX_PATH];
	HMODULE hModule = nullptr;
	
	// Get handle to this DLL
	if (!GetModuleHandleExW(GET_MODULE_HANDLE_EX_FLAG_FROM_ADDRESS | GET_MODULE_HANDLE_EX_FLAG_UNCHANGED_REFCOUNT,
		(LPCWSTR)&GetGladhenIconPath, &hModule))
	{
		*ppszIcon = nullptr;
		return E_FAIL;
	}
	
	// Get the DLL's path
	if (GetModuleFileNameW(hModule, dllPath, MAX_PATH) == 0)
	{
		*ppszIcon = nullptr;
		return E_FAIL;
	}
	
	// Navigate to the app folder (DLL is in app folder)
	// Remove the DLL filename to get the directory
	WCHAR* lastSlash = wcsrchr(dllPath, L'\\');
	if (lastSlash)
	{
		*lastSlash = L'\0';
	}
	
	// Build path to the main executable (which has the icon embedded)
	WCHAR exePath[MAX_PATH];
	wcscpy_s(exePath, MAX_PATH, dllPath);
	wcscat_s(exePath, MAX_PATH, L"\\Gladhen3.exe");
	
	// Check if the exe exists
	if (GetFileAttributesW(exePath) != INVALID_FILE_ATTRIBUTES)
	{
		// Return exe path with icon index 0
		WCHAR iconPath[MAX_PATH + 10];
		swprintf_s(iconPath, MAX_PATH + 10, L"%s,0", exePath);
		return SHStrDupW(iconPath, ppszIcon);
	}
	
	// Fallback: Try to find an .ico file in Assets
	WCHAR iconFilePath[MAX_PATH];
	wcscpy_s(iconFilePath, MAX_PATH, dllPath);
	wcscat_s(iconFilePath, MAX_PATH, L"\\Assets\\app.ico");
	
	if (GetFileAttributesW(iconFilePath) != INVALID_FILE_ATTRIBUTES)
	{
		return SHStrDupW(iconFilePath, ppszIcon);
	}
	
	// Try PNG as last resort (Windows 10+ may support this)
	wcscpy_s(iconFilePath, MAX_PATH, dllPath);
	wcscat_s(iconFilePath, MAX_PATH, L"\\Assets\\Square44x44Logo.targetsize-32.png");
	
	if (GetFileAttributesW(iconFilePath) != INVALID_FILE_ATTRIBUTES)
	{
		return SHStrDupW(iconFilePath, ppszIcon);
	}
	
	// No icon found
	*ppszIcon = nullptr;
	return E_NOTIMPL;
}

// Open with Gladhen3 handler (for image files)
class ATL_NO_VTABLE __declspec(uuid("748744E1-F3A0-40BA-B7B3-938A4734EC96")) CShellExtension :
	public ATL::CComObjectRootEx<ATL::CComSingleThreadModel>,
	public ATL::CComCoClass<CShellExtension, &__uuidof(CShellExtension)>,
	public IExplorerCommand
{
public:
	CShellExtension() {}

	DECLARE_REGISTRY_RESOURCEID(IDR_SHELLEXTENSION)
	DECLARE_NOT_AGGREGATABLE(CShellExtension)

	BEGIN_COM_MAP(CShellExtension)
		COM_INTERFACE_ENTRY(IExplorerCommand)
	END_COM_MAP()

	DECLARE_PROTECT_FINAL_CONSTRUCT()

	HRESULT FinalConstruct() { return S_OK; }
	void FinalRelease() {}

	HRESULT __stdcall GetTitle(IShellItemArray*, LPWSTR* ppszName) override
	{
		return SHStrDupW(L"Open with Gladhen3", ppszName);
	}

	HRESULT __stdcall GetIcon(IShellItemArray*, LPWSTR* ppszIcon) override
	{
		return GetGladhenIconPath(ppszIcon);
	}

	HRESULT __stdcall GetToolTip(IShellItemArray*, LPWSTR* ppszInfotip) override
	{
		*ppszInfotip = nullptr;
		return E_NOTIMPL;
	}

	HRESULT __stdcall GetCanonicalName(GUID* pguidCommandName) override
	{
		*pguidCommandName = __uuidof(CShellExtension);
		return S_OK;
	}

	HRESULT __stdcall GetState(IShellItemArray*, BOOL, EXPCMDSTATE* pCmdState) override
	{
		*pCmdState = ECS_ENABLED;
		return S_OK;
	}

	HRESULT __stdcall Invoke(IShellItemArray* psiItemArray, IBindCtx*) override
	{
		if (!psiItemArray) return E_INVALIDARG;

		WCHAR uri[32768];
		BuildGladhenUri(psiItemArray, uri, _countof(uri));

		SHELLEXECUTEINFOW sei = { sizeof(sei) };
		sei.lpVerb = L"open";
		sei.lpFile = uri;
		sei.nShow = SW_SHOWNORMAL;
		ShellExecuteExW(&sei);

		return S_OK;
	}

	HRESULT __stdcall GetFlags(EXPCMDFLAGS* pFlags) override
	{
		*pFlags = ECF_DEFAULT;
		return S_OK;
	}

	HRESULT __stdcall EnumSubCommands(IEnumExplorerCommand** ppEnum) override
	{
		*ppEnum = nullptr;
		return E_NOTIMPL;
	}
};

// Open with Gladhen3 handler (for PDF files) - same functionality, different CLSID
class ATL_NO_VTABLE __declspec(uuid("748744E1-F3A0-40BA-B7B3-938A4734EC97")) CMergePdfExtension :
	public ATL::CComObjectRootEx<ATL::CComSingleThreadModel>,
	public ATL::CComCoClass<CMergePdfExtension, &__uuidof(CMergePdfExtension)>,
	public IExplorerCommand
{
public:
	CMergePdfExtension() {}

	DECLARE_REGISTRY_RESOURCEID(IDR_MERGEPDFEXTENSION)
	DECLARE_NOT_AGGREGATABLE(CMergePdfExtension)

	BEGIN_COM_MAP(CMergePdfExtension)
		COM_INTERFACE_ENTRY(IExplorerCommand)
	END_COM_MAP()

	DECLARE_PROTECT_FINAL_CONSTRUCT()

	HRESULT FinalConstruct() { return S_OK; }
	void FinalRelease() {}

	HRESULT __stdcall GetTitle(IShellItemArray*, LPWSTR* ppszName) override
	{
		return SHStrDupW(L"Open with Gladhen3", ppszName);
	}

	HRESULT __stdcall GetIcon(IShellItemArray*, LPWSTR* ppszIcon) override
	{
		return GetGladhenIconPath(ppszIcon);
	}

	HRESULT __stdcall GetToolTip(IShellItemArray*, LPWSTR* ppszInfotip) override
	{
		*ppszInfotip = nullptr;
		return E_NOTIMPL;
	}

	HRESULT __stdcall GetCanonicalName(GUID* pguidCommandName) override
	{
		*pguidCommandName = __uuidof(CMergePdfExtension);
		return S_OK;
	}

	HRESULT __stdcall GetState(IShellItemArray*, BOOL, EXPCMDSTATE* pCmdState) override
	{
		*pCmdState = ECS_ENABLED;
		return S_OK;
	}

	HRESULT __stdcall Invoke(IShellItemArray* psiItemArray, IBindCtx*) override
	{
		if (!psiItemArray) return E_INVALIDARG;

		WCHAR uri[32768];
		BuildGladhenUri(psiItemArray, uri, _countof(uri));

		SHELLEXECUTEINFOW sei = { sizeof(sei) };
		sei.lpVerb = L"open";
		sei.lpFile = uri;
		sei.nShow = SW_SHOWNORMAL;
		ShellExecuteExW(&sei);

		return S_OK;
	}

	HRESULT __stdcall GetFlags(EXPCMDFLAGS* pFlags) override
	{
		*pFlags = ECF_DEFAULT;
		return S_OK;
	}

	HRESULT __stdcall EnumSubCommands(IEnumExplorerCommand** ppEnum) override
	{
		*ppEnum = nullptr;
		return E_NOTIMPL;
	}
};

OBJECT_ENTRY_AUTO(__uuidof(CShellExtension), CShellExtension)
OBJECT_ENTRY_AUTO(__uuidof(CMergePdfExtension), CMergePdfExtension)