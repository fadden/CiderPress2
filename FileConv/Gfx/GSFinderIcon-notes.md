# Apple IIgs Finder Icon File #

File types:
 - ProDOS ICN ($CA)

Primary references:
 - Apple II File Type Note $ca/xxxx, "Finder Icons File"
 - _Apple IIgs Toolbox Reference, Volume 2_, chapter 17 "QuickDraw II Auxiliary", p.17-3

Icon files are loaded by the Finder, from the `Icons` directory on mounted volumes.  On older
versions of the system software, the file `Finder.Icons` was required to exist on the boot disk.

## File Structure ##

The icon file includes small and large versions of each icon, as well as metadata used to
determine which icon to use with which files.  Patterns for matching on the file type,
auxiliary type, and filename are handled.

All multi-byte integers are in little-endian order.

The icon file has some empty holes that are expected to be filled in by the Finder after being
loaded into memory.
```
+$00 / 4: iBlkNext: (zero on disk) handle to next icon file
+$04 / 2: iBlkID: ID number of icon file, must be $0001 for Finder icons
+$06 / 4: iBlkPath: (zero on disk) handle to pathname of icon file
+$0a /16: iBlkName: name of the icon file (ASCII, prefixed with length byte)
+$1a /nn: array of icon data records
```

Each icon data record is:
```
+$00 / 2: iDataLen: length of this icon data record, or zero if this is the end of the array
+$02 /64: iDataBoss: pathname of application that owns this icon (ASCII, prefixed with length byte)
+$42 /16: iDataName: filename filter, may include wildcard (e.g. `*.ASM` matches all .ASM files)
+$52 / 2: iDataType: file type filter, zero matches all files
+$54 / 2: iDataAux: auxiliary type filter, zero matches all files
+$56 /nn: iDataBig: normal size icon image data
+$xx /mm: iDataSmall: small size icon image data
```

The icon image data is defined by the QuickDraw II Auxiliary tool set, which has the `DrawIcon`
call.  The structure is:
```
+$00 / 2: iconType: bit flags; bit 15 is set for color, clear for black & white
+$02 / 2: iconSize: number of bytes in icon image
+$04 / 2: iconHeight: height of icon, in pixels
+$06 / 2: iconWidth: width of icon, in pixels
+$08 /nn: icon image, 4 bits per pixel; each row is `1 + (iconWidth - 1) / 2` bytes wide
+$xx /nn: icon mask, same dimensions as image
```
The `iconType` field declares whether the icon's contents are color or black and white, but in
practice most icons (including those for the icon editor DIcEd) have a type of $0000, even when
they're clearly intended to be colorful.  It appears that it could affect the way the icon is
drawn in different modes, e.g. when selected in the Finder.

## Rendering ##

Icons drawn by QuickDraw II use a 16-bit `displayMode` word:
```
15-12: background color to apply to white part of B&W icons
 11-8: foreground color to apply to black part of B&W icons
  7-3: reserved, set to zero
    2: offLineBit: if 1, AND a light-gray pattern to image (e.g. to show a device as being offline)
    1: openIconBit: if 1, copy a light-gray pattern instead of the image
    0: selectedIconBit: if 1, invert the image before copying
```
If bits 15-8 are all zero, color is not applied to B&amp;W icons.

Icons need to look good for all permutations of offline/open/selected, so icon editors would
display all 8 possible situations.
