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
using System.Text;
using Avalonia.Controls;

namespace cp2_avalonia.Views;

public partial class AsciiChartWindow : Window
{
    private static readonly string[] CONTROL_NAMES = new string[]
    {
        "NUL", "SOH", "STX", "ETX", "EOT", "ENQ", "ACK", "BEL",
        "BS ", "HT ", "LF ", "VT ", "FF ", "CR ", "SO ", "SI ",
        "DLE", "DC1", "DC2", "DC3", "DC4", "NAK", "SYN", "ETB",
        "CAN", "EM ", "SUB", "ESC", "FS ", "GS ", "RS ", "US "
    };

   

    public AsciiChartWindow()
    {
        InitializeComponent();
        asciiRangeComboBox.SelectedIndex = 0;
        UpdateChartText();
    }

    private void AsciiRangeComboBox_SelectionChanged(object? sender,
            SelectionChangedEventArgs e)
    {
        UpdateChartText();
    }

    private void UpdateChartText()
    {
        bool showHighAscii = asciiRangeComboBox.SelectedIndex == 1;
        asciiChartTextBox.Text = showHighAscii ? GenerateHighAsciiChartText() :
            GenerateLowAsciiChartText();
    }

    private static string GenerateLowAsciiChartText()
    {
        var sb = new StringBuilder();
        sb.AppendLine("Dec Hex Chr Name | Dec Hex Chr | Dec Hex Chr | Dec Hex Chr");
        sb.AppendLine("--- --- --- ---- | --- --- --- | --- --- --- | --- --- ---");
        for (int row = 0; row < 32; row++)
        {
            sb.AppendLine(
                $"{FormatLowAsciiCell(row)} {CONTROL_NAMES[row]}  | {FormatLowAsciiCell(32 + row)} | " +
                $"{FormatLowAsciiCell(64 + row)} | {FormatLowAsciiCell(96 + row)}");
        }
        return sb.ToString();
    }

    private static string GenerateHighAsciiChartText()
    {
        var sb = new StringBuilder();
        sb.AppendLine("Dec Hex Chr Name | Dec Hex Chr | Dec Hex Chr | Dec Hex Chr");
        sb.AppendLine("--- --- --- ---- | --- --- --- | --- --- --- | --- --- ---");
        for (int row = 0; row < 32; row++)
        {
            sb.AppendLine(
                $"{FormatHighAsciiCell(128 + row)} {CONTROL_NAMES[row]}  | {FormatHighAsciiCell(160 + row)} | " +
                $"{FormatHighAsciiCell(192 + row)} | {FormatHighAsciiCell(224 + row)}");
        }
        return sb.ToString();
    }

    private static string FormatLowAsciiCell(int value)
    {
        return $"{value,3}  {value:X2} {GetLowDisplayCharacter(value),-3}";
    }
    
    private static string FormatHighAsciiCell(int value)
    {
        return $"{value,3}  {value:X2} {GetHighDisplayCharacter(value),-3}";
    }

    private static string GetLowDisplayCharacter(int value)
    {
        if (value < 32)
        {
            return "^" + (char)(value + '@');
        }
        if (value == 127)
        {
            return "^?";
        }
        if (value == 32)
        {
            return "SP";
        }
        return EscapeDisplayChar((char)value);
    }

    private static string GetHighDisplayCharacter(int value)
    {
        int baseValue = value & 0x7f;
        if (baseValue < 32)
        {
            return "^" + (char)(baseValue + '@');
        }
        if (baseValue == 127)
        {
            return "~";
        }
        if (baseValue == 32)
        {
            return " ";
        }
        return EscapeDisplayChar((char)baseValue);
    }

    private static string EscapeDisplayChar(char ch)
    {
        return ch switch
        {
            '\\' => "\\",
            _ => ch.ToString()
        };
    }

    private static string GetCodeName(int value)
    {
        if (value < 32)
        {
            return CONTROL_NAMES[value];
        }
        return value switch
        {
            32 => "SP",
            127 => "DEL",
            _ => string.Empty
        };
    }
    
}
