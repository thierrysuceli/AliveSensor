using System;
using System.Collections.Generic;
using System.Text;

namespace AliveSensor.Core;

/// <summary>
/// The tiny template language the event catalog writes its phrases in. Deliberately small: a sensor author
/// should be able to learn it from one example, and it can never run code or fail at render time.
///
/// <list type="bullet">
/// <item><c>{token}</c> — the token's value, or nothing when it has none.</item>
/// <item><c>{token?one|many}</c> — picks a form by the token's number (missing counts as 1); <c>#</c> becomes
/// the number. An empty chosen form counts as "no value", which lets <c>[ {p:turns?|for a while}]</c> vanish.</item>
/// <item><c>[ ... ]</c> — an optional part: dropped entirely unless every token directly inside it has a value.</item>
/// <item><c>[ ... | ... ]</c> — the same, with a fallback used when the first part is dropped. Parts nest.</item>
/// <item><c>{{</c>, <c>}}</c>, <c>[[</c>, <c>]]</c> — literal braces and brackets.</item>
/// </list>
/// Templates are parsed once when the catalog loads, then rendered many times (including on AliveNpcs'
/// background prompt thread), so instances are immutable and rendering allocates nothing shared.
/// </summary>
internal sealed class PhraseTemplate
{
    private readonly IReadOnlyList<Node> _nodes;
    private readonly string _source;

    private PhraseTemplate(IReadOnlyList<Node> nodes, string source)
    {
        _nodes = nodes;
        _source = source;
    }

    /// <summary>The template text this was parsed from (used in error messages).</summary>
    public override string ToString() => _source;

    /// <summary>Parse a template. Malformed markup degrades to literal text rather than throwing.</summary>
    public static PhraseTemplate Parse(string text)
    {
        int index = 0;
        var nodes = ParseSequence(text ?? "", ref index, insideGroup: false);
        return new PhraseTemplate(nodes, text ?? "");
    }

    /// <summary>
    /// Render with a resolver that returns a token's value, or null/empty when it has none.
    /// Unknown tokens simply have no value, so a template that mentions one still renders.
    /// </summary>
    public string Render(Func<string, string?> resolve)
    {
        var text = new StringBuilder();
        RenderSequence(_nodes, resolve, text, out _);
        return Tidy(text.ToString());
    }

    // ── parsing ──

    private static List<Node> ParseSequence(string text, ref int index, bool insideGroup)
    {
        var nodes = new List<Node>();
        var literal = new StringBuilder();

        void FlushLiteral()
        {
            if (literal.Length == 0)
                return;
            nodes.Add(new LiteralNode(literal.ToString()));
            literal.Clear();
        }

        while (index < text.Length)
        {
            char current = text[index];

            // The caller (a group) consumes these.
            if (insideGroup && (current == ']' || current == '|'))
                break;

            if (current == '{' && Next(text, index) == '{') { literal.Append('{'); index += 2; continue; }
            if (current == '}' && Next(text, index) == '}') { literal.Append('}'); index += 2; continue; }
            if (current == '[' && Next(text, index) == '[') { literal.Append('['); index += 2; continue; }
            if (current == ']' && Next(text, index) == ']') { literal.Append(']'); index += 2; continue; }

            if (current == '{')
            {
                int close = text.IndexOf('}', index + 1);
                if (close < 0)
                {
                    literal.Append(current); // unterminated: keep as text
                    index++;
                    continue;
                }
                FlushLiteral();
                nodes.Add(TokenNode.Parse(text.Substring(index + 1, close - index - 1)));
                index = close + 1;
                continue;
            }

            if (current == '[')
            {
                FlushLiteral();
                index++; // consume '['
                var body = ParseSequence(text, ref index, insideGroup: true);
                List<Node>? fallback = null;
                if (index < text.Length && text[index] == '|')
                {
                    index++; // consume '|'
                    fallback = ParseSequence(text, ref index, insideGroup: true);
                }
                if (index < text.Length && text[index] == ']')
                    index++; // consume ']'
                nodes.Add(new GroupNode(body, fallback));
                continue;
            }

            literal.Append(current);
            index++;
        }

        FlushLiteral();
        return nodes;
    }

    private static char Next(string text, int index) => index + 1 < text.Length ? text[index + 1] : '\0';

    // ── rendering ──

    private static void RenderSequence(IReadOnlyList<Node> nodes, Func<string, string?> resolve, StringBuilder text, out bool missingValue)
    {
        missingValue = false;
        foreach (Node node in nodes)
        {
            switch (node)
            {
                case LiteralNode literal:
                    text.Append(literal.Text);
                    break;

                case TokenNode token:
                    text.Append(token.Render(resolve, out bool empty));
                    missingValue |= empty;
                    break;

                case GroupNode group:
                {
                    var body = new StringBuilder();
                    RenderSequence(group.Body, resolve, body, out bool bodyMissing);
                    if (!bodyMissing)
                    {
                        text.Append(body);
                        break;
                    }
                    if (group.Fallback is null)
                        break;
                    var fallback = new StringBuilder();
                    RenderSequence(group.Fallback, resolve, fallback, out _);
                    text.Append(fallback);
                    break;
                }
            }
        }
    }

    /// <summary>Tidy up the seams a dropped token can leave behind, so templates stay forgiving to write.</summary>
    private static string Tidy(string text)
    {
        var tidy = new StringBuilder(text.Length);
        foreach (char current in text)
        {
            bool doubleSpace = current == ' ' && tidy.Length > 0 && tidy[tidy.Length - 1] == ' ';
            if (doubleSpace)
                continue;
            bool spaceBeforePunctuation = (current is ',' or '.' or ';' or ':' or ')' or '!' or '?')
                && tidy.Length > 0 && tidy[tidy.Length - 1] == ' ';
            if (spaceBeforePunctuation)
                tidy.Length--;
            tidy.Append(current);
        }
        return tidy.ToString().Trim();
    }

    // ── nodes ──

    private abstract record Node;

    private sealed record LiteralNode(string Text) : Node;

    private sealed record GroupNode(IReadOnlyList<Node> Body, IReadOnlyList<Node>? Fallback) : Node;

    private sealed record TokenNode(string Name, string? Singular, string? Plural) : Node
    {
        /// <summary>"name", or "name?one|many". The name keeps its own colons, so "p:item" stays one token.</summary>
        public static TokenNode Parse(string inner)
        {
            int mark = inner.IndexOf('?');
            if (mark >= 0)
            {
                string forms = inner.Substring(mark + 1);
                int pipe = forms.IndexOf('|');
                if (pipe >= 0)
                    return new TokenNode(inner.Substring(0, mark).Trim(), forms.Substring(0, pipe), forms.Substring(pipe + 1));
            }
            return new TokenNode(inner.Trim(), null, null);
        }

        public string Render(Func<string, string?> resolve, out bool missingValue)
        {
            string value = resolve(Name) ?? "";
            if (Singular is null)
            {
                missingValue = value.Length == 0;
                return value;
            }

            int number = int.TryParse(value, out int parsed) ? parsed : 1;
            string form = number == 1 ? Singular : Plural ?? Singular;
            missingValue = form.Length == 0;
            return form.Replace("#", number.ToString());
        }
    }
}
