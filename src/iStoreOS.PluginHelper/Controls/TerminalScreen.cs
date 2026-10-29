using System;
using System.Collections.Generic;
using System.Text;

namespace iStoreOS.PluginHelper.Controls
{
    // Small streaming VT subset, independent of menu content. SGR is safely ignored.
    // This is a text terminal for interactive shell menus, not a full-screen TUI emulator.
    public sealed class TerminalScreen
    {
        public const int MaximumLines = 4000;
        private const int MaximumColumns = 4096;
        private readonly List<StringBuilder> lines = new List<StringBuilder> { new StringBuilder() };
        private readonly StringBuilder sequence = new StringBuilder();
        private int row, column, state, savedRow, savedColumn;
        public long TrimmedCharacters { get; private set; }
        public long TrimmedLines { get; private set; }
        public int ViewRows { get; set; }
        public TerminalScreen() { ViewRows = 24; }
        public string Text
        {
            get
            {
                var result = new StringBuilder();
                for (int i = 0; i < lines.Count; i++) { if (i > 0) result.Append('\n'); result.Append(lines[i]); }
                return result.ToString();
            }
        }
        public int CursorOffset
        {
            get
            {
                int offset = Math.Min(column, lines[row].Length);
                for (int i = 0; i < row; i++) offset += lines[i].Length + 1;
                return offset;
            }
        }
        public void Clear() { lines.Clear(); lines.Add(new StringBuilder()); row = column = state = 0; TrimmedCharacters = TrimmedLines = 0; sequence.Clear(); }
        public void Feed(string text)
        {
            foreach (char c in text)
            {
                if (state == 3) { if (c == '\a') state = 0; else if (c == '\u001b') state = 4; continue; }
                if (state == 4) { state = c == '\\' ? 0 : 3; continue; }
                if (state == 1)
                {
                    if (c == '[') { state = 2; sequence.Clear(); }
                    else if (c == ']') state = 3;
                    else { state = 0; if (c == '7') Save(); if (c == '8') Restore(); }
                    continue;
                }
                if (state == 2)
                {
                    if (c >= '@' && c <= '~') { Apply(c, sequence.ToString()); state = 0; }
                    else if (sequence.Length < 64) sequence.Append(c);
                    // Keep consuming oversized control sequences until their terminator.
                    continue;
                }
                if (c == '\u001b') { state = 1; continue; }
                if (c == '\r') { column = 0; continue; }
                if (c == '\n') { row++; column = 0; EnsureRow(); continue; }
                if (c == '\b') { column = Math.Max(0, column - 1); continue; }
                if (c == '\t') { column = (column / 8 + 1) * 8; continue; }
                if (char.IsControl(c)) continue;
                var line = lines[row];
                while (line.Length < column) line.Append(' ');
                if (column < line.Length) line[column] = c; else line.Append(c);
                column++;
            }
        }
        private void EnsureRow()
        {
            row = Math.Max(0, row);
            while (lines.Count <= row) lines.Add(new StringBuilder());
            if (lines.Count > MaximumLines)
            {
                int removed = lines.Count - MaximumLines;
                for (int i = 0; i < removed; i++) TrimmedCharacters += lines[i].Length + 1;
                TrimmedLines += removed;
                lines.RemoveRange(0, removed); row -= removed; savedRow = Math.Max(0, savedRow - removed);
            }
        }
        private void Save() { savedRow = row; savedColumn = column; }
        private void Restore() { row = Math.Min(savedRow, lines.Count - 1); column = savedColumn; }
        private void Apply(char command, string parameters)
        {
            if (parameters.StartsWith("?", StringComparison.Ordinal) || parameters.StartsWith(">", StringComparison.Ordinal)) return;
            string[] parts = parameters.Split(';');
            int first, second;
            int.TryParse(parts[0], out first);
            second = 1; if (parts.Length > 1) int.TryParse(parts[1], out second);
            int amount = Math.Max(1, Math.Min(first, MaximumColumns));
            switch (command)
            {
                case 'A': row = Math.Max(0, row - amount); break;
                case 'B': row = Math.Min(MaximumLines - 1, row + amount); EnsureRow(); break;
                case 'C': column += amount; break;
                case 'D': column = Math.Max(0, column - amount); break;
                case 'G': column = amount - 1; break;
                case 'H': case 'f':
                    row = Math.Max(0, lines.Count - Math.Max(1, ViewRows)) + Math.Min(Math.Max(1, first), Math.Max(1, ViewRows)) - 1;
                    column = Math.Min(MaximumColumns, Math.Max(1, second)) - 1; EnsureRow(); break;
                case 'K':
                    if (first == 2) lines[row].Clear();
                    else if (first == 1) { for (int i = 0; i < Math.Min(column + 1, lines[row].Length); i++) lines[row][i] = ' '; }
                    else if (column < lines[row].Length) lines[row].Length = column;
                    break;
                case 'J':
                    if (first == 2 || first == 3) { lines.Clear(); lines.Add(new StringBuilder()); row = column = 0; }
                    else if (first == 0)
                    {
                        if (column < lines[row].Length) lines[row].Length = column;
                        if (row + 1 < lines.Count) lines.RemoveRange(row + 1, lines.Count - row - 1);
                    }
                    break;
                case 's': Save(); break;
                case 'u': Restore(); break;
            }
        }
    }
}

