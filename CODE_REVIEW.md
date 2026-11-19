# Gladhen3 code review

Review date: 2026-11-19

Scope: tracked C#, XAML, C++, project/manifest files, build scripts, and the current working-tree changes. Generated `obj`/`bin` output and packaged artifacts were excluded from the source assessment.

## Verification

- `dotnet build Gladhen3.csproj --no-restore` (Debug): passed with 0 warnings and 0 errors.
- Release compilation reached MSIX creation, then package signing failed with `APPX1204`/SignTool internal error in this environment.
- `dotnet build Gladhen3.sln` could not build the native shell extension because the environment lacks `VCTargetsPath\\Microsoft.Cpp.Default.props`.
- NuGet vulnerability lookup could not reach `https://api.nuget.org/v3/index.json`; dependency advisories still need to be checked in CI.
- No maintained test project is present in the repository. The project only references a removed/excluded `Gladhen3.Tests` tree.

## Findings that can break behavior

### High: named-pipe IPC assumes one read returns the complete frame

`App.xaml.cs:144-155` calls `ReadAsync` once for the four-byte length and once for the payload. A stream read is allowed to return fewer bytes than requested, including for named pipes. A fragmented frame is discarded, so Explorer/context-menu launches and second-instance file opens can silently lose files. Use `ReadExactlyAsync` (or a loop) for both the header and payload, and reject malformed UTF-8 explicitly.

The pipe listener is also started before `MainWindow` is constructed (`App.xaml.cs:118-123`). A second launch during that startup window is accepted and then dropped because `_mainWindow` is still null. Queue pending activations until the window exists. Serialize activation batches so multiple launches cannot interleave `AddFilesFromPaths` and undo history.

### High: settings load races the first document load and first save

`MainWindow.xaml.cs:61` starts `AppSettings.LoadAsync()` fire-and-forget. The path constructor starts loading documents immediately, and the estimate can run using defaults before persisted compression/paper settings arrive. There is no reschedule after settings finish, so the displayed estimate may remain wrong. A user can also open Settings and save before the load completes; the late load can overwrite the newly selected values.

Create an awaited initialization phase (load settings before enabling document operations), or gate all commands behind a single initialization task. Recompute the estimate after settings load.

### High: file-picker exceptions can escape `async void` event handlers

`MainWindow.xaml.cs:434-486` does not catch exceptions from `PickMultipleFilesAsync`. Picker/COM failures therefore escape the event boundary and can reach the global unhandled-exception handler. Wrap picker creation and invocation in the same user-facing error path used by `AddFilesAsync`.

### High: save runs against mutable UI objects

`SaveItemsAsync`/`SaveSectionsAsync` pass `DocumentItem` references into `Task.Run` (`MainWindow.xaml.cs:847-848`, `MainWindow.Sections.cs:554-555`) while page commands, normalization, and settings remain usable. Rotations, source paths, ordering, and compression settings can change during the build, producing an output that represents no single UI state. Snapshot an immutable save plan (items, rotation, source path, and settings) before starting the worker, or disable mutations and settings for the duration.

The same issue affects the estimate worker, which reads mutable `DocumentItem` instances and global `AppSettings.Current` from a background thread.

### High: unbounded PDF stream inflation permits resource exhaustion

`PdfService.cs:353-408` inflates arbitrary Flate streams into a `MemoryStream` with no decompressed-size limit. A small PDF stream can expand until memory is exhausted while saving. `ImageJob.Prepare` and image decoding have similar exposure to huge dimensions. Enforce hard limits on decoded stream bytes, image pixel count, page count, and total pages processed; fail the affected item cleanly when a limit is exceeded.

### Medium: settings dialog reports success before asynchronous persistence completes

`Dialogs/SettingsDialog.xaml.cs:468-512` handles `ContentDialog_PrimaryButtonClick` as `async void` without obtaining a button-click deferral. The dialog/caller can complete and display “settings saved” before `SaveAsync` finishes; a write failure is reported only afterward, and the in-memory settings remain changed. Use `args.GetDeferral()`, await the write before allowing the dialog to close, and restore the previous snapshot on failure.

### Medium: section names can produce invalid Windows device filenames

`Models/PdfSection.cs:89-103` only rejects exact reserved stems (`CON`, `PRN`, etc.). Windows reserves those device names with extensions too, so names such as `CON.report` become `CON.report.pdf` and can fail to save. Check the portion before the first dot against the reserved-name set (including `CONIN$`/`CONOUT$`) after sanitizing.

### Medium: section renames are absent from undo history

The section text binding mutates `DocumentItem.SectionName`, but `SectionName_LostFocus` (`MainWindow.Sections.cs:376-387`) never records a pre-edit state. “Undo” therefore does not undo a rename, despite the README promising undo for every edit. Capture on focus and push only if the name changed.

### Medium: reversing one selected page reverses the whole document

`MainWindow.Pages.cs:317-341` treats fewer than two selected pages as “no selection”. Selecting exactly one page and invoking Reverse therefore reverses every section. Treat a single explicit selection as a no-op (or clearly define that command as all-pages) instead of silently changing unrelated pages.

### Medium: viewer cancellation/resource cleanup is incomplete

`MainWindow.Viewer.cs:103-116` clears cached `PdfDocument` tasks without explicitly closing the documents they returned. `_viewerLoadCts` is also replaced/cancelled without disposal; Release builds report analyzer warning CA1001 for this field. Close/dispose completed documents and dispose each CTS after its operation finishes. The same cache cleanup issue exists in `OrientationDetector.Dispose` (`Services/OrientationDetector.cs`), which clears Windows PDF documents without closing them.

The viewer file itself contains a TODO acknowledging unresolved render races (`MainWindow.Viewer.cs:22`). Keep the version/token checks, but add an automated rapid-navigation test and make ownership of the active render/document explicit.

### Medium: PDF image recompression reads PDFsharp objects concurrently

`PdfService.cs:1397-1419` runs `ImageJob.Prepare`/`Recompress` for many jobs in parallel. PDFsharp document internals and stream decompression are not documented as thread-safe; concurrent access to dictionaries/streams can corrupt data or throw intermittently. Parallelize only independent byte decoding after first copying immutable stream bytes, or protect PDFsharp access with a document-level lock.

## Security and trust-boundary concerns

### Native codec plugins execute inside the app

`Services/NativeImageCodec.cs` loads user-provided DLLs and calls unmanaged function pointers in-process. A malformed or hostile codec can crash the app, corrupt memory, or execute with the user’s full rights. The UI warning is useful but is not isolation. Prefer an out-of-process codec worker with a narrow IPC protocol, or require signed/allow-listed plugins. If in-process loading remains, remove unsupported filters or validate every output format, including JPX/JBIG2; those paths are size/shape checked but not actually decoded (`NativeImageCodec.Validate`).

### Untrusted models/configuration need bounds

`OrientationModels.ReadSpec` validates array lengths and positive dimensions but does not cap width/height, `CropShare`, `Scale`, or standard deviations. A crafted ONNX sidecar can request enormous allocations or produce NaN/Infinity tensors. Add finite-value checks and conservative dimension/pixel limits before calibration or execution. Bound the settings JSON size before reading it.

### Protocol parsing breaks filenames containing commas

The shell extension percent-encodes each path and joins paths with commas (`Interop/ShellExtension.cpp:240-259`). `App.ParseGladhenUri` runs `HttpUtility.ParseQueryString` before splitting the `files` value, so `%2C` is decoded to `,` and a filename containing a comma is split into multiple paths. Parse the raw query value, split first, then URL-decode each element; add a round-trip test for commas, Unicode, `%`, `&`, and `?`.

### Packaging fallback can ship the wrong/missing shell extension

`Gladhen3.csproj` falls back to `$(SolutionDir)ShellExtension.dll` when architecture-specific native output is absent. The tracked source does not contain that root binary; local artifacts show the root fallback is x64. A clean x86 build can therefore omit the extension or copy an x64 DLL into an x86 package. Make the native build a required dependency and fail if the architecture-matched DLL is missing; never use a cross-architecture fallback.

## Structural and algorithm improvements

1. Move page editing into an immutable state/reducer layer. Keep `MainWindow` as a projection and command dispatcher. This makes undo, section naming, viewer position, and save snapshots deterministic.
2. Replace repeated full-list scans (`BuildItemIndexMap`, `Contains`, `IndexOf`, section rebuilds) with a maintained item-id/index map. For very large documents, use a gap buffer or rope for page order and a separate section-break index; this makes moves and inserts cheap without rebuilding every view.
3. Use a bounded producer/consumer pipeline for decode, encode, and PDF assembly with explicit byte/pixel budgets. The current channels limit some source-image work, but PDF stream inflation and embedded-image preparation still lack global limits.
4. Represent settings and save options as an immutable snapshot passed to services. Avoid reading `AppSettings.Current` from worker threads.
5. Introduce a single serialized operation queue for add, normalize, save, and activation commands, with cancellation and a document version check before applying results.
6. Add focused tests and fuzz/property tests for: pipe framing, URI round trips, EXIF rotations, section filename sanitization, undo/redo, viewer cancellation, malformed PDFs, decompression limits, model-spec validation, and codec output validation.

## Suggested priority order

1. Fix pipe framing/startup queuing, settings initialization, picker exception handling, and save-state snapshotting.
2. Add PDF/image/page/resource limits and close/dispose all cached native resources.
3. Fix filename sanitization, section-rename undo, reverse-single-selection behavior, and URI comma parsing.
4. Isolate native plugins and harden model/spec validation.
5. Add tests and make the native shell-extension build mandatory for each architecture.
