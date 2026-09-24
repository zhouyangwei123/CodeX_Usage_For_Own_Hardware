#ifndef PC_METRICS_PARSER_H
#define PC_METRICS_PARSER_H

#include <stddef.h>
#include <stdint.h>

#define PC_METRICS_FLAG_CPU_VALID       0x01u
#define PC_METRICS_FLAG_MEMORY_VALID    0x02u
#define PC_METRICS_FLAG_CPU_TEMP_VALID  0x04u
#define PC_METRICS_FLAG_GPU_LOAD_VALID  0x08u
#define PC_METRICS_FLAG_GPU_TEMP_VALID  0x10u
#define PC_METRICS_FLAG_MB_TEMP_VALID   0x20u
#define PC_METRICS_FLAG_STALE           0x40u
#define PC_METRICS_FLAG_NETWORK_VALID   0x80u
#define PC_METRICS_TEMP_UNKNOWN         ((int16_t)0x8000)

typedef struct
{
  uint8_t schema;
  uint8_t flags;
  uint8_t cpu_percent;
  uint8_t memory_percent;
  uint8_t gpu_percent;
  int16_t cpu_temperature_x10;
  int16_t gpu_temperature_x10;
  int16_t motherboard_temperature_x10;
  uint32_t memory_used_mb;
  uint32_t memory_total_mb;
  uint32_t download_kib_s;
  uint32_t upload_kib_s;
} pc_metrics_decoded_t;

static inline uint32_t pc_metrics_read_u32_le(const uint8_t *payload)
{
  return (uint32_t)payload[0] | ((uint32_t)payload[1] << 8)
       | ((uint32_t)payload[2] << 16) | ((uint32_t)payload[3] << 24);
}

static inline int16_t pc_metrics_read_i16_le(const uint8_t *payload)
{
  return (int16_t)((uint16_t)payload[0] | ((uint16_t)payload[1] << 8));
}

static inline void pc_metrics_parse_v2_fields(pc_metrics_decoded_t *decoded,
                                               const uint8_t *payload)
{
  decoded->cpu_percent = payload[2];
  decoded->memory_percent = payload[3];
  decoded->gpu_percent = payload[4];
  decoded->cpu_temperature_x10 = pc_metrics_read_i16_le(payload + 6u);
  decoded->gpu_temperature_x10 = pc_metrics_read_i16_le(payload + 8u);
  decoded->motherboard_temperature_x10 =
      pc_metrics_read_i16_le(payload + 10u);
  decoded->memory_used_mb = pc_metrics_read_u32_le(payload + 12u);
  decoded->memory_total_mb = pc_metrics_read_u32_le(payload + 16u);
}

static inline uint8_t pc_metrics_parse(pc_metrics_decoded_t *out,
                                       const uint8_t *payload,
                                       size_t length)
{
  pc_metrics_decoded_t decoded = {0};

  if (out == NULL || payload == NULL) return 0u;

  if (length >= 28u && payload[0] == 3u)
  {
    decoded.schema = 3u;
    decoded.flags = payload[1];
    pc_metrics_parse_v2_fields(&decoded, payload);
    decoded.download_kib_s = pc_metrics_read_u32_le(payload + 20u);
    decoded.upload_kib_s = pc_metrics_read_u32_le(payload + 24u);
  }
  else if (length >= 20u && payload[0] == 2u)
  {
    decoded.schema = 2u;
    decoded.flags = (uint8_t)(payload[1]
        & (uint8_t)~PC_METRICS_FLAG_NETWORK_VALID);
    pc_metrics_parse_v2_fields(&decoded, payload);
  }
  else if (length >= 14u && payload[0] == 1u)
  {
    decoded.schema = 1u;
    if (payload[1] & 0x01u) decoded.flags |= PC_METRICS_FLAG_CPU_VALID;
    if (payload[1] & 0x02u) decoded.flags |= PC_METRICS_FLAG_MEMORY_VALID;
    if (payload[1] & 0x04u) decoded.flags |= PC_METRICS_FLAG_CPU_TEMP_VALID;
    if (payload[1] & 0x08u) decoded.flags |= PC_METRICS_FLAG_STALE;
    decoded.cpu_percent = payload[2];
    decoded.memory_percent = payload[3];
    decoded.gpu_percent = 255u;
    decoded.cpu_temperature_x10 = pc_metrics_read_i16_le(payload + 4u);
    decoded.gpu_temperature_x10 = PC_METRICS_TEMP_UNKNOWN;
    decoded.motherboard_temperature_x10 = PC_METRICS_TEMP_UNKNOWN;
    decoded.memory_used_mb = pc_metrics_read_u32_le(payload + 6u);
    decoded.memory_total_mb = pc_metrics_read_u32_le(payload + 10u);
  }
  else
  {
    return 0u;
  }

  *out = decoded;
  return 1u;
}

#endif /* PC_METRICS_PARSER_H */
