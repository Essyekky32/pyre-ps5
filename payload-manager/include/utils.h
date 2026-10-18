#pragma once

#include <stddef.h>

#ifdef __cplusplus
extern "C" {
#endif

/* Gets the local IP address. Returns 0 on success, -1 on failure. */
int pldmgr_get_local_ip(char *ip_buf, size_t buf_size);
void pldmgr_utils_get_payload_folder_name(const char *filename, char *out_buf, size_t out_size);
/* Escape a NUL-terminated string for use between JSON quotes. Preserve valid
 * UTF-8; replace each invalid UTF-8 byte with \ufffd. Returns 0 on success,
 * -1 on insufficient capacity (or invalid arguments). With nonzero capacity,
 * truncation leaves a terminated prefix of complete escapes/UTF-8 scalars.
 * Source and destination must not overlap. Worst-case capacity is 6*strlen+1. */
int pldmgr_json_escape(const char *src, char *dst, size_t dst_size);
int is_pldmgr_name(const char *name);
int is_pldmgr_official_source(const char *url);
int is_legacy_official_repo_url(const char *url);

#ifdef __cplusplus
}
#endif
