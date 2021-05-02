﻿using Com.Danliris.Service.Finance.Accounting.Lib.Models.PaymentDispositionNote;
using Com.Danliris.Service.Finance.Accounting.Lib.Utilities;
using Com.Danliris.Service.Finance.Accounting.Lib.Utilities.BaseInterface;
using Com.Danliris.Service.Finance.Accounting.Lib.ViewModels.PaymentDispositionNoteViewModel;
using System;
using System.Collections.Generic;
using System.Text;
using System.Threading.Tasks;

namespace Com.Danliris.Service.Finance.Accounting.Lib.BusinessLogic.Interfaces.PaymentDispositionNote
{
    public interface IPaymentDispositionNoteService : IBaseService<PaymentDispositionNoteModel>
    {
        ReadResponse<PaymentDispositionNoteItemModel> ReadDetailsByEPOId(string epoId);
        Task<int> Post(PaymentDispositionNotePostDto form);
    }
}
