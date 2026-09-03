#pragma once

#define WIN32_LEAN_AND_MEAN
#define NOMINMAX
#ifndef STRICT
#define STRICT
#endif

#include <windows.h>
#include <objbase.h>
#include <ShObjIdl_core.h>

// SHELLEXECUTEINFOW's declaration only - ShellExecuteExW itself is resolved at call time so
// that shell32 is not in this DLL's import table. See LaunchUri in ShellExtension.cpp.
#include <shellapi.h>
