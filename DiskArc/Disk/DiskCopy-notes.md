# Apple DiskCopy File Format #

## Primary Sources ##

- Apple II File Type Note $e0/8005 (DiskCopy disk image)
- https://www.discferret.com/wiki/Apple_DiskCopy_4.2

## General ##

The DiskCopy disk image format was developed by Apple for internal use in duplicating and
distributing 3.5" disk images.  The version described here is for DiskCopy v4.2.

On the Apple II, the files should use type $e0/8005.  On the Mac, type 'dImg', usually with
creator 'dCpy'.  On systems without file types, these were usually ".image", ".img", ".dc",
".dc42", or sometimes ".dsk".

## File Structure ##

DiskCopy files have both data and resource forks, although the resource fork isn't strictly
necessary and is often discarded when images are distributed.  The format stores the 512-byte
blocks as well as the 12-byte "tag" data stored on Macintosh 3.5" disks.  The tag data was
important for MFS but not used for HFS, so images with nonzero tag data are uncommon.  There is
no tag data for 720KB or 1440KB MFM disks.

The format uses a custom checksum algorithm, calculated on the data and tag areas, that must be
updated whenever the disk contents are altered.

Files have three parts: header, user data, and tag data.  All integer values are in big-endian
order.

The header is:
```
+$00 /64: diskName - disk description string, preceded by length byte (assume Mac OS Roman)
+$40 / 4: dataSize - length of the user data, in bytes (must be a multiple of 512)
+$44 / 4: tagSize - length of the tag data, in bytes (must be a multiple of 12; may be zero)
+$48 / 4: dataChecksum - checksum of the userData area
+$4c / 4: tagChecksum - checksum of the tagData area
+$50 / 1: diskFormat - 0=400KB, 1=800KB, 2=720KB, 3=1440KB; other values reserved
+$51 / 1: formatByte - $12=400KB, $22=800KB Mac, $24=800KB IIgs
+$52 / 2: private - must be $0100
+$54 / n: userData - data blocks for the disk
+xxx / n: tagData - tag data for the disk, if present
```

The exact set of values for `formatByte` are debatable -- the Mac 400KB disk should probably
be $02 rather than $12 -- but the exact value doesn't seem to be critical.  Some additional
research into the subject can be found in the [nibble-notes](Nibble-notes.md) document.

The tag checksum must be zero if no tag data is present.  A note on the discferret site says that
the first 12 bytes of the tag data are not included in the checksum, to maintain backward
compatibility with an older version of DiskCopy.  Some experiments with old disk images
confirmed this behavior.
