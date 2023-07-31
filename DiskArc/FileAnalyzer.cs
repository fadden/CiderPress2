/*
 * Copyright 2022 faddenSoft
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
using System.Diagnostics;
using System.Reflection;

using CommonUtil;
using DiskArc.Arc;
using DiskArc.Disk;
using static DiskArc.Defs;

namespace DiskArc {
    /// <summary>
    /// Disk image / file archive analysis functions.
    /// </summary>
    public static class FileAnalyzer {
        /// <summary>
        /// List of supported filesystems and multi-partition formats.
        /// </summary>
        /// <remarks>
        /// List the filesystems in the order they should be tested when a disk is scanned.
        /// </remarks>
        private static DiskLayoutEntry[] sDiskLayouts = {
            new DiskLayoutEntry("APM", FileSystemType.APM, typeof(Multi.APM)),
            new DiskLayoutEntry("MacTS", FileSystemType.MacTS, typeof(Multi.MacTS)),
            new DiskLayoutEntry("CFFA", FileSystemType.CFFA, typeof(Multi.CFFA)),
            // MicroDrive, FocusDrive, ...
            // MSDOS (mostly to rule it out)

            // We want to check for DOS before ProDOS, so that we can identify DOS hybrids.
            new DiskLayoutEntry("DOS 3.x", FileSystemType.DOS33, typeof(FS.DOS)),
            new DiskLayoutEntry("ProDOS", FileSystemType.ProDOS, typeof(FS.ProDOS)),
            new DiskLayoutEntry("HFS", FileSystemType.HFS, typeof(FS.HFS)),

            new DiskLayoutEntry("Am/UniDOS", FileSystemType.AmUniDOS, typeof(Multi.AmUniDOS)),
            new DiskLayoutEntry("OzDOS", FileSystemType.OzDOS, typeof(Multi.OzDOS)),
            // ISO-9660? (rule out HFS first)
        };


        /// <summary>
        /// List of supported filesystems and multi-partition formats.
        /// </summary>
        public static DiskLayoutEntry[] DiskLayouts {
            get {
                // Entries are immutable, so a shallow copy is fine.
                DiskLayoutEntry[] arr = new DiskLayoutEntry[sDiskLayouts.Length];
                Array.Copy(sDiskLayouts, arr, arr.Length);
                return arr;
            }
        }

        /// <summary>
        /// One entry in the disk layout set.  Immutable.
        /// </summary>
        public class DiskLayoutEntry {
            /// <summary>
            /// Human-readable name.
            /// </summary>
            public string Name { get; }

            /// <summary>
            /// FileSystemType enumeration value.
            /// </summary>
            public FileSystemType FSType;

            /// <summary>
            /// Reference to filesystem class.
            /// </summary>
            private Type mImplClass;


            /// <summary>
            /// Constructs the object, using reflection to dig out static methods and properties.
            /// This is more efficient than using reflection every time, and also helps ensure
            /// that the implementations are complete.
            /// </summary>
            /// <param name="name">Short, human-readable name of class.</param>
            /// <param name="implClass">Implementation class.</param>
            /// <exception cref="Exception"></exception>
            public DiskLayoutEntry(string name, FileSystemType fsType, Type implClass) {
                // Make sure we're getting the right thing.
                Debug.Assert(implClass.GetInterface(nameof(IFileSystem)) != null ||
                             implClass.GetInterface(nameof(IMultiPart)) != null);

                Name = name;
                FSType = fsType;
                mImplClass = implClass;

                // Cache a reference to the constructor.
                ConstructorInfo? ctor = implClass.GetConstructor(
                    BindingFlags.Public | BindingFlags.Instance,
                    new Type[] { typeof(IChunkAccess), typeof(AppHook) });
                if (ctor == null) {
                    throw new Exception("Unable to find ctor in " + implClass.FullName);
                }
                mCtorInfo = ctor;

                // Cache reference to the TestImage function, which is a public static method
                // whose signature matches the TestImageFunc delegate.
                MethodInfo? method = implClass.GetMethod("TestImage",
                    BindingFlags.Public | BindingFlags.Static,
                    new Type[] { typeof(IChunkAccess), typeof(AppHook) });
                if (method == null) {
                    throw new Exception("Unable to find TestImage() in " + implClass.FullName);
                }
                // This will throw TypeInitializationException if the method signature is wrong.
                mTestImageFunc =
                        (TestImageFunc)Delegate.CreateDelegate(typeof(TestImageFunc), method);

                // Cache reference to the size test function.
                method = implClass.GetMethod("IsSizeAllowed",
                    BindingFlags.Public | BindingFlags.Static,
                    new Type[] { typeof(long) });
                if (method == null) {
                    throw new Exception("Unable to find IsSizeAllowed() in " + implClass.FullName);
                }
                mIsSizeAllowedFunc =
                    (IsSizeAllowedFunc)Delegate.CreateDelegate(typeof(IsSizeAllowedFunc), method);
            }

            /// <summary>
            /// Cached reflection reference to constructor.
            /// </summary>
            private ConstructorInfo mCtorInfo;

            /// <summary>
            /// Creates an instance of the filesystem.  This should only be called after a
            /// successful test result.
            /// </summary>
            /// <param name="source">Chunk source.</param>
            /// <param name="appHook">Application hook reference.</param>
            /// <param name="contents">Result: IFileSystem or IMultiPart object.</param>
            public void CreateInstance(IChunkAccess source, AppHook appHook,
                    out IDiskContents? contents) {
                Debug.Assert(source != null);
                object instance;
                try {
                    instance = mCtorInfo.Invoke(new object[] { source, appHook });
                } catch (TargetInvocationException ex) {
                    if (ex.InnerException != null) {
                        throw ex.InnerException;
                    } else {
                        throw ex;
                    }
                }
                if (instance is IDiskContents) {
                    contents = (IDiskContents)instance;
                } else {
                    throw new NotImplementedException("Not expecting instance of " +
                        instance.GetType().ToString());
                }
            }

            /// <summary>
            /// Cached reflection reference to filesystem test function.
            /// </summary>
            private TestImageFunc mTestImageFunc;
            private delegate TestResult TestImageFunc(IChunkAccess chunkSource, AppHook appHook);

            /// <summary>
            /// Result from filesystem test.  Higher values are better.
            /// </summary>
            public enum TestResult {
                Undefined = 0,
                No = 1,
                Barely = 2,
                Maybe = 3,
                Good = 4,
                Yes = 5
            }

            /// <summary>
            /// Invokes the filesystem test function.
            /// </summary>
            /// <param name="chunkSource">Data chunk source.</param>
            /// <param name="appHook">Application hook reference.</param>
            /// <returns>True if the filesystem code recognizes this as its own.</returns>
            public TestResult TestImage(IChunkAccess chunkSource, AppHook appHook) {
                return mTestImageFunc(chunkSource, appHook);
            }

            /// <summary>
            /// Cached reflection reference to filesystem creation size test function.
            /// </summary>
            private IsSizeAllowedFunc mIsSizeAllowedFunc;
            private delegate bool IsSizeAllowedFunc(long size);

            /// <summary>
            /// Invokes the filesystem creation size test function.
            /// </summary>
            /// <param name="chunkSource">Data chunk source.</param>
            /// <returns>True if a filesystem image of the specified size is allowed.</returns>
            public bool IsSizeAllowed(long size) {
                return mIsSizeAllowedFunc(size);
            }

            public override string ToString() {
                return "[FileSystemEntry: " + Name + "]";
            }
        }

        /// <summary>
        /// Invokes the filesystem-specific IsSizeAllowed() method.
        /// </summary>
        /// <param name="fsType">Filesystem type.</param>
        /// <param name="size">Size to test.</param>
        /// <returns>True if the filesystem can be placed on a volume of the specified
        ///   size.</returns>
        public static bool IsSizeAllowed(FileSystemType fsType, long size) {
            foreach (DiskLayoutEntry dle in sDiskLayouts) {
                if (dle.FSType == fsType) {
                    return dle.IsSizeAllowed(size);
                }
            }
            throw new ArgumentException("Unsupported file system type: " + fsType);
        }

        /// <summary>
        /// Invokes the filesystem-specific CreateInstance() method.
        /// </summary>
        /// <param name="fsType">Filesystem type.</param>
        /// <param name="source">Chunk source.  The IFileSystem object takes ownership.</param>
        /// <param name="appHook">Application hook reference.</param>
        /// <param name="contents">Result: IFileSystem or IMultiPart object.</param>
        public static void CreateInstance(FileSystemType fsType, IChunkAccess source,
                AppHook appHook, out IDiskContents? contents) {
            foreach (DiskLayoutEntry dle in sDiskLayouts) {
                if (dle.FSType == fsType) {
                    dle.CreateInstance(source, appHook, out contents);
                    return;
                }
            }
            throw new ArgumentException("Unsupported file system type: " + fsType);
        }

        /// <summary>
        /// Finds the file kind(s) associated with a filename extension.
        /// </summary>
        /// <param name="fileNameExt">Filename extension, with the leading '.'.</param>
        /// <param name="fileKind1">Result: what kind of file this is.</param>
        /// <param name="orderHint1">Result: the sector order hinted at by the filename (only
        ///   useful for 5.25" disk images).</param>
        /// <param name="fileKind2">Result: alternate possibility for file kind.</param>
        /// <returns>True if the extension was recognized, false if not.</returns>
        public static bool ExtensionToKind(string fileNameExt, out FileKind fileKind1,
                out SectorOrder orderHint1, out FileKind fileKind2, out bool okayForCreate) {
            okayForCreate = true;
            fileNameExt = fileNameExt.ToLowerInvariant();

            fileKind1 = fileKind2 = FileKind.Unknown;
            orderHint1 = SectorOrder.Unknown;

            switch (fileNameExt) {
                case ".do":
                case ".po":
                case ".iso":
                case ".hdv":
                case ".img":
                    fileKind1 = FileKind.UnadornedSector;
                    if (fileNameExt == ".do") {
                        orderHint1 = SectorOrder.DOS_Sector;
                    } else if (fileNameExt == ".img") {
                        orderHint1 = SectorOrder.Physical;
                    } else {
                        orderHint1 = SectorOrder.ProDOS_Block;
                    }
                    break;
                case ".d13":
                    fileKind1 = FileKind.UnadornedSector;
                    orderHint1 = SectorOrder.DOS_Sector;
                    break;
                case ".dsk":
                    okayForCreate = false;      // ambiguous
                    fileKind1 = FileKind.UnadornedSector;
                    orderHint1 = SectorOrder.DOS_Sector;
                    fileKind2 = FileKind.DiskCopy42;
                    break;
                case ".nib":
                    fileKind1 = FileKind.UnadornedNibble525;
                    orderHint1 = SectorOrder.Physical;
                    break;
                case ".nb2":
                    okayForCreate = false;      // not supported
                    fileKind1 = FileKind.UnadornedNibble525;
                    orderHint1 = SectorOrder.Physical;
                    break;
                case ".raw":     // could be unadorned sector or unadorned nibble
                    okayForCreate = false;      // ambiguous
                    fileKind1 = FileKind.UnadornedSector;
                    orderHint1 = SectorOrder.DOS_Sector;
                    fileKind2 = FileKind.UnadornedNibble525;
                    break;
                case ".woz":
                    fileKind1 = FileKind.Woz;
                    orderHint1 = SectorOrder.Physical;
                    break;
                case ".2mg":
                case ".2img":
                    fileKind1 = FileKind.TwoIMG;
                    break;
                case ".dc":
                case ".dc6":
                case ".image":
                    fileKind1 = FileKind.DiskCopy42;
                    fileKind2 = FileKind.UnadornedSector;
                    orderHint1 = SectorOrder.ProDOS_Block;
                    break;
                case ".app":
                    fileKind1 = FileKind.Trackstar;
                    orderHint1 = SectorOrder.Physical;
                    break;
                case ".zip":
                    fileKind1 = FileKind.Zip;
                    break;
                case ".shk":
                case ".sdk":
                    fileKind1 = FileKind.NuFX;
                    break;
                case ".bxy":
                case ".sea":
                case ".bse":
                    okayForCreate = false;      // don't allow wrapper creation
                    fileKind1 = FileKind.NuFX;
                    break;
                case ".bny":
                case ".bqy":
                    fileKind1 = FileKind.Binary2;
                    break;
                case ".acu":
                    fileKind1 = FileKind.ACU;
                    break;
                case ".gz":
                    fileKind1 = FileKind.GZip;
                    break;
                case ".as":
                    fileKind1 = FileKind.AppleSingle;
                    break;
                case ".ddd":
                    fileKind1 = FileKind.DDD;
                    orderHint1 = SectorOrder.Physical;
                    break;
                default:
                    Debug.WriteLine("Unrecognized filename extension: " + fileNameExt);
                    return false;
            }
            return true;
        }

        /// <summary>
        /// Outcome of file analysis.
        /// </summary>
        public enum AnalysisResult {
            Undefined = 0,

            Success,                    // analysis successfully identified the file
            DubiousSuccess,             // analysis succeeded but some evidence of damage
            FileDamaged,                // file recognized but severely damaged (e.g. truncated)
            UnknownExtension,           // filename extension was not recognized
            ExtensionMismatch,          // file extension does not match file contents
            NotImplemented,             // part of the analyzer is not implemented
        }

        /// <summary>
        /// Analyzes the filename extension and contents of a file to determine what kind of
        /// file it is.
        /// </summary>
        /// <remarks>
        /// <para>This only analyzes the file structure.  It will determine if something appears
        /// to be a disk image, but will not try to determine the sector skew or filesystem
        /// type.</para>
        /// <para>If the result is <see cref="AnalysisResult.ExtensionMismatch"/>, and the
        /// caller wants to retry the analysis considering only the file contents, it may call
        /// this function again with an empty <paramref name="fileNameExt"/>.</para>
        /// </remarks>
        /// <param name="stream">File data stream; must be seekable.</param>
        /// <param name="fileNameExt">Filename extension, with or without the leading '.';
        ///   may be empty.</param>
        /// <param name="kind">Result: what kind of file this is.</param>
        /// <param name="orderHint">Result: for disk image files, the sector order hinted at
        ///   by the filename.</param>
        /// <returns>Result indicating success or cause of failure.</returns>
        public static AnalysisResult Analyze(Stream stream, string fileNameExt, AppHook appHook,
                out FileKind kind, out SectorOrder orderHint) {
            if (!stream.CanSeek) {
                throw new ArgumentException("Stream must be seekable");
            }
            //if (stream.Length == 0) {
            //    throw new ArgumentException("Zero-length stream");
            //}

            AnalysisResult result = AnalysisResult.Success;

            kind = FileKind.Unknown;
            if (ExtensionToKind(fileNameExt, out FileKind kind1, out orderHint,
                    out FileKind kind2, out bool unused)) {
                // File extension was recognized.  See if the contents match.
                if (TestKind(stream, kind1, appHook)) {
                    kind = kind1;
                } else if (TestKind(stream, kind2, appHook)) {
                    kind = kind2;
                } else {
                    result = AnalysisResult.ExtensionMismatch;
                }
            } else {
                result = AnalysisResult.UnknownExtension;
            }

            // Handle special case.
            if (result == AnalysisResult.Success &&
                    fileNameExt == ".d13" && stream.Length != SECTOR_SIZE * 13 * 35) {
                result = AnalysisResult.ExtensionMismatch;
            }

            if (result == AnalysisResult.UnknownExtension) {
                // See if we can make sense of this.  We shouldn't find files without extensions
                // on the host filesystem, but files pulled out of an archive might not use a
                // filename extension for identification.
                foreach (FileKind testKind in sProbeKinds) {
                    if (TestKind(stream, testKind, appHook)) {
                        appHook.LogI("Extension '" + fileNameExt + "' was unknown, detected " +
                            testKind);
                        kind = testKind;
                        result = AnalysisResult.Success;
                        break;
                    }
                }
            }

            return result;
        }

        /// <summary>
        /// List of things to try, in order, when we don't recognize the file extension.
        /// </summary>
        private static FileKind[] sProbeKinds = new FileKind[] {
            // These can be detected reliably.
            FileKind.TwoIMG,
            FileKind.Woz,
            FileKind.Zip,
            FileKind.NuFX,
            FileKind.GZip,
            FileKind.AppleSingle,
            FileKind.DiskCopy42,
            FileKind.ACU,
            FileKind.Binary2,       // test after NuFX (.BXY > .BNY)
            // These are less definite, depending primarily on the size of the file.
            FileKind.Trackstar,
            FileKind.UnadornedNibble525,
            FileKind.UnadornedSector,
            // Skip: DDD
        };

        /// <summary>
        /// Invokes the appropriate file kind tester.
        /// </summary>
        /// <param name="stream">File stream.</param>
        /// <param name="kind">File kind to test against.</param>
        /// <param name="appHook">Application hook reference.</param>
        /// <returns>Result of TestKind() function.</returns>
        public static bool TestKind(Stream stream, FileKind kind, AppHook appHook) {
            switch (kind) {
                case FileKind.AppleSingle:
                    return AppleSingle.TestKind(stream, appHook) ||
                        AppleSingle.TestDouble(stream, appHook);
                case FileKind.ACU:
                    return false;       // TODO
                case FileKind.Binary2:
                    if (stream.Length == 0) {
                        // Empty Binary II archives are totally empty files.  The test code
                        // doesn't accept them, but we want to do so here.
                        return true;
                    }
                    return Binary2.TestKind(stream, appHook);
                case FileKind.DDD:
                    return false;       // TODO
                case FileKind.DiskCopy42:
                    return false;       // TODO
                case FileKind.GZip:
                    return GZip.TestKind(stream, appHook);
                case FileKind.NuFX:
                    return NuFX.TestKind(stream, appHook);
                case FileKind.Trackstar:
                    return Trackstar.TestKind(stream, appHook);
                case FileKind.TwoIMG:
                    return TwoIMG.TestKind(stream, appHook);
                case FileKind.UnadornedSector:
                    return UnadornedSector.TestKind(stream, appHook);
                case FileKind.UnadornedNibble525:
                    return UnadornedNibble525.TestKind(stream, appHook);
                case FileKind.Woz:
                    return Woz.TestKind(stream, appHook);
                case FileKind.Zip:
                    return Zip.TestKind(stream, appHook);

                case FileKind.Unknown:
                    return false;
                default:
                    Debug.WriteLine("Unhandled file kind: " + kind);
                    Debug.Assert(false);
                    return false;
            }
        }

        /// <summary>
        /// Prepares a disk image file for use, based on the detected file kind.
        /// </summary>
        /// <remarks>
        /// <para>This just opens and validates the disk image file.  It does not analyze the
        /// disk format or filesystem.</para>
        /// </remarks>
        /// <param name="stream">Disk image file.</param>
        /// <param name="kind">Result from file analyzer; indicates block/sector vs. nibble.</param>
        /// <param name="appHook">Application hook reference.</param>
        /// <returns>A disk image object of the appropriate type, or null if the file could not
        ///   be opened as directed.</returns>
        /// <exception cref="DAException">Called with a non-disk-image kind.</exception>
        public static IDiskImage? PrepareDiskImage(Stream stream, FileKind kind, AppHook appHook) {
            if (!IsDiskImageFile(kind)) {
                throw new DAException("This method is only for disk images");
            }

            // Instantiate the specified disk file kind.  If we're being called after a successful
            // file test, this should not fail.
            IDiskImage? diskImage = null;
            try {
                switch (kind) {
                    case FileKind.Trackstar:
                        diskImage = Trackstar.OpenDisk(stream, appHook);
                        break;
                    case FileKind.TwoIMG:
                        diskImage = TwoIMG.OpenDisk(stream, appHook);
                        break;
                    case FileKind.UnadornedSector:
                        diskImage = UnadornedSector.OpenDisk(stream, appHook);
                        break;
                    case FileKind.UnadornedNibble525:
                        diskImage = UnadornedNibble525.OpenDisk(stream, appHook);
                        break;
                    case FileKind.Woz:
                        diskImage = Woz.OpenDisk(stream, appHook);
                        break;
                    default:
                        Debug.Assert(false, "Unexpected kind " + kind);
                        break;
                }
            } catch (NotSupportedException) {
                // The constructor rejected our file.
                appHook.LogE("Failed to open disk file with kind=" + kind);
            } catch (Exception ex) {
                appHook.LogE("Failed to open disk file with kind=" + kind + " (" + ex + ")");
                Debug.Assert(false, "unexpected exception");
            }

            return diskImage;
        }

        /// <summary>
        /// Analyzes nibble data to determine the low-level sector format.  Used to get a chunk
        /// access object for a nibble disk image.
        /// </summary>
        /// <param name="nibbles">Nibble data source.</param>
        /// <param name="codec">Nibble codec to use.  If null, various standard codecs will
        ///   be tried.</param>
        /// <param name="appHook">Application hook reference.</param>
        /// <returns>Chunk access object.</returns>
        public static IChunkAccess? AnalyzeDiskFormat(INibbleDataAccess nibbles,
                SectorCodec? codec, AppHook appHook) {
            if (codec != null) {
                // Currently just accepting what we're given.  This feels like the correct
                // approach.
                return new NibbleChunkAccess(nibbles, codec);
            } else {
                if (nibbles.DiskKind == MediaKind.GCR_525) {
                    return Analyze525(nibbles, appHook);
                } else {
                    return Analyze35(nibbles, appHook);
                }
            }
        }

        /// <summary>
        /// Tracks to examine when trying to determine the disk format.
        /// </summary>
        /// <remarks>
        /// <para>Track 0 can be a little funky because it needs to be at least partly
        /// standard or the disk won't boot, so we start with track 1, which will usually
        /// have a DOS image or file data.  Track 17 is a natural for DOS, but sometimes we
        /// get a false positive if the copy protector decided to have a visible catalog
        /// track just for fun.  16 will be the first track with DOS data.  22 is mostly
        /// arbitrary; it's an even-numbered track past the catalog track.</para>
        /// </remarks>
        private static readonly uint[] sTracksToCheck = new uint[] { 1, 16, 17, 22 };

        /// <summary>
        /// Analyzes a 5.25" floppy disk image.
        /// </summary>
        private static IChunkAccess? Analyze525(INibbleDataAccess nibbles, AppHook appHook) {
            const int DMG_SCORE = 1;
            const int PARTIAL_SCORE = 2;
            const int FULL_SCORE = 4;
            int bestScore = 0;
            SectorCodec? bestCodec = null;

            foreach (StdSectorCodec.CodecIndex525 ci in
                    Enum.GetValues(typeof(StdSectorCodec.CodecIndex525))) {
                SectorCodec codec = StdSectorCodec.GetCodec(ci);

                // If we find at least 13 sectors on 3 tracks (or 10 sectors on 3 tracks for 5&3),
                // declare victory immediately.
                int expectedSectors =
                    codec.DataEncoding == SectorCodec.NibbleEncoding.GCR53 ? 13 : 16;
                int winThreshold = sTracksToCheck.Length * (expectedSectors - 3) * FULL_SCORE;

                int score = 0;
                foreach (uint track in sTracksToCheck) {
                    if (!nibbles.GetTrackBits(track, 0, out CircularBitBuffer? trackData)) {
                        // Track wasn't even stored.  Give up.
                        Debug.WriteLine("No data stored for track " + track);
                        return null;
                    }

                    // Find all the sectors.  If we come up completely empty, we either have the
                    // wrong codec, or it's a track that has a weird format because it's used for
                    // nibble counting or other tricks.
                    List<SectorPtr> sectorPtrs = codec.FindSectors(track, 0, trackData);

                    // Score the sectors based on contents.
                    foreach (SectorPtr secPtr in sectorPtrs) {
                        if (secPtr.Sector >= expectedSectors) {
                            score += DMG_SCORE;
                        } else if (secPtr.IsAddrDamaged || secPtr.IsDataDamaged) {
                            score += DMG_SCORE;
                        } else if (secPtr.DataFieldBitOffset == -1) {
                            // Undamaged but no data field, this is probably newly-formatted DOS 3.2.
                            score += PARTIAL_SCORE;
                        } else {
                            score += FULL_SCORE;
                        }
                    }
                }

                if (score >= winThreshold) {
                    // Confidence level is high enough to just accept this.
                    appHook.LogD("Confidently accepting codec (score=" + score + "): " +
                        codec.Name);
                    return new NibbleChunkAccess(nibbles, codec);
                }
                if (score > bestScore) {
                    bestScore = score;
                    bestCodec = codec;
                    appHook.LogD("New best codec (score=" + score + "): " + codec.Name);
                }
            }

            if (bestScore < 12 * FULL_SCORE) {
                return null;
            }
            Debug.Assert(bestCodec != null);
            appHook.LogD("Choosing best codec (score=" + bestScore + "): " + bestCodec.Name);
            return new NibbleChunkAccess(nibbles, bestCodec);
        }

        /// <summary>
        /// Analyzes a 3.5" floppy disk image.
        /// </summary>
        private static IChunkAccess? Analyze35(INibbleDataAccess nibbles, AppHook appHook) {
            // These disks are usually ProDOS or HFS, which have key filesystem structures
            // on track zero.  I'm not sure what sort of see-through copy protection is used
            // on 3.5" disks, so for now we just do a quick check on track zero to confirm
            // that the disk is recognizable.

            StdSectorCodec.CodecIndex35 ci = StdSectorCodec.CodecIndex35.Std_35;
            SectorCodec codec = StdSectorCodec.GetCodec(ci);

            // Grab track 0 / side 0.
            uint track = 0;
            uint side = 0;
            if (!nibbles.GetTrackBits(track, side, out CircularBitBuffer? trackData)) {
                // Track wasn't even stored.  Give up.
                Debug.WriteLine("No data stored for track " + track + "/" + side);
                return null;
            }

            // Find all the sectors.  If we come up completely empty, we either have the
            // wrong codec, or it's a track that has a weird format because it's used for
            // nibble counting or other tricks.
            List<SectorPtr> sectorPtrs = codec.FindSectors(track, side, trackData);
            if (sectorPtrs.Count < 10) {
                appHook.LogD("3.5 sector format not recognized");
                return null;
            }

            appHook.LogD("Selecting 3.5 codec " + codec.Name);
            return new NibbleChunkAccess(nibbles, codec);
        }

        // List of file sector orders to test.
        private static SectorOrder[] sOrders = new SectorOrder[] {
            SectorOrder.DOS_Sector,
            SectorOrder.ProDOS_Block,
            SectorOrder.Physical,
            SectorOrder.CPM_KBlock
        };

        /// <summary>
        /// Analyzes a chunk of blocks or sectors to determine which filesystem is present.
        /// This is called by the disk image code, after it identifies the part of the stream
        /// where chunks are stored.
        /// </summary>
        /// <remarks>
        /// <para>For 5.25" disk images, determining the file order and filesystem must be
        /// done at the same time.  For all other images, the file order will either be
        /// "physical" (if it's a nibble image) or ProDOS block.</para>
        /// <para>If a bad block is encountered during analysis, this will return false.</para>
        ///
        /// <para>For a multi-partition format, the filesystems with the partitions will not
        /// be prepared.  Each partition will have an accessible IChunkAccess.</para>
        /// </remarks>
        /// <param name="chunkAccess">Chunk data source.</param>
        /// <param name="doProbeOrders">If set, probe for the proper file order.  Set to false
        ///   if the file order is already known.</param>
        /// <param name="appHook">Application callback hook.</param>
        /// <param name="contents">Result: if disk layout was a single filesystem, this
        ///   receives an IFileSystem object instance; if layout was a multi-partition format,
        ///   this receives an IMultiPart instance.  Otherwise null.</param>
        /// <returns>True if the disk layout was recognized; <paramref name="contents"/> will
        ///   be non-null.  If false, <paramref name="contents"/> will be null.</returns>
        /// <exception cref="DAException">Called with a disk image kind.</exception>
        public static bool AnalyzeFileSystem(IChunkAccess chunkAccess, bool doProbeOrders,
                AppHook appHook, out IDiskContents? contents) {
            SectorOrder initialOrder = chunkAccess.FileOrder;
            SectorOrder bestOrder = initialOrder;
            DiskLayoutEntry? bestEntry = null;
            DiskLayoutEntry.TestResult bestResult = DiskLayoutEntry.TestResult.No;

            Debug.Assert(chunkAccess is not GatedChunkAccess);
            try {
                // Try each filesystem in turn, stopping if we get a positive match.  If we get
                // something between "yes" and "no", remember the value, keeping the best one we find
                // (the search list should be in priority order).
                foreach (DiskLayoutEntry fse in sDiskLayouts) {
                    chunkAccess.FileOrder = initialOrder;

                    DiskLayoutEntry.TestResult result = fse.TestImage(chunkAccess, appHook);
                    if (result == DiskLayoutEntry.TestResult.Yes) {
                        appHook.LogD("FS match on " + fse.Name + ", fileOrder=" +
                            chunkAccess.FileOrder);
                        bestEntry = fse;
                        bestOrder = initialOrder;
                        break;
                    } else if (result > bestResult) {
                        // Remember this one, but keep going.
                        bestEntry = fse;
                        bestOrder = initialOrder;
                        bestResult = result;
                    }

                    // Didn't get a "yes", try different orderings if this is a sector image.
                    if (chunkAccess.HasSectors && doProbeOrders) {
                        foreach (SectorOrder order in sOrders) {
                            if (order == initialOrder) {
                                continue;       // don't do this one again
                            }
                            chunkAccess.FileOrder = order;
                            Debug.Assert(chunkAccess.FileOrder == order);
                            result = fse.TestImage(chunkAccess, appHook);
                            if (result == DiskLayoutEntry.TestResult.Yes) {
                                appHook.LogD("FS probe match on " + fse.Name + ", fileOrder=" +
                                    chunkAccess.FileOrder);
                                bestEntry = fse;
                                bestOrder = order;
                                bestResult = result;
                                break;
                            } else if (result > bestResult) {
                                // Best we've seen so far, remember it.  If we hit the same score a
                                // second time, we want to keep the first one, so that we prioritize
                                // hinted orders.
                                bestEntry = fse;
                                bestOrder = order;
                                bestResult = result;
                            }
                        }

                        // We'll take "good" or "yes".  If we insist on "yes" we'll potentially
                        // miss out on some DOS+ProDOS hybrids that have short catalog tracks.
                        // TODO: we may want to rework this to always take the highest-scoring
                        //   item, but keep track of the next-best.  If we have yes+yes or yes+good
                        //   then we can evaluate for hybrid.  This removes the need to check DOS
                        //   before ProDOS.
                        if (bestResult >= DiskLayoutEntry.TestResult.Good) {
                            break;
                        }
                    }
                }
            } catch (BadBlockException ex) {
                Debug.WriteLine("Filesystem analysis failed: " + ex.Message);
            }

            if (bestEntry != null) {
                chunkAccess.FileOrder = bestOrder;
                bestEntry.CreateInstance(chunkAccess, appHook, out contents);
                return true;
            } else {
                chunkAccess.FileOrder = initialOrder;
                contents = null;
                return false;
            }
        }

        /// <summary>
        /// Creates an instance of a filesystem, by type.
        /// </summary>
        /// <param name="type">Filesystem type.</param>
        /// <param name="chunkAccess">Chunk access object.</param>
        /// <param name="appHook">Application hook reference.</param>
        /// <returns>Filesystem object, or null if unable to create it.</returns>
        public static IFileSystem? CreateFileSystem(FileSystemType type, IChunkAccess chunkAccess,
                AppHook appHook) {
            Debug.Assert(chunkAccess is not GatedChunkAccess);
            foreach (DiskLayoutEntry fse in sDiskLayouts) {
                if (fse.FSType == type) {
                    DiskLayoutEntry.TestResult result = fse.TestImage(chunkAccess, appHook);
                    if (result <= DiskLayoutEntry.TestResult.No) {
                        Debug.WriteLine("Requested filesystem not found");
                        return null;
                    }
                    fse.CreateInstance(chunkAccess, appHook, out IDiskContents? contents);
                    return contents as IFileSystem;
                }
            }
            Debug.Assert(false, "Unable to find FS type " + type);
            return null;
        }

        /// <summary>
        /// Prepares a file archive for use, based on the detected file kind.
        /// </summary>
        /// <param name="stream">Stream with archive data.  Must be seekable, may be
        ///   read-only.</param>
        /// <param name="kind">How to treat the data stream.</param>
        /// <returns>Archive object reference, or null if the stream could not be opened as
        ///   the specified kind.</returns>
        /// <exception cref="DAException">Called with a disk image kind.</exception>
        ///
        /// <remarks>
        /// <para>Normally the caller will pass in the file kind determined by the analyzer.
        /// In some ambiguous cases the analyzer result can be overridden, e.g. a BXY file could
        /// be opened as Binary II rather than NuFX to edit the header.</para>
        /// </remarks>
        public static IArchive? PrepareArchive(Stream stream, FileKind kind, AppHook appHook) {
            if (IsDiskImageFile(kind)) {
                throw new DAException("This method is not for disk images");
            }

            IArchive? archive = null;
            try {
                switch (kind) {
                    case FileKind.AppleSingle:
                        archive = AppleSingle.OpenArchive(stream, appHook);
                        break;
                    case FileKind.Binary2:
                        archive = Binary2.OpenArchive(stream, appHook);
                        break;
                    case FileKind.GZip:
                        archive = GZip.OpenArchive(stream, appHook);
                        break;
                    case FileKind.NuFX:
                        archive = NuFX.OpenArchive(stream, appHook);
                        break;
                    case FileKind.Zip:
                        archive = Zip.OpenArchive(stream, appHook);
                        break;
                    default:
                        throw new NotImplementedException("Not implemented: " + kind);
                }
            } catch (NotSupportedException) {
                // Rejection.
                appHook.LogE("Failed to open archive file with kind=" + kind);
            }

            return archive;
        }
    }
}
