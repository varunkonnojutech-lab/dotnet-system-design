namespace SystemDesign.Core;

/// <summary>
/// Base62 encoding: the standard way a URL shortener turns a numeric id
/// into a short, URL-safe code. 62 symbols (0-9, a-z, A-Z) means 7 characters
/// already hold 62^7 ≈ 3.5 trillion distinct codes.
/// </summary>
public static class Base62
{
    private const string Alphabet = "0123456789abcdefghijklmnopqrstuvwxyzABCDEFGHIJKLMNOPQRSTUVWXYZ";

    public static string Encode(long value)
    {
        if (value < 0) throw new ArgumentOutOfRangeException(nameof(value), "Ids are never negative.");
        if (value == 0) return "0";

        var chars = new Stack<char>();
        while (value > 0)
        {
            chars.Push(Alphabet[(int)(value % 62)]);
            value /= 62;
        }
        return new string(chars.ToArray());
    }

    public static long Decode(string encoded)
    {
        if (string.IsNullOrEmpty(encoded))
            throw new ArgumentException("Code must not be empty.", nameof(encoded));

        long value = 0;
        foreach (var c in encoded)
        {
            var index = Alphabet.IndexOf(c);
            if (index < 0)
                throw new ArgumentException($"'{c}' is not a Base62 character.", nameof(encoded));
            value = checked(value * 62 + index);
        }
        return value;
    }
}
