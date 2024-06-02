# Bitmap Fonts #

File types:
 - ProDOS FON ($c8) / $0000: Apple IIgs QuickDraw II font

Primary references:
 - Apple II File Type Note $c8/0000, "Apple IIGS Font File"
 - IIgs TN #41: "Font Family Numbers"
 - _Apple IIgs Toolbox Reference_, chapter 16 (vol 2, p.16-41)
 - _Inside Macintosh, volume I_, chapter 7 "The Font Manager", p.I-227

On vintage Mac OS, fonts were not stored in individual files.  Instead, they were stored as
resources with type `FONT` in `System Folder:System`, and `Font/DA Mover` was used to manage
them.  Resources with type `FWID` could be used to store font metadata.  System fonts, which
the user was not allowed to remove, used resource type `FRSV`.

The resource ID of a Macintosh font was based on its font number and size:
(128 * font_number) + font_size.  Because a font size of zero is invalid, a resource ID with
zero in the size field was used to indicate the font name.  (cf. _IM_, p.I-234)

On the Apple IIgs, bitmap fonts are stored in the data fork of FON files with auxtype $0000.
TrueType fonts are stored in the resource fork of FON files with auxtype $0001.

## Apple IIgs Definition ##

The bitmap font file starts with a Pascal string (i.e. string prefixed with an 8-bit length)
containing the font family name.  Immediately after that is the QuickDraw II Font definition.

The QuickDraw II definition consists of a variable-length header, followed by the Macintosh Font
record (MF).  The only difference between the MF record on the IIgs and an actual Macintosh font
definition is that multi-byte integers are stored in little-endian order.  Note this does not
apply to the font strike (bitImage), which is stored the same in both.

All 16-bit integers should be regarded as signed.

The IIgs font header is:
```
+$00 / 2: offsetToMF: offset in 16-bit words to Macintosh font part (usually 6, i.e. 12 bytes)
+$02 / 2: family: font family number
+$04 / 2: style: style font was designed with (so as to avoid italicizing an italic font)
+$06 / 2: size: point size
+$08 / 2: version: version number of font definition, e.g. $0101 is v1.1.
+$0a / 2: fbrExtent: font bounds rectangle extent
+$0c /xx: additional fields, if any
```
The `fbrExtent` field is essentially the maximum width of all characters in the font, but it's
more complicated than that.  See p.16-53 in the toolbox reference manual.

The `style` field is a bit map:
```
$0001 - bold
$0002 - italic
$0004 - underline
$0008 - outline
$0010 - shadow
```

The Macintosh font record is:
```
+$00 / 2: fontType: font type, ignored on the Apple IIgs
+$02 / 2: firstChar: ASCII code of first defined character
+$04 / 2: lastChar: ASCII code of last defined character
+$06 / 2: widMax: maximum character width, in pixels
+$08 / 2: kernMax: maximum leftward kern, in pixels (may be positive or negative)
+$0a / 2: nDescent: negative of descent, in pixels
+$0c / 2: fRectWidth: width of font rectangle, in pixels
+$0e / 2: fRectHeight: height of font rectangle, in pixels
+$10 / 2: owTLoc: offset in words from here to offset/width table
+$12 / 2: ascent: font ascent, in pixels
+$14 / 2: descent: font descent, in pixels
+$16 / 2: leading: leading, in pixels (affects vertical space between lines)
+$18 / 2: rowWords: width of font strike in words
+$1a /xx: bitImage: Array(1..rowWords, 1..fRectHeight) of word: font strike
+$nn /yy: locTable: Array(firstChar..lastChar + 2) of int: location table
+$mm /yy: owTable: Array(firstChar..lastChar + 2) of int: offset/width table
```

On the Macintosh, the `fontType` field could hold `propFont` for proportional fonts,
`fixedFont` for fixed-width fonts, or `fontWid` to indicate font width data (for `FWID`).

### Font Family Numbers ###

Font family numbers were defined in a Nov 1990 tech note (IIgs #41).  These also apply to
various LaserWriter printers.

| ID    | Family Name            |
|-------|------------------------|
| $fffd | Chicago                |
| $fffe | Shaston                |
| $ffff | (no font)              |
| 0     | System Font            |
| 1     | System Font            |
| 2     | New York               |
| 3     | Geneva                 |
| 4     | Monaco                 |
| 5     | Venice                 |
| 6     | London                 |
| 7     | Athens                 |
| 8     | San Francisco          |
| 9     | Toronto                |
| 11    | Cairo                  |
| 12    | Los Angeles            |
| 13    | Zapf Dingbats          |
| 14    | Bookman                |
| 15    | Helvetica Narrow       |
| 16    | Palatino               |
| 18    | Zapf Chancery          |
| 20    | Times                  |
| 21    | Helvetica              |
| 22    | Courier                |
| 23    | Symbol                 |
| 24    | Taliesin               |
| 33    | Avant Garde            |
| 34    | New Century Schoolbook |

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
