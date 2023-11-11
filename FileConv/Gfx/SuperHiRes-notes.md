# Apple II Super Hi-Res Graphics #

File types:
 - PNT ($c0) / $0000: PaintWorks packed image
 - PNT ($c0) / $0001: compressed super hi-res image (PackBytes)
 - PNT ($c0) / $0002: structured picture file (Apple Preferred Format, or APF)
 - PNT ($c0) / $0003: packed QuickDraw II Picture File
 - PNT ($c0) / $8005: DreamGrafix packed image
 - PIC ($c1) / $0000: uncompressed super hi-res image
 - PIC ($c1) / $0001: QuickDraw PICT file
 - PIC ($c1) / $0002: uncompressed 3200-color super hi-res image
 - PIC ($c1) / $8003: DreamGrafix unpacked image

Primary references:
 - _Apple IIgs Hardware Reference_, chapter 4
 - Apple II File Type Note $c0/0000, "Paintworks Packed Super Hi-Res Picture File"
 - Apple II File Type Note $c0/0001, "Packed Apple IIGS Super Hi-Res Image File"
 - Apple II File Type Note $c0/0002, "Apple IIgs Super Hi-Res Picture File"
 - Apple II File Type Note $c1/0000, "Apple IIGS Super Hi-Res Graphic Screen Image"
 - Apple II File Type Note $c1/0001, "Apple IIGS QuickDraw II Picture File"
 - Apple II File Type Note $c1/0002, "Super Hi-Res 3200 color screen image"
 - IIgs TN #46, "DrawPicture Data Format"
 - DreamGrafix decompression code, provided by co-author Jason Andersen

The Super Hi-Resolution graphics mode was introduced on the Apple IIgs.  It allowed 320x200 or
640x200 images from a palette of 4096 colors.  The pixel and color data require a total of
exactly 32KB of RAM.

## Hardware Details ##

The super hi-res graphics screen lives in the auxiliary 64K bank of the "slow" RAM, bank $e1,
from $2000-9fff.  The memory layout is:
```
 $2000-9cff: pixel data (32000 bytes)
 $9d00-9dc7: scan-line control bytes (SCB) (200 bytes)
 $9dc8-9dff: reserved (56 bytes)
 $9e00-9fff: color palettes (512 bytes)
```

Each of the 200 rows has an SCB entry that determines how the pixel data is interpreted for
that row.  Each entry is a single byte, with bits defined:
```
 7  : horizontal resolution; 0=320, 1=640
 6  : interrupt enable for this scan line; 0=disabled, 1=enabled
 5  : color fill enable (320 mode only); 0=disabled, 1=enabled
 4  : reserved, must be 0
 0-3: color palette (0-15)
```

There are 16 color palettes, with 16 colors in each, for a total of 256 entries.  Each color
entry is two consecutive bytes, holding an RGB444 value as `$0RGB`:
```
 even (low) byte:
   7-4: green intensity
   3-0: blue intensity
 odd (high) byte:
   7-4: reserved, must be zero
   3-0: red intensity
```
For example, color 0 in palette 0 is at $9e00-9e01, while color 14 in palette 2 is at $9e5e-9e5f.

In 320 mode, each byte specifies the colors of two pixels.  In 640 mode, each byte specifies the
colors of four pixels.  The byte layout is:
```
  bit: | 7 | 6 | 5 | 4 | 3 | 2 | 1 | 0 |
  640: | pix 0 | pix 1 | pix 2 | pix 3 |
  320: |     pix 0     |     pix 1     |
```
In 320 mode, the 4-bit pixel value is used as an index into the palette specified for the
current scan line.  This allows 16 simultaneous colors per line.

In 640 mode, the 2-bit pixel value is used as an index into *part* of the palette specified for
the current scan line.  The pixel position within the byte (0-3) is combined with the pixel value
(0-3) to determine the palette index (0-15).  Thus it's still possible to have 16 simultaneous
colors per line, but not every pixel can be any color.  The mapping looks like this:
```
  pixel num: 2        3        0        1
  pixel val: 0 1 2 3  0 1 2 3  0 1 2 3  0 1 2 3
  pal entry: 0 1 2 3  4 5 6 7  8 9 a b  c d e f
```
In practice, the palette should either be loaded with a single color for all 4 pixel positions,
or with two colors alternating to create a dither pattern.

It's worth noting that 320 and 640 mode can be specified on a per-scanline basis.

A scanline using 320 mode can also enable "fill mode".  This repurposes color index zero to
repeat whatever the last color drawn was, providing a way to fill large areas of the screen
with color quickly.  This was used in a handful of games and demos, but not usually in static
images.  If the leftmost pixel in a scanline is zero, the results are undefined.

An 320-mode image that uses a single color palette is limited to 16 different colors, where
each color can be one of 4096 different values.  With 16 palettes of 16 colors, it's possible to
have up to 256 colors in a single image, but only 16 on any given line.

With careful coding, it's possible to update the palette data as the screen is being drawn.  This
allows every scanline to have a unique set of 16 colors.  Such images are called "3200 color",
because 200*16=3200.  These images always use 320 mode.

## Image Formats ##

The QuickDraw II PICT formats are meant to be drawn with the IIgs toolbox functions.  Rendering
them is non-trivial.  The formats described here are for bitmaps.

### PIC/$0000: Uncompressed Image ###

This is just a dump of RAM: pixel data, SCBs, reserved, palettes, 32KB total.

Sometimes these have file type BIN.  If we see a 32KB file that ends with ".PIC" or ".SHR", it's
probably an uncompressed SHR image.

### PIC/$0002: Uncompressed 3200-Color Image ###

This is commonly known as "Brooks format", after the designer, John Brooks.  The file has
32,000 bytes of pixel data (200 lines, 160 bytes per line) followed by 200 sets of 32-byte
palette entries (one per line).  The color table is stored in reverse order, i.e. the color
value for color 15 is stored first.

No SCB data is stored, because lines are always 320 mode, and each line has its own palette.

### PNT/$0000: PaintWorks Packed ###

The PaintWorks format is briefly described in the file type note:
```
+$000 / 32: color palette (16 entries, two bytes each)
+$020 /  2: background color
+$022 /512: 16 QuickDraw II patterns, each 32 bytes long
+$222 /nnn: packed graphics data
```
The file type note says, "the unpacked data could be longer than one Super Hi-Res screen".  The
height and length are not explicitly stored, so you're expected to just unpack data until there's
nothing left to unpack.

There's a bit of strangeness around certain files that have 9 bytes left over at the end.

[ TODO ]

### PNT/$0001: Simple Compressed Image ###

This is a simple 32KB SHR image that has been compressed with Apple's PackBytes algorithm.

### PNT/$0002: Apple Preferred Format ###

The file format uses a "chunk" layout, where each chunk is:
```
+$00 / 4: length of block (including the length word)
+$04 /nn: block name string, preceded by a length byte
+$xx /yy: variable amount of block-specific data
```
The type string is case-sensitive ASCII; the use of uppercase characters is recommended.

[ TODO ]

### PNT/$8005 and PIC/$8003: DreamGrafix Image ###

DreamGrafix was an Apple IIgs application that allowed editing of super hi-res images in 256-color
mode and 3200-color mode.  The latter required overcoming some technical hurdles.

DreamGrafix files end with a 17-byte footer (all multi-byte values are little-endian):
```
+$00 / 2: color mode: 0=256 colors, 1=3200 colors
+$02 / 2: height, in pixels (expected to be 200)
+$04 / 2: width, in pixels (expected to be 320)
+$06 /11: signature string: "DreamWorld" in ASCII, preceded by length byte
```
The rest of the file contents depend on the color mode.  For a 256-color image it generally
matches the super hi-res screen memory layout:
```
+$0000/32000: pixel data (200 lines of 160 bytes, two pixels per byte)
+$7d00/  256: SCB (200 lines + 56 unused entries)
+$7e00/  512: palette (16 sets of 16 color entries, 2 bytes per entry)
+$8000/  512: optional / unused?
```
For a 3200-color image it changes to:
```
+$0000/32000: pixel data (200 lines of 160 bytes, two pixels per byte)
+$7d00/ 6400: color table (200 lines * 16 color entries, 2 bytes per entry)
+$9600/  512: optional / unused?
```
The color table has the same layout as PIC/$0002 ("Brooks format").

For PNT/$8005, everything except the footer is compressed with 12-bit LZW.

PIC/$8003 doesn't appear to be used.

### Miscellaneous ###

The IIgs version of "John Elway Quarterback" used a slightly customized format.  The 56-byte
"reserved" section between the SCB and color palettes was omitted, and only one palette entry
was stored (all rows use color palette zero).  This reduced the file size by 480 bytes.  The files
have ProDOS type BIN/$0000, are 32288 bytes long, and have filenames ending in ".APP".  The format
doesn't appear to have been used for anything else.
