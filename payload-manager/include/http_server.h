#ifndef HTTP_SERVER_H
#define HTTP_SERVER_H

#include <microhttpd.h>

/* Bound accumulated JSON and streamed payloads independently. */
#define HTTP_POST_BODY_LIMIT ((size_t)1024 * 1024)
#define HTTP_UPLOAD_BODY_LIMIT ((size_t)512 * 1024 * 1024)

/* MHD request handler callback — dispatches all routes. */
enum MHD_Result http_on_request(void *cls, struct MHD_Connection *conn,
                                const char *url, const char *method,
                                const char *version, const char *upload_data,
                                size_t *upload_data_size, void **con_cls);

/* Registered with MHD_OPTION_NOTIFY_COMPLETED for every daemon instance. */
void http_request_completed(void *cls, struct MHD_Connection *conn,
                            void **con_cls, enum MHD_RequestTerminationCode reason);

#endif
