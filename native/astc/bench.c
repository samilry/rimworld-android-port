// A measurement for native/astc on the phone (not part of the APK): encodes PNGs with libportastc.so at several
// qualities, single-threaded, and says how long it took and how close the decoded result is (PSNR over the visible
// texels, RGB weighted by alpha; and alpha).
// usage: astcbench <libportastc.so> <qualities, e.g. 0,10,20> <png>...
// THREADS=N: each image is tiled 8x8 into one large image (an atlas page's size) and encoded with
// port_astc_encode_parallel on N threads instead.
// build: the NDK's clang --target=aarch64-linux-android26 -O2 -I downloads/src -o build/native/astcbench native/astc/bench.c -ldl -lm
// Measured 2026-10-21 on the OnePlus (Snapdragon 8s Gen 4), quality 0: 36 Mtexels/s on 1 thread, 60 on 2, 98 on 4, 135 on 8.
#include <dlfcn.h>
#include <math.h>
#include <stdio.h>
#include <stdlib.h>
#include <string.h>
#include <time.h>
#define STB_IMAGE_IMPLEMENTATION
#include "stb_image.h"

typedef int (*encode_fn)(const unsigned char*, int, int, int, float, unsigned char*, size_t);
typedef int (*decode_fn)(const unsigned char*, size_t, int, int, int, unsigned char*);
typedef int (*parallel_fn)(const unsigned char*, int, int, int, float, unsigned char*, size_t, int);

static double now(void)
{
	struct timespec t;
	clock_gettime(CLOCK_MONOTONIC, &t);
	return t.tv_sec + t.tv_nsec * 1e-9;
}

int main(int argc, char** argv)
{
	if (argc < 4)
	{
		fprintf(stderr, "usage: %s <lib> <qualities> <png>...\n", argv[0]);
		return 1;
	}
	void* lib = dlopen(argv[1], RTLD_NOW);
	if (!lib)
	{
		fprintf(stderr, "%s\n", dlerror());
		return 1;
	}
	encode_fn encode = (encode_fn)dlsym(lib, "port_astc_encode");
	decode_fn decode = (decode_fn)dlsym(lib, "port_astc_decode");
	parallel_fn parallel = (parallel_fn)dlsym(lib, "port_astc_encode_parallel");
	int threads = getenv("THREADS") ? atoi(getenv("THREADS")) : 0;
	float qualities[8];
	int qualityCount = 0;
	for (char* q = strtok(argv[2], ","); q && qualityCount < 8; q = strtok(NULL, ","))
	{
		qualities[qualityCount++] = (float)atof(q);
	}
	double totalTexels = 0;
	double totalSeconds[8] = { 0 };
	for (int f = 3; f < argc; f++)
	{
		int w, h, n;
		unsigned char* rgba = stbi_load(argv[f], &w, &h, &n, 4);
		if (!rgba)
		{
			fprintf(stderr, "%s: not read\n", argv[f]);
			continue;
		}
		if (threads > 0)
		{
			int tw = w * 8, th = h * 8;
			unsigned char* tiled = malloc((size_t)tw * th * 4);
			for (int y = 0; y < th; y++)
			{
				for (int tx = 0; tx < 8; tx++)
				{
					memcpy(tiled + ((size_t)y * tw + (size_t)tx * w) * 4, rgba + (size_t)(y % h) * w * 4, (size_t)w * 4);
				}
			}
			stbi_image_free(rgba);
			rgba = tiled;
			w = tw;
			h = th;
		}
		size_t blocksLength = (size_t)((w + 3) / 4) * ((h + 3) / 4) * 16;
		unsigned char* blocks = malloc(blocksLength);
		unsigned char* back = malloc((size_t)w * h * 4);
		totalTexels += (double)w * h;
		printf("%s %dx%d:", strrchr(argv[f], '/') ? strrchr(argv[f], '/') + 1 : argv[f], w, h);
		for (int q = 0; q < qualityCount; q++)
		{
			double start = now();
			int error = threads > 0 ? parallel(rgba, w, h, 1, qualities[q], blocks, blocksLength, threads) : encode(rgba, w, h, 1, qualities[q], blocks, blocksLength);
			double seconds = now() - start;
			totalSeconds[q] += seconds;
			decode(blocks, blocksLength, w, h, 1, back);
			double rgbError = 0, alphaError = 0, weight = 0;
			for (size_t i = 0; i < (size_t)w * h; i++)
			{
				double a = rgba[i * 4 + 3] / 255.0;
				for (int c = 0; c < 3; c++)
				{
					double d = (double)rgba[i * 4 + c] - back[i * 4 + c];
					rgbError += a * d * d;
				}
				double da = (double)rgba[i * 4 + 3] - back[i * 4 + 3];
				alphaError += da * da;
				weight += a;
			}
			double rgbMse = weight > 0 ? rgbError / (weight * 3) : 0;
			double alphaMse = alphaError / ((double)w * h);
			printf("  q%.0f %.1f ms rgb %.1f dB alpha %.1f dB%s", qualities[q], seconds * 1000, rgbMse > 0 ? 10 * log10(255.0 * 255.0 / rgbMse) : 99.0,
				alphaMse > 0 ? 10 * log10(255.0 * 255.0 / alphaMse) : 99.0, error ? " (error)" : "");
		}
		printf("\n");
		free(blocks);
		free(back);
		stbi_image_free(rgba);
	}
	for (int q = 0; q < qualityCount; q++)
	{
		printf("quality %.0f: %.1f Mtexels/s on %d thread(s)\n", qualities[q], totalTexels / totalSeconds[q] / 1e6, threads > 0 ? threads : 1);
	}
	return 0;
}
