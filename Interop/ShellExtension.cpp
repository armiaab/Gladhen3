// ShellExtension.cpp : Implementation of DLL Exports.

#include "pch.h"
#include "framework.h"
#include "resource.h"
#include "ShellExtension_i.h"
#include "dllmain.h"
#include "xdlldata.h"

using namespace ATL;

// Helper function to build URI with encoded file paths
void BuildGladhenUri(IShellItemArray* psiItemArray, WCHAR* uri, size_t uriSize)
{
	wcscpy_s(uri, uriSize, L"gladhen2:///open?files=");
	size_t pos = wcslen(uri);

	DWORD count = 0;
	if (FAILED(psiItemArray->GetCount(&count)) || count == 0)
		return;

	for (DWORD i = 0; i < count && pos < uriSize - 10; i++)
	{
		IShellItem* psi = nullptr;
		if (SUCCEEDED(psiItemArray->GetItemAt(i, &psi)))
		{
			PWSTR filePath = nullptr;
			if (SUCCEEDED(psi->GetDisplayName(SIGDN_FILESYSPATH, &filePath)) && filePath)
			{
				if (i > 0 && pos < uriSize - 1)
					uri[pos++] = L',';

				for (PWSTR src = filePath; *src && pos < uriSize - 4; src++)
				{
					WCHAR c = *src;
					if (c == L' ' || c == L'%' || c == L',' || c == L'?' ||
						c == L'&' || c == L'=' || c == L'#' || c == L'+' ||
						c == L':' || c == L'/' || c == L'\\')
					{
						uri[pos++] = L'%';
						WCHAR hex[3];
						swprintf_s(hex, 3, L"%02X", (BYTE)c);
						uri[pos++] = hex[0];
						uri[pos++] = hex[1];
					}
					else
					{
						uri[pos++] = c;
					}
				}
				CoTaskMemFree(filePath);
			}
			psi->Release();
		}
	}
	uri[pos] = L'\0';
}

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

_Use_decl_annotations_
STDAPI DllRegisterServer(void)
{
	HRESULT hr = _AtlModule.DllRegisterServer();
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

STDAPI DllInstall(BOOL bInstall, _In_opt_ LPCWSTR pszCmdLine)
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
			DllUnregisterServer();
	}
	else
	{
		hr = DllUnregisterServer();
	}

	return hr;
}


