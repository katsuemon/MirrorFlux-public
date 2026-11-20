#pragma once
#include <stdint.h>
#include "unarr.h"
typedef struct rar15 rar15;
rar15 *rar15_create(void);
void rar15_destroy(rar15 *self);
void rar15_begin(rar15 *self, ar_stream *input, uint64_t packed, int64_t size, uint32_t crc, int solid);
int rar15_read(rar15 *self, void *output, int count);
