/*
 * Copyright 2025 faddenSoft
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

namespace cp2_avalonia.Common {
    /// <summary>
    /// Replacement for WPF's MessageBoxResult.  Indicates which button the user clicked.
    /// </summary>
    public enum MBResult { None, OK, Cancel, Yes, No }

    /// <summary>
    /// Replacement for WPF's MessageBoxButton.  Specifies which buttons to show.
    /// </summary>
    public enum MBButton { OK, OKCancel, YesNo, YesNoCancel }

    /// <summary>
    /// Replacement for WPF's MessageBoxImage.  Specifies the icon to display.
    /// </summary>
    public enum MBIcon { None, Info, Warning, Error, Question }
}
