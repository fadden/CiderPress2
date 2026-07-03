# Getting Started

This page walks you through installing CiderPress II, launching it for the first
time, and opening an image. If you already know your way around, skip ahead to
[The Main Window](main-window.md).

---

## Installing

CiderPress II runs on **Windows, macOS, and Linux**. It is built on .NET and the
Avalonia UI toolkit, so the same application works the same way on all three
platforms.

See the project's [Install.md](../Install.md) for platform-specific download and
setup instructions. In brief:

- **Windows**: unzip the release and run the executable. No installer is required.
- **macOS**: run the application bundle. You may need to allow it the first time in
  *System Settings → Privacy & Security*.
- **Linux**: run the executable from the extracted folder. CiderPress II runs as an
  X11 client (including under XWayland on Wayland desktops). A `.desktop` launcher
  may be provided in a future release.

> The command-line tool (`cp2`) is distributed and documented separately. Nothing on
> this page applies to it.

---

## The Launch Screen

When you start CiderPress II with no file open, you'll see a clean **launch screen**
showing the program name and version, with a few large buttons in the middle:

- **Create new disk image**: start a brand-new, empty disk image.
- **Create new file archive**: start a new, empty file archive (ZIP, ShrinkIt, etc.).
- **Open file**: browse for an existing image or archive.
- **Recent file** buttons: if you've opened files before, the most recent ones
  appear here for one-click reopening.

All of these are also available from the **File** menu.

---

## A Quick First Tour

Once a file is open, the window splits into a multi-panel layout:

1. **Top-left, Archive Contents:** a tree of the containers found (the image file,
   its filesystem, any nested archives or partitions).
2. **Bottom-left, Directory:** the folder hierarchy of the selected filesystem.
3. **Center, File List / Info:** the files in the current location, or disk
   information when a disk/partition is selected.
4. **Right, Settings panel:** options that control add/extract/import/export. It can
   be collapsed with the **Show/Hide Settings** button.

To look inside a file, **double-click it** (or select it and press **Enter**). To go
back to a parent directory, use the **up-arrow** toolbar button or the **Navigate**
menu.

The full layout is described in [The Main Window](main-window.md).

---

## Choosing a Theme

CiderPress II ships with **Light** and **Dark** themes, plus a **System Default**
option that follows your OS appearance. Set it under **Edit → Settings → General →
Theme**. See [Settings](settings.md).

---

## Next Steps

- [The Main Window](main-window.md): understand every part of the interface.
- [Viewing Files](viewing-files.md): read documents and view graphics with on-the-fly
  conversion.
- [Add / Extract / Import / Export](add-extract-import-export.md): move files in and out.
