using System.Text.Json;
using System.Text.Json.Serialization;

namespace TouchNumPad.Models;

public enum AppThemePreference
{
    System = 0,
    Light = 1,
    Dark = 2
}

public sealed class AppSettings
{
    private static readonly string SettingsDirectory = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "TouchNumPad");

    private static readonly string SettingsFilePath = Path.Combine(SettingsDirectory, "settings.json");

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() }
    };

    // Default: 95% opacity
    public double WindowOpacity { get; set; } = 0.95;

    // Default: System theme
    public AppThemePreference Theme { get; set; } = AppThemePreference.System;

    // Default: Shift (0x0004) + Alt (0x0001) = 0x0005
    public uint HotkeyModifiers { get; set; } = 0x0001 | 0x0004;

    // Default: 'N' (0x4E)
    public uint HotkeyVirtualKey { get; set; } = 0x4E;

    // Saved window position
    public int? WindowX { get; set; }
    public int? WindowY { get; set; }

    public static AppSettings Load()
    {
        try
        {
            if (File.Exists(SettingsFilePath))
            {
                string json = File.ReadAllText(SettingsFilePath);
                var loaded = JsonSerializer.Deserialize<AppSettings>(json, JsonOptions);
                if (loaded is not null)
                {
                    loaded.WindowOpacity = Math.Clamp(loaded.WindowOpacity, 0.25, 1.0);
                    if (loaded.HotkeyVirtualKey == 0)
                    {
                        loaded.HotkeyModifiers = 0x0001 | 0x0004;
                        loaded.HotkeyVirtualKey = 0x4E;
                    }
                    return loaded;
                }
            }
        }
        catch
        {
            // Fall back to defaults on corrupted file
        }

        return new AppSettings();
    }

    public void Save()
    {
        try
        {
            Directory.CreateDirectory(SettingsDirectory);
            string json = JsonSerializer.Serialize(this, JsonOptions);
            File.WriteAllText(SettingsFilePath, json);
        }
        catch
        {
            // Ignore write errors
        }
    }

    public string GetHotkeyDisplayText()
    {
        return FormatHotkey(HotkeyModifiers, HotkeyVirtualKey);
    }

    public static string FormatHotkey(uint modifiers, uint vk)
    {
        var parts = new List<string>();
        if ((modifiers & 0x0002) != 0) parts.Add("Ctrl");
        if ((modifiers & 0x0004) != 0) parts.Add("Shift");
        if ((modifiers & 0x0001) != 0) parts.Add("Alt");
        if ((modifiers & 0x0008) != 0) parts.Add("Win");

        parts.Add(VirtualKeyToDisplayName(vk));
        return string.Join(" + ", parts);
    }

    public static string VirtualKeyToDisplayName(uint vk)
    {
        return vk switch
        {
            >= 0x30 and <= 0x39 => ((char)vk).ToString(),
            >= 0x41 and <= 0x5A => ((char)vk).ToString(),
            >= 0x60 and <= 0x69 => $"Num {vk - 0x60}",
            >= 0x70 and <= 0x87 => $"F{vk - 0x70 + 1}",
            0x08 => "Backspace",
            0x09 => "Tab",
            0x0D => "Enter",
            0x13 => "Pause",
            0x14 => "CapsLock",
            0x1B => "Esc",
            0x20 => "Space",
            0x21 => "PageUp",
            0x22 => "PageDown",
            0x23 => "End",
            0x24 => "Home",
            0x25 => "←",
            0x26 => "↑",
            0x27 => "→",
            0x28 => "↓",
            0x2C => "PrtScn",
            0x2D => "Insert",
            0x2E => "Delete",
            0x6A => "Num *",
            0x6B => "Num +",
            0x6D => "Num -",
            0x6E => "Num .",
            0x6F => "Num /",
            0x90 => "NumLock",
            0x91 => "ScrollLock",
            0xBA => ";",
            0xBB => "=",
            0xBC => ",",
            0xBD => "-",
            0xBE => ".",
            0xBF => "/",
            0xC0 => "`",
            0xDB => "[",
            0xDC => "\\",
            0xDD => "]",
            0xDE => "'",
            _ => $"Key(0x{vk:X2})"
        };
    }
}
