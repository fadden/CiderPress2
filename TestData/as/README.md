"badmac-utf8name.as"
Created with Mac OS "applesingle" tool in the period where little-endian
files were generated.  The file has entry types 3 (filename), 8 (file
dates), 9 (Finder info), 10 (Macintosh file info), and 1 (data fork).

"gshk.hfs.as":
Created with GS/ShrinkIt, from a file on an HFS filesystem.  GSHK output
the Home File System field as "ProDOS" despite coming from HFS.  The file
is version 1, and has entry types 7 (v1 file info), 4 (comment),
3 (filename), 2 (resource fork), and 1 (data fork).  The comment is just
200 bytes of zeroes.

"hello__.as":
Created with Mac OS "applesingle" tool.  v2 archive with entry types
3 (filename), 8 (file dates), 9 (Finder info), 10 (Macintosh file info),
and 1 (data fork).  Curiously, #10 is 8 bytes long, even though the
documentation only shows 4.  The Finder Info is zeroed.

"illegal-chars.as":
Created with Mac OS "applesingle" too".  v2 archive with entry types
3 (filename), 8 (file dates), 9 (Finder info), 10 (Macintosh file info),
1 (data fork), and 2 (resource fork).  The filename contains '/', '\',
and ':', and so should should cause problems on any filesystem that
supports subdirectories.

"MacIP.RES.as":
The file is part of Marinetti, which uses the LGPLv2 license.  It was
retrieved while working on https://github.com/fadden/ciderpress/issues/1 .
The archive is notable for being a minimal version 2 AppleSingle file,
with no filename -- just entry types 1 (data fork), 2 (resource fork), and
9 (Finder info).  The Finder info has a 'pdos' file type, and the data
fork is zero bytes long.
