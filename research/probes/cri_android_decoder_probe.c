typedef unsigned char u8;
typedef unsigned short u16;
typedef unsigned int u32;
typedef unsigned long usize;

extern void *dlopen(const char *path, int mode);
extern void *dlsym(void *handle, const char *name);
extern const char *dlerror(void);
extern void *malloc(usize size);
extern void free(void *pointer);
extern int open(const char *path, int flags, ...);
extern long lseek(int fd, long offset, int origin);
extern long read(int fd, void *buffer, usize size);
extern long write(int fd, const void *buffer, usize size);
extern int close(int fd);
extern int printf(const char *format, ...);
extern void exit(int status);

enum { O_RDONLY = 0, O_WRONLY = 1, O_CREAT = 0100, O_TRUNC = 01000 };
enum { SEEK_SET = 0, SEEK_END = 2, RTLD_NOW = 2 };

typedef void (*InitializeFn)(void);
typedef void (*FinalizeFn)(void);
typedef void *(*CreateFn)(int max_channels);
typedef void (*DestroyFn)(void *decoder);
typedef void (*ResetFn)(void *decoder, int channels, int sample_rate, int bitrate);
typedef void (*DecodeFn)(void *decoder, const u8 *input, int input_offset, int input_size,
                         float *output, int *consumed_bytes, int *output_samples);

static u16 be16(const u8 *p)
{
    return (u16)(((u16)p[0] << 8) | p[1]);
}

static u32 be24(const u8 *p)
{
    return ((u32)p[0] << 16) | ((u32)p[1] << 8) | p[2];
}

static u32 be32(const u8 *p)
{
    return ((u32)p[0] << 24) | ((u32)p[1] << 16) | ((u32)p[2] << 8) | p[3];
}

static u8 *read_file(const char *path, long *size)
{
    int fd = open(path, O_RDONLY);
    if (fd < 0)
        return 0;
    *size = lseek(fd, 0, SEEK_END);
    lseek(fd, 0, SEEK_SET);
    u8 *data = (u8 *)malloc((usize)*size);
    long done = data ? read(fd, data, (usize)*size) : -1;
    close(fd);
    if (done != *size) {
        free(data);
        return 0;
    }
    return data;
}

static void *symbol(void *library, const char *name)
{
    void *result = dlsym(library, name);
    if (!result)
        printf("Missing symbol %s: %s\n", name, dlerror());
    return result;
}

__attribute__((used, noinline)) int run(int argc, char **argv)
{
    if (argc < 3) {
        printf("Usage: cri_android_decoder_probe LIBCRI_SO INPUT.hca [OUTPUT.f32]\n");
        return 2;
    }

    long input_size = 0;
    u8 *input = read_file(argv[2], &input_size);
    if (!input || input_size < 32) {
        printf("Could not read HCA input.\n");
        return 3;
    }

    int header_size = be16(input + 6);
    int channels = input[12];
    int sample_rate = (int)be24(input + 13);
    int frame_count = (int)be32(input + 16);
    int inserted = be16(input + 20);
    int appended = be16(input + 22);
    int frame_size = be16(input + 28);
    int bitrate = frame_size * sample_rate / 128;
    int expected_samples = frame_count * 1024 - inserted - appended;
    int capacity = frame_count * 1024 * channels;
    printf("HCA version=%u header=%d channels=%d rate=%d frames=%d frameSize=%d bitrate=%d samples=%d\n",
           be16(input + 4), header_size, channels, sample_rate, frame_count, frame_size, bitrate, expected_samples);

    void *library = dlopen(argv[1], RTLD_NOW);
    if (!library) {
        printf("dlopen failed: %s\n", dlerror());
        free(input);
        return 4;
    }

    InitializeFn initialize = (InitializeFn)symbol(library, "criHcaDecoderUnity_Initialize");
    FinalizeFn finalize = (FinalizeFn)symbol(library, "criHcaDecoderUnity_Finalize");
    CreateFn create = (CreateFn)symbol(library, "criHcaDecoderUnity_Create");
    DestroyFn destroy = (DestroyFn)symbol(library, "criHcaDecoderUnity_Destroy");
    ResetFn reset = (ResetFn)symbol(library, "criHcaDecoderUnity_Reset");
    DecodeFn decode = (DecodeFn)symbol(library, "criHcaDecoderUnity_DecodeHcaToInterleavedPcm");
    if (!initialize || !finalize || !create || !destroy || !reset || !decode) {
        free(input);
        return 5;
    }

    float *output = (float *)malloc((usize)capacity * sizeof(float));
    if (!output) {
        free(input);
        return 6;
    }

    initialize();
    void *decoder = create(channels);
    if (!decoder) {
        printf("Decoder creation failed.\n");
        finalize();
        free(output);
        free(input);
        return 7;
    }

    reset(decoder, channels, sample_rate, bitrate);
    int consumed = 0;
    int output_samples = 0;
    decode(decoder, input + header_size, 0, (int)input_size - header_size,
           output, &consumed, &output_samples);

    double energy = 0.0;
    float peak = 0.0f;
    int non_finite = 0;
    int non_zero = 0;
    for (int i = 0; i < output_samples; i++) {
        float value = output[i];
        if (!(value == value) || value > 3.4028234e38f || value < -3.4028234e38f) {
            non_finite++;
            continue;
        }
        float absolute = value < 0.0f ? -value : value;
        if (absolute > peak)
            peak = absolute;
        if (absolute > 0.000001f)
            non_zero++;
        energy += (double)value * value;
    }
    printf("RESULT consumed=%d/%ld output=%d/%d peak=%f energy=%f nonZero=%d nonFinite=%d\n",
           consumed, input_size - header_size, output_samples, capacity, peak, energy, non_zero, non_finite);

    if (argc >= 4) {
        int fd = open(argv[3], O_WRONLY | O_CREAT | O_TRUNC, 0644);
        if (fd >= 0) {
            write(fd, output, (usize)output_samples * sizeof(float));
            close(fd);
        }
    }

    destroy(decoder);
    finalize();
    free(output);
    free(input);
    return consumed > 0 && output_samples > 0 && non_finite == 0 ? 0 : 8;
}

__attribute__((naked, noreturn)) void _start(void)
{
    __asm__(
        "ldr x0, [sp]\n"
        "add x1, sp, #8\n"
        "bl run\n"
        "bl exit\n");
}
