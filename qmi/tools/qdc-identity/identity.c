/* SPDX-License-Identifier: GPL-3.0-only
 * VoWin read-only identity query for QDC507 3.18.44.
 * No vendor headers or implementation are copied. Uses the public QCCI ABI.
 * The only QMI request in this program is DMS Get Device Serial Numbers (0x25).
 */
typedef unsigned int u32;
typedef unsigned char u8;
extern void *dlopen(const char *, int);
extern void *dlsym(void *, const char *);
extern int printf(const char *, ...);
extern int puts(const char *);
extern unsigned int alarm(unsigned int);
typedef void *(*service_fn)(int, int, int);
typedef int (*init_fn)(void *, u32, void *, void *, void *, u32, void **);
typedef int (*send_fn)(void *, u32, void *, u32, void *, u32, u32 *, u32);
typedef int (*release_fn)(void *);

static int decimal15(const char *s) {
    unsigned i;
    for (i = 0; i < 15; ++i) if (s[i] < '0' || s[i] > '9') return 0;
    return s[15] == 0;
}

static int parse_ids(const u8 *data, u32 length, char imei[16]) {
    u32 offset = 0, result_seen = 0, imei_seen = 0;
    while (offset < length) {
        if (length - offset < 3) return 0;
        u32 type = data[offset], size = data[offset+1] | (data[offset+2] << 8);
        offset += 3;
        if (size > length - offset) return 0;
        if (type == 2) {
            if (result_seen++ || size != 4 || data[offset] || data[offset+1] || data[offset+2] || data[offset+3]) return 0;
        } else if (type == 0x11) {
            if (imei_seen++ || size != 15) return 0;
            for (u32 i = 0; i < 15; ++i) imei[i] = data[offset+i];
            imei[15] = 0;
            if (!decimal15(imei)) return 0;
        }
        offset += size;
    }
    return result_seen == 1 && imei_seen == 1;
}

int main(int argc, char **argv) {
    int expect = argc == 3 && argv[1][0] == '-' && argv[1][1] == '-' &&
        argv[1][2] == 'e' && argv[1][3] == 'x' && argv[1][4] == 'p' &&
        argv[1][5] == 'e' && argv[1][6] == 'c' && argv[1][7] == 't' && argv[1][8] == 0;
    if (!(argc == 1 || (expect && decimal15(argv[2])))) return 64;
    alarm(8); /* Bound the process even if the ADB client disconnects. */
    void *services = dlopen("libqmiservices.so.1", 2 | 256);
    void *cci = dlopen("libqmi_cci.so.1", 2 | 256);
    if (!services || !cci) { puts("identity:error:library"); return 65; }
    service_fn service = (service_fn)dlsym(services, "dms_get_service_object_internal_v01");
    init_fn init = (init_fn)dlsym(cci, "qmi_client_init_instance");
    send_fn send = (send_fn)dlsym(cci, "qmi_client_send_raw_msg_sync");
    release_fn release = (release_fn)dlsym(cci, "qmi_client_release");
    if (!service || !init || !send || !release) { puts("identity:error:abi"); return 66; }
    /* Verified from the installed service accessor: IDL major 1, minor 57, tool 6.
     * A different service ABI fails closed instead of guessing a structure layout. */
    void *object = service(1, 57, 6), *handle = 0;
    if (!object) { puts("identity:error:service-version"); return 67; }
    int error = init(object, 0xffff, 0, 0, 0, 2500, &handle);
    if (error || !handle) { printf("identity:error:init:%d\n", error); return 68; }
    u8 response[512], empty_request = 0;
    u32 received = 0;
    error = send(handle, 0x25, &empty_request, 0, response, sizeof response, &received, 2500);
    int released = release(handle);
    if (error || released || received > sizeof response) {
        printf("identity:error:query:%d:%d\n", error, released); return 69;
    }
    char imei[16];
    if (!parse_ids(response, received, imei)) { puts("identity:error:response"); return 70; }
    if (expect) {
        for (u32 i = 0; i < 15; ++i) if (imei[i] != argv[2][i]) return 71;
        return 0;
    }
    printf("VOWIN_IMEI=%s\n", imei);
    return 0;
}
