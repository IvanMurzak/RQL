using System.Collections.Generic;
using System.Globalization;
using System.Text;

namespace Rql.Parsing
{
    public enum TokenKind { Ident, QuotedIdent, Meta, Number, String, Punct, End }

    public readonly struct Token
    {
        public Token(TokenKind kind, string text, int start, int end, object? value = null, bool spaceBefore = false)
        {
            Kind = kind; Text = text; Start = start; End = end; Value = value; SpaceBefore = spaceBefore;
        }
        public TokenKind Kind { get; }
        /// <summary>Identifier name / punctuation / meta name (without '$').</summary>
        public string Text { get; }
        public int Start { get; }
        public int End { get; }
        public object? Value { get; }
        public bool SpaceBefore { get; }
        public TextSpan Span => new TextSpan(Start, End);

        public bool Is(string punct) => Kind == TokenKind.Punct && Text == punct;
        public bool IsWord(string word) => Kind == TokenKind.Ident && string.Equals(Text, word, System.StringComparison.OrdinalIgnoreCase);

        public string Describe() => Kind switch
        {
            TokenKind.End => "end of query",
            TokenKind.String => "string literal",
            TokenKind.Number => "number " + Text,
            TokenKind.Meta => "'$" + Text + "'",
            _ => "'" + Text + "'",
        };
    }

    public static class Lexer
    {
        private static readonly string[] Puncts = { "==", "!=", "<=", ">=", "&&", "||", "|", ".", ",", "(", ")", "[", "]", "=", "<", ">", "+", "-", "*", "/", "%", "!" };

        public static List<Token> Tokenize(string text)
        {
            var tokens = new List<Token>();
            int i = 0;
            int n = text.Length;
            while (true)
            {
                int ws = i;
                while (i < n && char.IsWhiteSpace(text[i])) i++;
                bool space = i > ws || i == 0;
                if (i >= n) { tokens.Add(new Token(TokenKind.End, "", n, n, null, space)); return tokens; }
                char c = text[i];
                int start = i;
                if (IsIdentStart(c))
                {
                    i = ReadIdent(text, i);
                    tokens.Add(new Token(TokenKind.Ident, text.Substring(start, i - start), start, i, null, space));
                }
                else if (c == '$')
                {
                    i++;
                    if (i < n && IsIdentStart(text[i])) i = ReadIdent(text, i);
                    tokens.Add(new Token(TokenKind.Meta, text.Substring(start + 1, i - start - 1), start, i, null, space));
                }
                else if (c == '`')
                {
                    int close = text.IndexOf('`', i + 1);
                    if (close < 0) throw Error("unterminated quoted identifier", start, n);
                    if (close == i + 1) throw Error("empty quoted identifier", start, close + 1);
                    tokens.Add(new Token(TokenKind.QuotedIdent, text.Substring(i + 1, close - i - 1), start, close + 1, null, space));
                    i = close + 1;
                }
                else if (IsDigit(c))
                {
                    i = ReadNumber(text, i, out var value);
                    tokens.Add(new Token(TokenKind.Number, text.Substring(start, i - start), start, i, value, space));
                }
                else if (c == '"' || c == '\'')
                {
                    i = ReadString(text, i, out var value);
                    tokens.Add(new Token(TokenKind.String, text.Substring(start, i - start), start, i, value, space));
                }
                else
                {
                    string? p = null;
                    foreach (var cand in Puncts)
                    {
                        if (string.CompareOrdinal(text, i, cand, 0, cand.Length) == 0) { p = cand; break; }
                    }
                    if (p == null)
                        throw Error($"unexpected character '{c}'", start, start + 1);
                    i += p.Length;
                    tokens.Add(new Token(TokenKind.Punct, p, start, i, null, space));
                }
            }
        }

        private static RqlException Error(string message, int start, int end) =>
            new RqlException("syntax-error", message + " at " + start, new TextSpan(start, end));

        private static bool IsDigit(char c) => c >= '0' && c <= '9';

        public static bool IsIdentStart(char c) => (c >= 'a' && c <= 'z') || (c >= 'A' && c <= 'Z') || c == '_';
        private static bool IsIdentPart(char c) => IsIdentStart(c) || (c >= '0' && c <= '9');

        /// <summary>ident_seg { "-" ident_seg } — hyphen only directly between segments, next char a letter/_.</summary>
        private static int ReadIdent(string text, int i)
        {
            int n = text.Length;
            while (i < n && IsIdentPart(text[i])) i++;
            while (i + 1 < n && text[i] == '-' && IsIdentStart(text[i + 1]))
            {
                i++;
                while (i < n && IsIdentPart(text[i])) i++;
            }
            return i;
        }

        private static int ReadNumber(string text, int i, out double value)
        {
            int start = i, n = text.Length;
            while (i < n && IsDigit(text[i])) i++;
            if (i + 1 < n && text[i] == '.' && IsDigit(text[i + 1]))
            {
                i++;
                while (i < n && IsDigit(text[i])) i++;
            }
            if (i < n && (text[i] == 'e' || text[i] == 'E'))
            {
                int j = i + 1;
                if (j < n && (text[j] == '+' || text[j] == '-')) j++;
                if (j < n && IsDigit(text[j]))
                {
                    i = j;
                    while (i < n && IsDigit(text[i])) i++;
                }
            }
            if (i < n && IsIdentStart(text[i]))
                throw Error($"invalid number '{text.Substring(start, i - start + 1)}'", start, i + 1);
            value = double.Parse(text.Substring(start, i - start), NumberStyles.Float, CultureInfo.InvariantCulture);
            if (double.IsInfinity(value)) throw Error($"number '{text.Substring(start, i - start)}' is out of range", start, i);
            return i;
        }

        private static int ReadString(string text, int i, out string value)
        {
            char quote = text[i];
            int start = i++;
            var sb = new StringBuilder();
            int n = text.Length;
            while (true)
            {
                if (i >= n) throw Error("unterminated string literal", start, n);
                char c = text[i];
                if (c == quote) { i++; break; }
                if (c == '\\')
                {
                    if (i + 1 >= n) throw Error("unterminated escape", i, n);
                    char e = text[i + 1];
                    switch (e)
                    {
                        case '"': sb.Append('"'); break;
                        case '\'': sb.Append('\''); break;
                        case '\\': sb.Append('\\'); break;
                        case 'n': sb.Append('\n'); break;
                        case 't': sb.Append('\t'); break;
                        case 'r': sb.Append('\r'); break;
                        case 'u':
                            if (i + 6 > n || !int.TryParse(text.Substring(i + 2, 4), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var code))
                                throw Error("invalid \\u escape", i, System.Math.Min(n, i + 6));
                            sb.Append((char)code);
                            i += 4;
                            break;
                        default:
                            // Unknown escapes are kept verbatim so regex patterns ("\[", "\.") survive.
                            sb.Append('\\').Append(e);
                            break;
                    }
                    i += 2;
                    continue;
                }
                sb.Append(c);
                i++;
            }
            value = sb.ToString();
            return i;
        }
    }
}
