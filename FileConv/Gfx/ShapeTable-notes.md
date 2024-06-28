# Applesoft Shape Table #

File types:
 - ProDOS BIN ($04) / DOS B, with correct structure

Primary references:
 - _Applesoft II BASIC Programming Reference Manual_, chapter 9 "High-Resolution Shapes"

Shape tables were widely used by Applesoft programs to draw simple shapes on the screen.  While
not terribly efficient, the implementation was built into the ROM, making them convenient.  The
format is essentially a series of vectors, allowing shapes to be scaled and rotated (crudely).

## File Structure ##

The file format is simply a shape table that has been BSAVEd.  This is similar to the cassette
tape format, which is a 16-bit table length followed by the table data.  While shape data must
be written manually to cassettes, it can be loaded with the Applesoft SHLOAD command.

The table format is:
```
+$00 / 1: number of shapes
+$01 / 1: (unused, could be any value)
+$02 / 2: offset from start of table to shape #1
+$04 / 2: offset from start of table to shape #2
 ...
+$xx /nn: shape #1 data
+$yy /mm: shape #2 data
 ...
```
Applesoft numbers shapes starting from 1, not 0.

When creating tables by hand, the offset table may have been created with more space than is
required to hold the number of shapes actually present, to leave room to add more without having
to resize the header.  The offsets of the unused entries may be zero or bogus values, and there
are often "holes" with unused space between shapes or at the end.  This makes detecting shape
table files tricky.  It's also possible (though unlikely) to have shapes that overlap.

The shape data is stored as a series of bytes.  Each byte holds three vectors, with the bits
defined as follows:
```
 7-6: vector C movement direction
   5: vector B plot flag
 4-3: vector B movement direction
   2: vector A plot flag
 0-1: vector A movement direction

Movement is:
  00: up
  01: right
  10: down
  11: left
```
When drawing, vectors are handled in the order A-B-C.  If the plot flag bit is set to 1, a point
will be plotted before the cursor is moved.  Note that vector C does not have a plot flag, so it
can only move without plotting.

If all bits are zero, the byte marks the end of the shape.  If C is zero then it is ignored, and if
B and C are both zero then both are ignored.

See page 93 in the Applesoft manual for an example.

### Shape Usage ###

Applesoft programs specify the shape to draw by number.  If the table has 10 shapes, the number
must be from 1-10, inclusive.  Applesoft will allow you to draw shape 0, but the code will
try to read the shape offset from +$00/01 in the file, which is unlikely to reference a valid
shape.

Shapes can be drawn with DRAW or XDRAW.  The latter toggles the current screen pixel value,
so it's best to avoid plotting the same point more than once.

While the shape data doesn't contain any color information, it's possible to create colored
shapes on the hi-res screen by drawing on alternating lines (although this falls apart when
scaling is used).  Further, the Applesoft routines that draw shapes will apply the current
HCOLOR value, masking pixels and setting the high bit.

### Tools ###

_Apple Mechanic_ and _Shape Mechanic_, published by Beagle Bros, have excellent tools for
creating and editing shape tables on an Apple II.  They allows you to draw or capture arbitrary
shapes that are then automatically converted into shape table format.  Both come with hi-res
fonts in shape table form.
