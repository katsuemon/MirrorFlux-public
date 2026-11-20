/* MirrorFlux C ABI around the BSD-2-Clause LhASsA encoder. */
#include <stdint.h>
#include "upstream/lh_internal.h"
__declspec(dllexport) int mf_lha_compress(const uint8_t *input, uint32_t length,
    uint8_t *output, uint32_t *capacity)
{
    unsigned long size;
    long status;
    if (!input || !output || !capacity || !length) return LH_ERR_INVALID_ARG;
    size = *capacity;
    status = lh_compress_lh5((void *)input, length, output, &size);
    *capacity = (uint32_t)size;
    return (int)status;
}
