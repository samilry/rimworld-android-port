// libwaylithaudio.so: the engine's audio library (Waylith.Audio.MiniAudio.MiniAudioBackend P/Invokes it as
// "waylithaudio"), rebuilt on miniaudio 0.11.22 for Android (AAudio, OpenSL ES fallback). The API is the engine's
// (host/stubs/waylithaudio.c lists the signatures, taken from the P/Invoke declarations):
//   sounds   shared data: decoded PCM (s16, the engine reads and writes it through wa_sound_pcm), a compressed
//            stream in memory or in a file (decoded per voice), or a user stream (the engine's callbacks)
//   voices   one ma_sound each, with optional low-pass, high-pass and echo nodes between it and its group
//   groups   ma_sound_group; handle 0 is the engine's endpoint
// Handles are indices with a generation, so that a voice the engine still refers to after its automatic release
// is harmless. Reverb is a Freeverb node of our own (the engine passes Freeverb-style parameters); the mix matrix
// is accepted and ignored.
#include <stdint.h>
#include <stddef.h>
#include <stdlib.h>
#include <string.h>
#include <math.h>
#include <pthread.h>
#include <android/log.h>

#define STB_VORBIS_HEADER_ONLY
#include "stb_vorbis.c"
#define MINIAUDIO_IMPLEMENTATION
#define MA_NO_ENCODING
#define MA_NO_GENERATION
#define MA_ENABLE_ONLY_SPECIFIC_BACKENDS
#define MA_ENABLE_AAUDIO
#define MA_ENABLE_OPENSL
#define MA_ENABLE_NULL
#include "miniaudio.h"
#undef STB_VORBIS_HEADER_ONLY
#include "stb_vorbis.c"

#define EXPORT __attribute__((visibility("default")))
#define LOGI(...) __android_log_print(ANDROID_LOG_INFO, "port17", __VA_ARGS__)
#define LOGW(...) __android_log_print(ANDROID_LOG_WARN, "port17", __VA_ARGS__)

typedef int (*wa_read_fn)(void* user, float* output, uint64_t frames);
typedef int (*wa_seek_fn)(void* user, uint64_t frame);

enum { KIND_PCM = 0, KIND_MEMORY_STREAM = 2, KIND_FILE_STREAM = 3, KIND_USER = 4 };

typedef struct Sound
{
    int kind;
    int16_t* pcm;            // KIND_PCM: interleaved s16, owned
    uint64_t frames;
    uint32_t channels, rate;
    const void* data;        // KIND_MEMORY_STREAM
    size_t length;
    void* owned;             // a copy of the stream data when the engine asked for one
    char* path;              // KIND_FILE_STREAM
    wa_read_fn read;         // KIND_USER
    wa_seek_fn seek;
    void* user;
    int refs;                // the handle plus every voice playing it
} Sound;

// The engine's stream source as a miniaudio data source (f32 interleaved).
typedef struct UserSource
{
    ma_data_source_base base;
    Sound* sound;
    uint64_t cursor;
} UserSource;

// A pass-through node that keeps the last frames it saw, for wa_voice_tap_read.
typedef struct TapNode
{
    ma_node_base base;
    float* ring;             // capacity * channels
    uint32_t capacity, channels;
    volatile uint64_t written; // frames written in total
} TapNode;

typedef struct Voice
{
    int used;
    uint32_t gen;
    Sound* sound;
    ma_audio_buffer_ref bufref;
    ma_decoder decoder;
    int decoderInit;
    UserSource userSource;
    int userInit;
    ma_sound snd;
    int sndInit;
    ma_sound_group* group;
    float volume;
    int muted, autoRelease, stopped;
    volatile int ended;
    int lpfOn, hpfOn, echoOn, lpfInit, hpfInit, delayInit;
    ma_lpf_node lpf;
    ma_hpf_node hpf;
    ma_delay_node delay;
    TapNode* tap;
    struct ReverbNode* reverb;
    int reverbOn;
} Voice;

typedef struct Group
{
    int used;
    uint32_t gen;
    ma_sound_group group;
    float volume;
    int muted;
} Group;

#define MAX_VOICES 1024
#define MAX_GROUPS 64

static ma_engine g_engine;
static ma_context g_nullContext;
static int g_initialized, g_nullBackend;
static ma_result g_lastError;
static uint32_t g_rate = 48000, g_channels = 2;
static pthread_mutex_t g_lock = PTHREAD_MUTEX_INITIALIZER;
static Voice g_voices[MAX_VOICES];
static Group g_groups[MAX_GROUPS];
static uint64_t g_voicesCreated, g_updates;
static void* (*g_alloc)(size_t);
static void* (*g_realloc)(void*, size_t);
static void (*g_free)(void*);

static void* host_malloc(size_t size, void* user) { (void)user; return g_alloc ? g_alloc(size) : malloc(size); }
static void* host_realloc(void* p, size_t size, void* user) { (void)user; return g_realloc ? g_realloc(p, size) : realloc(p, size); }
static void host_free(void* p, void* user) { (void)user; if (!p) return; if (g_free) g_free(p); else free(p); }
static ma_allocation_callbacks g_allocCallbacks;

// ---- handles ----
static uint64_t make_handle(uint32_t index, uint32_t gen) { return ((uint64_t)gen << 32) | (index + 1); }
static Voice* voice_of(uint64_t handle)
{
    uint32_t index = (uint32_t)(handle & 0xffffffffu);
    uint32_t gen = (uint32_t)(handle >> 32);
    if (index == 0 || index > MAX_VOICES) return NULL;
    Voice* v = &g_voices[index - 1];
    return (v->used && v->gen == gen) ? v : NULL;
}
static Group* group_of(uint64_t handle)
{
    uint32_t index = (uint32_t)(handle & 0xffffffffu);
    uint32_t gen = (uint32_t)(handle >> 32);
    if (index == 0 || index > MAX_GROUPS) return NULL;
    Group* g = &g_groups[index - 1];
    return (g->used && g->gen == gen) ? g : NULL;
}

// ---- sounds ----
static void sound_free(Sound* s)
{
    if (s->pcm) free(s->pcm);
    if (s->owned) free(s->owned);
    if (s->path) free(s->path);
    free(s);
}
static void sound_unref(Sound* s)
{
    if (s && --s->refs <= 0) sound_free(s);
}

// Decodes a whole stream (memory or file) to interleaved s16 at its own rate.
static Sound* decode_whole(const void* data, size_t length, const char* path)
{
    ma_decoder_config cfg = ma_decoder_config_init(ma_format_s16, 0, 0);
    ma_decoder dec;
    ma_result r = data ? ma_decoder_init_memory(data, length, &cfg, &dec) : ma_decoder_init_file(path, &cfg, &dec);
    if (r != MA_SUCCESS) { g_lastError = r; return NULL; }
    ma_uint64 total = 0;
    ma_decoder_get_length_in_pcm_frames(&dec, &total);
    uint32_t channels = dec.outputChannels, rate = dec.outputSampleRate;
    uint64_t capacity = total ? total : 48000;
    int16_t* pcm = (int16_t*)malloc((size_t)(capacity * channels * sizeof(int16_t)));
    uint64_t have = 0;
    while (pcm)
    {
        if (have == capacity)
        {
            capacity *= 2;
            int16_t* bigger = (int16_t*)realloc(pcm, (size_t)(capacity * channels * sizeof(int16_t)));
            if (!bigger) { free(pcm); pcm = NULL; break; }
            pcm = bigger;
        }
        ma_uint64 got = 0;
        r = ma_decoder_read_pcm_frames(&dec, pcm + have * channels, capacity - have, &got);
        have += got;
        if (r != MA_SUCCESS || got == 0) break;
    }
    ma_decoder_uninit(&dec);
    if (!pcm) { g_lastError = MA_OUT_OF_MEMORY; return NULL; }
    Sound* s = (Sound*)calloc(1, sizeof(Sound));
    s->kind = KIND_PCM;
    s->pcm = pcm;
    s->frames = have;
    s->channels = channels;
    s->rate = rate;
    s->refs = 1;
    return s;
}

EXPORT uint64_t wa_sound_load_pcm(void* data, uint64_t frames, uint32_t channels, uint32_t rate, int format)
{
    if (format != 1 || !data || channels == 0) { g_lastError = MA_INVALID_ARGS; return 0; }
    Sound* s = (Sound*)calloc(1, sizeof(Sound));
    size_t bytes = (size_t)(frames * channels * sizeof(int16_t));
    s->pcm = (int16_t*)malloc(bytes ? bytes : 2);
    memcpy(s->pcm, data, bytes);
    s->kind = KIND_PCM;
    s->frames = frames;
    s->channels = channels;
    s->rate = rate;
    s->refs = 1;
    return (uint64_t)(uintptr_t)s;
}

EXPORT uint64_t wa_sound_load_memory(void* data, size_t length)
{
    Sound* s = decode_whole(data, length, NULL);
    return (uint64_t)(uintptr_t)s;
}

EXPORT uint64_t wa_sound_load_memory_stream(void* data, size_t length, int copy)
{
    // Probe first, so that undecodable data is reported now (the engine then tries the extracted Vorbis stream).
    ma_decoder_config cfg = ma_decoder_config_init(ma_format_f32, 0, 0);
    ma_decoder dec;
    ma_result r = ma_decoder_init_memory(data, length, &cfg, &dec);
    if (r != MA_SUCCESS) { g_lastError = r; return 0; }
    ma_uint64 total = 0;
    ma_decoder_get_length_in_pcm_frames(&dec, &total);
    Sound* s = (Sound*)calloc(1, sizeof(Sound));
    s->kind = KIND_MEMORY_STREAM;
    s->channels = dec.outputChannels;
    s->rate = dec.outputSampleRate;
    s->frames = total;
    ma_decoder_uninit(&dec);
    if (copy)
    {
        s->owned = malloc(length ? length : 1);
        memcpy(s->owned, data, length);
        s->data = s->owned;
    }
    else
    {
        s->data = data;
    }
    s->length = length;
    s->refs = 1;
    return (uint64_t)(uintptr_t)s;
}

EXPORT uint64_t wa_sound_load_file_stream(uint8_t* path)
{
    ma_decoder_config cfg = ma_decoder_config_init(ma_format_f32, 0, 0);
    ma_decoder dec;
    ma_result r = ma_decoder_init_file((const char*)path, &cfg, &dec);
    if (r != MA_SUCCESS) { g_lastError = r; return 0; }
    ma_uint64 total = 0;
    ma_decoder_get_length_in_pcm_frames(&dec, &total);
    Sound* s = (Sound*)calloc(1, sizeof(Sound));
    s->kind = KIND_FILE_STREAM;
    s->channels = dec.outputChannels;
    s->rate = dec.outputSampleRate;
    s->frames = total;
    ma_decoder_uninit(&dec);
    s->path = strdup((const char*)path);
    s->refs = 1;
    return (uint64_t)(uintptr_t)s;
}

EXPORT uint64_t wa_sound_create_user(uint32_t channels, uint32_t rate, uint64_t lengthFrames, void* read, void* seek, void* user)
{
    if (!read || channels == 0) { g_lastError = MA_INVALID_ARGS; return 0; }
    Sound* s = (Sound*)calloc(1, sizeof(Sound));
    s->kind = KIND_USER;
    s->channels = channels;
    s->rate = rate;
    s->frames = lengthFrames;
    s->read = (wa_read_fn)read;
    s->seek = (wa_seek_fn)seek;
    s->user = user;
    s->refs = 1;
    return (uint64_t)(uintptr_t)s;
}

EXPORT int wa_sound_kind(uint64_t sound) { Sound* s = (Sound*)(uintptr_t)sound; return s ? s->kind : -1; }
EXPORT uint64_t wa_sound_length_frames(uint64_t sound) { Sound* s = (Sound*)(uintptr_t)sound; return s ? s->frames : 0; }
EXPORT uint32_t wa_sound_channels(uint64_t sound) { Sound* s = (Sound*)(uintptr_t)sound; return s ? s->channels : 0; }
EXPORT uint32_t wa_sound_rate(uint64_t sound) { Sound* s = (Sound*)(uintptr_t)sound; return s ? s->rate : 0; }
EXPORT int16_t* wa_sound_pcm(uint64_t sound) { Sound* s = (Sound*)(uintptr_t)sound; return s ? s->pcm : NULL; }
EXPORT void wa_sound_release(uint64_t sound)
{
    Sound* s = (Sound*)(uintptr_t)sound;
    if (!s) return;
    pthread_mutex_lock(&g_lock);
    sound_unref(s);
    pthread_mutex_unlock(&g_lock);
}

EXPORT int wa_probe_memory(void* data, size_t length, uint64_t* frames, uint32_t* channels, uint32_t* rate)
{
    ma_decoder_config cfg = ma_decoder_config_init(ma_format_s16, 0, 0);
    ma_decoder dec;
    ma_result r = ma_decoder_init_memory(data, length, &cfg, &dec);
    if (r != MA_SUCCESS) { g_lastError = r; return (int)r; }
    ma_uint64 total = 0;
    ma_decoder_get_length_in_pcm_frames(&dec, &total);
    if (frames) *frames = total;
    if (channels) *channels = dec.outputChannels;
    if (rate) *rate = dec.outputSampleRate;
    ma_decoder_uninit(&dec);
    return 0;
}

EXPORT int wa_probe_file(uint8_t* path, uint64_t* frames, uint32_t* channels, uint32_t* rate)
{
    ma_decoder_config cfg = ma_decoder_config_init(ma_format_s16, 0, 0);
    ma_decoder dec;
    ma_result r = ma_decoder_init_file((const char*)path, &cfg, &dec);
    if (r != MA_SUCCESS) { g_lastError = r; return (int)r; }
    ma_uint64 total = 0;
    ma_decoder_get_length_in_pcm_frames(&dec, &total);
    if (frames) *frames = total;
    if (channels) *channels = dec.outputChannels;
    if (rate) *rate = dec.outputSampleRate;
    ma_decoder_uninit(&dec);
    return 0;
}

// ---- the user stream as a data source ----
static ma_result us_read(ma_data_source* ds, void* out, ma_uint64 frameCount, ma_uint64* framesRead)
{
    UserSource* u = (UserSource*)ds;
    int n = u->sound->read(u->sound->user, (float*)out, frameCount);
    if (n < 0) n = 0;
    if ((ma_uint64)n > frameCount) n = (int)(n / u->sound->channels); // a count of samples rather than frames
    if ((ma_uint64)n > frameCount) n = (int)frameCount;
    if (framesRead) *framesRead = (ma_uint64)n;
    u->cursor += (uint64_t)n;
    return n == 0 ? MA_AT_END : MA_SUCCESS;
}
static ma_result us_seek(ma_data_source* ds, ma_uint64 frameIndex)
{
    UserSource* u = (UserSource*)ds;
    if (!u->sound->seek) return MA_NOT_IMPLEMENTED;
    if (u->sound->seek(u->sound->user, frameIndex) != 0) return MA_ERROR;
    u->cursor = frameIndex;
    return MA_SUCCESS;
}
static ma_result us_get_format(ma_data_source* ds, ma_format* format, ma_uint32* channels, ma_uint32* rate, ma_channel* map, size_t mapCap)
{
    UserSource* u = (UserSource*)ds;
    if (format) *format = ma_format_f32;
    if (channels) *channels = u->sound->channels;
    if (rate) *rate = u->sound->rate;
    if (map) ma_channel_map_init_standard(ma_standard_channel_map_default, map, mapCap, u->sound->channels);
    return MA_SUCCESS;
}
static ma_result us_get_cursor(ma_data_source* ds, ma_uint64* cursor) { *cursor = ((UserSource*)ds)->cursor; return MA_SUCCESS; }
static ma_result us_get_length(ma_data_source* ds, ma_uint64* length) { *length = ((UserSource*)ds)->sound->frames; return MA_SUCCESS; }
static ma_data_source_vtable g_userVtable = { us_read, us_seek, us_get_format, us_get_cursor, us_get_length, NULL, 0 };

// ---- the tap node ----
static void tap_process(ma_node* node, const float** in, ma_uint32* inFrames, float** out, ma_uint32* outFrames)
{
    TapNode* t = (TapNode*)node;
    ma_uint32 frames = *inFrames < *outFrames ? *inFrames : *outFrames;
    memcpy(out[0], in[0], (size_t)frames * t->channels * sizeof(float));
    uint64_t written = t->written;
    for (ma_uint32 i = 0; i < frames; i++)
    {
        uint32_t slot = (uint32_t)((written + i) % t->capacity);
        memcpy(t->ring + (size_t)slot * t->channels, in[0] + (size_t)i * t->channels, t->channels * sizeof(float));
    }
    t->written = written + frames;
    *inFrames = frames;
    *outFrames = frames;
}
static ma_node_vtable g_tapVtable = { tap_process, NULL, 1, 1, MA_NODE_FLAG_PASSTHROUGH };

// ---- reverb: a Freeverb (Schroeder) node, per voice ----
#define RV_COMBS 8
#define RV_ALLPASSES 4
static const int rv_combTuning[RV_COMBS] = { 1116, 1188, 1277, 1356, 1422, 1491, 1557, 1617 };
static const int rv_allpassTuning[RV_ALLPASSES] = { 556, 441, 341, 225 };
#define RV_STEREO_SPREAD 23
#define RV_MAX_CHANNELS 2

typedef struct { float* buf; int size, index; float filterStore; } RvComb;
typedef struct { float* buf; int size, index; } RvAllpass;
typedef struct ReverbNode
{
    ma_node_base base;
    uint32_t channels;
    RvComb comb[RV_MAX_CHANNELS][RV_COMBS];
    RvAllpass allpass[RV_MAX_CHANNELS][RV_ALLPASSES];
    float feedback, damp, wet1, wet2, dry;
    float highCutCoef, highCutState[RV_MAX_CHANNELS];
} ReverbNode;

static void rv_set(ReverbNode* r, float roomsize, float damp, float wet, float dry, float width, float highCutHz)
{
    r->feedback = roomsize * 0.28f + 0.7f;
    r->damp = damp * 0.4f;
    r->wet1 = wet * 3.0f * (width / 2.0f + 0.5f);
    r->wet2 = wet * 3.0f * ((1.0f - width) / 2.0f);
    r->dry = dry;
    r->highCutCoef = highCutHz > 0.0f && highCutHz < g_rate * 0.49f ? (float)(1.0 - exp(-2.0 * 3.14159265 * highCutHz / g_rate)) : 1.0f;
}

static void rv_process(ma_node* node, const float** in, ma_uint32* inFrames, float** out, ma_uint32* outFrames)
{
    ReverbNode* r = (ReverbNode*)node;
    ma_uint32 frames = *inFrames < *outFrames ? *inFrames : *outFrames;
    uint32_t ch = r->channels;
    for (ma_uint32 f = 0; f < frames; f++)
    {
        float input = 0.0f;
        for (uint32_t c = 0; c < ch; c++) input += in[0][f * ch + c];
        input *= 0.015f;
        float outL = 0.0f, outR = 0.0f;
        for (uint32_t c = 0; c < ch && c < RV_MAX_CHANNELS; c++)
        {
            float acc = 0.0f;
            for (int i = 0; i < RV_COMBS; i++)
            {
                RvComb* cb = &r->comb[c][i];
                float y = cb->buf[cb->index];
                cb->filterStore = y * (1.0f - r->damp) + cb->filterStore * r->damp;
                cb->buf[cb->index] = input + cb->filterStore * r->feedback;
                if (++cb->index >= cb->size) cb->index = 0;
                acc += y;
            }
            for (int i = 0; i < RV_ALLPASSES; i++)
            {
                RvAllpass* ap = &r->allpass[c][i];
                float bufout = ap->buf[ap->index];
                float y = -acc + bufout;
                ap->buf[ap->index] = acc + bufout * 0.5f;
                if (++ap->index >= ap->size) ap->index = 0;
                acc = y;
            }
            if (c == 0) outL = acc; else outR = acc;
        }
        if (ch == 1) outR = outL;
        float wets[2] = { outL * r->wet1 + outR * r->wet2, outR * r->wet1 + outL * r->wet2 };
        for (uint32_t c = 0; c < ch; c++)
        {
            float w = wets[c < 2 ? c : 1];
            if (r->highCutCoef < 1.0f && c < RV_MAX_CHANNELS)
            {
                r->highCutState[c] += r->highCutCoef * (w - r->highCutState[c]);
                w = r->highCutState[c];
            }
            out[0][f * ch + c] = in[0][f * ch + c] * r->dry + w;
        }
    }
    *inFrames = frames;
    *outFrames = frames;
}
static ma_node_vtable g_reverbVtable = { rv_process, NULL, 1, 1, 0 };

static void rv_free_buffers(ReverbNode* r)
{
    for (uint32_t c = 0; c < RV_MAX_CHANNELS; c++)
    {
        for (int i = 0; i < RV_COMBS; i++) free(r->comb[c][i].buf);
        for (int i = 0; i < RV_ALLPASSES; i++) free(r->allpass[c][i].buf);
    }
}

static ReverbNode* rv_create(void)
{
    ReverbNode* r = (ReverbNode*)calloc(1, sizeof(ReverbNode));
    r->channels = g_channels;
    float scale = g_rate / 44100.0f;
    for (uint32_t c = 0; c < RV_MAX_CHANNELS; c++)
    {
        for (int i = 0; i < RV_COMBS; i++)
        {
            r->comb[c][i].size = (int)((rv_combTuning[i] + c * RV_STEREO_SPREAD) * scale);
            r->comb[c][i].buf = (float*)calloc(r->comb[c][i].size, sizeof(float));
        }
        for (int i = 0; i < RV_ALLPASSES; i++)
        {
            r->allpass[c][i].size = (int)((rv_allpassTuning[i] + c * RV_STEREO_SPREAD) * scale);
            r->allpass[c][i].buf = (float*)calloc(r->allpass[c][i].size, sizeof(float));
        }
    }
    ma_node_config cfg = ma_node_config_init();
    ma_uint32 channels = g_channels;
    cfg.vtable = &g_reverbVtable;
    cfg.pInputChannels = &channels;
    cfg.pOutputChannels = &channels;
    if (ma_node_init(ma_engine_get_node_graph(&g_engine), &cfg, &g_allocCallbacks, r) != MA_SUCCESS)
    {
        rv_free_buffers(r);
        free(r);
        return NULL;
    }
    return r;
}

static void rv_destroy(ReverbNode* r)
{
    ma_node_uninit(r, &g_allocCallbacks);
    rv_free_buffers(r);
    free(r);
}

// ---- voices ----
static void voice_rebuild_chain(Voice* v)
{
    ma_node* target = v->group ? (ma_node*)v->group : ma_engine_get_endpoint(&g_engine);
    ma_node* nodes[5];
    int n = 0;
    if (v->lpfOn) nodes[n++] = &v->lpf;
    if (v->hpfOn) nodes[n++] = &v->hpf;
    if (v->echoOn) nodes[n++] = &v->delay;
    if (v->reverbOn && v->reverb) nodes[n++] = v->reverb;
    if (v->tap) nodes[n++] = v->tap;
    ma_node* next = target;
    for (int i = n - 1; i >= 0; i--)
    {
        ma_node_attach_output_bus(nodes[i], 0, next, 0);
        next = nodes[i];
    }
    ma_node_attach_output_bus(&v->snd, 0, next, 0);
    if (!v->lpfOn && v->lpfInit) ma_node_detach_output_bus(&v->lpf, 0);
    if (!v->hpfOn && v->hpfInit) ma_node_detach_output_bus(&v->hpf, 0);
    if (!v->echoOn && v->delayInit) ma_node_detach_output_bus(&v->delay, 0);
    if (!v->reverbOn && v->reverb) ma_node_detach_output_bus(v->reverb, 0);
}

static void voice_release_locked(Voice* v)
{
    if (!v->used) return;
    if (v->sndInit) { ma_sound_uninit(&v->snd); v->sndInit = 0; }
    if (v->lpfInit) { ma_lpf_node_uninit(&v->lpf, &g_allocCallbacks); v->lpfInit = 0; }
    if (v->hpfInit) { ma_hpf_node_uninit(&v->hpf, &g_allocCallbacks); v->hpfInit = 0; }
    if (v->delayInit) { ma_delay_node_uninit(&v->delay, &g_allocCallbacks); v->delayInit = 0; }
    if (v->tap) { ma_node_uninit(v->tap, &g_allocCallbacks); free(v->tap->ring); free(v->tap); v->tap = NULL; }
    if (v->reverb) { rv_destroy(v->reverb); v->reverb = NULL; }
    if (v->decoderInit) { ma_decoder_uninit(&v->decoder); v->decoderInit = 0; }
    if (v->userInit) { ma_data_source_uninit(&v->userSource); v->userInit = 0; }
    sound_unref(v->sound);
    v->sound = NULL;
    v->used = 0;
    v->gen++;
}

static void on_sound_end(void* user, ma_sound* sound)
{
    (void)sound;
    Voice* v = (Voice*)user;
    v->ended = 1;
}

EXPORT uint64_t wa_voice_play(uint64_t sound, uint64_t group, int startPaused, int autoRelease)
{
    Sound* s = (Sound*)(uintptr_t)sound;
    if (!g_initialized || !s) { g_lastError = MA_INVALID_ARGS; return 0; }
    pthread_mutex_lock(&g_lock);
    Voice* v = NULL;
    uint32_t index = 0;
    for (uint32_t i = 0; i < MAX_VOICES; i++) if (!g_voices[i].used) { v = &g_voices[i]; index = i; break; }
    if (!v) { pthread_mutex_unlock(&g_lock); g_lastError = MA_OUT_OF_MEMORY; LOGW("audio: out of voices"); return 0; }
    uint32_t gen = v->gen;
    memset(v, 0, sizeof *v);
    v->gen = gen;
    v->used = 1;
    v->sound = s;
    s->refs++;
    v->volume = 1.0f;
    v->autoRelease = autoRelease;
    Group* g = group_of(group);
    v->group = g ? &g->group : NULL;
    ma_data_source* ds = NULL;
    ma_result r = MA_SUCCESS;
    switch (s->kind)
    {
    case KIND_PCM:
        r = ma_audio_buffer_ref_init(ma_format_s16, s->channels, s->pcm, s->frames, &v->bufref);
        v->bufref.sampleRate = s->rate;
        ds = &v->bufref;
        break;
    case KIND_MEMORY_STREAM:
    case KIND_FILE_STREAM:
    {
        ma_decoder_config cfg = ma_decoder_config_init(ma_format_f32, 0, 0);
        r = s->kind == KIND_MEMORY_STREAM ? ma_decoder_init_memory(s->data, s->length, &cfg, &v->decoder) : ma_decoder_init_file(s->path, &cfg, &v->decoder);
        if (r == MA_SUCCESS) { v->decoderInit = 1; ds = &v->decoder; }
        break;
    }
    case KIND_USER:
    {
        ma_data_source_config cfg = ma_data_source_config_init();
        cfg.vtable = &g_userVtable;
        r = ma_data_source_init(&cfg, &v->userSource.base);
        v->userSource.sound = s;
        v->userSource.cursor = 0;
        if (r == MA_SUCCESS) { v->userInit = 1; ds = &v->userSource; }
        break;
    }
    }
    if (r == MA_SUCCESS)
    {
        r = ma_sound_init_from_data_source(&g_engine, ds, 0, v->group, &v->snd);
    }
    if (r != MA_SUCCESS)
    {
        g_lastError = r;
        voice_release_locked(v);
        pthread_mutex_unlock(&g_lock);
        LOGW("audio: voice not created: %s", ma_result_description(r));
        return 0;
    }
    v->sndInit = 1;
    if (++g_voicesCreated <= 8) LOGI("audio: voice %llu: sound kind %d, %llu frames, %u ch, %u Hz, group %s, paused %d, auto %d", (unsigned long long)g_voicesCreated, s->kind, (unsigned long long)s->frames, s->channels, s->rate, v->group ? "yes" : "none", startPaused, autoRelease);
    ma_sound_set_spatialization_enabled(&v->snd, MA_FALSE);
    ma_sound_set_end_callback(&v->snd, on_sound_end, v);
    if (!startPaused) ma_sound_start(&v->snd);
    uint64_t handle = make_handle(index, gen);
    pthread_mutex_unlock(&g_lock);
    return handle;
}

EXPORT void wa_voice_stop(uint64_t voice)
{
    pthread_mutex_lock(&g_lock);
    Voice* v = voice_of(voice);
    if (v)
    {
        ma_sound_stop(&v->snd);
        v->stopped = 1; // released at the next update: the engine has no release call for voices
    }
    pthread_mutex_unlock(&g_lock);
}

EXPORT int wa_voice_is_playing(uint64_t voice)
{
    pthread_mutex_lock(&g_lock);
    Voice* v = voice_of(voice);
    int playing = v && !v->stopped && ma_sound_is_playing(&v->snd);
    pthread_mutex_unlock(&g_lock);
    return playing;
}

EXPORT uint64_t wa_voice_get_position_frames(uint64_t voice)
{
    pthread_mutex_lock(&g_lock);
    Voice* v = voice_of(voice);
    ma_uint64 cursor = 0;
    if (v) ma_sound_get_cursor_in_pcm_frames(&v->snd, &cursor);
    pthread_mutex_unlock(&g_lock);
    return cursor;
}

EXPORT int wa_voice_set_position_frames(uint64_t voice, uint64_t frame)
{
    pthread_mutex_lock(&g_lock);
    Voice* v = voice_of(voice);
    ma_result r = v ? ma_sound_seek_to_pcm_frame(&v->snd, frame) : MA_INVALID_ARGS;
    pthread_mutex_unlock(&g_lock);
    return (int)r;
}

#define WITH_VOICE(body) do { pthread_mutex_lock(&g_lock); Voice* v = voice_of(voice); if (v) { body; } pthread_mutex_unlock(&g_lock); } while (0)

EXPORT void wa_voice_set_volume(uint64_t voice, float volume) { WITH_VOICE({ v->volume = volume; if (!v->muted) ma_sound_set_volume(&v->snd, volume); }); }
EXPORT void wa_voice_set_mute(uint64_t voice, int mute) { WITH_VOICE({ v->muted = mute; ma_sound_set_volume(&v->snd, mute ? 0.0f : v->volume); }); }
EXPORT void wa_voice_set_pitch(uint64_t voice, float pitch) { WITH_VOICE({ ma_sound_set_pitch(&v->snd, pitch > 0.001f ? pitch : 0.001f); }); }
EXPORT void wa_voice_set_pan(uint64_t voice, float pan) { WITH_VOICE({ ma_sound_set_pan(&v->snd, pan); }); }
EXPORT void wa_voice_set_loop(uint64_t voice, int loop) { WITH_VOICE({ ma_sound_set_looping(&v->snd, loop ? MA_TRUE : MA_FALSE); }); }
EXPORT void wa_voice_set_priority(uint64_t voice, int priority) { (void)voice; (void)priority; }
EXPORT void wa_voice_set_doppler(uint64_t voice, float factor) { WITH_VOICE({ ma_sound_set_doppler_factor(&v->snd, factor); }); }
EXPORT void wa_voice_set_paused(uint64_t voice, int paused) { WITH_VOICE({ if (paused) ma_sound_stop(&v->snd); else ma_sound_start(&v->snd); }); }
EXPORT void wa_voice_set_start_time(uint64_t voice, uint64_t absoluteFrames) { WITH_VOICE({ ma_sound_set_start_time_in_pcm_frames(&v->snd, absoluteFrames); }); }
EXPORT void wa_voice_set_group(uint64_t voice, uint64_t group) { WITH_VOICE({ Group* g = group_of(group); v->group = g ? &g->group : NULL; voice_rebuild_chain(v); }); }
EXPORT void wa_voice_set_mix_matrix(uint64_t voice, float* matrix, uint32_t outChannels, uint32_t inChannels) { (void)voice; (void)matrix; (void)outChannels; (void)inChannels; }
EXPORT void wa_voice_set_3d_position(uint64_t voice, float x, float y, float z) { WITH_VOICE({ ma_sound_set_position(&v->snd, x, y, z); }); }
EXPORT void wa_voice_set_3d(uint64_t voice, int enabled, float minDistance, float maxDistance, int rolloff)
{
    WITH_VOICE({
        ma_sound_set_spatialization_enabled(&v->snd, enabled ? MA_TRUE : MA_FALSE);
        if (enabled)
        {
            ma_sound_set_positioning(&v->snd, ma_positioning_absolute);
            ma_sound_set_min_distance(&v->snd, minDistance);
            ma_sound_set_max_distance(&v->snd, maxDistance);
            ma_sound_set_attenuation_model(&v->snd, rolloff == 1 ? ma_attenuation_model_linear : ma_attenuation_model_inverse);
            ma_sound_set_rolloff(&v->snd, 1.0f);
        }
    });
}

EXPORT int wa_voice_set_lowpass(uint64_t voice, int enabled, float cutoffHz, float q)
{
    (void)q;
    int result = MA_INVALID_ARGS;
    WITH_VOICE({
        result = MA_SUCCESS;
        if (enabled)
        {
            double cutoff = cutoffHz < 10.0f ? 10.0 : cutoffHz > g_rate * 0.49f ? g_rate * 0.49 : cutoffHz;
            if (!v->lpfInit)
            {
                ma_lpf_node_config cfg = ma_lpf_node_config_init(g_channels, g_rate, cutoff, 2);
                result = ma_lpf_node_init(ma_engine_get_node_graph(&g_engine), &cfg, &g_allocCallbacks, &v->lpf);
                v->lpfInit = result == MA_SUCCESS;
            }
            else
            {
                ma_lpf_config cfg = ma_lpf_config_init(ma_format_f32, g_channels, g_rate, cutoff, 2);
                result = ma_lpf_node_reinit(&cfg, &v->lpf);
            }
        }
        if (result == MA_SUCCESS && (v->lpfOn != (enabled != 0)))
        {
            v->lpfOn = enabled != 0;
            voice_rebuild_chain(v);
        }
    });
    return result;
}

EXPORT int wa_voice_set_highpass(uint64_t voice, int enabled, float cutoffHz, float q)
{
    (void)q;
    int result = MA_INVALID_ARGS;
    WITH_VOICE({
        result = MA_SUCCESS;
        if (enabled)
        {
            double cutoff = cutoffHz < 10.0f ? 10.0 : cutoffHz > g_rate * 0.49f ? g_rate * 0.49 : cutoffHz;
            if (!v->hpfInit)
            {
                ma_hpf_node_config cfg = ma_hpf_node_config_init(g_channels, g_rate, cutoff, 2);
                result = ma_hpf_node_init(ma_engine_get_node_graph(&g_engine), &cfg, &g_allocCallbacks, &v->hpf);
                v->hpfInit = result == MA_SUCCESS;
            }
            else
            {
                ma_hpf_config cfg = ma_hpf_config_init(ma_format_f32, g_channels, g_rate, cutoff, 2);
                result = ma_hpf_node_reinit(&cfg, &v->hpf);
            }
        }
        if (result == MA_SUCCESS && (v->hpfOn != (enabled != 0)))
        {
            v->hpfOn = enabled != 0;
            voice_rebuild_chain(v);
        }
    });
    return result;
}

EXPORT int wa_voice_set_echo(uint64_t voice, int enabled, float delayMs, float feedback, float wet, float dry)
{
    int result = MA_INVALID_ARGS;
    WITH_VOICE({
        result = MA_SUCCESS;
        if (enabled)
        {
            uint32_t delayFrames = (uint32_t)(delayMs * 0.001f * g_rate);
            if (delayFrames < 1) delayFrames = 1;
            if (!v->delayInit)
            {
                ma_delay_node_config cfg = ma_delay_node_config_init(g_channels, g_rate, delayFrames, feedback);
                cfg.delay.wet = wet;
                cfg.delay.dry = dry;
                result = ma_delay_node_init(ma_engine_get_node_graph(&g_engine), &cfg, &g_allocCallbacks, &v->delay);
                v->delayInit = result == MA_SUCCESS;
            }
            else
            {
                ma_delay_node_set_decay(&v->delay, feedback);
                ma_delay_node_set_wet(&v->delay, wet);
                ma_delay_node_set_dry(&v->delay, dry);
            }
        }
        if (result == MA_SUCCESS && (v->echoOn != (enabled != 0)))
        {
            v->echoOn = enabled != 0;
            voice_rebuild_chain(v);
        }
    });
    return result;
}

EXPORT int wa_voice_set_reverb(uint64_t voice, int enabled, float roomsize, float damp, float wet, float dry, float width, float highCutHz)
{
    int result = MA_INVALID_ARGS;
    WITH_VOICE({
        result = MA_SUCCESS;
        if (enabled)
        {
            if (!v->reverb) v->reverb = rv_create();
            if (!v->reverb) result = MA_OUT_OF_MEMORY;
            else rv_set(v->reverb, roomsize, damp, wet, dry, width, highCutHz);
        }
        if (result == MA_SUCCESS && (v->reverbOn != (enabled != 0)))
        {
            v->reverbOn = enabled != 0;
            voice_rebuild_chain(v);
        }
    });
    return result;
}

EXPORT int wa_voice_tap_enable(uint64_t voice, uint32_t capacityFrames)
{
    int result = MA_INVALID_ARGS;
    WITH_VOICE({
        if (v->tap) { result = MA_SUCCESS; }
        else
        {
            TapNode* t = (TapNode*)calloc(1, sizeof(TapNode));
            t->capacity = capacityFrames ? capacityFrames : 16384;
            t->channels = g_channels;
            t->ring = (float*)calloc((size_t)t->capacity * t->channels, sizeof(float));
            ma_node_config cfg = ma_node_config_init();
            ma_uint32 channels = g_channels;
            cfg.vtable = &g_tapVtable;
            cfg.pInputChannels = &channels;
            cfg.pOutputChannels = &channels;
            result = ma_node_init(ma_engine_get_node_graph(&g_engine), &cfg, &g_allocCallbacks, t);
            if (result == MA_SUCCESS) { v->tap = t; voice_rebuild_chain(v); }
            else { free(t->ring); free(t); }
        }
    });
    return result;
}

EXPORT int wa_voice_tap_read(uint64_t voice, float* output, uint32_t frames, uint32_t channel)
{
    int result = MA_INVALID_ARGS;
    WITH_VOICE({
        TapNode* t = v->tap;
        if (t && channel < t->channels)
        {
            uint64_t written = t->written;
            uint64_t available = written < t->capacity ? written : t->capacity;
            uint64_t take = frames < available ? frames : available;
            uint64_t start = written - take;
            for (uint32_t i = 0; i < frames; i++)
            {
                if (i < frames - take) { output[i] = 0.0f; continue; }
                uint64_t frame = start + (i - (frames - take));
                output[i] = t->ring[(size_t)(frame % t->capacity) * t->channels + channel];
            }
            result = MA_SUCCESS;
        }
    });
    return result;
}

// ---- groups ----
EXPORT uint64_t wa_group_create(uint64_t parent)
{
    if (!g_initialized) return 0;
    pthread_mutex_lock(&g_lock);
    Group* g = NULL;
    uint32_t index = 0;
    for (uint32_t i = 0; i < MAX_GROUPS; i++) if (!g_groups[i].used) { g = &g_groups[i]; index = i; break; }
    if (!g) { pthread_mutex_unlock(&g_lock); return 0; }
    Group* p = group_of(parent);
    ma_result r = ma_sound_group_init(&g_engine, 0, p ? &p->group : NULL, &g->group);
    if (r != MA_SUCCESS) { g_lastError = r; pthread_mutex_unlock(&g_lock); return 0; }
    g->used = 1;
    g->volume = 1.0f;
    g->muted = 0;
    uint64_t handle = make_handle(index, g->gen);
    pthread_mutex_unlock(&g_lock);
    return handle;
}

EXPORT void wa_group_release(uint64_t group)
{
    pthread_mutex_lock(&g_lock);
    Group* g = group_of(group);
    if (g)
    {
        for (int i = 0; i < MAX_VOICES; i++) if (g_voices[i].used && g_voices[i].group == &g->group) { g_voices[i].group = NULL; voice_rebuild_chain(&g_voices[i]); }
        ma_sound_group_uninit(&g->group);
        g->used = 0;
        g->gen++;
    }
    pthread_mutex_unlock(&g_lock);
}

#define WITH_GROUP(body) do { pthread_mutex_lock(&g_lock); Group* g = group_of(group); if (g) { body; } pthread_mutex_unlock(&g_lock); } while (0)
EXPORT void wa_group_set_volume(uint64_t group, float volume) { WITH_GROUP({ g->volume = volume; if (!g->muted) ma_sound_group_set_volume(&g->group, volume); }); }
EXPORT void wa_group_set_mute(uint64_t group, int mute) { WITH_GROUP({ g->muted = mute; ma_sound_group_set_volume(&g->group, mute ? 0.0f : g->volume); }); }
EXPORT void wa_group_set_paused(uint64_t group, int paused) { WITH_GROUP({ if (paused) ma_sound_group_stop(&g->group); else ma_sound_group_start(&g->group); }); }
EXPORT void wa_group_set_pitch(uint64_t group, float pitch) { WITH_GROUP({ ma_sound_group_set_pitch(&g->group, pitch > 0.001f ? pitch : 0.001f); }); }

// ---- the engine ----
EXPORT void wa_set_allocator(void* alloc, void* realloc_, void* free_)
{
    g_alloc = (void* (*)(size_t))alloc;
    g_realloc = (void* (*)(void*, size_t))realloc_;
    g_free = (void (*)(void*))free_;
}

EXPORT int wa_init(uint32_t sampleRate, uint32_t channels, int nullBackend)
{
    if (g_initialized) return 0;
    g_allocCallbacks.pUserData = NULL;
    g_allocCallbacks.onMalloc = host_malloc;
    g_allocCallbacks.onRealloc = host_realloc;
    g_allocCallbacks.onFree = host_free;
    ma_engine_config cfg = ma_engine_config_init();
    cfg.sampleRate = sampleRate;
    cfg.channels = channels;
    cfg.listenerCount = 1;
    cfg.allocationCallbacks = g_allocCallbacks;
    if (nullBackend)
    {
        ma_backend backends[] = { ma_backend_null };
        ma_context_config ccfg = ma_context_config_init();
        ccfg.allocationCallbacks = g_allocCallbacks;
        ma_result cr = ma_context_init(backends, 1, &ccfg, &g_nullContext);
        if (cr != MA_SUCCESS) { g_lastError = cr; return (int)cr; }
        cfg.pContext = &g_nullContext;
        g_nullBackend = 1;
    }
    ma_result r = ma_engine_init(&cfg, &g_engine);
    if (r != MA_SUCCESS)
    {
        g_lastError = r;
        LOGW("audio: engine init failed: %s", ma_result_description(r));
        if (g_nullBackend) { ma_context_uninit(&g_nullContext); g_nullBackend = 0; }
        return (int)r;
    }
    g_rate = ma_engine_get_sample_rate(&g_engine);
    g_channels = ma_engine_get_channels(&g_engine);
    g_initialized = 1;
    ma_device* device = ma_engine_get_device(&g_engine);
    LOGI("audio: miniaudio %s on %s, %u Hz, %u channels, period %u frames", ma_version_string(), device ? ma_get_backend_name(device->pContext->backend) : "no device", g_rate, g_channels, device ? device->playback.internalPeriodSizeInFrames : 0);
    return 0;
}

EXPORT void wa_shutdown(void)
{
    if (!g_initialized) return;
    pthread_mutex_lock(&g_lock);
    for (int i = 0; i < MAX_VOICES; i++) voice_release_locked(&g_voices[i]);
    for (int i = 0; i < MAX_GROUPS; i++) if (g_groups[i].used) { ma_sound_group_uninit(&g_groups[i].group); g_groups[i].used = 0; g_groups[i].gen++; }
    ma_engine_uninit(&g_engine);
    if (g_nullBackend) { ma_context_uninit(&g_nullContext); g_nullBackend = 0; }
    g_initialized = 0;
    pthread_mutex_unlock(&g_lock);
}

EXPORT int wa_last_error(void) { return (int)g_lastError; }
EXPORT uint32_t wa_sample_rate(void) { return g_rate; }
EXPORT uint32_t wa_channels(void) { return g_channels; }
EXPORT uint32_t wa_period_frames(void)
{
    if (!g_initialized) return 0;
    ma_device* device = ma_engine_get_device(&g_engine);
    return device ? device->playback.internalPeriodSizeInFrames : 0;
}
EXPORT uint64_t wa_time_frames(void) { return g_initialized ? ma_engine_get_time_in_pcm_frames(&g_engine) : 0; }
EXPORT void wa_set_master_volume(float volume) { if (g_initialized) ma_engine_set_volume(&g_engine, volume); }
EXPORT void wa_set_master_paused(int paused) { if (g_initialized) { if (paused) ma_engine_stop(&g_engine); else ma_engine_start(&g_engine); } }
EXPORT void wa_set_listener(float px, float py, float pz, float fx, float fy, float fz, float ux, float uy, float uz)
{
    if (!g_initialized) return;
    ma_engine_listener_set_position(&g_engine, 0, px, py, pz);
    ma_engine_listener_set_direction(&g_engine, 0, fx, fy, fz);
    ma_engine_listener_set_world_up(&g_engine, 0, ux, uy, uz);
}

EXPORT int wa_playing_count(void)
{
    int n = 0;
    pthread_mutex_lock(&g_lock);
    for (int i = 0; i < MAX_VOICES; i++) if (g_voices[i].used && !g_voices[i].stopped && ma_sound_is_playing(&g_voices[i].snd)) n++;
    pthread_mutex_unlock(&g_lock);
    return n;
}

// Once per frame: voices that have stopped or ended on their own are released.
EXPORT void wa_update(void)
{
    if (!g_initialized) return;
    pthread_mutex_lock(&g_lock);
    int playing = 0, used = 0;
    for (int i = 0; i < MAX_VOICES; i++)
    {
        Voice* v = &g_voices[i];
        if (v->used && (v->stopped || (v->autoRelease && v->ended))) voice_release_locked(v);
        if (v->used) { used++; if (ma_sound_is_playing(&v->snd)) playing++; }
    }
    if (++g_updates % 600 == 0) LOGI("audio: %d voices playing of %d live, %llu created, engine time %llu frames", playing, used, (unsigned long long)g_voicesCreated, (unsigned long long)ma_engine_get_time_in_pcm_frames(&g_engine));
    pthread_mutex_unlock(&g_lock);
}
