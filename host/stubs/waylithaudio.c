// Silent stand-in for waylithaudio (the engine's miniaudio wrapper) while the port has no audio yet: every call
// returns 0 or a null handle. The real thing comes later, built on miniaudio for Android.
#include <stdint.h>
#include <stddef.h>

#define EXPORT __attribute__((visibility("default")))

EXPORT int wa_init(const void* options) { (void)options; return 0; }
EXPORT void wa_shutdown(void) {}
EXPORT void wa_update(float dt) { (void)dt; }
EXPORT const char* wa_last_error(void) { return ""; }
EXPORT void wa_set_allocator(void* alloc, void* realloc_, void* free_, void* user) { (void)alloc; (void)realloc_; (void)free_; (void)user; }
EXPORT uint32_t wa_channels(void) { return 2; }
EXPORT uint32_t wa_sample_rate(void) { return 48000; }
EXPORT uint32_t wa_period_frames(void) { return 1024; }
EXPORT uint64_t wa_time_frames(void) { return 0; }
EXPORT int wa_playing_count(void) { return 0; }
EXPORT void wa_set_master_volume(float v) { (void)v; }
EXPORT void wa_set_master_paused(int p) { (void)p; }
EXPORT void wa_set_listener(const float* position, const float* forward, const float* up) { (void)position; (void)forward; (void)up; }
EXPORT uint64_t wa_group_create(uint64_t parent) { (void)parent; return 0; }
EXPORT void wa_group_release(uint64_t g) { (void)g; }
EXPORT void wa_group_set_volume(uint64_t g, float v) { (void)g; (void)v; }
EXPORT void wa_group_set_mute(uint64_t g, int m) { (void)g; (void)m; }
EXPORT void wa_group_set_paused(uint64_t g, int p) { (void)g; (void)p; }
EXPORT void wa_group_set_pitch(uint64_t g, float p) { (void)g; (void)p; }
EXPORT int wa_probe_file(const char* path, uint64_t* frames, uint32_t* channels, uint32_t* rate) { (void)path; if (frames) *frames = 0; if (channels) *channels = 2; if (rate) *rate = 48000; return 0; }
EXPORT int wa_probe_memory(const void* data, size_t length, uint64_t* frames, uint32_t* channels, uint32_t* rate) { (void)data; (void)length; if (frames) *frames = 0; if (channels) *channels = 2; if (rate) *rate = 48000; return 0; }
EXPORT uint64_t wa_sound_load_file_stream(const char* path) { (void)path; return 0; }
EXPORT uint64_t wa_sound_load_memory(const void* data, size_t length) { (void)data; (void)length; return 0; }
EXPORT uint64_t wa_sound_load_memory_stream(const void* data, size_t length) { (void)data; (void)length; return 0; }
EXPORT uint64_t wa_sound_load_pcm(const void* data, uint64_t frames, uint32_t channels, uint32_t rate) { (void)data; (void)frames; (void)channels; (void)rate; return 0; }
EXPORT uint64_t wa_sound_create_user(void* callback, void* user, uint32_t channels, uint32_t rate) { (void)callback; (void)user; (void)channels; (void)rate; return 0; }
EXPORT void wa_sound_release(uint64_t s) { (void)s; }
EXPORT int wa_sound_kind(uint64_t s) { (void)s; return 0; }
EXPORT uint32_t wa_sound_channels(uint64_t s) { (void)s; return 2; }
EXPORT uint32_t wa_sound_rate(uint64_t s) { (void)s; return 48000; }
EXPORT uint64_t wa_sound_length_frames(uint64_t s) { (void)s; return 0; }
EXPORT const void* wa_sound_pcm(uint64_t s) { (void)s; return NULL; }
EXPORT uint64_t wa_voice_play(uint64_t sound, uint64_t group, float volume, float pitch, int loop) { (void)sound; (void)group; (void)volume; (void)pitch; (void)loop; return 0; }
EXPORT void wa_voice_stop(uint64_t v) { (void)v; }
EXPORT int wa_voice_is_playing(uint64_t v) { (void)v; return 0; }
EXPORT uint64_t wa_voice_get_position_frames(uint64_t v) { (void)v; return 0; }
EXPORT void wa_voice_set_volume(uint64_t v, float x) { (void)v; (void)x; }
EXPORT void wa_voice_set_pitch(uint64_t v, float x) { (void)v; (void)x; }
EXPORT void wa_voice_set_paused(uint64_t v, int p) { (void)v; (void)p; }
EXPORT void wa_voice_set_position_frames(uint64_t v, uint64_t f) { (void)v; (void)f; }
EXPORT void wa_voice_set_3d(uint64_t v, int enabled) { (void)v; (void)enabled; }
EXPORT void wa_voice_set_3d_position(uint64_t v, float x, float y, float z) { (void)v; (void)x; (void)y; (void)z; }
EXPORT void wa_voice_set_doppler(uint64_t v, float f) { (void)v; (void)f; }
EXPORT void wa_voice_set_echo(uint64_t v, float a, float b) { (void)v; (void)a; (void)b; }
EXPORT void wa_voice_set_group(uint64_t v, uint64_t g) { (void)v; (void)g; }
EXPORT uint32_t wa_tap_channels(uint64_t t) { (void)t; return 2; }
EXPORT uint64_t wa_tap_create(uint64_t group, void* callback, void* user) { (void)group; (void)callback; (void)user; return 0; }
EXPORT void wa_tap_release(uint64_t t) { (void)t; }
