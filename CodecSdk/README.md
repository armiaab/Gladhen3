# Writing a Gladhen3 codec

A codec is a DLL that gives Gladhen3 another way to compress images. Once it is added, it
appears in **Tools > Settings > Compression method** next to the two built-in methods, and
every image Gladhen3 recompresses goes through it.

Gladhen3 does the rest. It decodes the image, scales it down to the DPI of the compression
level, hands your codec plain RGB pixels, checks what comes back, and keeps the result only if
it is smaller than the image already in the PDF.

## The interface

Everything is in [`gladhen_codec.h`](gladhen_codec.h): five C functions, plus an optional
sixth.

| Export | |
|---|---|
| `gladhen_codec_abi` | Returns `GLADHEN_CODEC_ABI` (1). |
| `gladhen_codec_name` | The name shown in Settings, UTF-8. |
| `gladhen_codec_filter` | The PDF filter that decodes your output: `DCTDecode`, `JPXDecode`, `FlateDecode`, `JBIG2Decode` or `RunLengthDecode`. |
| `gladhen_codec_encode` | Encodes one image. Return `GLADHEN_ENCODED`, `GLADHEN_SKIP` to leave the image alone, or any other value for an error. |
| `gladhen_codec_free` | Frees the buffer `encode` allocated. |
| `gladhen_codec_flags` | Optional. Return `GLADHEN_THREAD_SAFE` if `encode` can run on several threads at once. |

Because it is a plain C interface, a codec can be written in anything that builds a DLL: C,
C++, Rust, Zig, or C# with Native AOT. It also means existing libraries can be wrapped directly
in a few dozen lines. Some that are worth wrapping:

- **mozjpeg** or **jpegli** — `DCTDecode`, noticeably smaller JPEGs at the same quality
- **OpenJPEG** — `JPXDecode`, JPEG 2000
- **jbig2enc** — `JBIG2Decode`, the best compression there is for black-and-white scans

## What Gladhen3 checks

Your output is written into the PDF only if it passes these checks:

- `FlateDecode` and `RunLengthDecode` output is decoded, and must be exactly
  `ceil(width × channels × bits / 8) × height` bytes.
- `DCTDecode` output must be a JPEG with the input's width and height.
- `JBIG2Decode` output must be declared as 1 channel at 1 bit.

Anything that fails is counted. The image keeps its original encoding, and when the save
finishes the user is told how many images the codec failed on. `GLADHEN_SKIP` is not a
failure; use it for images your codec is not meant for.

## The sample

[`samples/bilevel`](samples/bilevel/bilevel.c) is a complete codec in about 160 lines of C. It
turns scanned text into black and white at one bit per pixel, and run-length encodes it.
Photographs are declined with `GLADHEN_SKIP`. On an A4 page scanned at 300 DPI it produces
17 KB, where JPEG at the Medium level produces 275 KB.

Build it from a *Developer Command Prompt for Visual Studio*:

```
cd CodecSdk\samples\bilevel
cl /nologo /O2 /W4 /MT /LD bilevel.c /Fe:bilevel.dll
```

Use the x64 prompt for 64-bit Windows and the x86 prompt for 32-bit. Gladhen3 refuses a DLL
built for the other architecture and says why.

## Installing and removing

**Add codec…** in Settings copies the DLL into Gladhen3's own data folder and loads it once
to check it. You can delete the original afterwards. **Remove** deletes Gladhen3's copy.

A codec runs inside Gladhen3 with the same access to your files that Gladhen3 has, and a crash
in it closes the app. Only add codecs you trust.
