# Gladhen 3

Gladhen 3 is a small, fast PDF tool for Windows. Drop in images and PDFs, fix the pages, and
save: merge, split, rotate, reorder, compress.

Available in **English** and **Bahasa Indonesia**. It follows the Windows display language,
or you can choose one in **Tools > Settings > Language**.

## Features

### Quick page edits

Each edit works on the page you clicked, or on every selected page in select mode. Right-click
a page for all of them.

- **Rotate** pages left, right or 180°. A turned page is saved with its rotation flag
  (`/Rotate`), so nothing on it is redrawn or recompressed.
- **Normalize orientation** turns every page so it reads the right way up: scans fed in
  sideways or upside down, phone photos, PDFs assembled from turned pages. With nothing
  selected it checks every page. A PDF's own text says which way it runs; pictures of pages are
  read by Windows' built-in text recognition, every way round. A page it cannot judge, such as
  a photo with no text, is left as it is, and *Select them* picks those out for a look. One
  undo puts everything back.
- **Duplicate** pages, or **insert a blank page** the size of the page before it.
- **Reverse** the order of the selected pages, or of every page. This fixes a stack that went
  through the scanner face down.
- **Select odd or even pages**, for example to turn every back side of a duplex scan.
- **Save pages as a separate PDF** to pull a few pages out of a larger file.
- **Undo and redo** every edit.
- **Save straight back over the original.** When every page comes from one PDF, Save opens in
  that PDF's folder with its name filled in. The new file is written beside it and only swapped
  in once it is complete, so a failed save never damages the original.

### Page viewer

Double-click a page, or press Enter, to open it full-window.

- Pages are shown turned the way they will be saved, and stay sharp at any zoom.
- Every page edit is on the viewer's toolbar, and works on the page you are looking at.
- A filmstrip along the bottom jumps to any page.
- Left/Right or Page Up/Down change pages, Home/End jump to the first or last, + and − zoom,
  Ctrl+0 fits the page, and Esc goes back to the list. Double-click zooms in on that spot.

### Building and splitting

- Convert one or many images into a PDF
- Merge PDFs with each other and with images
- Split into several PDFs in one go: before any page, by source file, every N pages, or into N
  parts. Each part's file name can be edited in place.
- Reorder by dragging, with Ctrl+Shift+↑/↓, or sort by name or type
- Choose the paper size (Automatic, A4, Letter, Legal, A3 or custom), orientation and margins,
  including a different margin on each side
- Bookmarks and document information are kept

### Compression

- **Low / Medium / High** presets scale images down to 300 / 150 / 96 DPI. Text and drawings
  stay selectable and sharp, and an image is only re-encoded if that makes it smaller.
- **Choose how images are encoded**:
  - **JPEG** gives the smallest files, best for photos and scans.
  - **Lossless** keeps every pixel (Deflate with PNG predictors), for screenshots and
    diagrams.
  - **Your own codec**: add any DLL that implements the small C interface in
    [`CodecSdk`](CodecSdk/README.md). It could wrap mozjpeg, JPEG 2000, JBIG2, or anything
    else a PDF can hold. A sample black-and-white codec is included.
- **See the estimated file size before you save**, measured with the method you chose.

### Everything else

- Thumbnail and list views
- Explorer context menu: right-click a file and choose *Open with Gladhen3*
- Drag and drop straight into the window

## Supported files

**Images** — JPG, JPEG, PNG, BMP, GIF, TIFF, TIF, WEBP, ICO, JXR, WDP, HDP, DDS

**Documents** — PDF

Every image format is decoded by Windows itself, and a format is only offered if your PC has
the decoder for it. HEIC/HEIF and camera RAW are not supported: they need optional Store
codecs (HEIC also needs Microsoft's paid HEVC extension), so on most PCs they could be added
but never saved.

Photos are placed the way they were taken: the orientation a phone records with each picture is
honoured whether or not the image is compressed.

*Normalize orientation* reads pictures with the text recognition that comes with most Windows
display languages. Windows has none for Indonesian, so on a PC set up in Indonesian alone, add
English (or any other language) in Settings > Time & language > Language & region. PDF pages
with a text layer are checked without it.

The Explorer context menu appears on JPG, JPEG, PNG, BMP, GIF, TIFF, TIF and PDF files.

## Keyboard shortcuts

| Shortcut | Action |
|---|---|
| `Ctrl` + `O` | Add files |
| `Enter` | Open the page in the viewer |
| `Ctrl` + `R` / `Ctrl` + `L` | Rotate right / left |
| `Ctrl` + `Shift` + `R` | Normalize orientation |
| `Ctrl` + `D` | Duplicate |
| `Ctrl` + `B` | Insert a blank page after |
| `Delete` | Remove |
| `Ctrl` + `Z` / `Ctrl` + `Y` | Undo / redo |
| `Ctrl` + `Shift` + `S` | Save the pages as a separate PDF |
| `Ctrl` + `Enter` | Split before this page |
| `Ctrl` + `Shift` + `↑` / `↓` | Move the page up / down |
| `Ctrl` + `E` | Turn select mode on or off |
| `Ctrl` + `A` | Select all |

In the viewer:

| Shortcut | Action |
|---|---|
| `←` `→` / `Page Up` `Page Down` | Previous / next page |
| `Home` / `End` | First / last page |
| `+` / `−` | Zoom in / out |
| `Ctrl` + `0` | Fit the whole page |
| `Esc` | Back to all pages |

## Screenshots

Every page of every file you have added, ready to be turned, reordered and saved:

![Thumbnail view](./docs/thumbnail-view.png)

The viewer, with a page turned 90° and the filmstrip below:

![Page viewer](./docs/viewer.png)

List view, with the estimated output size next to the Save button:

![List view](./docs/list-view.png)

Settings: page setup, compression level and method, and language:

![Settings](./docs/pdf-settings.png)

In Bahasa Indonesia, with a page's right-click menu open:

![Bahasa Indonesia](./docs/indonesian.png)

Right-click a supported file in Explorer:

![Explorer context menu](./docs/context-menu.png)

## Installation

[Get it from the Microsoft Store](https://apps.microsoft.com/detail/9PKH3VS88B8Q?hl=en-us&gl=US&ocid=pdpshare)

Install and run — there is nothing else to download. The .NET runtime is carried inside the
package, and the Windows App Runtime is a framework dependency the Store installs for you.

Requires Windows 10 version 1809 (build 17763) or newer, x64 or x86. The download is about
43 MB for x64 and 40 MB for x86; the Store sends only the one your PC needs.

## For developers

- **Translations** live in `Strings/<language>/Resources.resw`. Every string has to exist in
  every language with the same `{0}` placeholders: a missing string shows up as blank text, and
  a missing placeholder throws when the message is shown. ResXManager shows the gaps.
- **Codecs**: see [`CodecSdk/README.md`](CodecSdk/README.md) for the interface, the checks
  Gladhen 3 runs on a codec's output, and a sample to start from.
