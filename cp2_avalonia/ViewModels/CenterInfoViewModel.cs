/*
 * Copyright 2026 faddenSoft
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

using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Threading.Tasks;

using AppCommon;

using CommonUtil;

using DiskArc;
using DiskArc.Disk;
using DiskArc.Multi;

using static DiskArc.Defs;

using CommunityToolkit.Mvvm.ComponentModel;

using cp2_avalonia.Models;
using cp2_avalonia.Services;

namespace cp2_avalonia.ViewModels;

/// <summary>
/// ViewModel for the center info panel (right side of center area).
/// Owns key/value pairs, partition layout, metadata entries, and notes display.
/// </summary>
public class CenterInfoViewModel(IDialogService dialogService) : ObservableObject
{
    public ObservableCollection<CenterInfoItem> CenterInfoList { get; } = new();
    public ObservableCollection<PartitionListItem> PartitionList { get; } = new();
    public ObservableCollection<MetadataItem> MetadataItems { get; } = new();
    public ObservableCollection<Notes.Note> NotesList { get; } = new();

    private string mCenterInfoText1 = string.Empty;
    public string CenterInfoText1 {
        get => mCenterInfoText1;
        set => SetProperty(ref mCenterInfoText1, value);
    }

    private string mCenterInfoText2 = string.Empty;
    public string CenterInfoText2 {
        get => mCenterInfoText2;
        set => SetProperty(ref mCenterInfoText2, value);
    }

    private bool mShowDiskUtilityButtons;
    public bool ShowDiskUtilityButtons {
        get => mShowDiskUtilityButtons;
        set => SetProperty(ref mShowDiskUtilityButtons, value);
    }

    private bool mShowPartitionLayout;
    public bool ShowPartitionLayout {
        get => mShowPartitionLayout;
        set => SetProperty(ref mShowPartitionLayout, value);
    }

    private bool mShowNotes;
    public bool ShowNotes {
        get => mShowNotes;
        set => SetProperty(ref mShowNotes, value);
    }

    private bool mShowMetadata;
    public bool ShowMetadata {
        get => mShowMetadata;
        set => SetProperty(ref mShowMetadata, value);
    }

    private bool mCanAddMetadataEntry;
    public bool CanAddMetadataEntry {
        get => mCanAddMetadataEntry;
        set => SetProperty(ref mCanAddMetadataEntry, value);
    }

    /// <summary>
    /// Clears all center info panel content.
    /// </summary>
    public void ClearCenterInfo() {
        ShowDiskUtilityButtons = false;
        PartitionList.Clear();
        ShowPartitionLayout = false;
        NotesList.Clear();
        ShowNotes = false;
        MetadataItems.Clear();
        ShowMetadata = false;
        CanAddMetadataEntry = false;
    }

    private void AddInfoItem(string name, string value) {
        CenterInfoList.Add(new CenterInfoItem(name + ":", value));
    }

    /// <summary>
    /// Populates the info list with summary details for the given work object.
    /// </summary>
    public void ConfigureCenterInfo(object workObj, string selName, Formatter formatter) {
        CenterInfoList.Clear();
        string infoText;
        if (workObj is IArchive arc) {
            infoText = "File archive - " + ThingString.IArchive(arc) + " - " + selName;
            AddInfoItem("Entries", arc.Count.ToString());
        } else if (workObj is IDiskImage disk) {
            infoText = "Disk image - " + ThingString.IDiskImage(disk) + " - " + selName;
            IChunkAccess? chunks = disk.ChunkAccess;
            if (chunks != null) {
                AddInfoItem("Total size",
                    formatter.FormatSizeOnDisk(chunks.FormattedLength, KBLOCK_SIZE));
                if (chunks.HasSectors) {
                    AddInfoItem("Geometry",
                        chunks.NumTracks.ToString() + " tracks, " +
                        chunks.NumSectorsPerTrack.ToString() + " sectors");
                    if (chunks.NumSectorsPerTrack == 16) {
                        AddInfoItem("File order",
                            ThingString.SectorOrder(chunks.FileOrder));
                    }
                }
                if (chunks.NibbleCodec != null) {
                    AddInfoItem("Nibble codec", chunks.NibbleCodec.Name);
                }
            }
        } else if (workObj is IFileSystem fs) {
            infoText = "Filesystem - " + ThingString.IFileSystem(fs) + " - " + selName;
            IChunkAccess chunks = fs.RawAccess;
            AddInfoItem("Volume size",
                formatter.FormatSizeOnDisk(chunks.FormattedLength, KBLOCK_SIZE));
        } else if (workObj is IMultiPart partitions) {
            infoText = "Multi-partition format - " + ThingString.IMultiPart(partitions) +
                       " - " + selName;
            AddInfoItem("Partition count", partitions.Count.ToString());
        } else if (workObj is Partition part) {
            infoText = "Disk partition - " + ThingString.Partition(part) + " - " + selName;
            AddInfoItem("Start block", (part.StartOffset / BLOCK_SIZE).ToString());
            AddInfoItem("Block count", (part.Length / BLOCK_SIZE).ToString() + " (" +
                (part.Length / (1024 * 1024.0)).ToString("N1") + " MB)");
        } else {
            infoText = "???";
        }
        CenterInfoText1 = infoText;
    }

    public void SetPartitionList(IMultiPart parts) {
        PartitionList.Clear();
        for (int i = 0; i < parts.Count; i++) {
            PartitionList.Add(new PartitionListItem(i + 1, parts[i]));
        }
        ShowPartitionLayout = (PartitionList.Count > 0);
    }

    public void SetNotesList(Notes notes) {
        NotesList.Clear();
        foreach (Notes.Note note in notes.GetNotes()) {
            NotesList.Add(note);
        }
        ShowNotes = (notes.Count > 0);
    }

    public void SetMetadataList(IMetadata metaObj) {
        MetadataItems.Clear();
        List<IMetadata.MetaEntry> entries = metaObj.GetMetaEntries();
        foreach (IMetadata.MetaEntry met in entries) {
            var value = metaObj.GetMetaValue(met.Key, true) ?? "!NOT FOUND!";
            MetadataItems.Add(new MetadataItem(met.Key, value, met.Description,
                met.ValueSyntax, met.CanEdit));
        }
        ShowMetadata = true;
        CanAddMetadataEntry = metaObj.CanAddNewEntries;
    }

    public void UpdateMetadata(string key, string value) {
        foreach (MetadataItem item in MetadataItems) {
            if (item.Key == key) {
                item.SetValue(value);
                break;
            }
        }
    }

    public void AddMetadata(IMetadata.MetaEntry met, string value) {
        MetadataItems.Add(new MetadataItem(met.Key, value, met.Description,
            met.ValueSyntax, met.CanEdit));
    }

    public void RemoveMetadata(string key) {
        for (int i = 0; i < MetadataItems.Count; i++) {
            if (MetadataItems[i].Key == key) {
                MetadataItems.RemoveAt(i);
                return;
            }
        }
        Debug.Assert(false, "Key not found: " + key);
    }

    /// <summary>
    /// Handles a double click on a metadata list entry.  Shows the edit-metadata dialog.
    /// </summary>
    /// <param name="col"></param>
    /// <param name="workObject">Current work object (MainViewModel.mCurrentWorkObject).</param>
    /// <param name="item"></param>
    /// <param name="row"></param>
    public async Task HandleMetadataDoubleClick(MetadataItem item, int row, int col,
            object? workObject) {
        IMetadata? metaObj = workObject as IMetadata;
        if (metaObj == null) {
            Debug.Assert(false);
            return;
        }
        var vm = new EditMetadataViewModel(metaObj, item.Key);
        bool? result = await dialogService.ShowDialogAsync(vm);
        if (result == true) {
            if (vm.DoDelete) {
                metaObj.DeleteMetaEntry(vm.KeyText);
                RemoveMetadata(item.Key);
            } else {
                metaObj.SetMetaValue(item.Key, vm.ValueText);
                string? fancyValue = metaObj.GetMetaValue(item.Key, true);
                if (fancyValue != null) {
                    UpdateMetadata(item.Key, fancyValue);
                }
            }
            if (metaObj is IDiskImage diskImg) {
                diskImg.Flush();
            }
        }
    }

    /// <summary>
    /// Handles a click on the "Add Metadata Entry" button.
    /// </summary>
    /// <param name="workObject">Current work object (MainViewModel.mCurrentWorkObject).</param>
    public async Task HandleMetadataAddEntry(object? workObject) {
        IMetadata? metaObj = workObject as IMetadata;
        if (metaObj == null) {
            Debug.Assert(false);
            return;
        }
        if (metaObj is Woz { HasMeta: false } woz) {
            woz.AddMETA();
            SetMetadataList(metaObj);
        }
        var vm = new AddMetadataViewModel(metaObj);
        bool? result = await dialogService.ShowDialogAsync(vm);
        if (result == true) {
            metaObj.SetMetaValue(vm.KeyText, vm.ValueText);
            if (metaObj is IDiskImage diskImg2) {
                diskImg2.Flush();
            }
            var entry = metaObj.GetMetaEntry(vm.KeyText);
            if (entry != null) {
                var value = metaObj.GetMetaValue(vm.KeyText, true) ?? vm.ValueText;
                AddMetadata(entry, value);
            }
        }
    }
}
