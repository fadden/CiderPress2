# The Main Window

When a file is open, the CiderPress II window is divided into several regions. This
page describes each one.

```
+------------------------------------------------------------------+
|  Menu bar                                                        |
+------------------------------------------------------------------+
|  Toolbar:  [list views] | [reset sort] [up] | Drag&Copy mode | ? |
+----------------------+--------------------------------+----------+
|  Archive Contents    |                                |          |
|  (container tree)    |        File List  /  Info      | Settings |
+----------------------+         (center panel)         |  panel   |
|  Directory           |                                | (right)  |
|  (folder tree)       |                                |          |
+----------------------+                                |          |
|  [Find panel]        |                                |          |
+----------------------+--------------------------------+----------+
|  Status bar:  Ready           (center)         (counts/right)    |
+------------------------------------------------------------------+
```

The vertical dividers between panels are **splitters**; drag them to resize. The
left side also has a horizontal splitter between the Archive Contents and Directory
trees.

---

## Menus

- **File**: New Disk Image, New File Archive, Open, Open Physical Drive, Close,
  Recent Files, Exit.
- **Edit**: Copy, Paste, Find, Select All, and **Settings…**.
- **Actions**: every operation you can perform on the selected item(s): View, Add,
  Import, Extract, Export, Delete, Test, Rename/Edit Attributes, Create Directory,
  and disk-level actions (sector/block editing, Save As Disk Image, Replace Partition
  Contents, Scan for Bad Blocks, Scan for Sub-Volumes, Defragment, Close File Source).
- **View**: switch the center panel between Full List, Directory List, and
  Information.
- **Navigate**: Go To Parent Directory and Go To Parent.
- **Tools**: ASCII Chart.
- **Help**: open the manual, and About.
- **DEBUG**: hidden unless enabled in Settings; developer/diagnostic tools.

> **On macOS**, these appear in the system menu bar at the top of the screen, using
> native macOS menus. On Windows and Linux the menu bar sits inside the window.

Most actions also appear on the **right-click context menu** in the file list and in
the trees. Inappropriate actions are dimmed (for example, you can't sector-edit a
file archive).

---

## Toolbar

From left to right:

| Button | Action |
|---|---|
| List view | Show the **full file list** (all files, with pathnames) |
| Directory view | Show **a single directory's** contents |
| Information | Show the **Info** panel for the selected item |
| Reset Sort | Return the file list to its natural (stored) order |
| Up arrow | **Go To Parent**: move up one level |
| **Drag & Copy mode** | Choose **Add/Extract** or **Import/Export** for drag and clipboard transfers |
| ? (Help) | Open this manual in your browser (also **F1**) |

The first three buttons highlight to show which view is active.

---

## Archive Contents (top-left tree)

This tree shows the hierarchy of containers in the file you opened. A simple file
archive has a single entry; a disk image usually has two: one for the image file
itself, one for its filesystem.

There are five kinds of entries:

- **File archive**: ZIP, ShrinkIt, etc. A flat list of files, no directories.
- **Disk image**: DiskCopy, 2IMG, WOZ, etc. The image *file*, which may carry
  editable metadata or comments.
- **Multi-partition layout**: APM, UniDOS, etc. A disk holding several partitions.
- **Partition**: one partition within a multi-partition layout.
- **Filesystem**: DOS, ProDOS, HFS, etc. The actual files live here. Hierarchical
  filesystems populate the Directory tree below.

**Click** an entry to focus it; the other panels update to match. **Right-click** for
a menu of actions. Icons next to an entry indicate read-only status or problems.

If you open a disk image or archive *inside* the current one (by double-clicking it
in the file list), it's added to this tree automatically. To put it away, right-click
it and choose **Close File Source**.

> Some disks use custom filesystems CiderPress II can't recognize. As long as the
> disk image format itself is valid, the image still opens, though it won't have a
> filesystem entry.

---

## Directory (bottom-left tree)

For ProDOS and HFS filesystems, this shows the folder tree, with the volume directory
at the top labeled with the volume name. For flat filesystems and file archives it
holds a single placeholder entry.

**Click** a folder to select it; the file list updates. **Right-click** for actions.

---

## Center Panel: File List / Info

What appears in the center depends on what's selected in the Archive Contents tree.

### File list (filesystems and file archives)

A sortable, resizable table of files. Columns appear or hide depending on context:

| Column | Meaning |
|---|---|
| Status (`?`) | Blank for normal files. A speech-bubble icon means the entry has a comment. A yellow triangle marks a **dubious** entry (problems found; cannot be modified). A red marker means the file is too **damaged** to read. |
| Filename / Pathname | Flat filesystems show *Filename*; archives show *Pathname*. Hierarchical filesystems show one or the other depending on the view mode. |
| Type | ProDOS or HFS file type, whichever fits best. |
| Auxtype | ProDOS auxiliary type, or HFS creator. |
| Mod Date | Last-modified date, shown in local time. |
| Data Len | Length of the data fork. |
| Raw Len | "Raw" data length (only for DOS 3.2/3.3). |
| Data Fmt | Storage/compression format (only for file archives). |
| Rsrc Len | Resource-fork length (only where forks are supported). |
| Rsrc Fmt | Resource-fork storage format (only for forked archives). |
| Total Size | Combined storage used. Units vary by filesystem (DOS in 256-byte sectors, ProDOS in 512-byte blocks). |
| Access | ProDOS access flags: **D**elete, re**N**ame, **W**rite, **R**ead, **B**ackup, **I**nvisible. |

**Sorting:** click a column header to sort; click again to reverse. The **Reset Sort**
toolbar button restores the stored order. Your chosen sort is now **preserved across
refreshes**; adding, deleting, or renaming files no longer scrambles the order.

**Column widths** are saved in your settings between sessions.

**Double-click** behavior:
- A **directory** entry selects that folder in the Directory tree.
- A **file** opens in the [file viewer](viewing-files.md).
- A file that *looks like* an archive or disk image is opened as one and added to the
  Archive Contents tree.

When you select a file in the full list view, the Directory tree now highlights the
folder that contains it, keeping the two panels in sync.

### Three view modes

Switch with the toolbar buttons or the **View** menu:

1. **Full List**: every file in the filesystem, with full pathnames.
2. **Directory List**: only the files in the currently selected folder.
3. **Information**: replaces the list with details about the filesystem. If a
   warning/error icon appears beside a filesystem in the tree, this panel explains
   what was found (see "Notes" below).

File archives are always Full List; flat filesystems are always Directory List.

### Info panel (disk images, partitions, multi-partition layouts)

When you select a disk image or partition, the center panel shows:

- **Geometry and layout statistics** about the disk.
- **Disk / Partition Utilities** buttons: quick access to Edit Sectors, Edit Blocks,
  Save As Disk Image, Replace Partition Contents, and Scan For Bad Blocks.
- **Metadata**: for formats with metadata fields (WOZ, 2IMG, DiskCopy 4.2). If a
  field name isn't grayed out, double-click its row to edit it; any restrictions are
  shown in the edit window. You can also add new metadata entries where supported.
- **Partition Layout**: for multi-partition disks, a table of partitions.
  Double-click a row to jump to that partition (and its filesystem, if any).
- **Notes**: warnings and errors raised by the filesystem scanner, ranked by
  priority.

---

## Right Panel: Settings

The rightmost panel holds the options used when adding, extracting, importing, and
exporting files: preservation modes, compression, path stripping, conversion
choices, and so on. These are explained in
[Add / Extract / Import / Export](add-extract-import-export.md).

Collapse or expand it with the **Show/Hide Settings** button along its edge.

---

## Status Bar and Toasts

The bottom status bar shows a **Ready** indicator on the left, contextual text in the
center, and selection/size counts on the right.

Brief confirmations (for example, after a copy) appear as a **toast** notification
near the bottom-center of the window and fade away on their own.

---

## Navigation Shortcuts

- **Go To Parent Directory** moves the Directory tree selection up one level, stopping
  at the volume directory.
- **Go To Parent** does the same, but once it reaches the top of the Directory tree it
  continues upward in the Archive Contents tree. The up-arrow toolbar button performs
  this action (**Alt+Up**).

### Common keyboard shortcuts

| Shortcut | Action |
|---|---|
| Enter | View selected file(s) |
| Alt+Enter | View in a new viewer window |
| Ctrl/⌘+O | Open |
| Ctrl/⌘+N | New disk image |
| Ctrl/⌘+W | Close |
| Ctrl/⌘+C / Ctrl/⌘+V | Copy / Paste |
| Ctrl/⌘+F | Find file |
| Ctrl/⌘+A | Select all |
| Ctrl+Shift+A | Add files |
| Ctrl/⌘+E | Extract files |
| Delete | Delete files |
| Ctrl+Shift+N | Create directory |
| Ctrl+I | Toggle the Info panel |
| Alt+Up | Go to parent |
| Ctrl+Shift+1…6 | Open recent file 1–6 |
| F1 | Help |

*(On macOS, ⌘ is used in place of Ctrl for the standard shortcuts.)*

---

## Finding Files

Press **Ctrl/⌘+F** (or **Edit → Find**) to open the **Find** panel at the bottom of
the left side. Type a filename fragment and use the **▶ / ◀** buttons (or Enter /
Shift+Enter) to jump to the next or previous match. Check **Current archive only** to
limit the search to the active container instead of searching across everything open.
Press **Escape** or the **✕** to close the panel.

This finds *files by name* within what you have open. To search for *text inside* a
file, use the find box in the [file viewer](viewing-files.md).
