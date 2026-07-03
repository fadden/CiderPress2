/*
 * Copyright 2026 Lydian Scale Software
 *
 * Licensed under the Apache License, Version 2.0 (the "License");
 * you may not use this file except in compliance with the License.
 * You may obtain a copy of the License at
 *
 *     http://www.apache.org/licenses/LICENSE-2.0
 *
 * Unless required by applicable law or agreed to in writing, software
 * distributed under the License is distributed on an "AS IS" BASIS,
 * WITHOUT WARRANTIES OR CONDITIONS OF ANY KIND, either express or implied.
 * See the License for the specific language governing permissions and
 * limitations under the License.
 */
using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Reflection;
using System.Reflection.Emit;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Input;
using Avalonia.Threading;

using cp2_avalonia.Services;

namespace cp2_avalonia.Platform;

/// <summary>
/// Linux/X11 XDND handler for BOTH receiving and sending file drags.
///
/// IMPORT (desktop → CP2):
///   KWin's XWayland bridge sends XDND messages directly to the top-level Avalonia
///   window XID.  Avalonia's X11 backend ignores all non-WM_PROTOCOLS ClientMessages.
///   We fix this by hooking Avalonia's internal event-dispatch dictionary (the public
///   AvaloniaX11Platform.Windows property) via reflection + DynamicMethod, so XDND
///   ClientMessages are intercepted before Avalonia can discard them.
///
/// EXPORT (CP2 → desktop):
///   Claims XdndSelection, then polls XQueryPointer on the background thread so we
///   never need XGrabPointer (which Wayland compositors typically deny for X11 apps).
///   XDND messages are sent to whatever XDND-aware window is under the pointer.
/// </summary>
[SupportedOSPlatform("linux")]
internal sealed class LinuxDrag : IDisposable
{
    // ── libX11 P/Invoke ──────────────────────────────────────────────────────

    [DllImport("libX11.so.6")]
    private static extern int XInitThreads();

    [DllImport("libX11.so.6")]
    private static extern nint XOpenDisplay(string? displayName);

    [DllImport("libX11.so.6")]
    private static extern int XCloseDisplay(nint display);

    [DllImport("libX11.so.6")]
    private static extern nint XCreateSimpleWindow(nint display, nint parent,
        int x, int y, uint width, uint height, uint borderWidth, ulong border, ulong background);

    [DllImport("libX11.so.6")]
    private static extern int XDestroyWindow(nint display, nint window);

    [DllImport("libX11.so.6")]
    private static extern int XMapWindow(nint display, nint window);

    [DllImport("libX11.so.6")]
    private static extern nint XDefaultRootWindow(nint display);

    [DllImport("libX11.so.6")]
    private static extern nint XInternAtom(nint display, string atomName, bool onlyIfExists);

    [DllImport("libX11.so.6", EntryPoint = "XChangeProperty")]
    private static extern int XChangePropertyArr(nint display, nint window, nint property,
        nint type, int format, int mode, nint[] data, int nElements);

    [DllImport("libX11.so.6")]
    private static extern int XGetWindowProperty(nint display, nint window, nint property,
        nint longOffset, nint longLength, bool delete, nint reqType,
        out nint actualType, out int actualFormat, out nint nItems,
        out nint bytesAfter, out nint propReturn);

    [DllImport("libX11.so.6")]
    private static extern int XChangeProperty(nint display, nint window, nint property,
        nint type, int format, int mode, byte[] data, int nElements);

    [DllImport("libX11.so.6")]
    private static extern int XFree(nint data);

    [DllImport("libX11.so.6")]
    private static extern int XNextEvent(nint display, out XEvent ev);

    [DllImport("libX11.so.6")]
    private static extern int XPending(nint display);

    [DllImport("libX11.so.6")]
    private static extern int XFlush(nint display);

    [DllImport("libX11.so.6")]
    private static extern int XSendEvent(nint display, nint window, bool propagate,
        nint eventMask, ref XEvent ev);

    [DllImport("libX11.so.6")]
    private static extern int XConvertSelection(nint display, nint selection,
        nint target, nint property, nint requestor, nint time);

    [DllImport("libX11.so.6")]
    private static extern int XSetSelectionOwner(nint display, nint selection,
        nint owner, nint time);

    [DllImport("libX11.so.6")]
    private static extern nint XGetSelectionOwner(nint display, nint selection);

    [DllImport("libX11.so.6")]
    private static extern bool XQueryPointer(nint display, nint window,
        out nint rootReturn, out nint childReturn,
        out int rootXReturn, out int rootYReturn,
        out int winXReturn, out int winYReturn,
        out uint maskReturn);

    // ── XEvent layout ────────────────────────────────────────────────────────
    //
    // Used for our own _display connection events.  Fields are aliased across
    // event types by offset (same as the X11 union).
    //
    // For ClientMessage (type=33):
    //   window   = offset 32  → destination
    //   msgOrSel = offset 40  → message_type
    //   fmt      = offset 48  → format (int)
    //   data0…4  = offsets 56,64,72,80,88 → data.l[0..4]
    //
    // For SelectionNotify (type=31):
    //   window   = offset 32  → requestor
    //   msgOrSel = offset 40  → selection
    //   fmt      = offset 48  → lower 32 bits of target Atom
    //   data0    = offset 56  → property Atom  ← KEY: data0, NOT data2
    //   data1    = offset 64  → time
    //
    // For SelectionRequest (type=30):
    //   window   = offset 32  → owner
    //   msgOrSel = offset 40  → requestor window
    //   fmt      = offset 48  → selection Atom lower 32
    //   data0    = offset 56  → target Atom
    //   data1    = offset 64  → property Atom
    //   data2    = offset 72  → time

    [StructLayout(LayoutKind.Sequential, Size = 192)]
    private struct XEvent
    {
        public int type;         // offset 0
        public nint serial;      // offset 8  (4 bytes natural padding at 4-7)
        public int sendEvent;    // offset 16
        public nint display;     // offset 24 (4 bytes padding at 20-23)
        public nint window;      // offset 32
        public nint msgOrSel;    // offset 40
        public int fmt;          // offset 48
        // 4 bytes implicit padding at 52-55
        public nint data0;       // offset 56
        public nint data1;       // offset 64
        public nint data2;       // offset 72
        public nint data3;       // offset 80
        public nint data4;       // offset 88
    }

    private const int SelectionRequest = 30;
    private const int SelectionNotify  = 31;
    private const int ClientMessage    = 33;

    private const int PropModeReplace  = 0;
    private const long XdndVersion     = 5;
    private const nint CurrentTime     = 0;

    // Button1Mask in XQueryPointer's returned button/modifier mask.
    private const uint Button1Mask = 1u << 8;

    // ── Atoms ────────────────────────────────────────────────────────────────

    private nint _atomXdndAware;
    private nint _atomXdndEnter;
    private nint _atomXdndPosition;
    private nint _atomXdndStatus;
    private nint _atomXdndLeave;
    private nint _atomXdndDrop;
    private nint _atomXdndFinished;
    private nint _atomXdndSelection;
    private nint _atomXdndActionCopy;
    private nint _atomTextUriList;
    private nint _atomAtom;
    private nint _atomWindow;
    private nint _atomTargets;
    private nint _atomCP2DndData;

    // ── Infrastructure ───────────────────────────────────────────────────────

    private readonly nint _display;
    private readonly nint _proxyWindow;     // export source identity + import selection requestor
    private readonly nint _avaloniaWin;
    private readonly nint _rootWindow;
    private readonly Action<string[], int, int> _onImportDrop;
    private readonly Action<string, int, int>? _onImportCp2Drop;
    private readonly Thread _eventThread;
    private volatile bool _disposed;

    // ── Import state (written from Avalonia hook thread, read by _display thread) ──
    //
    // Invariant: _importRequestedAtom is set before XConvertSelection is called, so
    // it is always valid by the time SelectionNotify arrives on the _display thread.

    private readonly object _importLock = new();
    private volatile nint _importSourceWindow;
    private volatile nint _importTimestamp;
    private readonly List<nint> _importFormats = new();
    // Root-coordinate drop position, captured from the last XdndPosition before XdndDrop.
    private int _importDropRootX;
    private int _importDropRootY;
    // Which XDND selection type was requested (set in ImportOnXdndDrop).
    private volatile nint _importRequestedAtom;

    // ── Export state ─────────────────────────────────────────────────────────
    //
    // Invariants (do not re-break):
    //   1. XdndSelection must be claimed at drag start, before XdndEnter is sent.
    //      KWin uses this claim to build the XWayland→Wayland bridge for native targets.
    //   2. Never use XGrabPointer — Wayland compositors deny X11 global grabs.
    //      Use XQueryPointer polling (~8 ms) instead.
    //   3. XInitThreads() must be called before any display connection opens.
    //   4. All drop callbacks must be marshalled to the UI thread via Dispatcher.UIThread.Post.
    //   5. XdndPosition data.l[2] = (rootX<<16)|rootY, data.l[3] = timestamp.
    //   6. FindXdndAwareWindow returns Zero for our own windows — that is how
    //      internal vs. external drops are distinguished on button release.

    private volatile bool _isExporting;
    private string[] _exportPaths = Array.Empty<string>();
    private string _exportUriList = string.Empty;
    private string _exportClipJson = string.Empty;
    private string? _exportTempDir;
    // Set to true once SetNativeDragPaths has been called; SelectionRequest handler
    // spin-waits on this so it always has valid data even for fast drags.
    private volatile bool _exportPathsReady;
    private TaskCompletionSource<DragDropEffects>? _exportTcs;
    private nint _exportCurrentTarget;
    private bool _exportTargetAccepts;
    private bool _exportDropSent;

    // Called on the UI thread when the button is released over our own window.
    // Receives root-coordinate (screenX, screenY) of the drop point.
    private Action<int, int>? _onInternalDrop;

    // Pointer state tracking for export (background thread only).
    private int _pollRootX;
    private int _pollRootY;
    private bool _pollButton1WasDown;

    // ── Direct clipboard read ─────────────────────────────────────────────────
    //
    // Avalonia's IClipboard.GetTextAsync() requests TARGETS before the actual data
    // (two X11 round-trips), and both go through the Avalonia UI event loop rather
    // than our dedicated background thread — adding scheduling latency under
    // Wayland/XWayland.  Reading clipboard directly on our _display connection
    // skips the TARGETS step and runs on the fast background thread.

    private nint _atomClipboard;
    private nint _atomUtf8String;
    private TaskCompletionSource<string?>? _clipReadTcs;

    // Static reference so ClipboardService can reach us without a DI dependency.
    private static volatile LinuxDrag? s_mainInstance;
    public static void RegisterMainInstance(LinuxDrag instance) => s_mainInstance = instance;
    public static void UnregisterMainInstance(LinuxDrag instance) {
        if (s_mainInstance == instance) s_mainInstance = null;
    }

    /// <summary>
    /// Reads clipboard text via a direct XConvertSelection on the background
    /// event loop, bypassing Avalonia's two-round-trip TARGETS overhead.
    /// Returns null on timeout or if no clipboard owner exists.
    /// </summary>
    [System.Runtime.Versioning.SupportedOSPlatform("linux")]
    public static Task<string?> ReadMainWindowClipboardTextAsync() =>
        s_mainInstance?.ReadClipboardTextAsync() ?? System.Threading.Tasks.Task.FromResult<string?>(null);

    [System.Runtime.Versioning.SupportedOSPlatform("linux")]
    private async Task<string?> ReadClipboardTextAsync()
    {
        if (_disposed) return null;
        var tcs = new TaskCompletionSource<string?>();
        if (Interlocked.CompareExchange(ref _clipReadTcs, tcs, null) != null)
            return null; // another read already in flight

        XConvertSelection(_display, _atomClipboard, _atomUtf8String,
            _atomUtf8String, _proxyWindow, CurrentTime);
        XFlush(_display);

        try {
            return await tcs.Task.WaitAsync(TimeSpan.FromSeconds(2));
        } catch (OperationCanceledException) {
            return null;
        } finally {
            Interlocked.CompareExchange(ref _clipReadTcs, null, tcs);
        }
    }

    private void ClipboardOnSelectionNotify(ref XEvent ev)
    {
        nint property = ev.data0;
        string? text = property != IntPtr.Zero ? ReadStringProperty(_proxyWindow, property) : null;
        var tcs = Interlocked.Exchange(ref _clipReadTcs, null);
        tcs?.TrySetResult(text);
    }

    // ── Avalonia event-hook statics ───────────────────────────────────────────
    //
    // Per-window dictionaries keyed by Avalonia window XID.  Using shared single-
    // instance statics would break when multiple LinuxDrag instances are active
    // (e.g. MainWindow + DropTarget debug window) because TryHookAvaloniaDnD would
    // overwrite the shared fields, causing NullReferenceException in the hook for
    // the first window the next time it tries to call the (now-replaced) original.

    private static readonly System.Collections.Concurrent.ConcurrentDictionary<nint, LinuxDrag>
        s_hookInstances = new();
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<nint, Delegate>
        s_hookOriginals = new();

    // Lookup helpers called from emitted IL — must be static with known signatures.
    private static LinuxDrag? HookLookupDrag(nint windowXID) =>
        s_hookInstances.TryGetValue(windowXID, out var d) ? d : null;
    private static Delegate? HookLookupOriginal(nint windowXID) =>
        s_hookOriginals.TryGetValue(windowXID, out var h) ? h : null;

    // ── Construction / disposal ───────────────────────────────────────────────

    private LinuxDrag(nint avaloniaWindow, Action<string[], int, int> onImportDrop,
        Action<string, int, int>? onImportCp2Drop)
    {
        _avaloniaWin = avaloniaWindow;
        _onImportDrop = onImportDrop;
        _onImportCp2Drop = onImportCp2Drop;

        // Enable Xlib thread safety before opening our display connection.
        // Must be called before the first use of display-connection functions.
        XInitThreads();

        _display = XOpenDisplay(null);
        if (_display == IntPtr.Zero)
            throw new InvalidOperationException("LinuxDrag: XOpenDisplay failed");

        _rootWindow = XDefaultRootWindow(_display);
        InitAtoms();

        // Create proxy window (child of Avalonia window).
        // Used as the XDND source identity in export, and as the XConvertSelection
        // requestor so SelectionNotify events come back on _display.
        _proxyWindow = XCreateSimpleWindow(_display, _avaloniaWin, -2, -2, 1, 1, 0, 0, 0);
        if (_proxyWindow == IntPtr.Zero)
        {
            XCloseDisplay(_display);
            throw new InvalidOperationException("LinuxDrag: XCreateSimpleWindow failed");
        }

        XMapWindow(_display, _proxyWindow);

        // Mark the Avalonia window as XDND-aware so KWin's bridge will bridge
        // Wayland drags to X11 XDND and deliver events to this window.
        SetXdndAware(_avaloniaWin);
        XFlush(_display);

        // Hook Avalonia's event dispatch so we receive XDND messages that KWin
        // sends directly to _avaloniaWin (bypassing any XdndProxy).
        TryHookAvaloniaDnD();

        _eventThread = new Thread(EventLoop)
        {
            IsBackground = true,
            Name = "LinuxXdndEventLoop"
        };
        _eventThread.Start();

        Debug.WriteLine($"LinuxDrag: ready, avaloniaWin=0x{_avaloniaWin:x} proxy=0x{_proxyWindow:x}");
    }

    public void Dispose()
    {
        _disposed = true;
        _exportTcs?.TrySetResult(DragDropEffects.None);

        // Restore Avalonia's original event handler before closing.
        TryUnhookAvaloniaDnD();

        if (_proxyWindow != IntPtr.Zero) XDestroyWindow(_display, _proxyWindow);
        if (_display != IntPtr.Zero)    XCloseDisplay(_display);
    }

    // ── Public factory ────────────────────────────────────────────────────────

    public static LinuxDrag? Install(nint avaloniaWindowXid,
        Action<string[], int, int> onImportDrop,
        Action<string, int, int>? onImportCp2Drop = null)
    {
        if (avaloniaWindowXid == IntPtr.Zero) return null;
        try { return new LinuxDrag(avaloniaWindowXid, onImportDrop, onImportCp2Drop); }
        catch (Exception ex) { AppLog.W("Linux drag: install failed", ex); return null; }
    }

    /// <summary>
    /// Arms the XDND export drag by claiming <c>XdndSelection</c> and starting the
    /// pointer-poll loop immediately — <em>before</em> file extraction runs.  This
    /// ensures KWin's XWayland→Wayland bridge is created early and that fast
    /// drag-flicks are not lost while extraction is in progress.
    /// </summary>
    /// <param name="onInternalDrop">
    /// Called on the UI thread when the button is released over one of our own windows
    /// (no external XDND target accepted the drop).  Receives root-coordinates (x, y).
    /// </param>
    /// <returns>
    /// A <see cref="Task{T}"/> that completes when the drag ends.  The caller should
    /// follow this with a call to <see cref="SetNativeDragPaths"/> once extraction
    /// finishes.
    /// </returns>
    public Task<DragDropEffects> BeginNativeDrag(Action<int, int>? onInternalDrop = null)
    {
        _exportPaths         = Array.Empty<string>();
        _exportUriList       = string.Empty;
        _exportClipJson      = string.Empty;
        _exportTempDir       = null;
        _exportPathsReady    = false;
        _exportCurrentTarget = IntPtr.Zero;
        _exportTargetAccepts = false;
        _exportDropSent      = false;
        _onInternalDrop      = onInternalDrop;
        _exportTcs           = new TaskCompletionSource<DragDropEffects>();

        // Claim XdndSelection before sending XdndEnter.
        // KWin uses this claim to create the XWayland→Wayland DnD bridge.
        XSetSelectionOwner(_display, _atomXdndSelection, _proxyWindow, CurrentTime);
        XFlush(_display);

        XQueryPointer(_display, _rootWindow, out _, out _,
            out _pollRootX, out _pollRootY, out _, out _, out uint mask);
        _pollButton1WasDown = (mask & Button1Mask) != 0;

        if (!_pollButton1WasDown)
        {
            Debug.WriteLine("LinuxDrag: button not held at drag start, aborting");
            _onInternalDrop = null;
            _exportTcs.TrySetResult(DragDropEffects.None);
            return _exportTcs.Task;
        }

        Debug.WriteLine("LinuxDrag: export drag armed (no-grab polling mode)");
        _isExporting = true;
        return _exportTcs.Task;
    }

    /// <summary>
    /// Provides the file paths and CP2 JSON to serve during the active drag.
    /// Must be called after <see cref="BeginNativeDrag"/> returns a running task.
    /// Safe to call even if the drag has already ended (paths are simply ignored).
    /// </summary>
    /// <param name="localPaths">Absolute local paths of files to offer.</param>
    /// <param name="clipJson">CP2 JSON payload for cross-instance rich drag (may be empty).</param>
    /// <param name="tempDir">Temp directory to delete on <c>XdndFinished</c> (or null).</param>
    public void SetNativeDragPaths(string[] localPaths, string clipJson, string? tempDir = null)
    {
        if (localPaths.Length > 0)
        {
            var sb = new StringBuilder();
            foreach (string p in localPaths)
            {
                string abs = System.IO.Path.GetFullPath(p);
                sb.Append(new Uri(abs).AbsoluteUri).Append("\r\n");
            }
            _exportPaths   = localPaths;
            _exportUriList = sb.ToString();
        }
        _exportClipJson   = clipJson;
        _exportTempDir    = tempDir;
        _exportPathsReady = true;   // signal ExportOnSelectionRequest
    }

    // ── Atom initialisation ───────────────────────────────────────────────────

    private void InitAtoms()
    {
        _atomXdndAware     = XInternAtom(_display, "XdndAware",      false);
        _atomXdndEnter     = XInternAtom(_display, "XdndEnter",      false);
        _atomXdndPosition  = XInternAtom(_display, "XdndPosition",   false);
        _atomXdndStatus    = XInternAtom(_display, "XdndStatus",     false);
        _atomXdndLeave     = XInternAtom(_display, "XdndLeave",      false);
        _atomXdndDrop      = XInternAtom(_display, "XdndDrop",       false);
        _atomXdndFinished  = XInternAtom(_display, "XdndFinished",   false);
        _atomXdndSelection = XInternAtom(_display, "XdndSelection",  false);
        _atomXdndActionCopy= XInternAtom(_display, "XdndActionCopy", false);
        _atomTextUriList   = XInternAtom(_display, "text/uri-list",  false);
        _atomAtom          = XInternAtom(_display, "ATOM",           false);
        _atomWindow        = XInternAtom(_display, "WINDOW",         false);
        _atomTargets       = XInternAtom(_display, "TARGETS",        false);
        // Use the same reverse-DNS UTI as ClipInfo.CROSS_INSTANCE_FORMAT so that
        // cross-instance drags are routed through the rich CP2 paste path.
        _atomCP2DndData    = XInternAtom(_display, "com.faddensoft.ciderpress2.clip", false);
        _atomClipboard     = XInternAtom(_display, "CLIPBOARD",    false);
        _atomUtf8String    = XInternAtom(_display, "UTF8_STRING",  false);
    }

    private void SetXdndAware(nint window)
    {
        XChangePropertyArr(_display, window, _atomXdndAware, _atomAtom, 32,
            PropModeReplace, new[] { (nint)XdndVersion }, 1);
    }

    // ── Avalonia event hook ───────────────────────────────────────────────────

    /// <summary>
    /// Replaces the event handler for _avaloniaWin in Avalonia's internal Windows
    /// dictionary with a DynamicMethod wrapper that intercepts XDND ClientMessages.
    /// Each installed hook embeds its window XID as a constant and looks up the
    /// correct LinuxDrag instance and original handler via the per-window dictionaries,
    /// so multiple concurrent instances (MainWindow + DropTarget etc.) never conflict.
    /// </summary>
    private void TryHookAvaloniaDnD()
    {
        try
        {
            // Avalonia.X11 is a transitive dependency; find it in loaded assemblies.
            var x11Asm = AppDomain.CurrentDomain.GetAssemblies()
                .FirstOrDefault(a => a.GetName().Name == "Avalonia.X11");
            if (x11Asm == null)
            {
                Debug.WriteLine("LinuxDrag: Avalonia.X11 assembly not found");
                return;
            }

            var platformType = x11Asm.GetType("Avalonia.X11.AvaloniaX11Platform");
            var dispType     = x11Asm.GetType("Avalonia.X11.X11EventDispatcher");
            var handlerType  = dispType?.GetNestedType("EventHandler", BindingFlags.Public);
            var xEventType   = x11Asm.GetType("Avalonia.X11.XEvent");
            var xcmType      = x11Asm.GetType("Avalonia.X11.XClientMessageEvent");

            if (platformType == null || handlerType == null ||
                xEventType == null || xcmType == null)
            {
                Debug.WriteLine("LinuxDrag: hook reflection targets not found");
                return;
            }

            // AvaloniaLocator is [PrivateApi] — access entirely via reflection.
            var avBase  = AppDomain.CurrentDomain.GetAssemblies()
                .FirstOrDefault(a => a.GetName().Name == "Avalonia.Base");
            var locType = avBase?.GetType("Avalonia.AvaloniaLocator");
            var curProp = locType?.GetProperty("Current",
                BindingFlags.Public | BindingFlags.Static);
            var locator = curProp?.GetValue(null);
            var getSvc  = locator?.GetType().GetMethod("GetService",
                new[] { typeof(Type) });
            var platform = getSvc?.Invoke(locator, new object[] { platformType });
            if (platform == null)
            {
                Debug.WriteLine("LinuxDrag: AvaloniaX11Platform not in locator");
                return;
            }

            var windowsProp = platformType.GetProperty("Windows",
                BindingFlags.Public | BindingFlags.Instance);
            if (windowsProp == null)
            {
                Debug.WriteLine("LinuxDrag: Windows property not found");
                return;
            }

            var windows = windowsProp.GetValue(platform) as IDictionary;
            if (windows == null)
            {
                Debug.WriteLine("LinuxDrag: Windows dictionary is null");
                return;
            }

            var key = (object)(IntPtr)_avaloniaWin;
            var originalHandler = windows[key] as Delegate;
            if (originalHandler == null)
            {
                Debug.WriteLine("LinuxDrag: no existing handler for avaloniaWin");
                return;
            }

            // Fields on Avalonia.X11.XEvent (via XClientMessageEvent overlay at offset 0)
            var xEventTypeField = xEventType.GetField("type",
                BindingFlags.NonPublic | BindingFlags.Instance);
            var cmField = xEventType.GetField("ClientMessageEvent",
                BindingFlags.NonPublic | BindingFlags.Instance);
            var msgTypeField = xcmType.GetField("message_type",
                BindingFlags.NonPublic | BindingFlags.Instance);

            if (xEventTypeField == null || cmField == null || msgTypeField == null)
            {
                Debug.WriteLine("LinuxDrag: XEvent fields not found");
                return;
            }

            // Unsafe.As<AvaloniasXEvent, OurXEvent> for reinterpretation
            var ourXEventType = typeof(LinuxDrag).GetNestedType("XEvent",
                BindingFlags.NonPublic | BindingFlags.Instance)!;
            var unsafeAs = typeof(Unsafe).GetMethods(
                    BindingFlags.Public | BindingFlags.Static)
                .Where(m => m.Name == "As" && m.IsGenericMethodDefinition &&
                            m.GetGenericArguments().Length == 2 &&
                            m.GetParameters().Length == 1)
                .First()
                .MakeGenericMethod(xEventType, ourXEventType);

            // Per-window lookup helpers called from the generated IL.
            var lookupDragMethod = typeof(LinuxDrag).GetMethod(
                nameof(HookLookupDrag),
                BindingFlags.Static | BindingFlags.NonPublic)!;
            var lookupOrigMethod = typeof(LinuxDrag).GetMethod(
                nameof(HookLookupOriginal),
                BindingFlags.Static | BindingFlags.NonPublic)!;

            // Static dispatch helpers (same as before).
            var isXdndMethod = typeof(LinuxDrag).GetMethod(
                nameof(IsImportXdndMessage),
                BindingFlags.Static | BindingFlags.NonPublic)!;
            var handleMethod = typeof(LinuxDrag).GetMethod(
                nameof(HandleImportXdndEvent),
                BindingFlags.Static | BindingFlags.NonPublic)!;

            // Build DynamicMethod: void(ref Avalonia.X11.XEvent)
            // Each method embeds _avaloniaWin as a constant so it looks up its own
            // LinuxDrag instance, never another window's.
            var dm = new DynamicMethod(
                "LinuxDragXdndHook_" + _avaloniaWin.ToString("x"),
                typeof(void),
                new[] { xEventType.MakeByRefType() },
                typeof(LinuxDrag).Module,
                skipVisibility: true);

            var il = dm.GetILGenerator();

            // Locals: 0 = LinuxDrag?, 1 = nint (msgType), 2 = Delegate?
            var local_drag  = il.DeclareLocal(typeof(LinuxDrag));
            var local_msg   = il.DeclareLocal(typeof(nint));
            var local_orig  = il.DeclareLocal(typeof(Delegate));
            var callOrigLabel = il.DefineLabel();
            var retLabel      = il.DefineLabel();

            // Helper: push _avaloniaWin as an nint constant.
            // On 64-bit Linux nint == long; conv.i converts to native int.
            void EmitWindowXID() {
                il.Emit(OpCodes.Ldc_I8, (long)_avaloniaWin);
                il.Emit(OpCodes.Conv_I);
            }

            // ── Check event type == ClientMessage (33) ────────────────────────
            il.Emit(OpCodes.Ldarg_0);
            il.Emit(OpCodes.Ldfld, xEventTypeField);
            il.Emit(OpCodes.Ldc_I4, ClientMessage);
            il.Emit(OpCodes.Bne_Un, callOrigLabel);

            // ── Read message_type → local_msg ────────────────────────────────
            il.Emit(OpCodes.Ldarg_0);
            il.Emit(OpCodes.Ldflda, cmField);
            il.Emit(OpCodes.Ldfld, msgTypeField);
            il.Emit(OpCodes.Stloc, local_msg);

            // ── Lookup this window's LinuxDrag instance → local_drag ─────────
            EmitWindowXID();
            il.EmitCall(OpCodes.Call, lookupDragMethod, null);
            il.Emit(OpCodes.Stloc, local_drag);
            il.Emit(OpCodes.Ldloc, local_drag);
            il.Emit(OpCodes.Brfalse, callOrigLabel);    // disposed → forward

            // ── IsImportXdndMessage(local_msg, local_drag) ───────────────────
            il.Emit(OpCodes.Ldloc, local_msg);
            il.Emit(OpCodes.Ldloc, local_drag);
            il.EmitCall(OpCodes.Call, isXdndMethod, null);
            il.Emit(OpCodes.Brfalse, callOrigLabel);

            // ── HandleImportXdndEvent(local_drag, Unsafe.As(arg0)) ───────────
            il.Emit(OpCodes.Ldloc, local_drag);
            il.Emit(OpCodes.Ldarg_0);
            il.EmitCall(OpCodes.Call, unsafeAs, null);
            il.EmitCall(OpCodes.Call, handleMethod, null);
            il.Emit(OpCodes.Ret);

            // ── callOrigLabel: look up and invoke this window's original handler
            il.MarkLabel(callOrigLabel);
            EmitWindowXID();
            il.EmitCall(OpCodes.Call, lookupOrigMethod, null);
            il.Emit(OpCodes.Stloc, local_orig);
            il.Emit(OpCodes.Ldloc, local_orig);
            il.Emit(OpCodes.Brfalse, retLabel);     // null → nothing to call (disposed/absent)
            il.Emit(OpCodes.Ldloc, local_orig);
            il.Emit(OpCodes.Castclass, handlerType);
            il.Emit(OpCodes.Ldarg_0);
            il.EmitCall(OpCodes.Callvirt, handlerType.GetMethod("Invoke")!, null);
            il.Emit(OpCodes.Ret);

            il.MarkLabel(retLabel);
            il.Emit(OpCodes.Ret);

            var hookDelegate = dm.CreateDelegate(handlerType);

            // Register in per-window dictionaries BEFORE installing in Avalonia's map.
            s_hookInstances[_avaloniaWin] = this;
            s_hookOriginals[_avaloniaWin] = originalHandler;
            windows[key] = hookDelegate;

            Debug.WriteLine($"LinuxDrag: hook installed for window 0x{_avaloniaWin:x}");
        }
        catch (Exception ex)
        {
            AppLog.W("Linux drag: hook installation failed", ex);
        }
    }

    private void TryUnhookAvaloniaDnD()
    {
        try
        {
            // Remove from both per-window dictionaries.
            s_hookInstances.TryRemove(_avaloniaWin, out _);
            if (!s_hookOriginals.TryRemove(_avaloniaWin, out var orig) || orig == null)
                return;

            // Restore the original Avalonia event handler for this window.
            var x11Asm = AppDomain.CurrentDomain.GetAssemblies()
                .FirstOrDefault(a => a.GetName().Name == "Avalonia.X11");
            if (x11Asm == null) return;
            var platformType = x11Asm.GetType("Avalonia.X11.AvaloniaX11Platform");
            if (platformType == null) return;

            var avBase  = AppDomain.CurrentDomain.GetAssemblies()
                .FirstOrDefault(a => a.GetName().Name == "Avalonia.Base");
            var locType = avBase?.GetType("Avalonia.AvaloniaLocator");
            var curProp = locType?.GetProperty("Current",
                BindingFlags.Public | BindingFlags.Static);
            var locator = curProp?.GetValue(null);
            var getSvc  = locator?.GetType().GetMethod("GetService",
                new[] { typeof(Type) });
            var platform = getSvc?.Invoke(locator, new object[] { platformType });
            if (platform == null) return;

            var windowsProp = platformType.GetProperty("Windows",
                BindingFlags.Public | BindingFlags.Instance);
            var windows = windowsProp?.GetValue(platform) as IDictionary;
            if (windows == null) return;

            windows[(object)(IntPtr)_avaloniaWin] = orig;
        }
        catch (Exception ex)
        {
            AppLog.W("Linux drag: hook removal failed", ex);
        }
    }

    // Called from DynamicMethod (runs on Avalonia's event-dispatch thread).
    // Returns true if msgType is one of the import-side XDND atoms.
    private static bool IsImportXdndMessage(nint msgType, LinuxDrag? drag)
    {
        if (drag == null) return false;
        return msgType == drag._atomXdndEnter   ||
               msgType == drag._atomXdndPosition ||
               msgType == drag._atomXdndLeave   ||
               msgType == drag._atomXdndDrop;
    }

    // Called from DynamicMethod; `ev` is our XEvent reinterpreted from Avalonia's.
    // Runs on Avalonia's event-dispatch thread.
    private static void HandleImportXdndEvent(LinuxDrag drag, ref XEvent ev)
    {
        try
        {
            nint mt = ev.msgOrSel;
            if      (mt == drag._atomXdndEnter)    drag.ImportOnXdndEnter(ref ev);
            else if (mt == drag._atomXdndPosition) drag.ImportOnXdndPosition(ref ev);
            else if (mt == drag._atomXdndLeave)    drag.ImportOnXdndLeave();
            else if (mt == drag._atomXdndDrop)     drag.ImportOnXdndDrop(ref ev);
        }
        catch (Exception ex)
        {
            AppLog.W("Linux drag: Avalonia hook error", ex);
        }
    }

    // ── Background event loop (_display connection) ───────────────────────────

    private void EventLoop()
    {
        while (!_disposed)
        {
            // When we are the XDND source, poll the pointer instead of waiting
            // for events.  We don't use XGrabPointer because Wayland compositors
            // typically deny X11 global pointer grabs.
            if (_isExporting)
            {
                PollPointerForExport();

                // Still drain any queued events (XdndStatus, XdndFinished,
                // SelectionRequest, SelectionNotify) while exporting.
                while (XPending(_display) > 0)
                {
                    XNextEvent(_display, out XEvent ev);
                    try { HandleEventFromDisplay(ref ev); }
                    catch (Exception ex) { AppLog.W("Linux drag: event handling error", ex); }
                }

                Thread.Sleep(8);
                continue;
            }

            if (XPending(_display) == 0) { Thread.Sleep(8); continue; }
            XNextEvent(_display, out XEvent nev);
            try { HandleEventFromDisplay(ref nev); }
            catch (Exception ex) { AppLog.W("Linux drag: event handling error", ex); }
        }
    }

    /// <summary>
    /// Called by the background event loop while an export drag is in progress.
    /// Queries the current pointer position/button state and drives the XDND
    /// protocol without needing a pointer grab.
    /// </summary>
    private void PollPointerForExport()
    {
        XQueryPointer(_display, _rootWindow, out _, out _,
            out int rootX, out int rootY, out _, out _, out uint mask);

        bool button1Down = (mask & Button1Mask) != 0;

        if (!button1Down && _pollButton1WasDown)
        {
            // Button was just released — end the drag.
            _pollButton1WasDown = false;
            ExportOnButtonRelease(rootX, rootY);
            return;
        }

        _pollButton1WasDown = button1Down;

        if (!button1Down) return;   // button not held; drag not started yet

        if (rootX == _pollRootX && rootY == _pollRootY) return;  // no movement

        _pollRootX = rootX;
        _pollRootY = rootY;
        ExportOnMotion(rootX, rootY);
    }

    // Events arriving on _display:
    //   SelectionNotify  → import data after XConvertSelection
    //   SelectionRequest → export data delivery
    //   XdndStatus       → export target accepted/rejected
    //   XdndFinished     → export target confirmed drop
    private void HandleEventFromDisplay(ref XEvent ev)
    {
        switch (ev.type)
        {
            case ClientMessage:
                nint mt = ev.msgOrSel;
                if      (mt == _atomXdndStatus)   ExportOnXdndStatus(ref ev);
                else if (mt == _atomXdndFinished) ExportOnXdndFinished();
                break;
            case SelectionNotify:
                if (ev.msgOrSel == _atomClipboard)
                    ClipboardOnSelectionNotify(ref ev);
                else
                    ImportOnSelectionNotify(ref ev);
                break;
            case SelectionRequest:
                ExportOnSelectionRequest(ref ev);
                break;
        }
    }

    // ── IMPORT: receive a drop from an external source ────────────────────────
    // (These run on Avalonia's event-dispatch thread, called from the hook.)

    private void ImportOnXdndEnter(ref XEvent ev)
    {
        lock (_importLock)
        {
            _importSourceWindow = ev.data0;
            bool moreTypes = ((long)ev.data1 & 1) != 0;

            _importFormats.Clear();
            if (!moreTypes)
            {
                if (ev.data2 != IntPtr.Zero) _importFormats.Add(ev.data2);
                if (ev.data3 != IntPtr.Zero) _importFormats.Add(ev.data3);
                if (ev.data4 != IntPtr.Zero) _importFormats.Add(ev.data4);
            }
            else
            {
                _importFormats.AddRange(ReadAtomList(_importSourceWindow, "XdndTypeList"));
            }
        }
        Debug.WriteLine($"LinuxDrag: XdndEnter src=0x{_importSourceWindow:x}");
    }

    private void ImportOnXdndPosition(ref XEvent ev)
    {
        // data2 = (rootX << 16) | rootY;  data3 = timestamp
        long packed = (long)ev.data2;
        _importDropRootX = (int)((packed >> 16) & 0xFFFF);
        _importDropRootY = (int)(packed & 0xFFFF);
        _importTimestamp = ev.data3;
        bool accept;
        lock (_importLock) { accept = _importFormats.Contains(_atomTextUriList); }
        ImportSendXdndStatus(_importSourceWindow, accept);
    }

    private void ImportOnXdndLeave()
    {
        Debug.WriteLine("LinuxDrag: XdndLeave");
        ImportReset();
    }

    private void ImportOnXdndDrop(ref XEvent ev)
    {
        _importTimestamp = ev.data2;
        Debug.WriteLine("LinuxDrag: XdndDrop");

        bool hasCP2, hasUri;
        lock (_importLock)
        {
            hasCP2 = _importFormats.Contains(_atomCP2DndData);
            hasUri = _importFormats.Contains(_atomTextUriList);
        }

        if (!hasCP2 && !hasUri)
        {
            ImportSendXdndFinished(_importSourceWindow, accepted: false);
            ImportReset();
            return;
        }

        // Prefer the CP2 rich format for cross-instance drags; fall back to URI list.
        _importRequestedAtom = hasCP2 ? _atomCP2DndData : _atomTextUriList;
        XConvertSelection(_display, _atomXdndSelection, _importRequestedAtom,
            _atomCP2DndData, _proxyWindow, _importTimestamp);
        XFlush(_display);
    }

    // Runs on _display event loop thread.
    private void ImportOnSelectionNotify(ref XEvent ev)
    {
        // XSelectionEvent.property is at offset 56 = data0.
        nint property = ev.data0;

        if (property == IntPtr.Zero)
        {
            Debug.WriteLine("LinuxDrag: SelectionNotify - conversion refused");

            // If we asked for CP2 and the source refused, fall back to URI list.
            if (_importRequestedAtom == _atomCP2DndData)
            {
                bool hasUri;
                lock (_importLock) { hasUri = _importFormats.Contains(_atomTextUriList); }
                if (hasUri)
                {
                    _importRequestedAtom = _atomTextUriList;
                    XConvertSelection(_display, _atomXdndSelection, _atomTextUriList,
                        _atomCP2DndData, _proxyWindow, _importTimestamp);
                    XFlush(_display);
                    return;
                }
            }
            ImportSendXdndFinished(_importSourceWindow, accepted: false);
            ImportReset();
            return;
        }

        string? payload = ReadStringProperty(_proxyWindow, property);

        if (_importRequestedAtom == _atomCP2DndData)
        {
            if (payload != null && _onImportCp2Drop != null)
            {
                ImportSendXdndFinished(_importSourceWindow, accepted: true);
                ImportReset();
                int dropX = _importDropRootX, dropY = _importDropRootY;
                Dispatcher.UIThread.Post(() => _onImportCp2Drop!(payload, dropX, dropY));
            }
            else
            {
                // Received CP2 atom but no handler registered or null payload —
                // fall back to URI list so plain-file add still works.
                bool hasUri;
                lock (_importLock) { hasUri = _importFormats.Contains(_atomTextUriList); }
                if (hasUri)
                {
                    _importRequestedAtom = _atomTextUriList;
                    XConvertSelection(_display, _atomXdndSelection, _atomTextUriList,
                        _atomCP2DndData, _proxyWindow, _importTimestamp);
                    XFlush(_display);
                    return;
                }
                ImportSendXdndFinished(_importSourceWindow, accepted: false);
                ImportReset();
            }
        }
        else
        {
            // URI list path.
            string[] paths = payload != null ? ParseUriList(payload) : Array.Empty<string>();
            ImportSendXdndFinished(_importSourceWindow, accepted: paths.Length > 0);
            ImportReset();
            if (paths.Length == 0) return;
            int dropX = _importDropRootX, dropY = _importDropRootY;
            Dispatcher.UIThread.Post(() => _onImportDrop(paths, dropX, dropY));
        }
    }

    private void ImportSendXdndStatus(nint target, bool accept)
    {
        var ev = new XEvent
        {
            type     = ClientMessage,
            window   = target,
            msgOrSel = _atomXdndStatus,
            fmt      = 32,
            data0    = _avaloniaWin,   // target window that is responding
            data1    = (nint)(accept ? 1 : 0),
            data4    = accept ? _atomXdndActionCopy : IntPtr.Zero,
        };
        XSendEvent(_display, target, false, 0, ref ev);
        XFlush(_display);
    }

    private void ImportSendXdndFinished(nint target, bool accepted)
    {
        if (target == IntPtr.Zero) return;
        var ev = new XEvent
        {
            type     = ClientMessage,
            window   = target,
            msgOrSel = _atomXdndFinished,
            fmt      = 32,
            data0    = _avaloniaWin,   // target window that accepted/rejected
            data1    = (nint)(accepted ? 1 : 0),
            data2    = accepted ? _atomXdndActionCopy : IntPtr.Zero,
        };
        XSendEvent(_display, target, false, 0, ref ev);
        XFlush(_display);
    }

    private void ImportReset()
    {
        _importSourceWindow = IntPtr.Zero;
        _importTimestamp    = IntPtr.Zero;
        lock (_importLock) { _importFormats.Clear(); }
    }

    // ── EXPORT: be the XDND source ────────────────────────────────────────────

    private void ExportOnMotion(int rootX, int rootY)
    {
        nint target = FindXdndAwareWindow(rootX, rootY);
        if (target != _exportCurrentTarget)
        {
            if (_exportCurrentTarget != IntPtr.Zero)
                ExportSendXdndLeave(_exportCurrentTarget);
            _exportCurrentTarget = target;
            _exportTargetAccepts = false;
            if (target != IntPtr.Zero)
                ExportSendXdndEnter(target);
        }
        if (target != IntPtr.Zero)
            ExportSendXdndPosition(target, rootX, rootY);
    }

    private void ExportOnButtonRelease(int rootX, int rootY)
    {
        _isExporting = false;

        if (_exportCurrentTarget != IntPtr.Zero && _exportTargetAccepts)
        {
            _exportDropSent = true;
            ExportSendXdndDrop(_exportCurrentTarget);
        }
        else
        {
            if (_exportCurrentTarget != IntPtr.Zero)
                ExportSendXdndLeave(_exportCurrentTarget);
            _exportCurrentTarget = IntPtr.Zero;

            // No external target accepted the drop. Invoke the internal-drop callback
            // unconditionally — the view layer performs its own hit-test against all
            // open CP2 windows (main window + FileViewer instances) and ignores
            // drops outside those bounds.
            var cb = _onInternalDrop;
            _onInternalDrop = null;
            if (cb != null)
            {
                int snapX = rootX, snapY = rootY;
                Dispatcher.UIThread.Post(() => cb(snapX, snapY));
            }

            _exportTcs?.TrySetResult(DragDropEffects.None);
        }
        XFlush(_display);
    }

    private void ExportOnXdndStatus(ref XEvent ev)
    {
        if (!_isExporting && !_exportDropSent) return;
        _exportTargetAccepts = ((long)ev.data1 & 1) != 0;
    }

    private void ExportOnXdndFinished()
    {
        Debug.WriteLine("LinuxDrag: XdndFinished received");
        // Temp dir cleanup is intentionally NOT done here.  Most file managers send
        // XdndFinished before their async file-copy completes, so deleting the temp dir
        // immediately causes "file not found" errors at the destination.  The 60-second
        // timer in MainWindow is the correct cleanup path for this reason.
        _exportCurrentTarget = IntPtr.Zero;
        _exportDropSent = false;
        _exportTcs?.TrySetResult(DragDropEffects.Copy);
    }

    private void ExportOnSelectionRequest(ref XEvent ev)
    {
        nint requestor  = ev.msgOrSel;
        nint targetAtom = ev.data0;
        nint property   = ev.data1;
        nint time       = ev.data2;

        if (property == IntPtr.Zero) property = targetAtom;

        // If extraction is still running on the UI thread (BeginNativeDrag was called
        // before SetNativeDragPaths), spin-wait up to 10 s for paths to arrive.
        // In practice this only fires when the file manager sends SelectionRequest
        // very rapidly after button release on slow hardware or with large archives.
        if (!_exportPathsReady && (targetAtom == _atomTextUriList || targetAtom == _atomCP2DndData))
        {
            var deadline = DateTime.UtcNow.AddSeconds(10);
            while (!_exportPathsReady && DateTime.UtcNow < deadline)
                Thread.Sleep(20);
        }

        if (targetAtom == _atomTargets)
        {
            nint[] targets = { _atomTextUriList, _atomCP2DndData };
            XChangePropertyArr(_display, requestor, property, _atomAtom, 32,
                PropModeReplace, targets, targets.Length);
        }
        else if (targetAtom == _atomTextUriList)
        {
            byte[] bytes = Encoding.UTF8.GetBytes(_exportUriList);
            XChangeProperty(_display, requestor, property, _atomTextUriList, 8,
                PropModeReplace, bytes, bytes.Length);
        }
        else if (targetAtom == _atomCP2DndData && !string.IsNullOrEmpty(_exportClipJson))
        {
            byte[] bytes = Encoding.UTF8.GetBytes(_exportClipJson);
            XChangeProperty(_display, requestor, property, _atomCP2DndData, 8,
                PropModeReplace, bytes, bytes.Length);
        }
        else
        {
            property = IntPtr.Zero;
        }

        var notify = new XEvent
        {
            type     = SelectionNotify,
            window   = requestor,
            msgOrSel = _atomXdndSelection,
            fmt      = (int)targetAtom,
            data0    = property,
            data1    = time,
        };
        XSendEvent(_display, requestor, false, 0, ref notify);
        XFlush(_display);

        if (targetAtom == _atomTextUriList && _exportDropSent)
        {
            _ = Task.Delay(500).ContinueWith(_ =>
                _exportTcs?.TrySetResult(DragDropEffects.Copy));
        }
    }

    private void ExportSendXdndEnter(nint target)
    {
        var ev = new XEvent
        {
            type     = ClientMessage,
            window   = target,
            msgOrSel = _atomXdndEnter,
            fmt      = 32,
            data0    = _proxyWindow,
            data1    = (nint)(XdndVersion << 24), // version; bit 0 = 0 (≤3 types in data2-4)
            data2    = _atomTextUriList,
            data3    = _atomCP2DndData,
        };
        XSendEvent(_display, target, false, 0, ref ev);
        XFlush(_display);
    }

    private void ExportSendXdndPosition(nint target, int xRoot, int yRoot)
    {
        // XDND v5 XdndPosition layout:
        //   data.l[0] = source window
        //   data.l[1] = reserved (zero)
        //   data.l[2] = (rootX << 16) | rootY
        //   data.l[3] = timestamp
        //   data.l[4] = requested action
        nint xy = (nint)(((long)xRoot << 16) | (uint)(yRoot & 0xFFFF));
        var ev = new XEvent
        {
            type     = ClientMessage,
            window   = target,
            msgOrSel = _atomXdndPosition,
            fmt      = 32,
            data0    = _proxyWindow,
            data1    = IntPtr.Zero,         // reserved
            data2    = xy,                  // root-relative coordinates
            data3    = CurrentTime,         // timestamp
            data4    = _atomXdndActionCopy,
        };
        XSendEvent(_display, target, false, 0, ref ev);
        XFlush(_display);
    }

    private void ExportSendXdndLeave(nint target)
    {
        var ev = new XEvent
        {
            type     = ClientMessage,
            window   = target,
            msgOrSel = _atomXdndLeave,
            fmt      = 32,
            data0    = _proxyWindow,
        };
        XSendEvent(_display, target, false, 0, ref ev);
        XFlush(_display);
    }

    private void ExportSendXdndDrop(nint target)
    {
        var ev = new XEvent
        {
            type     = ClientMessage,
            window   = target,
            msgOrSel = _atomXdndDrop,
            fmt      = 32,
            data0    = _proxyWindow,
            data1    = IntPtr.Zero,
            data2    = CurrentTime,
        };
        XSendEvent(_display, target, false, 0, ref ev);
        XFlush(_display);
    }

    // ── Window tree helpers ───────────────────────────────────────────────────

    private nint FindXdndAwareWindow(int xRoot, int yRoot)
    {
        // topLevel is already the child of the root under the pointer — the same
        // info XQueryPointer returns. Re-query here so we get the correct window
        // even if PollPointerForExport's XQueryPointer returned stale data.
        XQueryPointer(_display, _rootWindow,
            out _, out nint topLevel,
            out _, out _, out _, out _, out _);

        if (topLevel == IntPtr.Zero || topLevel == _avaloniaWin || topLevel == _proxyWindow)
            return IntPtr.Zero;

        if (!IsXdndAware(topLevel)) return IntPtr.Zero;

        nint proxy = ReadSingleWindowProperty(topLevel, XInternAtom(_display, "XdndProxy", true));
        if (proxy != IntPtr.Zero && proxy != topLevel && IsXdndAware(proxy))
            return proxy;

        return topLevel;
    }

    private bool IsXdndAware(nint window)
    {
        int rc = XGetWindowProperty(_display, window, _atomXdndAware,
            0, 1, delete: false, reqType: _atomAtom,
            out _, out _, out nint nItems, out _, out nint propReturn);
        if (rc != 0 || propReturn == IntPtr.Zero || nItems == 0) return false;
        XFree(propReturn);
        return true;
    }

    private nint ReadSingleWindowProperty(nint window, nint property)
    {
        if (property == IntPtr.Zero) return IntPtr.Zero;
        int rc = XGetWindowProperty(_display, window, property,
            0, 1, delete: false, reqType: _atomWindow,
            out _, out _, out nint nItems, out _, out nint propReturn);
        if (rc != 0 || propReturn == IntPtr.Zero || nItems == 0) return IntPtr.Zero;
        nint value = Marshal.ReadIntPtr(propReturn);
        XFree(propReturn);
        return value;
    }

    // ── X11 property helpers ──────────────────────────────────────────────────

    private string? ReadStringProperty(nint window, nint property)
    {
        int rc = XGetWindowProperty(_display, window, property,
            0, 65536, delete: true, reqType: IntPtr.Zero,
            out _, out int fmt, out nint nItems, out _, out nint propReturn);
        if (rc != 0 || propReturn == IntPtr.Zero || nItems == 0) return null;
        try
        {
            int byteLen = fmt == 16 ? (int)nItems * 2 : (int)nItems;
            byte[] bytes = new byte[byteLen];
            Marshal.Copy(propReturn, bytes, 0, byteLen);
            return Encoding.UTF8.GetString(bytes);
        }
        finally { XFree(propReturn); }
    }

    private List<nint> ReadAtomList(nint window, string propertyName)
    {
        nint prop = XInternAtom(_display, propertyName, onlyIfExists: true);
        if (prop == IntPtr.Zero) return new();
        int rc = XGetWindowProperty(_display, window, prop,
            0, 65536, delete: false, reqType: _atomAtom,
            out _, out int fmt, out nint nItems, out _, out nint propReturn);
        var result = new List<nint>();
        if (rc != 0 || propReturn == IntPtr.Zero || nItems == 0) return result;
        try
        {
            int sz = fmt / 8;
            for (int i = 0; i < (int)nItems; i++)
            {
                nint a = sz == 8 ? Marshal.ReadIntPtr(propReturn, i * 8)
                                 : (nint)Marshal.ReadInt32(propReturn, i * 4);
                if (a != IntPtr.Zero) result.Add(a);
            }
        }
        finally { XFree(propReturn); }
        return result;
    }

    // ── URI list parser ───────────────────────────────────────────────────────

    private static string[] ParseUriList(string uriList)
    {
        var paths = new List<string>();
        foreach (string raw in uriList.Split(new[] { '\r', '\n' },
                     StringSplitOptions.RemoveEmptyEntries))
        {
            string line = raw.Trim();
            if (line.Length == 0 || line[0] == '#') continue;
            if (!Uri.TryCreate(line, UriKind.Absolute, out Uri? uri) || !uri.IsFile) continue;
            string lp = uri.LocalPath;
            if (!string.IsNullOrEmpty(lp)) paths.Add(lp);
        }
        Debug.WriteLine($"LinuxDrag: parsed {paths.Count} path(s)");
        return paths.ToArray();
    }
}
