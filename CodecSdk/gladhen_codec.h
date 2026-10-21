/*
 * gladhen_codec.h - the interface a Gladhen3 image codec DLL implements.
 *
 * A codec is a DLL that turns the pixels of one image into the bytes of a PDF image stream.
 * Gladhen3 calls it for every image it recompresses: it decodes the image, scales it to the
 * DPI the compression level asks for, and hands the result to the codec. Whatever the codec
 * returns is checked and then written into the PDF in place of the original image - but only
 * if it is smaller.
 *
 * Install one from Tools > Settings > Add codec. Gladhen3 copies the DLL into its own data
 * folder, so the original can be moved or deleted afterwards.
 *
 * Build for the same architecture as Gladhen3 itself (x64 on most PCs, x86 on 32-bit Windows);
 * a DLL for the other one is refused when you add it.
 *
 * The codec runs inside Gladhen3. A crash in it ends the app, and it can do anything the user
 * can do, which is why Gladhen3 asks before installing one.
 *
 * All functions use the C calling convention and must be exported undecorated, which is what
 * GLADHEN_CODEC_API below does with MSVC, clang and MinGW.
 */
#ifndef GLADHEN_CODEC_H
#define GLADHEN_CODEC_H

#include <stddef.h>
#include <stdint.h>

#ifdef __cplusplus
#define GLADHEN_EXTERN_C extern "C"
#else
#define GLADHEN_EXTERN_C
#endif

#define GLADHEN_CODEC_API GLADHEN_EXTERN_C __declspec(dllexport)
#define GLADHEN_CALL __cdecl

/* The version of this interface. gladhen_codec_abi() must return exactly this. */
#define GLADHEN_CODEC_ABI 1

/* gladhen_codec_encode() results. */
#define GLADHEN_ENCODED 0   /* *output holds the encoded image */
#define GLADHEN_SKIP    1   /* leave this image as it is - not an error, nothing is reported */
/* Any other value is an error. The image is kept as it was, and the user is told how many
   images the codec failed on. */

/* gladhen_codec_flags() bits. */
#define GLADHEN_THREAD_SAFE 0x1u /* encode may be called from several threads at once */

/*
 * The image to encode.
 *
 * In this version of the interface the pixels are always 8-bit RGB: three bytes per pixel in
 * R, G, B order, rows top to bottom, no padding beyond `stride`. Grey and transparent sources
 * have already been converted. The memory belongs to Gladhen3 and is valid only for the
 * duration of the call.
 */
typedef struct gladhen_image {
    uint32_t width;
    uint32_t height;
    uint32_t stride;         /* bytes from the start of one row to the next */
    uint32_t channels;       /* always 3 in ABI 1 */
    const uint8_t *pixels;
} gladhen_image;

/*
 * What the codec produced.
 *
 * `data` is allocated by the codec, and handed back to gladhen_codec_free() once Gladhen3 has
 * copied it - whatever gladhen_codec_encode() returned, so free must accept anything encode can
 * leave here, including NULL.
 *
 * The output always has the input's width and height. `channels` and `bits_per_component`
 * describe the samples the stream decodes to: they become the image's /ColorSpace (1 is
 * DeviceGray, 3 is DeviceRGB) and /BitsPerComponent.
 */
typedef struct gladhen_output {
    uint8_t *data;
    size_t size;
    uint32_t channels;           /* 1 or 3 */
    uint32_t bits_per_component; /* 1, 2, 4 or 8 */
} gladhen_output;

/* Required. Returns GLADHEN_CODEC_ABI. Called first; nothing else is called if it differs. */
GLADHEN_CODEC_API uint32_t GLADHEN_CALL gladhen_codec_abi(void);

/* Required. A short UTF-8 name for the compression list, e.g. "mozjpeg". Static storage. */
GLADHEN_CODEC_API const char *GLADHEN_CALL gladhen_codec_name(void);

/*
 * Required. The PDF filter that decodes this codec's output. Static storage. One of:
 *
 *   "DCTDecode"        a complete baseline or progressive JPEG file; 8 bits per component
 *   "JPXDecode"        a JPEG 2000 codestream or JP2 file
 *   "FlateDecode"      a zlib stream of raw samples, rows packed to whole bytes, no predictor
 *   "JBIG2Decode"      an embedded JBIG2 stream with no global segments; 1-channel, 1-bit
 *   "RunLengthDecode"  PDF run-length encoding of raw samples, rows packed to whole bytes
 *
 * In 1-bit grey, as for any DeviceGray image, 0 is black and 1 is white.
 *
 * Gladhen3 decodes FlateDecode and RunLengthDecode output and checks that it is exactly the
 * image's size, and checks a DCTDecode JPEG's dimensions, before writing any of it.
 */
GLADHEN_CODEC_API const char *GLADHEN_CALL gladhen_codec_filter(void);

/*
 * Required. Encodes one image.
 *
 * `quality` is 0.0 to 1.0, from the compression level the user chose (0.85, 0.70 or 0.50).
 * A codec with no quality setting can ignore it.
 */
GLADHEN_CODEC_API int32_t GLADHEN_CALL gladhen_codec_encode(
    const gladhen_image *image, float quality, gladhen_output *output);

/* Required. Releases the `data` of a gladhen_output. Must accept NULL. */
GLADHEN_CODEC_API void GLADHEN_CALL gladhen_codec_free(void *data);

/*
 * Optional. GLADHEN_THREAD_SAFE if gladhen_codec_encode may run on several threads at once.
 * Without it, Gladhen3 calls encode on one image at a time; images are still decoded and
 * scaled in parallel.
 */
GLADHEN_CODEC_API uint32_t GLADHEN_CALL gladhen_codec_flags(void);

#endif /* GLADHEN_CODEC_H */
