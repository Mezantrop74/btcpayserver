using System;
using System.Collections.Generic;
using System.ComponentModel.DataAnnotations;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using BTCPayServer.Client.Models;
using BTCPayServer.Data;
using BTCPayServer.Events;
using BTCPayServer.Filters;
using BTCPayServer.Logging;
using BTCPayServer.Models;
using BTCPayServer.Models.InvoicingModels;
using BTCPayServer.Payments;
using BTCPayServer.Payments.Changelly;
using BTCPayServer.Payments.CoinSwitch;
using BTCPayServer.Payments.Lightning;
using BTCPayServer.Rating;
using BTCPayServer.Security;
using BTCPayServer.Services.Apps;
using BTCPayServer.Services.Invoices;
using BTCPayServer.Services.Rates;
using BTCPayServer.Services.Stores;
using BTCPayServer.Services.Wallets;
using BTCPayServer.Validation;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using NBitcoin;
using NBitpayClient;
using Newtonsoft.Json;
using CreateInvoiceRequest = BTCPayServer.Models.CreateInvoiceRequest;
using StoreData = BTCPayServer.Data.StoreData;

namespace BTCPayServer.Controllers
{
    [Filters.BitpayAPIConstraint(false)]
    public class CheckoutController : Controller
    {
        InvoiceRepository _InvoiceRepository;
        ContentSecurityPolicies _CSP;
        RateFetcher _RateProvider;
        StoreRepository _StoreRepository;
        UserManager<ApplicationUser> _UserManager;
        private CurrencyNameTable _CurrencyNameTable;
        EventAggregator _EventAggregator;
        BTCPayNetworkProvider _NetworkProvider;
        private readonly PaymentMethodHandlerDictionary _paymentMethodHandlerDictionary;
        IServiceProvider _ServiceProvider;
        public CheckoutController(
            IServiceProvider serviceProvider,
            InvoiceRepository invoiceRepository,
            CurrencyNameTable currencyNameTable,
            UserManager<ApplicationUser> userManager,
            RateFetcher rateProvider,
            StoreRepository storeRepository,
            EventAggregator eventAggregator,
            ContentSecurityPolicies csp,
            BTCPayNetworkProvider networkProvider,
            PaymentMethodHandlerDictionary paymentMethodHandlerDictionary)
        {
            _ServiceProvider = serviceProvider;
            _CurrencyNameTable = currencyNameTable ?? throw new ArgumentNullException(nameof(currencyNameTable));
            _StoreRepository = storeRepository ?? throw new ArgumentNullException(nameof(storeRepository));
            _InvoiceRepository = invoiceRepository ?? throw new ArgumentNullException(nameof(invoiceRepository));
            _RateProvider = rateProvider ?? throw new ArgumentNullException(nameof(rateProvider));
            _UserManager = userManager;
            _EventAggregator = eventAggregator;
            _NetworkProvider = networkProvider;
            _paymentMethodHandlerDictionary = paymentMethodHandlerDictionary;
            _CSP = csp;
        }

        [HttpGet]
        [Route("i/{invoiceId}")]
        [Route("i/{invoiceId}/{paymentMethodId}")]
        [Route("invoice")]
        [Route("/invoice/checkout")]
        [AcceptMediaTypeConstraint("application/bitcoin-paymentrequest", false)]
        [XFrameOptions(null)]
        [ReferrerPolicy("origin")]
        public async Task<IActionResult> Checkout(string invoiceId, string id = null, string paymentMethodId = null,
            [FromQuery]string view = null)
        {
            //Keep compatibility with Bitpay
            invoiceId = invoiceId ?? id;
            id = invoiceId;
            //

            var model = await GetInvoiceModel(invoiceId, paymentMethodId == null ? null : PaymentMethodId.Parse(paymentMethodId));
            if (model == null)
                return NotFound();

            if (view == "modal")
                model.IsModal = true;

            _CSP.Add(new ConsentSecurityPolicy("script-src", "'unsafe-eval'")); // Needed by Vue
            if (!string.IsNullOrEmpty(model.CustomCSSLink) &&
                Uri.TryCreate(model.CustomCSSLink, UriKind.Absolute, out var uri))
            {
                _CSP.Clear();
            }

            if (!string.IsNullOrEmpty(model.CustomLogoLink) &&
                Uri.TryCreate(model.CustomLogoLink, UriKind.Absolute, out uri))
            {
                _CSP.Clear();
            }

            return View(nameof(Checkout), model);
        }

        [HttpGet]
        [Route("invoice-noscript")]
        public async Task<IActionResult> CheckoutNoScript(string invoiceId, string id = null, string paymentMethodId = null)
        {
            //Keep compatibility with Bitpay
            invoiceId = invoiceId ?? id;
            id = invoiceId;
            //

            var model = await GetInvoiceModel(invoiceId, paymentMethodId == null ? null : PaymentMethodId.Parse(paymentMethodId));
            if (model == null)
                return NotFound();

            return View(model);
        }

        [HttpGet]
        [Route("i/{invoiceId}/status")]
        [Route("i/{invoiceId}/{paymentMethodId}/status")]
        [Route("invoice/{invoiceId}/status")]
        [Route("invoice/{invoiceId}/{paymentMethodId}/status")]
        [Route("invoice/status")]
        public async Task<IActionResult> GetStatus(string invoiceId, string paymentMethodId = null)
        {
            var model = await GetInvoiceModel(invoiceId, paymentMethodId == null ? null : PaymentMethodId.Parse(paymentMethodId));
            if (model == null)
                return NotFound();
            return Json(model);
        }

        private async Task<PaymentModel> GetInvoiceModel(string invoiceId, PaymentMethodId paymentMethodId)
        {
            var invoice = await _InvoiceRepository.GetInvoice(invoiceId);
            if (invoice == null)
                return null;
            var store = await _StoreRepository.FindStore(invoice.StoreId);
            bool isDefaultPaymentId = false;
            if (paymentMethodId == null)
            {
                paymentMethodId = store.GetDefaultPaymentId(_NetworkProvider);
                isDefaultPaymentId = true;
            }
            BTCPayNetworkBase network = _NetworkProvider.GetNetwork<BTCPayNetworkBase>(paymentMethodId.CryptoCode);
            if (network == null && isDefaultPaymentId)
            {
                //TODO: need to look into a better way for this as it does not scale
                network = _NetworkProvider.GetAll().OfType<BTCPayNetwork>().FirstOrDefault();
                paymentMethodId = new PaymentMethodId(network.CryptoCode, PaymentTypes.BTCLike);
            }
            if (invoice == null || network == null)
                return null;
            if (!invoice.Support(paymentMethodId))
            {
                if (!isDefaultPaymentId)
                    return null;
                var paymentMethodTemp = invoice.GetPaymentMethods()
                                               .Where(c => paymentMethodId.CryptoCode == c.GetId().CryptoCode)
                                               .FirstOrDefault();
                if (paymentMethodTemp == null)
                    paymentMethodTemp = invoice.GetPaymentMethods().First();
                network = paymentMethodTemp.Network;
                paymentMethodId = paymentMethodTemp.GetId();
            }

            var paymentMethod = invoice.GetPaymentMethod(paymentMethodId);
            var paymentMethodDetails = paymentMethod.GetPaymentMethodDetails();
            var dto = invoice.EntityToDTO();
            var cryptoInfo = dto.CryptoInfo.First(o => o.GetpaymentMethodId() == paymentMethodId);
            var storeBlob = store.GetStoreBlob();
            var currency = invoice.ProductInformation.Currency;
            var accounting = paymentMethod.Calculate();

            ChangellySettings changelly = (storeBlob.ChangellySettings != null && storeBlob.ChangellySettings.Enabled &&
                                           storeBlob.ChangellySettings.IsConfigured())
                ? storeBlob.ChangellySettings
                : null;

            CoinSwitchSettings coinswitch = (storeBlob.CoinSwitchSettings != null && storeBlob.CoinSwitchSettings.Enabled &&
                                           storeBlob.CoinSwitchSettings.IsConfigured())
                ? storeBlob.CoinSwitchSettings
                : null;


            var changellyAmountDue = changelly != null
                ? (accounting.Due.ToDecimal(MoneyUnit.BTC) *
                   (1m + (changelly.AmountMarkupPercentage / 100m)))
                : (decimal?)null;

            var paymentMethodHandler = _paymentMethodHandlerDictionary[paymentMethodId];

            var divisibility = _CurrencyNameTable.GetNumberFormatInfo(paymentMethod.GetId().CryptoCode, false)?.CurrencyDecimalDigits;

            var model = new PaymentModel()
            {
                CryptoCode = network.CryptoCode,
                RootPath = this.Request.PathBase.Value.WithTrailingSlash(),
                OrderId = invoice.OrderId,
                InvoiceId = invoice.Id,
                DefaultLang = storeBlob.DefaultLang ?? "en",
                CustomCSSLink = storeBlob.CustomCSS,
                CustomLogoLink = storeBlob.CustomLogo,
                HtmlTitle = storeBlob.HtmlTitle ?? "BTCPay Invoice",
                CryptoImage = Request.GetRelativePathOrAbsolute(paymentMethodHandler.GetCryptoImage(paymentMethodId)),
                BtcAddress = paymentMethodDetails.GetPaymentDestination(),
                BtcDue = accounting.Due.ShowMoney(divisibility),
                OrderAmount = (accounting.TotalDue - accounting.NetworkFee).ShowMoney(divisibility),
                OrderAmountFiat = OrderAmountFromInvoice(network.CryptoCode, invoice.ProductInformation),
                CustomerEmail = invoice.RefundMail,
                RequiresRefundEmail = storeBlob.RequiresRefundEmail,
                ShowRecommendedFee = storeBlob.ShowRecommendedFee,
                FeeRate = paymentMethodDetails.GetFeeRate(),
                ExpirationSeconds = Math.Max(0, (int)(invoice.ExpirationTime - DateTimeOffset.UtcNow).TotalSeconds),
                MaxTimeSeconds = (int)(invoice.ExpirationTime - invoice.InvoiceTime).TotalSeconds,
                MaxTimeMinutes = (int)(invoice.ExpirationTime - invoice.InvoiceTime).TotalMinutes,
                ItemDesc = invoice.ProductInformation.ItemDesc,
                Rate = _CurrencyNameTable.ExchangeRate(paymentMethod),
                MerchantRefLink = invoice.RedirectURL?.AbsoluteUri ?? "/",
                RedirectAutomatically = invoice.RedirectAutomatically,
                StoreName = store.StoreName,
                PeerInfo = (paymentMethodDetails as LightningLikePaymentMethodDetails)?.NodeInfo,
                TxCount = accounting.TxRequired,
                BtcPaid = accounting.Paid.ShowMoney(divisibility),
#pragma warning disable CS0618 // Type or member is obsolete
                Status = invoice.StatusString,
#pragma warning restore CS0618 // Type or member is obsolete
                NetworkFee = paymentMethodDetails.GetNextNetworkFee(),
                IsMultiCurrency = invoice.GetPayments().Select(p => p.GetPaymentMethodId()).Concat(new[] { paymentMethod.GetId() }).Distinct().Count() > 1,
                ChangellyEnabled = changelly != null,
                ChangellyMerchantId = changelly?.ChangellyMerchantId,
                ChangellyAmountDue = changellyAmountDue,
                CoinSwitchEnabled = coinswitch != null,
                CoinSwitchAmountMarkupPercentage = coinswitch?.AmountMarkupPercentage ?? 0,
                CoinSwitchMerchantId = coinswitch?.MerchantId,
                CoinSwitchMode = coinswitch?.Mode,
                StoreId = store.Id,
                AvailableCryptos = invoice.GetPaymentMethods()
                                          .Where(i => i.Network != null)
                                          .Select(kv =>
                                          {
                                              var availableCryptoPaymentMethodId = kv.GetId();
                                              var availableCryptoHandler = _paymentMethodHandlerDictionary[availableCryptoPaymentMethodId];
                                              return new PaymentModel.AvailableCrypto()
                                              {
                                                  PaymentMethodId = kv.GetId().ToString(),
                                                  CryptoCode = kv.Network?.CryptoCode ?? kv.GetId().CryptoCode,
                                                  PaymentMethodName = availableCryptoHandler.GetPaymentMethodName(availableCryptoPaymentMethodId),
                                                  IsLightning =
                                                      kv.GetId().PaymentType == PaymentTypes.LightningLike,
                                                  CryptoImage = Request.GetRelativePathOrAbsolute(availableCryptoHandler.GetCryptoImage(availableCryptoPaymentMethodId)),
                                                  Link = Url.Action(nameof(Checkout),
                                                      new
                                                      {
                                                          invoiceId = invoiceId,
                                                          paymentMethodId = kv.GetId().ToString()
                                                      })
                                              };
                                          }).Where(c => c.CryptoImage != "/")
                                          .OrderByDescending(a => a.CryptoCode == "BTC").ThenBy(a => a.PaymentMethodName).ThenBy(a => a.IsLightning ? 1 : 0)
                                          .ToList()
            };

            paymentMethodHandler.PreparePaymentModel(model, dto, storeBlob);
            if (model.IsLightning && storeBlob.LightningAmountInSatoshi && model.CryptoCode == "Sats")
            {
                model.Rate = _CurrencyNameTable.DisplayFormatCurrency(paymentMethod.Rate / 100_000_000, paymentMethod.ParentEntity.ProductInformation.Currency);
            }
            model.UISettings = paymentMethodHandler.GetCheckoutUISettings();
            model.PaymentMethodId = paymentMethodId.ToString();
            var expiration = TimeSpan.FromSeconds(model.ExpirationSeconds);
            model.TimeLeft = expiration.PrettyPrint();
            return model;
        }

        private string OrderAmountFromInvoice(string cryptoCode, ProductInformation productInformation)
        {
            // if invoice source currency is the same as currently display currency, no need for "order amount from invoice"
            if (cryptoCode == productInformation.Currency)
                return null;

            return _CurrencyNameTable.DisplayFormatCurrency(productInformation.Price, productInformation.Currency);
        }


    }
}
