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

using Avalonia.Controls.Primitives;

namespace cp2_avalonia.Controls {
    /// <summary>
    /// GroupBox analog for Avalonia (which has no built-in GroupBox control).
    /// Draws a labeled border around arbitrary content.
    /// </summary>
    /// <remarks>
    /// This is a templated <see cref="HeaderedContentControl"/> (Header + Content), not a
    /// UserControl.  A UserControl <em>is</em> a ContentControl whose own visual tree is its
    /// Content, so wrapping consumer-supplied content by binding to Content replaces the
    /// header/border chrome instead of hosting the content — which is why the earlier
    /// UserControl version rendered no header or border (nested groups collapsed into a flat
    /// list of controls).  The visual is defined by a ControlTheme in App.axaml.
    /// </remarks>
    public class GroupBox : HeaderedContentControl {
        // Ensure the implicit ControlTheme is looked up by this type (not the base type),
        // so the App.axaml GroupBox theme is applied rather than the Fluent HeaderedContentControl theme.
        protected override Type StyleKeyOverride => typeof(GroupBox);
    }
}
