#pragma once

#define WIN32_LEAN_AND_MEAN
#define NOMINMAX
#ifndef STRICT
#define STRICT
#endif

#include <windows.h>
#include <wchar.h>
#include <objbase.h>
#include <ShObjIdl_core.h>
#include <shlwapi.h>
#include <shellapi.h>

#pragma comment(lib, "shlwapi.lib")
