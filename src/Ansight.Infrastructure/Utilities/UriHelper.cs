// ReSharper disable UnusedMember.Global
using System.Buffers;

namespace Ansight.Infrastructure.Utilities;

public static class UriHelper
{
    public static Dictionary<string, string> DecodeQueryParameters(string uri)
    {
        if (uri == null)
        {
            return new Dictionary<string, string>();
        }

        return DecodeQueryParameters(new Uri(uri));
    }

    public static Dictionary<string, string> DecodeQueryParameters(Uri uri)
    {
        if (uri == null)
        {
            return new Dictionary<string, string>();
        }

        var query = uri.Query;
        if (string.IsNullOrEmpty(query) || query == "?")
        {
            return new Dictionary<string, string>();
        }

        var result = new Dictionary<string, string>();
        var span = query.AsSpan();
        if (span[0] == '?')
        {
            span = span[1..];
        }

        var start = 0;
        for (var i = 0; i <= span.Length; i++)
        {
            if (i < span.Length && span[i] != '&' && span[i] != ';')
            {
                continue;
            }

            var length = i - start;
            if (length > 0)
            {
                ParseParameter(span.Slice(start, length), result);
            }

            start = i + 1;
        }

        return result;
    }

    private static void ParseParameter(ReadOnlySpan<char> pair, IDictionary<string, string> result)
    {
        if (pair.IsEmpty)
        {
            return;
        }

        var equalsIndex = pair.IndexOf('=');
        if (equalsIndex == 0)
        {
            return;
        }

        ReadOnlySpan<char> keySpan;
        ReadOnlySpan<char> valueSpan;

        if (equalsIndex < 0)
        {
            keySpan = pair;
            valueSpan = ReadOnlySpan<char>.Empty;
        }
        else
        {
            keySpan = pair[..equalsIndex];
            valueSpan = pair[(equalsIndex + 1)..];
        }

        if (keySpan.IsEmpty)
        {
            return;
        }

        var key = DecodeComponent(keySpan);
        var value = DecodeComponent(valueSpan);

        if (result.TryGetValue(key, out var existing))
        {
            result[key] = string.Concat(existing, ",", value);
        }
        else
        {
            result[key] = value;
        }
    }

    private static string DecodeComponent(ReadOnlySpan<char> value)
    {
        if (value.IsEmpty)
        {
            return string.Empty;
        }

        var needsDecoding = false;
        for (var i = 0; i < value.Length; i++)
        {
            var c = value[i];
            if (c == '%' || c == '+')
            {
                needsDecoding = true;
                break;
            }
        }

        if (!needsDecoding)
        {
            return new string(value);
        }

        var rented = ArrayPool<char>.Shared.Rent(value.Length);
        try
        {
            var length = 0;
            for (var i = 0; i < value.Length; i++)
            {
                var c = value[i];
                if (c == '+')
                {
                    rented[length++] = ' ';
                }
                else if (c == '%' && i + 2 < value.Length && TryDecodeHex(value[i + 1], value[i + 2], out var decoded))
                {
                    rented[length++] = decoded;
                    i += 2;
                }
                else
                {
                    rented[length++] = c;
                }
            }

            return new string(rented, 0, length);
        }
        finally
        {
            ArrayPool<char>.Shared.Return(rented);
        }
    }

    private static bool TryDecodeHex(char high, char low, out char value)
    {
        static int FromHex(char c)
        {
            if (c >= '0' && c <= '9') return c - '0';
            if (c >= 'a' && c <= 'f') return c - 'a' + 10;
            if (c >= 'A' && c <= 'F') return c - 'A' + 10;
            return -1;
        }

        var highValue = FromHex(high);
        var lowValue = FromHex(low);

        if (highValue == -1 || lowValue == -1)
        {
            value = default;
            return false;
        }

        value = (char)((highValue << 4) | lowValue);
        return true;
    }
}
