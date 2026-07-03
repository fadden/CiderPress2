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
using System;
using System.Collections.Generic;
using System.Text.Json;
using Avalonia;
using Avalonia.Controls;

using cp2_avalonia.Services;

namespace cp2_avalonia.Common {
    /// <summary>
    /// Saves and restores main window position and size.  Replaces the WPF Win32
    /// Get/SetWindowPlacement approach with a JSON-based cross-platform implementation.
    /// </summary>
    public static class WindowPlacement {
        // Cache of normal-state (non-maximized) bounds, keyed by window instance.
        private static readonly Dictionary<Window, (PixelPoint Pos, double W, double H)>
            sNormalBounds = new();

        /// <summary>
        /// Call from the MainWindow constructor to begin tracking normal-state bounds.
        /// These are needed when the window is maximized so we can save/restore the
        /// unmaximized size and position.
        /// </summary>
        public static void TrackNormalBounds(Window window) {
            void UpdateCache(Window w) {
                if (w.WindowState == WindowState.Normal) {
                    sNormalBounds[w] = (w.Position, w.Width, w.Height);
                }
            }
            window.PositionChanged += (_, _) => UpdateCache(window);
            window.Resized += (_, _) => UpdateCache(window);
            window.Closed += (_, _) => sNormalBounds.Remove(window);
        }

        /// <summary>
        /// Serializes the window's current position and state to a JSON string.
        /// </summary>
        public static string Save(Window window) {
            double w, h;
            int x, y;
            if (window.WindowState == WindowState.Maximized &&
                    sNormalBounds.TryGetValue(window, out var nb)) {
                x = nb.Pos.X;
                y = nb.Pos.Y;
                w = nb.W;
                h = nb.H;
            } else {
                x = window.Position.X;
                y = window.Position.Y;
                w = window.Width;
                h = window.Height;
            }

            var data = new {
                X = x, Y = y, Width = w, Height = h,
                State = window.WindowState.ToString()
            };
            return JsonSerializer.Serialize(data);
        }

        /// <summary>
        /// Restores a window's position and state from a JSON string previously produced
        /// by <see cref="Save"/>.
        /// </summary>
        public static void Restore(Window window, string json) {
            try {
                var data = JsonSerializer.Deserialize<JsonElement>(json);
                window.Position = new PixelPoint(
                    data.GetProperty("X").GetInt32(),
                    data.GetProperty("Y").GetInt32());
                window.Width = data.GetProperty("Width").GetDouble();
                window.Height = data.GetProperty("Height").GetDouble();
                if (Enum.TryParse<WindowState>(
                        data.GetProperty("State").GetString(), out var state)) {
                    window.WindowState = state;
                }
            } catch (Exception ex) {
                // Ignore invalid or missing placement data; window stays at default position.
                AppLog.D("Window placement: ignoring invalid saved data", ex);
            }
        }
    }
}
