using System.Globalization;

namespace AIEngineer.Core.Settings
{
    /// <summary>
    /// A number typed into a settings field, read as the player types it: a
    /// dot or a comma for the decimal point, and taken only once it reads as
    /// a number within the field's range ("1." on the way to "1.5" is 1;
    /// "" or "7" for a field up to 6 is nothing, so the value stays and
    /// never jumps to an end of the range).
    /// </summary>
    public static class TypedNumber
    {
        public static bool TryRead(string text, float min, float max, out float value)
        {
            value = 0;
            if (string.IsNullOrWhiteSpace(text))
                return false;
            if (!float.TryParse(text.Trim().Replace(',', '.'), NumberStyles.Float, CultureInfo.InvariantCulture, out var v))
                return false;
            if (float.IsNaN(v) || v < min || v > max)
                return false;
            value = v;
            return true;
        }
    }
}
