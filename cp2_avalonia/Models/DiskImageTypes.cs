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

namespace cp2_avalonia.Models;

public static class DiskImageTypes
{
    public enum DiskSizeValue {
        Unknown = 0,
        Flop525_114, Flop525_140, Flop525_160,
        Flop35_400, Flop35_800, Flop35_1440,
        Other_32MB, Other_Custom
    }

    public enum FileTypeValue {
        Unknown = 0,
        DOSSector,
        ProDOSBlock,
        SimpleBlock,
        TwoIMG,
        DiskCopy42,
        NuFX,
        Woz,
        Moof,
        Nib,
        Trackstar
    }
}
