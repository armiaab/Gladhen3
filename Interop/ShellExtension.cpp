// ShellExtension.cpp : Implementation of DLL Exports.


#include "pch.h"
#include "framework.h"
#include "resource.h"
#include "ShellExtension_i.h"
#include "dllmain.h"
#include "xdlldata.h"


using namespace ATL;

// Used to determine whether the DLL can be unloaded by OLE.
_Use_decl_annotations_
STDAPI DllCanUnloadNow(void)
{
#ifdef _MERGE_PROXYSTUB
	HRESULT hr = PrxDllCanUnloadNow();
	if (hr != S_OK)
		return hr;
#endif
	return _AtlModule.DllCanUnloadNow();
}

// Returns a class factory to create an object of the requested type.
_Use_decl_annotations_
STDAPI DllGetClassObject(_In_ REFCLSID rclsid, _In_ REFIID riid, _Outptr_ LPVOID* ppv)
{
#ifdef _MERGE_PROXYSTUB
	HRESULT hr = PrxDllGetClassObject(rclsid, riid, ppv);
	if (hr != CLASS_E_CLASSNOTAVAILABLE)
		return hr;
#endif
	return _AtlModule.DllGetClassObject(rclsid, riid, ppv);
}

HRESULT RegisterForFileType(LPCWSTR fileType)
{
    CRegKey key;
    LONG lResult;
    WCHAR szKeyPath[256];
    WCHAR szCLSID[64];
    
    StringFromGUID2(__uuidof(CShellExtension), szCLSID, sizeof(szCLSID)/sizeof(WCHAR));
    
    swprintf_s(szKeyPath, L"%s\\shellex\\ContextMenuHandlers\\Convert to PDF", fileType);
    lResult = key.Create(HKEY_CLASSES_ROOT, szKeyPath);
    if (lResult != ERROR_SUCCESS)
        return HRESULT_FROM_WIN32(lResult);
        
    lResult = key.SetStringValue(NULL, szCLSID);
    if (lResult != ERROR_SUCCESS)
        return HRESULT_FROM_WIN32(lResult);
    
    return S_OK;
}

HRESULT UnregisterForFileType(LPCWSTR fileType)
{
    WCHAR szKeyPath[256];
    
    swprintf_s(szKeyPath, L"%s\\shellex\\ContextMenuHandlers\\Convert to PDF", fileType);
    
    RegDeleteKeyW(HKEY_CLASSES_ROOT, szKeyPath);
    
    return S_OK;
}

_Use_decl_annotations_
STDAPI DllRegisterServer(void)
{
    HRESULT hr = _AtlModule.DllRegisterServer();
    
    if (SUCCEEDED(hr))
    {
        const WCHAR* supportedExtensions[] = {
            L".jpg", L".jpeg", L".png", L".bmp"
        };

        for (int i = 0; i < sizeof(supportedExtensions) / sizeof(supportedExtensions[0]); i++)
        {
            HRESULT hrExt = RegisterForFileType(supportedExtensions[i]);
            if (FAILED(hrExt))
            {
                ATLTRACE(L"Failed to register for %s, hr = 0x%08x\n", supportedExtensions[i], hrExt);
            }
        }
    }
    
#ifdef _MERGE_PROXYSTUB
	if (FAILED(hr))
		return hr;
	hr = PrxDllRegisterServer();
#endif
	return hr;
}

_Use_decl_annotations_
STDAPI DllUnregisterServer(void)
{
    HRESULT hr = _AtlModule.DllUnregisterServer();
    
    if (SUCCEEDED(hr))
    {
        const WCHAR* supportedExtensions[] = {
            L".jpg", L".jpeg", L".png", L".gif", L".bmp"
        };

        for (int i = 0; i < sizeof(supportedExtensions) / sizeof(supportedExtensions[0]); i++)
        {
            UnregisterForFileType(supportedExtensions[i]);
        }
    }
    
#ifdef _MERGE_PROXYSTUB
	if (FAILED(hr))
		return hr;
	hr = PrxDllRegisterServer();
	if (FAILED(hr))
		return hr;
	hr = PrxDllUnregisterServer();
#endif
	return hr;
}

STDAPI DllInstall(BOOL bInstall, _In_opt_  LPCWSTR pszCmdLine)
{
	HRESULT hr = E_FAIL;
	static const wchar_t szUserSwitch[] = L"user";

	if (pszCmdLine != nullptr)
	{
		if (_wcsnicmp(pszCmdLine, szUserSwitch, _countof(szUserSwitch)) == 0)
		{
			ATL::AtlSetPerUserRegistration(true);
		}
	}

	if (bInstall)
	{
		hr = DllRegisterServer();
		if (FAILED(hr))
		{
			DllUnregisterServer();
		}
	}
	else
	{
		hr = DllUnregisterServer();
	}

	return hr;
}


