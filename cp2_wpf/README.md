# CiderPress II Utility (GUI) #

If you've used the original CiderPress, most of what CiderPress II does will be familiar.  This
is a quick bit of documentation about new behavior.  Proper documentation will be added later.

The initial window has an "open file" button and, eventually, buttons for the two files that
were opened most recently.

## Work Area ##

Once you've opened a file, the screen divides into four windows: a hierarchical view of the
file contents, a hierarchical view of the directory structure, a file list / info panel, and
the add/extract/import/export options panel.

### File Tree ###

The top-left window holds a hierarchical view of the open file.  For a simple file archive this
might only have one entry.  For a simple disk image it will often have two: one for the disk image
(WOZ, 2IMG, etc.) and one for the filesystem (DOS, ProDOS, HFS).  If the disk has a custom
filesystem or nibble format, the filesystem entry won't appear.  If the disk image or archive
contains other disk images or archives, those may be added to the tree, depending on the settings.

The file tree view is populated based on the "Auto-Open Depth" setting:

 - Shallow: only open the top-level disk image or archive automatically.
 - Sub-Volume: descend into multi-partition disk images and ZIP archives.
 - Max: descend into everything until we hit bottom.

If you double-click on a disk image or file archive in the file list, you will jump to that
entry in the file tree.  If it wasn't already in the tree, it will be added.

There is no double-click path to scan for hybrid disks and embedded volumes.  To open those,
right-click on a filesystem entry and select the "Scan for Sub-Volumes" menu item.

### Directory Tree ###

The bottom-left window holds a hierarchical view of the filesystem, if it exists.  Only ProDOS
and HFS disks will have more than one entry here.  Selecting a directory in the tree will
select that in the file list.

### File List / Info Panel ###

The middle window holds the file list or information panel.  There are three possible views:

 - Full-file directory list.  This is the default for file archives and non-hierarchical
   filesystems.
 - Single-directory list.  This is the default for hierarchical filesystems, and is not
   available for anything else.
 - Info panel.  This is the default for disk images and multi-partition layouts, which don't
   have files.

You can switch between the views with buttons on the toolbar.

The info panel changes based on what is selected:

 - Disk image: notes list, metadata list (WOZ and 2IMG).  (Editing of metadata will happen here.)
 - Filesystem: notes list.
 - File archive: notes list.
 - Multi-partition layout: partition map.  Double-clicking on a map entry will select that
   entry in the file tree.
 - Partition header: nothing of value.

The "notes list" is a collection of messages generated while the structure in question was being
analyzed.  If an entry is shown in the file tree with a warning or error icon, the cause of the
problems will be reported here.

### Options Panel ###

Options that affect add, extract, import, and export, including drag & drop operations.

The panel can be minimized by clicking the show/hide button.


## Add, Extract, Import, Export ##

There are four distinct operations for adding and extracting files:

 - Add: add a file to an archive without modification.  Attempt to restore
   file attributes from saved metadata.
 - Extract: extract a file from an archive without modification.  Attempt to
   preserve file attributes.
 - Import: add a file to an archive, converting its format.  For example,
   the end-of-line markers in a text file might be changed from CRLF to CR,
   or an Applesoft BASIC program could be converted from a text file to
   tokenized form.
 - Export: extract a file from an archive, converting it to something new.
   This could be a simple adjustment to a text file, or a conversion from
   Apple II hi-res to GIF or PNG.

Utilities such as NuLib2 and the original CiderPress blend the operations
together, which can lead to some ambiguous behavior.
