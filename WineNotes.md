# Wine Emulation Wrapper Notes #

The [Wine project](https://winehq.org/) provides a compatibility layer that
allows Windows applications to be executed on non-Windows operating systems,
including Linux and macOS.  It can be used to run the CiderPress II GUI
application, either by installing Wine as an application on the target
system, or by packaging the application in a Wine wrapper.

Getting it to work can be a little tricky.

## Linux ##

The first step is to get Wine working on your system.  It's an open-source
project, so you can download the sources and build it yourself, but it's
usually easier to get pre-built binaries through your Linux distribution.
Either way, go to the [Wine project](https://winehq.org/) page, click
Download, and work through one of the options.

Once you have Wine installed, run `wineboot`.  This populates configuration
data in `~/.wine/`.  If you've run Wine before, this won't do anything.

Now we need to install the .NET runtime and some fonts.  This is done with
a script called "winetricks", which may not be included as part of the base
Wine package, but should be available separately.  Install it, with e.g.
`sudo apt-get install winetricks`.

Now run it: `winetricks dotnetdesktop6 corefonts consolas tahoma`

This might fail with a message indicating that `dotnetdesktop6` is unknown.
If that's the case, your copy of winetricks is too old.  (Even if you know
that your Linux distro is stale it's still useful to install the `winetricks`
package to get some dependencies, like `cabextract`.)  Follow the instructions
on [this page](https://gitlab.winehq.org/wine/wine/-/wikis/Winetricks) to get
a fresh script (it's just one file).

If the script succeeds, you should see a Microsoft Windows Desktop Runtime
installer dialog.  Click through it.  Then click through it again (?).  The
script will then go and gather some fonts.

Wine is now ready to go.

Download a framework-dependent CiderPress II package for Windows.  Both 32-bit
and 64-bit should work.  For example, `cp2_1.0.6_win-x86_fd.zip`.  Unzip it
into a new directory.

Run the application with `wine CiderPress2.exe`.

### Known Issues ###

If you find that the application menus aren't rendering, you can try disabling
Avalon 3D acceleration:

`wine reg add "HKEY_CURRENT_USER\SOFTWARE\Microsoft\Avalon.Graphics" /v DisableHWAcceleration /t REG_DWORD /d 1 /f`

The font rendering looks bad.  The GUI font used by WPF is called "Segoe UI",
which is not available via winetricks.  Various attempts to make it available
to Wine have not yielded an improvement.  Installing the Tahoma font does seem
to help, but it's unclear whether the blurry text is due to a missing font or
to some issue with the font renderer.


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

### Wineskin / Sikarugir ###

Wrapping the application seems to work a bit better, and is more generally
useful because it allows people to download a fully functional program
without having to install and configure Wine.  For macOS, the
[Sikarugir project](https://github.com/Sikarugir-App/Sikarugir) (formerly
Kegworks) provides a reasonably user-friendly interface for this.  The chief
drawback to this approach is that the wrapped package is well over 1 GB.

As with Wine, getting started is a little bumpy.  The project page on github
has download instructions, again using Homebrew and MacPorts, but doesn't
tell you what to do next.  The installed program is not a command-line
utility, but rather a GUI app called "Sikarugir Creator" that can be found in
the Applications folder.

When you launch the app, it will initially be a little blank.  Click on the
buttons to install an "engine" and update/install the wrapper.  CiderPress II
seems to work best as a 32-bit app with a 32-bit engine, so find the topmost
engine with "32Bit" in the name, e.g. `WS11WineCX32Bit21.2.0`, and install
that.  (With a 64-bit engine there are significant visual glitches, e.g. the
menus may not render.)

Sikarugir is now ready to go.  Download the latest version of CiderPress II,
selecting the 32-bit (x86) self-contained Windows version.  For example,
for v1.0.6 you'd grab `cp2_1.0.6_win-x86_sc.zip` from the releases page.
Double-click the ZIP to unpack it into `cp2_1.0.6_win-x86_sc`.

In Sikarugir, select the 32-bit engine and click "Create New Blank Wrapper".
Choose an app name, such as `CP2Mac106[.app]`, and click "OK".  The application
will go off and do things for a while (about 45 seconds on a Mac Mini M4),
then hopefully report that the wrapper was created successfully.  Click "View
wrapper in Finder".  This will open the `~/Applications/Sikarugir` directory.
(You can quit out of Sikarugir at this point.)

We now need to put CiderPress II into the wrapper.  Double-click `CP2Mac106`.
Click "Install Software".  Click "Copy a Folder Inside".  From the Downloads
folder, select `cp2_1.0.6_win-x86_sc`, and click "Choose".  When prompted
to choose an executable, select `[...]/CiderPress2.exe`, and click "OK".

We're not quite done, but at this point if you quit and double-click the app
icon, it will launch directly into the CiderPress II GUI.  To access Wine
settings, you'll need to Ctrl+click or right-click on the app icon to open a
menu.  Select "Show Package Contents".  Double-click "Contents" to open the
folder, then double-click "Configure" to bring up the configuration buttons.

#### Fonts ####

The CiderPress II GUI uses the default WPF font, which is called Segoe UI.
For fixed-width elements, it uses a font called Consolas, which is shipped
with Windows but not included in Wine by default.  Without Consolas, all
fixed-width content looks wrong.

Unfortunately, Segoe UI isn't accessible from Wine.  Fortunately, Consolas is.
To add it:

 - If you're not still in the app configuration menu:
   - Ctrl+click the app icon, select "Show Package Contents".
   - Open the "Contents" folder, and double-click "Configure".
 - Click "Winetricks".
 - Expand the "fonts" item, and click the checkbox next to "consolas"
   ("MS Consolas console font").
 - Click "Run".  Click "Yes" to confirm.

This does a whole bunch of work.  When it's done, you'll have a copy of the
Consolas font family files in "Contents/drive_c/windows/Fonts".  Click "Close"
to close Winetricks, close the Configure window, then double-click on
the application icon to test your installation.

Installing the Tahoma font helped on Linux, but had no noticeable effect on
macOS (but macOS without Tahoma looks about the same as Linux with it).

#### Distribution and Installation ####

The wrapped application can be copied into a ZIP archive and distributed.
Others can download it and launch it directly, with one minor issue.  When
macOS downloads files from the Internet, it adds a "quarantine" attribute
that prevents them from being executed.  To undo this, it's necessary to
run the following command in Terminal:

```
% xattr -dr com.apple.quarantine ~/Downloads/<directory where CP2 was unzipped>
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

It may be useful to included a "README.txt" in the ZIP archive with the
quarantine removal instructions.  Also, you should remove the CiderPress II
settings file, so users start with a fresh installation.  In `CP2Mac106.app`,
navigate to `Contents/SharedSupport/prefix/drive_c/Program Files`.  There,
you'll find the directory you installed (e.g. `cp2_1.0.6_win-x86_sc`).
Remove `CiderPress2-settings.json` from it.  (The settings file is created on
first use, so an alternative is to create the package before testing the app.)

Running x86 code on Apple Silicon requires Rosetta 2 to be installed.  The
system will offer to install it automatically the first time an x86 application
is run, though this doesn't apply to command-line utilities.  You can install
Rosetta 2 manually with the command:
`softwareupdate --install-rosetta --agree-to-license`,
or use a trick: open the Applications folder, then Utilities, then Get Info
on the Terminal application.  Check the "Open using Rosetta" checkbox, then
close and re-open Terminal.  This should cause the system to install it.

#### Known Issues ####

 - Sometimes the main window goes almost totally black.  You can force a
   redraw by minimizing and restoring the application window.
 - The UI font can seem a little blurry in spots.  Resizing the window
   horizontally can make this better or worse.
 - The app can be a little slow to start, since it has to "boot" Windows.

Because this is a Windows emulation and not a native macOS application, the
"host" file preservation mode (which uses file attributes and resource forks
in the HFS+ filesystem) is not available.

## Closing Notes ##

My thanks to Eric Helgeson for figuring out a lot of this:
discussion https://github.com/fadden/CiderPress2/discussions/34
