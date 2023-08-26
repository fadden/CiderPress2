# Gutenberg Filesystem #

## Primary References ##

 - Reverse engineering, primarily by David Schmidt

## General ##

The Gutenberg word processor (reviewed [here](https://www.atarimagazines.com/creative/v9n6/64_Gutenberg.php))
is a somewhat obtuse program with some very advanced features.  For example, you could draw
graphics, insert them into a document, and flow columns around them.  The full version, and a
more limited version called Gutenberg, Jr., used a custom filesystem.

There are two types of files: programs and documents.  Fonts and non-resident commands are
considered programs, and use file type 'P'.  Documents use file type ' ', or 'L' if they have
been locked.

Disks have a volume name that can be up to nine characters long.  Filenames can be up to 12
characters, and may be any high-ASCII value other than slashes, spaces, and control characters.
When a file is deleted the entire entry is cleared to high-ASCII spaces, so deleted files can
be identified by checking the first byte of the filename.  (Having slashes in the volume name
is apparently allowed, since the program disks themselves do this.)

In the directory listing, sector zero is stored as $40, presumably because the directory
listing is displayed as a document and $00 has a special meaning.  This convention is not
followed in the six-byte sector header.  (Presumably track 0 would also be referenced this way,
but in practice no files are stored there.)

## Disk Structure ##

The filesystem used an unusual approach: every 256-byte sector is part of a doubly-linked list.
The first six bytes of every sector hold the track/sector numbers of the previous, current, and
next sector in the list.  The list is circular.  The high bit of the *next track* number is set
when it points to the first sector in the file, and the high bit of the *current sector* number
is set when the sector is the first in the file.

For example, consider a two-sector document that lives in track 3, sectors 1 and 3:
```
T3 S1:
  03 03 03 81 03 03 ...
T3 S3:
  03 01 03 03 83 01 ...
```
The self-reference in T3 S1, and forward-reference to T3 S1 from T3 S3, have the high bit set,
because traversing those links would take you to the start of the list.  The backward reference
doesn't have the high bit set, though it probably should.  (In some files, such as the directories
on the Gutenberg program disks, it looks like the backward reference in the first sector didn't
get updated when the file expanded.  Use backward links with caution.)

The first sector of the disk catalog lives in track 17, sector 7.  The first entry in the catalog
is itself, represented as a locked file called `DIR`.  Additional catalog sectors can be found
by traversing the linked list pointers.

Each catalog sector holds 15 entries, and looks like this:
```
+$00 / 1: previous track
+$01 / 1: previous sector
+$02 / 1: current track
+$03 / 1: current sector
+$04 / 1: next track
+$05 / 1: next sector
+$06 / 9: volume name, high ASCII padded with spaces
+$0f / 1: $8d (high-ASCII carriage return)
+$10 /16: entry #0
 ...
+$f0 /16: entry #14
```
The volume name is found in all catalog sectors.  The individual entries have the form:
```
+$00 /12: filename, high ASCII padded with spaces
+$0c / 1: file start track number
+$0d / 1: file start sector number
+$0e / 1: file type: high-ASCII ' ', 'L', or 'P'
+$0f / 1: $8d (high-ASCII carriage return)
```
Catalog sectors are initialized to $a0, with $8d every 16 bytes.  This matters when the `DIR`
file is loaded and displayed (which is how you view the directory from within the program).

The length of a file is not stored in the catalog.  The only way to determine it is to walk
through the file, and that only yields a multiple of 250.

The boot area, which spans the first 3 tracks, is not represented by a file.  Some of the sectors
seem to have the six-byte headers, and the program disk references files in that area.  On a
newly-formatted Gutenberg, Jr. data disk, the first file is created in T3 S0.

Track 17, sector 6 appears to hold a sector allocation bitmap.  The format details are unknown,
but allocations seem to start at track 3, and the last 12 bytes are unused.

### File Formats ###

Document files use an extended ASCII character set, and end when the first $00 byte is encountered.
The program supports custom character sets, so it's only possible to display the document
correctly if the character set can be identified.  "Normal" characters have the high bit set,
"alternate" characters have the high bit clear.  Only 0x80-0x9f are considered non-printing
control characters.  Open the file `FONTS.TABLE` on the Gutenberg, Jr. program disk to see the
full set of characters available.

The structure of "program" files is unknown.  Gutenberg executables generally begin with the bytes
`00 01 02 03 04 05 06 07 d3`, while Gutenberg, Jr.'s start with `00 01 02 03 04 05 06 07 08 d3`.
The following byte appears to hold flags.  This is then followed by 6502 code, or by the program
name between '/' characters and then the code.

Graphics and font files have a different structure.

The filesystem does not impose a maximum file length, though exceeding (((35 - 3) * 16) - 2) = 510
sectors is impossible on a 140KB floppy disk (unless the file dips into the boot tracks).
