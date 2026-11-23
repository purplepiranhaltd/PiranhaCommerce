using System.Text;

namespace PiranhaCommerce.Catalog;

/// <summary>A small RFC 4180 reader and writer: quoted cells, doubled quotes, line breaks inside quotes, a byte order mark, and comma, semicolon or tab as the separator.</summary>
internal static class Csv
{
    /// <summary>Rows of cells, with empty lines skipped. Throws <see cref="FormatException"/> for a quote that is never closed.</summary>
    public static List<List<string>> Parse(string text)
    {
        if (text.Length > 0 && text[0] == '﻿') text = text[1..];
        var delimiter = DetectDelimiter(text);
        var rows = new List<List<string>>();
        var row = new List<string>();
        var cell = new StringBuilder();
        var quoted = false;
        var started = false; // anything seen on this line, so a blank line is skipped but "" is not

        void EndCell() { row.Add(cell.ToString()); cell.Clear(); }
        void EndRow()
        {
            if (started || row.Count > 0) { EndCell(); rows.Add(row); }
            row = [];
            started = false;
        }

        for (var i = 0; i < text.Length; i++)
        {
            var c = text[i];
            if (quoted)
            {
                if (c == '"')
                {
                    if (i + 1 < text.Length && text[i + 1] == '"') { cell.Append('"'); i++; }
                    else quoted = false;
                }
                else cell.Append(c);
                continue;
            }
            if (c == '"' && cell.Length == 0) { quoted = true; started = true; }
            else if (c == delimiter) { EndCell(); started = true; }
            else if (c == '\r') { if (i + 1 < text.Length && text[i + 1] == '\n') i++; EndRow(); }
            else if (c == '\n') EndRow();
            else { cell.Append(c); started = true; }
        }
        if (quoted) throw new FormatException("A quoted cell is never closed.");
        EndRow();
        return rows;
    }

    private static char DetectDelimiter(string text)
    {
        // Looks at the first line only, outside quotes. Excel in many locales saves with semicolons.
        int comma = 0, semi = 0, tab = 0;
        var quoted = false;
        foreach (var c in text)
        {
            if (c == '"') quoted = !quoted;
            else if (!quoted && (c == '\n' || c == '\r')) break;
            else if (!quoted) { if (c == ',') comma++; else if (c == ';') semi++; else if (c == '\t') tab++; }
        }
        return semi > comma && semi >= tab ? ';' : tab > comma ? '\t' : ',';
    }

    public static void WriteRow(TextWriter w, IEnumerable<string?> cells)
    {
        var first = true;
        foreach (var cell in cells)
        {
            if (!first) w.Write(',');
            first = false;
            var v = cell ?? "";
            if (v.AsSpan().IndexOfAny(",\"\r\n") >= 0 || v.StartsWith(' ') || v.EndsWith(' ')) w.Write('"' + v.Replace("\"", "\"\"") + '"');
            else w.Write(v);
        }
        w.Write("\r\n");
    }

    /// <summary>
    /// For a free-text cell on export: a spreadsheet runs a cell that starts with = + - or @ as a formula, so such text is written with a leading
    /// apostrophe (and <see cref="Unprotect"/> takes it off on import, so a round trip is exact).
    /// </summary>
    public static string? Protect(string? text) => text is { Length: > 0 } && "=+-@\t\r".Contains(text[0]) ? "'" + text : text;

    public static string Unprotect(string text) => text.Length > 1 && text[0] == '\'' && "=+-@\t\r".Contains(text[1]) ? text[1..] : text;
}
