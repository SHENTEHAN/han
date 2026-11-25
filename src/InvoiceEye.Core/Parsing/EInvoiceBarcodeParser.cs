using System.Text.RegularExpressions;
using InvoiceEye.Core.Models;

namespace InvoiceEye.Core.Parsing;

/// <summary>
/// 電子發票證明聯的一維條碼（Code 39，共 19 碼）：期別(5) + 發票號碼(10) + 隨機碼(4)。
/// </summary>
public static class EInvoiceBarcodeParser
{
    private static readonly Regex Pattern = new(@"^(\d{5})([A-Z]{2}\d{8})([0-9A-Za-z]{4})$", RegexOptions.Compiled);

    public static InvoiceData? Parse(IEnumerable<string> barcodes)
    {
        foreach (var raw in barcodes)
        {
            var data = Parse(raw);
            if (data is not null) return data;
        }
        return null;
    }

    public static InvoiceData? Parse(string? raw)
    {
        if (raw is null) return null;
        var m = Pattern.Match(raw.Trim().Trim('*'));
        if (!m.Success || !InvoicePeriod.TryParseKey(m.Groups[1].Value, out var period)) return null;
        return new InvoiceData
        {
            Type = InvoiceType.EInvoice,
            Period = period,
            Number = m.Groups[2].Value,
            NumberSource = DataSource.Barcode,
            RandomCode = m.Groups[3].Value,
        };
    }
}
