#include <ctype.h>
#include <dirent.h>
#include <stdio.h>
#include <stdlib.h>
#include <string.h>
#include <strings.h>
#include <sys/stat.h>
#include <time.h>
#include <unistd.h>

#include "payload_mgr.h"
#include "slot.h"
#include "json_helpers.h"
#include "sha256.h"
#include "repository.h"
#include "config.h"
#include "pldmgr.h"
#include "autoload.h"
#include "utils.h"
#include "collection_helpers.h"
#include <errno.h>

#ifndef PLDMGR_USB_ROOT_FORMAT
#define PLDMGR_USB_ROOT_FORMAT "/mnt/usb%d"
#endif

/* ── Extension / path checks ──────────────────────────────── */

static int is_supported_extension(const char *path) {
    const char *ext = strrchr(path, '.');
    if (!ext) return 0;
    return (strcasecmp(ext, ".elf") == 0 || strcasecmp(ext, ".bin") == 0);
}

static int is_allowed_usb_path(const char *path) {
    if (!path || path[0] == '\0') return 0;
    if (strstr(path, "..")) return 0;
    if (!is_supported_extension(path)) return 0;
    for (int i = 0; i < 8; i++) {
        char root[512];
        snprintf(root, sizeof(root), PLDMGR_USB_ROOT_FORMAT, i);
        size_t len = strlen(root);
        if (!strncmp(path, root, len) && path[len] == '/') return 1;
    }
    return 0;
}

/* ── USB Scanning (Unmanaged, run-in-place) ─────────────────── */

static int append_usb_path(char ***paths, size_t *count, size_t *capacity, const char *path) {
    char **grown = collection_reserve(*paths, capacity, *count + 1, sizeof(char *));
    if (!grown) return -1;
    *paths = grown;
    char *copy = strdup(path);
    if (!copy) return -1;
    (*paths)[(*count)++] = copy;
    return 0;
}

static int scan_usb_dir(const char *dir_path, char ***out_paths, size_t *count, size_t *cap) {
    DIR *d = opendir(dir_path);
    if (!d) return errno == ENOENT ? 0 : -1;

    struct dirent *ent;
    while ((ent = readdir(d)) != NULL) {
        if (ent->d_name[0] == '.') continue;

        char full[512];
        snprintf(full, sizeof(full), "%s/%s", dir_path, ent->d_name);

        struct stat st;
        if (stat(full, &st) != 0) continue;

        if (S_ISDIR(st.st_mode)) {
            /* Scan 1 level deep */
            DIR *sub = opendir(full);
            if (sub) {
                struct dirent *sub_ent;
                while ((sub_ent = readdir(sub)) != NULL) {
                    if (sub_ent->d_name[0] == '.') continue;
                    if (is_supported_extension(sub_ent->d_name)) {
                        char sub_full[640];
                        snprintf(sub_full, sizeof(sub_full), "%s/%s", full, sub_ent->d_name);
                        if (append_usb_path(out_paths, count, cap, sub_full) != 0) {
                            closedir(sub); closedir(d); return -1;
                        }
                    }
                }
                closedir(sub);
            }
        } else if (is_supported_extension(ent->d_name)) {
            if (append_usb_path(out_paths, count, cap, full) != 0) { closedir(d); return -1; }
        }
    }
    closedir(d);
    return 0;
}

/* ── Listing API ───────────────────────────────────────────── */

size_t payload_mgr_list_json(char *json_buf, size_t buf_size) {
    if (!json_buf || !buf_size) return 0;
    SlotMeta *slots = NULL;
    size_t slot_count = 0;
    json_buf[0] = '\0';
    if (slot_list_all(&slots, &slot_count) != 0) return 0;

    /* Enumerate USB payloads */
    char **usb_paths = NULL;
    size_t usb_count = 0, usb_cap = 0;
    int scan_usb = config_read_bool("SCAN_USB_PAYLOADS", 0);

    for (int u = 0; u < 8; u++) {
        char pldmgr_usb[64];
        snprintf(pldmgr_usb, sizeof(pldmgr_usb), PLDMGR_USB_ROOT_FORMAT "/pldmgr", u);
        if (scan_usb_dir(pldmgr_usb, &usb_paths, &usb_count, &usb_cap) != 0) goto scan_failed;

        if (scan_usb) {
            char root_usb[64];
            snprintf(root_usb, sizeof(root_usb), PLDMGR_USB_ROOT_FORMAT, u);
            DIR *d = opendir(root_usb);
            if (d) {
                struct dirent *ent;
                while ((ent = readdir(d)) != NULL) {
                    if (ent->d_name[0] == '.') continue;
                    if (is_supported_extension(ent->d_name)) {
                        char full[512];
                        snprintf(full, sizeof(full), "%s/%s", root_usb, ent->d_name);
                        if (append_usb_path(&usb_paths, &usb_count, &usb_cap, full) != 0) {
                            closedir(d); goto scan_failed;
                        }
                    }
                }
                closedir(d);
            }
        }
    }

    /* Pop any legacy paths migration map */
    char legacy_map[16384] = "";
    int legacy_result = slot_pop_legacy_paths_json(legacy_map, sizeof(legacy_map));
    int has_legacy = legacy_result == 0 && legacy_map[0] != '\0';

    JsonListBuilder jb = { json_buf, buf_size, 0, 1, 0 };
    if (legacy_result < -1) json_builder_fail(&jb);
    char update_path[1024], pending_version[64], pending_e[6 * 64];
    int pending = repository_check_self_update(update_path, sizeof(update_path), pending_version, sizeof(pending_version));
    json_escape_checked(&jb, pending ? pending_version : "", pending_e, sizeof(pending_e));
    json_append(&jb, "{\n  \"self\":{\"id\":\"pldmgr\",\"running_version\":\"%s\",\"pending_version\":\"%s\"},\n", MENU_VERSION, pending_e);
    json_append(&jb, "  \"items\": [\n");

    for (size_t i = 0; i < slot_count; i++) {
        char sk_e[6 * 128], pp_e[6 * 512], kind_e[6 * 16], fn_e[6 * 256];
        json_escape_checked(&jb, slots[i].slot_key, sk_e, sizeof(sk_e));
        json_escape_checked(&jb, slots[i].payload_path, pp_e, sizeof(pp_e));
        json_escape_checked(&jb, slots[i].kind, kind_e, sizeof(kind_e));
        json_escape_checked(&jb, slots[i].filename, fn_e, sizeof(fn_e));

        json_append(&jb, "    {\n"
                         "      \"slot\": \"%s\",\n"
                         "      \"path\": \"%s\",\n"
                         "      \"kind\": \"%s\",\n"
                         "      \"filename\": \"%s\"",
                    sk_e, pp_e, kind_e, fn_e);

        if (strcmp(slots[i].kind, SLOT_KIND_REPO) == 0) {
            char id_e[6 * 128], surl_e[6 * 1024], sname_e[6 * 256], name_e[6 * 256];
            char ver_e[6 * 64], csum_e[6 * 65], lup_e[6 * 64], auth_e[6 * 128], det_e[6 * 1024];

            json_escape_checked(&jb, slots[i].id, id_e, sizeof(id_e));
            json_escape_checked(&jb, slots[i].source_url, surl_e, sizeof(surl_e));
            json_escape_checked(&jb, slots[i].source_name, sname_e, sizeof(sname_e));
            json_escape_checked(&jb, slots[i].name, name_e, sizeof(name_e));
            json_escape_checked(&jb, slots[i].version, ver_e, sizeof(ver_e));
            json_escape_checked(&jb, slots[i].checksum, csum_e, sizeof(csum_e));
            json_escape_checked(&jb, slots[i].last_update, lup_e, sizeof(lup_e));
            json_escape_checked(&jb, slots[i].author, auth_e, sizeof(auth_e));
            json_escape_checked(&jb, slots[i].details_url, det_e, sizeof(det_e));

            char src_e[6 * 1024], desc_e[6 * 1024];
            json_escape_checked(&jb, slots[i].source, src_e, sizeof(src_e));
            json_escape_checked(&jb, slots[i].description, desc_e, sizeof(desc_e));

            json_append(&jb, ",\n"
                             "      \"id\": \"%s\",\n"
                             "      \"source_url\": \"%s\",\n"
                             "      \"source_name\": \"%s\",\n"
                             "      \"name\": \"%s\",\n"
                             "      \"version\": \"%s\",\n"
                             "      \"checksum\": \"%s\",\n"
                             "      \"last_update\": \"%s\",\n"
                             "      \"author\": \"%s\",\n"
                             "      \"details_url\": \"%s\",\n"
                             "      \"source\": \"%s\",\n"
                             "      \"description\": \"%s\"",
                        id_e, surl_e, sname_e, name_e, ver_e,
                        csum_e, lup_e, auth_e, det_e, src_e, desc_e);
        } else {
            char org_e[6 * 64], od_e[6 * 1024];
            json_escape_checked(&jb, slots[i].origin, org_e, sizeof(org_e));
            json_escape_checked(&jb, slots[i].origin_detail, od_e, sizeof(od_e));

            json_append(&jb, ",\n"
                             "      \"origin\": \"%s\",\n"
                             "      \"origin_detail\": \"%s\"",
                        org_e, od_e);
        }

        json_append(&jb, "\n    }%s\n", (i + 1 < slot_count) ? "," : "");
    }

    json_append(&jb, "  ],\n  \"usb\": [\n");
    for (size_t u = 0; u < usb_count; u++) {
        char u_e[6 * 512];
        json_escape_checked(&jb, usb_paths[u], u_e, sizeof(u_e));
        json_append(&jb, "    \"%s\"%s\n", u_e, (u + 1 < usb_count) ? "," : "");
    }

    /* Provide payloads array containing all paths for backward compatibility */
    json_append(&jb, "  ],\n  \"payloads\": [\n");
    size_t total_p = slot_count + (size_t)usb_count;
    size_t curr_p = 0;
    for (size_t i = 0; i < slot_count; i++) {
        char pp_e[6 * 512];
        json_escape_checked(&jb, slots[i].payload_path, pp_e, sizeof(pp_e));
        curr_p++;
        json_append(&jb, "    \"%s\"%s\n", pp_e, (curr_p < total_p) ? "," : "");
    }
    for (size_t u = 0; u < usb_count; u++) {
        char u_e[6 * 512];
        json_escape_checked(&jb, usb_paths[u], u_e, sizeof(u_e));
        curr_p++;
        json_append(&jb, "    \"%s\"%s\n", u_e, (curr_p < total_p) ? "," : "");
    }

    if (has_legacy) {
        json_append(&jb, "  ],\n  \"legacy_paths\": %s\n}", legacy_map);
    } else {
        json_append(&jb, "  ]\n}\n");
    }

    slot_free_all(slots);
    for (size_t u = 0; u < usb_count; u++) free(usb_paths[u]);
    if (usb_paths) free(usb_paths);

    return jb.pos;
scan_failed:
    slot_free_all(slots);
    for (size_t u = 0; u < usb_count; u++) free(usb_paths[u]);
    free(usb_paths);
    json_buf[0] = '\0';
    return 0;
}

int payload_mgr_delete_payload(const char *slot_or_filename) {
    if (!slot_or_filename || slot_or_filename[0] == '\0') return -1;
    if (strstr(slot_or_filename, "..")) return -1;

    /* If it is a direct slot key */
    if (strncmp(slot_or_filename, "repo/", 5) == 0 || strncmp(slot_or_filename, "local/", 6) == 0) {
        return slot_delete(slot_or_filename);
    }

    /* If it starts with PAYLOADS_STORAGE_DIR */
    size_t base_len = strlen(PAYLOADS_STORAGE_DIR);
    if (strncmp(slot_or_filename, PAYLOADS_STORAGE_DIR, base_len) == 0 && slot_or_filename[base_len] == '/') {
        const char *rel = slot_or_filename + base_len + 1;
        if (strncmp(rel, "repo/", 5) == 0 || strncmp(rel, "local/", 6) == 0) {
            char extracted_slot[256];
            strncpy(extracted_slot, rel, sizeof(extracted_slot) - 1);
            extracted_slot[sizeof(extracted_slot) - 1] = '\0';
            char *s1 = strchr(extracted_slot, '/');
            if (s1) {
                char *s2 = strchr(s1 + 1, '/');
                if (s2) *s2 = '\0';
            }
            return slot_delete(extracted_slot);
        }
    }

    /* Fallback: match by filename or payload_path */
    const char *target_fn = strrchr(slot_or_filename, '/');
    target_fn = target_fn ? target_fn + 1 : slot_or_filename;

    SlotMeta *slots = NULL;
    size_t count = 0;
    if (slot_list_all(&slots, &count) == 0 && slots) {
        for (size_t i = 0; i < count; i++) {
            if (strcmp(slots[i].filename, target_fn) == 0 ||
                strcmp(slots[i].payload_path, slot_or_filename) == 0) {
                int res = slot_delete(slots[i].slot_key);
                slot_free_all(slots);
                return res;
            }
        }
        slot_free_all(slots);
    }

    return -1;
}

int payload_mgr_check_existing(const char *filename, char *out_json, size_t out_size) {
    JsonListBuilder jb = { out_json, out_size, 0, 1, 0 };
    if (is_pldmgr_name(filename)) {
        json_append(&jb, "{\"status\":\"error\",\"message\":\"Payload Manager updates require the official repository\"}");
        return -1;
    }
    if (!filename || filename[0] == '\0') {
        json_append(&jb, "{\"status\":\"error\",\"message\":\"Missing filename\"}");
        return -1;
    }

    char lkey[128];
    slot_local_key(filename, lkey, sizeof(lkey));

    char check_dir[640];
    snprintf(check_dir, sizeof(check_dir), "%s/local/%s", PAYLOADS_STORAGE_DIR, lkey);

    SlotMeta meta;
    if (slot_read_meta(check_dir, &meta) != 0) {
        json_append(&jb, "{\"error\":\"Failed to read slot metadata\"}");
        return -1;
    }
    int exists = meta.payload_path[0] != '\0';

    char key_e[6 * 128];
    json_escape_checked(&jb, lkey, key_e, sizeof(key_e));

    if (exists) {
        char slot_e[6 * 128], fn_e[6 * 256];
        json_escape_checked(&jb, meta.slot_key, slot_e, sizeof(slot_e));
        json_escape_checked(&jb, meta.filename, fn_e, sizeof(fn_e));
        json_append(&jb,
                 "{\"status\":\"ok\",\"key\":\"%s\",\"folder_exists\":true,\"file_exists\":true,"
                 "\"folder_name\":\"%s\",\"existing\":{\"slot\":\"%s\",\"filename\":\"%s\"}}",
                 key_e, key_e, slot_e, fn_e);
    } else {
        json_append(&jb,
                 "{\"status\":\"ok\",\"key\":\"%s\",\"folder_exists\":false,\"file_exists\":false,"
                 "\"folder_name\":\"%s\",\"existing\":null}",
                 key_e, key_e);
    }
    return jb.error ? -1 : 0;
}

int payload_mgr_import_to_storage_mode(const char *filename, const char *temp_path,
                                       const char *mode, const char *install_source,
                                       const char *install_source_detail,
                                       char *msg_buf, size_t msg_buf_size,
                                       char *out_slot, size_t out_slot_size) {
    if (!filename || strstr(filename, "/") || strstr(filename, "..") || is_pldmgr_name(filename)) {
        snprintf(msg_buf, msg_buf_size, "Invalid filename");
        return -1;
    }

    char lkey[128];
    slot_local_key(filename, lkey, sizeof(lkey));

    char target_slot[128], base_slot[128];
    snprintf(base_slot, sizeof(base_slot), "local/%s", lkey);
    SlotMeta meta;
    memset(&meta, 0, sizeof(meta));
    meta.schema = PAYLOAD_SLOT_SCHEMA;
    strcpy(meta.kind, SLOT_KIND_LOCAL);
    strncpy(meta.filename, filename, sizeof(meta.filename) - 1);

    time_t now = time(NULL);
    struct tm tmv;
    localtime_r(&now, &tmv);
    strftime(meta.installed_at, sizeof(meta.installed_at), "%Y-%m-%dT%H:%M:%S%z", &tmv);

    strncpy(meta.origin, install_source ? install_source : "web_upload", sizeof(meta.origin) - 1);
    if (install_source_detail) {
        strncpy(meta.origin_detail, install_source_detail, sizeof(meta.origin_detail) - 1);
    }

    if (slot_commit_file(base_slot, temp_path, mode, &meta, 0, target_slot, sizeof(target_slot))) {
        snprintf(msg_buf, msg_buf_size, "Storage commit failed (conflict, invalid mode, or I/O error)");
        return -1;
    }
    /* On failure the request completion callback still owns the upload. */
    if (remove(temp_path))
        pldmgr_log("[PLDMGR] Upload committed; retaining request file for startup cleanup: %s\n", temp_path);

    if (out_slot && out_slot_size > 0) {
        strncpy(out_slot, target_slot, out_slot_size - 1);
        out_slot[out_slot_size - 1] = '\0';
    }

    pldmgr_log("[PLDMGR] Local import complete: slot=%s filename=%s\n", target_slot, filename);
    snprintf(msg_buf, msg_buf_size, "Imported %s", filename);
    return 0;
}

int payload_mgr_usb_check(const char *usb_path, char *out_json, size_t out_size) {
    JsonListBuilder jb = { out_json, out_size, 0, 1, 0 };
    if (!is_allowed_usb_path(usb_path) || is_pldmgr_name(usb_path)) {
        json_append(&jb, "{\"error\":\"Invalid USB path\"}");
        return -1;
    }

    struct stat st;
    if (stat(usb_path, &st) != 0) {
        json_append(&jb, "{\"error\":\"File not found\"}");
        return -1;
    }

    const char *filename = strrchr(usb_path, '/');
    filename = filename ? filename + 1 : usb_path;

    char lkey[128];
    slot_local_key(filename, lkey, sizeof(lkey));

    char check_dir[640];
    snprintf(check_dir, sizeof(check_dir), "%s/local/%s", PAYLOADS_STORAGE_DIR, lkey);

    SlotMeta meta;
    if (slot_read_meta(check_dir, &meta) != 0) {
        json_append(&jb, "{\"error\":\"Failed to read slot metadata\"}");
        return -1;
    }
    int exists = meta.payload_path[0] != '\0';
    char usb_hash[65], installed_hash[65];
    if (!S_ISREG(st.st_mode) || st.st_size <= 0 || compute_sha256_file(usb_path, usb_hash) ||
        (exists && compute_sha256_file(meta.payload_path, installed_hash))) {
        json_append(&jb, "{\"error\":\"Failed to verify payload contents\"}"); return -1;
    }

    char name_e[6 * 256], path_e[6 * 512], key_e[6 * 128];
    json_escape_checked(&jb, filename, name_e, sizeof(name_e));
    json_escape_checked(&jb, usb_path, path_e, sizeof(path_e));
    json_escape_checked(&jb, lkey, key_e, sizeof(key_e));

    if (exists) {
        char slot_e[6 * 128], exfn_e[6 * 256];
        json_escape_checked(&jb, meta.slot_key, slot_e, sizeof(slot_e));
        json_escape_checked(&jb, meta.filename, exfn_e, sizeof(exfn_e));

        int is_same = !strcmp(meta.filename, filename) && !strcmp(usb_hash, installed_hash);
        json_append(&jb,
                 "{\"filename\":\"%s\",\"path\":\"%s\",\"size\":%lld,\"sha256\":\"%s\",\"key\":\"%s\","
                 "\"status\":\"%s\",\"folder_exists\":true,\"folder_name\":\"%s\","
                 "\"existing\":{\"slot\":\"%s\",\"filename\":\"%s\"}}",
                 name_e, path_e, (long long)st.st_size, usb_hash, key_e,
                 is_same ? "exists_same" : "exists_different", key_e,
                 slot_e, exfn_e);
    } else {
        json_append(&jb,
                 "{\"filename\":\"%s\",\"path\":\"%s\",\"size\":%lld,\"sha256\":\"%s\",\"key\":\"%s\","
                 "\"status\":\"ready\",\"folder_exists\":false,\"folder_name\":\"%s\","
                 "\"existing\":null}",
                 name_e, path_e, (long long)st.st_size, usb_hash, key_e, key_e);
    }
    return jb.error ? -1 : 0;
}

int payload_mgr_usb_move(const char *usb_path, const char *mode, int keep_original,
                         char *out_json, size_t out_size) {
    JsonListBuilder jb = { out_json, out_size, 0, 1, 0 };
    if (!is_allowed_usb_path(usb_path) || is_pldmgr_name(usb_path)) {
        json_append(&jb, "{\"ok\":false,\"message\":\"Invalid USB path\"}");
        return -1;
    }

    struct stat st;
    if (stat(usb_path, &st) != 0) {
        json_append(&jb, "{\"ok\":false,\"message\":\"File not found\"}");
        return -1;
    }

    const char *filename = strrchr(usb_path, '/');
    filename = filename ? filename + 1 : usb_path;

    char lkey[128];
    slot_local_key(filename, lkey, sizeof(lkey));

    char target_slot[128], base_slot[128];
    snprintf(base_slot, sizeof(base_slot), "local/%s", lkey);
    SlotMeta meta;
    memset(&meta, 0, sizeof(meta));
    meta.schema = PAYLOAD_SLOT_SCHEMA;
    strcpy(meta.kind, SLOT_KIND_LOCAL);
    strncpy(meta.filename, filename, sizeof(meta.filename) - 1);

    time_t now = time(NULL);
    struct tm tmv;
    localtime_r(&now, &tmv);
    strftime(meta.installed_at, sizeof(meta.installed_at), "%Y-%m-%dT%H:%M:%S%z", &tmv);

    strcpy(meta.origin, "usb");
    strncpy(meta.origin_detail, usb_path, sizeof(meta.origin_detail) - 1);

    if (slot_commit_file(base_slot, usb_path, mode, &meta, 0, target_slot, sizeof(target_slot))) {
        json_append(&jb, "{\"ok\":false,\"message\":\"Storage commit failed (conflict, invalid mode, or I/O error)\"}");
        return -1;
    }

    if (!keep_original) {
        char original_hash[65], copied_hash[65];
        struct stat current;
        int unchanged = !stat(usb_path, &current) && current.st_dev == st.st_dev &&
            current.st_ino == st.st_ino && current.st_size == st.st_size &&
            !compute_sha256_file(usb_path, original_hash) &&
            !compute_sha256_file(meta.payload_path, copied_hash) && !strcmp(original_hash, copied_hash);
        if (!unchanged || remove(usb_path) != 0) {
            pldmgr_log("[PLDMGR] Warning: Failed to remove original file from USB: %s\n", usb_path);
            json_append(&jb,
                     "{\"ok\":true,\"slot\":\"%s\",\"message\":\"Moved successfully\","
                     "\"warning\":\"Payload copied, but failed to delete original from USB.\"}",
                     target_slot);
        } else {
            pldmgr_log("[PLDMGR] USB payload moved: %s -> %s\n", usb_path, meta.payload_path);
            json_append(&jb, "{\"ok\":true,\"slot\":\"%s\",\"message\":\"Moved successfully\"}",
                     target_slot);
        }
    } else {
        pldmgr_log("[PLDMGR] USB payload copied: %s -> %s\n", usb_path, meta.payload_path);
        json_append(&jb, "{\"ok\":true,\"slot\":\"%s\",\"message\":\"Copied successfully\"}",
                 target_slot);
    }

    return jb.error ? -1 : 0;
}
