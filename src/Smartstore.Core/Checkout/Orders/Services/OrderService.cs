﻿using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using Smartstore.Core.Common;
using Smartstore.Core.Common.Services;
using Smartstore.Core.Data;
using Smartstore.Data.Hooks;
using Smartstore.Events;

namespace Smartstore.Core.Checkout.Orders
{
    public partial class OrderService : AsyncDbSaveHook<Order>, IOrderService
    {
        private readonly SmartDbContext _db;
        private readonly IWorkContext _workContext;
        private readonly ICurrencyService _currencyService;
        private readonly IEventPublisher _eventPublisher;

        public OrderService(
            SmartDbContext db, 
            IWorkContext workContext, 
            ICurrencyService currencyService,
            IEventPublisher eventPublisher)
        {
            _db = db;
            _workContext = workContext;
            _currencyService = currencyService;
            _eventPublisher = eventPublisher;
        }

        #region Hook

        protected override Task<HookResult> OnUpdatedAsync(Order entity, IHookedEntity entry, CancellationToken cancelToken) 
            => Task.FromResult(HookResult.Ok);

        public override async Task OnAfterSaveCompletedAsync(IEnumerable<IHookedEntity> entries, CancellationToken cancelToken)
        {
            var orders = entries
                .Select(x => x.Entity)
                .OfType<Order>()
                .ToList();

            foreach (var order in orders)
            {
                await _eventPublisher.PublishOrderUpdatedAsync(order);
            }
        }

        #endregion

        public async Task<(Money OrderTotal, Money RoundingAmount)> GetOrderTotalInCustomerCurrencyAsync(Order order)
        {
            Guard.NotNull(order, nameof(order));

            var orderTotal = _currencyService.ConvertCurrency(order.OrderTotal, order.CurrencyRate);
            var roundingAmount = order.OrderTotalRounding;

            var customerCurrency = order.CustomerCurrencyCode.HasValue()
                ? await _db.Currencies.AsNoTracking().FirstOrDefaultAsync(x => x.CurrencyCode == order.CustomerCurrencyCode)
                : null;

            // Avoid rounding a rounded value. It would zero roundingAmount.
            if (orderTotal != order.OrderTotal &&
                customerCurrency != null &&
                customerCurrency.RoundOrderTotalEnabled &&
                order.PaymentMethodSystemName.HasValue())
            {
                var paymentMethod = await _db.PaymentMethods.AsNoTracking().FirstOrDefaultAsync(x => x.PaymentMethodSystemName == order.PaymentMethodSystemName);
                if (paymentMethod != null && paymentMethod.RoundOrderTotalEnabled)
                {
                    orderTotal = customerCurrency.RoundToNearest(orderTotal, out roundingAmount);
                }
            }

            // Get currency for output. Fallback to working currency if there's no one (we do not have anything better).
            var currency = customerCurrency ?? 
                (order.CustomerCurrencyCode.HasValue() ? new Currency { CurrencyCode = order.CustomerCurrencyCode } : _workContext.WorkingCurrency);

            // Do not round again!
            return (currency.AsMoney(orderTotal, false), currency.AsMoney(roundingAmount, false));
        }
    }
}
