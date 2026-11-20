/*
 * SPDX-License-Identifier: BSD-2-Clause
 *
 * Copyright (c) 2026 amigazen project
 *
 * lh_lh5enc.c - LH4/LH5/LH6/LH7 compression (static Huffman + sliding dict).
 *
 * Emits the classic LHa "st1" block format consumed by lh_lh5dec.c:
 *   16-bit symbol count, PT-len (NT), C-len (NC), position PT-len (np),
 *   then Huffman-coded literals/matches.
 *
 * Algorithm follows the well-known LHa for UNIX encode_start_st1 / send_block
 * / make_tree design (Okumura / Yoshizaki / Watazaki lineage).  This file is
 * an original C89 rewrite for LhaSA (heap state, no FILE*, no globals).
 */

#include "lh_internal.h"

#include <limits.h>
#include <stddef.h>
#include <string.h>

#define LH5E_MAXMATCH    256
#define LH5E_THRESHOLD   3
#define LH5E_NC          (UCHAR_MAX + LH5E_MAXMATCH + 2 - LH5E_THRESHOLD)
#define LH5E_CBIT        9
#define LH5E_NT          (16 + 3)
#define LH5E_TBIT        5
#define LH5E_NPT         0x80
#define LH5E_HSHSIZ      4096
#define LH5E_LIMIT       256
#define LH5E_USHRT_BIT   16

struct lh5_enc {
    unsigned char *out;
    unsigned long out_cap;
    unsigned long out_pos;
    unsigned char subbitbuf;
    unsigned char bitcount;
    int unpackable;

    unsigned short left[2 * LH5E_NC - 1];
    unsigned short right[2 * LH5E_NC - 1];
    unsigned short c_freq[2 * LH5E_NC - 1];
    unsigned short p_freq[2 * LH5E_NPT - 1];
    unsigned short t_freq[2 * LH5E_NT - 1];
    unsigned short c_code[LH5E_NC];
    unsigned short pt_code[LH5E_NPT];
    unsigned char c_len[LH5E_NC];
    unsigned char pt_len[LH5E_NPT];

    unsigned char *buf;
    unsigned int bufsiz;
    unsigned short output_pos;
    unsigned short output_mask;
    unsigned short cpos;

    short np;
    short pbit;
    unsigned int dicbit;
    unsigned long dicsiz;
    unsigned long txtsiz;
    unsigned long maxmatch;

    unsigned char *text;
    unsigned long *hash;
    unsigned long *prev;
    unsigned char *too_flag;

    unsigned long pos;
    unsigned long remainder;
    unsigned long matchlen;
    unsigned long matchpos;
    unsigned long hval;
    unsigned long in_pos;
    const unsigned char *in;
    unsigned long in_size;

    unsigned short len_cnt[17];
    short heap[LH5E_NC + 1];
    size_t heapsize;
    int n_tree;
    unsigned short *freq_ptr;
    unsigned char *len_ptr;
    unsigned short *sort_ptr;
    int depth;
};

static void
lh5e_init_putbits(struct lh5_enc *e)
{
    e->bitcount = CHAR_BIT;
    e->subbitbuf = 0;
}

static void
lh5e_putcode(struct lh5_enc *e, unsigned char n, unsigned short x)
{
    while (n >= e->bitcount) {
        n = (unsigned char)(n - e->bitcount);
        e->subbitbuf = (unsigned char)(e->subbitbuf
            + (x >> (LH5E_USHRT_BIT - e->bitcount)));
        x = (unsigned short)(x << e->bitcount);
        if (e->out_pos < e->out_cap) {
            e->out[e->out_pos++] = e->subbitbuf;
        } else {
            e->unpackable = 1;
        }
        e->subbitbuf = 0;
        e->bitcount = CHAR_BIT;
    }
    e->subbitbuf = (unsigned char)(e->subbitbuf
        + (x >> (LH5E_USHRT_BIT - e->bitcount)));
    e->bitcount = (unsigned char)(e->bitcount - n);
}

static void
lh5e_putbits(struct lh5_enc *e, unsigned char n, unsigned short x)
{
    x = (unsigned short)(x << (LH5E_USHRT_BIT - n));
    lh5e_putcode(e, n, x);
}

static void
lh5e_make_code(struct lh5_enc *e, int n, unsigned char *len, unsigned short *code)
{
    unsigned short weight[17];
    unsigned short start[17];
    unsigned short j;
    unsigned short k;
    int i;

    j = 0;
    k = (unsigned short)(1 << (16 - 1));
    for (i = 1; i <= 16; i++) {
        start[i] = j;
        weight[i] = k;
        j = (unsigned short)(j + k * e->len_cnt[i]);
        k = (unsigned short)(k >> 1);
    }
    for (i = 0; i < n; i++) {
        j = len[i];
        code[i] = start[j];
        start[j] = (unsigned short)(start[j] + weight[j]);
    }
}

static void
lh5e_count_len(struct lh5_enc *e, int i)
{
    if (i < e->n_tree) {
        e->len_cnt[e->depth < 16 ? e->depth : 16]++;
    } else {
        e->depth++;
        lh5e_count_len(e, e->left[i]);
        lh5e_count_len(e, e->right[i]);
        e->depth--;
    }
}

static void
lh5e_make_len(struct lh5_enc *e, int root)
{
    int i;
    int k;
    unsigned long cum;

    for (i = 0; i <= 16; i++) {
        e->len_cnt[i] = 0;
    }
    e->depth = 0;
    lh5e_count_len(e, root);
    cum = 0;
    for (i = 16; i > 0; i--) {
        cum += ((unsigned long)e->len_cnt[i]) << (16 - i);
    }
    cum &= 0xffffUL;
    if (cum) {
        e->len_cnt[16] = (unsigned short)(e->len_cnt[16] - cum);
        do {
            for (i = 15; i > 0; i--) {
                if (e->len_cnt[i]) {
                    e->len_cnt[i]--;
                    e->len_cnt[i + 1] =
                        (unsigned short)(e->len_cnt[i + 1] + 2);
                    break;
                }
            }
        } while (--cum);
    }
    for (i = 16; i > 0; i--) {
        k = e->len_cnt[i];
        while (k > 0) {
            e->len_ptr[*e->sort_ptr++] = (unsigned char)i;
            k--;
        }
    }
}

static void
lh5e_downheap(struct lh5_enc *e, int i)
{
    short j;
    short k;

    k = e->heap[i];
    while ((j = (short)(2 * i)) <= (short)e->heapsize) {
        if (j < (short)e->heapsize
                && e->freq_ptr[e->heap[j]] > e->freq_ptr[e->heap[j + 1]]) {
            j++;
        }
        if (e->freq_ptr[k] <= e->freq_ptr[e->heap[j]]) {
            break;
        }
        e->heap[i] = e->heap[j];
        i = j;
    }
    e->heap[i] = k;
}

static short
lh5e_make_tree(
    struct lh5_enc *e,
    int nparm,
    unsigned short *freqparm,
    unsigned char *lenparm,
    unsigned short *codeparm)
{
    short i;
    short j;
    short k;
    short avail;

    e->n_tree = nparm;
    e->freq_ptr = freqparm;
    e->len_ptr = lenparm;
    avail = (short)nparm;
    e->heapsize = 0;
    e->heap[1] = 0;
    for (i = 0; i < nparm; i++) {
        lenparm[i] = 0;
        if (freqparm[i]) {
            e->heap[++e->heapsize] = i;
        }
    }
    if (e->heapsize < 2) {
        codeparm[e->heap[1]] = 0;
        return e->heap[1];
    }
    for (i = (short)(e->heapsize / 2); i >= 1; i--) {
        lh5e_downheap(e, i);
    }
    e->sort_ptr = codeparm;
    do {
        i = e->heap[1];
        if (i < nparm) {
            *e->sort_ptr++ = (unsigned short)i;
        }
        e->heap[1] = e->heap[e->heapsize--];
        lh5e_downheap(e, 1);
        j = e->heap[1];
        if (j < nparm) {
            *e->sort_ptr++ = (unsigned short)j;
        }
        k = avail++;
        freqparm[k] = (unsigned short)(freqparm[i] + freqparm[j]);
        e->heap[1] = k;
        lh5e_downheap(e, 1);
        e->left[k] = (unsigned short)i;
        e->right[k] = (unsigned short)j;
    } while (e->heapsize > 1);
    e->sort_ptr = codeparm;
    lh5e_make_len(e, k);
    lh5e_make_code(e, nparm, lenparm, codeparm);
    return k;
}

static void
lh5e_encode_c(struct lh5_enc *e, short c)
{
    lh5e_putcode(e, e->c_len[c], e->c_code[c]);
}

static void
lh5e_encode_p(struct lh5_enc *e, unsigned short p)
{
    unsigned short c;
    unsigned short q;

    c = 0;
    q = p;
    while (q) {
        q = (unsigned short)(q >> 1);
        c++;
    }
    lh5e_putcode(e, e->pt_len[c], e->pt_code[c]);
    if (c > 1) {
        lh5e_putbits(e, (unsigned char)(c - 1), p);
    }
}

static void
lh5e_count_t_freq(struct lh5_enc *e)
{
    short i;
    short k;
    short n;
    short count;

    for (i = 0; i < LH5E_NT; i++) {
        e->t_freq[i] = 0;
    }
    n = LH5E_NC;
    while (n > 0 && e->c_len[n - 1] == 0) {
        n--;
    }
    i = 0;
    while (i < n) {
        k = e->c_len[i++];
        if (k == 0) {
            count = 1;
            while (i < n && e->c_len[i] == 0) {
                i++;
                count++;
            }
            if (count <= 2) {
                e->t_freq[0] = (unsigned short)(e->t_freq[0] + count);
            } else if (count <= 18) {
                e->t_freq[1]++;
            } else if (count == 19) {
                e->t_freq[0]++;
                e->t_freq[1]++;
            } else {
                e->t_freq[2]++;
            }
        } else {
            e->t_freq[k + 2]++;
        }
    }
}

static void
lh5e_write_pt_len(struct lh5_enc *e, short n, short nbit, short i_special)
{
    short i;
    short k;

    while (n > 0 && e->pt_len[n - 1] == 0) {
        n--;
    }
    lh5e_putbits(e, (unsigned char)nbit, (unsigned short)n);
    i = 0;
    while (i < n) {
        k = e->pt_len[i++];
        if (k <= 6) {
            lh5e_putbits(e, 3, (unsigned short)k);
        } else {
            lh5e_putbits(e, (unsigned char)(k - 3),
                (unsigned short)(USHRT_MAX << 1));
        }
        if (i == i_special) {
            while (i < 6 && e->pt_len[i] == 0) {
                i++;
            }
            lh5e_putbits(e, 2, (unsigned short)(i - 3));
        }
    }
}

static void
lh5e_write_c_len(struct lh5_enc *e)
{
    short i;
    short k;
    short n;
    short count;

    n = LH5E_NC;
    while (n > 0 && e->c_len[n - 1] == 0) {
        n--;
    }
    lh5e_putbits(e, LH5E_CBIT, (unsigned short)n);
    i = 0;
    while (i < n) {
        k = e->c_len[i++];
        if (k == 0) {
            count = 1;
            while (i < n && e->c_len[i] == 0) {
                i++;
                count++;
            }
            if (count <= 2) {
                for (k = 0; k < count; k++) {
                    lh5e_putcode(e, e->pt_len[0], e->pt_code[0]);
                }
            } else if (count <= 18) {
                lh5e_putcode(e, e->pt_len[1], e->pt_code[1]);
                lh5e_putbits(e, 4, (unsigned short)(count - 3));
            } else if (count == 19) {
                lh5e_putcode(e, e->pt_len[0], e->pt_code[0]);
                lh5e_putcode(e, e->pt_len[1], e->pt_code[1]);
                lh5e_putbits(e, 4, 15);
            } else {
                lh5e_putcode(e, e->pt_len[2], e->pt_code[2]);
                lh5e_putbits(e, LH5E_CBIT, (unsigned short)(count - 20));
            }
        } else {
            lh5e_putcode(e, e->pt_len[k + 2], e->pt_code[k + 2]);
        }
    }
}

static void
lh5e_send_block(struct lh5_enc *e)
{
    unsigned char flags;
    unsigned short i;
    unsigned short k;
    short root;
    unsigned short pos;
    unsigned short size;

    flags = 0;
    root = lh5e_make_tree(e, LH5E_NC, e->c_freq, e->c_len, e->c_code);
    size = e->c_freq[root];
    lh5e_putbits(e, 16, size);
    if (root >= LH5E_NC) {
        lh5e_count_t_freq(e);
        root = lh5e_make_tree(e, LH5E_NT, e->t_freq, e->pt_len, e->pt_code);
        if (root >= LH5E_NT) {
            lh5e_write_pt_len(e, LH5E_NT, LH5E_TBIT, 3);
        } else {
            lh5e_putbits(e, LH5E_TBIT, 0);
            lh5e_putbits(e, LH5E_TBIT, (unsigned short)root);
        }
        lh5e_write_c_len(e);
    } else {
        lh5e_putbits(e, LH5E_TBIT, 0);
        lh5e_putbits(e, LH5E_TBIT, 0);
        lh5e_putbits(e, LH5E_CBIT, 0);
        lh5e_putbits(e, LH5E_CBIT, (unsigned short)root);
    }
    root = lh5e_make_tree(e, e->np, e->p_freq, e->pt_len, e->pt_code);
    if (root >= e->np) {
        lh5e_write_pt_len(e, e->np, e->pbit, -1);
    } else {
        lh5e_putbits(e, (unsigned char)e->pbit, 0);
        lh5e_putbits(e, (unsigned char)e->pbit, (unsigned short)root);
    }
    pos = 0;
    for (i = 0; i < size; i++) {
        if (i % CHAR_BIT == 0) {
            flags = e->buf[pos++];
        } else {
            flags = (unsigned char)(flags << 1);
        }
        if (flags & (1 << (CHAR_BIT - 1))) {
            lh5e_encode_c(e, (short)(e->buf[pos++] + (1 << CHAR_BIT)));
            k = (unsigned short)(e->buf[pos++] << CHAR_BIT);
            k = (unsigned short)(k + e->buf[pos++]);
            lh5e_encode_p(e, k);
        } else {
            lh5e_encode_c(e, e->buf[pos++]);
        }
        if (e->unpackable) {
            return;
        }
    }
    for (i = 0; i < LH5E_NC; i++) {
        e->c_freq[i] = 0;
    }
    for (i = 0; i < (unsigned short)e->np; i++) {
        e->p_freq[i] = 0;
    }
}

static void
lh5e_output(struct lh5_enc *e, unsigned short c, unsigned short p)
{
    e->output_mask = (unsigned short)(e->output_mask >> 1);
    if (e->output_mask == 0) {
        e->output_mask = (unsigned short)(1 << (CHAR_BIT - 1));
        if (e->output_pos >= e->bufsiz - 3 * CHAR_BIT) {
            lh5e_send_block(e);
            if (e->unpackable) {
                return;
            }
            e->output_pos = 0;
        }
        e->cpos = e->output_pos++;
        e->buf[e->cpos] = 0;
    }
    e->buf[e->output_pos++] = (unsigned char)c;
    e->c_freq[c]++;
    if (c >= (1 << CHAR_BIT)) {
        e->buf[e->cpos] |= (unsigned char)e->output_mask;
        e->buf[e->output_pos++] = (unsigned char)(p >> CHAR_BIT);
        e->buf[e->output_pos++] = (unsigned char)p;
        c = 0;
        while (p) {
            p = (unsigned short)(p >> 1);
            c++;
        }
        e->p_freq[c]++;
    }
}

static unsigned long
lh5e_getbytes(struct lh5_enc *e, unsigned char *mem, unsigned long size)
{
    unsigned long take;

    if (e->in_pos >= e->in_size) {
        return 0;
    }
    take = e->in_size - e->in_pos;
    if (take > size) {
        take = size;
    }
    if (take) {
        memcpy(mem, e->in + e->in_pos, (size_t)take);
        e->in_pos += take;
    }
    return take;
}

static void
lh5e_init_slide(struct lh5_enc *e)
{
    unsigned long i;

    for (i = 0; i < LH5E_HSHSIZ; i++) {
        e->hash[i] = 0;
        e->too_flag[i] = 0;
    }
}

static void
lh5e_insert(struct lh5_enc *e)
{
    e->prev[e->pos & (e->dicsiz - 1)] = e->hash[e->hval];
    e->hash[e->hval] = e->pos;
}

static void
lh5e_update(struct lh5_enc *e)
{
    unsigned long i;
    unsigned long j;
    unsigned long n;
    unsigned long m;

    i = 0;
    j = e->dicsiz;
    m = e->txtsiz - e->dicsiz;
    while (m-- > 0) {
        e->text[i++] = e->text[j++];
    }
    n = lh5e_getbytes(e, &e->text[e->txtsiz - e->dicsiz], e->dicsiz);
    e->remainder += n;
    e->pos -= e->dicsiz;
    for (i = 0; i < LH5E_HSHSIZ; i++) {
        j = e->hash[i];
        e->hash[i] = (j > e->dicsiz) ? j - e->dicsiz : 0;
        e->too_flag[i] = 0;
    }
    for (i = 0; i < e->dicsiz; i++) {
        j = e->prev[i];
        e->prev[i] = (j > e->dicsiz) ? j - e->dicsiz : 0;
    }
}

static void
lh5e_get_next(struct lh5_enc *e)
{
    e->remainder--;
    if (++e->pos >= e->txtsiz - e->maxmatch) {
        lh5e_update(e);
    }
    e->hval = ((e->hval << 5) ^ e->text[e->pos + 2])
        & (unsigned long)(LH5E_HSHSIZ - 1);
}

static void
lh5e_match_insert(struct lh5_enc *e)
{
    unsigned long scan_pos;
    unsigned long scan_end;
    unsigned long len;
    unsigned char *a;
    unsigned char *b;
    unsigned long chain;
    unsigned long off;
    unsigned long h;
    unsigned long max;

    max = e->maxmatch;
    if (e->matchlen < LH5E_THRESHOLD - 1) {
        e->matchlen = LH5E_THRESHOLD - 1;
    }
    e->matchpos = e->pos;

    off = 0;
    for (h = e->hval;
            e->too_flag[h] && off < e->maxmatch - LH5E_THRESHOLD;) {
        h = ((h << 5) ^ e->text[e->pos + (++off) + 2])
            & (unsigned long)(LH5E_HSHSIZ - 1);
    }
    if (off == e->maxmatch - LH5E_THRESHOLD) {
        off = 0;
    }
    for (;;) {
        chain = 0;
        scan_pos = e->hash[h];
        scan_end = (e->pos > e->dicsiz) ? e->pos + off - e->dicsiz : off;
        while (scan_pos > scan_end) {
            chain++;
            if (e->text[scan_pos + e->matchlen - off]
                    == e->text[e->pos + e->matchlen]) {
                a = e->text + scan_pos - off;
                b = e->text + e->pos;
                for (len = 0; len < max && *a++ == *b++; len++) {
                    ;
                }
                if (len > e->matchlen) {
                    e->matchpos = scan_pos - off;
                    e->matchlen = len;
                    if (len == max) {
                        break;
                    }
                }
            }
            scan_pos = e->prev[scan_pos & (e->dicsiz - 1)];
        }
        if (chain >= LH5E_LIMIT) {
            e->too_flag[h] = 1;
        }
        if (e->matchlen > off + 2 || off == 0) {
            break;
        }
        max = off + 2;
        off = 0;
        h = e->hval;
    }
    e->prev[e->pos & (e->dicsiz - 1)] = e->hash[e->hval];
    e->hash[e->hval] = e->pos;
}

static void
lh5e_encode_start(struct lh5_enc *e)
{
    int i;

    for (i = 0; i < LH5E_NC; i++) {
        e->c_freq[i] = 0;
    }
    for (i = 0; i < e->np; i++) {
        e->p_freq[i] = 0;
    }
    e->output_pos = 0;
    e->output_mask = 0;
    lh5e_init_putbits(e);
    e->buf[0] = 0;
}

static void
lh5e_encode_end(struct lh5_enc *e)
{
    if (!e->unpackable) {
        lh5e_send_block(e);
        lh5e_putbits(e, (unsigned char)(CHAR_BIT - 1), 0);
    }
}

static void
lh5e_encode(struct lh5_enc *e)
{
    unsigned long lastmatchlen;
    unsigned long lastmatchoffset;
    unsigned long i;

    e->out_pos = 0;
    e->unpackable = 0;

    lh5e_init_slide(e);
    lh5e_encode_start(e);

    for (i = 0; i < e->txtsiz; i++) {
        e->text[i] = ' ';
    }

    e->remainder = lh5e_getbytes(e, &e->text[e->dicsiz],
        e->txtsiz - e->dicsiz);
    e->matchlen = LH5E_THRESHOLD - 1;
    e->pos = e->dicsiz;

    if (e->matchlen > e->remainder) {
        e->matchlen = e->remainder;
    }
    e->hval = ((((e->text[e->dicsiz] << 5) ^ e->text[e->dicsiz + 1]) << 5)
        ^ e->text[e->dicsiz + 2]) & (unsigned long)(LH5E_HSHSIZ - 1);

    lh5e_insert(e);
    while (e->remainder > 0 && !e->unpackable) {
        lastmatchlen = e->matchlen;
        lastmatchoffset = e->pos - e->matchpos - 1;
        --e->matchlen;
        lh5e_get_next(e);
        lh5e_match_insert(e);
        if (e->matchlen > e->remainder) {
            e->matchlen = e->remainder;
        }
        if (e->matchlen > lastmatchlen || lastmatchlen < LH5E_THRESHOLD) {
            lh5e_output(e, e->text[e->pos - 1], 0);
        } else {
            lh5e_output(e,
                (unsigned short)(lastmatchlen
                    + (UCHAR_MAX + 1 - LH5E_THRESHOLD)),
                (unsigned short)(lastmatchoffset & (e->dicsiz - 1)));
            --lastmatchlen;
            while (--lastmatchlen > 0) {
                lh5e_get_next(e);
                lh5e_insert(e);
            }
            lh5e_get_next(e);
            e->matchlen = LH5E_THRESHOLD - 1;
            lh5e_match_insert(e);
            if (e->matchlen > e->remainder) {
                e->matchlen = e->remainder;
            }
        }
    }
    lh5e_encode_end(e);
}

static long
lh_compress_lh_new(
    void *inBuf,
    unsigned long inSize,
    void *outBuf,
    unsigned long *outSize,
    unsigned int dicbit,
    unsigned int pbit,
    unsigned int np)
{
    struct lh5_enc *e;
    unsigned long bufsiz;
    long rc;

    if (!inBuf || !outBuf || !outSize || inSize == 0) {
        return LH_ERR_INVALID_ARG;
    }
    if (dicbit < 12 || dicbit > 16 || pbit < 4 || pbit > 5 || np == 0
            || np > LH5E_NPT) {
        return LH_ERR_INVALID_ARG;
    }
    if (*outSize < inSize) {
        return LH_ERR_INVALID_ARG;
    }

    e = (struct lh5_enc *)calloc(1, sizeof(*e));
    if (!e) {
        return LH_ERR_NO_MEMORY;
    }

    e->out = (unsigned char *)outBuf;
    e->out_cap = *outSize;
    e->in = (const unsigned char *)inBuf;
    e->in_size = inSize;
    e->in_pos = 0;
    e->dicbit = dicbit;
    e->pbit = (short)pbit;
    e->np = (short)np;
    e->dicsiz = 1UL << dicbit;
    e->maxmatch = LH5E_MAXMATCH;
    e->txtsiz = e->dicsiz * 2 + e->maxmatch;

    bufsiz = 16UL * 1024UL * 2UL;
    e->buf = NULL;
    while (bufsiz >= 4UL * 1024UL) {
        e->buf = (unsigned char *)malloc(bufsiz);
        if (e->buf) {
            break;
        }
        bufsiz = (bufsiz / 10) * 9;
    }
    if (!e->buf) {
        free(e);
        return LH_ERR_NO_MEMORY;
    }
    e->bufsiz = (unsigned int)bufsiz;

    e->hash = (unsigned long *)calloc(LH5E_HSHSIZ, sizeof(unsigned long));
    e->prev = (unsigned long *)calloc(e->dicsiz, sizeof(unsigned long));
    e->text = (unsigned char *)malloc(e->txtsiz);
    e->too_flag = (unsigned char *)calloc(LH5E_HSHSIZ, 1);
    if (!e->hash || !e->prev || !e->text || !e->too_flag) {
        free(e->buf);
        free(e->hash);
        free(e->prev);
        free(e->text);
        free(e->too_flag);
        free(e);
        return LH_ERR_NO_MEMORY;
    }

    lh5e_encode(e);

    if (e->unpackable || e->out_pos == 0 || e->out_pos >= inSize) {
        rc = LH_ERR_UNSUPPORTED;
        *outSize = 0;
    } else {
        *outSize = e->out_pos;
        rc = LH_OK;
    }

    free(e->buf);
    free(e->hash);
    free(e->prev);
    free(e->text);
    free(e->too_flag);
    free(e);
    return rc;
}

long
lh_compress_lh4(void *inBuf, unsigned long inSize, void *outBuf,
    unsigned long *outSize)
{
    return lh_compress_lh_new(inBuf, inSize, outBuf, outSize, 12, 4, 13);
}

long
lh_compress_lh5(void *inBuf, unsigned long inSize, void *outBuf,
    unsigned long *outSize)
{
    return lh_compress_lh_new(inBuf, inSize, outBuf, outSize, 13, 4, 14);
}

long
lh_compress_lh6(void *inBuf, unsigned long inSize, void *outBuf,
    unsigned long *outSize)
{
    return lh_compress_lh_new(inBuf, inSize, outBuf, outSize, 15, 5, 16);
}

long
lh_compress_lh7(void *inBuf, unsigned long inSize, void *outBuf,
    unsigned long *outSize)
{
    return lh_compress_lh_new(inBuf, inSize, outBuf, outSize, 16, 5, 17);
}
