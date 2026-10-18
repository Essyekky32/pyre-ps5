#include <stdio.h>
#include <string.h>
#include <strings.h>
#include <sys/socket.h>
#include <netdb.h>
#include <ifaddrs.h>
#include <arpa/inet.h>
#include <netinet/in.h>
#include "utils.h"
#include "pldmgr.h"

int pldmgr_get_local_ip(char *ip_buf, size_t buf_size) {
    struct ifaddrs *ifaddr, *ifa;
    int family, s;

    if (getifaddrs(&ifaddr) == -1) {
        return -1;
    }

    for (ifa = ifaddr; ifa != NULL; ifa = ifa->ifa_next) {
        if (ifa->ifa_addr == NULL) continue;

        family = ifa->ifa_addr->sa_family;

        if (family == AF_INET) {
            /* Skip loopback */
            if (strncmp(ifa->ifa_name, "lo", 2) == 0) continue;

            s = getnameinfo(ifa->ifa_addr, sizeof(struct sockaddr_in),
                           ip_buf, buf_size, NULL, 0, NI_NUMERICHOST);
            if (s == 0) {
                /* Found a valid IP */
                freeifaddrs(ifaddr);
                return 0;
            }
        }
    }

    freeifaddrs(ifaddr);
    return -1;
}

void pldmgr_utils_get_payload_folder_name(const char *filename, char *out_buf, size_t out_size) {
    char clean[256];
    strncpy(clean, filename, sizeof(clean) - 1);
    clean[sizeof(clean) - 1] = '\0';

    /* Strip extension */
    char *dot = strrchr(clean, '.');
    if (dot) *dot = '\0';

    /* Look for version marker like _v1.2.3 or -v1.2.3 */
    char *v = strstr(clean, "_v");
    if (!v) v = strstr(clean, "-v");
    
    if (v) {
        *v = '\0';
    } else {
        /* Fallback: look for just _ or - followed by digit */
        for (int i = 0; clean[i]; i++) {
            if ((clean[i] == '_' || clean[i] == '-') && (clean[i+1] >= '0' && clean[i+1] <= '9')) {
                clean[i] = '\0';
                break;
            }
        }
    }

    /* Further clean: remove -ps4, -ps5 suffixes if they were before the version */
    char *p = strstr(clean, "-ps5");
    if (!p) p = strstr(clean, "_ps5");
    if (!p) p = strstr(clean, "-ps4");
    if (!p) p = strstr(clean, "_ps4");
    if (p) *p = '\0';

    strncpy(out_buf, clean, out_size - 1);
    out_buf[out_size - 1] = '\0';
}

/* Validate one Unicode scalar, rejecting overlong forms, surrogate code points,
 * incomplete sequences, and values beyond U+10FFFF. */
static size_t utf8_scalar_size(const unsigned char *s, size_t remaining) {
    size_t n;
    if (s[0] < 0x80) return 1;
    if (s[0] >= 0xc2 && s[0] <= 0xdf) n = 2;
    else if (s[0] >= 0xe0 && s[0] <= 0xef) n = 3;
    else if (s[0] >= 0xf0 && s[0] <= 0xf4) n = 4;
    else return 0;
    if (remaining < n) return 0;
    for (size_t i = 1; i < n; i++)
        if (s[i] < 0x80 || s[i] > 0xbf) return 0;
    if ((s[0] == 0xe0 && s[1] < 0xa0) ||
        (s[0] == 0xed && s[1] >= 0xa0) ||
        (s[0] == 0xf0 && s[1] < 0x90) ||
        (s[0] == 0xf4 && s[1] > 0x8f)) return 0;
    return n;
}

int pldmgr_json_escape(const char *src, char *dst, size_t dst_size) {
    size_t pos = 0;
    if (!dst || dst_size == 0) return -1;
    dst[0] = '\0';
    if (!src) return -1;
    size_t size = strlen(src);

    for (size_t i = 0; i < size;) {
        unsigned char c = (unsigned char)src[i];
        const char *part = src + i;
        size_t consumed = 1, length = 1;
        char escaped[7];
        if (c == '"' || c == '\\') {
            escaped[0] = '\\';
            escaped[1] = (char)c;
            part = escaped;
            length = 2;
        } else if (c < 0x20) {
            char short_escape = 0;
            switch (c) {
                case '\b': short_escape = 'b'; break;
                case '\f': short_escape = 'f'; break;
                case '\n': short_escape = 'n'; break;
                case '\r': short_escape = 'r'; break;
                case '\t': short_escape = 't'; break;
            }
            if (short_escape) {
                escaped[0] = '\\';
                escaped[1] = short_escape;
                length = 2;
            } else {
                static const char hex[] = "0123456789abcdef";
                memcpy(escaped, "\\u00", 4);
                escaped[4] = hex[c >> 4];
                escaped[5] = hex[c & 15];
                length = 6;
            }
            part = escaped;
        } else if (c >= 0x80) {
            length = utf8_scalar_size((const unsigned char *)src + i, size - i);
            if (length) consumed = length;
            else {
                part = "\\ufffd";
                length = 6;
            }
        }
        if (length >= dst_size - pos) return -1;
        memcpy(dst + pos, part, length);
        pos += length;
        dst[pos] = '\0';
        i += consumed;
    }
    return 0;
}

int is_pldmgr_name(const char *name) {
    if (!name || !*name) return 0;
    const char *base = strrchr(name, '/');
    base = base ? base + 1 : name;
    return (strncasecmp(base, "pldmgr", 6) == 0 ||
            strncasecmp(base, "payload-manager", 15) == 0 ||
            strncasecmp(base, "payload_manager", 15) == 0 ||
            strncasecmp(base, "payload manager", 15) == 0 ||
            strncasecmp(base, "payloadmanager", 14) == 0);
}

int is_pldmgr_official_source(const char *url) {
    if (!url) return 0;
    size_t n = strlen(url);
    while (n && url[n - 1] == '/') n--;
    return n == strlen(REPOSITORY_SOURCE_URL) && !strncmp(url, REPOSITORY_SOURCE_URL, n);
}

int is_legacy_official_repo_url(const char *url) {
    if (!url) return 0;
    size_t n = strlen(url);
    while (n && url[n - 1] == '/') n--;
    return n == strlen(LEGACY_REPOSITORY_SOURCE_URL) && !strncmp(url, LEGACY_REPOSITORY_SOURCE_URL, n);
}

