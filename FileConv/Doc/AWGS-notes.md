# AppleWorks GS Documents #

File types:
 - GWP ($50) / $8010 : AppleWorks GS Word Processor
 - GSS ($51) / $8010 : AppleWorks GS Spreadsheet
 - GDB ($52) / $8010 : AppleWorks GS Data Base
 - GDB ($52) / $8011 : AppleWorks GS DB Template
 - DRW ($53) / $8010 : AppleWorks GS Graphics
 - GDP ($54) / $8010 : AppleWorks GS Page Layout
 - COM ($59) / $8010 : AppleWorks GS Communications
 - CFG ($5a) / $8010 : AppleWorks GS configuration

Primary references:
 - Apple II File Type Note $50/8010, "AppleWorks GS Word Processor File"

## General ##

AppleWorks GS is an integrated office suite, combining a word processor, database, spreadsheet,
communications, page layout, and graphics modules in a single program.  A detailed file format
description was published for the word processor, but not for any of the other components.

Originally created by StyleWare as "GS Works", the program was purchased by Claris and sold
under the AppleWorks name.  As with AppleWorks "Classic", rights to the program were licensed
to Quality Computers after Claris lost interest in Apple II products.

## Word Processor ##

The document structure is organized around paragraphs.  All multi-byte integers are stored in
little-endian order.

The overall structure is:
```
+$000 / 282: document header
+$11a / 386: globals
+$29c / nnn: document body chunk
+$xxx / ooo: document header chunk
+$yyy / ppp: document footer chunk
```
See the filetype note for details on the contents of the document header and globals.

Each "chunk" has three parts:

 - SaveArray entries.  One entry per paragraph, 12 bytes each.  The list is preceded by a count
   of the number of entries (1-65535).
 - Rulers.  52 bytes each.  Each paragraph is associated with a ruler, so there will be at
   least one defined.
 - Text blocks.  Variable size, holding one or more paragraphs.  Every chunk has at least one
   paragraph, so there will be at least one text block.

A SaveArray entry has six elements:
```
+$00 / 2: textBlock - number of text block that holds the paragraph; starts from zero
+$02 / 2: offset - offset to start of paragraph within the text block
+$04 / 2: attributes - 0=normal text, 1=page break paragraph
+$06 / 2: rulerNum - index of the ruler associated with this paragraph
+$08 / 2: pixelHeight - height of this paragraph, in pixels
+$0a / 2: numLines - number of lines in this paragraph
```
The number of rulers in a chunk can be determined by reading through the SaveArray entries and
noting the highest-numbered ruler reference.

Text Block Records start with a 32-bit word that identifies their size, followed by the Text Block
itself.  The Text Block starts with a pair of 16-bit lengths, one for the total size and one for
the actually-used size, both of which should be equal to the 32-bit length in the record header.
A text block can hold multiple paragraphs.

Each paragraph starts with a 7-byte header:
```
+$00 / 2: firstFont - font family number of the first character in the paragraph
+$02 / 1: firstStyle - style of the first character
+$03 / 1: firstSize - size, in points, of the first character
+$04 / 1: firstColor - offset into color table of color of first character
+$05 / 2: reserved
```
Paragraphs end with a carriage return ($0d).

The character set may be assumed to be Mac OS Roman.

The theoretical maximum size of an AWGS document is nearly 4GB (65535 paragraphs with 65523
characters).  Given the limitations of a typical Apple IIgs system, it's unlikely that documents
larger than a few hundred KB were created for anything other than testing.
