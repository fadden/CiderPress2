# StuffIt Archive Format #

## Primary References ##

- The source code for "unar", which appears to be based on a reverse-engineering effort:
  https://github.com/ashang/unar/blob/master/XADMaster/XADStuffItParser.m and
  https://github.com/ashang/unar/blob/473488cc612c3f47e1c76f9baf92d268db259dc1/XADMaster/XADStuffIt5Parser.m#L29

## General ##

[StuffIt](https://en.wikipedia.org/wiki/StuffIt) began in 1987, as an application for the
Macintosh developed by Raymond Lau.  The program was so successful that Aladdin Systems was formed
to sell it.  Various incarnations of the software were made available as freeware (e.g. StuffIt
Expander), shareware (StuffIt Classic), or commercial (StuffIt Deluxe) until it was discontinued
in 2020.

The archive file formats are proprietary.  The format changed significantly in version 5 of the
application.  A major update, the "StuffIt X" format, was introduced in 2002.

StuffIt archives found on the Internet typically use the ".sit" extension.  StuffIt X files
use ".sitx", and self-extracting archives use ".sea".  On HFS, archives used the creator `SIT!`
with file type `SIT!` or `SITD`.

Support for various compression algorithms was added over the years.  Early versions supported
LZC and the typical RLE / Huffman variants, later versions included arithmetic encoding, LZH,
BWT, and others.

Because the format is proprietary, very few programs (other than those developed by Aladdin or
its successors) can unpack StuffIt archives.  One of the few is
"[The Unarchiver](https://theunarchiver.com/command-line)", an open-source project based in
part on an Amiga unarchiving system called [XAD](https://en.wikipedia.org/wiki/XAD_(software)).

## File Structure ##

[ TODO ]
