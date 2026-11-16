# StuffIt Arsenic Compression #

## Primary References ##

- The source code for "unar", notably:
  https://github.com/ashang/unar/blob/master/XADMaster/XADStuffItArsenicHandle.m
- Matthew T. Russotto's explanation: http://www.russotto.net/arseniccomp.html

## General ##

StuffIt v5.0 introduced Arsenic compression (method 15), labeled in the interface as
"better" compression.  This usually provided better compression than the default LZ+Huffman
method, but was slower.

Files are compressed with RLE, then with the Burrows-Wheeler Transform (BWT) using an arithmetic
encoder.

## Details ##

[ TBD ]
