using InvoiceEye.Core.Classification;
using InvoiceEye.Core.Lottery;
using InvoiceEye.Core.Models;
using InvoiceEye.Core.Services;
using InvoiceEye.Core.Vision;

namespace InvoiceEye.Api;

/// <summary>辨識一張照片，並附上分類建議、重複檢查和對獎結果，給 App 的確認畫面使用。</summary>
public sealed class RecognitionService
{
    private readonly RecognitionPipeline _pipeline;
    private readonly LedgerService _ledger;
    // Open eVision 的工具物件不保證可同時多執行緒使用，一次只辨識一張
    private readonly SemaphoreSlim _gate = new(1, 1);

    public RecognitionService(IVisionEngine engine, LedgerService ledger)
    {
        _pipeline = new RecognitionPipeline(engine);
        _ledger = ledger;
    }

    public async Task<object> RecognizeAsync(Stream photo)
    {
        InvoiceData data;
        await _gate.WaitAsync();
        try
        {
            data = await Task.Run(() => _pipeline.Recognize(photo));
        }
        finally
        {
            _gate.Release();
        }

        var classification = new CategoryClassifier(_ledger, _ledger).Classify(data);
        var duplicate = data.HasValidNumber && _ledger.InvoiceExists(data.Number!, data.Period?.Key);
        var fixedMatch = data.TotalAmount is { } total ? _ledger.FindMatchingFixed(data.Date ?? _ledger.Today, total) : null;
        var lottery = LotteryChecker.Check(data, _ledger.GetWinningNumbers(data.Period?.Key), _ledger.Today);

        return new
        {
            engine = _pipeline.EngineName,
            found = data.HasValidNumber || data.TotalAmount is not null,
            invoice = Dto.Invoice(data, data.SellerTaxId is { } s ? _ledger.StoreNameOf(s) : null),
            lines = classification.Lines.Select(l => new { l.Name, l.Amount, l.Category, source = l.Source.ToString() }),
            needsUserChoice = classification.NeedsUserChoice,
            duplicate,
            fixedMatch,
            lottery = new { status = lottery.Status.ToString(), lottery.Message, prize = LotteryChecker.PrizeName(lottery.Prize), lottery.PrizeAmount },
        };
    }
}
