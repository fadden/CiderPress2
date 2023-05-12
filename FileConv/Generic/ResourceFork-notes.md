# Resource Forks #

Primary references:
 - _Apple IIgs Toolbox Reference, Volume 3_, chapter 45
 - IIgs TN #76, "Miscellaneous Resource Formats"
 - _Inside Macintosh: More Macintosh Toolbox_, starting on page 1-121
 - _Inside Macintosh, Volume I_, starting on page I-128

## Introduction ##

With the Apple Macintosh came the introduction of forked files.  Every file has a data fork,
with unstructured data, and a resource fork, with an array of typed information.  This allows
files to be bundled with structured data in a way that allows straightforward editing of
one without affecting the other.

Resource forks were introduced on the Apple IIgs with GS/OS v2.0 (System 4.0).  The format of
Apple II resource forks is similar to Macintosh resource forks, but they're not the same.

The various data structures were designed to be loaded into memory and used directly, so some
of them have space set aside for file and memory handles.  The structures may be written back
to disk with data in them, so fields labeled "zeroes" may have nonzero values.

## Apple IIgs Format ##

All multi-byte values are unsigned little-endian.

The file begins with the Resource File Header:
```
+$00 / 4: rFileVersion - version number for the entire file.  Always zero.  (Mac will be > 127.)
+$04 / 4: rFileToMap - offset, in bytes, of start of resource map
+$08 / 4: rFileMapSize - size, in bytes, of the resource map
+$0c / 128: rFileMemo - available for application data
```

This is followed by the resource map:
```
+$00 / 4: mapNext - zeroes (reserved for handle to next resource file)
+$04 / 2: mapFlag - control flags; really just a "dirty" bit when in memory
+$06 / 4: mapOffset - offset, in bytes, to resource map (same as rFileToMap when first loaded)
+$0a / 4: mapSize - size, in bytes, of resource map (same as rFileMapSize when first loaded)
+$0e / 2: mapToIndex - offset from start of map to start of `mapIndex` array
+$10 / 2: mapFileNum - zeroes (reserved for GS/OS file reference number)
+$12 / 2: mapID - zeroes (reserved for resource manager file ID for open resource file)
+$14 / 4: mapIndexSize - total number of resource reference records in `mapIndex`
+$18 / 4: mapIndexUsed - number of used resource reference records in `mapIndex`
+$1c / 2: mapFreeListSize - number of resource free blocks in `mapFreeList`
+$1e / 2: mapFreeListUsed - number of used resource free blocks in `mapFreeList`
+$20 / N: array of resource free blocks
+$xx / N: array of resource reference records
```

Resource Free Blocks describe contiguous areas of free space in the resource file.  Each
resource file has at least one resource free block, defining free space from the end of the
resource file to $ffffffff.  Free block records are:
```
+$00 / 4: blkOffset - offset, in bytes, to the free block from the fork start; zero if end of list
+$04 / 4: blkSize - size, in bytes, of the free block of space
```

Resource Reference Records hold control information about a resource.  Each record is defined:
```
+$00 / 2: resType - resource type; will be zero if this is the end of the list
+$02 / 4: resID - resource ID
+$06 / 4: resOffset - offset, in bytes, to the resource from the start of the resource file
+$0a / 2: resAttr - resource attributes
+$0c / 4: resSize - size, in bytes, of the resource
+$10 / 4: resHandle - zeroes (reserved for memory handle)
```

The `resAttr` field holds a 16-bit word with various attributes, mostly concerning how memory
for the resource should be allocated:
```
 15: attrLocked - 0=memory not locked, 1=memory locked
 14: attrFixed - 0=memory can be relocated, 1=memory must be fixed in place
 13: reserved - must be zero
 12: reserved - must be zero
 11: resConverter - 0=no converter needed, 1=converter routine required
 10: resAbsLoad - 0=load anywhere, 1=must be loaded at specific location
 9-8: attrPurge - purge level to pass to memory manager when resource allocated
  7: resProtected - 0=write-enabled, 1=write-protected
  6: resPreLoad - 0=do not preload, 1=preload resource at OpenResourceFile time
  5: resChanged - 0=not changed, 1=resource has been altered
  4: attrNoCross - 0=may cross bank boundary, 1=may not cross bank
  3: attrNoSpec - 0=may use special memory, 1=may not use special memory
  2: attrPage - 0=no alignment restriction, 1=memory must be page-aligned
  1: reserved - must be zero
  0: reserved - must be zero
```

Note: 2-byte resource type, 4-byte resource ID.

## Macintosh Format ##

All multi-byte values are signed big-endian.

The file begins with the 16-byte resource header and some reserved space:
```
+$00 / 4: offset from beginning of file to resource data
+$04 / 4: offset from beginning of file to resource map
+$08 / 4: length of resource data
+$0c / 4: length of resource map
+$10 / 112: reserved
+$80 / 128: available for application data
```

The resource data usually begins at +$0100.  Each resource is stored sequentially in a list:
```
+$00 / 4: length of resource data that follows
+$04 / N: resource data
```

The resource map follows the data, and has the form:
```
+$00 / 16: zeroes (reserved for copy of resource header)
+$10 / 4: zeroes (reserved for handle to next resource map to be searched)
+$14 / 2: zeroes (reserved for file reference number)
+$16 / 2: resource file attributes
+$18 / 2: offset from beginning of resource map to type list
+$1a / 2: offset from beginning of resource map to resource name list
+$xx / N: resource type list
+$yy / N: reference lists
+$zz / N: resource name list
```
The zeroed-out areas are filled in when the structure is loaded into memory.

The resource type list provides an index by type.  Each item specifies one resource type used in
the resource fork, the number of resources of that type, and the location of the reference list
for that type.  The list starts with a 16-bit value that holds the number of types in the map,
minus 1.  (IM:MMT page 1-123 incorrectly shows the value as being part of the map header, but it's
actually the first two bytes at the "offset to resource map type list".  The description in the
older _Inside Macintosh, Volume I_ is correct.)  Each entry is:
```
+$00 / 4: resource type (multi-character constant, e.g. "STR ").
+$04 / 2: number of resources of this type in map, minus 1
+$06 / 2: offset from beginning of resource type list to reference list for this type
```

Each resource type entry has a corresponding reference list.  Reference lists are contiguous,
and in the same order as the types in the resource type list.  There is one entry for each
resource of the given type, each of which should have a unique ID within that type:
```
+$00 / 2: resource ID
+$02 / 2: offset from beginning of resource name list to resource name
+$04 / 1: resource attributes
+$05 / 3: offset from beginning of resource data to data for this resource
+$08 / 4: zeroes (reserved for handle to resource)
```

Resource attributes are bit flags that specify how the resource should be loaded or indicate
the nature of the memory region:
```
  7: reserved
  6: resSysHeap - 1=read into system heap
  5: resPurgeable - 1=purgeable
  4: resLocked - 1=locked
  3: resProtected - 1=protected
  2: resPreload - 1=to be preloaded
  1: resChanged - 1=resource has been modified
  0: reserved
```

The 3-byte offset effectively limits resource forks to 16MB in size.

If a resource does not have a name, the offset to the resource name in the resource's entry in
the reference list is -1.  If it does have a name, the offset identifies the location of the
name's entry in the resource name list.  Each entry in the name list is a length byte followed
by character data.

(See IM:MMT page 1-125 for an illustration of a resource fork.)

Note: 4-byte resource type, 2-byte resource ID.
