# WOZ Disk Image Format #

## Primary References ##

- https://applesaucefdc.com/woz/reference1/ (version 1)
- https://applesaucefdc.com/woz/reference2/ (version 2)

## General ##

The WOZ format was developed by John K. Morris as a way to store the data coming from low-level
disk reading hardware, such as the [Applesauce](https://applesaucefdc.com/hardware/).  It stores
data as a bit stream, rather than a byte stream, allowing it to capture 10-bit self-sync bytes
and other low-level bit gymnastics.  It can also record track synchronization, making it very
effective for capturing the attributes of copy-protected media.

The format supports 5.25" and 3.5" floppy disks.  It has a number of features intended for use
by emulators, such as a boot sector format declaration and hardware compatibility flags.

## File Structure ##

After a 12-byte header, the file uses the common "chunk" format, where each section begins with
a 32-bit identifier followed by a 32-bit length.  To make parsing simpler, the first few chunks
have fixed lengths and file offsets, and some chunks have data references with absolute file
offsets.  The contents of some chunks (e.g. TMAP and TRKS) changed between version 1 and 2 without
alteration of the chunk identifier, so chunk-parsing is version-specific.  The position of the
FLUX chunk has a strict alignment requirement.  Two of the optional chunks are variable-length and
position-independent, so it's not possible to ignore the chunk headers entirely.

The disk image data is either stored as a bit stream, representing the bits as they would be seen
by the disk controller, or less commonly as flux transition timing data.  The amount of data
stored for each track is variable, though WOZ version 1 uses a fixed amount of file storage for
each track.

WOZ version 1 defines the following chunks:
 - header (12 bytes, includes signature and a full-file CRC32)
 - INFO (hdr +$0c, actual +$14, 60 bytes long) - best to use only INFOv1 fields
 - TMAP (hdr +$50, actual +$58, 160 bytes long)
 - TRKS (hdr +$f8, actual +$100, length is 6656 per track)
 - META (optional) - file metadata; some entries are required, some values are restricted

WOZ version 2 (re-)defines the following chunks:
 - header (signature updated)
 - INFO (hdr +$0c, actual +$14, 60 bytes long) - adds INFOv2 and INFOv3 fields
 - TMAP (hdr +$50, actual +$58, 160 bytes long) - unchanged for 5.25", reordered for 3.5"
 - TRKS (hdr +$f8, actual +$100, track storage lengths are variable multiples of 512) - each track
   has an 8-byte header that provides the starting block and length of the track data; actual
   track data starts at +1536 ($600)
 - FLUX (optional; 160 bytes long) - like TMAP, but for FLUX tracks; chunk must always be
   placed at the start of a 512-byte block within the WOZ file, with the offset stored in INFO
 - WRIT (optional) - variable-length chunk; useful when transferring back to physical media
 - META (optional) - file metadata; some entries are required, some values are restricted

The FLUX chunk's start offset must be 512-byte aligned.  The WOZ v2 TRKS track data begins at
+1536, and all tracks are stored in 512-byte blocks, so if FLUX immediately follows TRKS the
alignment will happen naturally.

(The WOZ documentation at https://applesaucefdc.com/woz/reference2/ defines everything nicely,
so the detailed contents of the fields are not replicated here.)

## Metadata ##

Metadata contents are rigidly defined for certain keys, e.g. the value for "Language" must come
from a specific set of strings.  All files should have the "standard" keys, and may include
additional keys as well.  The character set and syntax for keys isn't currently defined, other
than that the characters used for the structure (tab, linefeed, and pipe) are invalid.  Keys are
case-sensitive and must be unique.

It's safest to assume that keys must be in `[A-Z][a-z][0-9]_`, preferably in underscore_separated
form.  Values may be any valid UTF-8 string (no BOM), so long as tab/linefeed aren't used, and
pipe is only used as an item separator.  ASCII is recommended for best interaction with varied
hardware.

Anything that manipulates metadata in the META chunk should also be able to manipulate fields in
the INFO chunk.  Write Protected, Boot Sector Format, Compatible Hardware, and Required RAM may
reasonably be edited.  Other fields are set at creation time and should not be disturbed, as
doing so may prevent the file from being interpreted correctly.
