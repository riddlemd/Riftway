using Oriel.Cef.Platform;

namespace Oriel.Tests;

[Collection(nameof(InAppPurchaseCollection))]
public sealed class InAppPurchaseTests : IDisposable
{
    private readonly Action<Action> _savedPost = InAppPurchase.Post;
    private readonly Action<Action<IReadOnlyList<MacTransaction>>> _savedStart = InAppPurchase.StartObserving;
    private readonly Action<IReadOnlyCollection<string>, Action<IReadOnlyList<MacProduct>>> _savedRequest = InAppPurchase.RequestProducts;
    private readonly List<(IReadOnlyCollection<string> Ids, Action<IReadOnlyList<MacProduct>> Done)> _requests = [];
    private readonly List<Action<IReadOnlyList<MacTransaction>>> _observers = [];
    private readonly List<Action> _posted = [];

    public InAppPurchaseTests()
    {
        InAppPurchase.Post = _posted.Add;
        InAppPurchase.StartObserving = _observers.Add;
        InAppPurchase.RequestProducts = (ids, done) => _requests.Add((ids, done));
    }

    public void Dispose()
    {
        InAppPurchase.Post = _savedPost;
        InAppPurchase.StartObserving = _savedStart;
        InAppPurchase.RequestProducts = _savedRequest;
    }

    private static MacTransaction Sample(MacPaymentDiscount? discount = null)
        => new("t1", "2026-10-20T03:04:05+02:00", "t0", 2, "Cancelled", "failed", new MacPayment("pro", 3, "alice", discount));

    [Fact]
    public void ATransactionConvertsFieldForField()
    {
        var t = InAppPurchase.Convert(Sample(new MacPaymentDiscount("d", "k", "n", "s", 42)));
        Assert.Equal(new InAppTransaction("t1", "2026-10-20T03:04:05+02:00", "t0", "failed", 2, "Cancelled",
            new InAppPayment("pro", 3, "alice", new InAppPaymentDiscount("d", "k", "n", "s", 42))), t);
        Assert.Null(InAppPurchase.Convert(Sample()).Payment.PaymentDiscount);
    }

    private static MacProduct SampleProduct() => new("pro", "All features", "Pro", 4.99, "$4.99", "USD",
        new MacProductDiscount("", 0, 0, "$0.00", "freeTrial", 1, new MacProductSubscriptionPeriod(7, "day")),
        [new MacProductDiscount("winback", 1, 1.99, "$1.99", "payAsYouGo", 3, null)], "group", new MacProductSubscriptionPeriod(1, "month"),
        true, "1.2", [10, 20]);

    [Fact]
    public void AProductConvertsFieldForField()
    {
        var p = InAppPurchase.Convert(SampleProduct());
        Assert.Equal(("pro", "All features", "Pro", 4.99, "$4.99", "USD", "group", true, "1.2"),
            (p.ProductIdentifier, p.LocalizedDescription, p.LocalizedTitle, p.Price, p.FormattedPrice, p.CurrencyCode, p.SubscriptionGroupIdentifier,
                p.IsDownloadable, p.DownloadContentVersion));
        Assert.Equal(new InAppProductDiscount("", 0, 0, "$0.00", "freeTrial", 1, new InAppProductSubscriptionPeriod(7, "day")), p.IntroductoryPrice);
        Assert.Equal(new InAppProductDiscount("winback", 1, 1.99, "$1.99", "payAsYouGo", 3, null), Assert.Single(p.Discounts));
        Assert.Equal(new InAppProductSubscriptionPeriod(1, "month"), p.SubscriptionPeriod);
        Assert.Equal([10L, 20L], p.DownloadContentLengths);
    }

    [Fact]
    public void AProductWithoutOffersOrPeriodConvertsToNulls()
    {
        var p = InAppPurchase.Convert(SampleProduct() with { IntroductoryPrice = null, SubscriptionPeriod = null, Discounts = [] });
        Assert.Null(p.IntroductoryPrice);
        Assert.Null(p.SubscriptionPeriod);
        Assert.Empty(p.Discounts);
    }

    [Fact]
    public async Task ProductsArriveOnTheUiThreadConverted()
    {
        if (!OperatingSystem.IsMacOS())
        {
            Assert.Throws<InvalidOperationException>(() => { _ = InAppPurchase.GetProductsAsync(["pro"]); });
            return;
        }
        var task = InAppPurchase.GetProductsAsync(["pro", "basic"]);
        var (ids, done) = Assert.Single(_requests);
        Assert.Equal(["pro", "basic"], ids);
        done([SampleProduct()]);
        Assert.False(task.IsCompleted);
        Assert.Single(_posted)();
        Assert.Equal("pro", Assert.Single(await task).ProductIdentifier);
    }

    [Fact]
    public void UpdatesAreRaisedOnTheUiThreadConverted()
    {
        IReadOnlyList<InAppTransaction>? seen = null;
        EventHandler<IReadOnlyList<InAppTransaction>> handler = (_, list) => seen = list;
        InAppPurchase.TransactionsUpdated += handler;
        try
        {
            InAppPurchase.OnTransactionsUpdated([Sample()]);
            Assert.Null(seen);
            Assert.Single(_posted)();
            Assert.Equal("t1", Assert.Single(seen!).TransactionIdentifier);
        }
        finally { InAppPurchase.TransactionsUpdated -= handler; }
    }

    [Fact]
    public async Task AQuantityBelowOneIsRefused()
    {
        if (!OperatingSystem.IsMacOS())
        {
            Assert.Throws<InvalidOperationException>(() => { _ = InAppPurchase.PurchaseProductAsync("x"); });
            return;
        }
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => InAppPurchase.PurchaseProductAsync("x", new PurchaseProductOptions { Quantity = 0 }));
    }

    [Fact]
    public void ElsewhereItIsElectronsStub()
    {
        if (OperatingSystem.IsMacOS()) return;
        Assert.False(InAppPurchase.CanMakePayments());
        Assert.Equal("", InAppPurchase.GetReceiptUrl());
        Assert.Throws<PlatformNotSupportedException>(InAppPurchase.FinishAllTransactions);
    }
}

[CollectionDefinition(nameof(InAppPurchaseCollection), DisableParallelization = true)]
public sealed class InAppPurchaseCollection;

public sealed class MacStoreKitTests
{
    [Theory]
    [InlineData(0, "purchasing")]
    [InlineData(1, "purchased")]
    [InlineData(2, "failed")]
    [InlineData(3, "restored")]
    [InlineData(4, "deferred")]
    [InlineData(5, "")]
    [InlineData(-1, "")]
    public void StatesHaveElectronsNames(long state, string name)
    {
        if (!OperatingSystem.IsMacOS()) return;
        Assert.Equal(name, MacStoreKit.StateName(state));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1760922245)]
    [InlineData(1751328000)]
    public void DatesAreFormattedAsElectronFormatsThem(long seconds)
    {
        if (!OperatingSystem.IsMacOS()) return;
        var local = DateTimeOffset.FromUnixTimeSeconds(seconds).ToLocalTime();
        var offset = local.Offset == TimeSpan.Zero ? "Z" : local.ToString("zzz", System.Globalization.CultureInfo.InvariantCulture);
        var expected = local.ToString("yyyy'-'MM'-'dd'T'HH':'mm':'ss", System.Globalization.CultureInfo.InvariantCulture) + offset;
        Assert.Equal(expected, MacStoreKit.FormatDate(MacStoreKit.DateSince1970(seconds)));
    }

    [Theory]
    [InlineData(0, "day")]
    [InlineData(1, "week")]
    [InlineData(2, "month")]
    [InlineData(3, "year")]
    [InlineData(4, "")]
    [InlineData(-1, "")]
    public void PeriodUnitsHaveElectronsNames(long unit, string name)
    {
        if (!OperatingSystem.IsMacOS()) return;
        Assert.Equal(name, MacStoreKit.UnitName(unit));
    }

    [Theory]
    [InlineData(0, "payAsYouGo")]
    [InlineData(1, "payUpFront")]
    [InlineData(2, "freeTrial")]
    [InlineData(3, "")]
    [InlineData(-1, "")]
    public void PaymentModesHaveElectronsNames(long mode, string name)
    {
        if (!OperatingSystem.IsMacOS()) return;
        Assert.Equal(name, MacStoreKit.PaymentModeName(mode));
    }

    [Theory]
    [InlineData("0.99", "en_US", "$0.99")]
    [InlineData("1234.5", "en_US", "$1,234.50")]
    [InlineData("0.99", "de_DE", "0,99\u00A0€")]
    [InlineData("120", "ja_JP", "\u00A5120")]
    public void PricesAreFormattedInTheProductsLocale(string price, string locale, string expected)
    {
        if (!OperatingSystem.IsMacOS()) return;
        Assert.Equal(System.Text.RegularExpressions.Regex.Unescape(expected), MacStoreKit.FormatPrice(price, locale));
    }

    [Fact]
    public void TheReceiptPathIsTheBundlesReceiptOrNone()
    {
        if (!OperatingSystem.IsMacOS()) return;
        // The test host is no bundle, so AppKit has no receipt URL for it.
        if (MacNotifications.HasBundleIdentifier()) Assert.EndsWith("/receipt", MacStoreKit.ReceiptPath());
        else Assert.Equal("", MacStoreKit.ReceiptPath());
        _ = MacStoreKit.CanMakePayments();
    }
}
