# Apple II Hi-Res Graphics #

File types:
 - BIN ($06) / any (often $2000 or $4000), length 8184-8192: uncompressed hi-res image (~8KB file)
 - FOT ($08) / $0000-3fff: uncompressed hi-res image (8KB file)
 - FOT ($08) / $4000: compressed hi-res image (PackBytes)
 - FOT ($08) / $8066: compressed hi-res image (LZ4FH)

Primary references:
 - Apple II File Type Note $08/0000, "Apple II Graphics File"
 - Apple II File Type Note $08/4000, "Packed Apple II Hi-Res Graphics File"
 - IIgs TN #63, "Master Color Values"

## Apple II Hi-res in a Nutshell ##

This is a quick overview of the Apple II hi-res graphics architecture
for anyone not recently acquainted.  [This originally appeared in the
documentation for
[fdraw](https://github.com/fadden/fdraw/blob/master/docs/manual.md).]

The Apple II hi-res graphics screen is a quirky beast.  The typical
API treats it as 280x192 with 6 colors (black, white, green, purple,
orange, blue), though the reality is more complicated than that.

There are two hi-res screens, occupying 8K each, at $2000 and $4000.
You turn them on and flip between them by accessing softswitches in
memory-mapped I/O space.

Each byte determines the color of seven adjacent pixels, so it takes
(280 / 7) = 40 bytes to store each line.  The lines are organized into
groups of three (120 bytes), which are interleaved across thirds of
the screen.  To speed the computation used to find the start of a
line in memory, the group is padded out to 128 bytes; this means
((192 / 3) * 8) = 512 of the 8192 bytes are part of invisible
"screen holes".  The interleaving is responsible for the characteristic
"venetian blind" effect when clearing the screen.

Now imagine 280 bits in a row.  If two consecutive bits are on, you
get white.  If they're both off, you get black.  If they alternate
on and off, you get color.  The color depends on the position of the bit;
for example, if even-numbered bits are on, you get purple, while
odd-numbered bits yield green.  The high bit in each byte adjusts the
position of bits within that byte by half a pixel, changing purple and
green to blue and orange.

This arrangement has some curious consequences.  If you have green and
purple next to each other, there will be a color glitch where they meet.
The reason is obvious if you look at the bit patterns when odd/even meet:
`...010101101010...` or `...101010010101...`.  The first pattern has two
adjacent 1 bits (white), the latter two adjacent 0 bits (black).  Things
get even weirder if split occurs at a byte boundary and the high bit is
different, as the half-pixel shift can make the "glitch" pixel wider or
narrower by half a pixel.

The Applesoft ROM routines draw lines that are 1 bit wide.  If you execute
a command like `HGR : HCOLOR=1 : HPLOT 0,0 to 0,10`, you won't see
anything happen.  That's because HCOLOR=1 sets the color to green,
which means it only draws on odd pixels, but the HPLOT command we gave
drew a vertical line on even pixels.  It set 11 bits to zero, but since
the screen was already zeroed out there was no apparent effect.

If you execute `HGR : HCOLOR=3 : HPLOT 1,0 to 1,10`, you would expect a
white line to appear.  However, drawing in "white" just means that no
bit positions are excluded.  So it drew a vertical column of pixels at
X=1, which appears as a green line.

If (without clearing the screen after the previous command) you execute
"HCOLOR=4 : HPLOT 5,0 to 5,10`, something curious happens: the green line
turns orange.  HCOLOR=4 is black with the high-bit set.  So we drew a
line of black in column 5 (which we won't see, because that part of the
screen is already black), and set the high bit in that byte.  The same
byte holds columns 0 through 6, so drawing in column 5 also affected
column 1.  We can put it back to green with "HCOLOR=0 : HPLOT 5,0 to 5,10".

It's important to keep the structure in mind while drawing to avoid
surprises.

Note that the Applesoft ROM routines treat 0,0 as the top-left corner,
with positive coordinates moving right and down, and lines are drawn
with inclusive end coordinates.  This is different from many modern
systems.  fdraw follows the Applesoft conventions to avoid confusion.

## Bits and Bytes ##

The 8 colors and the associated bit patterns are:

    0 black0    4 black1        00 00 / 80 80
    1 green     5 orange        2a 55 / aa d5
    2 purple    6 blue          55 2a / d5 aa
    3 white0    7 white1        7f 7f / ff ff

Because colors are based on whether a pixel is in an odd or even column,
and each byte holds 7 pixels, the color bit patterns are different for
odd bytes vs. even bytes.  The low bit is the leftmost pixel.

The "half-shift" phenomenon causes pixels in bytes with the high bit set
to be shifted half a pixel to the right.  The left edge of colored pixels
ends up having a stair-step effect:
```
 white0
  purple
   white1
    blue
     green
      orange
```

The transition between colors will be filled with black or white pixels.  For
example, a byte of purple followed by a byte of orange, with the purple in an
even column, is 1010101-1010101.  Because there are two adjacent '1' bits, the
transition will be white.  Because orange has its high bit set, the white
transition area is half a pixel wider.  If the purple had started in an odd
column, the values would be 0101010-0101010, yielding a black transition.

To make things even more complicated, the transitions can show unexpected
colors.  For example, the IIgs RGB monitor generates some odd colors in the
borders between solid colors with different high bits:
```
                     observed                 generated
    d5 2a:    blue   [dk blue] purple       ... black ...
    aa 55:    orange [yellow]  green        ... white ...
    55 aa:    purple [lt blue] blue         ... black ...
    2a d5:    green  [brown]   orange       ... black ...
```
Some emulators (e.g. AppleWin) will model this behavior.

The IIgs monochrome mode is not enabled on the RGB output unless you
turn off AN3 by hitting $c05e (it can be re-enabled by hitting $c05f).
This register turns off the half-pixel shift, so it doesn't appear to
be possible to view hi-res output on an RGB monitor with the half-pixel
shift intact.  On the composite output, the presence of the half-pixel
shift is quite visible.

## Color Palette ##

The actual colors shown on a composite monitor or television are very
different from what appears on an RGB monitor.  There has been some debate
concerning which is "correct".

https://groups.google.com/g/comp.sys.apple2/c/uao26taTXEI/m/DwaJPt_0oPoJ

IIgs tech note #63 specifies the following for border colors:

    Color    Color Register    Master Color
    Name         Value            Value
    ---------------------------------------
    Black         $0              $0000
    Deep Red      $1              $0D03
    Dark Blue     $2              $0009
    Purple        $3              $0D2D
    Dark Green    $4              $0072
    Dark Gray     $5              $0555
    Medium Blue   $6              $022F
    Light Blue    $7              $06AF
    Brown         $8              $0850
    Orange        $9              $0F60
    Light Gray    $A              $0AAA
    Pink          $B              $0F98
    Light Green   $C              $01D0
    Yellow        $D              $0FF0
    Aquamarine    $E              $04F9
    White         $F              $0FFF
    ---------------------------------------

These are the same 16 colors that appear on the lo-res graphics screen, and
the hi-res output matches them.

## File Formats ##

Hi-res graphics screens may be saved on disk as a binary file with
length $1ff8, $1ffc, or $2000.  The shorter length is possible because the
data in the "screen holes" doesn't need to be saved.  The shorter length is
valuable because, on a DOS 3.x disk, it reduces the storage requirement by
one sector.

The ProDOS file type FOT is assigned as the official type, but most of the
time the files just use BIN.

For FOT files with an auxtype < $4000, a byte in the first screen hole
at offset +120 determines how the file should be treated (e.g. as B&W or
color).

LZ4FH files are created by [fhpack](https://github.com/fadden/fhpack).  The
[compression sources](https://github.com/fadden/fhpack/blob/master/fhpack.cpp)
describe the file format in detail.

# Hi-Res Fonts #

Apple II fonts usually span the printable character range ($20-7f, inclusive),
but sometimes include the control characters as well.  Each glyph is 7 pixels
wide by 8 pixels high, with one bit per pixel, so each glyph is 8 bytes.  The
high bit in each byte is usually zero, but can be set to 1 to cause a
half-pixel shift.

The glyphs are stored linearly: all 8 bytes for the first glyph, starting with
the top row, then all 8 bytes for the second glyph.

Apple /// fonts use a similar layout, but define a special purpose for the high
bit of each byte.  The following explanation is from the file "APPENDIX.D" on
the Washington Apple PI "CustomFONT Manual"
[disk set](https://www.apple3.org/Software/wap/html/a3fonts.html).

---
Appendix D: Technical Reference

How Character Sets are Stored

Each character is stored as a 7-by-8 bit array.  Bits corresponding to
pixels which are "ON" are set with a value of one;  background bits are set
to zero.  The 7-by-8 bit array is stored as eight consecutive bytes - one byte
for each row starting with the top row.  Within each of the eight bytes, bit 0
corresponds to the lest-most pixel.

Bit 7 determines how each row of the character is displayed in inverse
mode. If bit 7 is set to zero, the foreground and background will be exchanged
when inverse mode is on.  If all high bits are set to one, the character will
flash when inverse mode is on.  For more information on making characters
flash, see Chapter 8.

Every character set fontfile is stored as a three block data file (two
blocks for data and a third block for system information). The character set
used as the "system character set" is defined in the file "SOS.DRIVER" on any
boot disk.  The system character set can be changed using the Apple /// System
Utilities as detailed in Chapter 9 of this manual.

---
