# Paintworks Animation #

File types:
 - ANI ($c2) / $0000: animation file

Primary references:
 - Reverse engineering, primarily by Antoine Vignau
   (https://docs.google.com/document/d/11uAvoMm9SVFfMZCuU71cRhq5zKAH6xfaAckdXMyFGqw/)

## Overview ##

The file contains an uncompressed super hi-res image, followed by a series of variable-length
animation frames.  Each frame is a collection of offset/value pairs that change the value of
one location.

The inter-frame delay is specified globally, expressed as a whole number of VBLs (16.67 ms at
60Hz, or 20ms at 50Hz).  This means the animation will run at different speeds on NTSC and PAL
systems.  A commonly available viewer for the IIgs allowed the animation to be played faster
or slower.

Animations are usually played in a loop, so the animation will generally want to return the
image to its original state at the end of the sequence.

## File Structure ##

All multi-byte values are stored in little-endian order.

The overall file structure is:
```
+$0000 /32768: SHR image in $c1/0000 format
+$8000 / 4: length of animation data that follows header (file length minus $8008)
+$8004 / 2: frame delay, in VBLs
+$8006 / 2: ? (usually $00c0 or $00c1)
+$8008 /nn: animation data chunks
```

A common value for the frame delay is 4, yielding 15 fps on a 60Hz system.

Each animation data chunk is:
```
+$00 / 4: total length of frame data, including this length value
+$04 /nn: animation frame data
```

There is no count on the number of frames.  When playing the animation, the animation chunk length
is used to determine when the end has been reached.  Most files have a single animation data chunk,
so the animation chunk length should be exactly the same as the total length in the file header.

Some modern creation tools don't set the length to the actual value, e.g. it will be $00000004
(which would be a length of zero).  In such cases, the full-file length should be used.

The animation frame data consists of pairs of 16-bit values:
```
+$00 / 2: offset to value, in the range [$0000, $7ffe]
+$02 / 2: 16-bit value to store
```

Any value in the SHR image data can be changed, including the palette and SCB bytes.  This can
be used to perform palette-cycling animations.

The end of each frame is indicated by an offset of $0000.  (Consequently, the pixels at the
top-left corner of the screen cannot be modified.)  The associated value is not used, and appears
to be garbage.

The last pair in the animation data should be an end-of-frame indicator.  It's unclear what should
happen if it isn't.
