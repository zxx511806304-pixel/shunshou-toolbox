using System.Globalization;

namespace Shunshou.Core;

/// <summary>Parses "00:01:30", "1:05" or "75" into a position inside a media file.</summary>
public static class ClipTime
{
    public static bool TryParse(string? text, out double seconds)
    {
        seconds = 0;
        string value = (text ?? "").Trim();
        if (value.Length == 0) return false;
        string[] parts = value.Split(':');
        if (parts.Length is 0 or > 3) return false;
        double total = 0;
        foreach (string part in parts)
        {
            if (!double.TryParse(part.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out double number) || number < 0) return false;
            total = total * 60 + number;
        }
        if (!double.IsFinite(total)) return false;
        seconds = total;
        return true;
    }
}
