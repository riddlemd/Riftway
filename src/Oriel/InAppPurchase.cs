using Oriel.Cef;
using Oriel.Cef.Platform;

// Each StoreKit call runs behind an OperatingSystem.IsMacOS() check the analyzer cannot follow into lambdas.
#pragma warning disable CA1416

namespace Oriel;

/// <summary>A discount applied to a payment. Electron's <c>PaymentDiscount</c>.</summary>
public sealed record InAppPaymentDiscount(string Identifier, string KeyIdentifier, string Nonce, string Signature, int Timestamp);

/// <summary>A payment. Electron's <c>Payment</c>.</summary>
public sealed record InAppPayment(string ProductIdentifier, int Quantity, string ApplicationUsername, InAppPaymentDiscount? PaymentDiscount);

/// <summary>A payment transaction. Electron's <c>Transaction</c>.</summary>
/// <param name="TransactionIdentifier">The App Store's id for it.</param>
/// <param name="OriginalTransactionIdentifier">For a restored transaction, the id of the one it restores.</param>
/// <param name="ErrorCode">StoreKit's error code for a failed transaction; 0 otherwise.</param>
/// <param name="ErrorMessage">The error's description; empty otherwise.</param>
/// <param name="Payment">What was bought.</param>
/// <param name="TransactionState"><c>purchasing</c>, <c>purchased</c>, <c>failed</c>, <c>restored</c> or <c>deferred</c>.</param>
/// <param name="TransactionDate">Formatted <c>yyyy-MM-dd'T'HH:mm:ssZZZZZ</c> in local time, as Electron does; empty without one.</param>
public sealed record InAppTransaction(string TransactionIdentifier, string TransactionDate, string OriginalTransactionIdentifier,
    string TransactionState, int ErrorCode, string ErrorMessage, InAppPayment Payment);

/// <summary>A subscription's length. Electron's <c>ProductSubscriptionPeriod</c>.</summary>
/// <param name="NumberOfUnits">How many units long.</param>
/// <param name="Unit"><c>day</c>, <c>week</c>, <c>month</c> or <c>year</c>.</param>
public sealed record InAppProductSubscriptionPeriod(int NumberOfUnits, string Unit);

/// <summary>An introductory or promotional offer. Electron's <c>ProductDiscount</c>.</summary>
/// <param name="Identifier">The offer's id; empty for an introductory offer.</param>
/// <param name="Type">StoreKit's <c>SKProductDiscountType</c>: 0 introductory, 1 subscription.</param>
/// <param name="Price">The offer's price.</param>
/// <param name="PriceLocale">The price formatted in the App Store's locale, as Electron fills it, not a locale.</param>
/// <param name="PaymentMode"><c>payAsYouGo</c>, <c>payUpFront</c> or <c>freeTrial</c>.</param>
/// <param name="NumberOfPeriods">How many periods the offer lasts.</param>
/// <param name="SubscriptionPeriod">The length of one offer period, if the App Store gives one.</param>
public sealed record InAppProductDiscount(string Identifier, int Type, double Price, string PriceLocale, string PaymentMode, int NumberOfPeriods,
    InAppProductSubscriptionPeriod? SubscriptionPeriod);

/// <summary>A product in the App Store. Electron's <c>Product</c>.</summary>
/// <param name="ProductIdentifier">The product's id in App Store Connect.</param>
/// <param name="LocalizedDescription">The product's description in the App Store's locale.</param>
/// <param name="LocalizedTitle">The product's name in the App Store's locale.</param>
/// <param name="Price">The product's price; 0 without one.</param>
/// <param name="FormattedPrice">The price in the App Store's locale and currency.</param>
/// <param name="CurrencyCode">ISO 4217, from the App Store's locale.</param>
/// <param name="IntroductoryPrice">The introductory offer, if any.</param>
/// <param name="Discounts">The promotional offers.</param>
/// <param name="SubscriptionGroupIdentifier">The subscription's group; empty for other products.</param>
/// <param name="SubscriptionPeriod">The subscription's length; null for other products.</param>
/// <param name="IsDownloadable">Whether the App Store hosts content for it.</param>
/// <param name="DownloadContentVersion">The hosted content's version.</param>
/// <param name="DownloadContentLengths">Bytes per hosted content file; hosted content is deprecated since macOS 13.</param>
public sealed record InAppProduct(string ProductIdentifier, string LocalizedDescription, string LocalizedTitle, double Price, string FormattedPrice,
    string CurrencyCode, InAppProductDiscount? IntroductoryPrice, IReadOnlyList<InAppProductDiscount> Discounts, string SubscriptionGroupIdentifier,
    InAppProductSubscriptionPeriod? SubscriptionPeriod, bool IsDownloadable, string DownloadContentVersion, IReadOnlyList<long> DownloadContentLengths);

/// <summary>Options for <see cref="InAppPurchase.PurchaseProductAsync"/>.</summary>
public sealed record PurchaseProductOptions
{
    /// <summary>How many to buy; 1 by default. Electron's <c>quantity</c>.</summary>
    public int Quantity { get; init; } = 1;

    /// <summary>The user's account name, opaque to the App Store. Electron's <c>username</c>.</summary>
    public string Username { get; init; } = "";
}

/// <summary>Mac App Store in-app purchases through StoreKit (macOS). Electron's <c>inAppPurchase</c>.</summary>
/// <remarks>StoreKit answers only an app signed for the Mac App Store, or one run with a StoreKit test configuration.</remarks>
public static class InAppPurchase
{
    private static readonly Lock s_lock = new();
    private static bool s_observing;
    private static EventHandler<IReadOnlyList<InAppTransaction>>? s_transactionsUpdated;

    /// <summary>Transactions changed state; on the UI thread. Electron's <c>transactions-updated</c>.</summary>
    public static event EventHandler<IReadOnlyList<InAppTransaction>>? TransactionsUpdated
    {
        add
        {
            lock (s_lock) s_transactionsUpdated += value;
            Observe();
        }
        remove
        {
            lock (s_lock) s_transactionsUpdated -= value;
        }
    }

    // StoreKit calls back on its own thread; UI-thread posting is replaceable for tests.
    internal static Action<Action> Post { get; set; } = CefThread.PostUi;

    // Requests products from the App Store; replaceable so tests never reach it.
    internal static Action<IReadOnlyCollection<string>, Action<IReadOnlyList<MacProduct>>> RequestProducts { get; set; } = (ids, done) => MacStoreKit.GetProducts(ids, done);

    // Registers with StoreKit's payment queue; replaceable so tests never reach the App Store.
    internal static Action<Action<IReadOnlyList<MacTransaction>>> StartObserving { get; set; } = updated => MacStoreKit.ObserveTransactions(updated);

    /// <summary>Asks the App Store for <paramref name="productId"/> and, if it exists, adds a payment for it to the queue;
    /// <see cref="TransactionsUpdated"/> then follows the payment. The task gets whether the product was valid, and never
    /// completes if the request itself fails, as in Electron. Electron's <c>inAppPurchase.purchaseProduct()</c>.</summary>
    /// <exception cref="InvalidOperationException">Not on macOS, with Electron's message.</exception>
    /// <exception cref="ArgumentOutOfRangeException">The quantity is less than 1, which StoreKit refuses.</exception>
    public static Task<bool> PurchaseProductAsync(string productId, PurchaseProductOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(productId);
        if (!OperatingSystem.IsMacOS()) throw new InvalidOperationException("The inAppPurchase module can only be used on macOS");
        options ??= new PurchaseProductOptions();
        ArgumentOutOfRangeException.ThrowIfLessThan(options.Quantity, 1, nameof(options));
        Observe();
        var done = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        OnMain(() => MacStoreKit.PurchaseProduct(productId, options.Quantity, options.Username ?? "", valid => Post(() => done.SetResult(valid))));
        return done.Task;
    }

    /// <summary>Asks the App Store for <paramref name="productIds"/>; the task gets the products it knows, and never
    /// completes if the request itself fails, as in Electron. Electron's <c>inAppPurchase.getProducts()</c>.</summary>
    /// <exception cref="InvalidOperationException">Not on macOS, with Electron's message.</exception>
    public static Task<IReadOnlyList<InAppProduct>> GetProductsAsync(IEnumerable<string> productIds)
    {
        ArgumentNullException.ThrowIfNull(productIds);
        if (!OperatingSystem.IsMacOS()) throw new InvalidOperationException("The inAppPurchase module can only be used on macOS");
        string[] ids = [.. productIds];
        Observe();
        var done = new TaskCompletionSource<IReadOnlyList<InAppProduct>>(TaskCreationOptions.RunContinuationsAsynchronously);
        OnMain(() => RequestProducts(ids, products =>
        {
            IReadOnlyList<InAppProduct> converted = [.. products.Select(Convert)];
            Post(() => done.SetResult(converted));
        }));
        return done.Task;
    }

    /// <summary>Whether the user may make payments; false off macOS. Electron's <c>inAppPurchase.canMakePayments()</c>.</summary>
    public static bool CanMakePayments() => OperatingSystem.IsMacOS() && MacStoreKit.CanMakePayments();

    /// <summary>The path of the app's App Store receipt (which need not exist); empty off macOS. Electron's
    /// <c>inAppPurchase.getReceiptURL()</c>.</summary>
    public static string GetReceiptUrl() => OperatingSystem.IsMacOS() ? MacStoreKit.ReceiptPath() : "";

    /// <summary>Asks the App Store to send the user's earlier purchases again as <c>restored</c> transactions. Electron's
    /// <c>inAppPurchase.restoreCompletedTransactions()</c>.</summary>
    /// <exception cref="PlatformNotSupportedException">Not on macOS, where Electron has no such method.</exception>
    public static void RestoreCompletedTransactions()
    {
        RequireMac();
        Observe();
        OnMain(MacStoreKit.RestoreCompletedTransactions);
    }

    /// <summary>Finishes every pending transaction. Electron's <c>inAppPurchase.finishAllTransactions()</c>.</summary>
    /// <exception cref="PlatformNotSupportedException">Not on macOS.</exception>
    public static void FinishAllTransactions()
    {
        RequireMac();
        OnMain(MacStoreKit.FinishAllTransactions);
    }

    /// <summary>Finishes the pending transactions whose <see cref="InAppTransaction.TransactionDate"/> is
    /// <paramref name="date"/>. Electron's <c>inAppPurchase.finishTransactionByDate()</c>.</summary>
    /// <exception cref="PlatformNotSupportedException">Not on macOS.</exception>
    public static void FinishTransactionByDate(string date)
    {
        ArgumentNullException.ThrowIfNull(date);
        RequireMac();
        OnMain(() => MacStoreKit.FinishTransactionByDate(date));
    }

    internal static InAppTransaction Convert(MacTransaction t)
        => new(t.TransactionIdentifier, t.TransactionDate, t.OriginalTransactionIdentifier, t.TransactionState, t.ErrorCode, t.ErrorMessage,
            new InAppPayment(t.Payment.ProductIdentifier, t.Payment.Quantity, t.Payment.ApplicationUsername,
                t.Payment.PaymentDiscount is { } d ? new InAppPaymentDiscount(d.Identifier, d.KeyIdentifier, d.Nonce, d.Signature, d.Timestamp) : null));

    internal static InAppProduct Convert(MacProduct p)
        => new(p.ProductIdentifier, p.LocalizedDescription, p.LocalizedTitle, p.Price, p.FormattedPrice, p.CurrencyCode,
            p.IntroductoryPrice is { } i ? Convert(i) : null, [.. p.Discounts.Select(Convert)], p.SubscriptionGroupIdentifier,
            p.SubscriptionPeriod is { } s ? Convert(s) : null, p.IsDownloadable, p.DownloadContentVersion, [.. p.DownloadContentLengths]);

    private static InAppProductDiscount Convert(MacProductDiscount d)
        => new(d.Identifier, d.Type, d.Price, d.PriceLocale, d.PaymentMode, d.NumberOfPeriods, d.SubscriptionPeriod is { } s ? Convert(s) : null);

    private static InAppProductSubscriptionPeriod Convert(MacProductSubscriptionPeriod s) => new(s.NumberOfUnits, s.Unit);

    internal static void OnTransactionsUpdated(IReadOnlyList<MacTransaction> transactions)
    {
        IReadOnlyList<InAppTransaction> converted = [.. transactions.Select(Convert)];
        Post(() =>
        {
            EventHandler<IReadOnlyList<InAppTransaction>>? handler;
            lock (s_lock) handler = s_transactionsUpdated;
            handler?.Invoke(null, converted);
        });
    }

    // Electron's InAppPurchase is the payment queue's observer from the moment the module exists.
    private static void Observe()
    {
        if (!OperatingSystem.IsMacOS()) return;
        lock (s_lock)
        {
            if (s_observing) return;
            s_observing = true;
        }
        OnMain(() => StartObserving(OnTransactionsUpdated));
    }

    private static void RequireMac()
    {
        if (!OperatingSystem.IsMacOS()) throw new PlatformNotSupportedException("inAppPurchase is macOS only.");
    }

    private static void OnMain(Action action)
    {
        if (OrielApp.CurrentOrNull is { IsReady: true }) CefThread.RunOnUi(action);
        else action();
    }
}
