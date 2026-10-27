// Video decoding with the phone's own decoders (MediaExtractor + MediaCodec), for mobile/AndroidVideo.cs, which gives the
// engine's MediaFoundationReader (Waylith.VideoDecode) this body on Android. The engine plays MP4 (H.264) with Vulkan
// video decoding and, on Windows, with Media Foundation when that is missing; phone GPUs have no Vulkan video (an
// Adreno 825 lists no VK_KHR_video_*), so an MP4 had no decoder at all (2026-10-27: God Hands' demonstration clips,
// Achtung!'s feature videos). Frames come out as NV12, as Media Foundation hands them over: the engine's own converter
// (CpuNv12Converter) takes them to the GPU.
//
// The decoder writes into its own byte buffers, asked for NV12 (COLOR_FormatYUV420SemiPlanar); the layout it then
// reports (color format, stride, slice height, crop) is copied into one NV12 buffer: Y rows, then interleaved U,V rows,
// stride the frame's width (made even). An ImageReader surface was tried first: on this phone's decoder its YUV_420_888
// images had no CPU view of the U and V planes.
#include <android/log.h>
#include <media/NdkMediaCodec.h>
#include <media/NdkMediaExtractor.h>
#include <media/NdkMediaFormat.h>
#include <fcntl.h>
#include <stdint.h>
#include <stdio.h>
#include <stdlib.h>
#include <string.h>
#include <sys/stat.h>
#include <unistd.h>

#define LOG(...) __android_log_print(ANDROID_LOG_INFO, "port17", __VA_ARGS__)

// MediaCodecInfo.CodecCapabilities colour formats.
#define COLOR_I420 19
#define COLOR_NV12 21
#define COLOR_FLEXIBLE 0x7F420888

typedef struct
{
    AMediaExtractor* extractor;
    AMediaCodec* codec;
    int fd;
    int track;
    int width, height;          // the stream's frame size (the NV12 handed out)
    int stride, rows;           // of the NV12 buffer handed out
    int color, in_stride, in_slice; // the decoder's output layout
    int crop_x, crop_y, crop_w, crop_h;
    int64_t duration_us;
    double frame_rate;
    int color_standard, color_range;
    int input_done, output_done, said;
} Video;

// What the caller reads after pv_open: the frame's layout and the stream's details.
typedef struct
{
    int32_t width, height, stride, rows, crop_x, crop_y;
    double frame_rate, length;
    int32_t color_standard, color_range;
} VideoInfo;

static void close_video(Video* v)
{
    if (!v) return;
    if (v->codec) { AMediaCodec_stop(v->codec); AMediaCodec_delete(v->codec); }
    if (v->extractor) AMediaExtractor_delete(v->extractor);
    if (v->fd >= 0) close(v->fd);
    free(v);
}

void pv_close(void* handle)
{
    close_video((Video*)handle);
}

// The decoder's output layout from its output format (after INFO_OUTPUT_FORMAT_CHANGED, or at the start).
static void read_output_format(Video* v)
{
    AMediaFormat* f = AMediaCodec_getOutputFormat(v->codec);
    if (!f) return;
    int32_t color = 0, stride = 0, slice = 0, w = 0, h = 0, l = -1, t = -1, r = -1, b = -1;
    AMediaFormat_getInt32(f, AMEDIAFORMAT_KEY_COLOR_FORMAT, &color);
    AMediaFormat_getInt32(f, AMEDIAFORMAT_KEY_WIDTH, &w);
    AMediaFormat_getInt32(f, AMEDIAFORMAT_KEY_HEIGHT, &h);
    AMediaFormat_getInt32(f, "stride", &stride);
    AMediaFormat_getInt32(f, "slice-height", &slice);
    AMediaFormat_getInt32(f, "crop-left", &l);
    AMediaFormat_getInt32(f, "crop-top", &t);
    AMediaFormat_getInt32(f, "crop-right", &r);
    AMediaFormat_getInt32(f, "crop-bottom", &b);
    AMediaFormat_delete(f);
    if (color) v->color = color;
    v->in_stride = stride > 0 ? stride : (w > 0 ? w : v->width);
    v->in_slice = slice > 0 ? slice : (h > 0 ? h : v->height);
    if (l >= 0 && r >= l && b >= t && t >= 0)
    {
        v->crop_x = l;
        v->crop_y = t;
        v->crop_w = r - l + 1;
        v->crop_h = b - t + 1;
    }
    LOG("video: decoder output colour format 0x%x, %dx%d, stride %d, slice height %d, crop %d,%d %dx%d", v->color, w, h, v->in_stride, v->in_slice, v->crop_x, v->crop_y, v->crop_w, v->crop_h);
}

// Opens a video file; NULL when it has no video track or no decoder takes it (the reason is in the log, and in error).
void* pv_open(const char* path, VideoInfo* info, char* error, int error_size)
{
    Video* v = (Video*)calloc(1, sizeof(Video));
    if (!v) return NULL;
    v->fd = -1;
    v->track = -1;
    const char* why = NULL;
    AMediaFormat* format = NULL;
    struct stat st;
    v->fd = open(path, O_RDONLY | O_CLOEXEC);
    if (v->fd < 0 || fstat(v->fd, &st) != 0) { why = "the file cannot be opened"; goto fail; }
    v->extractor = AMediaExtractor_new();
    if (!v->extractor || AMediaExtractor_setDataSourceFd(v->extractor, v->fd, 0, st.st_size) != AMEDIA_OK) { why = "the container is not readable"; goto fail; }
    const char* mime = NULL;
    size_t tracks = AMediaExtractor_getTrackCount(v->extractor);
    for (size_t i = 0; i < tracks && v->track < 0; i++)
    {
        AMediaFormat* f = AMediaExtractor_getTrackFormat(v->extractor, i);
        const char* m = NULL;
        if (f && AMediaFormat_getString(f, AMEDIAFORMAT_KEY_MIME, &m) && m && strncmp(m, "video/", 6) == 0)
        {
            v->track = (int)i;
            format = f;
            mime = m;
        }
        else if (f) AMediaFormat_delete(f);
    }
    if (v->track < 0) { why = "no video track"; goto fail; }
    AMediaExtractor_selectTrack(v->extractor, (size_t)v->track);
    int32_t w = 0, h = 0, rate = 0;
    float rate_f = 0;
    AMediaFormat_getInt32(format, AMEDIAFORMAT_KEY_WIDTH, &w);
    AMediaFormat_getInt32(format, AMEDIAFORMAT_KEY_HEIGHT, &h);
    AMediaFormat_getInt64(format, AMEDIAFORMAT_KEY_DURATION, &v->duration_us);
    if (AMediaFormat_getInt32(format, AMEDIAFORMAT_KEY_FRAME_RATE, &rate) && rate > 0) v->frame_rate = rate;
    else if (AMediaFormat_getFloat(format, AMEDIAFORMAT_KEY_FRAME_RATE, &rate_f) && rate_f > 0) v->frame_rate = rate_f;
    AMediaFormat_getInt32(format, "color-standard", &v->color_standard);
    AMediaFormat_getInt32(format, "color-range", &v->color_range);
    if (w <= 0 || h <= 0) { why = "no frame size"; goto fail; }
    v->width = w;
    v->height = h;
    v->crop_w = w;
    v->crop_h = h;
    v->codec = AMediaCodec_createDecoderByType(mime);
    if (!v->codec) { why = "the phone has no decoder for this format"; goto fail; }
    // NV12 asked for; a decoder that refuses it gets its own format.
    AMediaFormat_setInt32(format, AMEDIAFORMAT_KEY_COLOR_FORMAT, COLOR_NV12);
    if (AMediaCodec_configure(v->codec, format, NULL, NULL, 0) != AMEDIA_OK)
    {
        AMediaFormat_delete(format);
        format = AMediaExtractor_getTrackFormat(v->extractor, (size_t)v->track);
        AMediaCodec_delete(v->codec);
        v->codec = AMediaCodec_createDecoderByType(mime);
        if (!v->codec || AMediaCodec_configure(v->codec, format, NULL, NULL, 0) != AMEDIA_OK) { why = "the decoder refused the stream"; goto fail; }
    }
    if (AMediaCodec_start(v->codec) != AMEDIA_OK) { why = "the decoder did not start"; goto fail; }
    v->color = COLOR_NV12;
    read_output_format(v);
    LOG("video: %s %dx%d, %.1f fps, %.1f s: %s", mime, w, h, v->frame_rate, v->duration_us / 1e6, path);
    AMediaFormat_delete(format);
    format = NULL;
    v->stride = (w + 1) & ~1;
    v->rows = (h + 1) & ~1;
    if (info)
    {
        info->width = w;
        info->height = h;
        info->stride = v->stride;
        info->rows = v->rows;
        info->crop_x = 0;
        info->crop_y = 0;
        info->frame_rate = v->frame_rate;
        info->length = v->duration_us / 1e6;
        info->color_standard = v->color_standard;
        info->color_range = v->color_range;
    }
    return v;
fail:
    LOG("video: %s: %s", why ? why : "failed", path);
    if (error && error_size > 0) snprintf(error, (size_t)error_size, "%s", why ? why : "failed");
    if (format) AMediaFormat_delete(format);
    close_video(v);
    return NULL;
}

// Feeds the decoder what it takes from the file.
static void feed(Video* v)
{
    while (!v->input_done)
    {
        ssize_t index = AMediaCodec_dequeueInputBuffer(v->codec, 0);
        if (index < 0) return;
        size_t size = 0;
        uint8_t* buffer = AMediaCodec_getInputBuffer(v->codec, (size_t)index, &size);
        ssize_t n = buffer ? AMediaExtractor_readSampleData(v->extractor, buffer, size) : -1;
        if (n < 0)
        {
            AMediaCodec_queueInputBuffer(v->codec, (size_t)index, 0, 0, 0, AMEDIACODEC_BUFFER_FLAG_END_OF_STREAM);
            v->input_done = 1;
            return;
        }
        int64_t pts = AMediaExtractor_getSampleTime(v->extractor);
        AMediaCodec_queueInputBuffer(v->codec, (size_t)index, 0, (size_t)n, (uint64_t)(pts < 0 ? 0 : pts), 0);
        AMediaExtractor_advance(v->extractor);
    }
}

// The decoder's frame (data, size bytes) into NV12 (stride v->stride, v->rows rows of Y, then v->rows / 2 rows of U,V).
static void copy_nv12(Video* v, const uint8_t* data, size_t size, uint8_t* out)
{
    int w = v->width, h = v->height;
    size_t stride = (size_t)v->in_stride, slice = (size_t)v->in_slice;
    if (stride < (size_t)w) stride = (size_t)w;
    if (slice < (size_t)h) slice = (size_t)h;
    for (int r = 0; r < h; r++)
    {
        size_t at = (size_t)r * stride;
        if (at + (size_t)w > size) break;
        memcpy(out + (size_t)r * v->stride, data + at, (size_t)w);
    }
    uint8_t* uv = out + (size_t)v->stride * v->rows;
    int cw = (w + 1) / 2, ch = (h + 1) / 2;
    if (v->color == COLOR_I420)
    {
        size_t cstride = stride / 2;
        const uint8_t* u = data + stride * slice;
        const uint8_t* vv = u + cstride * (slice / 2);
        for (int r = 0; r < ch; r++)
        {
            uint8_t* row = uv + (size_t)r * v->stride;
            for (int c = 0; c < cw; c++)
            {
                size_t i = (size_t)r * cstride + (size_t)c;
                row[c * 2] = (size_t)(u - data) + i < size ? u[i] : 128;
                row[c * 2 + 1] = (size_t)(vv - data) + i < size ? vv[i] : 128;
            }
        }
        return;
    }
    // NV12 and the flexible format (semi-planar in byte buffer mode on the decoders seen).
    const uint8_t* src = data + stride * slice;
    for (int r = 0; r < ch; r++)
    {
        size_t at = stride * slice + (size_t)r * stride;
        uint8_t* row = uv + (size_t)r * v->stride;
        if (at + (size_t)cw * 2 <= size) memcpy(row, src + (size_t)r * stride, (size_t)cw * 2);
        else memset(row, 128, (size_t)cw * 2);
    }
}

// The next frame into out (stride * rows * 3 / 2 bytes); its time in microseconds. 1: a frame, 0: the end, -1: an error.
int pv_read(void* handle, uint8_t* out, int out_size, int64_t* time_us, int32_t* crop)
{
    Video* v = (Video*)handle;
    if (!v || v->output_done) return 0;
    if (out_size < v->stride * v->rows * 3 / 2) return -1;
    for (int attempt = 0; attempt < 2000; attempt++)
    {
        feed(v);
        AMediaCodecBufferInfo info;
        ssize_t index = AMediaCodec_dequeueOutputBuffer(v->codec, &info, 5000);
        if (index == AMEDIACODEC_INFO_OUTPUT_FORMAT_CHANGED)
        {
            read_output_format(v);
            continue;
        }
        if (index == AMEDIACODEC_INFO_TRY_AGAIN_LATER || index == AMEDIACODEC_INFO_OUTPUT_BUFFERS_CHANGED) continue;
        if (index < 0) return -1;
        int end = (info.flags & AMEDIACODEC_BUFFER_FLAG_END_OF_STREAM) != 0;
        if (info.size <= 0)
        {
            AMediaCodec_releaseOutputBuffer(v->codec, (size_t)index, false);
            if (end) { v->output_done = 1; return 0; }
            continue;
        }
        size_t size = 0;
        uint8_t* buffer = AMediaCodec_getOutputBuffer(v->codec, (size_t)index, &size);
        if (buffer && (size_t)info.offset + (size_t)info.size <= size)
        {
            if (!v->said)
            {
                v->said = 1;
                LOG("video: first frame %d bytes (colour format 0x%x, stride %d, slice height %d)", info.size, v->color, v->in_stride, v->in_slice);
            }
            copy_nv12(v, buffer + info.offset, (size_t)info.size, out);
        }
        else memset(out, 128, (size_t)v->stride * v->rows * 3 / 2);
        AMediaCodec_releaseOutputBuffer(v->codec, (size_t)index, false);
        if (end) v->output_done = 1;
        *time_us = info.presentationTimeUs;
        if (crop)
        {
            crop[0] = v->crop_x;
            crop[1] = v->crop_y;
            crop[2] = v->crop_w;
            crop[3] = v->crop_h;
        }
        return 1;
    }
    return -1;
}

// To the key frame at or before the time; the decoder starts again from there.
void pv_seek(void* handle, double seconds)
{
    Video* v = (Video*)handle;
    if (!v) return;
    AMediaExtractor_seekTo(v->extractor, (int64_t)(seconds * 1e6), AMEDIAEXTRACTOR_SEEK_PREVIOUS_SYNC);
    AMediaCodec_flush(v->codec);
    v->input_done = 0;
    v->output_done = 0;
}
