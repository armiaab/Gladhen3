// ShellExtension.cpp - the IExplorerCommand handlers behind Gladhen3's context menu.
//
// The manifest registers these classes as a com:SurrogateServer, so this DLL is not loaded
// into Explorer: COM spins up a dllhost.exe, maps us into it, asks for a title/icon/state,
// and tears the process down a few seconds after the menu closes. Everything here is
// therefore written for a process that exists for a few milliseconds at a time:
//
//   * nothing here uses the CRT beyond the DLL startup glue, and /EHs-c- /GR- keep
//     VCRUNTIME140_1.dll - the C++ exception runtime - out of the surrogate entirely;
//   * shell32 is resolved on demand, so merely *showing* the menu does not pull 7.8 MB of
//     shell32 plus its user32/gdi32/uxtheme chain into the process - only actually picking
//     the command does;
//   * the icon path is resolved once instead of on every menu build, and nothing on the
//     menu path allocates beyond the strings COM's contract forces us to hand back.

#include "pch.h"

// Placement new, declared here rather than pulled from <new>, which would be the only
// standard header this file needs.
inline void* operator new(size_t, void* p) noexcept { return p; }
inline void  operator delete(void*, void*)  noexcept {}

// MSVC emits a scalar deleting destructor for these polymorphic classes that references the
// sized operator delete, even though nothing here uses a delete-expression. Define both on
// the same heap HeapNew allocates from: if that path ever did run, the CRT's version would
// free a process-heap block from the CRT heap.
void operator delete(void* p)         noexcept { if (p) HeapFree(GetProcessHeap(), 0, p); }
void operator delete(void* p, size_t) noexcept { if (p) HeapFree(GetProcessHeap(), 0, p); }

// {748744E1-F3A0-40BA-B7B3-938A4734EC96}  image files
static const CLSID CLSID_ShellExtension =
{ 0x748744E1,0xF3A0,0x40BA,{0xB7,0xB3,0x93,0x8A,0x47,0x34,0xEC,0x96} };
// {748744E1-F3A0-40BA-B7B3-938A4734EC97}  PDF files
static const CLSID CLSID_MergePdfExtension =
{ 0x748744E1,0xF3A0,0x40BA,{0xB7,0xB3,0x93,0x8A,0x47,0x34,0xEC,0x97} };

static LONG g_dllRefCount = 0;

// ---------------------------------------------------------------------------
// Small CRT-free helpers
// ---------------------------------------------------------------------------

static size_t StrLenW(const WCHAR* s) noexcept
{
	const WCHAR* p = s;
	while (*p) ++p;
	return (size_t)(p - s);
}

static void CopyW(WCHAR* dst, const WCHAR* src, size_t count) noexcept
{
	for (size_t i = 0; i < count; ++i) dst[i] = src[i];
}

/// Duplicates a string into COM's allocator, which is what every LPWSTR* out-parameter on
/// IExplorerCommand expects. Replaces SHStrDupW so shlwapi stays out of the import table.
static HRESULT CoTaskMemDupW(const WCHAR* src, LPWSTR* out) noexcept
{
	*out = nullptr;
	if (!src) return E_INVALIDARG;

	const size_t chars = StrLenW(src) + 1;
	auto* copy = static_cast<WCHAR*>(CoTaskMemAlloc(chars * sizeof(WCHAR)));
	if (!copy) return E_OUTOFMEMORY;

	CopyW(copy, src, chars);
	*out = copy;
	return S_OK;
}

template <class T, class A>
static T* HeapNew(A&& arg) noexcept
{
	void* mem = HeapAlloc(GetProcessHeap(), 0, sizeof(T));
	return mem ? new (mem) T(static_cast<A&&>(arg)) : nullptr;
}

template <class T>
static void HeapDelete(T* obj) noexcept
{
	obj->~T();
	HeapFree(GetProcessHeap(), 0, obj);
}

// ---------------------------------------------------------------------------
// URI construction
// ---------------------------------------------------------------------------

static constexpr WCHAR kHex[] = L"0123456789ABCDEF";

static bool NeedsEncoding(unsigned char ch) noexcept
{
	return !((ch >= 'A' && ch <= 'Z') || (ch >= 'a' && ch <= 'z') ||
		(ch >= '0' && ch <= '9') || ch == '-' || ch == '_' || ch == '.' || ch == '~');
}

/// A growable UTF-16 buffer on the process heap.
///
/// This replaced a 32 KB `WCHAR uri[32768]` local. That local committed 64 KB of stack on
/// every invoke whatever the selection actually was, and still silently truncated once the
/// encoded form outgrew it. Growing on demand costs one allocation for a typical selection
/// and is correct for every selection.
class UriBuffer
{
	WCHAR* _data = nullptr;
	size_t _len = 0;   // WCHARs written, excluding the terminator
	size_t _cap = 0;   // WCHARs allocated

public:
	UriBuffer() = default;
	UriBuffer(const UriBuffer&) = delete;
	UriBuffer& operator=(const UriBuffer&) = delete;

	~UriBuffer() noexcept
	{
		if (_data) HeapFree(GetProcessHeap(), 0, _data);
	}

	bool Reserve(size_t extra) noexcept
	{
		if (_len + extra + 1 <= _cap) return true;

		size_t cap = _cap ? _cap : 512;
		while (cap < _len + extra + 1)
		{
			if (cap > (size_t)-1 / 2) return false;
			cap *= 2;
		}

		void* grown = _data
			? HeapReAlloc(GetProcessHeap(), 0, _data, cap * sizeof(WCHAR))
			: HeapAlloc(GetProcessHeap(), 0, cap * sizeof(WCHAR));
		if (!grown) return false;

		_data = static_cast<WCHAR*>(grown);
		_cap = cap;
		return true;
	}

	bool Put(WCHAR c) noexcept
	{
		if (!Reserve(1)) return false;
		_data[_len++] = c;
		return true;
	}

	bool Put(const WCHAR* s, size_t n) noexcept
	{
		if (!Reserve(n)) return false;
		CopyW(_data + _len, s, n);
		_len += n;
		return true;
	}

	/// Null-terminates and hands back the buffer. Empty until something is written.
	const WCHAR* Finish() noexcept
	{
		if (!_data) return nullptr;
		_data[_len] = L'\0';
		return _data;
	}

	bool IsEmpty() const noexcept { return _len == 0; }
};

/// Percent-encodes one UTF-16 string as UTF-8, straight into the output buffer.
///
/// Encoding inline rather than staging through WideCharToMultiByte drops a fixed
/// `char utf8[MAX_PATH * 4]` local along with the truncation it caused: Windows paths can
/// reach 32767 characters, and the old code quietly cut them off at 260.
static bool AppendEncoded(UriBuffer& out, const WCHAR* s) noexcept
{
	for (; *s; ++s)
	{
		unsigned int cp = (unsigned int)*s;

		// Recombine a surrogate pair so astral characters encode as one 4-byte sequence.
		if (cp >= 0xD800 && cp <= 0xDBFF && s[1] >= 0xDC00 && s[1] <= 0xDFFF)
		{
			cp = 0x10000u + ((cp - 0xD800u) << 10) + ((unsigned int)s[1] - 0xDC00u);
			++s;
		}

		unsigned char utf8[4];
		int n;
		if (cp < 0x80)
		{
			utf8[0] = (unsigned char)cp;
			n = 1;
		}
		else if (cp < 0x800)
		{
			utf8[0] = (unsigned char)(0xC0 | (cp >> 6));
			utf8[1] = (unsigned char)(0x80 | (cp & 0x3F));
			n = 2;
		}
		else if (cp < 0x10000)
		{
			utf8[0] = (unsigned char)(0xE0 | (cp >> 12));
			utf8[1] = (unsigned char)(0x80 | ((cp >> 6) & 0x3F));
			utf8[2] = (unsigned char)(0x80 | (cp & 0x3F));
			n = 3;
		}
		else
		{
			utf8[0] = (unsigned char)(0xF0 | (cp >> 18));
			utf8[1] = (unsigned char)(0x80 | ((cp >> 12) & 0x3F));
			utf8[2] = (unsigned char)(0x80 | ((cp >> 6) & 0x3F));
			utf8[3] = (unsigned char)(0x80 | (cp & 0x3F));
			n = 4;
		}

		for (int i = 0; i < n; ++i)
		{
			const unsigned char ch = utf8[i];
			if (NeedsEncoding(ch))
			{
				WCHAR esc[3] = { L'%', kHex[ch >> 4], kHex[ch & 0xF] };
				if (!out.Put(esc, 3)) return false;
			}
			else if (!out.Put((WCHAR)ch))
			{
				return false;
			}
		}
	}
	return true;
}

static const WCHAR* BuildGladhenUri(IShellItemArray* psia, UriBuffer& out) noexcept
{
	static constexpr WCHAR  kPrefix[] = L"gladhen2:///open?files=";
	static constexpr size_t kPfxLen = _countof(kPrefix) - 1;

	DWORD count = 0;
	if (FAILED(psia->GetCount(&count)) || count == 0) return nullptr;
	if (!out.Put(kPrefix, kPfxLen)) return nullptr;

	bool wroteAny = false;
	for (DWORD i = 0; i < count; ++i)
	{
		IShellItem* psi = nullptr;
		if (FAILED(psia->GetItemAt(i, &psi))) continue;

		PWSTR path = nullptr;
		if (SUCCEEDED(psi->GetDisplayName(SIGDN_FILESYSPATH, &path)) && path)
		{
			bool ok = true;
			if (wroteAny) ok = out.Put(L',');
			if (ok) ok = AppendEncoded(out, path);
			CoTaskMemFree(path);

			if (!ok) { psi->Release(); return nullptr; }
			wroteAny = true;
		}
		psi->Release();
	}

	return wroteAny ? out.Finish() : nullptr;
}

// ---------------------------------------------------------------------------
// Icon path, resolved once per process
// ---------------------------------------------------------------------------

static INIT_ONCE g_iconOnce = INIT_ONCE_STATIC_INIT;
static WCHAR     g_iconPath[MAX_PATH + 8];

/// Works out what Explorer should draw next to the menu item.
///
/// This used to run on every menu build: a GetModuleFileNameW plus up to two
/// GetFileAttributesW round trips each time the user right-clicked a JPEG. The answer
/// cannot change while the DLL is mapped, so it is worked out once.
static BOOL CALLBACK ResolveIconPath(PINIT_ONCE, PVOID, PVOID*) noexcept
{
	g_iconPath[0] = L'\0';

	HMODULE self = nullptr;
	if (!GetModuleHandleExW(GET_MODULE_HANDLE_EX_FLAG_FROM_ADDRESS |
		GET_MODULE_HANDLE_EX_FLAG_UNCHANGED_REFCOUNT,
		reinterpret_cast<LPCWSTR>(&ResolveIconPath), &self))
		return TRUE;

	WCHAR dir[MAX_PATH];
	const DWORD len = GetModuleFileNameW(self, dir, MAX_PATH);
	if (len == 0 || len >= MAX_PATH) return TRUE;

	WCHAR* lastSlash = nullptr;
	for (WCHAR* p = dir; *p; ++p) if (*p == L'\\') lastSlash = p;
	if (!lastSlash) return TRUE;
	*lastSlash = L'\0';

	const size_t dirLen = StrLenW(dir);

	// The app's own icon, if the executable sits beside us.
	static constexpr WCHAR kExe[] = L"\\Gladhen3.exe";
	if (dirLen + _countof(kExe) + 2 <= _countof(g_iconPath))
	{
		CopyW(g_iconPath, dir, dirLen);
		CopyW(g_iconPath + dirLen, kExe, _countof(kExe));
		if (GetFileAttributesW(g_iconPath) != INVALID_FILE_ATTRIBUTES)
		{
			// ",0" tells Explorer to take the first icon resource out of the executable.
			const size_t end = dirLen + _countof(kExe) - 1;
			g_iconPath[end] = L',';
			g_iconPath[end + 1] = L'0';
			g_iconPath[end + 2] = L'\0';
			return TRUE;
		}
	}

	// Otherwise the packaged icon asset.
	static constexpr WCHAR kIco[] = L"\\Assets\\app.ico";
	if (dirLen + _countof(kIco) <= _countof(g_iconPath))
	{
		CopyW(g_iconPath, dir, dirLen);
		CopyW(g_iconPath + dirLen, kIco, _countof(kIco));
		if (GetFileAttributesW(g_iconPath) != INVALID_FILE_ATTRIBUTES) return TRUE;
	}

	g_iconPath[0] = L'\0';
	return TRUE;
}

static HRESULT GetIconPath(LPWSTR* ppszIcon) noexcept
{
	*ppszIcon = nullptr;
	if (!InitOnceExecuteOnce(&g_iconOnce, ResolveIconPath, nullptr, nullptr)) return E_FAIL;
	if (!g_iconPath[0]) return E_NOTIMPL;
	return CoTaskMemDupW(g_iconPath, ppszIcon);
}

// ---------------------------------------------------------------------------
// Launching
// ---------------------------------------------------------------------------

/// Hands the URI to the shell.
///
/// ShellExecuteExW is the only thing this DLL wants from shell32, and it is only wanted
/// when the user actually picks the command. Importing it statically made every surrogate
/// map shell32 (7.8 MB) plus the user32/gdi32full/uxtheme/imm32 chain it drags behind it,
/// just to answer GetTitle and GetIcon. Resolving it here keeps that cost on the one code
/// path that needs it.
static HRESULT LaunchUri(const WCHAR* uri) noexcept
{
	using ShellExecuteExFn = BOOL(WINAPI*)(SHELLEXECUTEINFOW*);

	const HMODULE shell32 = LoadLibraryExW(L"shell32.dll", nullptr,
		LOAD_LIBRARY_SEARCH_SYSTEM32);
	if (!shell32) return HRESULT_FROM_WIN32(GetLastError());

	HRESULT hr = E_FAIL;
	if (auto* exec = reinterpret_cast<ShellExecuteExFn>(
		GetProcAddress(shell32, "ShellExecuteExW")))
	{
		SHELLEXECUTEINFOW sei = { sizeof(sei) };
		sei.fMask = SEE_MASK_NOASYNC;
		sei.lpVerb = L"open";
		sei.lpFile = uri;
		sei.nShow = SW_SHOWNORMAL;
		hr = exec(&sei) ? S_OK : HRESULT_FROM_WIN32(GetLastError());
	}

	// The surrogate is about to be torn down anyway, but SEE_MASK_NOASYNC above means the
	// launch has completed by now, so shell32 can go straight back out.
	FreeLibrary(shell32);
	return hr;
}

// ---------------------------------------------------------------------------
// The command
// ---------------------------------------------------------------------------

class GladhenCommand final : public IExplorerCommand
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
		const LONG r = InterlockedDecrement(&_ref);
		if (!r) HeapDelete(this);
		return (ULONG)r;
	}

	HRESULT __stdcall QueryInterface(REFIID riid, void** ppv) noexcept override
	{
		if (!ppv) return E_POINTER;
		if (IsEqualIID(riid, IID_IUnknown) || IsEqualIID(riid, IID_IExplorerCommand))
		{
			*ppv = static_cast<IExplorerCommand*>(this);
			AddRef();
			return S_OK;
		}
		*ppv = nullptr;
		return E_NOINTERFACE;
	}

	HRESULT __stdcall GetTitle(IShellItemArray*, LPWSTR* p) noexcept override
	{
		return CoTaskMemDupW(L"Open with Gladhen3", p);
	}

	HRESULT __stdcall GetIcon(IShellItemArray*, LPWSTR* p) noexcept override
	{
		return GetIconPath(p);
	}

	HRESULT __stdcall GetToolTip(IShellItemArray*, LPWSTR* p) noexcept override
	{
		if (p) *p = nullptr;
		return E_NOTIMPL;
	}

	HRESULT __stdcall GetCanonicalName(GUID* g) noexcept override
	{
		if (!g) return E_POINTER;
		*g = _clsid;
		return S_OK;
	}

	HRESULT __stdcall GetState(IShellItemArray*, BOOL, EXPCMDSTATE* s) noexcept override
	{
		if (!s) return E_POINTER;
		*s = ECS_ENABLED;
		return S_OK;
	}

	HRESULT __stdcall GetFlags(EXPCMDFLAGS* f) noexcept override
	{
		if (!f) return E_POINTER;
		*f = ECF_DEFAULT;
		return S_OK;
	}

	HRESULT __stdcall EnumSubCommands(IEnumExplorerCommand** e) noexcept override
	{
		if (e) *e = nullptr;
		return E_NOTIMPL;
	}

	HRESULT __stdcall Invoke(IShellItemArray* psia, IBindCtx*) noexcept override
	{
		if (!psia) return E_INVALIDARG;

		UriBuffer uri;
		const WCHAR* text = BuildGladhenUri(psia, uri);
		if (!text) return E_FAIL;

		return LaunchUri(text);
	}
};

// ---------------------------------------------------------------------------
// Class factory
// ---------------------------------------------------------------------------

class GladhenClassFactory final : public IClassFactory
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
		const LONG r = InterlockedDecrement(&_ref);
		if (!r) HeapDelete(this);
		return (ULONG)r;
	}

	HRESULT __stdcall QueryInterface(REFIID riid, void** ppv) noexcept override
	{
		if (!ppv) return E_POINTER;
		if (IsEqualIID(riid, IID_IUnknown) || IsEqualIID(riid, IID_IClassFactory))
		{
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

		auto* obj = HeapNew<GladhenCommand>(_clsid);
		if (!obj) return E_OUTOFMEMORY;

		const HRESULT hr = obj->QueryInterface(riid, ppv);
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

// ---------------------------------------------------------------------------
// DLL entry points
// ---------------------------------------------------------------------------

extern "C" BOOL WINAPI DllMain(HINSTANCE, DWORD, LPVOID) noexcept
{
	return TRUE;
}

STDAPI DllCanUnloadNow()
{
	return (InterlockedCompareExchange(&g_dllRefCount, 0, 0) == 0) ? S_OK : S_FALSE;
}

STDAPI DllGetClassObject(REFCLSID rclsid, REFIID riid, LPVOID* ppv)
{
	if (!ppv) return E_POINTER;
	*ppv = nullptr;

	const CLSID* target;
	if (IsEqualCLSID(rclsid, CLSID_ShellExtension))          target = &CLSID_ShellExtension;
	else if (IsEqualCLSID(rclsid, CLSID_MergePdfExtension))  target = &CLSID_MergePdfExtension;
	else return CLASS_E_CLASSNOTAVAILABLE;

	auto* factory = HeapNew<GladhenClassFactory>(*target);
	if (!factory) return E_OUTOFMEMORY;

	const HRESULT hr = factory->QueryInterface(riid, ppv);
	factory->Release();
	return hr;
}

// ---------------------------------------------------------------------------
// Self-registration
//
// The MSIX declares both classes in its manifest, so none of this runs in the shipping
// product. It stays for the unpackaged case - dropping the DLL somewhere and running
// regsvr32 on it is how the extension gets tested without building a package.
// ---------------------------------------------------------------------------

static HRESULT WriteRegSz(HKEY root, LPCWSTR subkey, LPCWSTR name, LPCWSTR data) noexcept
{
	HKEY key;
	LONG r = RegCreateKeyExW(root, subkey, 0, nullptr, 0, KEY_SET_VALUE, nullptr, &key, nullptr);
	if (r != ERROR_SUCCESS) return HRESULT_FROM_WIN32(r);

	const DWORD cb = (DWORD)((StrLenW(data) + 1) * sizeof(WCHAR));
	r = RegSetValueExW(key, name, 0, REG_SZ, reinterpret_cast<const BYTE*>(data), cb);
	RegCloseKey(key);
	return r == ERROR_SUCCESS ? S_OK : HRESULT_FROM_WIN32(r);
}

/// Builds "CLSID\{guid}" and, when requested, its InprocServer32 subkey.
static void ClsidKey(const CLSID& clsid, WCHAR(&out)[80], bool inproc) noexcept
{
	static constexpr WCHAR kBase[] = L"CLSID\\";
	static constexpr WCHAR kInproc[] = L"\\InprocServer32";

	CopyW(out, kBase, _countof(kBase) - 1);
	size_t pos = _countof(kBase) - 1;
	pos += (size_t)StringFromGUID2(clsid, out + pos, 40) - 1;
	if (inproc)
	{
		CopyW(out + pos, kInproc, _countof(kInproc));
		return;
	}
	out[pos] = L'\0';
}

static HRESULT RegisterClsid(const CLSID& clsid, LPCWSTR description, LPCWSTR dllPath) noexcept
{
	WCHAR base[80], inproc[80];
	ClsidKey(clsid, base, false);
	ClsidKey(clsid, inproc, true);

	HRESULT hr = WriteRegSz(HKEY_CLASSES_ROOT, base, nullptr, description);
	if (SUCCEEDED(hr)) hr = WriteRegSz(HKEY_CLASSES_ROOT, inproc, nullptr, dllPath);
	if (SUCCEEDED(hr)) hr = WriteRegSz(HKEY_CLASSES_ROOT, inproc, L"ThreadingModel", L"Apartment");
	return hr;
}

static void UnregisterClsid(const CLSID& clsid) noexcept
{
	WCHAR key[80];
	ClsidKey(clsid, key, false);
	RegDeleteTreeW(HKEY_CLASSES_ROOT, key);
}

STDAPI DllRegisterServer()
{
	HMODULE self = nullptr;
	if (!GetModuleHandleExW(GET_MODULE_HANDLE_EX_FLAG_FROM_ADDRESS |
		GET_MODULE_HANDLE_EX_FLAG_UNCHANGED_REFCOUNT,
		reinterpret_cast<LPCWSTR>(&DllRegisterServer), &self))
		return HRESULT_FROM_WIN32(GetLastError());

	WCHAR path[MAX_PATH];
	const DWORD len = GetModuleFileNameW(self, path, MAX_PATH);
	if (len == 0 || len >= MAX_PATH) return HRESULT_FROM_WIN32(GetLastError());

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

STDAPI DllInstall(BOOL bInstall, LPCWSTR /*pszCmdLine*/)
{
	const HRESULT hr = bInstall ? DllRegisterServer() : DllUnregisterServer();
	if (bInstall && FAILED(hr)) DllUnregisterServer();
	return hr;
}
