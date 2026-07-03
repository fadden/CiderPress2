# Frequently Asked Questions

**Q: How do I convert a disk image to a different disk image format?**
A: Open the image, select it in the Archive Contents tree, and click **Save As Disk
Image** in the center panel (or use the Actions menu). Choose the target format. See
[Disk Operations](disk-operations.md#save-as-disk-image).

**Q: Why is my random-access DOS text file truncated? / Why can't I view an Applesoft
program on a DOS 3.3 disk?**
A: Check the **Raw** setting. It must be *enabled* when viewing random-access text files,
and *disabled* when viewing BASIC programs. In the file viewer, use the **Open raw (DOS
3.x only)** checkbox.

**Q: Which platforms does the desktop app run on?**
A: Windows, macOS, and Linux, from the same Avalonia-based codebase. See
[Getting Started](getting-started.md#installing).

**Q: How do I switch to a dark theme?**
A: **Edit → Settings → General → Theme**, then choose Dark (or System Default to follow
your OS). See [Settings](settings.md#theme).

**Q: Where are my settings stored?**
A: In `CiderPress2-settings.json`, in the same directory as the executable. Delete it to
reset everything to defaults. See [Settings](settings.md#where-settings-are-stored).

**Q: What's the difference between Add/Extract and Import/Export?**
A: Add/Extract transfer files faithfully (no conversion); Import/Export convert the
contents (e.g. hi-res → PNG, or BASIC tokenization). See
[Overview](overview.md#addextract-vs-importexport).

**Q: I accidentally opened a file as a disk image / archive. How do I close it?**
A: Right-click its entry in the Archive Contents tree and choose **Close File Source**.

**Q: How do I get into a deeply nested archive (an archive inside a disk inside a ZIP)?**
A: CiderPress II opens nested containers directly. Raise the **Auto-Open Depth** setting
if a level isn't opening automatically, or double-click the entry to open it. See
[Overview](overview.md#nested-archives-turduckens).

**Q: Drag-and-drop to my desktop isn't working on Linux. What do I do?**
A: Use **Copy/Paste** (Ctrl+C / Ctrl+V) or the **Actions** menu commands, which always
work. See the [Linux notes](drag-drop-clipboard.md#linux-notes).

**Q: Is this the same as the `cp2` command-line tool?**
A: No. `cp2` is a separate, text-based program with its own
[Command-Line Manual](../docs/Manual-cp2.md). It shares the same underlying libraries
but is documented independently.

**Q: How do I report a bug?**
A: Enable the DEBUG menu in Settings, then include the **System Info** dump and the
**Debug Log** (use *Copy to Clipboard*) with your report. See
[Tools & Extras](tools.md#the-debug-menu). 
