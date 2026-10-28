using System.Globalization;
namespace SssjServer.Game.Handlers;

internal static class PhoneEventWindow
{
    // Preserve existing local-hour and inclusive-end semantics; calendar validation
    // prevents malformed configuration from accidentally unlocking content.
    internal static bool Contains(string text, DateTime localNow)
    {
        var parts = text.Trim().Trim('[', ']').Split('-');
        if (parts.Length != 2 || !Parse(parts[0], out var from) ||
            !Parse(parts[1], out var to) || from > to) return false;
        var hour = new DateTime(localNow.Year, localNow.Month, localNow.Day, localNow.Hour, 0, 0);
        return hour >= from && hour <= to;
    }
    private static bool Parse(string value, out DateTime result)
        => DateTime.TryParseExact(value.Trim(), "yyyyMMddHH", CultureInfo.InvariantCulture,
            DateTimeStyles.None, out result);
}
