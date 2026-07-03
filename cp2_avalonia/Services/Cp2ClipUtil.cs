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
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Text.Json;

namespace cp2_avalonia.Services;

/// <summary>
/// Helpers for extending clipboard JSON within cp2_avalonia without modifying AppCommon.
///
/// AppCommon's ClipFileEntry carries metadata only.  File content is passed via a
/// "Cp2FileData" array injected into the ClipInfo JSON by the source instance.
/// System.Text.Json ignores unknown properties, so ClipInfo.FromClipString is unaffected;
/// the array is read separately by cp2_avalonia before building the stream generator.
///
/// Each element of Cp2FileData corresponds by index to ClipInfo.ClipEntries.
/// A null element means the entry has no associated file (directory, or no content).
/// A non-null element is an absolute path to a temp file on the same machine.
/// </summary>
internal static class Cp2ClipUtil
{
    private const string FieldName = "Cp2FileData";

    /// <summary>
    /// Injects a Cp2FileData array into clipboard JSON produced by ClipInfo.ToClipString.
    /// Returns the original string unchanged if injection fails or if all paths are null.
    /// </summary>
    internal static string InjectFileData(string clipText, string?[] filePaths)
    {
        bool hasAny = false;
        foreach (string? p in filePaths) if (p != null) { hasAny = true; break; }
        if (!hasAny) return clipText;

        const string prefix = "CiderPressII:clip:v1:";
        if (!clipText.StartsWith(prefix)) return clipText;
        string json = clipText.Substring(prefix.Length);

        try
        {
            using var doc = JsonDocument.Parse(json);
            using var ms = new MemoryStream();
            using (var writer = new Utf8JsonWriter(ms))
            {
                writer.WriteStartObject();
                foreach (var prop in doc.RootElement.EnumerateObject())
                    prop.WriteTo(writer);
                writer.WritePropertyName(FieldName);
                writer.WriteStartArray();
                foreach (string? path in filePaths)
                {
                    if (path == null) writer.WriteNullValue();
                    else writer.WriteStringValue(path);
                }
                writer.WriteEndArray();
                writer.WriteEndObject();
            }
            return prefix + Encoding.UTF8.GetString(ms.ToArray());
        }
        catch (Exception ex)
        {
            AppLog.E("Clipboard: failed to inject file data", ex);
            return clipText;
        }
    }

    /// <summary>
    /// Extracts the Cp2FileData array from clipboard JSON.  Returns null if absent.
    /// </summary>
    internal static string?[]? ExtractFileData(string? clipText)
    {
        if (clipText == null) return null;
        const string prefix = "CiderPressII:clip:v1:";
        string json = clipText.StartsWith(prefix)
            ? clipText.Substring(prefix.Length)
            : clipText;

        try
        {
            using var doc = JsonDocument.Parse(json);
            if (!doc.RootElement.TryGetProperty(FieldName, out JsonElement arr) ||
                arr.ValueKind != JsonValueKind.Array)
                return null;

            var result = new List<string?>();
            foreach (JsonElement item in arr.EnumerateArray())
                result.Add(item.ValueKind == JsonValueKind.String ? item.GetString() : null);
            return result.ToArray();
        }
        catch (Exception ex)
        {
            AppLog.E("Clipboard: failed to extract file data", ex);
            return null;
        }
    }
}
