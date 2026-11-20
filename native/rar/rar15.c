/*
 * XADRAR15Handle.m
 *
 * Copyright (c) 2017-present, MacPaw Way Ltd. All rights reserved.
 *
 * This library is free software; you can redistribute it and/or
 * modify it under the terms of the GNU Lesser General Public
 * License as published by the Free Software Foundation; either
 * version 2.1 of the License, or (at your option) any later version.
 *
 * This library is distributed in the hope that it will be useful,
 * but WITHOUT ANY WARRANTY; without even the implied warranty of
 * MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE.  See the GNU
 * Lesser General Public License for more details.
 *
 * You should have received a copy of the GNU Lesser General Public
 * License along with this library; if not, write to the Free Software
 * Foundation, Inc., 51 Franklin Street, Fifth Floor, Boston,
 * MA 02110-1301  USA
 */
/* Portable C adaptation for MirrorFlux, 2026. Same LGPL-2.1-or-later terms.
   Bounded stream input, prefix tables and output window replace Objective-C runtime. */
#include "rar15.h"
#include <stdlib.h>
#include <string.h>
#include <setjmp.h>
#include <limits.h>
typedef struct { unsigned code[257]; unsigned char bits[257]; int count; } Code;
struct rar15 {
 ar_stream *input; uint64_t packed; unsigned bitbuffer, bitcount;
 int64_t generated, delivered, fileEnd; uint32_t crc, expected;
 unsigned char window[65536]; jmp_buf failure; int storedblock;
	unsigned int flags,flagbits;
	unsigned int literalweight,matchweight;
	unsigned int numrepeatedliterals,numrepeatedlastmatches;
	unsigned int runningaverageliteral,runningaverageselector;
	unsigned int runningaveragelength,runningaverageoffset,runningaveragebelowmaximum;
	unsigned int maximumoffset;
	int bugfixflag;

	int lastoffset,lastlength;
	int oldoffset[4];
	unsigned int oldoffsetindex;

	int flagtable[256],flagreverse[256];
	int literaltable[256],literalreverse[256];
	int offsettable[256],offsetreverse[256];
	int shortoffsettable[256];
 Code lengthcode1;
 Code lengthcode2;
 Code huffmancode0;
 Code huffmancode1;
 Code huffmancode2;
 Code huffmancode3;
 Code huffmancode4;
 Code shortmatchcode0;
 Code shortmatchcode1;
 Code shortmatchcode2;
 Code shortmatchcode3;
};
static void ResetTable(int *table,int *reverse);
static int LookupByte(rar15 *self,int *table,int *reverse,int limit,int index);
static int GetFlagBit(rar15 *self);
static void EmitLiteral(rar15 *self,int64_t *posptr);
static void EmitLongMatch(rar15 *self,int64_t *posptr);
static void EmitShortMatch(rar15 *self,int64_t *posptr);
static unsigned ReadBits(rar15 *self,int count) {
 unsigned value=0;
 while(count--) {
  if(!self->bitcount) {
   unsigned char b;
   if(!self->packed || ar_read(self->input,&b,1)!=1) longjmp(self->failure,1);
   self->packed--;self->bitbuffer=b;self->bitcount=8;
  }
  value=(value<<1)|((self->bitbuffer>>--self->bitcount)&1);
 }
 return value;
}
static int ReadCode(rar15 *self,const Code *code) {
 unsigned bits=0;
 for(int length=1;length<=12;length++) {
  bits=(bits<<1)|ReadBits(self,1);
  for(int i=0;i<code->count;i++) if(code->bits[i]==length && code->code[i]==bits) return i;
 }
 longjmp(self->failure,1);
}
static void InitCode(Code *code,const int *lengths,int count) {
 unsigned sizes[13]={0},next[13]={0},value=0;code->count=count;
 for(int i=0;i<count;i++) sizes[lengths[i]]++;
 for(int i=1;i<=12;i++){value=(value+sizes[i-1])<<1;next[i]=value;}
 for(int i=0;i<count;i++){code->bits[i]=(unsigned char)lengths[i];code->code[i]=next[lengths[i]]++;}
}
static void XADEmitLZSSLiteral(rar15 *self,int byte,int64_t *position) {
 if(*position>=self->fileEnd) longjmp(self->failure,1);
 self->window[(uint64_t)*position&65535]=(unsigned char)byte;(*position)++;
}
static void XADEmitLZSSMatch(rar15 *self,int offset,int length,int64_t *position) {
 if(offset<1||offset>65536||length<1||length>self->fileEnd-*position) longjmp(self->failure,1);
 for(int i=0;i<length;i++) { unsigned char b=self->window[((uint64_t)*position-offset)&65535];XADEmitLZSSLiteral(self,b,position); }
}
rar15 *rar15_create(void) {
 rar15 *self=calloc(1,sizeof(*self));if(!self)return NULL;
 { const int lengths[256]={2,2,3,4,4,5,5,6,6,6,6,7,7,7,7,7,8,8,8,8,9,9,9,9,10,10,10,10,10,10,10,10,12,12,12,12,12,12,12,12,12,12,12,12,12,12,12,12,12,12,12,12,12,12,12,12,12,12,12,12,12,12,12,12,12,12,12,12,12,12,12,12,12,12,12,12,12,12,12,12,12,12,12,12,12,12,12,12,12,12,12,12,12,12,12,12,12,12,12,12,12,12,12,12,12,12,12,12,12,12,12,12,12,12,12,12,12,12,12,12,12,12,12,12,12,12,12,12,12,12,12,12,12,12,12,12,12,12,12,12,12,12,12,12,12,12,12,12,12,12,12,12,12,12,12,12,12,12,12,12,12,12,12,12,12,12,12,12,12,12,12,12,12,12,12,12,12,12,12,12,12,12,12,12,12,12,12,12,12,12,12,12,12,12,12,12,12,12,12,12,12,12,12,12,12,12,12,12,12,12,12,12,12,12,12,12,12,12,12,12,12,12,12,12,12,12,12,12,12,12,12,12,12,12,12,12,12,12,12,12,12,12,12,12,12,12,12,12,12,12,12,12,12,12,12,12};InitCode(&self->lengthcode1,lengths,256); }
 { const int lengths[256]={3,3,3,3,3,4,4,5,5,6,6,6,6,7,7,7,7,7,8,8,8,8,9,9,9,9,10,10,10,10,10,10,10,10,11,11,12,12,12,12,12,12,12,12,12,12,12,12,12,12,12,12,12,12,12,12,12,12,12,12,12,12,12,12,12,12,12,12,12,12,12,12,12,12,12,12,12,12,12,12,12,12,12,12,12,12,12,12,12,12,12,12,12,12,12,12,12,12,12,12,12,12,12,12,12,12,12,12,12,12,12,12,12,12,12,12,12,12,12,12,12,12,12,12,12,12,12,12,12,12,12,12,12,12,12,12,12,12,12,12,12,12,12,12,12,12,12,12,12,12,12,12,12,12,12,12,12,12,12,12,12,12,12,12,12,12,12,12,12,12,12,12,12,12,12,12,12,12,12,12,12,12,12,12,12,12,12,12,12,12,12,12,12,12,12,12,12,12,12,12,12,12,12,12,12,12,12,12,12,12,12,12,12,12,12,12,12,12,12,12,12,12,12,12,12,12,12,12,12,12,12,12,12,12,12,12,12,12,12,12,12,12,12,12,12,12,12,12,12,12,12,12,12,12,12,12};InitCode(&self->lengthcode2,lengths,256); }
 { const int lengths[257]={4,4,4,4,4,4,4,4,5,5,5,5,5,5,5,5,6,6,6,6,6,6,6,6,7,7,7,7,7,7,7,7,7,12,12,12,12,12,12,12,12,12,12,12,12,12,12,12,12,12,12,12,12,12,12,12,12,12,12,12,12,12,12,12,12,12,12,12,12,12,12,12,12,12,12,12,12,12,12,12,12,12,12,12,12,12,12,12,12,12,12,12,12,12,12,12,12,12,12,12,12,12,12,12,12,12,12,12,12,12,12,12,12,12,12,12,12,12,12,12,12,12,12,12,12,12,12,12,12,12,12,12,12,12,12,12,12,12,12,12,12,12,12,12,12,12,12,12,12,12,12,12,12,12,12,12,12,12,12,12,12,12,12,12,12,12,12,12,12,12,12,12,12,12,12,12,12,12,12,12,12,12,12,12,12,12,12,12,12,12,12,12,12,12,12,12,12,12,12,12,12,12,12,12,12,12,12,12,12,12,12,12,12,12,12,12,12,12,12,12,12,12,12,12,12,12,12,12,12,12,12,12,12,12,12,12,12,12,12,12,12,12,12,12,12,12,12,12,12,12,12,12,12,12,12,12,12};InitCode(&self->huffmancode0,lengths,257); }
 { const int lengths[257]={5,5,5,5,6,6,6,6,6,6,6,6,6,6,6,6,6,6,6,6,6,6,6,6,6,6,6,6,6,6,6,6,6,6,6,6,6,6,6,6,6,6,6,6,7,7,7,7,7,7,7,7,7,7,7,7,7,7,7,7,8,8,8,8,8,8,8,8,8,8,8,8,8,8,8,8,9,9,9,9,11,11,11,11,11,11,11,11,11,11,11,11,11,11,11,11,11,11,11,11,11,11,11,11,11,11,11,11,11,11,11,11,11,11,11,11,11,11,11,11,11,11,11,11,11,11,11,12,12,12,12,12,12,12,12,12,12,12,12,12,12,12,12,12,12,12,12,12,12,12,12,12,12,12,12,12,12,12,12,12,12,12,12,12,12,12,12,12,12,12,12,12,12,12,12,12,12,12,12,12,12,12,12,12,12,12,12,12,12,12,12,12,12,12,12,12,12,12,12,12,12,12,12,12,12,12,12,12,12,12,12,12,12,12,12,12,12,12,12,12,12,12,12,12,12,12,12,12,12,12,12,12,12,12,12,12,12,12,12,12,12,12,12,12,12,12,12,12,12,12,12,12,12,12,12,12,12};InitCode(&self->huffmancode1,lengths,257); }
 { const int lengths[257]={5,5,6,6,6,6,6,7,7,7,7,7,7,7,7,7,7,7,7,7,7,7,7,7,7,7,7,7,7,7,7,7,7,7,7,7,7,7,7,7,7,7,7,7,7,7,7,7,7,7,7,7,7,8,8,8,8,8,8,8,8,8,8,8,8,8,8,8,8,8,8,8,8,8,8,8,8,8,8,8,8,8,8,8,8,8,8,8,8,8,8,8,8,8,8,8,8,8,8,8,8,8,8,8,8,8,8,8,8,8,8,8,8,8,8,8,8,9,9,9,9,9,9,9,9,9,9,9,9,9,9,9,9,9,9,9,9,9,9,9,9,9,9,9,9,9,9,9,9,9,9,9,9,9,9,9,9,9,9,9,9,9,9,9,9,9,9,9,9,9,9,9,9,9,9,9,9,9,9,9,9,9,9,9,9,9,9,9,9,9,9,9,9,9,9,9,9,9,9,9,9,9,9,9,9,9,9,9,9,9,9,9,9,9,9,9,9,9,9,9,9,9,9,9,9,9,9,9,9,9,9,9,9,10,10,10,10,10,10,10,10,10,10,10,10,10,10,10,10,10,10,10,10,10,10,10,10};InitCode(&self->huffmancode2,lengths,257); }
 { const int lengths[257]={6,6,7,7,7,7,7,7,7,7,7,7,7,7,7,7,8,8,8,8,8,8,8,8,8,8,8,8,8,8,8,8,8,8,8,8,8,8,8,8,8,8,8,8,8,8,8,8,8,8,8,8,8,8,8,8,8,8,8,8,8,8,8,8,8,8,8,8,8,8,8,8,8,8,8,8,8,8,8,8,8,8,8,8,8,8,8,8,8,8,8,8,8,8,8,8,8,8,8,8,8,8,8,8,8,8,8,8,8,8,8,8,8,8,8,8,8,8,8,8,8,8,8,8,8,8,8,8,8,8,8,8,8,8,8,8,8,8,8,8,8,8,8,8,8,8,8,8,8,8,8,8,8,8,8,8,8,8,8,8,8,8,8,8,8,8,8,8,8,8,8,8,8,8,8,8,8,8,8,8,8,8,8,8,8,8,8,8,8,8,8,8,8,8,8,8,8,8,8,8,8,8,8,8,8,8,8,8,8,8,8,8,8,8,8,8,8,8,9,9,9,9,9,9,9,9,9,9,9,9,9,9,9,9,9,9,9,9,9,9,9,9,9,9,9,9,9,9,9,9,9,10,10,10,10,10,10};InitCode(&self->huffmancode3,lengths,257); }
 { const int lengths[257]={8,8,8,8,8,8,8,8,8,8,8,8,8,8,8,8,8,8,8,8,8,8,8,8,8,8,8,8,8,8,8,8,8,8,8,8,8,8,8,8,8,8,8,8,8,8,8,8,8,8,8,8,8,8,8,8,8,8,8,8,8,8,8,8,8,8,8,8,8,8,8,8,8,8,8,8,8,8,8,8,8,8,8,8,8,8,8,8,8,8,8,8,8,8,8,8,8,8,8,8,8,8,8,8,8,8,8,8,8,8,8,8,8,8,8,8,8,8,8,8,8,8,8,8,8,8,8,8,8,8,8,8,8,8,8,8,8,8,8,8,8,8,8,8,8,8,8,8,8,8,8,8,8,8,8,8,8,8,8,8,8,8,8,8,8,8,8,8,8,8,8,8,8,8,8,8,8,8,8,8,8,8,8,8,8,8,8,8,8,8,8,8,8,8,8,8,8,8,8,8,8,8,8,8,8,8,8,8,8,8,8,8,8,8,8,8,8,8,8,8,8,8,8,8,8,8,8,8,8,8,8,8,8,8,8,8,8,8,8,8,8,8,8,8,8,8,8,8,8,8,8,8,8,8,8,9,9};InitCode(&self->huffmancode4,lengths,257); }
 self->shortmatchcode0.count=15;
 self->shortmatchcode0.bits[0]=1;self->shortmatchcode0.code[0]=(0x00&((1u<<1)-1));
 self->shortmatchcode0.bits[1]=4;self->shortmatchcode0.code[1]=(0x0a&((1u<<4)-1));
 self->shortmatchcode0.bits[2]=4;self->shortmatchcode0.code[2]=(0x0d&((1u<<4)-1));
 self->shortmatchcode0.bits[3]=4;self->shortmatchcode0.code[3]=(0x0e&((1u<<4)-1));
 self->shortmatchcode0.bits[4]=5;self->shortmatchcode0.code[4]=(0x1e&((1u<<5)-1));
 self->shortmatchcode0.bits[5]=6;self->shortmatchcode0.code[5]=(0x3e&((1u<<6)-1));
 self->shortmatchcode0.bits[6]=7;self->shortmatchcode0.code[6]=(0x7e&((1u<<7)-1));
 self->shortmatchcode0.bits[7]=8;self->shortmatchcode0.code[7]=(0xfe&((1u<<8)-1));
 self->shortmatchcode0.bits[8]=8;self->shortmatchcode0.code[8]=(0xff&((1u<<8)-1));
 self->shortmatchcode0.bits[9]=4;self->shortmatchcode0.code[9]=(0x0c&((1u<<4)-1));
 self->shortmatchcode0.bits[10]=4;self->shortmatchcode0.code[10]=(0x08&((1u<<4)-1));
 self->shortmatchcode0.bits[11]=5;self->shortmatchcode0.code[11]=(0x12&((1u<<5)-1));
 self->shortmatchcode0.bits[12]=6;self->shortmatchcode0.code[12]=(0x26&((1u<<6)-1));
 self->shortmatchcode0.bits[13]=6;self->shortmatchcode0.code[13]=(0x27&((1u<<6)-1));
 self->shortmatchcode0.bits[14]=4;self->shortmatchcode0.code[14]=(0x0b&((1u<<4)-1));
 self->shortmatchcode1.count=14;
 self->shortmatchcode1.bits[0]=1;self->shortmatchcode1.code[0]=(0x00&((1u<<1)-1));
 self->shortmatchcode1.bits[1]=3;self->shortmatchcode1.code[1]=(0x05&((1u<<3)-1));
 self->shortmatchcode1.bits[2]=4;self->shortmatchcode1.code[2]=(0x0d&((1u<<4)-1));
 self->shortmatchcode1.bits[3]=4;self->shortmatchcode1.code[3]=(0x0e&((1u<<4)-1));
 self->shortmatchcode1.bits[4]=5;self->shortmatchcode1.code[4]=(0x1e&((1u<<5)-1));
 self->shortmatchcode1.bits[5]=6;self->shortmatchcode1.code[5]=(0x3e&((1u<<6)-1));
 self->shortmatchcode1.bits[6]=7;self->shortmatchcode1.code[6]=(0x7e&((1u<<7)-1));
 self->shortmatchcode1.bits[7]=8;self->shortmatchcode1.code[7]=(0xfe&((1u<<8)-1));
 self->shortmatchcode1.bits[8]=8;self->shortmatchcode1.code[8]=(0xff&((1u<<8)-1));
 self->shortmatchcode1.bits[9]=4;self->shortmatchcode1.code[9]=(0x0c&((1u<<4)-1));
 self->shortmatchcode1.bits[10]=4;self->shortmatchcode1.code[10]=(0x08&((1u<<4)-1));
 self->shortmatchcode1.bits[11]=5;self->shortmatchcode1.code[11]=(0x12&((1u<<5)-1));
 self->shortmatchcode1.bits[12]=6;self->shortmatchcode1.code[12]=(0x26&((1u<<6)-1));
 self->shortmatchcode1.bits[13]=6;self->shortmatchcode1.code[13]=(0x27&((1u<<6)-1));
 self->shortmatchcode2.count=15;
 self->shortmatchcode2.bits[0]=2;self->shortmatchcode2.code[0]=(0x00&((1u<<2)-1));
 self->shortmatchcode2.bits[1]=3;self->shortmatchcode2.code[1]=(0x02&((1u<<3)-1));
 self->shortmatchcode2.bits[2]=3;self->shortmatchcode2.code[2]=(0x03&((1u<<3)-1));
 self->shortmatchcode2.bits[3]=4;self->shortmatchcode2.code[3]=(0x0a&((1u<<4)-1));
 self->shortmatchcode2.bits[4]=4;self->shortmatchcode2.code[4]=(0x0d&((1u<<4)-1));
 self->shortmatchcode2.bits[5]=4;self->shortmatchcode2.code[5]=(0x3e&((1u<<4)-1));
 self->shortmatchcode2.bits[6]=5;self->shortmatchcode2.code[6]=(0x1e&((1u<<5)-1));
 self->shortmatchcode2.bits[7]=6;self->shortmatchcode2.code[7]=(0x3e&((1u<<6)-1));
 self->shortmatchcode2.bits[8]=6;self->shortmatchcode2.code[8]=(0x3f&((1u<<6)-1));
 self->shortmatchcode2.bits[9]=4;self->shortmatchcode2.code[9]=(0x0c&((1u<<4)-1));
 self->shortmatchcode2.bits[10]=4;self->shortmatchcode2.code[10]=(0x08&((1u<<4)-1));
 self->shortmatchcode2.bits[11]=5;self->shortmatchcode2.code[11]=(0x12&((1u<<5)-1));
 self->shortmatchcode2.bits[12]=6;self->shortmatchcode2.code[12]=(0x26&((1u<<6)-1));
 self->shortmatchcode2.bits[13]=6;self->shortmatchcode2.code[13]=(0x27&((1u<<6)-1));
 self->shortmatchcode2.bits[14]=4;self->shortmatchcode2.code[14]=(0x0b&((1u<<4)-1));
 self->shortmatchcode3.count=14;
 self->shortmatchcode3.bits[0]=2;self->shortmatchcode3.code[0]=(0x00&((1u<<2)-1));
 self->shortmatchcode3.bits[1]=3;self->shortmatchcode3.code[1]=(0x02&((1u<<3)-1));
 self->shortmatchcode3.bits[2]=3;self->shortmatchcode3.code[2]=(0x03&((1u<<3)-1));
 self->shortmatchcode3.bits[3]=3;self->shortmatchcode3.code[3]=(0x05&((1u<<3)-1));
 self->shortmatchcode3.bits[4]=4;self->shortmatchcode3.code[4]=(0x0d&((1u<<4)-1));
 self->shortmatchcode3.bits[5]=4;self->shortmatchcode3.code[5]=(0x3e&((1u<<4)-1));
 self->shortmatchcode3.bits[6]=5;self->shortmatchcode3.code[6]=(0x1e&((1u<<5)-1));
 self->shortmatchcode3.bits[7]=6;self->shortmatchcode3.code[7]=(0x3e&((1u<<6)-1));
 self->shortmatchcode3.bits[8]=6;self->shortmatchcode3.code[8]=(0x3f&((1u<<6)-1));
 self->shortmatchcode3.bits[9]=4;self->shortmatchcode3.code[9]=(0x0c&((1u<<4)-1));
 self->shortmatchcode3.bits[10]=4;self->shortmatchcode3.code[10]=(0x08&((1u<<4)-1));
 self->shortmatchcode3.bits[11]=5;self->shortmatchcode3.code[11]=(0x12&((1u<<5)-1));
 self->shortmatchcode3.bits[12]=6;self->shortmatchcode3.code[12]=(0x26&((1u<<6)-1));
 self->shortmatchcode3.bits[13]=6;self->shortmatchcode3.code[13]=(0x27&((1u<<6)-1));
 return self;
}
void rar15_destroy(rar15 *self){free(self);}
void rar15_begin(rar15 *self,ar_stream *input,uint64_t packed,int64_t size,uint32_t crc,int solid) {
 if(!solid) {
  memset(self->window,0,sizeof(self->window));self->generated=self->delivered=0;
  self->numrepeatedliterals=0;self->bugfixflag=0;
  self->runningaverageselector=0;self->runningaverageliteral=0x3500;
  self->runningaveragelength=self->runningaverageoffset=self->runningaveragebelowmaximum=0;
  self->maximumoffset=0x2001;self->literalweight=self->matchweight=0x80;
  for(int i=0;i<256;i++) {self->flagtable[i]=((-i)&255)<<8;self->literaltable[i]=i<<8;self->offsettable[i]=i<<8;self->shortoffsettable[i]=i;}
  memset(self->flagreverse,0,sizeof(self->flagreverse));memset(self->literalreverse,0,sizeof(self->literalreverse));ResetTable(self->offsettable,self->offsetreverse);
  self->lastoffset=self->lastlength=self->oldoffsetindex=0;memset(self->oldoffset,0,sizeof(self->oldoffset));
 }
 self->input=input;self->packed=packed;self->bitbuffer=self->bitcount=0;
 self->fileEnd=size>=0&&size<=INT64_MAX-self->generated?self->generated+size:-1;
 self->expected=crc;self->crc=0xffffffff;self->flagbits=0;self->storedblock=0;self->numrepeatedlastmatches=0;
}
int rar15_read(rar15 *self,void *output,int count) {
 if(count<0||self->fileEnd<0||setjmp(self->failure))return -1;
 unsigned char *out=output;int done=0;
 while(done<count&&self->delivered<self->fileEnd) {
  if(self->generated==self->delivered) {
   if(self->storedblock) EmitLiteral(self,&self->generated);
   else {
    int expectingmatch=self->matchweight>self->literalweight;
    if(GetFlagBit(self)) {if(expectingmatch)EmitLongMatch(self,&self->generated);else EmitLiteral(self,&self->generated);}
    else if(GetFlagBit(self)) {if(!expectingmatch)EmitLongMatch(self,&self->generated);else EmitLiteral(self,&self->generated);}
    else EmitShortMatch(self,&self->generated);
   }
  }
  while(done<count&&self->delivered<self->generated) {
   unsigned char b=self->window[(uint64_t)self->delivered++&65535];out[done++]=b;
   self->crc^=b;for(int i=0;i<8;i++)self->crc=(self->crc>>1)^((self->crc&1)?0xedb88320u:0);
  }
 }
 if(self->delivered==self->fileEnd&&~self->crc!=self->expected)return -1;
 return done;
}
static int GetFlagBit(rar15 *self)
{
	if(self->flagbits==0)
	{
		int index=ReadCode(self,&self->huffmancode2);
		if(index==256) longjmp(self->failure,1);
		self->flags=LookupByte(self,self->flagtable,self->flagreverse,0xff,index);
		self->flagbits=8;
	}

	self->flagbits--;
	return (self->flags>>self->flagbits)&1;
}

static void EmitLiteral(rar15 *self,int64_t *posptr)
{
	int index;
	if(self->runningaverageliteral<0xe00) index=ReadCode(self,&self->huffmancode0);
	else if(self->runningaverageliteral<0x3600) index=ReadCode(self,&self->huffmancode1);
	else if(self->runningaverageliteral<0x5e00)  index=ReadCode(self,&self->huffmancode2);
	else if(self->runningaverageliteral<0x7600) index=ReadCode(self,&self->huffmancode3);
	else index=ReadCode(self,&self->huffmancode4);

	if(self->storedblock)
	{
		// This differs slightly from what RAR does, but is sane.
		// (RAR masks away 0x100 before this part, and restores it clumsily).
		if(index==0)
		{
			if(ReadBits(self,1))
			{
				self->storedblock=0;
				self->numrepeatedliterals=0;
				return;
			}
			else
			{
				int length;
				if(ReadBits(self,1)) length=4;
				else length=3;

				int offset=ReadCode(self,&self->huffmancode2)<<5;
				offset|=ReadBits(self,5);

				XADEmitLZSSMatch(self,offset,length,posptr);
				return;
			}
		}
		else index--;
	}
	else
	{
		index&=0xff;
		if(self->numrepeatedliterals++>=16&&self->flagbits==0) self->storedblock=1;
	}

	self->runningaverageliteral+=index;
	self->runningaverageliteral-=self->runningaverageliteral>>8;

	self->literalweight+=16;
	if(self->literalweight>0xff)
	{
		self->literalweight=0x90;
		self->matchweight>>=1;
	}

	uint8_t byte=LookupByte(self,self->literaltable,self->literalreverse,0xa1,index);
	XADEmitLZSSLiteral(self,byte,posptr);
}

static void EmitLongMatch(rar15 *self,int64_t *posptr)
{
	self->numrepeatedliterals=0;
	self->matchweight+=16;
	if(self->matchweight>0xff)
	{
		self->matchweight=0x90;
		self->literalweight>>=1;
	}

	int rawlength;
	if(self->runningaveragelength>=122) rawlength=ReadCode(self,&self->lengthcode2);
	else if(self->runningaveragelength>=64) rawlength=ReadCode(self,&self->lengthcode1);
    else
	{
		rawlength=0;
		while(rawlength<8 && ReadBits(self,1)==0) rawlength++;

		if(rawlength==8) rawlength=ReadBits(self,8);
	}

	int offsetindex;
	if(self->runningaverageoffset<0x700) offsetindex=ReadCode(self,&self->huffmancode0);
	else if(self->runningaverageoffset<0x2900) offsetindex=ReadCode(self,&self->huffmancode1);
	else offsetindex=ReadCode(self,&self->huffmancode2);

	if(offsetindex==0x100) longjmp(self->failure,1);

	int offset=LookupByte(self,self->offsettable,self->offsetreverse,0xff,offsetindex)<<7;
	offset|=ReadBits(self,7);

	int length=rawlength+3;

	if(offset>=self->maximumoffset) length++;
	if(offset<=256) length+=8;

	unsigned int oldMaximumOffset = self->maximumoffset;
	if(self->runningaveragebelowmaximum>0xb0 || (self->runningaverageliteral>=0x2a00
	&& self->runningaveragelength<0x40)) self->maximumoffset=0x7f00;
	else self->maximumoffset=0x2001;


	self->runningaveragelength+=rawlength;
	self->runningaveragelength-=self->runningaveragelength>>5;

	self->runningaverageoffset+=offsetindex;
	self->runningaverageoffset-=self->runningaverageoffset>>8;

	if (rawlength == 0 && offset <= oldMaximumOffset)
    {
		self->runningaveragebelowmaximum++;
		self->runningaveragebelowmaximum-=self->runningaveragebelowmaximum>>8;
    }
    else if(rawlength!=1&&rawlength!=4)
	{
		if(self->runningaveragebelowmaximum>0) self->runningaveragebelowmaximum--;
	}



	self->lastoffset=self->oldoffset[self->oldoffsetindex++&3]=offset;
	self->lastlength=length;

	XADEmitLZSSMatch(self,self->lastoffset,self->lastlength,posptr);
}

static void EmitShortMatch(rar15 *self,int64_t *posptr)
{
	self->numrepeatedliterals=0;

	if(self->numrepeatedlastmatches==2)
	{
		if(ReadBits(self,1))
		{
			XADEmitLZSSMatch(self,self->lastoffset,self->lastlength,posptr);
			return;
		}
		else self->numrepeatedlastmatches=0;
	}

	unsigned int selector;
	if(self->runningaverageselector<37)
	{
		if(self->bugfixflag) selector=ReadCode(self,&self->shortmatchcode0);
		else selector=ReadCode(self,&self->shortmatchcode1);
	}
	else
	{
		if(self->bugfixflag) selector=ReadCode(self,&self->shortmatchcode2);
		else selector=ReadCode(self,&self->shortmatchcode3);
	}

	if(selector<9)
	{
		self->numrepeatedlastmatches=0;

		self->runningaverageselector+=selector;
		self->runningaverageselector-=self->runningaverageselector>>4;

		int offsetindex=ReadCode(self,&self->huffmancode2)&0xff;

		int offset=self->shortoffsettable[offsetindex];
		if(offsetindex!=0)
		{
			self->shortoffsettable[offsetindex]=self->shortoffsettable[offsetindex-1];
			self->shortoffsettable[offsetindex-1]=offset;
		}
		offset++;

		int length=selector+2;

		self->lastoffset=self->oldoffset[self->oldoffsetindex++&3]=offset;
		self->lastlength=length;

		XADEmitLZSSMatch(self,offset,length,posptr);
	}
	else if(selector==9)
    {
		self->numrepeatedlastmatches++;

		XADEmitLZSSMatch(self,self->lastoffset,self->lastlength,posptr);
    }
	else if(selector<14)
	{
		self->numrepeatedlastmatches=0;

		int offset=self->oldoffset[(self->oldoffsetindex-(selector-9))&3];

		int length=ReadCode(self,&self->lengthcode1)+2;
		if(length==0x101 && selector==10)
		{
			self->bugfixflag=!self->bugfixflag;
			return;
		}

		if(offset>256) length++;
		if(offset>=self->maximumoffset) length++;

		self->lastoffset=self->oldoffset[self->oldoffsetindex++&3]=offset;
		self->lastlength=length;

		XADEmitLZSSMatch(self,offset,length,posptr);
	}
	else //if(length==14)
	{
		self->numrepeatedlastmatches=0;

		int length=ReadCode(self,&self->lengthcode2)+5;
		int offset=ReadBits(self,15)+0x8000;

		self->lastoffset=offset;
		self->lastlength=length;

		XADEmitLZSSMatch(self,offset,length,posptr);
	}
}

static int LookupByte(rar15 *self,int *table,int *reverse,int limit,int index)
{
	if(index<0 || index>=256) longjmp(self->failure,1);
	int val=table[index];
	int newindex=reverse[val&0xff]++;

	if((val&0xff)>=limit)
	{
		ResetTable(table,reverse);
		val=table[index];
		newindex=reverse[val&0xff]++;
	}

	if(newindex<0 || newindex>=256) longjmp(self->failure,1);
	table[index]=table[newindex];
	table[newindex]=val+1;

	return val>>8;
}

static void ResetTable(int *table,int *reverse)
{
	for(int i=0;i<8;i++)
	for(int j=0;j<32;j++)
	table[i*32+j]=(table[i*32+j]&~0xff)|(7-i);

	memset(reverse,0,sizeof(int)*256);
	for(int i=0;i<7;i++) reverse[i]=(7-i)*32;
}
