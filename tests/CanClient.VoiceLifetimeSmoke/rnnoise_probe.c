#include <dlfcn.h>
#include <stdatomic.h>
#include <stddef.h>

/* Isolated test executable only. Every DSP/create/destroy call reaches the real
 * RNNoise library. The gate pauses the native call's return, after DSP completes,
 * so even an unfixed Dispose can be detected without dereferencing freed state. */
static void *(*real_create)(void *);
static float (*real_process)(void *, float *, const float *);
static void (*real_destroy)(void *);
static void (*process_gate)(void);
static atomic_int gate_after;
static atomic_int created;
static atomic_int destroyed;
static atomic_int processed;
static atomic_int processing;
static atomic_int destroyed_in_flight;

int probe_open(const char *path, void (*gate)(void))
{
    void *library = dlopen(path, RTLD_NOW | RTLD_LOCAL);
    if (library == NULL)
        return 0;
    real_create = (void *(*)(void *))dlsym(library, "rnnoise_create");
    real_process = (float (*)(void *, float *, const float *))dlsym(library, "rnnoise_process_frame");
    real_destroy = (void (*)(void *))dlsym(library, "rnnoise_destroy");
    process_gate = gate;
    return real_create != NULL && real_process != NULL && real_destroy != NULL;
}

void probe_arm(int frames)
{
    atomic_store(&gate_after, frames);
}

int probe_counter(int counter)
{
    switch (counter)
    {
    case 0: return atomic_load(&created);
    case 1: return atomic_load(&destroyed);
    case 2: return atomic_load(&processed);
    case 3: return atomic_load(&processing);
    case 4: return atomic_load(&destroyed_in_flight);
    default: return -1;
    }
}

void *rnnoise_create(void *model)
{
    void *state = real_create(model);
    if (state != NULL)
        atomic_fetch_add(&created, 1);
    return state;
}

float rnnoise_process_frame(void *state, float *output, const float *input)
{
    atomic_fetch_add(&processing, 1);
    float result = real_process(state, output, input);
    atomic_fetch_add(&processed, 1);
    if (atomic_fetch_sub(&gate_after, 1) == 1)
        process_gate();
    atomic_fetch_sub(&processing, 1);
    return result;
}

void rnnoise_destroy(void *state)
{
    if (atomic_load(&processing) != 0)
        atomic_fetch_add(&destroyed_in_flight, 1);
    real_destroy(state);
    atomic_fetch_add(&destroyed, 1);
}
