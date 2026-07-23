#pragma once

// {61e6437f-c160-4be1-a712-e207f2f34b44}
inline const IID LIBID_ShellExtensionLib =
{ 0x61e6437f, 0xc160, 0x4be1, { 0xa7, 0x12, 0xe2, 0x07, 0xf2, 0xf3, 0x4b, 0x44 } };

class CShellExtensionModule : public ATL::CAtlDllModuleT<CShellExtensionModule>
{
public:
	DECLARE_REGISTRY_APPID_RESOURCEID(IDR_SHELLEXTENSION, "{748744E1-F3A0-40BA-B7B3-938A4734EC96}")
};

extern CShellExtensionModule _AtlModule;

void BuildGladhenUri(IShellItemArray* psiItemArray, WCHAR* uri, size_t uriSize) noexcept;

HRESULT GetGladhenIconPath(LPWSTR* ppszIcon) noexcept;

namespace GladhenCmd
{
	inline HRESULT GetTitle(LPWSTR* ppszName) noexcept
	{
		return SHStrDupW(L"Open with Gladhen3", ppszName);
	}

	inline HRESULT GetIcon(LPWSTR* ppszIcon) noexcept
	{
		return GetGladhenIconPath(ppszIcon);
	}

	inline HRESULT Invoke(IShellItemArray* psiItemArray) noexcept
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
}

class ATL_NO_VTABLE __declspec(uuid("748744E1-F3A0-40BA-B7B3-938A4734EC96")) CShellExtension :
	public ATL::CComObjectRootEx<ATL::CComSingleThreadModel>,
	public ATL::CComCoClass<CShellExtension, &__uuidof(CShellExtension)>,
	public IExplorerCommand
{
public:
	DECLARE_REGISTRY_RESOURCEID(IDR_SHELLEXTENSION)
	DECLARE_NOT_AGGREGATABLE(CShellExtension)
	BEGIN_COM_MAP(CShellExtension)
		COM_INTERFACE_ENTRY(IExplorerCommand)
	END_COM_MAP()
	DECLARE_PROTECT_FINAL_CONSTRUCT()

	HRESULT FinalConstruct() noexcept { return S_OK; }
	void    FinalRelease()   noexcept {}

	HRESULT __stdcall GetTitle(IShellItemArray*, LPWSTR* p)              noexcept override { return GladhenCmd::GetTitle(p); }
	HRESULT __stdcall GetIcon(IShellItemArray*, LPWSTR* p)               noexcept override { return GladhenCmd::GetIcon(p); }
	HRESULT __stdcall GetToolTip(IShellItemArray*, LPWSTR* p)            noexcept override { *p = nullptr; return E_NOTIMPL; }
	HRESULT __stdcall GetCanonicalName(GUID* g)                          noexcept override { *g = __uuidof(CShellExtension); return S_OK; }
	HRESULT __stdcall GetState(IShellItemArray*, BOOL, EXPCMDSTATE* s)   noexcept override { *s = ECS_ENABLED; return S_OK; }
	HRESULT __stdcall Invoke(IShellItemArray* a, IBindCtx*)              noexcept override { return GladhenCmd::Invoke(a); }
	HRESULT __stdcall GetFlags(EXPCMDFLAGS* f)                           noexcept override { *f = ECF_DEFAULT; return S_OK; }
	HRESULT __stdcall EnumSubCommands(IEnumExplorerCommand** e)          noexcept override { *e = nullptr; return E_NOTIMPL; }
};

class ATL_NO_VTABLE __declspec(uuid("748744E1-F3A0-40BA-B7B3-938A4734EC97")) CMergePdfExtension :
	public ATL::CComObjectRootEx<ATL::CComSingleThreadModel>,
	public ATL::CComCoClass<CMergePdfExtension, &__uuidof(CMergePdfExtension)>,
	public IExplorerCommand
{
public:
	DECLARE_REGISTRY_RESOURCEID(IDR_MERGEPDFEXTENSION)
	DECLARE_NOT_AGGREGATABLE(CMergePdfExtension)
	BEGIN_COM_MAP(CMergePdfExtension)
		COM_INTERFACE_ENTRY(IExplorerCommand)
	END_COM_MAP()
	DECLARE_PROTECT_FINAL_CONSTRUCT()

	HRESULT FinalConstruct() noexcept { return S_OK; }
	void    FinalRelease()   noexcept {}

	HRESULT __stdcall GetTitle(IShellItemArray*, LPWSTR* p)              noexcept override { return GladhenCmd::GetTitle(p); }
	HRESULT __stdcall GetIcon(IShellItemArray*, LPWSTR* p)               noexcept override { return GladhenCmd::GetIcon(p); }
	HRESULT __stdcall GetToolTip(IShellItemArray*, LPWSTR* p)            noexcept override { *p = nullptr; return E_NOTIMPL; }
	HRESULT __stdcall GetCanonicalName(GUID* g)                          noexcept override { *g = __uuidof(CMergePdfExtension); return S_OK; }
	HRESULT __stdcall GetState(IShellItemArray*, BOOL, EXPCMDSTATE* s)   noexcept override { *s = ECS_ENABLED; return S_OK; }
	HRESULT __stdcall Invoke(IShellItemArray* a, IBindCtx*)              noexcept override { return GladhenCmd::Invoke(a); }
	HRESULT __stdcall GetFlags(EXPCMDFLAGS* f)                           noexcept override { *f = ECF_DEFAULT; return S_OK; }
	HRESULT __stdcall EnumSubCommands(IEnumExplorerCommand** e)          noexcept override { *e = nullptr; return E_NOTIMPL; }
};
