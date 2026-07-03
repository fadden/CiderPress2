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

namespace cp2_avalonia.Services;

using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using ViewModels;

public class ViewerService : IViewerService
{
    private readonly List<FileViewerViewModel> mActiveViewers = new();

    public IReadOnlyList<FileViewerViewModel> ActiveViewers =>
        new ReadOnlyCollection<FileViewerViewModel>(mActiveViewers);

    public void Register(FileViewerViewModel viewer)
    {
        if (!mActiveViewers.Contains(viewer)) {
            mActiveViewers.Add(viewer);
        }
    }

    public void Unregister(FileViewerViewModel viewer)
    {
        mActiveViewers.Remove(viewer);
    }

    public void CloseViewersForSource(string workPathName)
    {
        foreach (FileViewerViewModel viewer in mActiveViewers
                     .Where(viewer => viewer.SourceWorkPathName == workPathName).ToList())
        {
            viewer.RequestClose();
        }
    }
}
