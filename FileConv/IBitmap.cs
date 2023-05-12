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

namespace FileConv {
    /// <summary>
    /// Common interface for generated bitmap data.  Indexed color and truecolor are supported.
    /// </summary>
    public interface IBitmap : IConvOutput {
        /// <summary>
        /// Bitmap width, in pixels.
        /// </summary>
        int Width { get; }

        /// <summary>
        /// Bitmap height, in pixels.
        /// </summary>
        int Height { get; }

        /// <summary>
        /// True if this is an 8-bit indexed color image, false if this is 32-bit direct color.
        /// </summary>
        bool IsIndexed8 { get; }

        /// <summary>
        /// True if this image was pixel-doubled from its native resolution.  This has no
        /// bearing on how the image is processed, but may be useful for the viewer.
        /// </summary>
        bool IsDoubled { get; }

        /// <summary>
        /// Returns a densely-packed array of 8-bit color indices or 32-bit ARGB values.  The
        /// values are stored little-endian, so ARGB becomes B-G-R-A in memory.
        /// </summary>
        /// <remarks>
        /// This is a direct reference to the data.  Do not modify.
        /// </remarks>
        byte[] GetPixels();

        /// <summary>
        /// Returns the color palette as a series of 32-bit ARGB values.  Will be null for
        /// direct-color images.
        /// </summary>
        /// <remarks>
        /// This makes a copy of the color data.  The array will have 0-255 entries.
        /// </remarks>
        int[]? GetColors();
    }
}
