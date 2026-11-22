using System.Globalization;
using Microsoft.AspNetCore.Mvc.ViewFeatures;

namespace Recall.Web.Extensions;

/// <summary>
/// Typed reads of TempData that work whatever shape the value comes back in.
/// </summary>
/// <remarks>
/// TempData travels in a cookie between a POST and the page that shows its
/// toast, and the cookie serializer does not give back what was written: a
/// string that looks like a GUID comes back as a <see cref="Guid"/>, one that
/// looks like an ISO date as a <see cref="DateTime"/>, and an <see cref="int"/>
/// or a <see cref="bool"/> stays one, while within the same request the value
/// is still exactly what was written. A read like <c>tempData[key] as string</c>
/// therefore silently gives null for some values, which is how the Undo of a
/// bulk mark (a batch id written as a GUID string) went missing from every
/// toast between 2026-11-13 and 2026-11-22. Read TempData only through these.
/// (A <see cref="long"/> cannot be written at all; the serializer throws.)
/// </remarks>
public static class TempDataValueExtensions
{
    extension(ITempDataDictionary tempData)
    {
        /// <summary>
        /// The value as text, or null when there is none. A value the
        /// serializer turned into another type is written back in invariant
        /// form (a GUID as "D"); a date-shaped string comes back as ISO 8601,
        /// not necessarily in its original spelling.
        /// </summary>
        public string? GetText(string key) => AsText(tempData[key]);

        /// <summary>The value as an <see cref="int"/>, from an int or its invariant text; null otherwise.</summary>
        public int? GetInt32(string key) =>
            tempData[key] switch
            {
                int value => value,
                long value and >= int.MinValue and <= int.MaxValue => (int)value,
                var other => int.TryParse(AsText(other), NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed) ? parsed : null
            };

        /// <summary>The value as a <see cref="long"/>, from an integer or its invariant text; null otherwise.</summary>
        public long? GetInt64(string key) =>
            tempData[key] switch
            {
                long value => value,
                int value => value,
                var other => long.TryParse(AsText(other), NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed) ? parsed : null
            };

        /// <summary>The value as a <see cref="Guid"/>, from a Guid or its text; null otherwise.</summary>
        public Guid? GetGuid(string key) =>
            tempData[key] switch
            {
                Guid value => value,
                var other => Guid.TryParse(AsText(other), out var parsed) ? parsed : null
            };
    }

    private static string? AsText(object? value) => value switch
    {
        null => null,
        string text => text,
        Guid guid => guid.ToString("D"),
        DateTime dateTime => dateTime.ToString("O", CultureInfo.InvariantCulture),
        bool flag => flag ? "true" : "false",
        IFormattable formattable => formattable.ToString(null, CultureInfo.InvariantCulture),
        _ => value.ToString()
    };
}
