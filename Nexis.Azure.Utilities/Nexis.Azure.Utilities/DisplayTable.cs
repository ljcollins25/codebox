using System.Text;
using System.Text.Json;
using DotNext.Collections.Generic;

namespace Nexis.Azure.Utilities;

/// <summary>
/// Displays a table with aligned text
/// </summary>
internal sealed class DisplayTable
{
    private int[] m_maxColumnLengths = new int[0];
    private Dictionary<string, int> columns = new();
    private readonly List<IEnumerable<string>> m_rows = new();
    private readonly string m_columnDelimeter = " | ";

    public DisplayTable(string columnDelimeter = " | ", bool defaultHeader = true)
    {
        m_columnDelimeter = columnDelimeter;
        m_rows.Add(Enumerable.Range(0, 1).SelectMany(i => columns.Keys));
    }

    public int AddColumn(string columnName)
    {
        int index = columns.GetOrAdd(columnName, columns.Count);

        Array.Resize(ref m_maxColumnLengths, columns.Count);
        ref var max = ref m_maxColumnLengths[index];
        max = Math.Max(max, columnName.Length);
        return index;
    }

    public void Add<T0>(T0 v0)
    {
        Add(v0, default(string));
    }

    public void Add<T0, T1>(T0 v0, T1 v1)
    {
        Add(v0, v1, default(string));
    }

    public void Add<T0, T1, T2>(T0 v0, T1 v1, T2 v2)
    {
        var cells = new string[columns.Count];
        AddCore(ref cells, v0);
        AddCore(ref cells, v1);
        AddCore(ref cells, v2);
        m_rows.Add(cells);
    }

    public void AddCore<T>(ref string[] cells, T value)
    {
        if (value == null) return;

        var element = JsonSerializer.SerializeToElement(value);
        foreach (var property in element.EnumerateObject())
        {
            var valueKind = property.Value.ValueKind;
            if (valueKind == JsonValueKind.Array || valueKind == JsonValueKind.Object)
            {
                continue;
            }

            int index = AddColumn(property.Name);
            Array.Resize(ref cells, columns.Count);
            var cellValue = property.Value.ToString() ?? "";
            ref var max = ref m_maxColumnLengths[index];
            max = Math.Max(max, cellValue.Length);
            cells[index] = cellValue;
        }
    }

    public void Write(Action<Memory<char>> writeLine)
    {
        StringBuilder sb = new StringBuilder();

        IEnumerable<(string value, int index)> getCells(IEnumerable<string> cells)
        {
            int i = 0;
            foreach (var cell in cells)
            {
                yield return (cell, i);
                i++;
            }

            for (; i < columns.Count; i++)
            {
                yield return ("", i);
            }
        }

        var buffer = new char[m_maxColumnLengths.Sum() + (m_columnDelimeter.Length * Math.Max(0, (m_maxColumnLengths.Length - 1)))];
        foreach (var row in m_rows)
        {
            sb.Clear();

            foreach (var cell in getCells(row))
            {
                var value = cell.value ?? string.Empty;
                sb.Append(' ', m_maxColumnLengths[cell.index] - value.Length);
                sb.Append(value);
                if (cell.index != (columns.Count - 1))
                {
                    sb.Append(m_columnDelimeter);
                }
            }

            sb.CopyTo(0, buffer, 0, buffer.Length);
            writeLine(buffer.AsMemory(0, sb.Length));
        }
    }
}
