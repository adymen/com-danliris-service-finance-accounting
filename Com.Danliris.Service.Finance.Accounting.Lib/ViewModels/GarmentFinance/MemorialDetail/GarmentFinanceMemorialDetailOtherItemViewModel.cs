﻿using Com.Danliris.Service.Finance.Accounting.Lib.Utilities.BaseClass;
using Com.Danliris.Service.Finance.Accounting.Lib.ViewModels.NewIntegrationViewModel;
using System;
using System.Collections.Generic;
using System.Text;

namespace Com.Danliris.Service.Finance.Accounting.Lib.ViewModels.GarmentFinance.MemorialDetail
{
    public class GarmentFinanceMemorialDetailOtherItemViewModel : BaseViewModel
    {
        public ChartOfAccountViewModel Account { get; set; }
        public CurrencyViewModel Currency { get; set; }
        public decimal Amount { get; set; }
    }
}
