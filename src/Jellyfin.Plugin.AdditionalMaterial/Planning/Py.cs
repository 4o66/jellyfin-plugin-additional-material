using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;

namespace Jellyfin.Plugin.AdditionalMaterial.Planning;

/// <summary>
/// The few Python behaviors the planner relies on, reproduced exactly so the plugin and
/// tools/make_additional_material.py make the same decisions: pathlib's suffix and stem,
/// fnmatch globs, str.splitlines and str.strip, and byte regexes (run here on Latin-1 text,
/// which maps every byte to one character).
/// </summary>
public static class Py
{
    private static readonly Encoding _latin1 = Encoding.Latin1;

    /// <summary>pathlib's <c>suffix</c>: the last <c>.ext</c> of a name, or empty (".bashrc" and "name." have none).</summary>
    /// <param name="name">A file name, or a path (only its last part counts).</param>
    /// <returns>The suffix, with its dot.</returns>
    public static string Suffix(string name)
    {
        var leaf = Leaf(name);
        var dot = leaf.LastIndexOf('.');
        return dot <= 0 || dot == leaf.Length - 1 ? string.Empty : leaf[dot..];
    }

    /// <summary>pathlib's <c>stem</c>: the name without its suffix.</summary>
    /// <param name="name">A file name.</param>
    /// <returns>The stem.</returns>
    public static string Stem(string name)
    {
        var leaf = Leaf(name);
        var suffix = Suffix(leaf);
        return suffix.Length == 0 ? leaf : leaf[..^suffix.Length];
    }

    /// <summary>The last part of a <c>/</c> separated path.</summary>
    /// <param name="path">The path.</param>
    /// <returns>Its last part.</returns>
    public static string Leaf(string path)
    {
        var slash = path.LastIndexOf('/');
        return slash < 0 ? path : path[(slash + 1)..];
    }

    /// <summary>Python's <c>fnmatch.fnmatchcase</c> (case-sensitive; callers lower-case both sides for a case-insensitive match).</summary>
    /// <param name="name">The name.</param>
    /// <param name="pattern">The glob.</param>
    /// <returns>Whether it matches.</returns>
    public static bool FnMatch(string name, string pattern) => GlobRegex(pattern).IsMatch(name);

    private static readonly Dictionary<string, Regex> _globs = new(StringComparer.Ordinal);

    private static Regex GlobRegex(string pattern)
    {
        lock (_globs)
        {
            if (!_globs.TryGetValue(pattern, out var regex))
            {
                regex = new Regex(@"\A(?s:" + TranslateGlob(pattern) + @")\z", RegexOptions.CultureInvariant);
                _globs[pattern] = regex;
            }

            return regex;
        }
    }

    /// <summary>fnmatch.translate: <c>*</c>, <c>?</c>, <c>[seq]</c>, <c>[!seq]</c>; an unclosed <c>[</c> is literal.</summary>
    /// <param name="pattern">The glob.</param>
    /// <returns>The regular expression body.</returns>
    public static string TranslateGlob(string pattern)
    {
        var res = new StringBuilder();
        int i = 0, n = pattern.Length;
        while (i < n)
        {
            var c = pattern[i++];
            if (c == '*')
            {
                if (res.Length < 2 || res[^2] != '.' || res[^1] != '*')
                {
                    res.Append(".*");
                }
            }
            else if (c == '?')
            {
                res.Append('.');
            }
            else if (c == '[')
            {
                var j = i;
                if (j < n && pattern[j] == '!')
                {
                    j++;
                }

                if (j < n && pattern[j] == ']')
                {
                    j++;
                }

                while (j < n && pattern[j] != ']')
                {
                    j++;
                }

                if (j >= n)
                {
                    res.Append(@"\[");
                    continue;
                }

                var stuff = pattern[i..j];
                i = j + 1;
                if (stuff.Length == 0)
                {
                    res.Append("(?!)");
                }
                else if (stuff == "!")
                {
                    res.Append('.');
                }
                else
                {
                    res.Append('[');
                    var start = 0;
                    if (stuff[0] == '!')
                    {
                        res.Append('^');
                        start = 1;
                    }

                    for (var k = start; k < stuff.Length; k++)
                    {
                        var ch = stuff[k];
                        res.Append(ch is '\\' or ']' or '[' or '^' ? "\\" + ch : ch.ToString());
                    }

                    res.Append(']');
                }
            }
            else
            {
                res.Append(Regex.Escape(c.ToString()));
            }
        }

        return res.ToString();
    }

    /// <summary>Python's <c>str.splitlines()</c>.</summary>
    /// <param name="text">The text.</param>
    /// <returns>The lines, without their line breaks.</returns>
    public static List<string> SplitLines(string text)
    {
        var lines = new List<string>();
        var start = 0;
        for (var i = 0; i < text.Length; i++)
        {
            var c = text[i];
            if (c is '\n' or '\r' or '\v' or '\f' or '\x1c' or '\x1d' or '\x1e' or '\x85' or '\u2028' or '\u2029')
            {
                lines.Add(text[start..i]);
                if (c == '\r' && i + 1 < text.Length && text[i + 1] == '\n')
                {
                    i++;
                }

                start = i + 1;
            }
        }

        if (start < text.Length)
        {
            lines.Add(text[start..]);
        }

        return lines;
    }

    /// <summary>Python's <c>str.isspace()</c> for one character.</summary>
    /// <param name="c">The character.</param>
    /// <returns>Whether Python counts it as whitespace.</returns>
    public static bool IsSpace(char c) => char.IsWhiteSpace(c) || c is >= '\x1c' and <= '\x1f';

    /// <summary>Python's <c>str.strip()</c>.</summary>
    /// <param name="text">The text.</param>
    /// <returns>The text without leading and trailing whitespace.</returns>
    public static string Strip(string text)
    {
        int a = 0, b = text.Length;
        while (a < b && IsSpace(text[a]))
        {
            a++;
        }

        while (b > a && IsSpace(text[b - 1]))
        {
            b--;
        }

        return text[a..b];
    }

    /// <summary>Length in code points, as Python's <c>len()</c> counts a str.</summary>
    /// <param name="text">The text.</param>
    /// <returns>The number of code points.</returns>
    public static int CodePoints(string text) => text.EnumerateRunes().Count();

    /// <summary>Bytes as Latin-1 text: one character per byte, so a regex over it behaves like a bytes regex.</summary>
    /// <param name="data">The bytes.</param>
    /// <returns>The text.</returns>
    public static string Bytes(ReadOnlySpan<byte> data) => _latin1.GetString(data);

    /// <summary>A regular expression compiled the way the rules and the script compile it.</summary>
    /// <param name="pattern">The pattern.</param>
    /// <param name="ignoreCase">Whether to ignore case (<c>re.I</c>).</param>
    /// <returns>The regex.</returns>
    /// <param name="timeout">How long one match may take (rule patterns are bounded; internal ones scan large files).</param>
    public static Regex Compile(string pattern, bool ignoreCase, TimeSpan? timeout = null)
        => new(pattern, (ignoreCase ? RegexOptions.IgnoreCase : RegexOptions.None) | RegexOptions.CultureInvariant, timeout ?? Regex.InfiniteMatchTimeout);

    /// <summary>Python's <c>str.lower()</c> for the comparisons the script makes.</summary>
    /// <param name="text">The text.</param>
    /// <returns>Lower-cased text.</returns>
    public static string Lower(string text) => text.ToLower(CultureInfo.InvariantCulture);

    /// <summary>Python's default string ordering: by code point.</summary>
    public static readonly IComparer<string> Order = StringComparer.Ordinal;
}
