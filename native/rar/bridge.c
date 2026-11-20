/* MirrorFlux archive adapter, GPL-3.0-or-later. Original library notices retained. */
#include <stdint.h>
#include <stdlib.h>
#include <string.h>
#include <stdio.h>
#include <wchar.h>
#include "archive.h"
#include "archive_entry.h"
#include "unarr.h"
#include "rar/rar.h"
#include "rar15.h"
#define API __declspec(dllexport)
typedef struct mf_rar {
    struct archive *archive;
    struct archive_entry *entry;
    ar_stream *stream;
    ar_archive *unarr;
    int64_t remaining;
    int directory;
    rar15 *legacy;
    int legacy_active;
    char error[512];
} mf_rar;
API void mf_rar_close(mf_rar *r) {
    if (!r) return;
    rar15_destroy(r->legacy);
    if (r->archive) archive_read_free(r->archive);
    if (r->unarr) ar_close_archive(r->unarr);
    if (r->stream) ar_close(r->stream);
    free(r);
}
API mf_rar *mf_rar_open(const wchar_t **paths, int rar5) {
    mf_rar *r = calloc(1, sizeof(*r));
    if (!r || !paths || !paths[0]) { free(r); return NULL; }
    if (rar5) {
        r->archive = archive_read_new();
        if (!r->archive) { free(r); return NULL; }
        archive_read_support_filter_none(r->archive);
        archive_read_support_format_rar5(r->archive);
        if (archive_read_open_filenames_w(r->archive, paths, 65536) < ARCHIVE_WARN) {
            snprintf(r->error, sizeof(r->error), "%s", archive_error_string(r->archive));
        }
    } else {
        r->stream = ar_open_file_w(paths[0]);
        if (r->stream) r->unarr = ar_open_rar_archive(r->stream);
        if (!r->unarr) snprintf(r->error,sizeof(r->error),"RAR cannot be opened (invalid or unsupported archive)");
    }
    return r;
}
API const char *mf_rar_error(mf_rar *r) { return r ? r->error : "Out of memory"; }
API int mf_rar_next(mf_rar *r) {
    if (!r || r->error[0]) return -1;
    if(r->legacy_active) {
        unsigned char discard[4096]; int count;
        while((count=rar15_read(r->legacy,discard,sizeof(discard)))>0) { }
        if(count<0){snprintf(r->error,sizeof(r->error),"RAR1.5 decompression or CRC failed");return -1;}
        r->legacy_active=0;
    }
    if (r->archive) {
        int result=archive_read_next_header(r->archive,&r->entry);
        if (result==ARCHIVE_EOF) return 0;
        if (result<ARCHIVE_WARN) { snprintf(r->error,sizeof(r->error),"%s",archive_error_string(r->archive));return -1; }
        if (archive_entry_is_encrypted(r->entry)) { snprintf(r->error,sizeof(r->error),"Encrypted RAR is not supported");return -1; }
        r->remaining=archive_entry_size(r->entry);
        r->directory=archive_entry_filetype(r->entry)==AE_IFDIR;
    } else {
        if (!ar_parse_entry(r->unarr)) {
            if (ar_at_eof(r->unarr)) return 0;
            snprintf(r->error,sizeof(r->error),"Invalid, truncated or encrypted RAR header");return -1;
        }
        if (((ar_archive_rar *)r->unarr)->entry.is_encrypted) { snprintf(r->error,sizeof(r->error),"Encrypted RAR is not supported");return -1; }
        r->remaining=(int64_t)ar_entry_get_size(r->unarr);
        r->directory=((ar_archive_rar *)r->unarr)->entry.is_directory;
        ar_archive_rar *u=(ar_archive_rar *)r->unarr;
        if(!r->directory && u->entry.version==15 && u->entry.method!=METHOD_STORE) {
            if(!r->legacy)r->legacy=rar15_create();
            if(!r->legacy){snprintf(r->error,sizeof(r->error),"Out of memory");return -1;}
            rar15_begin(r->legacy,r->stream,u->progress.data_left,r->remaining,u->entry.crc,u->entry.solid);
            r->legacy_active=1;
        }
    }
    return 1;
}
API const char *mf_rar_name(mf_rar *r) { return r->archive ? archive_entry_pathname_utf8(r->entry) : ar_entry_get_name(r->unarr); }
API int64_t mf_rar_size(mf_rar *r) { return r->remaining; }
API int64_t mf_rar_time(mf_rar *r) { return r->archive ? archive_entry_mtime(r->entry) : ar_entry_get_filetime(r->unarr)/10000000-11644473600LL; }
API int mf_rar_directory(mf_rar *r) { return r->directory; }
API const char *mf_rar_link(mf_rar *r) { return r->archive ? archive_entry_symlink_utf8(r->entry) : NULL; }
API int mf_rar_read(mf_rar *r, void *buffer, int count) {
    if (!r || count<0 || r->error[0]) return -1;
    if (!count) return 0;
    if(r->legacy_active) {
        int n=rar15_read(r->legacy,buffer,count);
        if(n<0)snprintf(r->error,sizeof(r->error),"RAR1.5 decompression or CRC failed");
        return n;
    }
    if (r->archive) {
        la_ssize_t n=archive_read_data(r->archive,buffer,(size_t)count);
        if (n<0) { snprintf(r->error,sizeof(r->error),"%s",archive_error_string(r->archive));return -1; }
        return (int)n;
    }
    if (r->directory || !r->remaining) return 0;
    if (count>r->remaining) count=(int)r->remaining;
    if (!ar_entry_uncompress(r->unarr,buffer,(size_t)count)) { snprintf(r->error,sizeof(r->error),"RAR decompression or CRC check failed");return -1; }
    r->remaining-=count;
    return count;
}
