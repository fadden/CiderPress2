# GUI Tests #

## Selection and Focus ##

- Select archive tree in multi-member archive.  Use arrows to traverse entire tree.

- Select directory tree in disk image with multiple directories with contents.  Use arrows
  to traverse entire tree.

- Create a directory.  Selection should appear on new directory in file list.

- Delete a file.  Selection should move to previous item.
- Delete multiple files.  Selection should move to item before first item.

- Rename a file on an HFS volume so that it changes position.  Selection should stick.
- Repeat with filename column sorted in descending order.

- Double-click on a directory in the file list.  Directory tree selection should change.
  In single-dir view mode, focus should be on [?].

## Edit Attributes ##

- Set file types on all kinds of files.  Noteworthy:
  - DOS has limited set of types.
  - NuFX, AppleSingle, MacZip, and ProDOS "extended" files can do ProDOS and HFS types
	independently.
