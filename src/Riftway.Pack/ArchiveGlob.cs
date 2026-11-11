using System.Text;
using System.Text.RegularExpressions;

namespace Riftway.Pack;

/// <summary>An <c>--unpack</c> or <c>--unpack-dir</c> pattern, matched as <c>@electron/asar</c> matches it: minimatch's
/// <c>*</c>, <c>?</c>, <c>**</c>, <c>{a,b}</c> and <c>[abc]</c> against <c>/</c>-separated paths from the source.</summary>
internal sealed class ArchiveGlob
{
    private readonly string _pattern;
    private readonly bool _baseName;
    private readonly Regex _regex;

    public ArchiveGlob(string pattern)
    {
        _pattern = pattern.Replace('\\', '/').Trim('/');
        _baseName = !_pattern.Contains('/');
        _regex = new Regex("^" + Translate(_pattern) + "$", RegexOptions.CultureInvariant);
    }

    /// <summary><c>--unpack</c>: minimatch with <c>matchBase</c>, so a pattern without a slash matches the file's name in
    /// any directory.</summary>
    public bool MatchesFile(string path) => _regex.IsMatch(_baseName ? path[(path.LastIndexOf('/') + 1)..] : path);

    /// <summary><c>--unpack-dir</c>: <c>@electron/asar</c>'s <c>isUnpackedDir</c>, a directory whose path starts with the
    /// pattern or matches it (everything under it follows it out).</summary>
    public bool MatchesDirectory(string path) => path.StartsWith(_pattern, StringComparison.Ordinal) || _regex.IsMatch(path);

    private static string Translate(string glob)
    {
        var regex = new StringBuilder();
        var braces = 0;
        for (var i = 0; i < glob.Length; i++)
        {
            var c = glob[i];
            switch (c)
            {
                case '*' when i + 1 < glob.Length && glob[i + 1] == '*':
                    i++;
                    if (i + 1 < glob.Length && glob[i + 1] == '/')
                    {
                        i++;
                        regex.Append("(?:.*/)?");
                    }
                    else regex.Append(".*");
                    break;
                case '*': regex.Append("[^/]*"); break;
                case '?': regex.Append("[^/]"); break;
                case '{': braces++; regex.Append("(?:"); break;
                case '}' when braces > 0: braces--; regex.Append(')'); break;
                case ',' when braces > 0: regex.Append('|'); break;
                case '[' when glob.IndexOf(']', i + 1) is var end and > 0:
                    var set = glob[(i + 1)..end];
                    regex.Append('[').Append(set.StartsWith('!') ? "^" + set[1..] : set).Append(']');
                    i = end;
                    break;
                default: regex.Append(Regex.Escape(c.ToString())); break;
            }
        }
        return regex.ToString();
    }
}
