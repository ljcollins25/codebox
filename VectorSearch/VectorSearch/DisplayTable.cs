// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.
// Adapted from https://github.com/microsoft/buildxl/blob/196242c02a998750002de4c9e9b19b83756ac9c5/Public/Src/Tools/Execution.Analyzer/DisplayTable.cs

using System.Text;

namespace VectorSearch;

/// <summary>
/// Displays a table with aligned text columns.
/// Uses integer column indices with configurable headers.
/// </summary>
public sealed class DisplayTable
{
    private readonly int _columnCount;
    private readonly int[] _maxColumnLengths;
    private readonly List<string?[]> _rows = new();
    private string?[] _currentRow;
    private readonly string _columnDelimiter;
    private string?[] _headers;
    private bool _headerWritten;

    /// <summary>
    /// Creates a new DisplayTable with the specified number of columns.
    /// </summary>
    /// <param name="columnCount">Number of columns.</param>
    /// <param name="columnDelimiter">String to insert between columns (e.g., " | " or "  ").</param>
    public DisplayTable(int columnCount, string columnDelimiter = "  ")
    {
        _columnCount = columnCount;
        _maxColumnLengths = new int[_columnCount];
        _columnDelimiter = columnDelimiter;
        _currentRow = new string?[_columnCount];
        _headers = new string?[_columnCount];
    }

    /// <summary>
    /// Gets the number of columns in the table.
    /// </summary>
    public int ColumnCount => _columnCount;

    /// <summary>
    /// Gets the number of rows in the table (excluding header).
    /// </summary>
    public int RowCount => _rows.Count;

    /// <summary>
    /// Sets the header for a column.
    /// </summary>
    /// <param name="column">The column index (0-based).</param>
    /// <param name="header">The header text.</param>
    public void SetHeader(int column, string header)
    {
        if (column < 0 || column >= _columnCount)
        {
            return;
        }

        _headers[column] = header;
        _maxColumnLengths[column] = Math.Max(_maxColumnLengths[column], header.Length);
    }

    /// <summary>
    /// Starts a new row. Must be called before setting values for each row.
    /// </summary>
    public void NextRow()
    {
        _currentRow = new string?[_columnCount];
        _rows.Add(_currentRow);
    }

    /// <summary>
    /// Sets the value for a column in the current row.
    /// </summary>
    /// <param name="column">The column index (0-based).</param>
    /// <param name="value">The value to display (will be converted to string).</param>
    public void Set(int column, object? value)
    {
        if (value == null || column < 0 || column >= _columnCount)
        {
            return;
        }

        var stringValue = value.ToString() ?? string.Empty;
        _maxColumnLengths[column] = Math.Max(_maxColumnLengths[column], stringValue.Length);
        _currentRow[column] = stringValue;
    }

    /// <summary>
    /// Sets all columns in the current row from left to right.
    /// </summary>
    /// <param name="values">Values for each column, in order.</param>
    public void SetRow(params object?[] values)
    {
        for (int i = 0; i < values.Length && i < _columnCount; i++)
        {
            Set(i, values[i]);
        }
    }

    /// <summary>
    /// Adds a new row with the specified values.
    /// </summary>
    /// <param name="values">Values for each column, in order.</param>
    public void AddRow(params object?[] values)
    {
        NextRow();
        SetRow(values);
    }

    /// <summary>
    /// Writes the table to a TextWriter with right-aligned columns.
    /// </summary>
    public void Write(TextWriter writer)
    {
        var sb = new StringBuilder();
        var bufferSize = _maxColumnLengths.Sum() + (_columnDelimiter.Length * (_columnCount - 1));
        var buffer = new char[Math.Max(bufferSize, 1)];

        // Write header row if any headers are set
        if (!_headerWritten && _headers.Any(h => h != null))
        {
            WriteRow(writer, sb, ref buffer, _headers);
            writer.WriteLine();
            _headerWritten = true;
        }

        foreach (var row in _rows)
        {
            WriteRow(writer, sb, ref buffer, row);

            if (row != _currentRow)
            {
                writer.WriteLine();
            }
        }
    }

    private void WriteRow(TextWriter writer, StringBuilder sb, ref char[] buffer, string?[] row)
    {
        sb.Clear();

        for (int i = 0; i < row.Length; i++)
        {
            var value = row[i] ?? string.Empty;
            sb.Append(' ', _maxColumnLengths[i] - value.Length);
            sb.Append(value);

            if (i != row.Length - 1)
            {
                sb.Append(_columnDelimiter);
            }
        }

        // Ensure buffer is large enough
        if (sb.Length > buffer.Length)
        {
            buffer = new char[sb.Length];
        }

        sb.CopyTo(0, buffer, 0, sb.Length);
        writer.Write(buffer, 0, sb.Length);
    }

    /// <summary>
    /// Returns the table as a string.
    /// </summary>
    public override string ToString()
    {
        using var writer = new StringWriter();
        Write(writer);
        return writer.ToString();
    }
}
