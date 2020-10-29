using Com.Danliris.Service.Finance.Accounting.Lib.BusinessLogic.Interfaces.DailyBankTransaction;
using Com.Danliris.Service.Finance.Accounting.Lib.BusinessLogic.Services.JournalTransaction;
using Com.Danliris.Service.Finance.Accounting.Lib.Helpers;
using Com.Danliris.Service.Finance.Accounting.Lib.Models.DailyBankTransaction;
using Com.Danliris.Service.Finance.Accounting.Lib.Services.HttpClientService;
using Com.Danliris.Service.Finance.Accounting.Lib.Services.IdentityService;
using Com.Danliris.Service.Finance.Accounting.Lib.Utilities;
using Com.Danliris.Service.Finance.Accounting.Lib.ViewModels.DailyBankTransaction;
using Com.Moonlay.Models;
using Com.Moonlay.NetCore.Lib;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Newtonsoft.Json;
using OfficeOpenXml;
using OfficeOpenXml.Style;
using System;
using System.Collections.Generic;
using System.Data;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading.Tasks;

namespace Com.Danliris.Service.Finance.Accounting.Lib.BusinessLogic.Services.DailyBankTransaction
{
    public class DailyBankTransactionService : IDailyBankTransactionService
    {
        private const string _UserAgent = "finance-service";
        protected DbSet<DailyBankTransactionModel> _DbSet;
        protected DbSet<BankTransactionMonthlyBalanceModel> _DbMonthlyBalanceSet;
        protected IIdentityService _IdentityService;
        private readonly IServiceProvider _serviceProvider;
        public FinanceDbContext _DbContext;

        public DailyBankTransactionService(IServiceProvider serviceProvider, FinanceDbContext dbContext)
        {
            _DbContext = dbContext;
            _DbSet = dbContext.Set<DailyBankTransactionModel>();
            _DbMonthlyBalanceSet = dbContext.Set<BankTransactionMonthlyBalanceModel>();
            _IdentityService = serviceProvider.GetService<IIdentityService>();
            _serviceProvider = serviceProvider;
        }

        public async Task<int> CreateAsync(DailyBankTransactionModel model)
        {
            do
            {
                model.Code = CodeGenerator.Generate();
            }
            while (_DbSet.Any(d => d.Code.Equals(model.Code)));

            model.Date = model.Date.AddHours(_IdentityService.TimezoneOffset);

            if (model.IsPosted)
                UpdateRemainingBalance(model);

            if (string.IsNullOrWhiteSpace(model.ReferenceNo))
            {
                if (model.Status == "OUT")
                    model.ReferenceNo = await GetDocumentNo("K", model.AccountBankCode, _IdentityService.Username);
                else if (model.Status == "IN")
                    model.ReferenceNo = await GetDocumentNo("M", model.AccountBankCode, _IdentityService.Username);
            }

            EntityExtension.FlagForCreate(model, _IdentityService.Username, _UserAgent);

            _DbSet.Add(model);
            return await _DbContext.SaveChangesAsync();
        }

        private async Task<string> GetDocumentNo(string type, string bankCode, string username)
        {
            var jsonSerializerSettings = new JsonSerializerSettings
            {
                MissingMemberHandling = MissingMemberHandling.Ignore
            };

            var http = _serviceProvider.GetService<IHttpClientService>();
            var uri = APIEndpoint.Purchasing + $"bank-expenditure-notes/bank-document-no?type={type}&bankCode={bankCode}&username={username}";
            var response = await http.GetAsync(uri);

            var result = new BaseResponse<string>();

            if (response.IsSuccessStatusCode)
            {
                var responseContent = await response.Content.ReadAsStringAsync();
                result = JsonConvert.DeserializeObject<BaseResponse<string>>(responseContent, jsonSerializerSettings);
            }
            return result.data;
        }

        private void UpdateRemainingBalance(DailyBankTransactionModel model)
        {
            bool isValas = (model.AccountBankCurrencyCode != "IDR");
            var Month = model.Date.Month;
            var Year = model.Date.Year;
            var BankId = model.AccountBankId;
            var ActualBalanceByMonth = _DbMonthlyBalanceSet.Where(w => w.Month.Equals(Month) && w.Year.Equals(Year) && w.AccountBankId.Equals(BankId)).FirstOrDefault();
            var Nominal = model.Status.Equals("IN") ? model.Nominal : model.Nominal * -1;
            var NominalValas = model.Status.Equals("IN") ? model.NominalValas : model.NominalValas * -1;

            if (ActualBalanceByMonth == null)
            {
                SetNewActualBalanceByMonth(Month, Year, model, Nominal, NominalValas, model.Date);
            }
            else
            {
                var NextMonthBalance = GetNextMonthBalance(Month, Year, model.AccountBankId, model.Date);
                var SumInByMonth = GetSumInByMonth(Month, Year, BankId);
                var SumOutByMonth = GetSumOutByMonth(Month, Year, BankId);
                var SumInByMonthValas = GetSumInByMonth(Month, Year, BankId, isValas);
                var SumOutByMonthValas = GetSumOutByMonth(Month, Year, BankId, isValas);

                ActualBalanceByMonth.RemainingBalance = ActualBalanceByMonth.InitialBalance + ((double)SumInByMonth + (double)Nominal - (double)SumOutByMonth);
                ActualBalanceByMonth.RemainingBalanceValas = ActualBalanceByMonth.InitialBalanceValas + ((double)SumInByMonthValas + (double)NominalValas - (double)SumOutByMonthValas);

                if (NextMonthBalance != null)
                {
                    NextMonthBalance.InitialBalance = ActualBalanceByMonth.RemainingBalance;
                    NextMonthBalance.InitialBalanceValas = ActualBalanceByMonth.RemainingBalanceValas;
                    EntityExtension.FlagForUpdate(NextMonthBalance, _IdentityService.Username, _UserAgent);
                    _DbMonthlyBalanceSet.Update(NextMonthBalance);
                }

                EntityExtension.FlagForUpdate(ActualBalanceByMonth, _IdentityService.Username, _UserAgent);
                _DbMonthlyBalanceSet.Update(ActualBalanceByMonth);
            }
        }

        private void SetNewActualBalanceByMonth(int month, int year, DailyBankTransactionModel model, decimal nominal, decimal nominalValas, DateTimeOffset date)
        {
            var PreviousMonthBalance = GetPreviousMonthBalance(month, year, model.AccountBankId, date);
            var NextMonthBalance = GetNextMonthBalance(month, year, model.AccountBankId, date);
            var NewMonthBalance = new BankTransactionMonthlyBalanceModel
            {
                Month = month,
                Year = year,
                InitialBalance = PreviousMonthBalance != null ? PreviousMonthBalance.RemainingBalance : 0,
                RemainingBalance = PreviousMonthBalance != null ? PreviousMonthBalance.RemainingBalance + (double)nominal : (double)nominal,
                AccountBankId = model.AccountBankId
            };

            NewMonthBalance.InitialBalanceValas = NewMonthBalance.InitialBalance * (double)model.CurrencyRate;
            NewMonthBalance.RemainingBalanceValas = NewMonthBalance.RemainingBalance * (double)model.CurrencyRate;

            EntityExtension.FlagForCreate(NewMonthBalance, _IdentityService.Username, _UserAgent);
            _DbMonthlyBalanceSet.Add(NewMonthBalance);

            if (NextMonthBalance != null)
            {
                NextMonthBalance.InitialBalance = NewMonthBalance.RemainingBalance;
                NextMonthBalance.InitialBalanceValas = NewMonthBalance.RemainingBalanceValas;
                NextMonthBalance.RemainingBalance += (double)nominal;
                NextMonthBalance.RemainingBalanceValas += (double)nominalValas;
                EntityExtension.FlagForUpdate(NextMonthBalance, _IdentityService.Username, _UserAgent);
                _DbMonthlyBalanceSet.Update(NextMonthBalance);
            }
        }

        private decimal GetSumOutByMonth(int month, int year, int bankId, bool isValas = false)
        {
            if (isValas)
                return _DbSet.Where(w => w.Date.Month.Equals(month) && w.Date.Year.Equals(year) && w.AccountBankId.Equals(bankId) && w.Status.Equals("OUT")).Sum(s => s.NominalValas);
            else
                return _DbSet.Where(w => w.Date.Month.Equals(month) && w.Date.Year.Equals(year) && w.AccountBankId.Equals(bankId) && w.Status.Equals("OUT")).Sum(s => s.Nominal);
        }

        private decimal GetSumInByMonth(int month, int year, int bankId, bool isValas = false)
        {
            if (isValas)
                return _DbSet.Where(w => w.Date.Month.Equals(month) && w.Date.Year.Equals(year) && w.AccountBankId.Equals(bankId) && w.Status.Equals("IN")).Sum(s => s.NominalValas);
            else
                return _DbSet.Where(w => w.Date.Month.Equals(month) && w.Date.Year.Equals(year) && w.AccountBankId.Equals(bankId) && w.Status.Equals("IN")).Sum(s => s.Nominal);
        }

        private BankTransactionMonthlyBalanceModel GetNextMonthBalance(int month, int year, int accountBankId, DateTimeOffset date)        
        {
            var query = _DbMonthlyBalanceSet.Where(entity => entity.AccountBankId == accountBankId);

            var sameYearQuery = query.Where(entity => entity.Year == date.Year);
            if (sameYearQuery.Count() > 0)
                return sameYearQuery.Where(entity => entity.Month > month).OrderBy(entity => entity.Month).FirstOrDefault();
            else
                return query.Where(entity => entity.Year > year).OrderBy(entity => entity.Year).ThenBy(entity => entity.Month).FirstOrDefault();

            //if (month == 12)
            //{
            //    return _DbMonthlyBalanceSet.Where(w => w.Month.Equals(1) && w.Year.Equals(year + 1)).FirstOrDefault();
            //}
            //else
            //{
            //    return _DbMonthlyBalanceSet.Where(w => w.Month.Equals(month + 1) && w.Year.Equals(year)).FirstOrDefault();
            //}
        }

        private BankTransactionMonthlyBalanceModel GetPreviousMonthBalance(int month, int year, int bankId, DateTimeOffset date)
        {
            var query = _DbMonthlyBalanceSet.Where(entity => entity.AccountBankId == bankId);

            var sameYearQuery = query.Where(entity => entity.Year == date.Year);
            if (sameYearQuery.Count() > 0)
                return sameYearQuery.Where(entity => entity.Month < month).OrderByDescending(entity => entity.Month).FirstOrDefault();
            else
                return query.Where(entity => entity.Year < year).OrderByDescending(entity => entity.Year).ThenByDescending(entity => entity.Month).FirstOrDefault();

            //if (month == 1)
            //{
            //    return _DbMonthlyBalanceSet.Where(w => w.Month.Equals(12) && w.Year.Equals(year - 1)).FirstOrDefault();
            //}
            //else
            //{
            //    return _DbMonthlyBalanceSet.Where(w => w.Month.Equals(month - 1) && w.Year.Equals(year)).FirstOrDefault();
            //}

            //return _DbMonthlyBalanceSet.Where(entity => entity.AccountBankId == bankId && entity.Year <= date.Year && entity.Month < date.Month).OrderByDescending(entity => entity.Year).ThenByDescending(entity => entity.Month).FirstOrDefault();

            //try
            //{
            //    var result = _DbMonthlyBalanceSet.Where(entity => entity.AccountBankId == bankId).OrderByDescending(entity => entity.Year).ThenByDescending(entity => entity.Month).FirstOrDefault();
            //    return result;
            //}
            //catch (Exception e)
            //{
            //    throw e;
            //}
        }

        public async Task<int> DeleteAsync(int id)
        {
            //not implemented
            var model = _DbSet.FirstOrDefault(entity => entity.Id == id);

            if (model != null && !model.IsPosted)
            {
                EntityExtension.FlagForDelete(model, _IdentityService.Username, _UserAgent);
                _DbSet.Update(model);
            }
            return await _DbContext.SaveChangesAsync();
        }

        public MemoryStream GetExcel(int bankId, int month, int year, int clientTimeZoneOffset)
        {
            string title = "Laporan Mutasi Bank Harian";
            AccountBank dataAccountBank = GetAccountBank(bankId).GetAwaiter().GetResult();

            if (dataAccountBank.Currency.Code == "IDR")
                return GenerateExcel(dataAccountBank, title, month, year, clientTimeZoneOffset);
            else
                return GenerateExcelValas(dataAccountBank, title, month, year, clientTimeZoneOffset);
        }

        private MemoryStream GenerateExcel(AccountBank dataAccountBank, string title, int month, int year, int clientTimeZoneOffset)
        {
            var Query = GetQuery(dataAccountBank.Id, month, year, clientTimeZoneOffset);
            string date = new DateTime(year, month, DateTime.DaysInMonth(year, month)).ToString("dd MMMM yyyy");

            string bank = $"Bank {dataAccountBank.BankName} A/C : {dataAccountBank.AccountNumber}";

            DataTable result = new DataTable();

            result.Columns.Add(new DataColumn() { ColumnName = "Tanggal", DataType = typeof(String) });
            result.Columns.Add(new DataColumn() { ColumnName = "Keterangan", DataType = typeof(String) });
            result.Columns.Add(new DataColumn() { ColumnName = "Nomor Referensi", DataType = typeof(String) });
            result.Columns.Add(new DataColumn() { ColumnName = "Jenis Referensi", DataType = typeof(String) });
            result.Columns.Add(new DataColumn() { ColumnName = "Currency", DataType = typeof(String) });
            result.Columns.Add(new DataColumn() { ColumnName = "Before", DataType = typeof(String) });
            result.Columns.Add(new DataColumn() { ColumnName = "Debit", DataType = typeof(String) });
            result.Columns.Add(new DataColumn() { ColumnName = "Kredit", DataType = typeof(String) });
            result.Columns.Add(new DataColumn() { ColumnName = "Saldo", DataType = typeof(String) });

            int index = 0;
            if (Query.ToArray().Count() == 0)
                result.Rows.Add("", "", "", "", "", 0.ToString("#,##0.#0"), 0.ToString("#,##0.#0"), 0.ToString("#,##0.#0"), 0.ToString("#,##0.#0")); // to allow column name to be generated properly for empty data as template
            else
            {
                var BalanceByMonthAndYear = GetBalanceMonthAndYear(dataAccountBank.Id, month, year, clientTimeZoneOffset);
                var beforeBalance = BalanceByMonthAndYear.InitialBalance;
                //var previous = new DailyBankTransactionModel();
                foreach (var item in Query)
                {
                    var debit = item.Status.ToUpper().Equals("IN") ? item.Nominal.ToString("#,##0.#0") : 0.ToString("#,##0.#0");
                    var kredit = item.Status.ToUpper().Equals("OUT") ? item.Nominal.ToString("#,##0.#0") : 0.ToString("#,##0.#0");
                    var afterBalance = beforeBalance + (item.Status.Equals("IN") ? (double)item.Nominal : (double)item.Nominal * -1);

                    result.Rows.Add(item.Date.ToOffset(new TimeSpan(clientTimeZoneOffset, 0, 0)).ToString("dd MMM yyyy", new CultureInfo("id-ID")),
                        item.Remark,
                        item.ReferenceNo,
                        item.ReferenceType,
                        item.AccountBankCurrencyCode,
                        beforeBalance.ToString("#,##0.#0"),
                        debit,
                        kredit,
                        afterBalance.ToString("#,##0.#0")
                        );
                    beforeBalance = afterBalance;
                    index++;
                }
            }

            return Excel.DailyMutationReportExcel(new List<KeyValuePair<DataTable, string>>() { new KeyValuePair<DataTable, string>(result, "Mutasi") }, title, bank, date, true, index);
        }

        private MemoryStream GenerateExcelValas(AccountBank dataAccountBank, string title, int month, int year, int clientTimeZoneOffset)
        {
            var Query = GetQuery(dataAccountBank.Id, month, year, clientTimeZoneOffset);
            string date = new DateTime(year, month, DateTime.DaysInMonth(year, month)).ToString("dd MMMM yyyy");

            var garmentCurrency = GetGarmentCurrency(dataAccountBank.Currency.Code).GetAwaiter().GetResult();
            string bank = $"Bank {dataAccountBank.BankName} A/C : {dataAccountBank.AccountNumber}";

            DataTable result = new DataTable();

            result.Columns.Add(new DataColumn() { ColumnName = "Tanggal", DataType = typeof(String) });
            result.Columns.Add(new DataColumn() { ColumnName = "Keterangan", DataType = typeof(String) });
            result.Columns.Add(new DataColumn() { ColumnName = "Nomor Referensi", DataType = typeof(String) });
            result.Columns.Add(new DataColumn() { ColumnName = "Jenis Referensi", DataType = typeof(String) });
            result.Columns.Add(new DataColumn() { ColumnName = "Currency", DataType = typeof(String) });
            result.Columns.Add(new DataColumn() { ColumnName = "Before", DataType = typeof(String) });
            result.Columns.Add(new DataColumn() { ColumnName = "Debit", DataType = typeof(String) });
            result.Columns.Add(new DataColumn() { ColumnName = "Debit2", DataType = typeof(String) });
            result.Columns.Add(new DataColumn() { ColumnName = "Kredit", DataType = typeof(String) });
            result.Columns.Add(new DataColumn() { ColumnName = "Kredit2", DataType = typeof(String) });
            result.Columns.Add(new DataColumn() { ColumnName = "After", DataType = typeof(String) });
            result.Columns.Add(new DataColumn() { ColumnName = "After2", DataType = typeof(String) });

            int index = 0;
            if (Query.ToArray().Count() == 0)
                result.Rows.Add("", "", "", "", "", 0.ToString("#,##0.#0"), 0.ToString("#,##0.#0"), 0.ToString("#,##0.#0"), 0.ToString("#,##0.#0")); // to allow column name to be generated properly for empty data as template
            else
            {
                var BalanceByMonthAndYear = GetBalanceMonthAndYear(dataAccountBank.Id, month, year, clientTimeZoneOffset);
                var beforeBalance = BalanceByMonthAndYear.InitialBalance;
                var beforeBalanceValas = beforeBalance / garmentCurrency.Rate;
                //var previous = new DailyBankTransactionModel();
                foreach (var item in Query)
                {
                    var debit = item.Status.ToUpper().Equals("IN") ? item.Nominal.ToString("#,##0.#0") : 0.ToString("#,##0.#0");
                    var kredit = item.Status.ToUpper().Equals("OUT") ? item.Nominal.ToString("#,##0.#0") : 0.ToString("#,##0.#0");
                    var afterBalance = beforeBalance + (item.Status.Equals("IN") ? (double)item.Nominal : (double)item.Nominal * -1);
                    var debitValas = item.Status.ToUpper().Equals("IN") ? item.NominalValas.ToString("#,##0.#0") : 0.ToString("#,##0.#0");
                    var kreditValas = item.Status.ToUpper().Equals("OUT") ? item.NominalValas.ToString("#,##0.#0") : 0.ToString("#,##0.#0");
                    var afterBalanceValas = beforeBalanceValas + (item.Status.Equals("IN") ? (double)item.NominalValas : (double)item.NominalValas * -1);

                    result.Rows.Add(item.Date.ToOffset(new TimeSpan(clientTimeZoneOffset, 0, 0)).ToString("dd MMM yyyy", new CultureInfo("id-ID")),
                        item.Remark,
                        item.ReferenceNo,
                        item.ReferenceType,
                        item.AccountBankCurrencyCode,
                        beforeBalance.ToString("#,##0.#0"),
                        debit,
                        debitValas,
                        kredit,
                        kreditValas,
                        afterBalance.ToString("#,##0.#0"),
                        afterBalanceValas.GetValueOrDefault().ToString("#,##0.#0")
                        );
                    beforeBalance = afterBalance;
                    beforeBalanceValas = afterBalanceValas;
                    index++;
                }
            }

            var headers = new List<string> { "Tanggal", "Keterangan", "Nomor Referensi", "Jenis Referensi", "Currency", "Before", "DEBIT", "Debit2", "KREDIT", "Kredit2", "SALDO AKHIR", "After2" };
            var subHeaders = new List<string> { "Original Amount", "Equivalent", "Original Amount", "Equivalent", "Original Amount", "Equivalent" };

            ExcelPackage package = new ExcelPackage();
            var sheet = package.Workbook.Worksheets.Add("Mutasi");

            sheet.Cells["A2"].Value = "PT. DANLIRIS";
            sheet.Cells["A2:D2"].Merge = true;

            sheet.Cells["A3"].Value = title;
            sheet.Cells["A3:D3"].Merge = true;

            sheet.Cells["A4"].Value = bank;
            sheet.Cells["A4:D4"].Merge = true;

            sheet.Cells["A5"].Value = $"Per {date}";
            sheet.Cells["A5:D5"].Merge = true;

            sheet.Cells["J6"].Value = "Kurs : Rp.";
            sheet.Cells["J6"].Style.HorizontalAlignment = ExcelHorizontalAlignment.Right;
            sheet.Cells["K6"].Value = $" {garmentCurrency.Rate.GetValueOrDefault().ToString("#,##0.#0")}";

            sheet.Cells["G7"].Value = headers[6];
            sheet.Cells["G7:H7"].Merge = true;
            sheet.Cells["I7"].Value = headers[8];
            sheet.Cells["I7:J7"].Merge = true;
            sheet.Cells["K7"].Value = headers[10];
            sheet.Cells["K7:L7"].Merge = true;

            foreach (var i in Enumerable.Range(0, 6))
            {
                var col = (char)('A' + i);
                sheet.Cells[$"{col}7"].Value = headers[i];
                sheet.Cells[$"{col}7:{col}8"].Merge = true;
            }

            foreach (var i in Enumerable.Range(0, 6))
            {
                var col = (char)('G' + i);
                sheet.Cells[$"{col}8"].Value = subHeaders[i];
            }

            sheet.Cells["A7:L8"].Style.HorizontalAlignment = ExcelHorizontalAlignment.Center;
            sheet.Cells["A7:L8"].Style.VerticalAlignment = ExcelVerticalAlignment.Center;
            sheet.Cells["A7:L8"].Style.Font.Bold = true;

            sheet.Cells["A9"].LoadFromDataTable(result, false, OfficeOpenXml.Table.TableStyles.Light16);
            sheet.Cells[sheet.Dimension.Address].AutoFitColumns();

            int cells = 9;
            sheet.Cells[$"F{cells}:L{cells + index}"].Style.HorizontalAlignment = ExcelHorizontalAlignment.Right;

            MemoryStream stream = new MemoryStream();
            package.SaveAs(stream);
            return stream;

            //return Excel.DailyMutationReportExcel(new List<KeyValuePair<DataTable, string>>() { new KeyValuePair<DataTable, string>(result, "Mutasi") }, title, bank, date, true, index);
        }

        private BankTransactionMonthlyBalanceModel GetBalanceMonthAndYear(int bankId, int month, int year, int clientTimeZoneOffset)
        {
            return _DbMonthlyBalanceSet.Where(w => w.AccountBankId.Equals(bankId) && w.Month.Equals(month) && w.Year.Equals(year)).FirstOrDefault();
        }
        
        private async Task<GarmentCurrency> GetGarmentCurrency(string codeCurrency)
        {
            string date = DateTimeOffset.UtcNow.ToString("yyyy/MM/dd HH:mm:ss");
            string queryString = $"code={codeCurrency}&stringDate={date}";

            var http = _serviceProvider.GetService<IHttpClientService>();
            var response = await http.GetAsync(APIEndpoint.Core + $"master/garment-currencies/single-by-code-date?{queryString}");

            var responseString = await response.Content.ReadAsStringAsync();
            var jsonSerializationSetting = new JsonSerializerSettings() { MissingMemberHandling = MissingMemberHandling.Ignore };

            var result = JsonConvert.DeserializeObject<APIDefaultResponse<GarmentCurrency>>(responseString, jsonSerializationSetting);

            return result.data;
        }

        private IQueryable<DailyBankTransactionModel> GetQuery(int bankId, int month, int year, int clientTimeZoneOffset)
        {
            //DateTimeOffset DateFrom = dateFrom == null ? dateTo == null ? DateTimeOffset.Now.AddDays(-30) : dateTo.Value.AddHours(clientTimeZoneOffset * -1).AddDays(-30) : dateFrom.Value.AddHours(clientTimeZoneOffset * -1);
            //DateTimeOffset DateTo = dateTo == null ? dateFrom == null ? DateTimeOffset.Now : dateFrom.Value.AddHours(clientTimeZoneOffset * -1).AddDays(DateTimeOffset.Now.Subtract(dateFrom.Value.AddHours(clientTimeZoneOffset * -1)).TotalDays) : dateTo.Value.AddHours(clientTimeZoneOffset * -1);

            var Query = (from transaction in _DbContext.DailyBankTransactions
                         where
                         transaction.IsDeleted == false
                         && transaction.AccountBankId == bankId
                         && transaction.Date.Month == month
                         && transaction.Date.Year == year
                         orderby transaction.Date, transaction.CreatedUtc
                         select new DailyBankTransactionModel
                         {
                             Id = transaction.Id,
                             Date = transaction.Date,
                             Remark = $"{transaction.SupplierName ?? transaction.BuyerName}\n{transaction.Remark}",
                             ReferenceNo = transaction.ReferenceNo,
                             ReferenceType = transaction.ReferenceType,
                             AccountBankCurrencyCode = transaction.AccountBankCurrencyCode,
                             BeforeNominal = transaction.BeforeNominal,
                             AfterNominal = transaction.AfterNominal,
                             Nominal = transaction.Nominal,
                             BeforeNominalValas = transaction.BeforeNominalValas,
                             AfterNominalValas = transaction.AfterNominalValas,
                             NominalValas = transaction.NominalValas,
                             Status = transaction.Status,
                         });

            return Query;
        }

        public ReadResponse<DailyBankTransactionModel> GetReport(int bankId, int month, int year, int clientTimeZoneOffset)
        {
            IQueryable<DailyBankTransactionModel> Query = GetQuery(bankId, month, year, clientTimeZoneOffset);

            //var Test = Query.ToList();
            List<DailyBankTransactionModel> Result = Query.ToList();
            if (Query.ToArray().Count() > 0)
            {
                var BalanceByMonthAndYear = GetBalanceMonthAndYear(bankId, month, year, clientTimeZoneOffset);
                var beforeBalance = BalanceByMonthAndYear.InitialBalance;
                var beforeBalanceValas = BalanceByMonthAndYear.InitialBalanceValas;

                foreach (var item in Result)
                {
                    var afterBalance = beforeBalance + (item.Status.Equals("IN") ? (double)item.Nominal : (double)item.Nominal * -1);
                    var afterBalanceValas = beforeBalanceValas + (item.Status.Equals("IN") ? (double)item.NominalValas : (double)item.NominalValas * -1);
                    item.BeforeNominal = (decimal)beforeBalance;
                    item.BeforeNominalValas = (decimal)beforeBalanceValas;
                    item.AfterNominal = (decimal)afterBalance;
                    item.AfterNominalValas = (decimal)afterBalanceValas;
                    beforeBalance = afterBalance;
                    beforeBalanceValas = afterBalanceValas;
                }
            }

            Dictionary<string, string> order = new Dictionary<string, string>();

            return new ReadResponse<DailyBankTransactionModel>(Result, Result.Count, order, new List<string>());
        }

        public ReadResponse<DailyBankTransactionModel> Read(int page, int size, string order, List<string> select, string keyword, string filter)
        {
            IQueryable<DailyBankTransactionModel> Query = _DbSet;

            Query = Query
                .Select(s => new DailyBankTransactionModel
                {
                    Id = s.Id,
                    CreatedUtc = s.CreatedUtc,
                    Code = s.Code,
                    LastModifiedUtc = s.LastModifiedUtc,
                    AccountBankName = s.AccountBankName,
                    AccountBankAccountName = s.AccountBankAccountName,
                    AccountBankAccountNumber = s.AccountBankAccountNumber,
                    AccountBankCode = s.AccountBankCode,
                    AccountBankCurrencyCode = s.AccountBankCurrencyCode,
                    AccountBankCurrencyId = s.AccountBankCurrencyId,
                    AccountBankCurrencySymbol = s.AccountBankCurrencySymbol,
                    AccountBankId = s.AccountBankId,
                    Date = s.Date,
                    ReferenceNo = s.ReferenceNo,
                    ReferenceType = s.ReferenceType,
                    Status = s.Status,
                    SourceType = s.SourceType,
                    IsPosted = s.IsPosted
                });

            List<string> searchAttributes = new List<string>()
            {
                "Code", "ReferenceNo", "ReferenceType","AccountBankName", "AccountBankCurrencyCode", "Status", "SourceType"
            };

            Query = QueryHelper<DailyBankTransactionModel>.Search(Query, searchAttributes, keyword);

            Dictionary<string, object> FilterDictionary = JsonConvert.DeserializeObject<Dictionary<string, object>>(filter);
            Query = QueryHelper<DailyBankTransactionModel>.Filter(Query, FilterDictionary);

            Dictionary<string, string> OrderDictionary = JsonConvert.DeserializeObject<Dictionary<string, string>>(order);
            Query = QueryHelper<DailyBankTransactionModel>.Order(Query, OrderDictionary);

            Pageable<DailyBankTransactionModel> pageable = new Pageable<DailyBankTransactionModel>(Query, page - 1, size);
            List<DailyBankTransactionModel> Data = pageable.Data.ToList();

            List<DailyBankTransactionModel> list = new List<DailyBankTransactionModel>();
            list.AddRange(
               Data.Select(s => new DailyBankTransactionModel
               {
                   Id = s.Id,
                   CreatedUtc = s.CreatedUtc,
                   Code = s.Code,
                   LastModifiedUtc = s.LastModifiedUtc,
                   AccountBankName = s.AccountBankName,
                   AccountBankAccountName = s.AccountBankAccountName,
                   AccountBankAccountNumber = s.AccountBankAccountNumber,
                   AccountBankCode = s.AccountBankCode,
                   AccountBankCurrencyCode = s.AccountBankCurrencyCode,
                   AccountBankCurrencyId = s.AccountBankCurrencyId,
                   AccountBankCurrencySymbol = s.AccountBankCurrencySymbol,
                   AccountBankId = s.AccountBankId,
                   Date = s.Date,
                   ReferenceNo = s.ReferenceNo,
                   ReferenceType = s.ReferenceType,
                   Status = s.Status,
                   SourceType = s.SourceType,
                   IsPosted = s.IsPosted
               }).ToList()
            );

            int TotalData = pageable.TotalCount;

            return new ReadResponse<DailyBankTransactionModel>(list, TotalData, OrderDictionary, new List<string>());
        }

        public async Task<DailyBankTransactionModel> ReadByIdAsync(int id)
        {
            return await _DbSet.Where(w => w.Id.Equals(id)).FirstOrDefaultAsync();
        }

        public async Task<int> UpdateAsync(int id, DailyBankTransactionModel model)
        {
            //not implemented
            //do
            //{
            //    model.Code = CodeGenerator.Generate();
            //}
            //while (_DbSet.Any(d => d.Code.Equals(model.Code)));

            //model.Date = model.Date.AddHours(_IdentityService.TimezoneOffset);

            if (!model.IsPosted)
            {
                if (string.IsNullOrWhiteSpace(model.ReferenceNo))
                {
                    if (model.Status == "OUT")
                        model.ReferenceNo = await GetDocumentNo("K", model.AccountBankCode, _IdentityService.Username);
                    else if (model.Status == "IN")
                        model.ReferenceNo = await GetDocumentNo("M", model.AccountBankCode, _IdentityService.Username);
                }

                EntityExtension.FlagForUpdate(model, _IdentityService.Username, _UserAgent);

                _DbSet.Update(model);
            }
            //UpdateRemainingBalance(model);

            return await _DbContext.SaveChangesAsync();
        }

        public async Task<int> DeleteByReferenceNoAsync(string referenceNo)
        {
            var bankTransaction = _DbSet.Where(w => w.ReferenceNo.Equals(referenceNo)).FirstOrDefault();

            if (bankTransaction != null)
            {
                EntityExtension.FlagForDelete(bankTransaction, _IdentityService.Username, _UserAgent);
                _DbSet.Update(bankTransaction);

                var monthlyBalance = _DbMonthlyBalanceSet.Where(w => w.Month.Equals(bankTransaction.Date.Month) && w.Year.Equals(bankTransaction.Date.Year) && w.AccountBankId.Equals(bankTransaction.AccountBankId)).FirstOrDefault();
                var nextMonthBalance = GetNextMonthBalance(bankTransaction.Date.Month, bankTransaction.Date.Year, bankTransaction.AccountBankId, bankTransaction.Date);

                if (monthlyBalance != null)
                {
                    if (bankTransaction.Status.Equals("IN"))
                    {
                        monthlyBalance.RemainingBalance -= (double)bankTransaction.Nominal;
                        if (nextMonthBalance != null)
                        {
                            nextMonthBalance.InitialBalance = monthlyBalance.RemainingBalance;
                            nextMonthBalance.RemainingBalance -= (double)bankTransaction.Nominal;
                            EntityExtension.FlagForUpdate(nextMonthBalance, _IdentityService.Username, _UserAgent);
                            _DbMonthlyBalanceSet.Update(nextMonthBalance);
                        }
                    }
                    else
                    {
                        monthlyBalance.RemainingBalance += (double)bankTransaction.Nominal;
                        if (nextMonthBalance != null)
                        {
                            nextMonthBalance.InitialBalance = monthlyBalance.RemainingBalance;
                            nextMonthBalance.RemainingBalance += (double)bankTransaction.Nominal;
                            EntityExtension.FlagForUpdate(nextMonthBalance, _IdentityService.Username, _UserAgent);
                            _DbMonthlyBalanceSet.Update(nextMonthBalance);
                        }
                    }

                    EntityExtension.FlagForUpdate(monthlyBalance, _IdentityService.Username, _UserAgent);
                    _DbMonthlyBalanceSet.Update(monthlyBalance);
                }
            }

            return await _DbContext.SaveChangesAsync();
        }

        public async Task<int> CreateInOutTransactionAsync(DailyBankTransactionModel model)
        {
            int result = 0;
            var inputModel = model.Clone();
            inputModel.AccountBankAccountName = model.DestinationBankAccountName;
            inputModel.AccountBankAccountNumber = model.DestinationBankAccountNumber;
            inputModel.AccountBankCode = model.DestinationBankCode;
            inputModel.AccountBankCurrencyCode = model.DestinationBankCurrencyCode;
            inputModel.AccountBankCurrencyId = model.DestinationBankCurrencyId;
            inputModel.AccountBankCurrencySymbol = model.DestinationBankCurrencySymbol;
            inputModel.AccountBankId = model.DestinationBankId;
            inputModel.AccountBankName = model.DestinationBankName;
            inputModel.Status = "IN";
            inputModel.DestinationBankAccountName = "";
            inputModel.DestinationBankAccountNumber = "";
            inputModel.DestinationBankCode = "";
            inputModel.DestinationBankCurrencyCode = "";
            inputModel.DestinationBankCurrencyId = 0;
            inputModel.DestinationBankCurrencySymbol = "";
            inputModel.DestinationBankId = 0;
            inputModel.DestinationBankName = "";
            inputModel.Nominal = model.TransactionNominal;
            inputModel.NominalValas = model.NominalValas;
            inputModel.CurrencyRate = model.CurrencyRate;

            model.Remark = FormatOutRemark(model);
            inputModel.Remark = FormatInRemark(inputModel, model);

            using (var transaction = _DbContext.Database.BeginTransaction())
            {
                try
                {
                    result += await CreateAsync(model);
                    result += await CreateAsync(inputModel);
                    transaction.Commit();
                }
                catch (Exception ex)
                {
                    transaction.Rollback();
                    throw ex;
                }

            }
            return result;
        }

        private string FormatInRemark(DailyBankTransactionModel inputModel, DailyBankTransactionModel model)
        {
            return !string.IsNullOrWhiteSpace(inputModel.Remark) ? $"{inputModel.Remark}\n\nPendanaan dari {model.AccountBankAccountName} - {model.AccountBankName} - {model.AccountBankAccountNumber} - {model.AccountBankCurrencyCode}\nSenilai {string.Format("{0:0,0.0}", model.Nominal)} {model.AccountBankCurrencyCode}" : $"Pendanaan dari {model.AccountBankAccountName} - {model.AccountBankName} - {model.AccountBankAccountNumber} - {model.AccountBankCurrencyCode}\nSenilai {string.Format("{0:0,0.0}", model.Nominal)} {model.AccountBankCurrencyCode}";
        }

        private string FormatOutRemark(DailyBankTransactionModel model)
        {
            return !string.IsNullOrWhiteSpace(model.Remark) ? $"{model.Remark}\n\nPendanaan untuk {model.DestinationBankAccountName} - {model.DestinationBankName} - {model.DestinationBankAccountNumber} - {model.DestinationBankCurrencyCode}\nSenilai {string.Format("{0:0,0.0}", model.TransactionNominal)} {model.DestinationBankCurrencyCode}" : $"Pendanaan untuk {model.DestinationBankAccountName} - {model.DestinationBankName} - {model.DestinationBankAccountNumber} - {model.DestinationBankCurrencyCode}\nSenilai {string.Format("{0:0,0.0}", model.TransactionNominal)} {model.DestinationBankCurrencyCode}";
        }

        private async Task<List<AccountBank>> GetAccountBankByDivision(string divisionName)
        {
            var http = _serviceProvider.GetService<IHttpClientService>();

            string uri = APIEndpoint.Core + $"master/account-banks/division/{divisionName}";
            var response = await http.GetAsync(uri);

            var responseString = await response.Content.ReadAsStringAsync();
            var jsonSerializationSetting = new JsonSerializerSettings() { MissingMemberHandling = MissingMemberHandling.Ignore };

            var result = JsonConvert.DeserializeObject<APIDefaultResponse<List<AccountBank>>>(responseString, jsonSerializationSetting);

            return result.data;
        }

        public List<DailyBalanceReportViewModel> GetDailyBalanceReport(int bankId, DateTime startDate, DateTime endDate, string divisionName)
        {

            //var result = _DbSet.Where(w => w.AccountBankId.Equals(bankId))
            var query = _DbSet.Where(w => w.Date >= startDate && w.Date <= endDate);

            if (bankId > 0)
            {
                query = query.Where(w => w.AccountBankId.Equals(bankId));
            }

            if (!string.IsNullOrWhiteSpace(divisionName))
            {
                var listOfBanks = GetAccountBankByDivision(divisionName).Result;
                var bankIds = listOfBanks.Select(bank => bank.Id).ToList();
                query = query.Where(w => bankIds.Contains(w.AccountBankId));
            }

            var result = query.GroupBy(g => g.AccountBankId).Select(s => new DailyBalanceReportViewModel()
            {
                AccountNumber = s.FirstOrDefault().AccountBankAccountNumber,
                Balance = (decimal)s.Sum(sum => sum.Status.Equals("IN") ? sum.Nominal : sum.Nominal * -1),
                BankName = s.FirstOrDefault().AccountBankName,
                Credit = (decimal)s.Sum(sum => sum.Status.Equals("OUT") ? sum.Nominal : 0),
                Debit = (decimal)s.Sum(sum => sum.Status.Equals("IN") ? sum.Nominal : 0),
                CurrencyCode = s.FirstOrDefault().AccountBankCurrencyCode
            });


            return result.ToList();
            //throw new NotImplementedException();
        }

        public MemoryStream GenerateExcelDailyBalance(int bankId, DateTime startDate, DateTime endDate, string divisionName, int clientTimeZoneOffset)
        {
            var queryResult = GetDailyBalanceReport(bankId, startDate, endDate, divisionName);
            var currencyQueryResult = GetDailyBalanceCurrencyReport(bankId, startDate, endDate, divisionName);
            string title = "Laporan Saldo Bank Harian",
                dateFrom = startDate == null ? "-" : startDate.ToString("dd MMMM yyyy"),
                dateTo = endDate == null ? "-" : endDate.ToString("dd MMMM yyyy");

            DataTable result = new DataTable();

            result.Columns.Add(new DataColumn() { ColumnName = "Nama Bank", DataType = typeof(String) });
            result.Columns.Add(new DataColumn() { ColumnName = "Nomor Rekening", DataType = typeof(String) });
            result.Columns.Add(new DataColumn() { ColumnName = "Mata Uang", DataType = typeof(String) });
            result.Columns.Add(new DataColumn() { ColumnName = "Debit", DataType = typeof(String) });
            result.Columns.Add(new DataColumn() { ColumnName = "Credit", DataType = typeof(String) });
            result.Columns.Add(new DataColumn() { ColumnName = "Saldo", DataType = typeof(String) });

            int index1 = 0;
            if (queryResult.ToArray().Count() == 0)
            {
                result.Rows.Add("", "", "", 0.ToString("#,##0.#0"), 0.ToString("#,##0.#0"), 0.ToString("#,##0.#0")); // to allow column name to be generated properly for empty data as template
                index1++;
            }
            else
            {
                foreach (var item in queryResult)
                {
                    result.Rows.Add(item.BankName, item.AccountNumber, item.CurrencyCode, item.Debit.ToString("#,##0.#0"), item.Credit.ToString("#,##0.#0"), item.Balance.ToString("#,##0.#0"));
                    index1++;
                }
            }

            DataTable currency = new DataTable();
            currency.Columns.Add(new DataColumn() { ColumnName = "Mata Uang", DataType = typeof(string) });
            currency.Columns.Add(new DataColumn() { ColumnName = "Debit", DataType = typeof(string) });
            currency.Columns.Add(new DataColumn() { ColumnName = "Credit", DataType = typeof(string) });
            currency.Columns.Add(new DataColumn() { ColumnName = "Saldo", DataType = typeof(string) });

            int index2 = 0;
            if (currencyQueryResult.ToArray().Count() == 0)
            {
                currency.Rows.Add("", 0.ToString("#,##0.#0"), 0.ToString("#,##0.#0"), 0.ToString("#,##0.#0")); // to allow column name to be generated properly for empty data as template
                index2++;
            }
            else
            {
                foreach (var item in currencyQueryResult)
                {
                    currency.Rows.Add(item.CurrencyCode, item.Debit.ToString("#,##0.#0"), item.Credit.ToString("#,##0.#0"), item.Balance.ToString("#,##0.#0"));
                    index2++;
                }
            }

            return Excel.CreateExcelWithTitle(new List<KeyValuePair<DataTable, string>>() { new KeyValuePair<DataTable, string>(result, "Saldo Harian"), new KeyValuePair<DataTable, string>(currency, "Saldo Harian Mata Uang") },
                new List<KeyValuePair<string, int>>() { new KeyValuePair<string, int>("Saldo Harian", index1), new KeyValuePair<string, int>("Saldo Harian Mata Uang", index2) },
                title, dateFrom, dateTo, true);
        }

        public List<DailyBalanceCurrencyReportViewModel> GetDailyBalanceCurrencyReport(int bankId, DateTime startDate, DateTime endDate, string divisionName)
        {
            var query = _DbSet.Where(w => w.Date >= startDate && w.Date <= endDate);

            if (bankId > 0)
            {
                query = query.Where(w => w.AccountBankId.Equals(bankId));
            }

            if (!string.IsNullOrWhiteSpace(divisionName))
            {
                var listOfBanks = GetAccountBankByDivision(divisionName).Result;
                var bankIds = listOfBanks.Select(bank => bank.Id).ToList();
                query = query.Where(w => bankIds.Contains(w.AccountBankId));
            }

            var currencyResult = query.GroupBy(g => g.AccountBankCurrencyId).Select(s => new DailyBalanceCurrencyReportViewModel()
            {
                Balance = (decimal)s.Sum(sum => sum.Status.Equals("IN") ? sum.Nominal : sum.Nominal * -1),
                Credit = (decimal)s.Sum(sum => sum.Status.Equals("OUT") ? sum.Nominal : 0),
                Debit = (decimal)s.Sum(sum => sum.Status.Equals("IN") ? sum.Nominal : 0),
                CurrencyCode = s.FirstOrDefault().AccountBankCurrencyCode
            });


            return currencyResult.ToList();
        }

        private async Task<AccountBank> GetAccountBank(int accountBankId)
        {
            var http = _serviceProvider.GetService<IHttpClientService>();

            string uri = APIEndpoint.Core + $"master/account-banks/{accountBankId}";
            var response = await http.GetAsync(uri);

            var responseString = await response.Content.ReadAsStringAsync();
            var jsonSerializationSetting = new JsonSerializerSettings() { MissingMemberHandling = MissingMemberHandling.Ignore };

            var result = JsonConvert.DeserializeObject<APIDefaultResponse<AccountBank>>(responseString, jsonSerializationSetting);

            return result.data;
        }

        public async Task<int> Posting(List<int> ids)
        {
            var models = _DbContext.DailyBankTransactions.Where(entity => ids.Contains(entity.Id)).ToList();

            foreach (var model in models)
            {
                model.IsPosted = true;
                EntityExtension.FlagForUpdate(model, _IdentityService.Username, _UserAgent);
                _DbContext.DailyBankTransactions.Update(model);

                UpdateRemainingBalance(model);

                await _DbContext.SaveChangesAsync();
            }

            return models.Count;
        }

        public List<DailyBankTransactionModel> GeneratePdf(int bankId, int month, int year, int clientTimeZoneOffset)
        {
            var Data = GetQuery(bankId, month, year, clientTimeZoneOffset).ToList();

            return Data;
        }

        public double GetBeforeBalance(int bankId, int month, int year, int clientTimeZoneOffset)
        {
            var BalanceByMonthAndYear = GetBalanceMonthAndYear(bankId, month, year, clientTimeZoneOffset);
            var beforeBalance = BalanceByMonthAndYear.InitialBalance;

            return beforeBalance;
        }

        public string GetDataAccountBank(int bankId)
        {
           var dataAccountBank = GetAccountBank(bankId).GetAwaiter().GetResult();
           string bank = $"Bank {dataAccountBank.BankName} A/C : {dataAccountBank.AccountNumber}";

            return bank;
        }

    }
}
