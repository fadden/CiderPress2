# Print Shop Graphics

File types:

- BIN / $4800/5800/6800/7800, length 572 or 576: Print Shop 88x52 Monochrome Clip Art
- BIN / $4800/5800/6800/7800, length 144 or 148: Print Shop 12x12 Monochrome Border
- BIN / $5FF4/6000: Print Shop Font
- $f8 / $c311: Print Shop GS ?x? Monochrome Pattern
- $f8 / $c312: Print Shop GS ?x? Monochrome Border
- $f8 / $c313: Print Shop GS 88x52 Monochrome Clip Art
- $f8 / $c316: Print Shop GS Font
- $f8 / $c321: Print Shop GS ?x? 8-Color Pattern
- $f8 / $c322: Print Shop GS ?x? 8-Color Border
- $f8 / $c323: Print Shop GS 88x52 8-color Clip Art
- $f8 / $c324: Print Shop GS "Full Panel GC"
- $f8 / $c325: Print Shop GS "Full Panel LH"
- $f8 / $c328: Print Shop GS "Editor"
- $f8 / $c329: Print Shop GS "Full Panel EV"

Primary references:

- Reverse engineering

The ProDOS files used a generic type, so no file type notes exist for these formats.

The web site https://theprintshop.club/ provides an emulated version of the original Apple II
program that can print to PDF.

## Clip Art

The original Print Shop uses 88x52 monochrome images.  When printed or displayed, these are
doubled in width and tripled in height to form a 176x156 image.  Print Shop GS also supports
these, as "single-color" artwork.

The files are simple bitmaps.  The top-left pixel is stored in the most-significant bit of the
first byte of the file.  A '1' bit indicates a black pixel, while a '0' bit indicates white.

The color clip art used by Print Shop GS is stored as a bitmap with three color planes.  These
are approximately CMY.  The yellow plane is stored first, followed by the magenta plane, then
the cyan plane.

The actual color palette, as determined by a screen grab from an emulator, is comprised of RGB
primaries and purple/orange that are similar to the classic Apple II hi-res colors.

| Y M C | color  | red, green, blue |
| ----- | ------ | ---------------- |
| 0 0 0 | white  | 0xff, 0xff, 0xff |
| 0 0 1 | blue   | 0x00, 0x00, 0xff |
| 0 1 0 | red    | 0xff, 0x00, 0x00 |
| 0 1 1 | purple | 0xcc, 0x00, 0xcc |
| 1 0 0 | yellow | 0xff, 0xff, 0x00 |
| 1 0 1 | green  | 0x00, 0xff, 0x00 |
| 1 1 0 | orange | 0xff, 0x66, 0x00 |
| 1 1 1 | black  | 0x00, 0x00, 0x00 |

## Fonts

Each font contains 59 characters (0-58).

In the Print SHop Companion (PSC) font editor, offsets 0 (space) and 32 (image) are not editable.

In Print Shop[, offset 32 points to the current graphic selected, which
is generally not contained in the font file itself.  However, since it is an arbitrary data pointer, it is
conceivble that there could be self-contained data in the file.

The Space character is at offset 0, and in all the examples I've seen, has a height of 0 pixels.
The data is calculated in the PSC font editor based off of an average of all other character widths.

$5FF4-$5FFF - Optional Print Shop Companion Font Header (From Font Editor)
$6000-$603A - Character widths (in pixels).  High bit indicates "Edited" in PSC.  Should be stripped.
$603B-$6075 - Character heights in rows.
$6076-$60B0 - Low Bytes of character data pointers
$60B1-$60EB - High Bytes of character data pointers
$60EC-EOF   - Character data.

To check for validity, we can iterate through all 59 characters (excepting 0 and 32) and check that:

- The address pointers fall within the range of the data file.
- Width and height values are "reasonable".  The PSC font editor limits character sizes to a max of 48x38 (152 bytes)

If all are within range, then acceptability should be flagged as Yes, if the pointers are in range, but some characters are out of the expected sizes, then the accptability should be flagged as ProbablyYes

Like the Print Shop graphic data, fonts are expanded 2x3 in usage.
