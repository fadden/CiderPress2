/*
 * Copyright 2023 faddenSoft
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

using AppCommon;
using CommonUtil;
using DiskArc;
using DiskArc.Arc;
using DiskArc.Disk;
using DiskArc.FS;
using DiskArc.Multi;
using static DiskArc.Defs;

namespace cp2 {
    internal static class DiskUtil {
        #region Create Disk Image

        /// <summary>
        /// Handles the "create-disk-image" command.
        /// </summary>
        public static bool HandleCreateDiskImage(string cmdName, string[] args, ParamsBag parms) {
            if (args.Length < 2 || args.Length > 3) {
                CP2Main.ShowUsage(cmdName);
                return false;
            }
            string outFileName = args[0];
            string sizeStr = args[1];
            string? formatStr = null;
            if (args.Length == 3) {
                formatStr = args[2];
            }

            if (Directory.Exists(outFileName)) {
                Console.Error.WriteLine("Cannot create archive: is a directory: '" +
                    outFileName + "'");
                return false;
            }

            // Convert the extension to a format.
            string ext = Path.GetExtension(outFileName).ToLowerInvariant();
            if (!FileAnalyzer.ExtensionToKind(ext, out FileKind createKind,
                    out SectorOrder orderHint, out FileKind unused1, out FileKind unused2,
                    out bool okayForCreate)) {
                Console.Error.WriteLine("Disk image file extension not recognized");
                return false;
            }
            if (!okayForCreate) {
                Console.Error.WriteLine("Filename extension \"" + ext +
                    "\" is ambiguous or not supported for file creation");
                return false;
            }
            if (ext == ".d13" && !(parms.Sectors == 13)) {
                Console.WriteLine("Switching to 13-sector mode for .d13 file");
                parms.Sectors = 13;
            }

            // Only disk images; need special handling for NuFX ".SDK".
            if (!Defs.IsDiskImageFile(createKind) && ext != ".sdk") {
                Console.Error.WriteLine("File extension is not a disk image type");
                return false;
            }

            // Translate the "size" parameter.
            int trackSize = parms.Sectors * SECTOR_SIZE;
            long byteSize = StringToValue.SizeToBytes(sizeStr, trackSize);
            if (byteSize < 0) {
                Console.Error.WriteLine("Invalid size specifier '" + sizeStr + "'");
                return false;
            }

            // Special handling for the popular ".dsk" extension.  If the size is 320KB or
            // smaller, they could be creating a 16-sector 5.25" disk image, and it would be
            // ambiguous.  For larger images we can assume it's just a series of blocks.
            if (ext == ".dsk") {
                if (byteSize <= 320 * 1024) {
                    Console.Error.WriteLine("Filename extension \"" + ext +
                        "\" is ambiguous at that size (try \".do\" or \".po\"?)");
                    return false;
                }
                orderHint = SectorOrder.ProDOS_Block;
            }

            // Translate the "format" parameter, if provided.
            FileSystemType fsType = FileSystemType.Unknown;
            if (formatStr != null) {
                switch (formatStr.ToLowerInvariant()) {
                    case "cpm":
                        fsType = FileSystemType.CPM;
                        break;
                    case "dos":
                        fsType = FileSystemType.DOS33;
                        break;
                    case "hfs":
                        fsType = FileSystemType.HFS;
                        break;
                    case "pascal":
                        fsType = FileSystemType.Pascal;
                        break;
                    case "prodos":
                        fsType = FileSystemType.ProDOS;
                        break;
                    default:
                        Console.Error.WriteLine("Unsupported filesystem '" + formatStr + "'");
                        return false;
                }
            }

            // Confirm that the filesystem formatter works with this size.
            if (fsType != FileSystemType.Unknown && !FileAnalyzer.IsSizeAllowed(fsType, byteSize)) {
                Console.Error.WriteLine("Cannot format requested filesystem at that size");
                return false;
            }

            // Create the output file.
            if (Directory.Exists(outFileName)) {
                Console.Error.WriteLine("Output file is a directory");
                return false;
            } else if (File.Exists(outFileName)) {
                if (parms.Overwrite) {
                    try {
                        File.Delete(outFileName);
                    } catch (Exception ex) {
                        Console.Error.WriteLine("Unable to delete existing file: " + ex.Message);
                        return false;
                    }
                } else {
                    Console.Error.WriteLine("Output file already exists (add --overwrite?)");
                    return false;
                }
            }
            FileStream imgStream;
            bool keepFile = false;
            try {
                imgStream = new FileStream(outFileName, FileMode.CreateNew);
            } catch (IOException ex) {
                Console.Error.WriteLine("Unable to create disk image: " + ex.Message);
                return false;
            }
            using (imgStream) {
                // Create the disk image.  Some systems may not be able to delete an open file,
                // though that may be a <= WinXP concern.  We catch all possible exceptions and,
                // if there was an error, delete the file after it's closed.
                if (parms.Verbose) {
                    bool hasFs = (fsType != FileSystemType.Unknown);
                    Console.WriteLine("Creating " +
                        (hasFs ? "" : "UNFORMATTED ") +
                        "disk image: " + ThingString.FileKind(createKind) +
                        ", order=" + ThingString.SectorOrder(orderHint) +
                        ", size=" + Catalog.FormatSizeOnDisk(byteSize, 1024) +
                        (hasFs ? ", " + ThingString.FileSystemType(fsType) : ""));
                }
                try {
                    using IDiskImage diskImage = CreateDiskImage(imgStream, createKind, orderHint,
                        byteSize, trackSize, fsType, DEFAULT_525_VOLUME_NUM,
                        parms.MakeBootable, parms.AppHook, out Stream? streamForArc);

                    // Handle NuFX ".SDK" disk image creation.  (This doesn't strictly belong here,
                    // but it's nicer for the user if we handle it.)
                    if (createKind == FileKind.NuFX) {
                        Debug.Assert(streamForArc != null);

                        NuFX archive = NuFX.CreateArchive(parms.AppHook);
                        archive.StartTransaction();
                        IFileEntry entry = archive.CreateRecord();
                        entry.FileName = Defs.DEFAULT_VOL_NAME;
                        SimplePartSource source = new SimplePartSource(streamForArc);
                        CompressionFormat fmt = parms.Compress ?
                            CompressionFormat.Default : CompressionFormat.Uncompressed;
                        archive.AddPart(entry, FilePart.DiskImage, source, fmt);
                        archive.CommitTransaction(imgStream);
                    }
                    keepFile = true;
                } catch (Exception ex) {
                    Console.Error.WriteLine("Error: " + ex.Message);
                    // fall through to delete file
                }
            }
            if (!keepFile) {
                File.Delete(outFileName);
                return false;
            }

            return true;
        }

        /// <summary>
        /// Creates a disk image file with the specified parameters.
        /// </summary>
        /// <param name="imgStream">Output stream.  Must be writable and seekable.</param>
        /// <param name="fileKind">Disk image file kind.  Must be a disk image or NuFX.</param>
        /// <param name="sectorOrder">Sector order, for 5.25" images.</param>
        /// <param name="byteSize">Formatted capacity of the disk image, in bytes.</param>
        /// <param name="trackSize">Length of a track, in bytes.</param>
        /// <param name="fsType">Filesystem to format the image with.  If this is "unknown",
        ///   the disk image will not be formatted.</param>
        /// <param name="volumeNum">Disk volume number (0-255), for low-level 5.25" sector
        ///   initialization and DOS filesystem formatting.</param>
        /// <param name="doMakeBootable">If set, format the filesystem with boot tracks.</param>
        /// <param name="appHook">Application hook reference.</param>
        /// <param name="streamForArc">Result: if we're creating a disk image in a NuFX archive,
        ///   this holds the disk image stream.</param>
        /// <returns>New disk image.  Caller must dispose.</returns>
        /// <exception cref="DAException">Internal error.</exception>
        private static IDiskImage CreateDiskImage(Stream imgStream, FileKind fileKind,
                SectorOrder sectorOrder, long byteSize, int trackSize, FileSystemType fsType,
                byte volumeNum, bool doMakeBootable, AppHook appHook, out Stream? streamForArc) {
            //Console.WriteLine("kind=" + fileKind + " order=" + sectorOrder +
            //    " size=" + byteSize + " trackSize=" + trackSize + " fsType=" + fsType);
            Debug.Assert(trackSize % SECTOR_SIZE == 0);

            streamForArc = null;
            bool is13Sector = (trackSize == 13 * SECTOR_SIZE);
            IDiskImage image;

            switch (fileKind) {
                case FileKind.UnadornedSector: {
                        // Except for .d13, size must be a multiple of 512.
                        // Call CreateSectorImage if it looks like a 5.25" floppy, CreateBlockImage
                        // for everything else.
                        if (byteSize == 35 * 4096 || byteSize == 40 * 4096 ||
                                byteSize == 50 * 4096 || byteSize == 80 * 4096 || is13Sector) {
                            if (byteSize % trackSize != 0) {
                                // Should have been caught by IsSizeAllowed test.
                                throw new DAException("byte size not a multiple of track size");
                            }
                            if (byteSize % SECTOR_SIZE != 0) {
                                throw new DAException("size must be a multiple of 256");
                            }
                            uint numTracks = (uint)(byteSize / trackSize);
                            uint sectorsPerTrack = (uint)trackSize / SECTOR_SIZE;
                            image = UnadornedSector.CreateSectorImage(imgStream, numTracks,
                                sectorsPerTrack, sectorOrder, appHook);
                        } else if (byteSize == 400 * 1024 && trackSize == 32 * SECTOR_SIZE &&
                                sectorOrder == SectorOrder.DOS_Sector) {
                            // Special case: 50 track, 32 sector DOS disk.
                            uint numTracks = (uint)(byteSize / trackSize);
                            uint sectorsPerTrack = (uint)trackSize / SECTOR_SIZE;
                            image = UnadornedSector.CreateSectorImage(imgStream, numTracks,
                                sectorsPerTrack, sectorOrder, appHook);
                        } else {
                            if (byteSize % BLOCK_SIZE != 0) {
                                throw new DAException("size must be a multiple of 512");
                            }
                            if (sectorOrder != SectorOrder.ProDOS_Block) {
                                throw new DAException("unadorned disks of this size must use " +
                                    "ProDOS block order (not " + sectorOrder + ")");
                            }
                            uint numBlocks = (uint)(byteSize / BLOCK_SIZE);
                            image = UnadornedSector.CreateBlockImage(imgStream, numBlocks, appHook);
                        }
                    }
                    break;
                case FileKind.UnadornedNibble525: {
                        // Must be 35-track 5.25".
                        if (byteSize % trackSize != 0) {
                            throw new DAException("size must be a multiple of track length (" +
                                trackSize + ") (try '35trk'?)");
                        }
                        if (byteSize / trackSize != 35) {
                            throw new DAException("only 35-track images are allowed");
                        }
                        SectorCodec codec = is13Sector ?
                            StdSectorCodec.GetCodec(StdSectorCodec.CodecIndex525.Std_525_13) :
                            StdSectorCodec.GetCodec(StdSectorCodec.CodecIndex525.Std_525_16);
                        image = UnadornedNibble525.CreateDisk(imgStream, codec,
                            volumeNum, appHook);
                    }
                    break;
                case FileKind.Trackstar: {
                         // Must be 35- or 40-track 5.25".
                        if (byteSize % trackSize != 0) {
                            throw new DAException("size must be a multiple of track length (" +
                                trackSize + ") (try '35trk'?)");
                        }
                        uint numTracks = (uint)(byteSize / trackSize);
                        if (numTracks != 35 && numTracks != 40) {
                            throw new DAException("only 35- or 40-track images are allowed");
                        }
                        SectorCodec codec = is13Sector ?
                            StdSectorCodec.GetCodec(StdSectorCodec.CodecIndex525.Std_525_13) :
                            StdSectorCodec.GetCodec(StdSectorCodec.CodecIndex525.Std_525_16);
                        image = Trackstar.CreateDisk(imgStream, codec, volumeNum, numTracks,
                            appHook);
                    }
                    break;
                case FileKind.TwoIMG: {
                        // We don't create nibble images here.  Create a DOS-ordered sector image
                        // if it looks like a 35/40-track floppy, otherwise create a block image.
                        if (trackSize != 4096) {
                            throw new DAException("only 16-sector disks are supported");
                        }
                        if (byteSize == 35 * 4096 || byteSize == 40 * 4096) {
                            uint numTracks = (uint)(byteSize / trackSize);
                            image = TwoIMG.CreateDOSSectorImage(imgStream, numTracks, appHook);
                        } else {
                            if (byteSize % BLOCK_SIZE != 0) {
                                throw new DAException("size must be a multiple of 512");
                            }
                            uint numBlocks = (uint)(byteSize / BLOCK_SIZE);
                            image = TwoIMG.CreateProDOSBlockImage(imgStream, numBlocks, appHook);
                        }
                    }
                    break;
                case FileKind.Woz: {
                        // We support four configurations for disk creation:
                        // - 35 track 5.25" (13-/16-sector)
                        // - 40 track 5.25" (13-/16-sector)
                        // - single-sided 3.5"
                        // - double-sided 3.5"
                        // Call CreateDisk525 or CreateDisk35 depending on size (140/160/400/800).
                        // We could get confused with a 32-sector 50-track disk, so disallow DOS
                        // on the 3.5" images.
                        //
                        // We use disk volume 254 for 5.25" disks, and and 4:1 interleave for 3.5".
                        const int INTERLEAVE = 4;
                        if (byteSize == 35 * trackSize || byteSize == 40 * trackSize) {
                            // 5.25" disk image.
                            SectorCodec codec = is13Sector ?
                                StdSectorCodec.GetCodec(StdSectorCodec.CodecIndex525.Std_525_13) :
                                StdSectorCodec.GetCodec(StdSectorCodec.CodecIndex525.Std_525_16);
                            image = Woz.CreateDisk525(imgStream, (uint)(byteSize / trackSize),
                                codec, volumeNum, appHook);
                        } else if (byteSize == 400 * 1024 || byteSize == 800 * 1024) {
                            // 3.5" disk image.
                            if (fsType == FileSystemType.DOS33 || fsType == FileSystemType.DOS32) {
                                throw new DAException("Can't put DOS on a 3.5\" disk");
                            }
                            SectorCodec codec =
                                StdSectorCodec.GetCodec(StdSectorCodec.CodecIndex35.Std_35);
                            image = Woz.CreateDisk35(imgStream,
                                byteSize == 400*1024 ? MediaKind.GCR_SSDD35 : MediaKind.GCR_DSDD35,
                                INTERLEAVE, codec, appHook);
                        } else {
                            throw new DAException("Size not supported for WOZ disks");
                        }
                        // Let's just add a default META chunk to all disks.
                        Woz woz = (Woz)image;
                        woz.AddMETA();
                        // Replace the lib creator string.
                        woz.SetCreator("CiderPress II v" + GlobalAppVersion.AppVersion);
                    }
                    break;
                case FileKind.NuFX: {
                        // Handle like UnadornedSector, but only allow 140K / 800K images, since
                        // that's what Apple II utilities expect.  Create in a temp stream and
                        // compress after formatting.
                        streamForArc = new MemoryStream();
                        if (byteSize == 140 * 1024) {
                            image = UnadornedSector.CreateSectorImage(streamForArc, 35, 16,
                                SectorOrder.ProDOS_Block, appHook);
                        } else if (byteSize == 800 * 1024) {
                            image = UnadornedSector.CreateBlockImage(streamForArc, 1600, appHook);
                        } else {
                            throw new DAException("Size not supported for NuFX disks");
                        }
                    }
                    break;
                case FileKind.DiskCopy: {
                        MediaKind mediaKind;
                        if (byteSize == 400 * 1024) {
                            mediaKind = MediaKind.GCR_SSDD35;
                        } else if (byteSize == 720 * 1024) {
                            mediaKind = MediaKind.MFM_DSDD35;
                        } else if (byteSize == 800 * 1024) {
                            mediaKind = MediaKind.GCR_DSDD35;
                        } else if (byteSize == 1440 * 1024) {
                            mediaKind = MediaKind.MFM_DSHD35;
                        } else {
                            throw new DAException("Size not supported for DiskCopy disks");
                        }
                        image = DiskCopy.CreateDisk(imgStream, mediaKind, appHook);
                    }
                    break;
                default:
                    throw new DAException("File kind not implemented: " + fileKind);
            }

            if (fsType != FileSystemType.Unknown) {
                // Map a filesystem instance on top of the disk image chunks.
                FileAnalyzer.CreateInstance(fsType, image.ChunkAccess!, appHook,
                    out IDiskContents? contents);
                if (contents is not IFileSystem) {
                    throw new DAException("Unable to create filesystem");
                }
                IFileSystem fs = (IFileSystem)contents;

                // New stream, no need to initialize chunks.  Format the filesystem.
                fs.Format(Defs.DEFAULT_VOL_NAME, volumeNum, doMakeBootable);

                // Dispose of the object to ensure everything has been flushed to the chunks.
                fs.Dispose();
            }
            image.Flush();

            return image;
        }

        #endregion Create Disk Image

        #region Copy Sectors

        public static bool HandleCopySectors(string cmdName, string[] args, ParamsBag parms) {
            if (args.Length != 2) {
                CP2Main.ShowUsage(cmdName);
                return false;
            }
            string srcExtArchive = args[0];
            string dstExtArchive = args[1];

            if (!ExtArchive.CheckSameHostFile(srcExtArchive, dstExtArchive, out bool isSame)) {
                Console.Error.WriteLine("Error: source or destination file does not exist");
                return false;
            }
            if (isSame) {
                Console.Error.WriteLine("Error: source and destination must not be the same file");
                return false;
            }

            DiskArcNode? srcRootNode = null;
            DiskArcNode? dstRootNode = null;
            try {
                if (!ExtArchive.OpenExtArc(dstExtArchive, false, false, parms, out dstRootNode,
                        out DiskArcNode? dstLeafNode, out object? dstLeaf,
                        out IFileEntry dstDirEntry)) {
                    return false;
                }
                if (!ExtArchive.OpenExtArc(srcExtArchive, false, true, parms, out srcRootNode,
                        out DiskArcNode? srcLeafNode, out object? srcLeaf,
                        out IFileEntry srcDirEntry)) {
                    return false;
                }
                if (srcLeaf is not IDiskImage || dstLeaf is not IDiskImage) {
                    Console.Error.WriteLine("Error: this can only be used with disk images");
                    return false;
                }

                IDiskImage dstDisk = (IDiskImage)dstLeaf;
                if (!Misc.StdChecks(dstDisk, needWrite: true)) {
                    return false;
                }
                IDiskImage srcDisk = (IDiskImage)srcLeaf;
                if (!Misc.StdChecks(srcDisk, needWrite: false)) {
                    return false;
                }

                bool success = DoCopySectors(srcDisk, dstDisk, parms);
                try {
                    dstLeafNode.SaveUpdates(parms.Compress);
                } catch (Exception ex) {
                    Console.Error.WriteLine("Error: update failed: " + ex.Message);
                    return false;
                }
                if (!success) {
                    return false;
                }
            } finally {
                srcRootNode?.Dispose();
                dstRootNode?.Dispose();
            }
            return true;
        }

        private static bool DoCopySectors(IDiskImage srcDisk, IDiskImage dstDisk, ParamsBag parms) {
            IChunkAccess? dstChunks = GetChunkAccess(dstDisk);
            if (dstChunks == null) {
                return false;
            }
            if (dstChunks.IsReadOnly) {
                Console.Error.WriteLine("Error: unable to write sectors to destination");
                return false;
            }
            IChunkAccess? srcChunks = GetChunkAccess(srcDisk);
            if (srcChunks == null) {
                return false;
            }
            if (dstChunks.FormattedLength != srcChunks.FormattedLength ||
                    dstChunks.NumTracks != srcChunks.NumTracks) {
                Console.Error.WriteLine("Error: source and destination are different sizes");
                return false;
            }
            if (srcChunks.NumTracks == 0) {
                Console.Error.WriteLine("Error: unable to determine number of tracks");
                return false;
            }
            Debug.Assert(dstChunks.NumSectorsPerTrack == srcChunks.NumSectorsPerTrack);
            return CopyAllSectors(srcChunks, dstChunks, parms);
        }

        private static bool CopyAllSectors(IChunkAccess srcChunks, IChunkAccess dstChunks,
                ParamsBag parms) {
            int errorCount = 0;
            byte[] copyBuf = new byte[SECTOR_SIZE];
            for (uint trk = 0; trk < srcChunks.NumTracks; trk++) {
                for (uint sct = 0; sct < srcChunks.NumSectorsPerTrack; sct++) {
                    try {
                        srcChunks.ReadSector(trk, sct, copyBuf, 0);
                    } catch (IOException) {
                        Console.WriteLine("Failed reading T" + trk + " S" + sct);
                        // Continue, but write a zeroed-out buffer.
                        RawData.MemSet(copyBuf, 0, SECTOR_SIZE, 0x00);
                        errorCount++;
                    }
                    try {
                        dstChunks.WriteSector(trk, sct, copyBuf, 0);
                    } catch (IOException) {
                        Console.WriteLine("Failed writing T" + trk + " S" + sct);
                        errorCount++;
                    }
                }
            }
            if (parms.Verbose) {
                Console.WriteLine("Copied " +
                    (srcChunks.NumTracks * srcChunks.NumSectorsPerTrack) +
                    " sectors, encountered " + errorCount + " errors");
            }
            return true;
        }

        #endregion Copy Sectors

        #region Copy Blocks

        public static bool HandleCopyBlocks(string cmdName, string[] args, ParamsBag parms) {
            if (args.Length != 2 && args.Length != 5) {
                CP2Main.ShowUsage(cmdName);
                return false;
            }
            string srcExtArchive = args[0];
            string dstExtArchive = args[1];
            uint srcStart, dstStart, copyCount;
            if (args.Length == 5) {
                try {
                    srcStart = uint.Parse(args[2]);
                    dstStart = uint.Parse(args[3]);
                    copyCount = uint.Parse(args[4]);
                } catch (Exception ex) {
                    Console.Error.WriteLine("Error: invalid block parameters: " + ex.Message);
                    return false;
                }
                if (copyCount == 0) {
                    Console.Error.WriteLine("Error: copy count must be > 0");
                    return false;
                }
            } else {
                srcStart = dstStart = copyCount = 0;
            }

            if (!ExtArchive.CheckSameHostFile(srcExtArchive, dstExtArchive, out bool isSame)) {
                Console.Error.WriteLine("Error: source or destination file does not exist");
                return false;
            }
            if (isSame) {
                Console.Error.WriteLine("Error: source and destination must not be the same file");
                return false;
            }

            DiskArcNode? srcRootNode = null;
            DiskArcNode? dstRootNode = null;
            try {
                if (!ExtArchive.OpenExtArc(dstExtArchive, false, false, parms, out dstRootNode,
                        out DiskArcNode? dstLeafNode, out object? dstLeaf,
                        out IFileEntry dstDirEntry)) {
                    return false;
                }
                if (!ExtArchive.OpenExtArc(srcExtArchive, false, true, parms, out srcRootNode,
                        out DiskArcNode? srcLeafNode, out object? srcLeaf,
                        out IFileEntry srcDirEntry)) {
                    return false;
                }
                if ((srcLeaf is not IDiskImage && srcLeaf is not Partition) ||
                        (dstLeaf is not IDiskImage && dstLeaf is not Partition)) {
                    Console.Error.WriteLine(
                        "Error: this only works with disk images and partitions");
                    return false;
                }

                IChunkAccess? dstChunks;
                if (dstLeaf is IDiskImage) {
                    IDiskImage disk = (IDiskImage)dstLeaf;
                    if (!Misc.StdChecks(disk, needWrite: true)) {
                        return false;
                    }
                    dstChunks = GetChunkAccess(disk);
                } else {
                    Debug.Assert(dstLeaf is Partition);
                    Partition part = (Partition)dstLeaf;
                    dstChunks = GetChunkAccess(part);
                }
                if (dstChunks == null) {
                    return false;
                }
                if (((GatedChunkAccess)dstChunks).AccessLevel != GatedChunkAccess.AccessLvl.Open) {
                    Console.Error.WriteLine("Error: unable to open destination chunks as writable");
                    return false;
                }

                IChunkAccess? srcChunks;
                if (srcLeaf is IDiskImage) {
                    IDiskImage disk = (IDiskImage)srcLeaf;
                    if (!Misc.StdChecks(disk, needWrite: false)) {
                        return false;
                    }
                    srcChunks = GetChunkAccess(disk);
                } else {
                    Debug.Assert(srcLeaf is Partition);
                    Partition part = (Partition)srcLeaf;
                    srcChunks = GetChunkAccess(part);
                }
                if (srcChunks == null) {
                    return false;
                }

                bool success =
                    DoCopyBlocks(srcChunks, srcStart, dstChunks, dstStart, copyCount, parms);
                try {
                    dstLeafNode.SaveUpdates(parms.Compress);
                } catch (Exception ex) {
                    Console.Error.WriteLine("Error: update failed: " + ex.Message);
                    return false;
                }
                if (!success) {
                    return false;
                }
            } finally {
                srcRootNode?.Dispose();
                dstRootNode?.Dispose();
            }
            return true;
        }

        private static bool DoCopyBlocks(IChunkAccess srcChunks, uint srcStart,
                IChunkAccess dstChunks, uint dstStart, uint copyCount, ParamsBag parms) {
            if (!srcChunks.HasBlocks || !dstChunks.HasBlocks) {
                Console.Error.WriteLine("Error: source and destination must be block-addressable");
                return false;
            }
            uint srcBlockCount = (uint)(srcChunks.FormattedLength / BLOCK_SIZE);
            uint dstBlockCount = (uint)(dstChunks.FormattedLength / BLOCK_SIZE);
            if (copyCount == 0) {
                // No range provided, we're copying the full thing.
                if (srcBlockCount != dstBlockCount) {
                    // For safety, we require an explicit range specification if source and
                    // destination are different sizes.
                    Console.Error.WriteLine("Error: source and destination are different sizes (" +
                        srcBlockCount + " vs " + dstBlockCount + ")");
                    Console.Error.WriteLine("       (add: \"0 0 " + srcBlockCount +
                        "\" to command line)");
                    return false;
                }
                copyCount = srcBlockCount;
            }
            if (srcStart + copyCount > srcBlockCount) {
                Console.Error.WriteLine("Error: copy span (" + srcStart + " + " + copyCount +
                    ") overruns source (" + srcBlockCount + " blocks)");
                return false;
            }
            if (dstStart + copyCount > dstBlockCount) {
                Console.Error.WriteLine("Error: copy span (" + dstStart + " + " + copyCount +
                    ") overruns destination (" + dstBlockCount + " blocks)");
                return false;
            }

            if (parms.Verbose) {
                Console.WriteLine("Copying blocks " + srcStart + "-" + (srcStart + copyCount - 1) +
                    " to " + dstStart + "-" + (dstStart + copyCount - 1));
            }

            int errorCount = 0;
            byte[] copyBuf = new byte[BLOCK_SIZE];
            for (uint block = 0; block < copyCount; block++) {
                try {
                    srcChunks.ReadBlock(srcStart + block, copyBuf, 0);
                } catch (IOException) {
                    Console.WriteLine("Failed reading block " + srcStart + block);
                    // Continue, but write a zeroed-out buffer.
                    RawData.MemSet(copyBuf, 0, BLOCK_SIZE, 0x00);
                    errorCount++;
                }
                try {
                    dstChunks.WriteBlock(dstStart + block, copyBuf, 0);
                } catch (IOException) {
                    Console.WriteLine("Failed writing block " + dstStart + block);
                    errorCount++;
                }
            }
            if (parms.Verbose) {
                Console.WriteLine("Copied " + copyCount + " blocks, encountered " +
                    errorCount + " errors");
            }

            return true;
        }

        #endregion Copy Blocks

        #region Extract Partition

        /// <summary>
        /// Handles the "extract-partition" command.
        /// </summary>
        public static bool HandleExtractPartition(string cmdName, string[] args, ParamsBag parms) {
            if (args.Length != 2) {
                CP2Main.ShowUsage(cmdName);
                return false;
            }
            string extArchive = args[0];
            string outFileName = args[1];

            if (Directory.Exists(outFileName)) {
                Console.Error.WriteLine("Cannot create archive: is a directory: '" +
                    outFileName + "'");
                return false;
            }

            // Convert the extension to a format.
            string ext = Path.GetExtension(outFileName).ToLowerInvariant();
            if (!FileAnalyzer.ExtensionToKind(ext, out FileKind createKind,
                    out SectorOrder orderHint, out FileKind unused1, out FileKind unused2,
                    out bool okayForCreate)) {
                Console.Error.WriteLine("Disk image file extension not recognized");
                return false;
            }
            if (!okayForCreate) {
                Console.Error.WriteLine("Filename extension (" + ext +
                    ") is ambiguous or not supported for file creation");
                return false;
            }
            if (ext == ".d13") {
                Console.Error.WriteLine("Cannot copy a partition to a 13-sector disk image.");
                return false;
            }
            if (createKind == FileKind.NuFX) {
                // This is currently awkward because we're sharing code with create-disk-image.
                // We'd need to keep the NuFX archive open, or re-open it, to access the blocks.
            }

            // Only disk images; need special handling for NuFX ".SDK".
            if (!Defs.IsDiskImageFile(createKind) && ext != ".sdk") {
                Console.Error.WriteLine("File extension is not a disk image type");
                return false;
            }

            DiskArcNode? rootNode = null;
            try {
                if (!ExtArchive.OpenExtArc(extArchive, false, true, parms, out rootNode,
                        out DiskArcNode? srcLeafNode, out object? srcLeaf,
                        out IFileEntry srcDirEntry)) {
                    return false;
                }
                if (srcLeaf is not Partition) {
                    Console.Error.WriteLine(
                        "Error: this only works with partitions in disk images");
                    return false;
                }

                IChunkAccess? srcChunks = GetChunkAccess((Partition)srcLeaf);
                if (srcChunks == null) {
                    return false;
                }

                // Determine size of source partition.
                long byteSize = srcChunks.FormattedLength;
                int trackSize = parms.Sectors * SECTOR_SIZE;

                // Create the output file.
                if (Directory.Exists(outFileName)) {
                    Console.Error.WriteLine("Output file is a directory");
                    return false;
                } else if (File.Exists(outFileName)) {
                    if (parms.Overwrite) {
                        try {
                            File.Delete(outFileName);
                        } catch (Exception ex) {
                            Console.Error.WriteLine("Unable to delete existing file: " + ex.Message);
                            return false;
                        }
                    } else {
                        Console.Error.WriteLine("Output file already exists (add --overwrite?)");
                        return false;
                    }
                }
                FileStream imgStream;
                bool keepFile = false;
                try {
                    imgStream = new FileStream(outFileName, FileMode.CreateNew);
                } catch (IOException ex) {
                    Console.Error.WriteLine("Unable to create disk image: " + ex.Message);
                    return false;
                }
                using (imgStream) {
                    try {
                        using IDiskImage diskImage = CreateDiskImage(imgStream, createKind,
                            orderHint, byteSize, trackSize, FileSystemType.Unknown,
                            DEFAULT_525_VOLUME_NUM, false, parms.AppHook, out Stream? streamForArc);

                        IChunkAccess dstChunks = diskImage.ChunkAccess!;
                        bool copyOk;
                        if (srcChunks.HasBlocks && dstChunks.HasBlocks) {
                            uint copyCount = (uint)(byteSize / BLOCK_SIZE);
                            copyOk = DoCopyBlocks(srcChunks, 0, dstChunks, 0, copyCount, parms);
                        } else if (srcChunks.HasSectors && dstChunks.HasSectors) {
                            copyOk = CopyAllSectors(srcChunks, dstChunks, parms);
                        } else {
                            Debug.Assert(false);
                            Console.Error.WriteLine("Error: internal failure (blocks vs. sectors)");
                            copyOk = false;
                        }
                        if (copyOk) {
                            if (createKind == FileKind.NuFX) {
                                Debug.Assert(streamForArc != null);

                                NuFX archive = NuFX.CreateArchive(parms.AppHook);
                                archive.StartTransaction();
                                IFileEntry entry = archive.CreateRecord();
                                entry.FileName = Defs.DEFAULT_VOL_NAME;
                                SimplePartSource source = new SimplePartSource(streamForArc);
                                CompressionFormat fmt = parms.Compress ?
                                    CompressionFormat.Default : CompressionFormat.Uncompressed;
                                archive.AddPart(entry, FilePart.DiskImage, source, fmt);
                                archive.CommitTransaction(imgStream);
                            }
                            keepFile = true;
                        }
                    } catch (Exception ex) {
                        Console.Error.WriteLine("Error: " + ex.Message);
                        // fall through to delete file
                    }
                }
                if (!keepFile) {
                    File.Delete(outFileName);
                    return false;
                }
            } finally {
                rootNode?.Dispose();
            }
            return true;
        }

        #endregion

        #region Replace Partition

        /// <summary>
        /// Handles the "replace-partition" command.
        /// </summary>
        public static bool HandleReplacePartition(string cmdName, string[] args, ParamsBag parms) {
            if (args.Length != 2) {
                CP2Main.ShowUsage(cmdName);
                return false;
            }
            string srcExtArchive = args[0];
            string dstExtArchive = args[1];

            if (!ExtArchive.CheckSameHostFile(srcExtArchive, dstExtArchive, out bool isSame)) {
                Console.Error.WriteLine("Error: source or destination file does not exist");
                return false;
            }
            if (isSame) {
                Console.Error.WriteLine("Error: source and destination must not be the same file");
                return false;
            }

            DiskArcNode? srcRootNode = null;
            DiskArcNode? dstRootNode = null;
            try {
                if (!ExtArchive.OpenExtArc(dstExtArchive, false, false, parms, out dstRootNode,
                        out DiskArcNode? dstLeafNode, out object? dstLeaf,
                        out IFileEntry dstDirEntry)) {
                    return false;
                }
                if (!ExtArchive.OpenExtArc(srcExtArchive, false, true, parms, out srcRootNode,
                        out DiskArcNode? srcLeafNode, out object? srcLeaf,
                        out IFileEntry srcDirEntry)) {
                    return false;
                }
                if (srcLeaf is not IDiskImage) {
                    // TODO: support NuFX .SDK?
                    Console.Error.WriteLine("Error: source must be a simple disk image");
                    return false;
                }
                if (dstLeaf is not Partition) {
                    Console.Error.WriteLine("Error: destination must be a partition");
                    return false;
                }

                IChunkAccess? dstChunks = GetChunkAccess((Partition)dstLeaf);
                if (dstChunks == null) {
                    return false;
                }
                if (((GatedChunkAccess)dstChunks).AccessLevel != GatedChunkAccess.AccessLvl.Open) {
                    Console.Error.WriteLine("Error: unable to open destination chunks as writable");
                    return false;
                }

                IDiskImage srcDisk = (IDiskImage)srcLeaf;
                if (!Misc.StdChecks(srcDisk, needWrite: false)) {
                    return false;
                }
                IChunkAccess? srcChunks = GetChunkAccess(srcDisk);
                if (srcChunks == null) {
                    return false;
                }

                if (srcChunks.FormattedLength > dstChunks.FormattedLength) {
                    Console.Error.WriteLine("Error: source is larger than destination");
                    return false;
                } else if (srcChunks.FormattedLength < dstChunks.FormattedLength) {
                    if (parms.Overwrite) {
                        Console.WriteLine("Note: source is smaller than destination");
                    } else {
                        Console.Error.WriteLine("Error: source is smaller than destination " +
                            "(add --overwrite to proceed anyway)");
                        return false;
                    }
                }

                bool success;
                if (srcChunks.HasBlocks && dstChunks.HasBlocks) {
                    uint copyCount = (uint)(srcChunks.FormattedLength / BLOCK_SIZE);
                    success = DoCopyBlocks(srcChunks, 0, dstChunks, 0, copyCount, parms);
                } else if (srcChunks.HasSectors && dstChunks.HasSectors) {
                    success = CopyAllSectors(srcChunks, dstChunks, parms);
                } else {
                    Debug.Assert(false);
                    Console.Error.WriteLine("Error: internal failure (blocks vs. sectors)");
                    success = false;
                }

                try {
                    dstLeafNode.SaveUpdates(parms.Compress);
                } catch (Exception ex) {
                    Console.Error.WriteLine("Error: update failed: " + ex.Message);
                    return false;
                }
                if (!success) {
                    return false;
                }
            } finally {
                srcRootNode?.Dispose();
                dstRootNode?.Dispose();
            }
            return true;
        }

        #endregion Replace Partition

        #region Defragment

        /// <summary>
        /// Handles the "defrag" command.
        /// </summary>
        public static bool HandleDefrag(string cmdName, string[] args, ParamsBag parms) {
            if (args.Length != 1) {
                CP2Main.ShowUsage(cmdName);
                return false;
            }
            string extArchive = args[0];
            if (!ExtArchive.OpenExtArc(extArchive, false, false, parms, out DiskArcNode? rootNode,
                    out DiskArcNode? leafNode, out object? leaf, out IFileEntry unused)) {
                return false;
            }
            using (rootNode) {
                if (leaf is IArchive) {
                    Console.Error.WriteLine("Operation not available for file archives");
                    return false;
                } else {
                    IFileSystem? fs = Misc.GetTopFileSystem(leaf);
                    if (fs is not Pascal) {
                        Console.Error.WriteLine("This operation is only available for " +
                            "Apple Pascal filesystems.");
                        return false;
                    }
                    if (!Misc.StdChecks(fs, needWrite: true, parms.FastScan)) {
                        return false;
                    }
                    fs.PrepareRawAccess();
                    bool success = false;
                    try {
                        success = ((Pascal)fs).Defragment();
                        if (!success) {
                            Console.Error.WriteLine("Unable to defragment (bad blocks?)");
                        }
                    } catch (Exception ex) {
                        Console.Error.WriteLine("Error: " + ex.Message);
                    }
                    try {
                        leafNode.SaveUpdates(parms.Compress);
                    } catch (Exception ex) {
                        Console.Error.WriteLine("Error: update failed: " + ex.Message);
                        return false;
                    }
                    if (!success) {
                        return false;
                    }
                }
            }
            if (parms.Verbose) {
                Console.WriteLine("Defrag operation successful");
            }
            return true;
        }

        #endregion Defragment

        #region Bless

        private const string DEFAULT_BOOT_ARG = "-";
        private const int BOOT_IMAGE_LEN = 1024;

        /// <summary>
        /// Handles the "bless" command.
        /// </summary>
        public static bool HandleBless(string cmdName, string[] args, ParamsBag parms) {
            if (args.Length < 2 || args.Length > 3) {
                CP2Main.ShowUsage(cmdName);
                return false;
            }
            string extArchive = args[0];
            string systemFolder = args[1];
            string? bootImagePath = null;
            if (args.Length == 3) {
                bootImagePath = args[2];
            }

            // If a boot image file path has been specified, grab it now.
            byte[]? bootImage = null;
            if (bootImagePath != null) {
                bootImage = GetBootImage(bootImagePath);
                if (bootImage == null) {
                    return false;
                }
            }

            if (!ExtArchive.OpenExtArc(extArchive, false, false, parms, out DiskArcNode? rootNode,
                    out DiskArcNode? leafNode, out object? leaf, out IFileEntry endDirEntry)) {
                return false;
            }
            using (rootNode) {
                if (leaf is not IDiskImage && leaf is not Partition) {
                    Console.Error.WriteLine(
                        "Error: this only works with disk images and partitions");
                    return false;
                }
                IFileSystem? fs = Misc.GetTopFileSystem(leaf);
                if (fs is not HFS) {
                    Console.Error.WriteLine("Error: this operation is only available for " +
                        "HFS filesystems.");
                    return false;
                }
                if (!Misc.StdChecks(fs, needWrite: true, parms.FastScan)) {
                    return false;
                }
                if (fs.IsDubious) {
                    Console.Error.WriteLine("Error: filesystem has unresolved issues");
                    return false;
                }

                // Get the directory CNID.
                uint cnid;
                try {
                    IFileEntry entry = fs.EntryFromPath(systemFolder, HFS.SCharacteristics.DirSep);
                    cnid = ((HFS_FileEntry)entry).EntryCNID;
                } catch (FileNotFoundException) {
                    Console.Error.WriteLine("Error: folder '" + systemFolder + "' not found");
                    return false;
                }

                // Switch to block-access mode.
                fs.PrepareRawAccess();
                IChunkAccess chunks = fs.RawAccess;
                bool success = DoBless(chunks, cnid, bootImage, parms);

                try {
                    leafNode.SaveUpdates(parms.Compress);
                } catch (Exception ex) {
                    Console.Error.WriteLine("Error: update failed: " + ex.Message);
                    return false;
                }
                if (!success) {
                    return false;
                }
            }
            return true;
        }

        /// <summary>
        /// Prepares a 1024-byte buffer with a boot image.  If pathName is "-", a default boot
        /// image is used.
        /// </summary>
        /// <param name="pathName">Path to file on disk</param>
        /// <returns>Boot image, or null if it could not be loaded.</returns>
        private static byte[]? GetBootImage(string pathName) {
            byte[] image = new byte[BOOT_IMAGE_LEN];
            if (pathName == DEFAULT_BOOT_ARG) {
                Array.Copy(MacBoot.sBootBlock0, image, BLOCK_SIZE);
                Array.Copy(MacBoot.sBootBlock1, 0, image, BLOCK_SIZE, BLOCK_SIZE);
            } else {
                if (!File.Exists(pathName)) {
                    Console.Error.WriteLine("Boot image file '" + pathName + "' not found");
                    return null;
                }
                using (Stream stream = new FileStream(pathName, FileMode.Open, FileAccess.Read)) {
                    if (stream.Length != BOOT_IMAGE_LEN) {
                        Console.Error.WriteLine("Error: boot image file must be " +
                            BOOT_IMAGE_LEN + " bytes");
                        return null;
                    }
                    stream.ReadExactly(image, 0, BOOT_IMAGE_LEN);
                }
            }
            return image;
        }

        /// <summary>
        /// Blesses an HFS filesystem for booting.
        /// </summary>
        /// <param name="chunks">Chunk access object.</param>
        /// <param name="cnid">CNID of system folder.</param>
        /// <param name="bootImage">New boot image, for blocks 0/1.</param>
        /// <param name="overwrite">If true, overwrite existing boot image.</param>
        /// <returns>True on success.</returns>
        private static bool DoBless(IChunkAccess chunks, uint cnid, byte[]? bootImage,
                ParamsBag parms) {
            byte[] mdb = new byte[BLOCK_SIZE];
            byte[] boot = new byte[BLOCK_SIZE * 2];

            // Read the MDB, and check the signature just to be sure we're in the right place.  We
            // should be looking at an HFS filesystem, so if this is wrong then we're very confused.
            chunks.ReadBlock(HFS.MDB_BLOCK_NUM, mdb, 0);
            uint sig = RawData.GetU16BE(mdb, 0);
            if (sig != HFS.MDB_SIGNATURE) {
                throw new Exception("MDB signature bytes not found");
            }

            if (bootImage != null) {
                bool doWrite;
                chunks.ReadBlock(0, boot, 0);
                chunks.ReadBlock(1, boot, BLOCK_SIZE);

                if (RawData.IsAllZeroes(boot, 0, BLOCK_SIZE * 2)) {
                    if (parms.Verbose) {
                        Console.WriteLine("Existing boot blocks are zeroed; overwriting");
                    }
                    doWrite = true;
                } else if (RawData.MemCmp(bootImage, boot, BLOCK_SIZE * 2) == 0) {
                    if (parms.Verbose) {
                        Console.WriteLine("Existing boot blocks match new boot image; " +
                            "not updating");
                    }
                    doWrite = false;
                } else {
                    if (!parms.Overwrite) {
                        Console.Error.WriteLine("Boot area is nonzero and doesn't match; " +
                            "not updating (add --overwrite)");
                        return false;
                    }
                    if (parms.Verbose) {
                        Console.WriteLine("Existing boot blocks are different; overwriting");
                    }
                    doWrite = true;
                }

                if (doWrite) {
                    chunks.WriteBlock(0, bootImage, 0);
                    chunks.WriteBlock(1, bootImage, BLOCK_SIZE);
                }
            }

            // The drFndrInfo array starts at +$5c.  We want to update entry 0.  We could do
            // this with the HFS_MDB class, but this is much simpler.
            const int FNDR_INFO_OFFSET = 0x5c;
            uint oldCNID = RawData.GetU32BE(mdb, FNDR_INFO_OFFSET);
            if (oldCNID == cnid) {
                if (parms.Verbose) {
                    Console.WriteLine("Existing CNID matches (" + cnid + "), not updating");
                }
            } else {
                if (parms.Verbose) {
                    Console.WriteLine("Replacing existing CNID (" + oldCNID + ") with " + cnid);
                }
                RawData.SetU32BE(mdb, FNDR_INFO_OFFSET, cnid);
                chunks.WriteBlock(HFS.MDB_BLOCK_NUM, mdb, 0);
            }

            if (parms.Verbose) {
                Console.WriteLine("Done");
            }
            return true;
        }

        #endregion Bless

        #region Misc

        /// <summary>
        /// Returns the best chunk access object published by the disk image.
        /// </summary>
        /// <remarks>
        /// <para>This is a little awkward because we're getting the disk image object after
        /// ExtArchive has done its analysis.  Ideally we'd just do the analysis without attempting
        /// to detect the filesystem, so the ChunkAccess in the disk object would be active,
        /// but it's awkward to re-do the analysis without the sector order hint.  So we just
        /// pull it out of the disk or the filesystem, whichever is active.</para>
        /// </remarks>
        /// <param name="disk">Disk image object.</param>
        /// <returns>Chunk access object, or null if the disk sector format wasn't recognized
        ///   and so chunk access is not available.</returns>
        public static IChunkAccess? GetChunkAccess(IDiskImage disk) {
            // ExtArchive has already analyzed the disk.  Analyzing it again is awkward
            // without the sector order hint, so just pick out the best chunk access.
            IChunkAccess chunks;
            if (disk.ChunkAccess == null) {
                Console.Error.WriteLine("Error: unable to determine sector format");
                return null;
            } else if (disk.Contents == null) {
                // Filesystem not identified, use the disk-level chunk object.
                chunks = disk.ChunkAccess;
            } else if (disk.Contents is IFileSystem) {
                // Filesystem was identified, use the "raw" chunk object.
                chunks = ((IFileSystem)disk.Contents).RawAccess;
            } else if (disk.Contents is IMultiPart) {
                // This for multi-part images like APM.
                chunks = ((IMultiPart)disk.Contents).RawAccess;
            } else {
                Console.Error.WriteLine("Internal error: nothing got set");
                return null;
            }
            return chunks;
        }

        /// <summary>
        /// Returns the best chunk access object available from the partition.
        /// </summary>
        /// <param name="part">Partition object</param>
        /// <returns>Chunk access object.</returns>
        public static IChunkAccess? GetChunkAccess(Partition part) {
            // Chunks are always available.  Prefer the filesystem copy since the partition copy
            // will be read-only or inaccessible if a filesystem was analyzed by ExtArchive.
            IChunkAccess chunks;
            if (part.FileSystem != null) {
                chunks = part.FileSystem.RawAccess;
            } else {
                chunks = part.ChunkAccess;
            }
            return chunks;
        }

        #endregion Misc
    }
}
