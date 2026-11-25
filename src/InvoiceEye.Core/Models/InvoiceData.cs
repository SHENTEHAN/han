namespace InvoiceEye.Core.Models;

public enum InvoiceType
{
    Unknown = 0,
    /// <summary>電子發票證明聯（有 QR Code 與一維條碼）。</summary>
    EInvoice = 1,
    /// <summary>收銀機發票（細長紙條，紅色預印號碼）。</summary>
    CashRegister = 2,
    /// <summary>手開發票（二聯式／三聯式，紅色預印號碼，手寫金額）。</summary>
    Handwritten = 3,
}

/// <summary>號碼、金額等資料是從哪裡讀出來的。</summary>
public enum DataSource
{
    None = 0,
    QrCode = 1,
    Barcode = 2,
    Ocr = 3,
    Manual = 4,
}

public sealed record InvoiceItem(string Name, decimal Quantity, decimal UnitPrice)
{
    public decimal Amount => Quantity * UnitPrice;
}

/// <summary>從一張發票辨識出來的資料。缺的欄位為 null，由使用者補上。</summary>
public sealed class InvoiceData
{
    public InvoiceType Type { get; set; }

    /// <summary>發票號碼，格式 2 個英文字 + 8 位數字，例如 "AB12345678"。</summary>
    public string? Number { get; set; }

    public InvoicePeriod? Period { get; set; }
    public DateOnly? Date { get; set; }
    public string? RandomCode { get; set; }
    public decimal? SalesAmount { get; set; }
    public decimal? TotalAmount { get; set; }
    public string? BuyerTaxId { get; set; }
    public string? SellerTaxId { get; set; }
    public List<InvoiceItem> Items { get; set; } = new();

    public DataSource NumberSource { get; set; }
    public DataSource AmountSource { get; set; }

    /// <summary>需要使用者確認的原因，例如「QR Code 與條碼號碼不一致」。</summary>
    public List<string> Warnings { get; set; } = new();

    /// <summary>號碼格式正確，可以拿來對獎。</summary>
    public bool HasValidNumber => Number is not null && InvoiceNumber.IsValid(Number);

    /// <summary>號碼的 8 位數字部分（對獎用）。</summary>
    public string? Digits => HasValidNumber ? Number![2..] : null;
}
