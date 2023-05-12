# ZIP Archive #

## Primary References ##

- APPNOTE.TXT (http://www.pkware.com/appnote)
- https://en.wikipedia.org/wiki/ZIP_(file_format)

## General ##

The ZIP archive file format is one of the most widely used and documented formats in existence.

It's worth noting that the format was designed for efficiency in situations where a single
archive was spread across multiple floppy disks.  Replacing or deleting an entry from such an
archive is best done by adding to the end of the archive, rather than rewriting the entire thing,
so the contents of an archive are defined by the central directory stored at the end of the file.
Walking through a ZIP file from start to finish would normally be done only by recovery software,
as doing so could encounter deleted or stale copies of files.

## File Layout ##

Ignoring some less-commonly used features, files look like:
```
(optional stuff, e.g. a self-extraction executable)
local file header 1
file data 1
local file header 2
file data 2
  ...
local file header N
file data N
central directory header 1
central directory header 2
  ...
central directory header N
end of central directory record
```

## Structure ##

All integer values are unsigned, stored in little-endian order.

Local file header:
```
+$00 / 4: signature (0x50 0x4b 0x03 0x04)
+$04 / 2: version needed to extract
+$06 / 2: flags
+$08 / 2: compression method
+$0a / 2: modification time
+$0c / 2: modification date
+$0e / 4: CRC-32 of uncompressed data
+$12 / 4: compressed size (0xffffffff for ZIP64)
+$16 / 4: uncompressed size (0xffffffff for ZIP64)
+$1a / 2: filename length
+$1c / 2: "extra field" length
+$1e /nn: filename data
+$xx /yy : extra field data
```

This is immediately followed by the file data.

If the archive was created by writing to a stream, the CRC and file sizes may not have been known
at the time the data was being written.  If bit 3 in the flags is set, the CRC and size fields
will be zero, and the actual values will be stored in a "data descriptor" immediately following
the data.  (While it's possible to *create* a ZIP archive as a stream, it's not always possible
to *read* it as a stream, and because of the central directory arrangement it's a bad idea to try.)

The data descriptor is usually:
```
+$00 / 4: signature (0x50 0x4b 0x07 0x08) (might be missing)
+$04 / 4: CRC-32 of uncompressed data
+$08 / 4: compressed size (will be 8 bytes for ZIP64)
+$0c / 4: uncompressed size (will be 8 bytes for ZIP64)
```

The central directory comes after the data for the last file.  The central directory
header is a superset of the local file header, containing:
```
+$00 / 4: signature (0x50 0x4b 0x01 0x02)
+$04 / 2: version made by
+$06 / 2: version needed to extract
+$08 / 2: flags
+$0a / 2: compression method
+$0c / 2: modification time
+$0e / 2: modification date
+$10 / 4: CRC-32 of uncompressed data
+$14 / 4: compressed size (0xffffffff for ZIP64)
+$18 / 4: uncompressed size (0xffffffff for ZIP64)
+$1c / 2: filename length
+$1e / 2: "extra field" length
+$20 / 2: file comment length
+$22 / 2: disk number where file starts
+$24 / 2: internal file attributes
+$26 / 2: external file attributes
+$2a / 4: relative file offset of local file header
+$2e /nn: filename data
+$xx /nn: extra field data
+$yy /nn: file comment data
```

Generally speaking, the values of fields in the local and central headers will be identical.  The
central directory CRC and size fields will be set correctly even if the archive was created by
writing to a stream (which means the data descriptor can generally be ignored).

The end-of-central-directory record (EOCD) appears at the end of the archive:
```
+$00 / 4: signature (0x50 0x4b 0x05 0x06)
+$04 / 2: number of this disk
+$06 / 2: disk where central directory starts
+$08 / 2: number of central directory records on this disk
+$0a / 2: total number of central directory records
+$0c / 4: size of central directory, in bytes (0xffffffff for ZIP64)
+$10 / 4: relative offset of start of central directory
+$14 / 2: archive comment length
+$16 /nn: archive comment data
```

The only way to find the EOCD is to start scanning backward from the end of the file until the
signature is found.  If the comment happens to include the signature bytes, hilarity ensues.

### Filenames and Comments ###

Filenames may be partial pathnames, with components separated by slashes ('/').  If a file was
added from standard input, the filename will be an empty string.

The default character set for filenames and comments is IBM Code Page 437
(https://en.wikipedia.org/wiki/Code_page_437).  Specification 6.3.0 added a second option: if
flag bit 11 ("language encoding flag" or "EFS") is set, filenames and comments in that record
are encoded with UTF-8.  There is no flag for the archive comment in the EOCD record, however.

The standard doesn't require filenames to be unique, and makes no mention of whether filename
comparisons should be case-insensitive.  Since ZIP may be used on UNIX systems, case-sensitive
comparisions should be used when checking for duplicates.

Directories may be stored explicitly, though this is not required.  ZIP records have an "external
attribute" value that may include an "is directory" flag, e.g. MS-DOS has FILE_ATTRIBUTE_DIRECTORY
(https://learn.microsoft.com/en-us/windows/win32/fileio/file-attribute-constants), but such a flag
may not be present for all supported operating systems.  A more general approach is to check for
zero-length files whose names end with '/'.

### Date/Time Storage ###

Date/time are stored in MS-DOS format, which uses 16-bit little-endian values:
```
  date: YYYYYYYMMMMDDDDD
  time: hhhhhmmmmmmsssss
```
where:
```
  YYYYYYY - years since 1980 (spans 1980-2107)
  MMMM - month (1-12)
  DDDDD - day (1-31)
  hhhhh - hour (1-23)
  mmmmmm - minute (1-59)
  sssss - seconds (0-29 * 2 -> 0-58)
```

Time values are in local time.

## Extensible Data Fields ##

It is possible to store OS-specific and application-specific data in the "extra" field,
allowing metadata like file types and access permissions to be kept.  The blocks of data are
identified 16-bit tags that are defined by the ZIP specification maintainer.

As of specification 6.3.10, there are no definitions for HFS or ProDOS.  There are a few
definitions for Macintosh programs that could be useful for HFS files.

ZIP archives generally hold files archived from a host filesystem, rather than files copied
directly from a ProDOS volume or NuFX archive.  Defining the extra data fields is not useful
for Apple II files because ZIP utilities are rarely used on the Apple II itself.

## Miscellaneous ##

ZIP archives are designed so that they can be written to a stream, but aren't really intended
to be read from a stream.  The [sunzip](https://github.com/madler/sunzip) project can extract
a ZIP file from a stream, using various tricks to work around the issues.

The optional Data Descriptor field is not self-identifying.  Even if the signature is present,
you can't know if the data values are 32-bit or 64-bit without examining other parts of the
archive.  ("sunzip" makes various guesses at its form until the interpreted data values match
the uncompressed stream.)

The Mac OS port of the popular Info-ZIP utility appears to encode filenames as UTF-8 without
setting the appropriate flag, causing confusion when the archives are opened by other
applications (e.g. Windows Explorer).  Technically this is allowed, as the ZIP standard only
says that the filename SHOULD use CP437 if the flag isn't set.  It appears that Info-ZIP (as well
as 7-Zip) uses the OS identifier in the "version made by" field to determine the default
encoding, using UTF-8 when the system is $03 (UNIX).

Archives created on a Macintosh may have paired entries that start with "__MACOSX/".  See the
[AppleSingle notes](AppleSingle-notes.md) for more information.
