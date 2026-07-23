#include "pch.h"
#include <new>

// {748744E1-F3A0-40BA-B7B3-938A4734EC96}  image files
static const CLSID CLSID_ShellExtension =
{ 0x748744E1,0xF3A0,0x40BA,{0xB7,0xB3,0x93,0x8A,0x47,0x34,0xEC,0x96} };
// {748744E1-F3A0-40BA-B7B3-938A4734EC97}  PDF files
static const CLSID CLSID_MergePdfExtension =
{ 0x748744E1,0xF3A0,0x40BA,{0xB7,0xB3,0x93,0x8A,0x47,0x34,0xEC,0x97} };

static LONG g_dllRefCount = 0;


static constexpr WCHAR kHex[] = L"0123456789ABCDEF";

static __forceinline bool NeedsEncoding(unsigned char ch) noexcept
{
	return !((ch >= 'A' && ch <= 'Z') || (ch >= 'a' && ch <= 'z') ||
		(ch >= '0' && ch <= '9') || ch == '-' || ch == '_' || ch == '.' || ch == '~');
}

static void BuildGladhenUri(IShellItemArray* psia, WCHAR* uri, size_t uriSize) noexcept
{
	static constexpr WCHAR  kPrefix[] = L"gladhen2:///open?files=";
	static constexpr size_t kPfxLen = _countof(kPrefix) - 1;

	DWORD count = 0;
	if (FAILED(psia->GetCount(&count)) || count == 0) { uri[0] = L'\0'; return; }

	wmemcpy(uri, kPrefix, kPfxLen);
	size_t pos = kPfxLen;

	for (DWORD i = 0; i < count; i++)
	{
		IShellItem* psi = nullptr;
		if (FAILED(psia->GetItemAt(i, &psi))) continue;

		PWSTR path = nullptr;
		if (SUCCEEDED(psi->GetDisplayName(SIGDN_FILESYSPATH, &path)) && path)
		{
			if (i > 0 && pos < uriSize - 1) uri[pos++] = L',';

			char utf8[MAX_PATH * 4];
			int  len = WideCharToMultiByte(CP_UTF8, 0, path, -1,
				utf8, (int)sizeof(utf8), nullptr, nullptr);
			for (int j = 0; j < len - 1 && pos < uriSize - 4; j++)
			{
				unsigned char ch = (unsigned char)utf8[j];
				if (NeedsEncoding(ch)) {
					uri[pos++] = L'%';
					uri[pos++] = kHex[ch >> 4];
					uri[pos++] = kHex[ch & 0xF];
				}
				else {
					uri[pos++] = (WCHAR)ch;
				}
			}
			CoTaskMemFree(path);
		}
		psi->Release();
	}
	uri[pos] = L'\0';
}

static HRESULT GetIconPath(LPWSTR* ppszIcon) noexcept
{
	*ppszIcon = nullptr;
	HMODULE hMod = nullptr;
	if (!GetModuleHandleExW(GET_MODULE_HANDLE_EX_FLAG_FROM_ADDRESS |
		GET_MODULE_HANDLE_EX_FLAG_UNCHANGED_REFCOUNT,
		reinterpret_cast<LPCWSTR>(&GetIconPath), &hMod))
		return E_FAIL;

	WCHAR dir[MAX_PATH];
	if (!GetModuleFileNameW(hMod, dir, MAX_PATH)) return E_FAIL;
	if (auto* s = wcsrchr(dir, L'\\')) *s = L'\0';

	WCHAR exe[MAX_PATH];
	wcscpy_s(exe, dir); wcscat_s(exe, L"\\Gladhen3.exe");
	if (GetFileAttributesW(exe) != INVALID_FILE_ATTRIBUTES) {
		WCHAR buf[MAX_PATH + 4];
		swprintf_s(buf, L"%s,0", exe);
		return SHStrDupW(buf, ppszIcon);
	}

	WCHAR ico[MAX_PATH];
	wcscpy_s(ico, dir); wcscat_s(ico, L"\\Assets\\app.ico");
	if (GetFileAttributesW(ico) != INVALID_FILE_ATTRIBUTES)
		return SHStrDupW(ico, ppszIcon);

	return E_NOTIMPL;
}

class GladhenCommand : public IExplorerCommand
{
	LONG         _ref;
	const CLSID& _clsid;

public:
	explicit GladhenCommand(const CLSID& clsid) noexcept
		: _ref(1), _clsid(clsid)
	{
		InterlockedIncrement(&g_dllRefCount);
	}

	~GladhenCommand() noexcept
	{
		InterlockedDecrement(&g_dllRefCount);
	}

	ULONG __stdcall AddRef() noexcept override
	{
		return (ULONG)InterlockedIncrement(&_ref);
	}

	ULONG __stdcall Release() noexcept override
	{
		LONG r = InterlockedDecrement(&_ref);
		if (!r) delete this;
		return (ULONG)r;
	}

	HRESULT __stdcall QueryInterface(REFIID riid, void** ppv) noexcept override
	{
		if (!ppv) return E_POINTER;
		if (IsEqualIID(riid, IID_IUnknown) || IsEqualIID(riid, IID_IExplorerCommand)) {
			*ppv = static_cast<IExplorerCommand*>(this);
			AddRef();
			return S_OK;
		}
		*ppv = nullptr;
		return E_NOINTERFACE;
	}

	HRESULT __stdcall GetTitle(IShellItemArray*, LPWSTR* p) noexcept override
	{
		return SHStrDupW(L"Open with Gladhen3", p);
	}

	HRESULT __stdcall GetIcon(IShellItemArray*, LPWSTR* p) noexcept override
	{
		return GetIconPath(p);
	}

	HRESULT __stdcall GetToolTip(IShellItemArray*, LPWSTR* p) noexcept override
	{
		*p = nullptr; return E_NOTIMPL;
	}

	HRESULT __stdcall GetCanonicalName(GUID* g) noexcept override
	{
		*g = _clsid; return S_OK;
	}

	HRESULT __stdcall GetState(IShellItemArray*, BOOL, EXPCMDSTATE* s) noexcept override
	{
		*s = ECS_ENABLED; return S_OK;
	}

	HRESULT __stdcall GetFlags(EXPCMDFLAGS* f) noexcept override
	{
		*f = ECF_DEFAULT; return S_OK;
	}

	HRESULT __stdcall EnumSubCommands(IEnumExplorerCommand** e) noexcept override
	{
		*e = nullptr; return E_NOTIMPL;
	}

	HRESULT __stdcall Invoke(IShellItemArray* psia, IBindCtx*) noexcept override
	{
		if (!psia) return E_INVALIDARG;
		WCHAR uri[32768];
		BuildGladhenUri(psia, uri, _countof(uri));
		SHELLEXECUTEINFOW sei = { sizeof(sei) };
		sei.lpVerb = L"open";
		sei.lpFile = uri;
		sei.nShow = SW_SHOWNORMAL;
		ShellExecuteExW(&sei);
		return S_OK;
	}
};

class GladhenClassFactory : public IClassFactory
{
	LONG         _ref;
	const CLSID& _clsid;

public:
	explicit GladhenClassFactory(const CLSID& clsid) noexcept
		: _ref(1), _clsid(clsid)
	{
		InterlockedIncrement(&g_dllRefCount);
	}

	~GladhenClassFactory() noexcept
	{
		InterlockedDecrement(&g_dllRefCount);
	}

	ULONG __stdcall AddRef() noexcept override
	{
		return (ULONG)InterlockedIncrement(&_ref);
	}

	ULONG __stdcall Release() noexcept override
	{
		LONG r = InterlockedDecrement(&_ref);
		if (!r) delete this;
		return (ULONG)r;
	}

	HRESULT __stdcall QueryInterface(REFIID riid, void** ppv) noexcept override
	{
		if (!ppv) return E_POINTER;
		if (IsEqualIID(riid, IID_IUnknown) || IsEqualIID(riid, IID_IClassFactory)) {
			*ppv = static_cast<IClassFactory*>(this);
			AddRef();
			return S_OK;
		}
		*ppv = nullptr;
		return E_NOINTERFACE;
	}

	HRESULT __stdcall CreateInstance(IUnknown* outer, REFIID riid, void** ppv) noexcept override
	{
		if (!ppv) return E_POINTER;
		*ppv = nullptr;
		if (outer) return CLASS_E_NOAGGREGATION;
		auto* obj = new (std::nothrow) GladhenCommand(_clsid);
		if (!obj) return E_OUTOFMEMORY;
		HRESULT hr = obj->QueryInterface(riid, ppv);
		obj->Release();
		return hr;
	}

	HRESULT __stdcall LockServer(BOOL lock) noexcept override
	{
		if (lock) InterlockedIncrement(&g_dllRefCount);
		else      InterlockedDecrement(&g_dllRefCount);
		return S_OK;
	}
};

extern "C" BOOL WINAPI DllMain(HINSTANCE, DWORD, LPVOID) noexcept
{
	return TRUE;
}


STDAPI DllCanUnloadNow()
{
	return (g_dllRefCount == 0) ? S_OK : S_FALSE;
}

STDAPI DllGetClassObject(REFCLSID rclsid, REFIID riid, LPVOID* ppv)
{
	if (!ppv) return E_POINTER;
	*ppv = nullptr;

	const CLSID* target = nullptr;
	if (IsEqualCLSID(rclsid, CLSID_ShellExtension))        target = &CLSID_ShellExtension;
	else if (IsEqualCLSID(rclsid, CLSID_MergePdfExtension)) target = &CLSID_MergePdfExtension;
	else return CLASS_E_CLASSNOTAVAILABLE;

	auto* cf = new (std::nothrow) GladhenClassFactory(*target);
	if (!cf) return E_OUTOFMEMORY;
	HRESULT hr = cf->QueryInterface(riid, ppv);
	cf->Release();
	return hr;
}


static HRESULT WriteRegSz(HKEY root, LPCWSTR subkey, LPCWSTR name, LPCWSTR data) noexcept
{
	HKEY hk;
	LONG r = RegCreateKeyExW(root, subkey, 0, nullptr, 0, KEY_SET_VALUE,
		nullptr, &hk, nullptr);
	if (r != ERROR_SUCCESS) return HRESULT_FROM_WIN32(r);
	DWORD cb = (DWORD)((wcslen(data) + 1) * sizeof(WCHAR));
	r = RegSetValueExW(hk, name, 0, REG_SZ, reinterpret_cast<const BYTE*>(data), cb);
	RegCloseKey(hk);
	return r == ERROR_SUCCESS ? S_OK : HRESULT_FROM_WIN32(r);
}

static HRESULT RegisterClsid(const CLSID& clsid, LPCWSTR description, LPCWSTR dllPath) noexcept
{
	WCHAR id[40];
	StringFromGUID2(clsid, id, 40);

	WCHAR base[80], inproc[120];
	swprintf_s(base, L"CLSID\\%s", id);
	swprintf_s(inproc, L"CLSID\\%s\\InprocServer32", id);

	HRESULT hr = WriteRegSz(HKEY_CLASSES_ROOT, base, nullptr, description);
	if (SUCCEEDED(hr)) hr = WriteRegSz(HKEY_CLASSES_ROOT, inproc, nullptr, dllPath);
	if (SUCCEEDED(hr)) hr = WriteRegSz(HKEY_CLASSES_ROOT, inproc, L"ThreadingModel", L"Apartment");
	return hr;
}

static void UnregisterClsid(const CLSID& clsid) noexcept
{
	WCHAR id[40]; StringFromGUID2(clsid, id, 40);
	WCHAR key[80]; swprintf_s(key, L"CLSID\\%s", id);
	RegDeleteTreeW(HKEY_CLASSES_ROOT, key);
}

STDAPI DllRegisterServer()
{
	WCHAR path[MAX_PATH] = {};
	HMODULE hMod = nullptr;
	GetModuleHandleExW(GET_MODULE_HANDLE_EX_FLAG_FROM_ADDRESS |
		GET_MODULE_HANDLE_EX_FLAG_UNCHANGED_REFCOUNT,
		reinterpret_cast<LPCWSTR>(&DllRegisterServer), &hMod);
	if (!GetModuleFileNameW(hMod, path, MAX_PATH))
		return HRESULT_FROM_WIN32(GetLastError());

	HRESULT hr = RegisterClsid(CLSID_ShellExtension, L"Convert to PDF Shell Extension", path);
	if (SUCCEEDED(hr))
		hr = RegisterClsid(CLSID_MergePdfExtension, L"Merge PDFs Shell Extension", path);
	return hr;
}

STDAPI DllUnregisterServer()
{
	UnregisterClsid(CLSID_ShellExtension);
	UnregisterClsid(CLSID_MergePdfExtension);
	return S_OK;
}

STDAPI DllInstall(BOOL bInstall, LPCWSTR /*pszCmdLine*/) noexcept
{
	HRESULT hr = bInstall ? DllRegisterServer() : DllUnregisterServer();
	if (bInstall && FAILED(hr)) DllUnregisterServer();
	return hr;
}
