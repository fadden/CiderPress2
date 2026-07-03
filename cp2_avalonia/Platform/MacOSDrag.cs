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
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;

using cp2_avalonia.Services;

namespace cp2_avalonia.Platform;

/// <summary>
/// macOS-specific drag helper. Uses NSFilePromiseProvider so that Finder and
/// other Cocoa drop targets accept the drag and receive the actual files when
/// the drop occurs. Files must be pre-extracted to a temporary directory; the
/// promise provider fulfills the promise by copying from there to the drop destination.
///
/// One ObjC class, CP2PromiseDelegate, is registered lazily on first use. It
/// implements both NSFilePromiseProviderDelegate (fileNameForType, writePromiseToURL)
/// and NSDraggingSource (sourceOperationMaskForDraggingContext, endedAtPoint:operation).
/// A single shared instance is used as both the provider delegate and drag source.
/// </summary>
[SupportedOSPlatform("macos")]
internal static class MacOSDrag
{
    // ── ObjC / CoreFoundation P/Invoke ───────────────────────────────────────

    [DllImport("/usr/lib/libobjc.dylib")]
    private static extern IntPtr objc_getClass(string name);

    [DllImport("/usr/lib/libobjc.dylib")]
    private static extern IntPtr objc_allocateClassPair(IntPtr superclass, string name, nint extra);

    [DllImport("/usr/lib/libobjc.dylib")]
    private static extern void objc_registerClassPair(IntPtr cls);

    [DllImport("/usr/lib/libobjc.dylib")]
    private static extern bool class_addMethod(IntPtr cls, IntPtr sel, IntPtr imp, string types);

    [DllImport("/usr/lib/libobjc.dylib")]
    private static extern IntPtr sel_registerName(string name);

    [DllImport("/usr/lib/libobjc.dylib", EntryPoint = "objc_msgSend")]
    private static extern IntPtr Send(IntPtr obj, IntPtr sel);

    [DllImport("/usr/lib/libobjc.dylib", EntryPoint = "objc_msgSend")]
    private static extern IntPtr Send1(IntPtr obj, IntPtr sel, IntPtr a);

    [DllImport("/usr/lib/libobjc.dylib", EntryPoint = "objc_msgSend")]
    private static extern IntPtr Send2(IntPtr obj, IntPtr sel, IntPtr a, IntPtr b);

    [DllImport("/usr/lib/libobjc.dylib", EntryPoint = "objc_msgSend")]
    private static extern IntPtr Send3(IntPtr obj, IntPtr sel, IntPtr a, IntPtr b, IntPtr c);

    // NSArray arrayWithObjects:count: – ptr is a C array of id, count is NSUInteger
    [DllImport("/usr/lib/libobjc.dylib", EntryPoint = "objc_msgSend")]
    private static extern IntPtr SendArrayCount(IntPtr obj, IntPtr sel, IntPtr ptr, nint count);

    [DllImport("/usr/lib/libobjc.dylib", EntryPoint = "objc_msgSend")]
    private static extern void SendVoid(IntPtr obj, IntPtr sel);

    [DllImport("/usr/lib/libobjc.dylib", EntryPoint = "objc_msgSend")]
    private static extern bool SendBool2(IntPtr obj, IntPtr sel, IntPtr a, IntPtr b);

    [DllImport("/usr/lib/libobjc.dylib", EntryPoint = "objc_msgSend")]
    private static extern bool SendBool1(IntPtr obj, IntPtr sel, IntPtr a);

    // objc_msgSendSuper: calls the superclass implementation of a method.
    [DllImport("/usr/lib/libobjc.dylib", EntryPoint = "objc_msgSendSuper")]
    private static extern IntPtr SendSuper1(ref ObjcSuper sup, IntPtr sel, IntPtr a);

    [DllImport("/usr/lib/libobjc.dylib", EntryPoint = "objc_msgSendSuper")]
    private static extern nuint SendSuperUInt2(ref ObjcSuper sup, IntPtr sel, IntPtr a, IntPtr b);

    // Returns NSUInteger (used for -[NSEvent type]).
    [DllImport("/usr/lib/libobjc.dylib", EntryPoint = "objc_msgSend")]
    private static extern nuint SendUInt(IntPtr obj, IntPtr sel);

    // Returns NSPoint (used for -[NSEvent locationInWindow]).  A 16-byte all-double
    // struct is returned in FP registers on both x86_64 and arm64, so plain
    // objc_msgSend (not objc_msgSend_stret) is correct.
    [DllImport("/usr/lib/libobjc.dylib", EntryPoint = "objc_msgSend")]
    private static extern NSPoint SendPoint(IntPtr obj, IntPtr sel);

    // -[NSDraggingItem setDraggingFrame:contents:] – NSRect passed by value, then id.
    [DllImport("/usr/lib/libobjc.dylib", EntryPoint = "objc_msgSend")]
    private static extern void SendSetDragFrame(IntPtr obj, IntPtr sel, NSRect frame, IntPtr contents);

    [DllImport("/System/Library/Frameworks/CoreFoundation.framework/CoreFoundation")]
    private static extern IntPtr CFStringCreateWithCString(IntPtr alloc, string str, uint enc);

    [DllImport("/System/Library/Frameworks/CoreFoundation.framework/CoreFoundation")]
    private static extern void CFRelease(IntPtr cf);

    private const uint kCFStringEncodingUTF8 = 0x08000100;

    // ── Shared selectors ─────────────────────────────────────────────────────

    private static readonly IntPtr SelAlloc       = sel_registerName("alloc");
    private static readonly IntPtr SelInit        = sel_registerName("init");
    private static readonly IntPtr SelRelease     = sel_registerName("release");
    private static readonly IntPtr SelAutorelease = sel_registerName("autorelease");

    // ── State ────────────────────────────────────────────────────────────────

    // Maps NSFilePromiseProvider native pointer → temp file path.
    private static readonly Dictionary<IntPtr, string> sProviderPaths = new();
    private static readonly object sLock = new();

    // Invoked on the main thread when the drag session terminates.
    private static Action? sOnDragEnd;

    // CP2 clipboard JSON for the current drag; served to drop targets that request
    // CROSS_INSTANCE_FORMAT via the CP2PromiseProvider subclass.
    private static string sClipJson = string.Empty;

    // GCHandles prevent the delegate closures from being GC'd while ObjC holds
    // a pointer to the native thunk.
    private static GCHandle sFileNameHandle;
    private static GCHandle sWritePromiseHandle;
    private static GCHandle sDragOpMaskHandle;
    private static GCHandle sDragEndedHandle;
    private static GCHandle sWritableTypesHandle;
    private static GCHandle sPropListHandle;
    private static GCHandle sWritingOptionsHandle;

    // Delegate/source class + singleton instance reused across drags.
    private static IntPtr sDelegateClass;
    private static IntPtr sDelegateInst;
    // NSFilePromiseProvider subclass that also carries the CP2 JSON pasteboard type,
    // so the cross-instance type rides on each file item (no extra dragging item,
    // hence no spurious "+1" badge on the drag image).
    private static IntPtr sProviderClass;

    // ── ObjC method signatures ────────────────────────────────────────────────

    // filePromiseProvider:fileNameForType:  →  id (NSString*)
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate IntPtr FileNameCb(IntPtr self, IntPtr sel, IntPtr provider, IntPtr type);

    // filePromiseProvider:writePromiseToURL:completionHandler:  →  void
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate void WritePromiseCb(IntPtr self, IntPtr sel, IntPtr provider,
                                         IntPtr url, IntPtr completionBlock);

    // draggingSession:sourceOperationMaskForDraggingContext:  →  NSDragOperation (NSUInteger)
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate nuint DragOpMaskCb(IntPtr self, IntPtr sel, IntPtr session, nint ctx);

    // draggingSession:endedAtPoint:operation:  →  void
    // NSPoint is {double x, double y}; on both arm64 and x86_64 the two doubles
    // are passed in FP/SIMD registers (v0/v1 on arm64, xmm0/xmm1 on x86_64),
    // independent of the preceding pointer arguments in integer registers.
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate void DragEndedCb(IntPtr self, IntPtr sel, IntPtr session,
                                      double ptX, double ptY, nuint op);

    // writableTypesForPasteboard:  →  id (NSArray*)
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate IntPtr WritableTypesCb(IntPtr self, IntPtr sel, IntPtr pasteboard);

    // pasteboardPropertyListForType:  →  id
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate IntPtr PropListCb(IntPtr self, IntPtr sel, IntPtr type);

    // writingOptionsForType:pasteboard:  →  NSPasteboardWritingOptions (NSUInteger)
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate nuint WritingOptionsCb(IntPtr self, IntPtr sel, IntPtr type, IntPtr pasteboard);

    // ObjC completion block: void (^)(NSError*)
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate void BlockInvoke(IntPtr block, IntPtr error);

    // NSDragOperation bit flags
    private const nuint NSDragOpCopy = 1;
    private const nuint NSDragOpMove = 16;

    // NSPoint = { double x; double y; }
    [StructLayout(LayoutKind.Sequential)]
    private struct NSPoint
    {
        public double X, Y;
        public NSPoint(double x, double y) { X = x; Y = y; }
    }

    // struct objc_super { id receiver; Class super_class; } – for objc_msgSendSuper.
    [StructLayout(LayoutKind.Sequential)]
    private struct ObjcSuper
    {
        public IntPtr Receiver;
        public IntPtr SuperClass;
    }

    // NSRect = { NSPoint origin {x,y}; NSSize size {w,h}; } – four contiguous doubles.
    [StructLayout(LayoutKind.Sequential)]
    private struct NSRect
    {
        public double X, Y, W, H;
        public NSRect(double x, double y, double w, double h) { X = x; Y = y; W = w; H = h; }
    }

    // NSEventType values that beginDraggingSessionWithItems:event:source: accepts.
    // A drag must originate from a mouse-down or mouse-dragged event.
    private static bool IsMouseDragEvent(nuint t) =>
        t == 1  /* LeftMouseDown    */ || t == 6  /* LeftMouseDragged  */ ||
        t == 3  /* RightMouseDown   */ || t == 7  /* RightMouseDragged */ ||
        t == 25 /* OtherMouseDown   */ || t == 27 /* OtherMouseDragged */;

    // ── One-time class registration ───────────────────────────────────────────

    /// <summary>
    /// Registers the CP2PromiseDelegate ObjC class the first time it is needed.
    /// Returns true on success.
    /// </summary>
    private static bool EnsureClasses()
    {
        if (sDelegateClass != IntPtr.Zero) return true;

        Debug.WriteLine("MacOSDrag: EnsureClasses – registering CP2PromiseDelegate");

        IntPtr nsObject = objc_getClass("NSObject");
        Debug.WriteLine($"MacOSDrag: NSObject=0x{nsObject:x}");

        sDelegateClass = objc_allocateClassPair(nsObject, "CP2PromiseDelegate", 0);
        if (sDelegateClass == IntPtr.Zero)
        {
            Debug.WriteLine("MacOSDrag: objc_allocateClassPair returned nil (name conflict?)");
            return false;
        }
        Debug.WriteLine($"MacOSDrag: sDelegateClass=0x{sDelegateClass:x}");

        // ── NSFilePromiseProviderDelegate methods ──────────────────────────

        var fnCb = new FileNameCb(FileNameImpl);
        sFileNameHandle = GCHandle.Alloc(fnCb);
        bool ok1 = class_addMethod(sDelegateClass,
            sel_registerName("filePromiseProvider:fileNameForType:"),
            Marshal.GetFunctionPointerForDelegate(fnCb), "@@:@@");
        Debug.WriteLine($"MacOSDrag: added fileNameForType: {ok1}");

        var wpCb = new WritePromiseCb(WritePromiseImpl);
        sWritePromiseHandle = GCHandle.Alloc(wpCb);
        bool ok2 = class_addMethod(sDelegateClass,
            sel_registerName("filePromiseProvider:writePromiseToURL:completionHandler:"),
            Marshal.GetFunctionPointerForDelegate(wpCb), "v@:@@?");
        Debug.WriteLine($"MacOSDrag: added writePromiseToURL: {ok2}");

        // ── NSDraggingSource methods ───────────────────────────────────────

        var omCb = new DragOpMaskCb(DragOpMaskImpl);
        sDragOpMaskHandle = GCHandle.Alloc(omCb);
        bool ok3 = class_addMethod(sDelegateClass,
            sel_registerName("draggingSession:sourceOperationMaskForDraggingContext:"),
            Marshal.GetFunctionPointerForDelegate(omCb), "Q@:@l");
        Debug.WriteLine($"MacOSDrag: added sourceOperationMask: {ok3}");

        var deCb = new DragEndedCb(DragEndedImpl);
        sDragEndedHandle = GCHandle.Alloc(deCb);
        bool ok4 = class_addMethod(sDelegateClass,
            sel_registerName("draggingSession:endedAtPoint:operation:"),
            Marshal.GetFunctionPointerForDelegate(deCb), "v@:@{CGPoint=dd}Q");
        Debug.WriteLine($"MacOSDrag: added endedAtPoint: {ok4}");

        objc_registerClassPair(sDelegateClass);
        Debug.WriteLine("MacOSDrag: class registered");

        sDelegateInst = Send(Send(sDelegateClass, SelAlloc), SelInit);
        Debug.WriteLine($"MacOSDrag: sDelegateInst=0x{sDelegateInst:x}");

        if (sDelegateInst == IntPtr.Zero)
        {
            Debug.WriteLine("MacOSDrag: failed to create delegate instance");
            sDelegateClass = IntPtr.Zero; // allow retry
            return false;
        }

        // ── CP2PromiseProvider : NSFilePromiseProvider ─────────────────────
        // Subclass that adds CROSS_INSTANCE_FORMAT to the file item's own pasteboard
        // so cross-instance CP2 drops work without a second dragging item.
        IntPtr nsFPP = objc_getClass("NSFilePromiseProvider");
        sProviderClass = objc_allocateClassPair(nsFPP, "CP2PromiseProvider", 0);
        if (sProviderClass == IntPtr.Zero)
        {
            Debug.WriteLine("MacOSDrag: failed to allocate CP2PromiseProvider");
            sDelegateClass = IntPtr.Zero; // allow retry
            return false;
        }

        var wtCb = new WritableTypesCb(WritableTypesImpl);
        sWritableTypesHandle = GCHandle.Alloc(wtCb);
        bool ok5 = class_addMethod(sProviderClass,
            sel_registerName("writableTypesForPasteboard:"),
            Marshal.GetFunctionPointerForDelegate(wtCb), "@@:@");

        var plCb = new PropListCb(PropertyListForTypeImpl);
        sPropListHandle = GCHandle.Alloc(plCb);
        bool ok6 = class_addMethod(sProviderClass,
            sel_registerName("pasteboardPropertyListForType:"),
            Marshal.GetFunctionPointerForDelegate(plCb), "@@:@");

        var woCb = new WritingOptionsCb(WritingOptionsImpl);
        sWritingOptionsHandle = GCHandle.Alloc(woCb);
        bool ok7 = class_addMethod(sProviderClass,
            sel_registerName("writingOptionsForType:pasteboard:"),
            Marshal.GetFunctionPointerForDelegate(woCb), "Q@:@@");

        objc_registerClassPair(sProviderClass);
        Debug.WriteLine($"MacOSDrag: CP2PromiseProvider registered (wt={ok5} pl={ok6} wo={ok7})");

        Debug.WriteLine("MacOSDrag: EnsureClasses complete");
        return true;
    }

    // ── Delegate implementations ──────────────────────────────────────────────

    private static IntPtr FileNameImpl(IntPtr self, IntPtr sel, IntPtr provider, IntPtr type)
    {
        Debug.WriteLine($"MacOSDrag: FileNameImpl provider=0x{provider:x}");
        string path;
        lock (sLock)
        {
            if (!sProviderPaths.TryGetValue(provider, out path!))
                path = "file";
        }
        // Return an autoreleased NSString (standard convention for non-alloc/copy/new methods).
        IntPtr nsStr = CFStringCreateWithCString(IntPtr.Zero,
            Path.GetFileName(path), kCFStringEncodingUTF8);
        return Send(nsStr, SelAutorelease);
    }

    private static void WritePromiseImpl(IntPtr self, IntPtr sel, IntPtr provider,
                                         IntPtr url, IntPtr completionBlock)
    {
        string? src = null;
        lock (sLock) { sProviderPaths.TryGetValue(provider, out src); }

        if (src != null)
        {
            try
            {
                string? dest = GetUrlPath(url);
                if (dest != null)
                    CopyItem(src, dest);
                else
                    Debug.WriteLine("MacOSDrag: WritePromise – could not get dest path");
            }
            catch (Exception ex)
            {
                AppLog.W("macOS drag: file promise copy failed", ex);
            }
        }

        InvokeBlock(completionBlock, IntPtr.Zero); // nil error = success
    }

    /// <summary>
    /// Copies <paramref name="src"/> to <paramref name="dest"/>.
    /// If <paramref name="src"/> is a directory, copies it recursively.
    /// <paramref name="dest"/> is the full destination path (not the parent directory).
    /// </summary>
    private static void CopyItem(string src, string dest)
    {
        if (Directory.Exists(src))
        {
            Directory.CreateDirectory(dest);
            foreach (string entry in Directory.GetFileSystemEntries(src))
            {
                string name = Path.GetFileName(entry);
                CopyItem(entry, Path.Combine(dest, name));
            }
        }
        else
        {
            File.Copy(src, dest, overwrite: true);
        }
    }

    private static nuint DragOpMaskImpl(IntPtr self, IntPtr sel, IntPtr session, nint ctx)
    {
        Debug.WriteLine($"MacOSDrag: DragOpMaskImpl ctx={ctx}");
        return NSDragOpCopy | NSDragOpMove;
    }

    private static void DragEndedImpl(IntPtr self, IntPtr sel, IntPtr session,
                                      double ptX, double ptY, nuint op)
    {
        Debug.WriteLine($"MacOSDrag: drag ended op=0x{op:x}");
        lock (sLock) { sProviderPaths.Clear(); }
        Action? cb = sOnDragEnd;
        sOnDragEnd = null;
        cb?.Invoke();
    }

    // ── CP2PromiseProvider (NSFilePromiseProvider subclass) implementations ────

    // writableTypesForPasteboard: – super's promise types plus CROSS_INSTANCE_FORMAT.
    // NSPasteboardTypeString UTI – plain text, reliably surfaced by Avalonia's native
    // IDataObject wrapper as DataFormats.Text.  We expose the CP2 JSON under this type
    // in addition to our private UTI so that the receiving CP2 instance can detect a
    // cross-instance drag even when Avalonia's custom-UTI lookup is unavailable.
    //
    // public.file-url – exposes the already-extracted temp file as a concrete file URL
    // so that non-Finder apps (Mail, TextEdit, etc.) can receive the file directly.
    // Finder uses the NSFilePromiseProvider promise; everything else uses this.
    private const string NsPasteboardTypeString = "public.utf8-plain-text";
    private const string NsPasteboardTypeFileUrl = "public.file-url";

    private static IntPtr WritableTypesImpl(IntPtr self, IntPtr sel, IntPtr pasteboard)
    {
        var sup = new ObjcSuper { Receiver = self, SuperClass = objc_getClass("NSFilePromiseProvider") };
        IntPtr superTypes = SendSuper1(ref sup, sel, pasteboard);
        // mutableCopy gives an owned (+1) NSMutableArray.
        IntPtr mutable = Send(superTypes, sel_registerName("mutableCopy"));

        // File URL – allows non-Finder drop targets (Mail, browsers, etc.) to receive
        // the concrete temp file rather than the CP2 JSON fallback text.
        string? srcPath = null;
        lock (sLock) { sProviderPaths.TryGetValue(self, out srcPath); }
        if (srcPath != null)
        {
            IntPtr fileUrlFmt = MakeNSStr(NsPasteboardTypeFileUrl);
            Send1(mutable, sel_registerName("addObject:"), fileUrlFmt);
            CFRelease(fileUrlFmt);
        }

        // Private UTI – read by CP2's IsCrossInstanceCp2Drop if Avalonia supports it.
        IntPtr fmt = MakeNSStr(Models.ClipInfo.CROSS_INSTANCE_FORMAT);
        Send1(mutable, sel_registerName("addObject:"), fmt);
        CFRelease(fmt);

        // Plain text – guaranteed to be surfaced by Avalonia's IDataObject as
        // DataFormats.Text, used as a reliable fallback for cross-instance detection.
        IntPtr textFmt = MakeNSStr(NsPasteboardTypeString);
        Send1(mutable, sel_registerName("addObject:"), textFmt);
        CFRelease(textFmt);

        return Send(mutable, SelAutorelease);
    }

    // pasteboardPropertyListForType: – CP2 JSON for our types, file URL for public.file-url,
    // super for promise types.
    private static IntPtr PropertyListForTypeImpl(IntPtr self, IntPtr sel, IntPtr type)
    {
        IntPtr fmtFileUrl = MakeNSStr(NsPasteboardTypeFileUrl);
        bool isFileUrl = SendBool1(type, sel_registerName("isEqualToString:"), fmtFileUrl);
        CFRelease(fmtFileUrl);
        if (isFileUrl)
        {
            string? srcPath = null;
            lock (sLock) { sProviderPaths.TryGetValue(self, out srcPath); }
            if (srcPath != null)
            {
                // Return the file:// URL as an NSString – the standard property-list
                // representation for public.file-url that AppKit and most apps expect.
                string fileUrl = new Uri(srcPath).AbsoluteUri;
                return Send(MakeNSStr(fileUrl), SelAutorelease);
            }
            return IntPtr.Zero;
        }

        IntPtr fmtUti = MakeNSStr(Models.ClipInfo.CROSS_INSTANCE_FORMAT);
        bool isCp2 = SendBool1(type, sel_registerName("isEqualToString:"), fmtUti);
        CFRelease(fmtUti);
        if (isCp2)
            return Send(MakeNSStr(sClipJson), SelAutorelease);

        IntPtr fmtText = MakeNSStr(NsPasteboardTypeString);
        bool isText = SendBool1(type, sel_registerName("isEqualToString:"), fmtText);
        CFRelease(fmtText);
        if (isText)
            return Send(MakeNSStr(sClipJson), SelAutorelease);

        var sup = new ObjcSuper { Receiver = self, SuperClass = objc_getClass("NSFilePromiseProvider") };
        return SendSuper1(ref sup, sel, type);
    }

    // writingOptionsForType:pasteboard: – synchronous (0) for our types, super otherwise.
    private static nuint WritingOptionsImpl(IntPtr self, IntPtr sel, IntPtr type, IntPtr pasteboard)
    {
        IntPtr fmtFileUrl = MakeNSStr(NsPasteboardTypeFileUrl);
        bool isFileUrl = SendBool1(type, sel_registerName("isEqualToString:"), fmtFileUrl);
        CFRelease(fmtFileUrl);
        if (isFileUrl) return 0;

        IntPtr fmtUti = MakeNSStr(Models.ClipInfo.CROSS_INSTANCE_FORMAT);
        bool isCp2 = SendBool1(type, sel_registerName("isEqualToString:"), fmtUti);
        CFRelease(fmtUti);
        if (isCp2) return 0;

        IntPtr fmtText = MakeNSStr(NsPasteboardTypeString);
        bool isText = SendBool1(type, sel_registerName("isEqualToString:"), fmtText);
        CFRelease(fmtText);
        if (isText) return 0;

        var sup = new ObjcSuper { Receiver = self, SuperClass = objc_getClass("NSFilePromiseProvider") };
        return SendSuperUInt2(ref sup, sel, type, pasteboard);
    }

    // ── Public API ────────────────────────────────────────────────────────────

    /// <summary>
    /// Initiates a native macOS NSDraggingSession with NSFilePromiseProvider items
    /// for each extracted file, plus an NSPasteboardItem carrying the CP2 JSON for
    /// same-process and cross-instance CP2 drops.
    ///
    /// This call is fire-and-forget; <paramref name="onDragEnd"/> is invoked on the
    /// main thread when the drag session terminates (drop or cancel).
    /// </summary>
    /// <param name="nsWindow">NSWindow handle from Avalonia's TopLevel.PlatformImpl.Handle.</param>
    /// <param name="sourcePaths">Extracted temp-file paths to promise.</param>
    /// <param name="clipJson">Serialised CP2 clipboard JSON for cross-instance detection.</param>
    /// <param name="onDragEnd">Called when the drag ends; resets drag state and deletes temp dir.</param>
    /// <returns>True if the native drag session was successfully started.</returns>
    public static bool StartNativeDrag(IntPtr nsWindow, IReadOnlyList<string> sourcePaths,
                                       string clipJson, Action onDragEnd)
    {
        Debug.WriteLine($"MacOSDrag: StartNativeDrag – {sourcePaths.Count} paths, nsWindow=0x{nsWindow:x}");

        if (!EnsureClasses())
            return false;

        // Get the content NSView (drag must be initiated from a view, not the window).
        IntPtr contentView = Send(nsWindow, sel_registerName("contentView"));
        Debug.WriteLine($"MacOSDrag: contentView=0x{contentView:x}");
        if (contentView == IntPtr.Zero)
        {
            Debug.WriteLine("MacOSDrag: no contentView – falling back to Avalonia DoDragDrop");
            return false;
        }

        // The current NSEvent provides the mouse position for the drag origin.
        IntPtr nsAppCls = objc_getClass("NSApplication");
        IntPtr nsApp    = Send(nsAppCls, sel_registerName("sharedApplication"));
        IntPtr nsEvent  = Send(nsApp, sel_registerName("currentEvent"));
        Debug.WriteLine($"MacOSDrag: nsApp=0x{nsApp:x}, nsEvent=0x{nsEvent:x}");
        if (nsEvent == IntPtr.Zero)
        {
            Debug.WriteLine("MacOSDrag: no current NSEvent – falling back to Avalonia DoDragDrop");
            return false;
        }

        // beginDraggingSessionWithItems:event:source: faults if the event is not a
        // mouse-down/mouse-dragged event, so validate before proceeding.
        nuint evtType = SendUInt(nsEvent, sel_registerName("type"));
        Debug.WriteLine($"MacOSDrag: nsEvent type={evtType}");
        if (!IsMouseDragEvent(evtType))
        {
            Debug.WriteLine("MacOSDrag: current event is not a mouse drag – falling back to DoDragDrop");
            return false;
        }

        // Every NSDraggingItem must have a non-empty draggingFrame and an image before
        // beginDraggingSession, or AppKit faults while laying out the drag image.  The
        // frame is in the content view's (non-flipped) coordinate system; anchoring it
        // at the mouse-down location makes the image appear under the cursor instead of
        // animating up from the window's bottom-left corner.  The content view fills the
        // window, so locationInWindow already matches the view's coordinate space.
        NSPoint mouseLoc = SendPoint(nsEvent, sel_registerName("locationInWindow"));
        const double IconSize = 32.0;
        var iconFrame = new NSRect(mouseLoc.X - IconSize / 2, mouseLoc.Y - IconSize / 2,
                                   IconSize, IconSize);

        IntPtr nsWorkspace     = Send(objc_getClass("NSWorkspace"), sel_registerName("sharedWorkspace"));
        IntPtr selIconForFile  = sel_registerName("iconForFile:");
        IntPtr selSetDragFrame = sel_registerName("setDraggingFrame:contents:");
        // Fallback image if a file's icon can't be resolved.
        IntPtr fallbackIcon    = Send(nsApp, sel_registerName("applicationIconImage"));
        Debug.WriteLine($"MacOSDrag: mouseLoc=({mouseLoc.X},{mouseLoc.Y}), fallbackIcon=0x{fallbackIcon:x}");

        // CP2 JSON served to drop targets that request CROSS_INSTANCE_FORMAT (see
        // CP2PromiseProvider).  Set before building providers.
        sClipJson = clipJson ?? string.Empty;

        // ── Build the array of NSDraggingItems ────────────────────────────

        // CP2PromiseProvider (NSFilePromiseProvider subclass) so each file item also
        // carries the CP2 JSON type — no separate dragging item, no "+1" count badge.
        IntPtr clsProvider = sProviderClass;
        IntPtr selInitFT   = sel_registerName("initWithFileType:delegate:");
        IntPtr clsDragItem = objc_getClass("NSDraggingItem");
        IntPtr selInitPBW  = sel_registerName("initWithPasteboardWriter:");

        Debug.WriteLine($"MacOSDrag: CP2PromiseProvider=0x{clsProvider:x}, NSDraggingItem=0x{clsDragItem:x}");

        // "public.data" – generic UTI; Finder infers the real type from the file extension.
        IntPtr utiStr = CFStringCreateWithCString(IntPtr.Zero, "public.data", kCFStringEncodingUTF8);

        var allItems = new List<IntPtr>();
        IntPtr firstIcon = IntPtr.Zero;

        // One NSFilePromiseProvider item per extracted file.
        foreach (string src in sourcePaths)
        {
            IntPtr provider = Send2(Send(clsProvider, SelAlloc), selInitFT, utiStr, sDelegateInst);
            Debug.WriteLine($"MacOSDrag: provider=0x{provider:x} for {Path.GetFileName(src)}");
            if (provider == IntPtr.Zero)
            {
                Debug.WriteLine("MacOSDrag: NSFilePromiseProvider init returned nil – skipping file");
                continue;
            }
            lock (sLock) { sProviderPaths[provider] = src; }

            // Real icon for the file (document icon for files, folder icon for folders);
            // iconForFile: returns an autoreleased NSImage we do not own.
            IntPtr pathStr = CFStringCreateWithCString(IntPtr.Zero, src, kCFStringEncodingUTF8);
            IntPtr icon = Send1(nsWorkspace, selIconForFile, pathStr);
            CFRelease(pathStr);
            if (icon == IntPtr.Zero) icon = fallbackIcon;
            if (firstIcon == IntPtr.Zero) firstIcon = icon;

            IntPtr dragItem = Send1(Send(clsDragItem, SelAlloc), selInitPBW, provider);
            SendSetDragFrame(dragItem, selSetDragFrame, iconFrame, icon);
            allItems.Add(dragItem);
            SendVoid(provider, SelRelease); // retained by the drag item
        }

        CFRelease(utiStr);

        // The CP2 JSON (CROSS_INSTANCE_FORMAT) now rides on each file item's pasteboard
        // via CP2PromiseProvider, so no separate dragging item is needed.  _ = firstIcon
        // keeps the variable meaningful for future per-item image tuning.
        _ = firstIcon;

        if (allItems.Count == 0)
        {
            Debug.WriteLine("MacOSDrag: no items to drag");
            return false;
        }

        IntPtr itemsArray = CreateNSArray(allItems);
        foreach (IntPtr it in allItems) SendVoid(it, SelRelease);

        Debug.WriteLine($"MacOSDrag: itemsArray=0x{itemsArray:x} ({allItems.Count} items)");
        Debug.WriteLine($"MacOSDrag: sDelegateInst=0x{sDelegateInst:x} (source + delegate)");

        // Store the end callback before starting the session.
        sOnDragEnd = onDragEnd;

        // [contentView beginDraggingSessionWithItems:array event:event source:delegate]
        // sDelegateInst implements both NSFilePromiseProviderDelegate and NSDraggingSource.
        Debug.WriteLine("MacOSDrag: calling beginDraggingSessionWithItems:event:source:");
        IntPtr session = Send3(contentView,
            sel_registerName("beginDraggingSessionWithItems:event:source:"),
            itemsArray, nsEvent, sDelegateInst);
        Debug.WriteLine($"MacOSDrag: session=0x{session:x}");

        // NOTE: itemsArray comes from +[NSArray arrayWithObjects:count:], which returns
        // an autoreleased object we do NOT own.  Do not release it here — the drag session
        // retains what it needs and the autorelease pool frees the array.  Releasing it
        // caused a double-free that crashed in objc_release during the pool drain.

        if (session == IntPtr.Zero)
        {
            Debug.WriteLine("MacOSDrag: beginDraggingSession returned nil");
            sOnDragEnd = null;
            return false;
        }

        Debug.WriteLine("MacOSDrag: native drag session started successfully");
        return true;
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    // Creates an owned (+1) NSString via toll-free-bridged CFString.  Caller releases
    // with CFRelease (or transfers ownership to the autorelease pool / a container).
    private static IntPtr MakeNSStr(string s) =>
        CFStringCreateWithCString(IntPtr.Zero, s, kCFStringEncodingUTF8);

    private static string? GetUrlPath(IntPtr url)
    {
        if (url == IntPtr.Zero) return null;
        IntPtr pathStr = Send(url, sel_registerName("path"));
        if (pathStr == IntPtr.Zero) return null;
        IntPtr cstr = Send(pathStr, sel_registerName("UTF8String"));
        return cstr == IntPtr.Zero ? null : Marshal.PtrToStringUTF8(cstr);
    }

    private static IntPtr CreateNSArray(List<IntPtr> items)
    {
        if (items.Count == 0)
            return Send(objc_getClass("NSArray"), sel_registerName("array"));

        IntPtr buf = Marshal.AllocHGlobal(IntPtr.Size * items.Count);
        try
        {
            for (int i = 0; i < items.Count; i++)
                Marshal.WriteIntPtr(buf, i * IntPtr.Size, items[i]);
            return SendArrayCount(objc_getClass("NSArray"),
                sel_registerName("arrayWithObjects:count:"), buf, (nint)items.Count);
        }
        finally { Marshal.FreeHGlobal(buf); }
    }

    /// <summary>
    /// Calls an ObjC completion block of type <c>void (^)(NSError *)</c>.
    /// Block layout (64-bit): [isa:8][flags:4][reserved:4][invoke:8][descriptor:8]…
    /// The invoke function pointer lives at byte offset 16.
    /// </summary>
    private static void InvokeBlock(IntPtr block, IntPtr error)
    {
        if (block == IntPtr.Zero) return;
        IntPtr invokePtr = Marshal.ReadIntPtr(block, 16);
        if (invokePtr == IntPtr.Zero) return;
        var invoke = Marshal.GetDelegateForFunctionPointer<BlockInvoke>(invokePtr);
        invoke(block, error);
    }
}
