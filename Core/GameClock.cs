using System;
using System.Linq;

namespace AliveSensor.Core;

/// <summary>Game-time math and wording. Pure functions: safe on any thread.</summary>
internal static class GameClock
{
    /// <summary>Minutes in an active game day (6:00 → 2:00).</summary>
    public const double MinutesPerDay = 1200.0;

    /// <summary>1430 → 870.</summary>
    public static int ToMinutes(int time) => time / 100 * 60 + time % 100;

    public static int MinutesBetween(int fromTime, int toTime) => ToMinutes(toTime) - ToMinutes(fromTime);

    /// <summary>Age of an event in (fractional) game days.</summary>
    public static double AgeInDays(int eventTotalDay, int eventTime, int nowTotalDay, int nowTime)
        => Math.Max(0, (nowTotalDay - eventTotalDay) + (ToMinutes(nowTime) - ToMinutes(eventTime)) / MinutesPerDay);

    /// <summary>1430 → "2:30 PM"; 2530 → "1:30 AM".</summary>
    public static string Clock12(int time)
    {
        int hour = time / 100 % 24;
        int minute = time % 100;
        string suffix = hour >= 12 ? "PM" : "AM";
        int hour12 = hour % 12 == 0 ? 12 : hour % 12;
        return minute == 0 ? $"{hour12} {suffix}" : $"{hour12}:{minute:00} {suffix}";
    }

    public static string PartOfDay(int time) => time switch
    {
        < 1200 => "morning",
        < 1700 => "afternoon",
        < 2100 => "evening",
        _ => "night",
    };

    /// <summary>"just now", "about 2 hours ago", "yesterday evening", "last night", "3 days ago", "last week"...</summary>
    public static string Relative(int eventTotalDay, int eventTime, int nowTotalDay, int nowTime)
    {
        int days = nowTotalDay - eventTotalDay;
        if (days <= 0)
        {
            int minutes = Math.Max(0, MinutesBetween(eventTime, nowTime));
            if (minutes < 20)
                return "just now";
            if (minutes < 60)
                return $"about {minutes / 10 * 10} minutes ago";
            int hours = (int)Math.Round(minutes / 60.0);
            return hours <= 1 ? "about an hour ago" : $"about {hours} hours ago";
        }
        if (days == 1)
            return PartOfDay(eventTime) == "night" ? "last night" : $"yesterday {PartOfDay(eventTime)}";
        if (days < 7)
            return $"{days} days ago";
        if (days < 14)
            return "last week";
        if (days < 28)
            return $"about {days / 7} weeks ago";
        int seasons = days / 28;
        return seasons == 1 ? "about a season ago" : $"about {seasons} seasons ago";
    }

    /// <summary>"spring 5, 2:30 PM" (year added when it differs from the current one).</summary>
    public static string Absolute(int day, string season, int year, int time, int currentYear)
        => year == currentYear ? $"{season} {day}, {Clock12(time)}" : $"{season} {day} of year {year}, {Clock12(time)}";

    /// <summary>22:00 – 2:00.</summary>
    public static bool IsLateNight(int time) => time >= 2200;
}

/// <summary>Deterministic pseudo-random rolls (string.GetHashCode is randomized per process in .NET).</summary>
internal static class StableRandom
{
    /// <summary>A value in [0, 1] that is always the same for the same inputs.</summary>
    public static double Roll(params string[] parts)
    {
        uint hash = 2166136261;
        foreach (string part in parts)
        {
            foreach (char c in part)
            {
                hash ^= c;
                hash *= 16777619;
            }
            hash ^= '|';
            hash *= 16777619;
        }
        return hash / (double)uint.MaxValue;
    }
}

internal static class Text
{
    /// <summary>Shorten on a word boundary and add an ellipsis.</summary>
    public static string Clip(string? value, int maxChars)
    {
        if (string.IsNullOrWhiteSpace(value))
            return "";
        string text = value.Replace('\n', ' ').Replace('\r', ' ').Trim();
        if (text.Length <= maxChars)
            return text;
        int cut = text.LastIndexOf(' ', Math.Max(0, maxChars - 1));
        if (cut < maxChars / 2)
            cut = maxChars;
        return text.Substring(0, cut).TrimEnd(',', '.', ';', ' ') + "…";
    }

    /// <summary>
    /// "a Daffodil" / "an Amethyst". Item names come localized from the game ("Biscoitos", "Algas Verdes"),
    /// so the article is only added to names that look like an English singular (ASCII, one or two words, no trailing s).
    /// </summary>
    public static string Article(string noun)
    {
        if (string.IsNullOrWhiteSpace(noun))
            return "something";
        bool ascii = noun.All(ch => ch < 128);
        bool plural = noun.EndsWith("s", StringComparison.OrdinalIgnoreCase);
        bool shortName = noun.Split(' ', StringSplitOptions.RemoveEmptyEntries).Length <= 2;
        if (!ascii || plural || !shortName)
            return noun;
        return "aeiouAEIOU".IndexOf(noun[0]) >= 0 ? $"an {noun}" : $"a {noun}";
    }

    public static string Ordinal(int n) => n switch
    {
        1 => "first", 2 => "second", 3 => "third", 4 => "fourth", 5 => "fifth",
        _ => $"{n}th",
    };

    public static string Capitalize(string value) => string.IsNullOrEmpty(value) ? value : char.ToUpperInvariant(value[0]) + value.Substring(1);

    /// <summary>["Jodi","Kent"] → "Jodi and Kent"; three or more → "A, B and C".</summary>
    public static string JoinAnd(System.Collections.Generic.IReadOnlyList<string> items)
    {
        return items.Count switch
        {
            0 => "",
            1 => items[0],
            2 => $"{items[0]} and {items[1]}",
            _ => string.Join(", ", System.Linq.Enumerable.Take(items, items.Count - 1)) + " and " + items[items.Count - 1],
        };
    }

    public static System.Collections.Generic.List<string> SplitList(string? csv)
    {
        var list = new System.Collections.Generic.List<string>();
        if (string.IsNullOrWhiteSpace(csv))
            return list;
        foreach (string part in csv.Split(','))
        {
            string trimmed = part.Trim();
            if (trimmed.Length > 0)
                list.Add(trimmed);
        }
        return list;
    }
}
