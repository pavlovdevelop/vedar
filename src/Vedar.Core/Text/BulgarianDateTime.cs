using System.Globalization;

namespace Vedar.Core.Text;

/// <summary>Spoken Bulgarian phrases for times and dates.</summary>
public static class BulgarianDateTime
{
    public static readonly string[] Months =
        ["януари", "февруари", "март", "април", "май", "юни", "юли", "август", "септември", "октомври", "ноември", "декември"];

    public static readonly string[] DaysOfWeek =
        ["неделя", "понеделник", "вторник", "сряда", "четвъртък", "петък", "събота"];

    /// <summary>Phrase used after "Часът е": "четиринадесет и тридесет и пет минути", "точно девет".</summary>
    public static string SpokenTime(int hour, int minute)
    {
        var hourWords = BulgarianNumbers.Cardinal(hour, GrammaticalGender.Masculine);
        if (minute == 0)
        {
            return $"точно {hourWords}";
        }

        var minuteWords = BulgarianNumbers.Count(minute, "минута", "минути", GrammaticalGender.Feminine);
        return $"{hourWords} и {minuteWords}";
    }

    /// <summary>"неделя, първи ноември две хиляди двадесет и шеста година".</summary>
    public static string SpokenDate(DateTime date, bool includeDayOfWeek = true, bool includeYear = true)
    {
        var day = BulgarianNumbers.Ordinal(date.Day, GrammaticalGender.Masculine);
        var text = $"{day} {Months[date.Month - 1]}";
        if (includeYear)
        {
            text += " " + SpokenYear(date.Year);
        }

        return includeDayOfWeek ? $"{DaysOfWeek[(int)date.DayOfWeek]}, {text}" : text;
    }

    public static string SpokenYear(int year) => BulgarianNumbers.Ordinal(year, GrammaticalGender.Feminine) + " година";

    public static string FormatShortDate(DateTime date) => date.ToString("d MMMM yyyy", CultureInfo.GetCultureInfo("bg-BG"));
}
