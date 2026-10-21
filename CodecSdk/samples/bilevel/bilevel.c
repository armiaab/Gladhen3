/*
 * bilevel.c - a sample Gladhen3 codec: black and white, one bit per pixel, run-length encoded.
 *
 * For scanned text. Every pixel becomes ink or paper, split at the grey level that best
 * separates the two (Otsu's method), so the paper's tint, show-through and scanner noise all
 * disappear and a page costs an eighth of a byte per pixel before compression - and the long
 * runs of white paper are what run-length encoding is good at.
 *
 * It would ruin a photograph, so it declines anything with real colour in it: those images
 * come back GLADHEN_SKIP and stay exactly as they were.
 *
 * Build from a Developer Command Prompt for the architecture you want:
 *     cl /nologo /O2 /W4 /MT /LD bilevel.c /Fe:bilevel.dll
 * /MT links the C runtime in, so the DLL needs nothing installed beside it.
 */
#include "../../gladhen_codec.h"

#include <stdlib.h>
#include <string.h>

GLADHEN_CODEC_API uint32_t GLADHEN_CALL gladhen_codec_abi(void) { return GLADHEN_CODEC_ABI; }

GLADHEN_CODEC_API const char *GLADHEN_CALL gladhen_codec_name(void) { return "Black & white (1-bit)"; }

GLADHEN_CODEC_API const char *GLADHEN_CALL gladhen_codec_filter(void) { return "RunLengthDecode"; }

/* No state is shared between calls, so any number may run at once. */
GLADHEN_CODEC_API uint32_t GLADHEN_CALL gladhen_codec_flags(void) { return GLADHEN_THREAD_SAFE; }

GLADHEN_CODEC_API void GLADHEN_CALL gladhen_codec_free(void *data) { free(data); }

/* A pixel counts as coloured when its channels differ by more than this... */
#define COLOUR_SPREAD 48
/* ...and an image with more than 1 in this many coloured pixels is not a text scan. */
#define COLOUR_SHARE 20

/* The threshold that maximises the variance between the two classes it creates. */
static int otsu_threshold(const uint64_t histogram[256], uint64_t total)
{
    uint64_t weighted_total = 0;
    for (int i = 0; i < 256; i++) weighted_total += (uint64_t)i * histogram[i];

    uint64_t below = 0, weighted_below = 0;
    double best = -1.0;
    int threshold = 127;

    for (int t = 0; t < 256; t++) {
        below += histogram[t];
        if (below == 0) continue;

        uint64_t above = total - below;
        if (above == 0) break;

        weighted_below += (uint64_t)t * histogram[t];
        double mean_below = (double)weighted_below / (double)below;
        double mean_above = (double)(weighted_total - weighted_below) / (double)above;
        double difference = mean_below - mean_above;
        double between = (double)below * (double)above * difference * difference;

        if (between > best) {
            best = between;
            threshold = t;
        }
    }

    return threshold;
}

/*
 * PDF run-length encoding: a length byte of 0-127 copies the next length+1 bytes, 129-255
 * repeats the next byte 257-length times, and 128 ends the data. The output can be at most
 * one byte in 128 longer than the input, plus the end marker.
 */
static size_t run_length_encode(const uint8_t *in, size_t length, uint8_t *out)
{
    size_t i = 0, o = 0;

    while (i < length) {
        size_t run = 1;
        while (i + run < length && run < 128 && in[i + run] == in[i]) run++;

        if (run >= 2) {
            out[o++] = (uint8_t)(257 - run);
            out[o++] = in[i];
            i += run;
            continue;
        }

        /* A literal stretch, up to where the next repeat begins. */
        size_t start = i, literal = 0;
        while (i < length && literal < 128) {
            if (i + 1 < length && in[i] == in[i + 1]) break;
            i++;
            literal++;
        }

        out[o++] = (uint8_t)(literal - 1);
        memcpy(out + o, in + start, literal);
        o += literal;
    }

    out[o++] = 128;
    return o;
}

GLADHEN_CODEC_API int32_t GLADHEN_CALL gladhen_codec_encode(
    const gladhen_image *image, float quality, gladhen_output *output)
{
    (void)quality; /* a threshold has no quality to trade */

    const uint32_t width = image->width, height = image->height;
    const uint64_t pixels = (uint64_t)width * height;
    if (pixels == 0 || image->channels != 3) return -1;

    uint64_t histogram[256] = {0};
    uint64_t coloured = 0;

    for (uint32_t y = 0; y < height; y++) {
        const uint8_t *row = image->pixels + (size_t)y * image->stride;
        for (uint32_t x = 0; x < width; x++) {
            int r = row[x * 3], g = row[x * 3 + 1], b = row[x * 3 + 2];

            int high = r > g ? (r > b ? r : b) : (g > b ? g : b);
            int low = r < g ? (r < b ? r : b) : (g < b ? g : b);
            if (high - low > COLOUR_SPREAD) coloured++;

            histogram[(r * 299 + g * 587 + b * 114) / 1000]++;
        }
    }

    if (coloured * COLOUR_SHARE > pixels) return GLADHEN_SKIP;

    const int threshold = otsu_threshold(histogram, pixels);

    /* 1 bit per pixel, most significant bit first, each row padded to a whole byte. */
    const size_t row_bytes = (width + 7) / 8;
    const size_t packed_size = row_bytes * height;
    uint8_t *packed = calloc(packed_size, 1);
    uint8_t *encoded = malloc(packed_size + packed_size / 128 + 2);
    if (packed == NULL || encoded == NULL) {
        free(packed);
        free(encoded);
        return -2;
    }

    for (uint32_t y = 0; y < height; y++) {
        const uint8_t *row = image->pixels + (size_t)y * image->stride;
        uint8_t *bits = packed + (size_t)y * row_bytes;
        for (uint32_t x = 0; x < width; x++) {
            int r = row[x * 3], g = row[x * 3 + 1], b = row[x * 3 + 2];
            int luma = (r * 299 + g * 587 + b * 114) / 1000;

            /* DeviceGray: 1 is white. Everything brighter than the threshold is paper. */
            if (luma > threshold) bits[x >> 3] |= (uint8_t)(0x80u >> (x & 7));
        }
    }

    output->size = run_length_encode(packed, packed_size, encoded);
    output->data = encoded;
    output->channels = 1;
    output->bits_per_component = 1;

    free(packed);
    return GLADHEN_ENCODED;
}
