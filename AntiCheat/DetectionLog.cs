using System;
using System.Globalization;
using System.Text;

namespace ServerManager;

internal static class DetectionLog
{
    internal static string QuoteValue(
        string value,
        int maximumLength)
    {
        if (maximumLength <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(maximumLength));
        }

        if (string.IsNullOrWhiteSpace(value))
        {
            return "\"-\"";
        }

        StringBuilder builder = new(maximumLength * 2 + 2);
        builder.Append('"');
        for (int index = 0;
             index < value.Length && index < maximumLength;
             ++index)
        {
            char character = value[index];
            UnicodeCategory category =
                char.GetUnicodeCategory(character);
            if (char.IsControl(character) ||
                category == UnicodeCategory.Format ||
                category == UnicodeCategory.LineSeparator ||
                category == UnicodeCategory.ParagraphSeparator)
            {
                builder.Append('?');
            }
            else
            {
                if (character == '\\' || character == '"')
                {
                    builder.Append('\\');
                }

                builder.Append(character);
            }
        }

        builder.Append('"');
        return builder.ToString();
    }
}
