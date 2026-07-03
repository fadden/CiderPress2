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
using ViewModels;

public interface IViewerService {
    IReadOnlyList<FileViewerViewModel> ActiveViewers { get; }
    void Register(FileViewerViewModel viewer);
    void Unregister(FileViewerViewModel viewer);

    /// <summary>
    /// Close all viewers associated with the given work path (called on
    /// file close).
    /// </summary>
    void CloseViewersForSource(string workPathName);
}