using Microsoft.Win32;

namespace SimpleWindowsScreentime.Blocker;

public class AccessibilityManager
{
    private const string StickyKeysKey = @"Control Panel\Accessibility\StickyKeys";
    private const string FilterKeysKey = @"Control Panel\Accessibility\Keyboard Response";
    private const string ToggleKeysKey = @"Control Panel\Accessibility\ToggleKeys";

    private string? _originalStickyKeysFlags;
    private string? _originalFilterKeysFlags;
    private string? _originalToggleKeysFlags;

    public void SuppressAccessibilityShortcuts()
    {
        try
        {
            // Backup and disable StickyKeys popup
            using (var key = Registry.CurrentUser.OpenSubKey(StickyKeysKey, true))
            {
                if (key != null)
                {
                    _originalStickyKeysFlags = key.GetValue("Flags") as string;
                    // Set flags to disable the confirmation dialog (bit 0 = 0)
                    // 506 = default, 510 = with popup disabled
                    key.SetValue("Flags", "506");
                }
            }

            // Backup and disable FilterKeys popup
            using (var key = Registry.CurrentUser.OpenSubKey(FilterKeysKey, true))
            {
                if (key != null)
                {
                    _originalFilterKeysFlags = key.GetValue("Flags") as string;
                    key.SetValue("Flags", "122");
                }
            }

            // Backup and disable ToggleKeys popup
            using (var key = Registry.CurrentUser.OpenSubKey(ToggleKeysKey, true))
            {
                if (key != null)
                {
                    _originalToggleKeysFlags = key.GetValue("Flags") as string;
                    key.SetValue("Flags", "58");
                }
            }
        }
        catch
        {
            // Ignore registry access errors
        }
    }

    public void RestoreAccessibilityShortcuts()
    {
        try
        {
            // Restore StickyKeys
            if (_originalStickyKeysFlags != null)
            {
                using var key = Registry.CurrentUser.OpenSubKey(StickyKeysKey, true);
                key?.SetValue("Flags", _originalStickyKeysFlags);
            }

            // Restore FilterKeys
            if (_originalFilterKeysFlags != null)
            {
                using var key = Registry.CurrentUser.OpenSubKey(FilterKeysKey, true);
                key?.SetValue("Flags", _originalFilterKeysFlags);
            }

            // Restore ToggleKeys
            if (_originalToggleKeysFlags != null)
            {
                using var key = Registry.CurrentUser.OpenSubKey(ToggleKeysKey, true);
                key?.SetValue("Flags", _originalToggleKeysFlags);
            }
        }
        catch
        {
            // Ignore registry access errors
        }
    }
}
