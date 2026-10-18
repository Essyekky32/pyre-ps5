#include <stdio.h>
#include <stdlib.h>
#include <string.h>
#include <ctype.h>
#include <errno.h>
#include <limits.h>
#include <pthread.h>
#include "config.h"
#include "pldmgr.h"
#include "json_helpers.h"

/* Covers read/modify/write across settings, favorites and autoload edits.
 * Never call loader/autoload callbacks with this lock held. */
static pthread_mutex_t config_mutex = PTHREAD_MUTEX_INITIALIZER;
#define LIST_CAPACITY 4096
#define CONFIG_LIMIT (64 * 1024)

static const char *setting_keys[] = {"AUTOLOAD_ENABLED", "AUTO_BROWSER_OPEN",
    "AUTOLOAD_DELAY", "SCAN_USB_PAYLOADS", "AUTO_INSTALL_APP", "MULTI_SOURCES_ENABLED", "KILL_DISC_PLAYER_ON_STARTUP"};

static void defaults(PldmgrConfig *cfg) {
    *cfg = (PldmgrConfig){.auto_browser_open=1, .autoload_delay=5, .auto_install_app=1, .kill_disc_player=1};
}
static int read_config(PldmgrConfig *cfg) {
    defaults(cfg);
    char *text = NULL; size_t size = 0;
    if (read_file_text(PLDMGR_CONFIG_PATH, &text, &size)) return errno == ENOENT ? 0 : -1;
    if (size > CONFIG_LIMIT || memchr(text, 0, size)) { free(text); return -1; }
    char *save = NULL;
    for (char *line = strtok_r(text, "\r\n", &save); line; line = strtok_r(NULL, "\r\n", &save)) {
        char *equal = strchr(line, '=');
        if (!equal) continue;
        *equal++ = 0;
        char *end; errno = 0;
        long value = strtol(equal, &end, 10);
        if (errno || *end || end == equal) continue;
        if (!strcmp(line, "LAST_REPOSITORY_UPDATE")) { if (value >= 0) cfg->last_repository_update = value; continue; }
        int *fields[] = {&cfg->autoload_enabled, &cfg->auto_browser_open, &cfg->autoload_delay,
            &cfg->scan_usb_payloads, &cfg->auto_install_app, &cfg->multi_sources_enabled, &cfg->kill_disc_player};
        for (size_t i = 0; i < 7; i++) if (!strcmp(line, setting_keys[i])) {
            if (i == 2 ? value >= 0 && value <= 3600 : value == 0 || value == 1) *fields[i] = (int)value;
        }
    }
    free(text); return 0;
}
void config_read(PldmgrConfig *cfg) { if (cfg) read_config(cfg); }
static int write_config(const PldmgrConfig *cfg) {
    char text[512];
    int length = snprintf(text, sizeof(text),
        "AUTOLOAD_ENABLED=%d\nLAST_REPOSITORY_UPDATE=%ld\nAUTO_BROWSER_OPEN=%d\n"
        "AUTOLOAD_DELAY=%d\nSCAN_USB_PAYLOADS=%d\nAUTO_INSTALL_APP=%d\nMULTI_SOURCES_ENABLED=%d\nKILL_DISC_PLAYER_ON_STARTUP=%d\n",
        !!cfg->autoload_enabled, cfg->last_repository_update, !!cfg->auto_browser_open,
        cfg->autoload_delay, !!cfg->scan_usb_payloads, !!cfg->auto_install_app, !!cfg->multi_sources_enabled, !!cfg->kill_disc_player);
    if (length < 0 || (size_t)length >= sizeof(text) || ensure_dir_recursive(BASE_DATA_DIR)) return -1;
    char *old = NULL; size_t size = 0;
    if (read_file_text(PLDMGR_CONFIG_PATH, &old, &size) && errno != ENOENT) return -1;
    if (size > CONFIG_LIMIT || (old && memchr(old, 0, size))) { free(old); return -1; }
    char *complete = malloc((size_t)length + size + 2);
    if (!complete) { free(old); return -1; }
    memcpy(complete, text, (size_t)length);
    size_t pos = (size_t)length;
    const char *line = old;
    while (line && line < old + size) {
        const char *end = memchr(line, '\n', (size_t)(old + size - line));
        if (!end) end = old + size;
        size_t n = (size_t)(end - line);
        int known = n >= 23 && !memcmp(line, "LAST_REPOSITORY_UPDATE=", 23);
        for (size_t i = 0; i < 7; i++) {
            size_t k = strlen(setting_keys[i]);
            if (n > k && !memcmp(line, setting_keys[i], k) && line[k] == '=') known = 1;
        }
        if (!known) { memcpy(complete + pos, line, n); pos += n; complete[pos++] = '\n'; }
        line = end < old + size ? end + 1 : old + size;
    }
    int result = pos > CONFIG_LIMIT ? -1 : write_file_text(PLDMGR_CONFIG_PATH, complete, pos);
    free(complete); free(old); return result;
}
int config_write(const PldmgrConfig *cfg) {
    if (!cfg || cfg->autoload_delay < 0 || cfg->autoload_delay > 3600 || cfg->last_repository_update < 0) return -1;
    pthread_mutex_lock(&config_mutex);
    int result = write_config(cfg);
    pthread_mutex_unlock(&config_mutex); return result;
}
int config_read_bool(const char *key, int default_val) {
    if (!key) return default_val;
    char *text = NULL; size_t size = 0;
    if (read_file_text(PLDMGR_CONFIG_PATH, &text, &size)) return default_val;
    int result = default_val;
    if (size <= CONFIG_LIMIT && !memchr(text, 0, size)) {
        char *save = NULL;
        for (char *line = strtok_r(text, "\r\n", &save); line; line = strtok_r(NULL, "\r\n", &save)) {
            size_t length = strlen(key);
            if (strlen(line) > length && !strncmp(line, key, length) && line[length] == '=') {
                if (!strcmp(line + length + 1, "0")) result = 0;
                else if (!strcmp(line + length + 1, "1")) result = 1;
                break;
            }
        }
    }
    free(text); return result;
}
int config_upsert_value(const char *key, const char *value) {
    if (!key || !key[0] || !value || strlen(key) > 255 || strlen(value) > CONFIG_LIMIT ||
        strpbrk(key, "=\r\n") || strpbrk(value, "\r\n")) return -1;
    pthread_mutex_lock(&config_mutex);
    char *old = NULL; size_t size = 0;
    int result = -1;
    if (read_file_text(PLDMGR_CONFIG_PATH, &old, &size) && errno != ENOENT) goto done;
    if (size > CONFIG_LIMIT || (old && memchr(old, 0, size))) goto done;
    size_t capacity = size + strlen(key) + strlen(value) + 4;
    char *text = malloc(capacity);
    if (!text) goto done;
    size_t pos = 0;
    /* Preserve unrelated and unknown keys without fixed-line truncation. */
    const char *line = old;
    while (line && line < old + size) {
        const char *end = memchr(line, '\n', (size_t)(old + size - line));
        if (!end) end = old + size;
        size_t n = (size_t)(end - line), key_size = strlen(key);
        if (!(n > key_size && !memcmp(line, key, key_size) && line[key_size] == '=')) {
            memcpy(text + pos, line, n); pos += n; text[pos++] = '\n';
        }
        line = end < old + size ? end + 1 : old + size;
    }
    int count = snprintf(text + pos, capacity - pos, "%s=%s\n", key, value);
    if (count >= 0 && (size_t)count < capacity - pos && !ensure_dir_recursive(BASE_DATA_DIR))
        result = write_file_text(PLDMGR_CONFIG_PATH, text, pos + (size_t)count);
    free(text);
done:
    free(old); pthread_mutex_unlock(&config_mutex); return result;
}
int config_read_last_update(long *out_ts) {
    if (!out_ts) return -1;
    PldmgrConfig cfg; int result = read_config(&cfg); *out_ts = cfg.last_repository_update; return result;
}
int config_write_last_update(long ts) {
    if (ts < 0) return -1;
    char value[64]; snprintf(value, sizeof(value), "%ld", ts);
    return config_upsert_value("LAST_REPOSITORY_UPDATE", value);
}

/* CSV is a legacy wire format; commas separate entries, never occur inside one.
 * Newlines/control characters cannot be represented safely in line files. */
static int csv_lines(const char *csv, char *out, size_t capacity) {
    size_t pos = 0;
    if (!csv) csv = "";
    const char *start = csv;
    while (*start) {
        const char *end = strchr(start, ','); if (!end) end = start + strlen(start);
        const char *next = *end ? end + 1 : end;
        while (start < end && (*start == ' ' || *start == '\t')) start++;
        while (end > start && (end[-1] == ' ' || end[-1] == '\t')) end--;
        size_t length = (size_t)(end - start);
        for (const char *p = start; p < end; p++) if ((unsigned char)*p < 32 || *p == 127) return -1;
        if (length) {
            if (length + 1 >= capacity - pos) return -1;
            memcpy(out + pos, start, length); pos += length; out[pos++] = '\n';
        }
        start = next;
    }
    out[pos] = 0; return 0;
}
static int save_csv(const char *path, const char *csv) {
    char lines[LIST_CAPACITY];
    if (csv_lines(csv, lines, sizeof(lines)) || ensure_dir_recursive(BASE_DATA_DIR)) return -1;
    return write_file_text(path, lines, strlen(lines));
}
typedef struct { unsigned seen; int values[7]; char autoload[LIST_CAPACITY], favorites[LIST_CAPACITY]; } Update;
static int favorite_item(const char *start, const char *end, void *context) {
    Update *update = context;
    char item[LIST_CAPACITY];
    if (json_decode_string(start, end, item, sizeof(item)) || strchr(item, ',') || strpbrk(item, "\r\n")) return -1;
    size_t used = strlen(update->favorites), count = strlen(item);
    if (used + count + (used && count ? 1 : 0) >= sizeof(update->favorites)) return -1;
    if (count) { if (used) update->favorites[used++] = ','; memcpy(update->favorites + used, item, count + 1); }
    return 0;
}
static int update_member(const char *key, const char *start, const char *end, void *context) {
    Update *update = context;
    size_t index = 0;
    for (; index < 7; index++) if (!strcmp(key, setting_keys[index])) break;
    if (!strcmp(key, "AUTOLOAD_LIST")) index = 7;
    else if (!strcmp(key, "FAVORITES")) index = 8;
    if (index == 7 && strcmp(key, "AUTOLOAD_LIST")) return 0; /* read-only/unknown fields */
    unsigned bit = 1u << index;
    if (update->seen & bit) return -1;
    update->seen |= bit;
    size_t size = (size_t)(end - start);
    if (index == 2) {
        if (!size || size > 4) return -1;
        long value = 0;
        for (const char *p = start; p < end; p++) { if (*p < '0' || *p > '9') return -1; value = value * 10 + *p - '0'; }
        if (value > 3600) return -1;
        update->values[index] = (int)value;
    } else if (index < 7) {
        if (size == 4 && !memcmp(start, "true", 4)) update->values[index] = 1;
        else if (size == 5 && !memcmp(start, "false", 5)) update->values[index] = 0;
        else return -1;
    } else {
        char *csv = index == 7 ? update->autoload : update->favorites;
        if (index == 8 && *start == '[') {
            if (json_visit_array(start, end, favorite_item, update)) return -1;
        } else if (json_decode_string(start, end, csv, LIST_CAPACITY)) return -1;
        char lines[LIST_CAPACITY];
        if (csv_lines(csv, lines, sizeof(lines))) return -1;
    }
    return 0;
}
int config_handle_set_json(const char *json) {
    Update update = {0};
    if (!json || json_visit_object(json, json + strlen(json), update_member, &update)) return CONFIG_INVALID;
    pthread_mutex_lock(&config_mutex);
    PldmgrConfig cfg;
    int result = read_config(&cfg);
    int *fields[] = {&cfg.autoload_enabled, &cfg.auto_browser_open, &cfg.autoload_delay,
        &cfg.scan_usb_payloads, &cfg.auto_install_app, &cfg.multi_sources_enabled, &cfg.kill_disc_player};
    for (size_t i = 0; i < 7; i++) if (update.seen & (1u << i)) *fields[i] = update.values[i];
    /* Validate the entire request before any write. Individual files publish
     * atomically; on I/O failure prior successful file publications remain.
     * Lists go first, enabled/settings last: never enable an unsaved new list. */
    if (!result && (update.seen & (1u << 7))) result = save_csv(AUTOLOAD_CONFIG_PATH, update.autoload);
    if (!result && (update.seen & (1u << 8))) result = save_csv(FAVORITES_CONFIG_PATH, update.favorites);
    if (!result && (update.seen & 127)) result = write_config(&cfg);
    pthread_mutex_unlock(&config_mutex);
    if (!result && (update.seen & 1) && !update.values[0]) pldmgr_autoload_abort();
    return result;
}

int config_get_favorites_csv(char *out_buf, size_t max_size, int *out_file_exists) {
    if (!out_buf || !max_size) return -1;
    out_buf[0] = '\0';
    if (out_file_exists) *out_file_exists = 0;
    FILE *f = fopen(FAVORITES_CONFIG_PATH, "r");
    if (!f) return errno == ENOENT ? 0 : -1;
    if (out_file_exists) *out_file_exists = 1;
    char *line = malloc(max_size);
    if (!line) { fclose(f); return -1; }
    size_t length = 0, written = 0;
    int first = 1;
    for (;;) {
        int c = fgetc(f);
        if (c == 0 || (c == EOF && ferror(f))) goto failed;
        if (c == EOF || c == '\r' || c == '\n') {
            line[length] = '\0';
            char *start = line;
            while (*start && isspace((unsigned char)*start)) start++;
            char *end = line + length;
            while (end > start && isspace((unsigned char)end[-1])) end--;
            size_t count = (size_t)(end - start);
            if (count) {
                if (!first) {
                    if (written + 1 >= max_size) goto failed;
                    out_buf[written++] = ',';
                }
                if (count >= max_size - written) goto failed;
                memcpy(out_buf + written, start, count);
                written += count;
                out_buf[written] = '\0';
                first = 0;
            }
            length = 0;
            if (c == EOF) break;
        } else {
            if (length + 1 >= max_size) goto failed;
            line[length++] = (char)c;
        }
    }
    free(line);
    if (fclose(f) == 0) return 0;
    out_buf[0] = '\0';
    return -1;
failed:
    free(line);
    fclose(f);
    out_buf[0] = '\0';
    return -1;
}


int config_save_favorites_csv(const char *csv) {
    pthread_mutex_lock(&config_mutex);
    int result = save_csv(FAVORITES_CONFIG_PATH, csv);
    pthread_mutex_unlock(&config_mutex); return result;
}
int config_update_list_entry(const char *path, const char *old_key, const char *new_key) {
    if (!path || !old_key || !old_key[0] || strpbrk(old_key, ",\r\n") ||
        strlen(old_key) >= LIST_CAPACITY ||
        (new_key && (strlen(new_key) >= LIST_CAPACITY || strpbrk(new_key, ",\r\n")))) return -1;
    pthread_mutex_lock(&config_mutex);
    char *old = NULL; size_t size = 0; int result = -1;
    if (read_file_text(path, &old, &size)) { result = errno == ENOENT ? 0 : -1; goto done; }
    if (size > CONFIG_LIMIT || memchr(old, 0, size)) goto done;
    size_t capacity = size + 1;
    /* Each matching line may grow. Bound allocation even for legacy big files. */
    if (new_key) capacity += (size + 1) * (strlen(new_key) + 1);
    if (capacity > 16 * 1024 * 1024) goto done;
    char *text = malloc(capacity);
    if (!text) goto done;
    size_t pos = 0; int changed = 0; char *save = NULL;
    for (char *line = strtok_r(old, "\r\n", &save); line; line = strtok_r(NULL, "\r\n", &save)) {
        while (*line && isspace((unsigned char)*line)) line++;
        char *end = line + strlen(line);
        while (end > line && isspace((unsigned char)end[-1])) *--end = 0;
        if (!strcmp(line, old_key)) { changed = 1; line = (char *)new_key; }
        if (line && *line) { size_t n = strlen(line); memcpy(text + pos, line, n); pos += n; text[pos++] = '\n'; }
    }
    result = changed ? (pos >= LIST_CAPACITY ? -1 : write_file_text(path, text, pos)) : 0;
    free(text);
done:
    free(old); pthread_mutex_unlock(&config_mutex); return result;
}
int pldmgr_favorites_update_entry(const char *old_key, const char *new_key) {
    int result = config_update_list_entry(FAVORITES_CONFIG_PATH, old_key, new_key);
    if (result) pldmgr_log("[Favorites] Failed to update entry: %s\n", old_key ? old_key : "");
    return result;
}
