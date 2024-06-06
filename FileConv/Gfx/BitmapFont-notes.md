# Bitmap Fonts #

File types:
 - ProDOS FON ($c8) / $0000: Apple IIgs QuickDraw II font

Primary references:
 - Apple II File Type Note $c8/0000, "Apple IIGS Font File"
 - IIgs TN #41: "Font Family Numbers"
 - _Apple IIgs Toolbox Reference_, chapter 16 (vol 2, p.16-41)
 - _Inside Macintosh, volume I_, chapter 7 "The Font Manager", p.I-227
 - _Inside Macintosh, volume IV_, chapter 5 "The Font Manager", p.IV-29

On vintage Mac OS, fonts were not stored in individual files.  Instead, they were stored as
resources with type `FONT` in `System Folder:System`, and `Font/DA Mover` was used to manage
them.  Resources with type `FWID` could be used to store font metadata.  System fonts, which
the user was not allowed to remove, used resource type `FRSV`.  Later versions of the Font
Manager, introduced with the Macintosh Plus, looked for font definition `FOND` resources and
recognized `NFNT`.

The resource ID of a Macintosh font was based on its font number and size:
(128 * font_number) + font_size.  Because a font size of zero is invalid, a resource ID with
zero in the size field was used to indicate the font name.  (cf. _IMv1_, p.I-234)

On the Apple IIgs, bitmap fonts are stored in the data fork of FON files with auxtype $0000.
TrueType fonts are stored in the resource fork of FON files with auxtype $0001.  Some valid
bitmap font files have been found with auxtypes $0006, $0016, and $0026, though it's unclear
why those auxtypes were used.

## Apple IIgs Font File ##

The bitmap font file starts with a Pascal string (i.e. string prefixed with an 8-bit length)
containing the font family name.  It's unclear whether this is strictly ASCII or may include
Mac OS Roman characters.  The string is immediately followed by the QuickDraw II Font definition.

The QuickDraw II definition consists of a variable-length header, followed by the Macintosh Font
record (MF).  The only difference between the MF record on the IIgs and an actual Macintosh font
definition is that multi-byte integers are stored in little-endian order.  Note this does not
apply to the font strike (bitImage), which is stored the same in both.

16-bit integers should generally be regarded as signed.

The IIgs font header is:
```
+$00 / 2: offsetToMF: offset in 16-bit words to Macintosh font part (usually 6, i.e. 12 bytes)
+$02 / 2: family: font family number
+$04 / 2: style: style font was designed with (so as to avoid italicizing an italic font)
+$06 / 2: size: point size
+$08 / 2: version: version number of font definition, usually $0101 for v1.1
+$0a / 2: fbrExtent: font bounds rectangle extent
+$0c /xx: additional fields, if any
```
The `fbrExtent` field is essentially the maximum width of all characters in the font, taking
kerning into account, but it's more complicated than that.  See p.16-53 in the IIgs Toolbox
Reference manual.

The `style` field is a bit map:
```
$0001 - bold
$0002 - italic
$0004 - underline
$0008 - outline
$0010 - shadow
$0020 - condensed [not on IIgs]
$0040 - extended [not on IIgs]
```

The Macintosh font record is:
```
+$00 / 2: fontType: font type; ignored on the Apple IIgs
+$02 / 2: firstChar: Mac OS character code of first defined character (0-255)
+$04 / 2: lastChar: Mac OS character code of last defined character (0-255)
+$06 / 2: widMax: maximum character width, in pixels
+$08 / 2: kernMax: maximum leftward kern, in pixels (may be positive or negative)
+$0a / 2: nDescent: negative of descent, in pixels
+$0c / 2: fRectWidth: width of font rectangle, in pixels
+$0e / 2: fRectHeight: height of font rectangle, in pixels (equal to ascent + descent)
+$10 / 2: owTLoc: offset in words from here to offset/width table
+$12 / 2: ascent: font ascent, in pixels
+$14 / 2: descent: font descent, in pixels
+$16 / 2: leading: leading (rhymes with "heading"), in pixels (vertical space between lines)
+$18 / 2: rowWords: width of font strike, in 16-bit words
+$1a /xx: bitImage: (rowWords * fRectHeight) 16-bit words: font strike
+$nn /yy: locTable: (lastChar - firstChar + 3) 16-bit ints: pixel offset of glyph in bitImage
+$mm /yy: owTable: (lastChar - firstChar + 3) 16-bit words: offset/width table
```
Note there are two additional character entries in the location and offset/width tables.
The entry at `lastChar + 1` is for the "missing symbol" glyph.  One additional entry is needed
at `lastChar + 2`, because the image width of a glyph for character C in `bitImage` is given by
`locTable[C + 1] - locTable[C]`.

The `owTLoc` value is equal to `4 + (rowWords * fRectHeight) + (lastChar-firstChar+3) +1`.
Remember that this is expressed in 16-bit words, not bytes.

An `owTable` entry value of -1 ($ffff) indicates that the character is not represented in the font.
Otherwise, the high byte holds the pixel offset of the glyph origin, and the low byte holds
the character width.

The `bitImage` table is stored in row-major order, i.e. all of the pixels for row 0 are laid
out, followed by all of the pixels for row 1.  The leftmost pixel is stored in the high bit.
A '1' bit indicates a lit pixel.  The table is measured in 16-bit words, so the last 0-15 pixels
are garbage.

Later versions of Mac OS added kerning tables that allow the amount to vary based on which
characters are adjacent.

You might expect `nDescent` == `-descent`, but in practice this is not always the case, even
with fonts supplied by Apple.  It's unclear what `nDescent` means or what purpose it serves.

_IMv1_, p.I-230 declares that every font must have a "missing symbol", and the characters
with ASCII codes $00 (NUL), $09 (horizontal tab), and $0d (carriage return) must not be missing
from the font.  In practice, most but not all fonts define $09/$0d, fewer define $00, and the
last glyph in every font is de facto the missing symbol glyph.

### Font Type ###

On the Macintosh, the `fontType` field initially held `propFont` for proportional fonts,
`fixedFont` for fixed-width fonts, or `fontWid` to indicate font width data (for `FWID`).
This was later updated (see _IMv4_, p.IV-35):

| Value | Name
|-------|--------------------------------|
| $9000 | proportional font
| $9001 | ..with height table
| $9002 | ..with width table
| $9003 | ..with height & width tables
| $b000 | fixed-width font
| $b001 | ..with height table
| $b002 | ..with width table
| $b003 | ..with height & width tables
| $acb0 | font width data: 64K ROM only

The optional width table held the character widths for all entries as 8.8 fixed-point values.
The fractional part allows more precise placement of glyphs, which is important when printing.

The optional height table holds the image height (all entries have the same *character* height)
for all entries, stored as two 8-bit values: the high-order byte is the offset of the first
non-white row, the low-order byte is the number of rows that must be drawn.  (Font resources
didn't typically include this; rather, it was generated by the Font Manager in memory.)

The Font Type field is ignored by QuickDraw II on the Apple IIgs.  Fonts converted from the Mac
will have one of these values, but will not include the extra tables even if the type field
indicates that they are present.

### Font Family Numbers ###

Font family numbers were listed in a Nov 1990 tech note (IIgs #41).  These also apply to
various LaserWriter printers.

| ID    | Family Name            |
|-------|------------------------|
| $fffd | Chicago
| $fffe | Shaston
| $ffff | (no font)
| 0     | System Font
| 1     | System Font
| 2     | New York
| 3     | Geneva
| 4     | Monaco
| 5     | Venice
| 6     | London
| 7     | Athens
| 8     | San Francisco
| 9     | Toronto
| 11    | Cairo
| 12    | Los Angeles
| 13    | Zapf Dingbats
| 14    | Bookman
| 15    | Helvetica Narrow
| 16    | Palatino
| 18    | Zapf Chancery
| 20    | Times
| 21    | Helvetica
| 22    | Courier
| 23    | Symbol
| 24    | Taliesin
| 33    | Avant Garde
| 34    | New Century Schoolbook

The tech note contains a caution that font family numbers may be arbitrarily reassigned, e.g.
the Macintosh Font/DA Mover will renumber a font if it discovers that the family number is
already in use.  Asking for a font by family name is recommended.

The tech note also says:
> By convention, font family numbers that have the high bit set are designed for
> the 5:12 aspect ratio of the Apple IIgs computer.  Font family numbers with the
> high bit clear are designed for computers with a 1:1 pixel aspect ratio, such as
> the Macintosh.  Fonts designed for a 1:1 pixel aspect ratio appear "tall and
> skinny" when displayed on an Apple IIgs.
>
> Some third-party font packages were released before this convention was defined;
> therefore, font family numbers between 1000 and 1200 (decimal) do not adhere to
> this convention.
