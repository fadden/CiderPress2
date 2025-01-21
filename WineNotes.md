# Wine Emulation Wrapper Notes #

The [Wine project](https://winehq.org/) provides a compatibility layer that
allows Windows applications to be executed on non-Windows operating systems,
including Linux and macOS.  It can be used to run the CiderPress II GUI
application, either by installing Wine as an application on the target
system, or by packaging the application in a Wine wrapper.

Getting it to work can be a little tricky.

## Linux ##

[ TBD ]

## macOS ##

Getting the Wine app installed isn't easy.  First, you need to install a
package manager.  Homebrew and MacPorts are suggested.  Then the instructions
on the wiki direct you to install "(selected wine package)" without giving
you any sense for what that is.  Fortunately, Homebrew offers up some
suggestions if you just enter "wine" (e.g. `wine-stable`).

As of early January 2025, the pre-built binary reported `wine-9.0` when
queried with `wine --version`.  This was released nearly a year earlier, and
does not work well with CiderPress II.  While Wine is updated regularly, the
pre-built macOS binary is not.

### Wineskin / Kegworks ###

Wrapping the application seems to work a bit better, and is more generally
useful because it allows people to download a fully functional configuration.
For macOS, the [Kegworks project](https://github.com/Kegworks-App/Kegworks)
provides a fairly user-friendly interface for this.  The chief drawback to
this approach is that the wrapper is well over 1GB.

As with Wine, getting started is a little bumpy.  The project page on github
has download instructions, again using Homebrew and MacPorts, but doesn't
tell you what to do next.  The installed program is not a command-line
utility, but rather a GUI app called "Kegworks Winery" that can be found in
the Applications folder.

When you launch the app, it will initially be a little blank.  Click on the
buttons to install an "engine" and update/install the wrapper.  CiderPress II
seems to work best as a 32-bit app with a 32-bit engine, so find the topmost
engine with "32Bit" in the name, e.g. `WS11WineCX32Bit21.2.0`, and install
that.  (With a 64-bit engine there are significant visual glitches, e.g. the
menus may not render.)

Kegworks is now ready to go.  Download the latest version of CiderPress II,
selecting the 32-bit (x86) self-contained Windows version.  For example,
for v1.0.6 you'd grab `cp2_1.0.6_win-x86_sc.zip` from the releases page.
Double-click the ZIP to unpack it into `cp2_1.0.6_win-x86_sc`.

In Kegworks, select the 32-bit engine and click "Create New Blank Wrapper".
Choose an app name, such as `CP2Mac.app`, and click OK.  The application will
go off and do things for a while (about 45 seconds on a Mac Mini M4), then
hopefully report that the wrapper was created successfully.  Click "View
wrapper in Finder".  This will open the ~/Applications/Kegworks directory.

We now need to put CiderPress II into the wrapper.  Double-click "CP2Mac".
Click "Install Software".  Click "Copy a Folder Inside".  From the Downloads
folder, select `cp2_1.0.6_win-x86_sc`, and click "Choose".  When prompted
to choose an executable, select `[...]/CiderPress2.exe`, and continue.

At this point if you quit and double-click the icon, it will launch directly
into the CiderPress II GUI.  To access Wine settings, Ctrl+click or
right-click on the app icon to open a menu.  Select "Show Package Contents".
Double-click "Contents" to open the folder, then double-click "Wineskin"
to bring up the configuration buttons.

#### Fonts ####

The CiderPress II GUI uses the default WPF font, which is called Segoe UI.
For fixed-width elements, it uses a font called Consolas, which is shipped
with Windows but not included in Wine by default.  Without Consolas, all
fixed-width content looks wrong.

Unfortunately, Segoe UI isn't accessible from Wine.  Fortunately, Consolas is.
To add it:

 - Ctrl+click the app icon, select "Show Package Contents".
 - Open the "Contents" folder, and double-click "Wineskin".
 - Click "Winetricks".
 - Expand the "fonts" item, and click the checkbox next to "consolas"
   ("MS Consolas console font").
 - Click "Run".

This does a whole bunch of work.  When it's done, you'll have a copy of the
Consolas font family files in "Contents/drive_c/windows/Fonts".  Unfortunately
you can't just copy .ttf files in there and have them recognized; otherwise
you could just copy them out of C:\Windows\Fonts and be done.  (There may be
some way to install arbitrary fonts that I'm unaware of.)

#### Distribution and Installation ####

The wrapped application can be copied into a ZIP archive and distributed.
Others can download it and launch it directly, with one minor issue.  When
macOS downloads files from the Internet, it adds a "quarantine" attribute
that prevents them from being executed.  To undo this, it's necessary to
run the following command in Terminal:

```
% cd ~/Downloads/<place where CP2 was unzipped>
% find . -print0 | xargs -0 xattr -d com.apple.quarantine
```

If you don't do this, a warning will appear claiming that the application is
"damaged" and can't be run.  It appears this step could be avoided by
signing the app, but that costs money.

If you're having trouble accessing files from Terminal, go into System
Settings, Privacy & Security, Files & Folders, and look for an entry for
the Terminal app.  It should be configured to allow access to Downloads
and Desktop.

NOTE: when the ZIP archive was uploaded to Google Drive, the automated
system flagged it as potential malware, which prevents it from being
shared.

#### Known Issues ####

 - Sometimes the main window goes almost totally black.  You can force a
   redraw by minimizing and restoring the application window.
 - The UI font can seem a little blurry in spots.  I don't know if that's
   because the correct font (Segoe UI) isn't available, or something to do
   with the rendering engine.

## Notes ##

Running x86 code on Apple Silicon requires Rosetta 2 to be installed.  The
system will offer to install it automatically the first time an x86 application
is run, though this doesn't apply to command-line utilities.  You can install
Rosetta 2 manually with the command:
`softwareupdate --install-rosetta --agree-to-license`,
or use a trick: open the Applications folder, then Utilities, then Get Info
on the Terminal application.  Check the "Open using Rosetta" checkbox, then
close and re-open Terminal.  This should cause the system to install it.

My thanks to Eric Helgeson for showing that Wineskin would work:
https://github.com/fadden/CiderPress2/discussions/34
