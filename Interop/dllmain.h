// dllmain.h : Declaration of module class.

#include <atlbase.h>
#include <atlcom.h>
#include <ShlObj_core.h>
#include <windows.h>

class CShellExtensionModule : public ATL::CAtlDllModuleT< CShellExtensionModule >
{
public :
	DECLARE_LIBID(LIBID_ShellExtensionLib)
	DECLARE_REGISTRY_APPID_RESOURCEID(IDR_SHELLEXTENSION, "{748744E1-F3A0-40BA-B7B3-938A4734EC96}")
};

extern class CShellExtensionModule _AtlModule;

class ATL_NO_VTABLE CShellextencsionHandler : 
	public ATL::CComObjectRootEx<ATL::CComSingleThreadModel>,
	public IExplorerCommand
{
public:
	BEGIN_COM_MAP(CShellextencsionHandler)
		COM_INTERFACE_ENTRY(IExplorerCommand)
	END_COM_MAP()

	HRESULT __stdcall GetTitle(IShellItemArray* psiItemArray, LPWSTR* ppszName) override
	{
		*ppszName = SysAllocString(L"Convert to PDF");
		return S_OK;
	}

	HRESULT __stdcall GetIcon(IShellItemArray* psiItemArray, LPWSTR* ppszIcon) override
	{
		return E_NOTIMPL;
	}

	HRESULT __stdcall GetToolTip(IShellItemArray* psiItemArray, LPWSTR* ppszInfotip) override
	{
		return E_NOTIMPL;
	}

	HRESULT __stdcall GetCanonicalName(GUID* pguidCommandName) override
	{
		return E_NOTIMPL;
	}

    HRESULT __stdcall GetState(IShellItemArray* psiItemArray, BOOL fOkToBeSlow, EXPCMDSTATE* pCmdState) override
    {
        if (!psiItemArray)
        {
            *pCmdState = ECS_DISABLED;
            return S_OK;
        }

        DWORD count = 0;
        HRESULT hr = psiItemArray->GetCount(&count);
        if (FAILED(hr) || count == 0)
        {
            *pCmdState = ECS_DISABLED;
            return S_OK;
        }

        for (DWORD i = 0; i < count; i++)
        {
            IShellItem* psi = nullptr;
            hr = psiItemArray->GetItemAt(i, &psi);

            if (SUCCEEDED(hr))
            {
                PWSTR filePath = nullptr;
                hr = psi->GetDisplayName(SIGDN_FILESYSPATH, &filePath);

                if (SUCCEEDED(hr))
                {
                    WCHAR filePathLower[MAX_PATH] = { 0 };
                    wcscpy_s(filePathLower, MAX_PATH, filePath);
                    _wcslwr_s(filePathLower, MAX_PATH);

                    PWSTR extension = wcsrchr(filePathLower, L'.');

                    bool isSupported = false;

                    if (extension)
                    {
                        const WCHAR* supportedExtensions[] = {
                            L".jpg", L".jpeg", L".png", L".gif", L".bmp", L".tiff",
                            L".tif", L".webp", L".heic", L".heif", L".svg",
                            L".ico", L".jfif"
                        };

                        for (int j = 0; j < sizeof(supportedExtensions) / sizeof(supportedExtensions[0]); j++)
                        {
                            if (wcscmp(extension, supportedExtensions[j]) == 0)
                            {
                                isSupported = true;
                                break;
                            }
                        }
                    }

                    CoTaskMemFree(filePath);

                    if (!isSupported)
                    {
                        psi->Release();
                        *pCmdState = ECS_DISABLED;
                        return S_OK;
                    }
                }

                psi->Release();
            }
        }

        *pCmdState = ECS_ENABLED;
        return S_OK;
    }

    HRESULT __stdcall Invoke(IShellItemArray* psiItemArray, IBindCtx* pbc) override
    {
        DWORD count = 0;
        HRESULT hr = psiItemArray->GetCount(&count);
        if (FAILED(hr) || count == 0)
            return hr;

        WCHAR uri[32768] = L"gladhen2:///open?files=";
        size_t uriPos = wcslen(uri);

        for (DWORD i = 0; i < count; i++)
        {
            IShellItem* psi = nullptr;
            hr = psiItemArray->GetItemAt(i, &psi);
            if (SUCCEEDED(hr))
            {
                PWSTR filePath = nullptr;
                hr = psi->GetDisplayName(SIGDN_FILESYSPATH, &filePath);
                if (SUCCEEDED(hr))
                {
                    if (i > 0)
                    {
                        wcscat_s(uri, L",");
                        uriPos++;
                    }

                    PWSTR src = filePath;
                    while (*src != L'\0' && uriPos < 32767)
                    {
                        WCHAR c = *src++;

                        if (c == L' ')
                        {
                            uri[uriPos++] = L'%';
                            uri[uriPos++] = L'2';
                            uri[uriPos++] = L'0';
                        }
                        else if (c == L'%')
                        {
                            uri[uriPos++] = L'%';
                            uri[uriPos++] = L'2';
                            uri[uriPos++] = L'5';
                        }
                        else if (c == L',' || c == L'?' || c == L'&' || c == L'=' ||
                            c == L'#' || c == L'+' || c == L':' || c == L'/' ||
                            c == L'\\')
                        {
                            uri[uriPos++] = L'%';

                            WCHAR hex[3];
                            swprintf_s(hex, 3, L"%02X", (BYTE)c);
                            uri[uriPos++] = hex[0];
                            uri[uriPos++] = hex[1];
                        }
                        else
                        {
                            uri[uriPos++] = c;
                        }
                    }

                    uri[uriPos] = L'\0';

                    CoTaskMemFree(filePath);
                }
                psi->Release();
            }
        }

        SHELLEXECUTEINFOW sei = { 0 };
        sei.cbSize = sizeof(sei);
        sei.lpVerb = L"open";
        sei.lpFile = uri;
        sei.nShow = SW_SHOWNORMAL;

        if (!ShellExecuteExW(&sei))
        {
            MessageBoxW(NULL, L"Failed to launch Gladhen2 via protocol. Check that the protocol handler is registered correctly.", L"Error", MB_ICONERROR);
        }

        return S_OK;
    }

	HRESULT __stdcall GetFlags(EXPCMDFLAGS* pFlags) override
	{
		return E_NOTIMPL;
	}

	HRESULT __stdcall EnumSubCommands(IEnumExplorerCommand** ppEnum) override
	{
		return E_NOTIMPL;
	}
};

	
class ATL_NO_VTABLE __declspec(uuid("748744E1-F3A0-40BA-B7B3-938A4734EC96")) CShellExtension : 
	public CShellextencsionHandler,
	public ATL::CComCoClass<CShellExtension, &__uuidof(CShellExtension)>
{
public:
	CShellExtension() {}

	DECLARE_REGISTRY_RESOURCEID(IDR_SHELLEXTENSION)
	DECLARE_NOT_AGGREGATABLE(CShellExtension)

	DECLARE_PROTECT_FINAL_CONSTRUCT()

	HRESULT FinalConstruct()
	{
		return S_OK;
	}

	void FinalRelease()
	{
	}
};

OBJECT_ENTRY_AUTO(__uuidof(CShellExtension), CShellExtension)