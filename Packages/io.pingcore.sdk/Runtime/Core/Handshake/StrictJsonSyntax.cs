namespace PingCore.Core.Handshake
{
    /// <summary>
    /// An RFC 8259 syntax check, so the join-ticket decoder refuses what Newtonsoft would otherwise
    /// forgive (single quotes, unquoted names, comments, trailing commas, <c>NaN</c>, leading zeros,
    /// control characters in strings) and every engine's decoder accepts exactly the same payloads.
    /// It checks syntax only; duplicate keys and the schema are judged afterwards. Bounded depth,
    /// no recursion beyond it, no allocation.
    /// </summary>
    internal static class StrictJsonSyntax
    {
        private const int MaxDepth = 64;

        /// <summary>True when <paramref name="text"/> is exactly one JSON object with optional surrounding whitespace.</summary>
        public static bool IsSingleObject(string text)
        {
            if (text == null)
            {
                return false;
            }

            int i = 0;
            SkipWhitespace(text, ref i);
            if (i >= text.Length || text[i] != '{')
            {
                return false;
            }

            if (!Value(text, ref i, 0))
            {
                return false;
            }

            SkipWhitespace(text, ref i);
            return i == text.Length;
        }

        private static bool Value(string s, ref int i, int depth)
        {
            if (depth > MaxDepth || i >= s.Length)
            {
                return false;
            }

            switch (s[i])
            {
                case '{':
                    return ObjectValue(s, ref i, depth + 1);
                case '[':
                    return ArrayValue(s, ref i, depth + 1);
                case '"':
                    return StringValue(s, ref i);
                case 't':
                    return Literal(s, ref i, "true");
                case 'f':
                    return Literal(s, ref i, "false");
                case 'n':
                    return Literal(s, ref i, "null");
                default:
                    return NumberValue(s, ref i);
            }
        }

        private static bool ObjectValue(string s, ref int i, int depth)
        {
            i++; // '{'
            SkipWhitespace(s, ref i);
            if (i < s.Length && s[i] == '}')
            {
                i++;
                return true;
            }

            while (true)
            {
                SkipWhitespace(s, ref i);
                if (i >= s.Length || s[i] != '"' || !StringValue(s, ref i))
                {
                    return false;
                }

                SkipWhitespace(s, ref i);
                if (i >= s.Length || s[i] != ':')
                {
                    return false;
                }

                i++;
                SkipWhitespace(s, ref i);
                if (!Value(s, ref i, depth))
                {
                    return false;
                }

                SkipWhitespace(s, ref i);
                if (i >= s.Length)
                {
                    return false;
                }

                if (s[i] == ',')
                {
                    i++;
                    continue;
                }

                if (s[i] == '}')
                {
                    i++;
                    return true;
                }

                return false;
            }
        }

        private static bool ArrayValue(string s, ref int i, int depth)
        {
            i++; // '['
            SkipWhitespace(s, ref i);
            if (i < s.Length && s[i] == ']')
            {
                i++;
                return true;
            }

            while (true)
            {
                SkipWhitespace(s, ref i);
                if (!Value(s, ref i, depth))
                {
                    return false;
                }

                SkipWhitespace(s, ref i);
                if (i >= s.Length)
                {
                    return false;
                }

                if (s[i] == ',')
                {
                    i++;
                    continue;
                }

                if (s[i] == ']')
                {
                    i++;
                    return true;
                }

                return false;
            }
        }

        private static bool StringValue(string s, ref int i)
        {
            i++; // opening quote
            while (i < s.Length)
            {
                char c = s[i];
                if (c == '"')
                {
                    i++;
                    return true;
                }

                if (c < 0x20)
                {
                    return false;
                }

                if (c == '\\')
                {
                    i++;
                    if (i >= s.Length)
                    {
                        return false;
                    }

                    char e = s[i];
                    if (e == 'u')
                    {
                        if (i + 4 >= s.Length)
                        {
                            return false;
                        }

                        for (int k = 1; k <= 4; k++)
                        {
                            if (!IsHex(s[i + k]))
                            {
                                return false;
                            }
                        }

                        i += 4;
                    }
                    else if (e != '"' && e != '\\' && e != '/' && e != 'b' && e != 'f' && e != 'n' && e != 'r' && e != 't')
                    {
                        return false;
                    }
                }

                i++;
            }

            return false;
        }

        private static bool NumberValue(string s, ref int i)
        {
            int start = i;
            if (i < s.Length && s[i] == '-')
            {
                i++;
            }

            if (i >= s.Length)
            {
                return false;
            }

            if (s[i] == '0')
            {
                i++;
            }
            else if (s[i] >= '1' && s[i] <= '9')
            {
                while (i < s.Length && IsDigit(s[i]))
                {
                    i++;
                }
            }
            else
            {
                return false;
            }

            if (i < s.Length && s[i] == '.')
            {
                i++;
                if (!Digits(s, ref i))
                {
                    return false;
                }
            }

            if (i < s.Length && (s[i] == 'e' || s[i] == 'E'))
            {
                i++;
                if (i < s.Length && (s[i] == '+' || s[i] == '-'))
                {
                    i++;
                }

                if (!Digits(s, ref i))
                {
                    return false;
                }
            }

            return i > start;
        }

        private static bool Digits(string s, ref int i)
        {
            int start = i;
            while (i < s.Length && IsDigit(s[i]))
            {
                i++;
            }

            return i > start;
        }

        private static bool Literal(string s, ref int i, string word)
        {
            if (string.CompareOrdinal(s, i, word, 0, word.Length) != 0)
            {
                return false;
            }

            i += word.Length;
            return true;
        }

        private static void SkipWhitespace(string s, ref int i)
        {
            while (i < s.Length && (s[i] == ' ' || s[i] == '\t' || s[i] == '\n' || s[i] == '\r'))
            {
                i++;
            }
        }

        private static bool IsDigit(char c) => c >= '0' && c <= '9';

        private static bool IsHex(char c) => IsDigit(c) || (c >= 'a' && c <= 'f') || (c >= 'A' && c <= 'F');
    }
}
