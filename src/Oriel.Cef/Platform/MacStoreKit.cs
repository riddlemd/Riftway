using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace Oriel.Cef.Platform;

/// <summary>An <c>SKPaymentDiscount</c> as Electron's <c>PaymentDiscount</c>.</summary>
public sealed record MacPaymentDiscount(string Identifier, string KeyIdentifier, string Nonce, string Signature, int Timestamp);

/// <summary>An <c>SKPayment</c> as Electron's <c>Payment</c>.</summary>
public sealed record MacPayment(string ProductIdentifier, int Quantity, string ApplicationUsername, MacPaymentDiscount? PaymentDiscount);

/// <summary>An <c>SKPaymentTransaction</c> as Electron's <c>Transaction</c>.</summary>
public sealed record MacTransaction(string TransactionIdentifier, string TransactionDate, string OriginalTransactionIdentifier, int ErrorCode,
    string ErrorMessage, string TransactionState, MacPayment Payment);

/// <summary>An <c>SKProductSubscriptionPeriod</c> as Electron's <c>ProductSubscriptionPeriod</c>.</summary>
public sealed record MacProductSubscriptionPeriod(int NumberOfUnits, string Unit);

/// <summary>An <c>SKProductDiscount</c> as Electron's <c>ProductDiscount</c>; <paramref name="PriceLocale"/> holds the
/// formatted price, as Electron's does.</summary>
public sealed record MacProductDiscount(string Identifier, int Type, double Price, string PriceLocale, string PaymentMode, int NumberOfPeriods,
    MacProductSubscriptionPeriod? SubscriptionPeriod);

/// <summary>An <c>SKProduct</c> as Electron's <c>Product</c>.</summary>
public sealed record MacProduct(string ProductIdentifier, string LocalizedDescription, string LocalizedTitle, double Price, string FormattedPrice,
    string CurrencyCode, MacProductDiscount? IntroductoryPrice, IReadOnlyList<MacProductDiscount> Discounts, string SubscriptionGroupIdentifier,
    MacProductSubscriptionPeriod? SubscriptionPeriod, bool IsDownloadable, string DownloadContentVersion, IReadOnlyList<long> DownloadContentLengths);

/// <summary>Electron's <c>in_app_purchase</c> over StoreKit's payment queue (shell/browser/mac/in_app_purchase.mm and
/// in_app_purchase_observer.mm). Main thread.</summary>
[SupportedOSPlatform("macos")]
public static unsafe class MacStoreKit
{
    private const string ObjC = "/usr/lib/libobjc.A.dylib";
    private static readonly nint s_msgSend = NativeLibrary.GetExport(NativeLibrary.Load(ObjC), "objc_msgSend");
    private static readonly Lazy<nint> s_storeKit = new(() => NativeLibrary.Load("/System/Library/Frameworks/StoreKit.framework/StoreKit"));
    private static readonly string[] s_states = ["purchasing", "purchased", "failed", "restored", "deferred"];
    private static readonly string[] s_units = ["day", "week", "month", "year"];
    private static readonly string[] s_paymentModes = ["payAsYouGo", "payUpFront", "freeTrial"];
    // Each products request's delegate, kept alive until StoreKit answers.
    private static readonly Dictionary<nint, Action<IReadOnlyList<MacProduct>>> s_productRequests = [];
    // Each purchase's request delegate, kept alive until StoreKit answers, as Electron's self_.
    private static readonly Dictionary<nint, (Action<bool> Done, long Quantity, string Username)> s_purchases = [];
    // StoreKit may answer a request on its own thread, as Electron's posting to the UI thread allows for.
    private static readonly Lock s_requestsLock = new();
    private static nint s_observer;
    private static Action<IReadOnlyList<MacTransaction>>? s_updated;

    /// <summary><c>[SKPaymentQueue canMakePayments]</c>.</summary>
    public static bool CanMakePayments()
    {
        _ = s_storeKit.Value;
        return ((delegate* unmanaged<nint, nint, byte>)s_msgSend)(Class("SKPaymentQueue"u8), Sel("canMakePayments"u8)) != 0;
    }

    /// <summary>The main bundle's <c>appStoreReceiptURL</c> path, or empty.</summary>
    public static string ReceiptPath() => WithPool(() =>
    {
        var url = Send(Send(Class("NSBundle"u8), Sel("mainBundle"u8)), Sel("appStoreReceiptURL"u8));
        return url == 0 ? "" : ReadString(Send(url, Sel("path"u8)));
    });

    /// <summary><c>restoreCompletedTransactions</c>.</summary>
    public static void RestoreCompletedTransactions() => Send(Queue(), Sel("restoreCompletedTransactions"u8));

    /// <summary>Electron's <c>FinishAllTransactions</c>.</summary>
    public static void FinishAllTransactions() => WithPool(() =>
    {
        foreach (var transaction in Transactions()) Send(Queue(), Sel("finishTransaction:"u8), transaction);
        return 0;
    });

    /// <summary>Electron's <c>FinishTransactionByDate</c>: every transaction whose date, formatted as
    /// <see cref="FormatDate"/> does, is <paramref name="date"/>.</summary>
    public static void FinishTransactionByDate(string date) => WithPool(() =>
    {
        foreach (var transaction in Transactions())
        {
            var purchased = Send(transaction, Sel("transactionDate"u8));
            if (purchased != 0 && FormatDate(purchased) == date) Send(Queue(), Sel("finishTransaction:"u8), transaction);
        }
        return 0;
    });

    /// <summary>Electron's <c>PurchaseProduct</c>: requests the product, and if exactly one comes back adds a payment for
    /// <paramref name="quantity"/> of it under <paramref name="username"/>. <paramref name="done"/> gets whether the
    /// product was valid; it is never called when the request fails, as in Electron.</summary>
    public static void PurchaseProduct(string productId, long quantity, string username, Action<bool> done)
    {
        _ = s_storeKit.Value;
        WithPool(() =>
        {
            var requestDelegate = Send(Send(PurchaseDelegateClass(), Sel("alloc"u8)), Sel("init"u8));
            lock (s_requestsLock) s_purchases[requestDelegate] = (done, quantity, username);
            var ids = Send(Class("NSSet"u8), Sel("setWithObject:"u8), NSString(productId));
            var request = Send(Send(Class("SKProductsRequest"u8), Sel("alloc"u8)), Sel("initWithProductIdentifiers:"u8), ids);
            Send(request, Sel("setDelegate:"u8), requestDelegate);
            Send(request, Sel("start"u8));
            // StoreKit keeps a started request until it answers.
            Send(request, Sel("release"u8));
            return 0;
        });
    }

    /// <summary>Electron's <c>GetProducts</c>: requests <paramref name="productIds"/> and hands <paramref name="done"/> the
    /// products the App Store knows. <paramref name="done"/> is never called when the request fails, as in Electron.</summary>
    public static void GetProducts(IReadOnlyCollection<string> productIds, Action<IReadOnlyList<MacProduct>> done)
    {
        _ = s_storeKit.Value;
        WithPool(() =>
        {
            var requestDelegate = Send(Send(ProductsDelegateClass(), Sel("alloc"u8)), Sel("init"u8));
            lock (s_requestsLock) s_productRequests[requestDelegate] = done;
            var ids = Send(Class("NSMutableSet"u8), Sel("setWithCapacity:"u8), productIds.Count);
            foreach (var id in productIds) Send(ids, Sel("addObject:"u8), NSString(id));
            var request = Send(Send(Class("SKProductsRequest"u8), Sel("alloc"u8)), Sel("initWithProductIdentifiers:"u8), ids);
            Send(request, Sel("setDelegate:"u8), requestDelegate);
            Send(request, Sel("start"u8));
            Send(request, Sel("release"u8));
            return 0;
        });
    }

    /// <summary>Electron's <c>formatPrice:withLocal:</c> for <paramref name="price"/> (a decimal string) in the locale
    /// <paramref name="localeIdentifier"/>; for tests.</summary>
    public static string FormatPrice(string price, string localeIdentifier) => WithPool(() =>
        FormatPrice(Send(Class("NSDecimalNumber"u8), Sel("decimalNumberWithString:"u8), NSString(price)),
            Send(Class("NSLocale"u8), Sel("localeWithLocaleIdentifier:"u8), NSString(localeIdentifier))));

    /// <summary>The subscription period unit name Electron gives; empty for a unit it does not know.</summary>
    public static string UnitName(long unit) => unit is >= 0 and < 4 ? s_units[unit] : "";

    /// <summary>The discount payment mode name Electron gives; empty for a mode it does not know.</summary>
    public static string PaymentModeName(long mode) => mode is >= 0 and < 3 ? s_paymentModes[mode] : "";

    /// <summary>Electron's <c>TransactionObserver</c>: reports every <c>paymentQueue:updatedTransactions:</c>, on StoreKit's
    /// thread; registered once.</summary>
    public static void ObserveTransactions(Action<IReadOnlyList<MacTransaction>> updated)
    {
        _ = s_storeKit.Value;
        s_updated = updated;
        if (s_observer != 0) return;
        fixed (byte* name = "OrielTransactionObserver"u8)
        {
            var cls = objc_getClass(name);
            if (cls == 0)
            {
                cls = objc_allocateClassPair(Class("NSObject"u8), name, 0);
                fixed (byte* types = "v@:@@"u8)
                    class_addMethod(cls, Sel("paymentQueue:updatedTransactions:"u8), (delegate* unmanaged<nint, nint, nint, nint, void>)&OnUpdatedTransactions, types);
                fixed (byte* protocolName = "SKPaymentTransactionObserver"u8)
                    if (objc_getProtocol(protocolName) is var protocol and not 0) class_addProtocol(cls, protocol);
                objc_registerClassPair(cls);
            }
            s_observer = Send(Send(cls, Sel("alloc"u8)), Sel("init"u8));
        }
        Send(Queue(), Sel("addTransactionObserver:"u8), s_observer);
    }

    /// <summary>Electron's <c>dateToISOString:</c>: <c>yyyy-MM-dd'T'HH:mm:ssZZZZZ</c> in en_US_POSIX and the local time zone.</summary>
    public static string FormatDate(nint date) => WithPool(() =>
    {
        var formatter = Send(Send(Send(Class("NSDateFormatter"u8), Sel("alloc"u8)), Sel("init"u8)), Sel("autorelease"u8));
        Send(formatter, Sel("setLocale:"u8), Send(Class("NSLocale"u8), Sel("localeWithLocaleIdentifier:"u8), NSString("en_US_POSIX")));
        Send(formatter, Sel("setDateFormat:"u8), NSString("yyyy-MM-dd'T'HH:mm:ssZZZZZ"));
        return ReadString(Send(formatter, Sel("stringFromDate:"u8), date));
    });

    /// <summary>An autoreleased NSDate <paramref name="seconds"/> after 1970; for tests.</summary>
    public static nint DateSince1970(double seconds)
        => ((delegate* unmanaged<nint, nint, double, nint>)s_msgSend)(Class("NSDate"u8), Sel("dateWithTimeIntervalSince1970:"u8), seconds);

    /// <summary>The <c>transactionState</c> name Electron gives; empty for a state it does not know.</summary>
    public static string StateName(long state) => state is >= 0 and < 5 ? s_states[state] : "";

    private static IReadOnlyList<nint> Transactions()
    {
        var array = Send(Queue(), Sel("transactions"u8));
        var result = new List<nint>();
        for (nint i = 0, n = array == 0 ? 0 : Send(array, Sel("count"u8)); i < n; i++) result.Add(Send(array, Sel("objectAtIndex:"u8), i));
        return result;
    }

    private static nint Queue()
    {
        _ = s_storeKit.Value;
        return Send(Class("SKPaymentQueue"u8), Sel("defaultQueue"u8));
    }

    private static nint PurchaseDelegateClass()
    {
        fixed (byte* name = "OrielProductPurchase"u8)
        {
            var cls = objc_getClass(name);
            if (cls != 0) return cls;
            cls = objc_allocateClassPair(Class("NSObject"u8), name, 0);
            fixed (byte* types = "v@:@@"u8)
                class_addMethod(cls, Sel("productsRequest:didReceiveResponse:"u8), (delegate* unmanaged<nint, nint, nint, nint, void>)&OnPurchaseResponse, types);
            objc_registerClassPair(cls);
            return cls;
        }
    }

    private static nint ProductsDelegateClass()
    {
        fixed (byte* name = "OrielProductsRequest"u8)
        {
            var cls = objc_getClass(name);
            if (cls != 0) return cls;
            cls = objc_allocateClassPair(Class("NSObject"u8), name, 0);
            fixed (byte* types = "v@:@@"u8)
                class_addMethod(cls, Sel("productsRequest:didReceiveResponse:"u8), (delegate* unmanaged<nint, nint, nint, nint, void>)&OnProductsResponse, types);
            objc_registerClassPair(cls);
            return cls;
        }
    }

    [UnmanagedCallersOnly]
    private static void OnProductsResponse(nint self, nint cmd, nint request, nint response)
    {
        Action<IReadOnlyList<MacProduct>>? done;
        lock (s_requestsLock) if (!s_productRequests.Remove(self, out done)) return;
        try
        {
            var converted = WithPool(() =>
            {
                var products = Send(response, Sel("products"u8));
                return Each(products, Product);
            });
            done(converted);
        }
        catch (Exception e) { Console.Error.WriteLine($"[Oriel] Unhandled exception in a products request: {e}"); }
        finally { Send(self, Sel("release"u8)); }
    }

    // skProductToStruct:.
    private static MacProduct Product(nint product)
    {
        var price = Send(product, Sel("price"u8));
        var locale = Send(product, Sel("priceLocale"u8));
        var introductory = Send(product, Sel("introductoryPrice"u8));
        var period = Send(product, Sel("subscriptionPeriod"u8));
        return new MacProduct(
            ReadString(Send(product, Sel("productIdentifier"u8))),
            ReadString(Send(product, Sel("localizedDescription"u8))),
            ReadString(Send(product, Sel("localizedTitle"u8))),
            price == 0 ? 0 : Double(price),
            price == 0 || locale == 0 ? "" : FormatPrice(price, locale),
            price == 0 || locale == 0 ? "" : ReadString(Send(locale, Sel("currencyCode"u8))),
            introductory == 0 ? null : Discount(introductory),
            Each(Send(product, Sel("discounts"u8)), Discount),
            ReadString(Send(product, Sel("subscriptionGroupIdentifier"u8))),
            period == 0 ? null : Period(period),
            ((delegate* unmanaged<nint, nint, byte>)s_msgSend)(product, Sel("isDownloadable"u8)) != 0,
            ReadString(Send(product, Sel("downloadContentVersion"u8))),
            Each(Send(product, Sel("downloadContentLengths"u8)), n => ((delegate* unmanaged<nint, nint, long>)s_msgSend)(n, Sel("longLongValue"u8))));
    }

    // skProductDiscountToStruct:.
    private static MacProductDiscount Discount(nint discount)
    {
        var price = Send(discount, Sel("price"u8));
        var locale = Send(discount, Sel("priceLocale"u8));
        var period = Send(discount, Sel("subscriptionPeriod"u8));
        return new MacProductDiscount(
            ReadString(Send(discount, Sel("identifier"u8))),
            (int)Send(discount, Sel("type"u8)),
            price == 0 ? 0 : Double(price),
            locale == 0 || price == 0 ? "" : FormatPrice(price, locale),
            PaymentModeName(Send(discount, Sel("paymentMode"u8))),
            (int)Send(discount, Sel("numberOfPeriods"u8)),
            period == 0 ? null : Period(period));
    }

    // skProductSubscriptionPeriodToStruct:.
    private static MacProductSubscriptionPeriod Period(nint period)
        => new((int)Send(period, Sel("numberOfUnits"u8)), UnitName(Send(period, Sel("unit"u8))));

    private static string FormatPrice(nint price, nint locale)
    {
        var formatter = Send(Send(Send(Class("NSNumberFormatter"u8), Sel("alloc"u8)), Sel("init"u8)), Sel("autorelease"u8));
        Send(formatter, Sel("setFormatterBehavior:"u8), 1040);
        Send(formatter, Sel("setNumberStyle:"u8), 2);
        Send(formatter, Sel("setLocale:"u8), locale);
        return ReadString(Send(formatter, Sel("stringFromNumber:"u8), price));
    }

    private static double Double(nint number) => ((delegate* unmanaged<nint, nint, double>)s_msgSend)(number, Sel("doubleValue"u8));

    private static IReadOnlyList<T> Each<T>(nint array, Func<nint, T> convert)
    {
        var result = new List<T>();
        for (nint i = 0, n = array == 0 ? 0 : Send(array, Sel("count"u8)); i < n; i++) result.Add(convert(Send(array, Sel("objectAtIndex:"u8), i)));
        return result;
    }

    // productsRequest:didReceiveResponse: exactly one product checks out; anything else is an invalid product.
    [UnmanagedCallersOnly]
    private static void OnPurchaseResponse(nint self, nint cmd, nint request, nint response)
    {
        (Action<bool> Done, long Quantity, string Username) purchase;
        lock (s_requestsLock) if (!s_purchases.Remove(self, out purchase)) return;
        try
        {
            var products = Send(response, Sel("products"u8));
            var product = products != 0 && Send(products, Sel("count"u8)) == 1 ? Send(products, Sel("firstObject"u8)) : 0;
            if (product != 0)
            {
                var payment = Send(Class("SKMutablePayment"u8), Sel("paymentWithProduct:"u8), product);
                ((delegate* unmanaged<nint, nint, nint, void>)s_msgSend)(payment, Sel("setQuantity:"u8), (nint)purchase.Quantity);
                Send(payment, Sel("setApplicationUsername:"u8), NSString(purchase.Username));
                Send(Queue(), Sel("addPayment:"u8), payment);
            }
            purchase.Done(product != 0);
        }
        catch (Exception e) { Console.Error.WriteLine($"[Oriel] Unhandled exception in a purchase: {e}"); }
        finally { Send(self, Sel("release"u8)); }
    }

    [UnmanagedCallersOnly]
    private static void OnUpdatedTransactions(nint self, nint cmd, nint queue, nint transactions)
    {
        try
        {
            var converted = new List<MacTransaction>();
            var pool = objc_autoreleasePoolPush();
            try
            {
                for (nint i = 0, n = Send(transactions, Sel("count"u8)); i < n; i++) converted.Add(Transaction(Send(transactions, Sel("objectAtIndex:"u8), i)));
            }
            finally { objc_autoreleasePoolPop(pool); }
            s_updated?.Invoke(converted);
        }
        catch (Exception e) { Console.Error.WriteLine($"[Oriel] Unhandled exception in a transaction update: {e}"); }
    }

    // skPaymentTransactionToStruct:.
    private static MacTransaction Transaction(nint transaction)
    {
        var id = Send(transaction, Sel("transactionIdentifier"u8));
        var date = Send(transaction, Sel("transactionDate"u8));
        var original = Send(transaction, Sel("originalTransaction"u8));
        var error = Send(transaction, Sel("error"u8));
        var payment = Send(transaction, Sel("payment"u8));
        return new MacTransaction(
            ReadString(id),
            date == 0 ? "" : FormatDate(date),
            original == 0 ? "" : ReadString(Send(original, Sel("transactionIdentifier"u8))),
            error == 0 ? 0 : (int)Send(error, Sel("code"u8)),
            error == 0 ? "" : ReadString(Send(error, Sel("localizedDescription"u8))),
            StateName(Send(transaction, Sel("transactionState"u8))),
            payment == 0 ? new MacPayment("", 1, "", null) : Payment(payment));
    }

    // skPaymentToStruct: a quantity below 1 keeps the default 1.
    private static MacPayment Payment(nint payment)
    {
        var quantity = (long)Send(payment, Sel("quantity"u8));
        var discount = Send(payment, Sel("paymentDiscount"u8));
        return new MacPayment(ReadString(Send(payment, Sel("productIdentifier"u8))), quantity >= 1 ? (int)quantity : 1,
            ReadString(Send(payment, Sel("applicationUsername"u8))),
            discount == 0 ? null : new MacPaymentDiscount(ReadString(Send(discount, Sel("identifier"u8))), ReadString(Send(discount, Sel("keyIdentifier"u8))),
                ReadString(Send(Send(discount, Sel("nonce"u8)), Sel("UUIDString"u8))), ReadString(Send(discount, Sel("signature"u8))),
                ((delegate* unmanaged<nint, nint, int>)s_msgSend)(Send(discount, Sel("timestamp"u8)), Sel("intValue"u8))));
    }

    private static T WithPool<T>(Func<T> func)
    {
        var pool = objc_autoreleasePoolPush();
        try { return func(); }
        finally { objc_autoreleasePoolPop(pool); }
    }

    private static string ReadString(nint value) => value == 0 ? "" : Marshal.PtrToStringUTF8(Send(value, Sel("UTF8String"u8))) ?? "";

    private static nint NSString(string s)
    {
        fixed (char* p = s)
            return ((delegate* unmanaged<nint, nint, char*, nuint, nint>)s_msgSend)(Class("NSString"u8), Sel("stringWithCharacters:length:"u8), p, (nuint)s.Length);
    }

    private static nint Class(ReadOnlySpan<byte> name)
    {
        fixed (byte* p = name) return objc_getClass(p);
    }

    private static nint Sel(ReadOnlySpan<byte> name)
    {
        fixed (byte* p = name) return sel_registerName(p);
    }

    private static nint Send(nint receiver, nint selector) => ((delegate* unmanaged<nint, nint, nint>)s_msgSend)(receiver, selector);

    private static nint Send(nint receiver, nint selector, nint arg) => ((delegate* unmanaged<nint, nint, nint, nint>)s_msgSend)(receiver, selector, arg);

    [DllImport(ObjC)] private static extern nint objc_getClass(byte* name);
    [DllImport(ObjC)] private static extern nint sel_registerName(byte* name);
    [DllImport(ObjC)] private static extern nint objc_getProtocol(byte* name);
    [DllImport(ObjC)] private static extern byte class_addProtocol(nint cls, nint protocol);
    [DllImport(ObjC)] private static extern nint objc_autoreleasePoolPush();
    [DllImport(ObjC)] private static extern void objc_autoreleasePoolPop(nint pool);
    [DllImport(ObjC)] private static extern nint objc_allocateClassPair(nint superclass, byte* name, nuint extraBytes);
    [DllImport(ObjC)] private static extern void objc_registerClassPair(nint cls);
    [DllImport(ObjC)] private static extern byte class_addMethod(nint cls, nint name, void* imp, byte* types);
}
