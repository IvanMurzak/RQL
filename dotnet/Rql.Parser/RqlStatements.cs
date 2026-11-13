using System;
using System.Collections.Generic;

namespace Rql
{
    /// <summary>One statement of a request text (spec §14): its trimmed text and where its segment lies in the request.</summary>
    public readonly struct RqlStatement
    {
        public RqlStatement(string text, TextSpan span) { Text = text; Span = span; }

        /// <summary>The statement, trimmed. Diagnostic positions of a statement run on its own are relative to this text.</summary>
        public string Text { get; }

        /// <summary>The untrimmed segment between separators in the request text.</summary>
        public TextSpan Span { get; }
    }

    public sealed partial class RqlParser
    {
        /// <summary>Most statements one request may hold (spec §14).</summary>
        public const int MaxStatements = 10;

        /// <summary>
        /// Splits a request text into statements at each <c>;</c> outside string literals, quoted identifiers and brackets
        /// (spec §14). Blank statements (a trailing <c>;</c>, <c>;;</c>) are dropped. A text without a separator is one
        /// statement spanning the whole text, unchanged; a text with nothing but separators and blanks is its first
        /// (blank) segment, so parsing it reports the empty query. Never throws for malformed text: an unterminated
        /// string or quoted identifier runs to the end of the text, and the parser reports it.
        /// </summary>
        public static IReadOnlyList<RqlStatement> SplitStatements(string text)
        {
            if (text == null) throw new ArgumentNullException(nameof(text));
            var segments = new List<RqlStatement>();
            int depth = 0, start = 0;
            for (int i = 0; i < text.Length; i++)
            {
                switch (text[i])
                {
                    case '"':
                    case '\'':
                        i = ClosingQuote(text, i, escapes: true);
                        break;
                    case '`':
                        i = ClosingQuote(text, i, escapes: false);
                        break;
                    case '(':
                    case '[':
                    case '{':
                        depth++;
                        break;
                    case ')':
                    case ']':
                    case '}':
                        if (depth > 0) depth--;
                        break;
                    case ';' when depth == 0:
                        segments.Add(SegmentOf(text, start, i));
                        start = i + 1;
                        break;
                }
            }
            segments.Add(SegmentOf(text, start, text.Length));
            if (segments.Count == 1) return segments;
            var statements = segments.FindAll(s => s.Text.Length > 0);
            return statements.Count > 0 ? statements : segments.GetRange(0, 1);
        }

        private static RqlStatement SegmentOf(string text, int start, int end) =>
            new RqlStatement(text.Substring(start, end - start).Trim(), new TextSpan(start, end));

        // The index of the quote closing the one at `open`, or the last index when it is unterminated.
        private static int ClosingQuote(string text, int open, bool escapes)
        {
            char quote = text[open];
            for (int i = open + 1; i < text.Length; i++)
            {
                if (escapes && text[i] == '\\') i++;
                else if (text[i] == quote) return i;
            }
            return text.Length - 1;
        }
    }
}
