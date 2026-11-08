using System.Globalization;
using System.IO.Compression;
using System.Security;
using System.Text;
using System.Text.Json;

namespace DbAccessManager.Services;

public static class ExportFormatters
{
    public const string XlsxMimeType = "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet";
    public const string XlsbMimeType = "application/vnd.ms-excel.sheet.binary.macroEnabled.12";
    public const string CsvMimeType = "text/csv;charset=utf-8";

    public static string ToCsv(IReadOnlyList<string> columns, IReadOnlyList<object?[]> rows)
    {
        var sb = new StringBuilder();
        sb.AppendLine(string.Join(",", columns.Select(Csv.Cell)));
        foreach (var row in rows)
        {
            sb.AppendLine(string.Join(",", row.Select(v => Csv.Cell(FormatValue(v)))));
        }
        return sb.ToString();
    }

    public static byte[] ToXlsx(IReadOnlyList<string> columns, IReadOnlyList<object?[]> rows, string sheetName = "Query Results")
    {
        var cleanSheet = string.IsNullOrWhiteSpace(sheetName) ? "Query Results" : sheetName.Trim();
        if (cleanSheet.Length > 31) cleanSheet = cleanSheet[..31];
        cleanSheet = EscapeXml(cleanSheet);

        using var memoryStream = new MemoryStream();
        using (var archive = new ZipArchive(memoryStream, ZipArchiveMode.Create, leaveOpen: true))
        {
            // 1. [Content_Types].xml
            AddZipEntry(archive, "[Content_Types].xml",
                "<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?>\r\n" +
                "<Types xmlns=\"http://schemas.openxmlformats.org/package/2006/content-types\">\r\n" +
                "  <Default Extension=\"rels\" ContentType=\"application/vnd.openxmlformats-package.relationships+xml\"/>\r\n" +
                "  <Default Extension=\"xml\" ContentType=\"application/xml\"/>\r\n" +
                "  <Override PartName=\"/xl/workbook.xml\" ContentType=\"application/vnd.openxmlformats-officedocument.spreadsheetml.sheet.main+xml\"/>\r\n" +
                "  <Override PartName=\"/xl/worksheets/sheet1.xml\" ContentType=\"application/vnd.openxmlformats-officedocument.spreadsheetml.worksheet+xml\"/>\r\n" +
                "  <Override PartName=\"/xl/styles.xml\" ContentType=\"application/vnd.openxmlformats-officedocument.spreadsheetml.styles+xml\"/>\r\n" +
                "</Types>");

            // 2. _rels/.rels
            AddZipEntry(archive, "_rels/.rels",
                "<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?>\r\n" +
                "<Relationships xmlns=\"http://schemas.openxmlformats.org/package/2006/relationships\">\r\n" +
                "  <Relationship Id=\"rId1\" Type=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships/officeDocument\" Target=\"xl/workbook.xml\"/>\r\n" +
                "</Relationships>");

            // 3. xl/_rels/workbook.xml.rels
            AddZipEntry(archive, "xl/_rels/workbook.xml.rels",
                "<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?>\r\n" +
                "<Relationships xmlns=\"http://schemas.openxmlformats.org/package/2006/relationships\">\r\n" +
                "  <Relationship Id=\"rId1\" Type=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships/worksheet\" Target=\"worksheets/sheet1.xml\"/>\r\n" +
                "  <Relationship Id=\"rId2\" Type=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships/styles\" Target=\"styles.xml\"/>\r\n" +
                "</Relationships>");

            // 4. xl/styles.xml
            AddZipEntry(archive, "xl/styles.xml",
                "<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?>\r\n" +
                "<styleSheet xmlns=\"http://schemas.openxmlformats.org/spreadsheetml/2006/main\">\r\n" +
                "  <fonts count=\"2\">\r\n" +
                "    <font><name val=\"Segoe UI\"/><sz val=\"10\"/></font>\r\n" +
                "    <font><name val=\"Segoe UI\"/><sz val=\"10\"/><b/></font>\r\n" +
                "  </fonts>\r\n" +
                "  <fills count=\"3\">\r\n" +
                "    <fill><patternFill patternType=\"none\"/></fill>\r\n" +
                "    <fill><patternFill patternType=\"gray125\"/></fill>\r\n" +
                "    <fill><patternFill patternType=\"solid\"><fgColor rgb=\"FFEBF2FA\"/></patternFill></fill>\r\n" +
                "  </fills>\r\n" +
                "  <borders count=\"1\">\r\n" +
                "    <border><left/><right/><top/><bottom/><diagonal/></border>\r\n" +
                "  </borders>\r\n" +
                "  <cellStyleXfs count=\"1\">\r\n" +
                "    <xf numFmtId=\"0\" fontId=\"0\" fillId=\"0\" borderId=\"0\"/>\r\n" +
                "  </cellStyleXfs>\r\n" +
                "  <cellXfs count=\"2\">\r\n" +
                "    <xf numFmtId=\"0\" fontId=\"0\" fillId=\"0\" borderId=\"0\" xfId=\"0\"/>\r\n" +
                "    <xf numFmtId=\"0\" fontId=\"1\" fillId=\"2\" borderId=\"0\" xfId=\"0\" applyFont=\"1\" applyFill=\"1\"/>\r\n" +
                "  </cellXfs>\r\n" +
                "  <cellStyles count=\"1\">\r\n" +
                "    <cellStyle name=\"Normal\" xfId=\"0\" builtinId=\"0\"/>\r\n" +
                "  </cellStyles>\r\n" +
                "</styleSheet>");

            // 5. xl/workbook.xml
            AddZipEntry(archive, "xl/workbook.xml",
                "<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?>\r\n" +
                "<workbook xmlns=\"http://schemas.openxmlformats.org/spreadsheetml/2006/main\" xmlns:r=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships\">\r\n" +
                "  <sheets>\r\n" +
                $"    <sheet name=\"{cleanSheet}\" sheetId=\"1\" r:id=\"rId1\"/>\r\n" +
                "  </sheets>\r\n" +
                "</workbook>");

            // 6. xl/worksheets/sheet1.xml
            var sheetSb = new StringBuilder();
            sheetSb.AppendLine("<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?>");
            sheetSb.AppendLine("<worksheet xmlns=\"http://schemas.openxmlformats.org/spreadsheetml/2006/main\">");
            sheetSb.AppendLine("  <sheetData>");

            // Header row (r=1, style=1 for bold/fill)
            sheetSb.AppendLine("    <row r=\"1\">");
            for (var c = 0; c < columns.Count; c++)
            {
                var cellRef = $"{GetColumnLetter(c)}1";
                var headerVal = EscapeXml(columns[c] ?? string.Empty);
                sheetSb.AppendLine($"      <c r=\"{cellRef}\" s=\"1\" t=\"inlineStr\"><is><t>{headerVal}</t></is></c>");
            }
            sheetSb.AppendLine("    </row>");

            // Data rows (r=2, 3...)
            for (var r = 0; r < rows.Count; r++)
            {
                var rowIdx = r + 2;
                var row = rows[r];
                sheetSb.AppendLine($"    <row r=\"{rowIdx}\">");
                for (var c = 0; c < columns.Count; c++)
                {
                    var cellRef = $"{GetColumnLetter(c)}{rowIdx}";
                    var val = c < row.Length ? row[c] : null;

                    if (val is null or DBNull)
                    {
                        // Blank cell
                        continue;
                    }

                    if (val is bool b)
                    {
                        sheetSb.AppendLine($"      <c r=\"{cellRef}\" t=\"b\"><v>{(b ? "1" : "0")}</v></c>");
                    }
                    else if (IsNumeric(val))
                    {
                        var numStr = Convert.ToString(val, CultureInfo.InvariantCulture) ?? "0";
                        sheetSb.AppendLine($"      <c r=\"{cellRef}\"><v>{numStr}</v></c>");
                    }
                    else if (val is DateTime dt)
                    {
                        var dtStr = dt.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture);
                        sheetSb.AppendLine($"      <c r=\"{cellRef}\" t=\"inlineStr\"><is><t>{dtStr}</t></is></c>");
                    }
                    else if (val is DateTimeOffset dto)
                    {
                        var dtsStr = dto.ToString("yyyy-MM-dd HH:mm:ss zzz", CultureInfo.InvariantCulture);
                        sheetSb.AppendLine($"      <c r=\"{cellRef}\" t=\"inlineStr\"><is><t>{dtsStr}</t></is></c>");
                    }
                    else
                    {
                        var textVal = EscapeXml(FormatValue(val));
                        sheetSb.AppendLine($"      <c r=\"{cellRef}\" t=\"inlineStr\"><is><t>{textVal}</t></is></c>");
                    }
                }
                sheetSb.AppendLine("    </row>");
            }

            sheetSb.AppendLine("  </sheetData>");
            sheetSb.AppendLine("</worksheet>");
            AddZipEntry(archive, "xl/worksheets/sheet1.xml", sheetSb.ToString());
        }

        return memoryStream.ToArray();
    }

    public static byte[] ToXlsb(IReadOnlyList<string> columns, IReadOnlyList<object?[]> rows, string sheetName = "Query Results")
    {
        var cleanSheet = string.IsNullOrWhiteSpace(sheetName) ? "Query Results" : sheetName.Trim();
        if (cleanSheet.Length > 31) cleanSheet = cleanSheet[..31];

        using var memoryStream = new MemoryStream();
        using (var archive = new ZipArchive(memoryStream, ZipArchiveMode.Create, leaveOpen: true))
        {
            // 1. [Content_Types].xml
            AddZipEntry(archive, "[Content_Types].xml",
                "<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?>\r\n" +
                "<Types xmlns=\"http://schemas.openxmlformats.org/package/2006/content-types\">\r\n" +
                "  <Default Extension=\"bin\" ContentType=\"application/vnd.ms-excel.sheet.binary.macroEnabled.main\"/>\r\n" +
                "  <Default Extension=\"rels\" ContentType=\"application/vnd.openxmlformats-package.relationships+xml\"/>\r\n" +
                "  <Default Extension=\"xml\" ContentType=\"application/xml\"/>\r\n" +
                "  <Override PartName=\"/xl/workbook.bin\" ContentType=\"application/vnd.ms-excel.workbook\"/>\r\n" +
                "  <Override PartName=\"/xl/worksheets/sheet1.bin\" ContentType=\"application/vnd.ms-excel.worksheet\"/>\r\n" +
                "  <Override PartName=\"/xl/styles.bin\" ContentType=\"application/vnd.ms-excel.styles\"/>\r\n" +
                "</Types>");

            // 2. _rels/.rels
            AddZipEntry(archive, "_rels/.rels",
                "<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?>\r\n" +
                "<Relationships xmlns=\"http://schemas.openxmlformats.org/package/2006/relationships\">\r\n" +
                "  <Relationship Id=\"rId1\" Type=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships/officeDocument\" Target=\"xl/workbook.bin\"/>\r\n" +
                "</Relationships>");

            // 3. xl/_rels/workbook.bin.rels
            AddZipEntry(archive, "xl/_rels/workbook.bin.rels",
                "<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?>\r\n" +
                "<Relationships xmlns=\"http://schemas.openxmlformats.org/package/2006/relationships\">\r\n" +
                "  <Relationship Id=\"rId1\" Type=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships/worksheet\" Target=\"worksheets/sheet1.bin\"/>\r\n" +
                "  <Relationship Id=\"rId2\" Type=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships/styles\" Target=\"styles.bin\"/>\r\n" +
                "</Relationships>");

            // 4. xl/styles.bin (minimal stylesheet)
            using (var stylesStream = archive.CreateEntry("xl/styles.bin").Open())
            {
                WriteBiff12Record(stylesStream, 0x0187); // BrtBeginStyleSheet
                WriteBiff12Record(stylesStream, 0x0188); // BrtEndStyleSheet
            }

            // 5. xl/workbook.bin
            using (var wbStream = archive.CreateEntry("xl/workbook.bin").Open())
            {
                WriteBiff12Record(wbStream, 0x0183); // BrtBeginBook
                WriteBiff12Record(wbStream, 0x018F); // BrtBeginBundle

                // BrtBundleSh (0x009C)
                WriteBiff12Record(wbStream, 0x009C, bw =>
                {
                    bw.Write((uint)0); // hsState (0 = visible)
                    bw.Write((uint)1); // iTabId (1)
                    WriteBiff12WideString(bw, "rId1"); // strRelId
                    WriteBiff12WideString(bw, cleanSheet); // strName
                });

                WriteBiff12Record(wbStream, 0x0190); // BrtEndBundle
                WriteBiff12Record(wbStream, 0x0184); // BrtEndBook
            }

            // 6. xl/worksheets/sheet1.bin
            using (var wsStream = archive.CreateEntry("xl/worksheets/sheet1.bin").Open())
            {
                WriteBiff12Record(wsStream, 0x0081); // BrtBeginSheet

                // BrtWsDim (0x0094)
                WriteBiff12Record(wsStream, 0x0094, bw =>
                {
                    bw.Write((int)0); // rwFirst
                    bw.Write((int)rows.Count); // rwLast
                    bw.Write((int)0); // colFirst
                    bw.Write((int)Math.Max(0, columns.Count - 1)); // colLast
                });

                WriteBiff12Record(wsStream, 0x0091); // BrtBeginWsData

                // Row 0: Headers
                WriteBiff12Row(wsStream, 0, columns.Count, col =>
                {
                    var header = columns[col] ?? string.Empty;
                    return new Biff12Cell(Biff12CellType.String, header);
                });

                // Data Rows
                for (var r = 0; r < rows.Count; r++)
                {
                    var row = rows[r];
                    WriteBiff12Row(wsStream, r + 1, columns.Count, col =>
                    {
                        var val = col < row.Length ? row[col] : null;
                        if (val is null or DBNull) return new Biff12Cell(Biff12CellType.Blank, null);
                        if (val is bool b) return new Biff12Cell(Biff12CellType.Bool, b);
                        if (IsNumeric(val))
                        {
                            try
                            {
                                var d = Convert.ToDouble(val, CultureInfo.InvariantCulture);
                                return new Biff12Cell(Biff12CellType.Real, d);
                            }
                            catch
                            {
                                return new Biff12Cell(Biff12CellType.String, FormatValue(val));
                            }
                        }
                        return new Biff12Cell(Biff12CellType.String, FormatValue(val));
                    });
                }

                WriteBiff12Record(wsStream, 0x0092); // BrtEndWsData
                WriteBiff12Record(wsStream, 0x0082); // BrtEndSheet
            }
        }

        return memoryStream.ToArray();
    }

    private enum Biff12CellType { Blank, Bool, Real, String }
    private sealed record Biff12Cell(Biff12CellType Type, object? Value);

    private static void WriteBiff12Row(Stream stream, int rowIndex, int colCount, Func<int, Biff12Cell> getCell)
    {
        // BrtRowHdr (0x0000)
        WriteBiff12Record(stream, 0x0000, bw =>
        {
            bw.Write((int)rowIndex); // rw
            bw.Write((int)0);        // ixfe (3 bytes) + flags (1 byte)
            bw.Write((ushort)0);     // miyRw
            bw.Write((ushort)0);     // flags2
            bw.Write((byte)0);       // bWrap
        });

        for (var c = 0; c < colCount; c++)
        {
            var cell = getCell(c);
            switch (cell.Type)
            {
                case Biff12CellType.Blank:
                    WriteBiff12Record(stream, 0x0001, bw =>
                    {
                        bw.Write((int)c); // col
                        bw.Write((int)0); // ixfe (3 bytes) + fShowPhonetic (1 byte)
                    });
                    break;

                case Biff12CellType.Bool:
                    WriteBiff12Record(stream, 0x0003, bw =>
                    {
                        bw.Write((int)c); // col
                        bw.Write((int)0); // ixfe (3 bytes) + fShowPhonetic (1 byte)
                        bw.Write((byte)((bool)cell.Value! ? 1 : 0)); // fVal
                        bw.Write((byte)0); // fError
                    });
                    break;

                case Biff12CellType.Real:
                    WriteBiff12Record(stream, 0x0005, bw =>
                    {
                        bw.Write((int)c); // col
                        bw.Write((int)0); // ixfe (3 bytes) + fShowPhonetic (1 byte)
                        bw.Write((double)cell.Value!); // val
                    });
                    break;

                case Biff12CellType.String:
                    WriteBiff12Record(stream, 0x0002, bw =>
                    {
                        bw.Write((int)c); // col
                        bw.Write((int)0); // ixfe (3 bytes) + fShowPhonetic (1 byte)
                        WriteBiff12WideString(bw, cell.Value?.ToString() ?? string.Empty);
                    });
                    break;
            }
        }
    }

    private static void WriteBiff12Record(Stream stream, ushort recordType, Action<BinaryWriter>? writePayload = null)
    {
        byte[] payload = [];
        if (writePayload != null)
        {
            using var ms = new MemoryStream();
            using (var bw = new BinaryWriter(ms, Encoding.Unicode, leaveOpen: true))
            {
                writePayload(bw);
                bw.Flush();
            }
            payload = ms.ToArray();
        }

        // Record Type (variable-length, up to 2 bytes)
        if (recordType > 0x7F)
        {
            stream.WriteByte((byte)((recordType & 0x7F) | 0x80));
            stream.WriteByte((byte)(recordType >> 7));
        }
        else
        {
            stream.WriteByte((byte)recordType);
        }

        // Record Length (variable-length, up to 4 bytes)
        var len = (uint)payload.Length;
        while (len > 0x7F)
        {
            stream.WriteByte((byte)((len & 0x7F) | 0x80));
            len >>= 7;
        }
        stream.WriteByte((byte)len);

        // Payload
        if (payload.Length > 0)
        {
            stream.Write(payload, 0, payload.Length);
        }
    }

    private static void WriteBiff12WideString(BinaryWriter bw, string str)
    {
        bw.Write((uint)str.Length);
        if (str.Length > 0)
        {
            var bytes = Encoding.Unicode.GetBytes(str);
            bw.Write(bytes);
        }
    }

    private static void AddZipEntry(ZipArchive archive, string entryName, string content)
    {
        var entry = archive.CreateEntry(entryName, CompressionLevel.Fastest);
        using var writer = new StreamWriter(entry.Open(), Encoding.UTF8);
        writer.Write(content);
    }

    private static string GetColumnLetter(int colIndex)
    {
        var dividend = colIndex + 1;
        var columnName = string.Empty;
        while (dividend > 0)
        {
            var modulo = (dividend - 1) % 26;
            columnName = Convert.ToChar('A' + modulo) + columnName;
            dividend = (dividend - modulo) / 26;
        }
        return columnName;
    }

    private static string EscapeXml(string? value)
    {
        if (string.IsNullOrEmpty(value)) return string.Empty;
        var sb = new StringBuilder(value.Length);
        foreach (var ch in value)
        {
            if (ch is < (char)32 and not '\t' and not '\r' and not '\n') continue;
            switch (ch)
            {
                case '&': sb.Append("&amp;"); break;
                case '<': sb.Append("&lt;"); break;
                case '>': sb.Append("&gt;"); break;
                case '"': sb.Append("&quot;"); break;
                case '\'': sb.Append("&apos;"); break;
                default: sb.Append(ch); break;
            }
        }
        return sb.ToString();
    }

    public static string ToExcelXml(IReadOnlyList<string> columns, IReadOnlyList<object?[]> rows, string sheetName = "Query Results")
    {
        var cleanSheet = SecurityElement.Escape(string.IsNullOrWhiteSpace(sheetName) ? "Sheet1" : sheetName);
        var sb = new StringBuilder();
        sb.AppendLine("<?xml version=\"1.0\"?>");
        sb.AppendLine("<?mso-application progid=\"Excel.Sheet\"?>");
        sb.AppendLine("<Workbook xmlns=\"urn:schemas-microsoft-com:office:spreadsheet\"");
        sb.AppendLine(" xmlns:o=\"urn:schemas-microsoft-com:office:office\"");
        sb.AppendLine(" xmlns:x=\"urn:schemas-microsoft-com:office:excel\"");
        sb.AppendLine(" xmlns:ss=\"urn:schemas-microsoft-com:office:spreadsheet\"");
        sb.AppendLine(" xmlns:html=\"http://www.w3.org/TR/REC-html40\">");
        sb.AppendLine(" <Styles>");
        sb.AppendLine("  <Style ss:ID=\"Default\" ss:Name=\"Normal\"><Font ss:FontName=\"Segoe UI\" ss:Size=\"10\"/></Style>");
        sb.AppendLine("  <Style ss:ID=\"Header\"><Font ss:FontName=\"Segoe UI\" ss:Size=\"10\" ss:Bold=\"1\"/><Interior ss:Color=\"#EBF2FA\" ss:Pattern=\"Solid\"/></Style>");
        sb.AppendLine(" </Styles>");
        sb.AppendLine($" <Worksheet ss:Name=\"{cleanSheet}\">");
        sb.AppendLine("  <Table>");

        // Header Row
        sb.AppendLine("   <Row ss:StyleID=\"Header\">");
        foreach (var col in columns)
        {
            var escaped = SecurityElement.Escape(col ?? "");
            sb.AppendLine($"    <Cell><Data ss:Type=\"String\">{escaped}</Data></Cell>");
        }
        sb.AppendLine("   </Row>");

        // Data Rows
        foreach (var row in rows)
        {
            sb.AppendLine("   <Row>");
            foreach (var val in row)
            {
                if (val is null or DBNull)
                {
                    sb.AppendLine("    <Cell/>");
                }
                else if (IsNumeric(val))
                {
                    var numStr = Convert.ToString(val, CultureInfo.InvariantCulture);
                    sb.AppendLine($"    <Cell><Data ss:Type=\"Number\">{numStr}</Data></Cell>");
                }
                else if (val is DateTime dt)
                {
                    sb.AppendLine($"    <Cell><Data ss:Type=\"String\">{dt:yyyy-MM-dd HH:mm:ss}</Data></Cell>");
                }
                else if (val is DateTimeOffset dto)
                {
                    sb.AppendLine($"    <Cell><Data ss:Type=\"String\">{dto:yyyy-MM-dd HH:mm:ss zzz}</Data></Cell>");
                }
                else
                {
                    var str = FormatValue(val);
                    var escaped = SecurityElement.Escape(str);
                    sb.AppendLine($"    <Cell><Data ss:Type=\"String\">{escaped}</Data></Cell>");
                }
            }
            sb.AppendLine("   </Row>");
        }

        sb.AppendLine("  </Table>");
        sb.AppendLine(" </Worksheet>");
        sb.AppendLine("</Workbook>");
        return sb.ToString();
    }

    public static string ToJson(IReadOnlyList<string> columns, IReadOnlyList<object?[]> rows)
    {
        var list = new List<Dictionary<string, object?>>(rows.Count);
        foreach (var row in rows)
        {
            var dict = new Dictionary<string, object?>(columns.Count);
            for (var i = 0; i < columns.Count; i++)
            {
                var col = columns[i];
                dict[col] = (i < row.Length && row[i] is not DBNull) ? row[i] : null;
            }
            list.Add(dict);
        }
        return JsonSerializer.Serialize(list, new JsonSerializerOptions { WriteIndented = true });
    }

    public static string ToMarkdown(IReadOnlyList<string> columns, IReadOnlyList<object?[]> rows)
    {
        var sb = new StringBuilder();
        sb.AppendLine("| " + string.Join(" | ", columns.Select(c => (c ?? "").Replace("|", "\\|"))) + " |");
        sb.AppendLine("| " + string.Join(" | ", columns.Select(_ => "---")) + " |");
        foreach (var row in rows)
        {
            var cells = row.Select(v =>
            {
                if (v is null or DBNull) return "NULL";
                var s = FormatValue(v);
                return s.Replace("|", "\\|").Replace("\r\n", " ").Replace("\n", " ").Replace("\r", " ");
            });
            sb.AppendLine("| " + string.Join(" | ", cells) + " |");
        }
        return sb.ToString();
    }

    public static string ToSqlInsert(
        IReadOnlyList<string> columns,
        IReadOnlyList<object?[]> rows,
        string tableName = "Products",
        IReadOnlyCollection<string>? duplicateCheckColumns = null,
        IReadOnlyCollection<string>? excludedColumns = null)
    {
        var cleanTable = FormatTableName(string.IsNullOrWhiteSpace(tableName) ? "Products" : tableName);
        var excludedSet = new HashSet<string>(excludedColumns ?? Array.Empty<string>(), StringComparer.OrdinalIgnoreCase);

        var activeColIndices = new List<int>();
        var activeCols = new List<string>();
        for (var i = 0; i < columns.Count; i++)
        {
            if (!excludedSet.Contains(columns[i]))
            {
                activeColIndices.Add(i);
                activeCols.Add(columns[i]);
            }
        }

        if (activeCols.Count == 0)
        {
            for (var i = 0; i < columns.Count; i++)
            {
                activeColIndices.Add(i);
                activeCols.Add(columns[i]);
            }
        }

        var cols = string.Join(", ", activeCols.Select(c => $"[{c.Replace("]", "]]")}]"));
        var sb = new StringBuilder();

        var checkCols = (duplicateCheckColumns ?? Array.Empty<string>())
            .Where(c => columns.Contains(c, StringComparer.OrdinalIgnoreCase))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        var colMap = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        for (var i = 0; i < columns.Count; i++) colMap[columns[i]] = i;

        foreach (var row in rows)
        {
            var vals = string.Join(", ", activeColIndices.Select(idx => idx < row.Length ? FormatSqlLiteral(row[idx]) : "NULL"));
            var insertStmt = $"INSERT INTO {cleanTable} ({cols}) VALUES ({vals});";

            if (checkCols.Count > 0)
            {
                var conditions = checkCols.Select(c =>
                {
                    var colIdx = colMap[c];
                    var val = colIdx < row.Length ? row[colIdx] : null;
                    if (val is null or DBNull)
                    {
                        return $"[{c.Replace("]", "]]")}] IS NULL";
                    }
                    return $"[{c.Replace("]", "]]")}] = {FormatSqlLiteral(val)}";
                });

                var whereClause = string.Join(" AND ", conditions);
                sb.AppendLine($"IF NOT EXISTS (SELECT 1 FROM {cleanTable} WHERE {whereClause})");
                sb.AppendLine("BEGIN");
                sb.AppendLine($"    {insertStmt}");
                sb.AppendLine("END");
            }
            else
            {
                sb.AppendLine(insertStmt);
            }
        }
        return sb.ToString();
    }

    public static string InferTableName(string? sql, string fallback = "Products")
    {
        if (string.IsNullOrWhiteSpace(sql)) return fallback;
        try
        {
            var parser = new Microsoft.SqlServer.TransactSql.ScriptDom.TSql160Parser(true);
            using var reader = new StringReader(sql);
            var fragment = parser.Parse(reader, out var errors);
            if (errors.Count == 0 && fragment is not null)
            {
                var visitor = new PrimaryTableVisitor();
                fragment.Accept(visitor);
                if (!string.IsNullOrWhiteSpace(visitor.TableName)) return visitor.TableName;
            }
        }
        catch { /* fallback to regex on parse exception */ }

        var match = System.Text.RegularExpressions.Regex.Match(
            sql,
            @"\bFROM\s+(?:\[?[\w]+\]?\.)?\[?([a-zA-Z_][\w]*)\]?",
            System.Text.RegularExpressions.RegexOptions.IgnoreCase);

        if (match.Success && !string.IsNullOrWhiteSpace(match.Groups[1].Value))
        {
            return match.Groups[1].Value;
        }

        return fallback;
    }

    private sealed class PrimaryTableVisitor : Microsoft.SqlServer.TransactSql.ScriptDom.TSqlFragmentVisitor
    {
        public string? TableName { get; private set; }

        public override void ExplicitVisit(Microsoft.SqlServer.TransactSql.ScriptDom.NamedTableReference node)
        {
            if (TableName is null && node.SchemaObject is not null)
            {
                TableName = node.SchemaObject.BaseIdentifier?.Value;
            }
            base.ExplicitVisit(node);
        }
    }

    private static string FormatTableName(string tableName)
    {
        var raw = tableName.Trim();
        if (raw.StartsWith('[') && raw.EndsWith(']')) return raw;
        if (raw.Contains('.'))
        {
            var parts = raw.Split('.');
            return string.Join(".", parts.Select(p => $"[{p.Trim('[', ']').Replace("]", "]]")}]"));
        }
        return $"[{raw.Replace("]", "]]")}]";
    }

    private static string FormatValue(object? value) =>
        value switch
        {
            null or DBNull => "",
            DateTime dt => dt.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture),
            DateTimeOffset dto => dto.ToString("yyyy-MM-dd HH:mm:ss zzz", CultureInfo.InvariantCulture),
            _ => Convert.ToString(value, CultureInfo.InvariantCulture) ?? ""
        };

    private static string FormatSqlLiteral(object? value)
    {
        if (value is null or DBNull) return "NULL";
        if (value is bool b) return b ? "1" : "0";
        if (IsNumeric(value)) return Convert.ToString(value, CultureInfo.InvariantCulture) ?? "0";
        if (value is DateTime dt) return $"'{dt:yyyy-MM-dd HH:mm:ss}'";
        if (value is DateTimeOffset dto) return $"'{dto:yyyy-MM-dd HH:mm:ss zzz}'";
        var str = Convert.ToString(value, CultureInfo.InvariantCulture) ?? "";
        return $"'{str.Replace("'", "''")}'";
    }

    private static bool IsNumeric(object value) =>
        value is byte or sbyte or short or ushort or int or uint or long or ulong or float or double or decimal;
}
