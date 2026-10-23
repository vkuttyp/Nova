namespace Nova.Compiler.Syntax;

/// <summary>A half-open range [Start, Start + Length) of UTF-16 code units in a source text.</summary>
public readonly record struct TextSpan(int Start, int Length)
{
    public int End => Start + Length;

    public static TextSpan FromBounds(int start, int end) => new(start, end - start);

    public override string ToString() => $"[{Start}..{End})";
}

/// <summary>A 1-based line/column position. Columns count UTF-16 code units (grammar.md §1.1).</summary>
public readonly record struct LinePosition(int Line, int Column)
{
    public override string ToString() => $"{Line}:{Column}";
}

/// <summary>Immutable source file contents with a line index.</summary>
public sealed class SourceText
{
    private readonly int[] _lineStarts;

    public SourceText(string path, string text)
    {
        ArgumentNullException.ThrowIfNull(path);
        ArgumentNullException.ThrowIfNull(text);
        Path = path;
        // A leading BOM is not part of the program (grammar.md §1.1).
        Text = text.Length > 0 && text[0] == '﻿' ? text[1..] : text;
        _lineStarts = ComputeLineStarts(Text);
    }

    /// <summary>The path used in diagnostics, exactly as supplied by the caller.</summary>
    public string Path { get; }

    /// <summary>
    /// The source path for a file named on the command line (c-backend.md §7): <c>/</c> separators; an
    /// absolute path under <paramref name="currentDirectory"/> becomes relative to it, and any other
    /// absolute path is reduced to its file name, so generated code never contains a host path.
    /// </summary>
    public static string SourcePath(string path, string currentDirectory)
    {
        ArgumentNullException.ThrowIfNull(path);
        ArgumentNullException.ThrowIfNull(currentDirectory);
        if (System.IO.Path.IsPathRooted(path))
        {
            var relative = System.IO.Path.GetRelativePath(currentDirectory, path);
            path = relative == ".." || relative.StartsWith(".." + System.IO.Path.DirectorySeparatorChar, StringComparison.Ordinal)
                || System.IO.Path.IsPathRooted(relative)
                ? System.IO.Path.GetFileName(path)
                : relative;
        }
        return path.Replace('\\', '/');
    }

    public string Text { get; }

    public int Length => Text.Length;

    public char this[int index] => Text[index];

    public int LineCount => _lineStarts.Length;

    public LinePosition GetLinePosition(int position)
    {
        var line = Array.BinarySearch(_lineStarts, position);
        if (line < 0)
        {
            line = ~line - 1;
        }
        return new LinePosition(line + 1, position - _lineStarts[line] + 1);
    }

    /// <summary>Returns the text of a 1-based line without its terminator.</summary>
    public string GetLineText(int line)
    {
        var start = _lineStarts[line - 1];
        var end = line < _lineStarts.Length ? _lineStarts[line] : Text.Length;
        while (end > start && (Text[end - 1] == '\n' || Text[end - 1] == '\r'))
        {
            end--;
        }
        return Text[start..end];
    }

    public string GetText(TextSpan span) => Text.Substring(span.Start, span.Length);

    private static int[] ComputeLineStarts(string text)
    {
        var starts = new List<int> { 0 };
        for (var i = 0; i < text.Length; i++)
        {
            var c = text[i];
            if (c == '\r')
            {
                if (i + 1 < text.Length && text[i + 1] == '\n')
                {
                    i++;
                }
                starts.Add(i + 1);
            }
            else if (c == '\n')
            {
                starts.Add(i + 1);
            }
        }
        return [.. starts];
    }
}
