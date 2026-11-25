using System.Text;
using InvoiceEye.Core.Models;
using InvoiceEye.Core.Parsing;

namespace InvoiceEye.Tests;

public class ParsingTests
{
    // 115/11/22 開立、未稅 81 (0x51)、含稅 85 (0x55)、無買方統編、賣方 12345678
    private const string Header = "AB12345678" + "1151122" + "1234" + "00000051" + "00000055" + "00000000" + "12345678" + "ABCDEFGHIJKLMNOPQRSTUVWX";

    [Fact]
    public void Parses_left_qr_header_and_items()
    {
        var left = Header + ":**********:2:3:1:茶葉蛋:1:10:御飯糰:1:35";
        var right = "**鮮奶:1:40";

        var d = EInvoiceQrParser.Parse(left, right)!;

        Assert.Equal(InvoiceType.EInvoice, d.Type);
        Assert.Equal("AB12345678", d.Number);
        Assert.Equal(new DateOnly(2026, 11, 22), d.Date);
        Assert.Equal("11512", d.Period!.Value.Key);
        Assert.Equal("1234", d.RandomCode);
        Assert.Equal(81m, d.SalesAmount);
        Assert.Equal(85m, d.TotalAmount);
        Assert.Null(d.BuyerTaxId);
        Assert.Equal("12345678", d.SellerTaxId);
        Assert.Equal(new[] { "茶葉蛋", "御飯糰", "鮮奶" }, d.Items.Select(i => i.Name));
        Assert.Equal(85m, d.Items.Sum(i => i.Amount));
        Assert.Empty(d.Warnings);
    }

    [Fact]
    public void Picks_left_and_right_from_unordered_list()
    {
        var d = EInvoiceQrParser.Parse(new[] { "**鮮奶:2:40", "https://example.com", Header + ":**********:0:1:1:" })!;
        Assert.Equal("AB12345678", d.Number);
        Assert.Single(d.Items);
        Assert.Equal(80m, d.Items[0].Amount);
    }

    [Fact]
    public void Decodes_base64_items()
    {
        var b64 = Convert.ToBase64String(Encoding.UTF8.GetBytes("咖啡:1:55"));
        var d = EInvoiceQrParser.Parse(Header + ":**********:1:1:2:" + b64, null)!;
        Assert.Equal("咖啡", d.Items.Single().Name);
        Assert.Equal(55m, d.Items.Single().UnitPrice);
    }

    [Fact]
    public void Decodes_big5_bytes_using_encoding_param()
    {
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
        var raw = Encoding.GetEncoding(950).GetBytes(Header + ":**********:1:1:0:便當:1:85");
        var text = EInvoiceQrParser.DecodeBytes(raw);
        Assert.EndsWith("便當:1:85", text);
    }

    [Fact]
    public void Rejects_non_invoice_qr()
    {
        Assert.Null(EInvoiceQrParser.Parse(new[] { "hello world", "**abc" }));
    }

    [Fact]
    public void Parses_19_digit_barcode()
    {
        var d = EInvoiceBarcodeParser.Parse("11512AB123456781234")!;
        Assert.Equal("11512", d.Period!.Value.Key);
        Assert.Equal("AB12345678", d.Number);
        Assert.Equal("1234", d.RandomCode);
        Assert.Equal(DataSource.Barcode, d.NumberSource);
    }

    [Theory]
    [InlineData("11511AB123456781234")] // 末月必須是雙數
    [InlineData("11512A1123456781234")]
    [InlineData("1151AB123456781234")]
    public void Rejects_bad_barcodes(string raw) => Assert.Null(EInvoiceBarcodeParser.Parse(raw));

    [Theory]
    [InlineData("AB-12345678", "AB12345678")]
    [InlineData("ab 1234 5678", "AB12345678")]
    [InlineData("A8123456O8", "AB12345608")]   // 第二碼 8→B、O→0
    [InlineData("xx: CD12S4S678 yy", "CD12545678")]
    [InlineData("0B12345678", "OB12345678")]
    public void Normalizes_ocr_invoice_numbers(string raw, string expected) =>
        Assert.Equal(expected, InvoiceNumber.Normalize(raw));

    [Theory]
    [InlineData("1234567890")]
    [InlineData("AB1234")]
    [InlineData("")]
    public void Rejects_unfixable_numbers(string raw) => Assert.Null(InvoiceNumber.Normalize(raw));
}
