/*
 * HTTP Server — Route dispatch for all API and asset endpoints.
 *
 * Contains the MHD on_request callback and all route-handling logic
 * previously embedded in main.c.
 */

#include <stdio.h>
#include <stdlib.h>
#include <string.h>
#include <strings.h>
#include <ctype.h>
#include <errno.h>
#include <sys/stat.h>
#include <unistd.h>
#include <microhttpd.h>

#include "http_server.h"
#include "pldmgr.h"
#include "log_server.h"
#include "config.h"
#include "payload_mgr.h"
#include "repository.h"
#include "sha256.h"
#include "sources.h"
#include "history_mgr.h"
#include "process_mgr.h"
#include "ps5_launcher.h"
#include "slot.h"
#include "json_helpers.h"

#include "assets_index_html.h"
#include "assets_cache_appcache.h"
#include "assets_favicon_svg.h"
#include "assets_icon_png.h"

#define RESPONSE_BUFFER_SIZE 4194304
#define SMALL_RESPONSE_BUFFER_SIZE 65536
#define SOURCES_RESPONSE_BUFFER_SIZE 524288
#define LOG_RESPONSE_BUFFER_SIZE (MAX_LOG_LINES * (6 * MAX_LOG_LINE_LEN + 4) + 32)

/* Shared flag — set from main() shutdown route, read by main loop */
volatile int http_keep_running = 1;

/* ── Helpers ───────────────────────────────────────────────── */

/* A failed response creation leaves MUST_FREE buffers owned by us. */
static struct MHD_Response *create_buffer_response(size_t size, void *buffer,
                                                  enum MHD_ResponseMemoryMode mode) {
    struct MHD_Response *resp = MHD_create_response_from_buffer(size, buffer, mode);
    if (!resp && mode == MHD_RESPMEM_MUST_FREE)
        free(buffer);
    return resp;
}

static enum MHD_Result add_response_header(struct MHD_Response *resp,
                                           const char *name, const char *value) {
    return resp ? MHD_add_response_header(resp, name, value) : MHD_NO;
}

/* Explicit actions use POST. Automatic cache refresh on a read remains a read;
 * repository_refresh is the explicit forced-refresh action. */
static int is_mutation_route(const char *url) {
    const char *routes[] = { ROUTE_UPLOAD, ROUTE_DELETE, ROUTE_SHUTDOWN,
        ROUTE_SET_CONFIG, ROUTE_ABORT, ROUTE_AUTOLOAD_CLEAR, ROUTE_REPO_REFRESH,
        ROUTE_REPO_INSTALL, ROUTE_SOURCES_SET, ROUTE_SOURCES_ADD,
        ROUTE_SOURCES_REMOVE, ROUTE_REPO_CACHE_CLEAR, ROUTE_USB_MOVE_PERFORM, ROUTE_PROCESS_KILL };
    for (size_t i = 0; i < sizeof(routes)/sizeof(routes[0]); i++)
        if (!strcmp(url, routes[i])) return 1;
    return !strncmp(url, ROUTE_LOAD_PAYLOAD, strlen(ROUTE_LOAD_PAYLOAD));
}
/* HTTP daemon only. Compare the complete authority, including the port; never
 * accept suffix/prefix matches, null/file origins, userinfo, or multiple origins.
 * Host matching is CSRF protection, not authentication or DNS-rebinding defense. */
static int same_origin(const char *value, const char *host, int referer) {
    if (!value || !host || !host[0] || strncmp(value, "http://", 7)) return 0;
    for (const unsigned char *p = (const unsigned char *)host; *p; p++)
        if (!isalnum(*p) && *p != '.' && *p != '-' && *p != ':' && *p != '[' && *p != ']') return 0;
    const char *authority = value + 7;
    size_t n = strcspn(authority, "/?#");
    if (strlen(host) != n || strncasecmp(authority, host, n)) return 0;
    return referer ? (!authority[n] || authority[n] == '/') : authority[n] == 0;
}
static int mutation_authorized(struct MHD_Connection *conn) {
    const char *host = MHD_lookup_connection_value(conn, MHD_HEADER_KIND, "Host");
    const char *origin = MHD_lookup_connection_value(conn, MHD_HEADER_KIND, "Origin");
    const char *referer = MHD_lookup_connection_value(conn, MHD_HEADER_KIND, "Referer");
    const char *site = MHD_lookup_connection_value(conn, MHD_HEADER_KIND, "Sec-Fetch-Site");
    if (site && !strcmp(site, "cross-site")) return 0;
    if (origin) return same_origin(origin, host, 0);
    if (referer) return same_origin(referer, host, 1);
    const char *marker = MHD_lookup_connection_value(conn, MHD_HEADER_KIND, "X-PLDMGR-Request");
    return marker && !strcmp(marker, "1");
}
static int preflight_headers_valid(const char *headers) {
    if (!headers || !headers[0]) return 1;
    const char *p = headers;
    while (*p) {
        while (*p == ' ' || *p == '\t') p++;
        const char *start = p;
        while (*p && *p != ',') p++;
        const char *end = p;
        while (end > start && (end[-1] == ' ' || end[-1] == '\t')) end--;
        size_t n = (size_t)(end-start);
        if (!((n == 12 && !strncasecmp(start, "Content-Type", n)) ||
              (n == 16 && !strncasecmp(start, "X-PLDMGR-Request", n)))) return 0;
        if (*p && !*++p) return 0;
    }
    return 1;
}

/* MHD retains its own reference on a successful queue. Always release ours,
 * including when queueing fails. CORS headers have exactly one owner. */
static enum MHD_Result send_response(struct MHD_Connection *conn,
                                     unsigned int status, struct MHD_Response *resp) {
    if (!resp)
        return MHD_NO;
    enum MHD_Result ret = MHD_NO;
    const char *origin = MHD_lookup_connection_value(conn, MHD_HEADER_KIND, "Origin");
    const char *host = MHD_lookup_connection_value(conn, MHD_HEADER_KIND, "Host");
    int headers_ok = add_response_header(resp, "Vary", "Origin") == MHD_YES;
    if (origin && same_origin(origin, host, 0))
        headers_ok = headers_ok && add_response_header(resp, "Access-Control-Allow-Origin", origin) == MHD_YES;
    if (headers_ok) ret = MHD_queue_response(conn, status, resp);
    MHD_destroy_response(resp);
    return ret;
}

static enum MHD_Result send_out_of_memory(struct MHD_Connection *conn) {
    static const char oom[] = "{\"error\":\"Out of memory\"}";
    struct MHD_Response *resp = create_buffer_response(
        sizeof(oom) - 1, (void *)oom, MHD_RESPMEM_PERSISTENT);
    add_response_header(resp, "Content-Type", "application/json");
    return send_response(conn, MHD_HTTP_INTERNAL_SERVER_ERROR, resp);
}

/* Messages passed here are fixed application strings, not user input. */
static enum MHD_Result send_request_error(struct MHD_Connection *conn,
                                          unsigned int status, const char *message) {
    char json[256];
    snprintf(json, sizeof(json), "{\"error\":\"%s\"}", message);
    struct MHD_Response *resp = create_buffer_response(
        strlen(json), json, MHD_RESPMEM_MUST_COPY);
    add_response_header(resp, "Content-Type", "application/json");
    return send_response(conn, status, resp);
}

static enum MHD_Result send_serialization_error(struct MHD_Connection *conn, char *buffer) {
    free(buffer);
    return send_request_error(conn, MHD_HTTP_INTERNAL_SERVER_ERROR,
                              "JSON serialization failed or exceeds the size limit");
}

/* Success is separate from response creation: a failed OOM response must not
 * be mistaken for a successful buffer allocation. */
static int alloc_response_buffer_sized(char **out, size_t size) {
    *out = malloc(size);
    if (*out && size) (*out)[0] = '\0';
    return *out != NULL;
}

static int alloc_response_buffer(char **out) {
    return alloc_response_buffer_sized(out, RESPONSE_BUFFER_SIZE);
}

static int alloc_response_buffer_small(char **out) {
    return alloc_response_buffer_sized(out, SMALL_RESPONSE_BUFFER_SIZE);
}

static int is_safe_filename(const char *name) {
    if (!name || name[0] == '\0') return 0;
    if (strstr(name, "/") || strstr(name, "..")) return 0;
    return 1;
}

static void url_decode_in_place(char *str) {
    char *src = str, *dst = str;
    while (*src) {
        if (*src == '%' && src[1] && src[2] && isxdigit((unsigned char)src[1]) && isxdigit((unsigned char)src[2])) {
            int h1 = tolower((unsigned char)src[1]);
            int h2 = tolower((unsigned char)src[2]);
            int v1 = isdigit(h1) ? (h1 - '0') : (h1 - 'a' + 10);
            int v2 = isdigit(h2) ? (h2 - '0') : (h2 - 'a' + 10);
            *dst++ = (char)((v1 << 4) | v2);
            src += 3;
        } else if (*src == '+') {
            *dst++ = ' ';
            src++;
        } else {
            *dst++ = *src++;
        }
    }
    *dst = '\0';
}

/* ── POST request state ────────────────────────────────────── */

struct PostStatus {
    char *data;
    size_t size;
    size_t capacity;
    unsigned int error;
};

struct UploadStatus {
    FILE *fp;
    size_t total_size;
    unsigned int error;
    char filename[256];
    char temp_path[512];
    char mode[32];
};

enum RequestKind { REQUEST_CONFIG, REQUEST_SOURCES, REQUEST_UPLOAD, REQUEST_ACTION };

struct RequestContext {
    enum RequestKind kind;
    int finished;
    union {
        struct PostStatus post;
        struct UploadStatus upload;
    } state;
};

/* Read-only requests need no allocation. This marker is never dereferenced. */
#define REQUEST_MARKER ((void *)1)

void http_request_completed(void *cls, struct MHD_Connection *conn,
                            void **con_cls, enum MHD_RequestTerminationCode reason) {
    (void)cls;
    (void)conn;
    (void)reason;
    if (!con_cls || !*con_cls)
        return;
    void *context = *con_cls;
    *con_cls = NULL;
    if (context == REQUEST_MARKER)
        return;

    struct RequestContext *request = context;
    if (request->kind == REQUEST_UPLOAD) {
        struct UploadStatus *upload = &request->state.upload;
        if (upload->fp && fclose(upload->fp) != 0)
            pldmgr_log("[PLDMGR] Warning: Failed to close upload during cleanup\n");
        /* Each request owns a unique path. A committed file was renamed out
         * of this path; remove any uncommitted remainder on every exit. */
        if (upload->temp_path[0] && remove(upload->temp_path) != 0 && errno != ENOENT)
            pldmgr_log("[PLDMGR] Warning: Failed to remove upload temp file: %s\n",
                       upload->temp_path);
    } else {
        free(request->state.post.data);
    }
    free(request);
}

static unsigned int check_body_length(struct MHD_Connection *conn, size_t limit) {
    const char *value = MHD_lookup_connection_value(conn, MHD_HEADER_KIND, "Content-Length");
    if (!value)
        return 0; /* Chunked/unknown length is checked while receiving data. */
    if (!isdigit((unsigned char)*value))
        return MHD_HTTP_BAD_REQUEST;
    errno = 0;
    char *end;
    unsigned long long length = strtoull(value, &end, 10);
    if (errno == ERANGE || *end)
        return MHD_HTTP_BAD_REQUEST;
    return length > limit ? MHD_HTTP_CONTENT_TOO_LARGE : 0;
}

static void append_post_data(struct PostStatus *post, const char *data, size_t size) {
    if (post->error)
        return;
    if (size > HTTP_POST_BODY_LIMIT - post->size) {
        post->error = MHD_HTTP_CONTENT_TOO_LARGE;
        return;
    }
    size_t needed = post->size + size + 1;
    if (needed > post->capacity) {
        size_t capacity = post->capacity ? post->capacity : 4096;
        while (capacity < needed) {
            capacity = capacity > (HTTP_POST_BODY_LIMIT + 1) / 2
                ? HTTP_POST_BODY_LIMIT + 1 : capacity * 2;
        }
        char *new_data = realloc(post->data, capacity);
        if (!new_data) {
            post->error = MHD_HTTP_INTERNAL_SERVER_ERROR;
            return;
        }
        post->data = new_data;
        post->capacity = capacity;
    }
    memcpy(post->data + post->size, data, size);
    post->size += size;
    post->data[post->size] = '\0';
}

static void prepare_upload(struct MHD_Connection *conn, struct UploadStatus *upload) {
    const char *filename = MHD_lookup_connection_value(conn, MHD_GET_ARGUMENT_KIND, "filename");
    const char *mode = MHD_lookup_connection_value(conn, MHD_GET_ARGUMENT_KIND, "mode");
    if (!mode)
        mode = "new";
    if (!is_safe_filename(filename) || strlen(filename) >= sizeof(upload->filename) ||
        strlen(mode) >= sizeof(upload->mode)) {
        upload->error = MHD_HTTP_BAD_REQUEST;
        return;
    }
    strcpy(upload->filename, filename);
    strcpy(upload->mode, mode);
    upload->error = check_body_length(conn, HTTP_UPLOAD_BODY_LIMIT);
    if (upload->error)
        return;

    if ((mkdir(BASE_DATA_DIR, 0777) != 0 && errno != EEXIST) ||
        (mkdir(PAYLOADS_STORAGE_DIR, 0777) != 0 && errno != EEXIST)) {
        upload->error = MHD_HTTP_INTERNAL_SERVER_ERROR;
        return;
    }
    int n = snprintf(upload->temp_path, sizeof(upload->temp_path),
                     "%s/.upload-XXXXXX", PAYLOADS_STORAGE_DIR);
    if (n < 0 || (size_t)n >= sizeof(upload->temp_path)) {
        upload->temp_path[0] = '\0';
        upload->error = MHD_HTTP_INTERNAL_SERVER_ERROR;
        return;
    }
    int fd = mkstemp(upload->temp_path);
    if (fd < 0) {
        upload->temp_path[0] = '\0'; /* No file was acquired to clean up. */
        upload->error = MHD_HTTP_INTERNAL_SERVER_ERROR;
        return;
    }
    upload->fp = fdopen(fd, "wb");
    if (!upload->fp) {
        close(fd);
        upload->error = MHD_HTTP_INTERNAL_SERVER_ERROR;
        return;
    }
    pldmgr_log("[PLDMGR] Starting upload: %s (mode: %s)\n", filename, mode);
}

static enum MHD_Result finish_request_error(struct MHD_Connection *conn,
                                           struct RequestContext *request,
                                           unsigned int status) {
    request->finished = 1;
    if (status == MHD_HTTP_CONTENT_TOO_LARGE)
        return send_request_error(conn, status, "Request body exceeds the size limit");
    if (status == MHD_HTTP_BAD_REQUEST)
        return send_request_error(conn, status, "Invalid or empty request");
    return send_request_error(conn, status, "Failed to receive or save request");
}

/* Build complete CSV entries within the output limit, including lines that
 * span read chunks. Excess input is reported rather than silently truncated. */
static int read_autoload_list(char *out, size_t size, int include_delays) {
    if (!size)
        return -1;
    out[0] = '\0';
    FILE *file = fopen(AUTOLOAD_CONFIG_PATH, "r");
    if (!file)
        return errno == ENOENT ? 0 : -1;
    size_t pos = 0;
    int line_start = 1, has_data = 0, skip = 0, result = 0;
    int c;
    while ((c = fgetc(file)) != EOF) {
        if (c == '\0') {
            result = -1;
            break;
        }
        if (c == '\n') {
            line_start = 1; has_data = 0; skip = 0;
            continue;
        }
        if (line_start) {
            skip = !include_delays && c == '!';
            line_start = 0;
        }
        if (c == '\r')
            skip = 1;
        if (skip)
            continue;
        size_t needed = (!has_data && pos ? 1 : 0) + 1;
        if (needed >= size - pos) {
            result = -1;
            break;
        }
        if (!has_data && pos)
            out[pos++] = ',';
        out[pos++] = (char)c;
        has_data = 1;
    }
    out[pos] = '\0';
    if (ferror(file))
        result = -1;
    fclose(file);
    return result;
}

/* ── Helper: Noisy routes ──────────────────────────────────── */

/* Filter out endpoints that the frontend polls automatically to reduce log spam */
static int is_noisy_route(const char *url) {
    if (strcmp(url, ROUTE_LOG) == 0) return 1;
    if (strcmp(url, ROUTE_INDEX) == 0) return 1;
    if (strcmp(url, ROUTE_INDEX_HTML) == 0) return 1;
    if (strcmp(url, "/favicon.svg") == 0) return 1;
    if (strcmp(url, "/icon.png") == 0) return 1;
    if (strcmp(url, ROUTE_CACHE_MANIFEST) == 0) return 1;
    if (strcmp(url, ROUTE_GETIP) == 0) return 1;
    if (strcmp(url, ROUTE_AUTOLOAD_STATUS) == 0) return 1;
    if (strcmp(url, ROUTE_VERSION) == 0) return 1;
    if (strcmp(url, ROUTE_LIST_PAYLOADS) == 0) return 1;
    if (strcmp(url, ROUTE_GET_CONFIG) == 0) return 1;
    if (strcmp(url, ROUTE_REPO_LIST) == 0) return 1;
    if (strcmp(url, ROUTE_PROCESSES_LIST) == 0) return 1;
    if (strcmp(url, ROUTE_HISTORY_LIST) == 0) return 1;
    if (strcmp(url, ROUTE_UPDATE_CHECK) == 0) return 1;
    if (strcmp(url, "/events") == 0) return 1;
    return 0;
}

/* ── Main request handler ──────────────────────────────────── */

enum MHD_Result http_on_request(void *cls, struct MHD_Connection *conn,
                                const char *url, const char *method,
                                const char *version, const char *upload_data,
                                size_t *upload_data_size, void **con_cls) {
    (void)cls;
    (void)version;

    int mutation = is_mutation_route(url);
    if (!strcmp(method, "OPTIONS")) {
        const char *origin = MHD_lookup_connection_value(conn, MHD_HEADER_KIND, "Origin");
        const char *host = MHD_lookup_connection_value(conn, MHD_HEADER_KIND, "Host");
        const char *requested_method = MHD_lookup_connection_value(conn, MHD_HEADER_KIND, "Access-Control-Request-Method");
        const char *headers = MHD_lookup_connection_value(conn, MHD_HEADER_KIND, "Access-Control-Request-Headers");
        const char *allowed_method = mutation ? "POST" : "GET";
        if (!same_origin(origin, host, 0) || !preflight_headers_valid(headers) ||
            (requested_method && strcmp(requested_method, allowed_method)))
            return send_request_error(conn, MHD_HTTP_FORBIDDEN, "Preflight rejected");
        struct MHD_Response *resp = create_buffer_response(0, NULL, MHD_RESPMEM_PERSISTENT);
        add_response_header(resp, "Access-Control-Allow-Methods", allowed_method);
        add_response_header(resp, "Access-Control-Allow-Headers", "Content-Type, X-PLDMGR-Request");
        return send_response(conn, MHD_HTTP_OK, resp);
    }
    if (strcmp(method, mutation ? "POST" : "GET")) {
        struct MHD_Response *resp = create_buffer_response(0, NULL, MHD_RESPMEM_PERSISTENT);
        add_response_header(resp, "Allow", mutation ? "POST, OPTIONS" : "GET, OPTIONS");
        return send_response(conn, MHD_HTTP_METHOD_NOT_ALLOWED, resp);
    }
    /* Reject before allocating request state, opening an upload, activating
     * browser/autoload state, consuming a body, or calling any backend action. */
    if (mutation && !mutation_authorized(conn))
        return send_request_error(conn, MHD_HTTP_FORBIDDEN, "Mutation origin or request marker rejected");

    /* Set flag that we received a request (meaning browser is open) */
    if (mutation_authorized(conn)) log_server_set_active();

    /* The completion callback owns request state on success, error, disconnect,
     * and daemon stop. Keep it alive until MHD finishes the request. */
    if (*con_cls == NULL) {
        int upload = strcmp(url, ROUTE_UPLOAD) == 0;
        int config = strcmp(url, ROUTE_SET_CONFIG) == 0 && strcmp(method, "POST") == 0;
        int sources = strcmp(url, ROUTE_SOURCES_SET) == 0 && strcmp(method, "POST") == 0;
        if (upload || config || sources || mutation) {
            struct RequestContext *request = malloc(sizeof(*request));
            if (!request) {
                *con_cls = REQUEST_MARKER;
                return send_out_of_memory(conn);
            }
            memset(request, 0, sizeof(*request));
            request->kind = upload ? REQUEST_UPLOAD : (config ? REQUEST_CONFIG : (sources ? REQUEST_SOURCES : REQUEST_ACTION));
            *con_cls = request;
            unsigned int error;
            if (upload) {
                prepare_upload(conn, &request->state.upload);
                error = request->state.upload.error;
            } else {
                error = check_body_length(conn, HTTP_POST_BODY_LIMIT);
                request->state.post.error = error;
            }
            if (error)
                return finish_request_error(conn, request, error);
        } else {
            *con_cls = REQUEST_MARKER;
        }
        return MHD_YES;
    }

    if (*con_cls != REQUEST_MARKER) {
        struct RequestContext *request = *con_cls;
        if (request->finished) {
            *upload_data_size = 0;
            return MHD_YES;
        }
        if (request->kind == REQUEST_ACTION) {
            struct PostStatus *post = &request->state.post;
            if (*upload_data_size) {
                if (*upload_data_size > HTTP_POST_BODY_LIMIT - post->size) {
                    *upload_data_size = 0;
                    return finish_request_error(conn, request, MHD_HTTP_CONTENT_TOO_LARGE);
                }
                post->size += *upload_data_size;
                *upload_data_size = 0;
                return MHD_YES;
            }
            request->finished = 1;
        } else if (request->kind != REQUEST_UPLOAD) {
            struct PostStatus *post = &request->state.post;
            if (*upload_data_size) {
                append_post_data(post, upload_data, *upload_data_size);
                *upload_data_size = 0;
                if (post->error)
                    return finish_request_error(conn, request, post->error);
                return MHD_YES;
            }
            if (!post->size)
                return finish_request_error(conn, request, MHD_HTTP_BAD_REQUEST);
            int rc = request->kind == REQUEST_CONFIG
                ? config_handle_set_json(post->data) : sources_save(post->data, post->size);
            if (rc != 0)
                return finish_request_error(conn, request, rc == CONFIG_INVALID ? MHD_HTTP_BAD_REQUEST : MHD_HTTP_INTERNAL_SERVER_ERROR);
            request->finished = 1;
            struct MHD_Response *resp = create_buffer_response(
                strlen(MSG_OK), (void *)MSG_OK, MHD_RESPMEM_PERSISTENT);
            add_response_header(resp, "Content-Type", "text/plain");
            return send_response(conn, MHD_HTTP_OK, resp);
        }

        if (request->kind == REQUEST_UPLOAD) {
            struct UploadStatus *status = &request->state.upload;
            if (*upload_data_size) {
                if (*upload_data_size > HTTP_UPLOAD_BODY_LIMIT - status->total_size) {
                    status->error = MHD_HTTP_CONTENT_TOO_LARGE;
                } else {
                    size_t written = fwrite(upload_data, 1, *upload_data_size, status->fp);
                    status->total_size += written;
                    if (written != *upload_data_size)
                        status->error = MHD_HTTP_INTERNAL_SERVER_ERROR;
                }
                *upload_data_size = 0;
                if (status->error)
                    return finish_request_error(conn, request, status->error);
                return MHD_YES;
            }

            /* Both operations must run: a failed flush must not skip fclose. */
            if (fflush(status->fp) != 0)
                status->error = MHD_HTTP_INTERNAL_SERVER_ERROR;
            if (fclose(status->fp) != 0)
                status->error = MHD_HTTP_INTERNAL_SERVER_ERROR;
            status->fp = NULL;
            if (!status->total_size && !status->error)
                status->error = MHD_HTTP_BAD_REQUEST;
            if (!status->error) {
                char msg[256] = "";
                char out_slot[128];
                if (payload_mgr_import_to_storage_mode(status->filename, status->temp_path,
                                                      status->mode, "web_upload", "",
                                                      msg, sizeof(msg), out_slot, sizeof(out_slot)) != 0) {
                    pldmgr_log("[PLDMGR] !!! Failed to commit upload: %s\n", msg);
                    status->error = MHD_HTTP_INTERNAL_SERVER_ERROR;
                } else {
                    /* Import consumed the temp file. Relinquish its pathname so
                     * completion cannot unlink a later owner of the same name. */
                    status->temp_path[0] = '\0';
                }
            }
            pldmgr_log("[PLDMGR] Upload finished. Total bytes: %zu, Error: %u\n",
                       status->total_size, status->error);
            if (status->error)
                return finish_request_error(conn, request, status->error);
            request->finished = 1;
            struct MHD_Response *resp = create_buffer_response(
                strlen(MSG_OK), (void *)MSG_OK, MHD_RESPMEM_PERSISTENT);
            add_response_header(resp, "Content-Type", "text/plain");
            return send_response(conn, MHD_HTTP_OK, resp);
        }
    }

    /* ── Log significant requests ──────────────────────────── */
    if (!is_noisy_route(url)) {
        pldmgr_log("[PLDMGR] Request: %s %s\n", method, url);
    }

    struct MHD_Response *resp = NULL;
    int http_status = MHD_HTTP_OK;

    /* ── USB routes ────────────────────────────────────────── */
    if (strcmp(url, ROUTE_USB_MOVE_CHECK) == 0) {
        const char *path = MHD_lookup_connection_value(conn, MHD_GET_ARGUMENT_KIND, "path");
        if (path) {
            char *resp_buf;
            if (!alloc_response_buffer_small(&resp_buf))
                return send_out_of_memory(conn);
            int rc = payload_mgr_usb_check(path, resp_buf, SMALL_RESPONSE_BUFFER_SIZE);
            if (!resp_buf[0]) return send_serialization_error(conn, resp_buf);
            resp = create_buffer_response(strlen(resp_buf), (void *)resp_buf, MHD_RESPMEM_MUST_FREE);
            add_response_header(resp, "Content-Type", "application/json");

            return send_response(conn, rc == 0 ? MHD_HTTP_OK : MHD_HTTP_BAD_REQUEST, resp);
        } else {
            const char *err = "{\"error\":\"Missing path\"}";
            resp = create_buffer_response(strlen(err), (void *)err, MHD_RESPMEM_MUST_COPY);
            add_response_header(resp, "Content-Type", "application/json");
        }

        return send_response(conn, MHD_HTTP_BAD_REQUEST, resp);
    }

    if (strcmp(url, ROUTE_USB_MOVE_PERFORM) == 0) {
        const char *path = MHD_lookup_connection_value(conn, MHD_GET_ARGUMENT_KIND, "path");
        const char *mode = MHD_lookup_connection_value(conn, MHD_GET_ARGUMENT_KIND, "mode");
        const char *overwrite_str = MHD_lookup_connection_value(conn, MHD_GET_ARGUMENT_KIND, "overwrite");
        if (!mode) {
            int overwrite = (overwrite_str && strcmp(overwrite_str, "true") == 0) ? 1 : 0;
            mode = overwrite ? "replace" : "new";
        }
        const char *keep_original_str = MHD_lookup_connection_value(conn, MHD_GET_ARGUMENT_KIND, "keep_original");
        int keep_original = (keep_original_str && strcmp(keep_original_str, "true") == 0) ? 1 : 0;
        if (path) {
            char *resp_buf;
            if (!alloc_response_buffer_small(&resp_buf))
                return send_out_of_memory(conn);
            int rc = payload_mgr_usb_move(path, mode, keep_original, resp_buf, SMALL_RESPONSE_BUFFER_SIZE);
            if (!resp_buf[0]) return send_serialization_error(conn, resp_buf);
            resp = create_buffer_response(strlen(resp_buf), (void *)resp_buf, MHD_RESPMEM_MUST_FREE);
            add_response_header(resp, "Content-Type", "application/json");

            return send_response(conn, rc == 0 ? MHD_HTTP_OK : MHD_HTTP_BAD_REQUEST, resp);
        } else {
            const char *err = "{\"error\":\"Missing path\"}";
            resp = create_buffer_response(strlen(err), (void *)err, MHD_RESPMEM_MUST_COPY);
            add_response_header(resp, "Content-Type", "application/json");
        }

        return send_response(conn, MHD_HTTP_BAD_REQUEST, resp);
    }

    /* ── Static assets ─────────────────────────────────────── */
    if (strcmp(url, ROUTE_INDEX) == 0 || strcmp(url, ROUTE_INDEX_HTML) == 0) {
        resp = create_buffer_response(assets_index_html_len,
                                               (void *)assets_index_html,
                                               MHD_RESPMEM_PERSISTENT);
        add_response_header(resp, "Content-Type", "text/html");
        add_response_header(resp, "Cache-Control", "no-cache, must-revalidate");
    } else if (strcmp(url, ROUTE_CACHE_MANIFEST) == 0) {
        resp = create_buffer_response(assets_cache_appcache_len,
                                               (void *)assets_cache_appcache,
                                               MHD_RESPMEM_PERSISTENT);
        add_response_header(resp, "Content-Type", "text/cache-manifest");
        add_response_header(resp, "Cache-Control", "no-cache, must-revalidate");
    } else if (strcmp(url, "/favicon.svg") == 0) {
        resp = create_buffer_response(assets_favicon_svg_len,
                                               (void *)assets_favicon_svg,
                                               MHD_RESPMEM_PERSISTENT);
        add_response_header(resp, "Content-Type", "image/svg+xml");
        add_response_header(resp, "Cache-Control", "max-age=604800");
    } else if (strcmp(url, "/icon.png") == 0) {
        resp = create_buffer_response(assets_icon_png_len,
                                               (void *)assets_icon_png,
                                               MHD_RESPMEM_PERSISTENT);
        add_response_header(resp, "Content-Type", "image/png");
        add_response_header(resp, "Cache-Control", "max-age=604800");

    /* ── API routes ────────────────────────────────────────── */
    } else if (strcmp(url, ROUTE_UPDATE_CHECK) == 0) {
        const char *resp_buf = "{\"status\":\"none\"}";
        resp = create_buffer_response(strlen(resp_buf), (void *)resp_buf, MHD_RESPMEM_PERSISTENT);
        add_response_header(resp, "Content-Type", "application/json");

        return send_response(conn, MHD_HTTP_OK, resp);
    } else if (strcmp(url, ROUTE_CHECK) == 0) {
        const char *filename = MHD_lookup_connection_value(conn, MHD_GET_ARGUMENT_KIND, "filename");
        if (!filename) {
            const char *err = "{\"error\":\"Missing filename\"}";
            resp = create_buffer_response(strlen(err), (void *)err, MHD_RESPMEM_MUST_COPY);
            add_response_header(resp, "Content-Type", "application/json");

            return send_response(conn, MHD_HTTP_BAD_REQUEST, resp);
        }
        if (!is_safe_filename(filename)) {
            const char *err = "{\"error\":\"Invalid filename\"}";
            resp = create_buffer_response(strlen(err), (void *)err, MHD_RESPMEM_MUST_COPY);
            add_response_header(resp, "Content-Type", "application/json");

            return send_response(conn, MHD_HTTP_BAD_REQUEST, resp);
        }
        char *resp_buf;
        if (!alloc_response_buffer_small(&resp_buf))
            return send_out_of_memory(conn);
        payload_mgr_check_existing(filename, resp_buf, SMALL_RESPONSE_BUFFER_SIZE);
        if (!resp_buf[0]) return send_serialization_error(conn, resp_buf);
        resp = create_buffer_response(strlen(resp_buf), (void *)resp_buf, MHD_RESPMEM_MUST_FREE);
        add_response_header(resp, "Content-Type", "application/json");

        return send_response(conn, MHD_HTTP_OK, resp);
    } else if (strcmp(url, ROUTE_LIST_PAYLOADS) == 0) {
        char *resp_buf;
        if (!alloc_response_buffer(&resp_buf))
            return send_out_of_memory(conn);
        size_t len = payload_mgr_list_json(resp_buf, RESPONSE_BUFFER_SIZE);
        if (!len || len >= RESPONSE_BUFFER_SIZE) return send_serialization_error(conn, resp_buf);
        resp = create_buffer_response(len, (void *)resp_buf, MHD_RESPMEM_MUST_FREE);
        add_response_header(resp, "Content-Type", "application/json");
    } else if (strcmp(url, ROUTE_PROCESSES_LIST) == 0) {
        char *resp_buf;
        if (!alloc_response_buffer_small(&resp_buf))
            return send_out_of_memory(conn);
        size_t len = process_list_json(resp_buf, SMALL_RESPONSE_BUFFER_SIZE);
        if (!len || len >= SMALL_RESPONSE_BUFFER_SIZE) return send_serialization_error(conn, resp_buf);
        resp = create_buffer_response(len, (void *)resp_buf, MHD_RESPMEM_MUST_FREE);
        add_response_header(resp, "Content-Type", "application/json");
    } else if (strcmp(url, ROUTE_HISTORY_LIST) == 0) {
        char *json = malloc(SMALL_RESPONSE_BUFFER_SIZE);
        if (json) {
            size_t len = history_mgr_to_json(json, SMALL_RESPONSE_BUFFER_SIZE);
            if (!len) return send_serialization_error(conn, json);
            resp = create_buffer_response(len, (void *)json, MHD_RESPMEM_MUST_FREE);
            add_response_header(resp, "Content-Type", "application/json");

            return send_response(conn, MHD_HTTP_OK, resp);
        } else {
            return send_out_of_memory(conn);
        }
    } else if (strcmp(url, ROUTE_PROCESS_KILL) == 0) {
        const char *pid_str = MHD_lookup_connection_value(conn, MHD_GET_ARGUMENT_KIND, "pid");
        if (!pid_str) {
            const char *err = "{\"ok\":false,\"message\":\"Missing pid\"}";
            resp = create_buffer_response(strlen(err), (void *)err, MHD_RESPMEM_MUST_COPY);
            add_response_header(resp, "Content-Type", "application/json");

            return send_response(conn, MHD_HTTP_BAD_REQUEST, resp);
        }
        int pid = atoi(pid_str);
        int rc = process_kill(pid);

        pldmgr_log("[PLDMGR] Process kill requested for PID %d: %s\n", pid, rc == 0 ? "SUCCESS" : "FAILED");

        char json_resp[256];
        snprintf(json_resp, sizeof(json_resp), "{\"ok\":%s,\"message\":\"%s\"}",
                 rc == 0 ? "true" : "false", rc == 0 ? "Killed" : "Failed to kill");
        resp = create_buffer_response(strlen(json_resp), (void *)json_resp, MHD_RESPMEM_MUST_COPY);
        add_response_header(resp, "Content-Type", "application/json");

        return send_response(conn, rc == 0 ? MHD_HTTP_OK : MHD_HTTP_INTERNAL_SERVER_ERROR, resp);
    } else if (strcmp(url, ROUTE_REPO_CACHE_CLEAR) == 0) {
        int result = sources_clear_cache();
        const char *body = result ? "{\"ok\":false}" : "{\"ok\":true}";
        resp = create_buffer_response(strlen(body), (void *)body, MHD_RESPMEM_PERSISTENT);
        add_response_header(resp, "Content-Type", "application/json");
        return send_response(conn, result ? MHD_HTTP_INTERNAL_SERVER_ERROR : MHD_HTTP_OK, resp);
    } else if (strcmp(url, ROUTE_REPO_ICONS) == 0) {
        const char *source = MHD_lookup_connection_value(conn, MHD_GET_ARGUMENT_KIND, "source");
        char *buffer;
        if (!alloc_response_buffer(&buffer)) return send_out_of_memory(conn);
        size_t length = sources_icons_json(source, buffer, RESPONSE_BUFFER_SIZE);
        if (!length) {
            free(buffer);
            resp = create_buffer_response(strlen("{\"icons\":{}}"), (void *)"{\"icons\":{}}", MHD_RESPMEM_PERSISTENT);
            add_response_header(resp, "Content-Type", "application/json");
            return send_response(conn, MHD_HTTP_NOT_FOUND, resp);
        }
        resp = create_buffer_response(length, buffer, MHD_RESPMEM_MUST_FREE);
        add_response_header(resp, "Content-Type", "application/json");
        add_response_header(resp, "Cache-Control", "no-store");
    } else if (strcmp(url, ROUTE_REPO_LIST) == 0) {
        char *resp_buf;
        if (!alloc_response_buffer(&resp_buf))
            return send_out_of_memory(conn);
        PldmgrConfig cfg;
        config_read(&cfg);
        size_t len;
        if (cfg.multi_sources_enabled)
            len = sources_multi_repository_list_json(resp_buf, RESPONSE_BUFFER_SIZE, 0);
        else
            len = repository_list_json(resp_buf, RESPONSE_BUFFER_SIZE, 0);
        if (!len || len >= RESPONSE_BUFFER_SIZE) return send_serialization_error(conn, resp_buf);
        resp = create_buffer_response(len, (void *)resp_buf, MHD_RESPMEM_MUST_FREE);
        add_response_header(resp, "Content-Type", "application/json");
    } else if (strcmp(url, ROUTE_REPO_REFRESH) == 0) {
        char *resp_buf;
        if (!alloc_response_buffer(&resp_buf))
            return send_out_of_memory(conn);
        PldmgrConfig cfg;
        config_read(&cfg);
        size_t len2;
        if (cfg.multi_sources_enabled)
            len2 = sources_multi_repository_list_json(resp_buf, RESPONSE_BUFFER_SIZE, 1);
        else
            len2 = repository_list_json(resp_buf, RESPONSE_BUFFER_SIZE, 1);
        if (!len2 || len2 >= RESPONSE_BUFFER_SIZE) return send_serialization_error(conn, resp_buf);
        resp = create_buffer_response(len2, (void *)resp_buf, MHD_RESPMEM_MUST_FREE);
        add_response_header(resp, "Content-Type", "application/json");
    } else if (strcmp(url, ROUTE_REPO_INSTALL) == 0) {
        const char *id = MHD_lookup_connection_value(conn, MHD_GET_ARGUMENT_KIND, "id");
        const char *filename = MHD_lookup_connection_value(conn, MHD_GET_ARGUMENT_KIND, "filename");
        const char *source_url = MHD_lookup_connection_value(conn, MHD_GET_ARGUMENT_KIND, "source_url");
        if (!source_url) {
            source_url = MHD_lookup_connection_value(conn, MHD_GET_ARGUMENT_KIND, "repo_url");
        }
        const char *mode = MHD_lookup_connection_value(conn, MHD_GET_ARGUMENT_KIND, "mode");
        const char *replace_slot = MHD_lookup_connection_value(conn, MHD_GET_ARGUMENT_KIND, "replace_slot");

        char derived_id[128] = "";
        if (!id || id[0] == '\0') {
            if (filename && filename[0]) {
                slot_local_key(filename, derived_id, sizeof(derived_id));
                id = derived_id;
            } else {
                const char *err = "{\"ok\":false,\"message\":\"Missing id parameter\"}";
                resp = create_buffer_response(strlen(err), (void *)err, MHD_RESPMEM_MUST_COPY);
                add_response_header(resp, "Content-Type", "application/json");

                return send_response(conn, MHD_HTTP_BAD_REQUEST, resp);
            }
        }

        char *resp_buf;
        if (!alloc_response_buffer_small(&resp_buf))
            return send_out_of_memory(conn);

        int rc = sources_repo_install(source_url, id, mode, replace_slot, resp_buf, SMALL_RESPONSE_BUFFER_SIZE);
        if (!resp_buf[0]) return send_serialization_error(conn, resp_buf);
        resp = create_buffer_response(strlen(resp_buf), (void *)resp_buf, MHD_RESPMEM_MUST_FREE);
        add_response_header(resp, "Content-Type", "application/json");

        return send_response(conn, rc == 0 ? MHD_HTTP_OK : MHD_HTTP_BAD_REQUEST, resp);
    } else if (strcmp(url, ROUTE_SOURCES_LIST) == 0) {
        char *resp_buf;
        if (!alloc_response_buffer_sized(&resp_buf, SOURCES_RESPONSE_BUFFER_SIZE))
            return send_out_of_memory(conn);
        if (sources_list_json(resp_buf, SOURCES_RESPONSE_BUFFER_SIZE) != 0) resp_buf[0] = '\0';
        if (!resp_buf[0]) return send_serialization_error(conn, resp_buf);
        resp = create_buffer_response(strlen(resp_buf), (void *)resp_buf, MHD_RESPMEM_MUST_FREE);
        add_response_header(resp, "Content-Type", "application/json");
    } else if (strcmp(url, ROUTE_SOURCES_ADD) == 0) {
        const char *src_url = MHD_lookup_connection_value(conn, MHD_GET_ARGUMENT_KIND, "url");
        if (!src_url || !src_url[0]) {
            const char *err = "{\"ok\":false,\"message\":\"Missing url parameter\"}";
            resp = create_buffer_response(strlen(err), (void *)err, MHD_RESPMEM_MUST_COPY);
            add_response_header(resp, "Content-Type", "application/json");

            return send_response(conn, MHD_HTTP_BAD_REQUEST, resp);
        }
        char msg_buf_src[512] = "";
        int rc_src = sources_add(src_url, msg_buf_src, sizeof(msg_buf_src));
        char json_resp_src[4096];
        if (rc_src == 0) {
            char name_escaped[6 * 512];
            pldmgr_json_escape(msg_buf_src, name_escaped, sizeof(name_escaped));
            json_format(json_resp_src, sizeof(json_resp_src),
                     "{\"ok\":true,\"name\":\"%s\"}", name_escaped);
        } else {
            char msg_escaped[6 * 512];
            pldmgr_json_escape(msg_buf_src, msg_escaped, sizeof(msg_escaped));
            json_format(json_resp_src, sizeof(json_resp_src),
                     "{\"ok\":false,\"message\":\"%s\"}", msg_escaped);
        }
        if (!json_resp_src[0]) return send_request_error(conn, MHD_HTTP_INTERNAL_SERVER_ERROR, "JSON serialization failed or exceeds the size limit");
        resp = create_buffer_response(strlen(json_resp_src), (void *)json_resp_src, MHD_RESPMEM_MUST_COPY);
        add_response_header(resp, "Content-Type", "application/json");

        return send_response(conn, rc_src == 0 ? MHD_HTTP_OK : MHD_HTTP_BAD_REQUEST, resp);
    } else if (strcmp(url, ROUTE_SOURCES_REMOVE) == 0) {
        const char *idx_str = MHD_lookup_connection_value(conn, MHD_GET_ARGUMENT_KIND, "index");
        if (!idx_str) {
            const char *err = "{\"ok\":false,\"message\":\"Missing index parameter\"}";
            resp = create_buffer_response(strlen(err), (void *)err, MHD_RESPMEM_MUST_COPY);
            add_response_header(resp, "Content-Type", "application/json");

            return send_response(conn, MHD_HTTP_BAD_REQUEST, resp);
        }
        int idx = atoi(idx_str);
        char msg_rm[256] = "";
        int rc_rm = sources_remove(idx, msg_rm, sizeof(msg_rm));
        char json_rm[512];
        char msg_rm_e[6 * 512];
        pldmgr_json_escape(msg_rm, msg_rm_e, sizeof(msg_rm_e));
        json_format(json_rm, sizeof(json_rm), "{\"ok\":%s,\"message\":\"%s\"}",
                 rc_rm == 0 ? "true" : "false", msg_rm_e);
        if (!json_rm[0]) return send_request_error(conn, MHD_HTTP_INTERNAL_SERVER_ERROR, "JSON serialization failed or exceeds the size limit");
        resp = create_buffer_response(strlen(json_rm), (void *)json_rm, MHD_RESPMEM_MUST_COPY);
        add_response_header(resp, "Content-Type", "application/json");

        return send_response(conn, rc_rm == 0 ? MHD_HTTP_OK : MHD_HTTP_BAD_REQUEST, resp);
    } else if (strncmp(url, ROUTE_LOAD_PAYLOAD, strlen(ROUTE_LOAD_PAYLOAD)) == 0) {
        const char *arg = url + strlen(ROUTE_LOAD_PAYLOAD);
        char clean_arg[512];
        strncpy(clean_arg, arg, sizeof(clean_arg) - 1);
        clean_arg[sizeof(clean_arg) - 1] = '\0';
        url_decode_in_place(clean_arg);

        char resolved_path[512];
        if (strlen(arg) >= sizeof(clean_arg) || strstr(arg, "%00")) {
            const char *err = "Invalid payload path\n";
            resp = create_buffer_response(strlen(err), (void *)err, MHD_RESPMEM_PERSISTENT);
            http_status = MHD_HTTP_BAD_REQUEST;
        } else if (is_pldmgr_name(clean_arg)) {
            pldmgr_log("[PLDMGR] Refusing to launch payload manager itself: %s\n", clean_arg);
            const char *err = "Payload Manager cannot be launched as a payload\n";
            resp = create_buffer_response(strlen(err), (void *)err, MHD_RESPMEM_PERSISTENT);
            http_status = MHD_HTTP_BAD_REQUEST;
        } else if (slot_resolve(clean_arg, resolved_path, sizeof(resolved_path)) == 0) {
            if (is_pldmgr_name(resolved_path)) {
                pldmgr_log("[PLDMGR] Refusing to launch payload manager itself: %s\n", resolved_path);
                const char *err = "Payload Manager cannot be launched as a payload\n";
                resp = create_buffer_response(strlen(err), (void *)err, MHD_RESPMEM_PERSISTENT);
                http_status = MHD_HTTP_BAD_REQUEST;
            } else if (ps5_launch_elf(resolved_path) == 0) {
                resp = create_buffer_response(strlen(MSG_OK), (void *)MSG_OK,
                                                       MHD_RESPMEM_PERSISTENT);
            } else {
                const char *err = "Failed to launch payload\n";
                resp = create_buffer_response(strlen(err), (void *)err,
                                                       MHD_RESPMEM_PERSISTENT);
                http_status = MHD_HTTP_INTERNAL_SERVER_ERROR;
            }
        } else {
            pldmgr_log("[PLDMGR] !!! Payload slot/path rejected or not found: %s (raw: %s)\n", clean_arg, arg);
            const char *err = "Invalid payload name\n";
            resp = create_buffer_response(strlen(err), (void *)err,
                                                   MHD_RESPMEM_PERSISTENT);
            http_status = MHD_HTTP_BAD_REQUEST;
        }
        add_response_header(resp, "Content-Type", "text/plain");
    } else if (strcmp(url, ROUTE_DELETE) == 0) {
        const char *slot =
            MHD_lookup_connection_value(conn, MHD_GET_ARGUMENT_KIND, "slot");
        const char *filename =
            MHD_lookup_connection_value(conn, MHD_GET_ARGUMENT_KIND, "filename");
        const char *target = slot ? slot : filename;
        if (target) {
            if (strstr(target, "..")) {
                const char *err = "Invalid payload identifier\n";
                resp = create_buffer_response(strlen(err), (void *)err,
                                                       MHD_RESPMEM_PERSISTENT);
                http_status = MHD_HTTP_BAD_REQUEST;
            } else {
                if (payload_mgr_delete_payload(target) == 0) {
                    pldmgr_log("[PLDMGR] Deleted payload: %s\n", target);
                    resp = create_buffer_response(strlen(MSG_OK), (void *)MSG_OK,
                                                           MHD_RESPMEM_PERSISTENT);
                } else {
                    const char *err = "Failed to delete payload\n";
                    resp = create_buffer_response(strlen(err), (void *)err,
                                                           MHD_RESPMEM_PERSISTENT);
                    http_status = MHD_HTTP_INTERNAL_SERVER_ERROR;
                }
            }
        } else {
            const char *err = "Missing slot parameter\n";
            resp = create_buffer_response(strlen(err), (void *)err,
                                                   MHD_RESPMEM_PERSISTENT);
            http_status = MHD_HTTP_BAD_REQUEST;
        }
        add_response_header(resp, "Content-Type", "text/plain");
    } else if (strcmp(url, ROUTE_SHUTDOWN) == 0) {
        const char *msg = "Payload Manager Core shutting down...\n";
        pldmgr_log("[PLDMGR] %s", msg);
        resp = create_buffer_response(strlen(msg), (void *)msg,
                                               MHD_RESPMEM_PERSISTENT);
        add_response_header(resp, "Content-Type", "text/plain");
        http_keep_running = 0;
    } else if (strcmp(url, ROUTE_LOG) == 0) {
        char *resp_buf;
        if (!alloc_response_buffer_sized(&resp_buf, LOG_RESPONSE_BUFFER_SIZE))
            return send_out_of_memory(conn);
        size_t pos = log_build_json(resp_buf, LOG_RESPONSE_BUFFER_SIZE);
        if (!pos || pos >= LOG_RESPONSE_BUFFER_SIZE) return send_serialization_error(conn, resp_buf);
        resp = create_buffer_response(pos, (void *)resp_buf,
                                               MHD_RESPMEM_MUST_FREE);
        add_response_header(resp, "Content-Type", "application/json");
    } else if (strcmp(url, ROUTE_VERSION) == 0) {
        resp = create_buffer_response(
            strlen(MENU_VERSION), (void *)MENU_VERSION, MHD_RESPMEM_PERSISTENT);
        add_response_header(resp, "Content-Type", "text/plain");
    } else if (strcmp(url, ROUTE_GETIP) == 0) {
        char ip[64];
        if (pldmgr_get_local_ip(ip, sizeof(ip)) != 0) {
            strcpy(ip, "0.0.0.0");
        }
        resp = create_buffer_response(strlen(ip), (void *)ip,
                                               MHD_RESPMEM_MUST_COPY);
        add_response_header(resp, "Content-Type", "text/plain");
    } else if (strcmp(url, ROUTE_AUTOLOAD_STATUS) == 0) {
        if (mutation_authorized(conn)) pldmgr_autoload_frontend_ready();
        AutoloadSnapshot snapshot;
        pldmgr_autoload_get_snapshot(&snapshot);
        char current_escaped[6 * 128], list_escaped[6 * 4096];
        if (pldmgr_json_escape(snapshot.current,current_escaped,sizeof(current_escaped)) ||
            pldmgr_json_escape(snapshot.list,list_escaped,sizeof(list_escaped)))
            return send_request_error(conn,MHD_HTTP_INTERNAL_SERVER_ERROR,"Invalid autoload snapshot");

        PldmgrConfig cfg;
        config_read(&cfg);
        char *resp_buf;
        if (!alloc_response_buffer_small(&resp_buf))
            return send_out_of_memory(conn);
        json_format(resp_buf, SMALL_RESPONSE_BUFFER_SIZE,
                 "{\"remaining\":%d,\"remaining_ms\":%lld,\"total\":%d,\"done\":%d,\"current\":\"%s\","
                 "\"list\":\"%s\",\"delay\":%d,\"KILL_DISC_PLAYER_ON_STARTUP\":%s,\"SCAN_USB_PAYLOADS\":%s,"
                 "\"state\":\"%s\",\"successful\":%d,\"failed\":%d,\"skipped\":%d,\"outcomes\":\"%s\",\"entry_index\":%d,\"entry_duration_ms\":%d,\"entry_remaining_ms\":%lld}",
                 snapshot.remaining, snapshot.remaining_ms, snapshot.total, snapshot.done, current_escaped,
                 list_escaped, snapshot.delay, cfg.kill_disc_player ? "true" : "false", snapshot.scan_usb_payloads ? "true" : "false",
                 pldmgr_autoload_state_name(snapshot.state),snapshot.successful,snapshot.failed,snapshot.skipped,snapshot.outcomes,
                 snapshot.entry_index,snapshot.entry_duration_ms,snapshot.entry_remaining_ms);
        if (!resp_buf[0]) return send_serialization_error(conn, resp_buf);
        resp = create_buffer_response(strlen(resp_buf),
                                               (void *)resp_buf,
                                               MHD_RESPMEM_MUST_FREE);
        add_response_header(resp, "Content-Type", "application/json");
    } else if (strcmp(url, ROUTE_AUTOLOAD_CLEAR) == 0) {
        pldmgr_autoload_reset();
        const char *msg = "Autoload status cleared.\n";
        resp = create_buffer_response(strlen(msg), (void *)msg,
                                               MHD_RESPMEM_PERSISTENT);
        add_response_header(resp, "Content-Type", "text/plain");
    } else if (strcmp(url, ROUTE_ABORT) == 0) {
        pldmgr_autoload_abort();
        const char *msg = "Autoload sequence aborted.\n";
        resp = create_buffer_response(strlen(msg), (void *)msg,
                                               MHD_RESPMEM_PERSISTENT);
        add_response_header(resp, "Content-Type", "text/plain");
    } else if (strcmp(url, ROUTE_GET_CONFIG) == 0) {
        PldmgrConfig cfg;
        config_read(&cfg);

        char list_buf[4096];
        if (read_autoload_list(list_buf, sizeof(list_buf), 1) != 0)
            return send_request_error(conn, MHD_HTTP_INTERNAL_SERVER_ERROR,
                                      "Autoload list is unreadable or exceeds the size limit");

        char list_escaped[6 * 4096];
        pldmgr_json_escape(list_buf, list_escaped, sizeof(list_escaped));

        int fav_exists = 0;
        char fav_buf[4096] = {0};
        if (config_get_favorites_csv(fav_buf, sizeof(fav_buf), &fav_exists) != 0)
            return send_request_error(conn, MHD_HTTP_INTERNAL_SERVER_ERROR, "Favorites list is unreadable or exceeds the size limit");
        char fav_escaped[6 * 4096];
        pldmgr_json_escape(fav_buf, fav_escaped, sizeof(fav_escaped));

        char *resp_buf;
        if (!alloc_response_buffer_small(&resp_buf))
            return send_out_of_memory(conn);
        json_format(resp_buf, SMALL_RESPONSE_BUFFER_SIZE,
                 "{\"AUTOLOAD_ENABLED\":%s,\"AUTOLOAD_LIST\":\"%s\",\"LAST_"
                 "REPOSITORY_UPDATE\":%ld,"
                 "\"AUTO_BROWSER_OPEN\":%s,\"AUTOLOAD_DELAY\":%d,\"KILL_DISC_PLAYER_ON_STARTUP\":%s,"
                 "\"SCAN_USB_PAYLOADS\":%s,\"AUTO_INSTALL_APP\":%s,"
                 "\"MULTI_SOURCES_ENABLED\":%s,\"FAVORITES\":\"%s\",\"FAVORITES_INITIALIZED\":%s}",
                 cfg.autoload_enabled ? "true" : "false", list_escaped, cfg.last_repository_update,
                 cfg.auto_browser_open ? "true" : "false", cfg.autoload_delay,
                 cfg.kill_disc_player ? "true" : "false",
                 cfg.scan_usb_payloads ? "true" : "false",
                 cfg.auto_install_app ? "true" : "false",
                 cfg.multi_sources_enabled ? "true" : "false",
                 fav_escaped, fav_exists ? "true" : "false");
        if (!resp_buf[0]) return send_serialization_error(conn, resp_buf);
        resp = create_buffer_response(strlen(resp_buf),
                                               (void *)resp_buf,
                                               MHD_RESPMEM_MUST_FREE);
        add_response_header(resp, "Content-Type", "application/json");
    } else if (strcmp(url, "/events") == 0) {
        struct LogSSEConnection *sse = malloc(sizeof(struct LogSSEConnection));
        if (!sse)
            return MHD_NO;
        log_stream_init(sse, MHD_lookup_connection_value(conn, MHD_HEADER_KIND, "Last-Event-ID"));

        resp = MHD_create_response_from_callback(
            MHD_SIZE_UNKNOWN, 1024, &log_stream_callback, sse, &log_stream_cleanup);
        if (!resp) {
            log_stream_cleanup(sse);
            return MHD_NO;
        }
        add_response_header(resp, "Content-Type", "text/event-stream");
        add_response_header(resp, "Cache-Control", "no-cache");
        add_response_header(resp, "Connection", "keep-alive");
    } else {
        /* Default: 404 */
        const char *not_found = "404 Not Found\n";
        resp = create_buffer_response(strlen(not_found), (void *)not_found,
                                               MHD_RESPMEM_PERSISTENT);
        add_response_header(resp, "Content-Type", "text/plain");
    }

    if (!resp)
        return MHD_NO;

    return send_response(conn, http_status, resp);
}
