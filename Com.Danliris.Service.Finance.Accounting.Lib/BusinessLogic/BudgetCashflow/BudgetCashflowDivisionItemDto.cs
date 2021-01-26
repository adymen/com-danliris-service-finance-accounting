﻿using Com.Danliris.Service.Finance.Accounting.Lib.Models.BudgetCashflow;
using System;
using System.Collections.Generic;

namespace Com.Danliris.Service.Finance.Accounting.Lib.BusinessLogic.BudgetCashflow
{
    public class BudgetCashflowDivisionItemDto
    {
        public BudgetCashflowDivisionItemDto(BudgetCashflowTypeModel cashflowType)
        {
            CashflowType = cashflowType;
        }

        public BudgetCashflowDivisionItemDto()
        {
        }

        public BudgetCashflowDivisionItemDto(BudgetCashflowTypeModel cashflowType, CashType type, CurrencyDto currency, bool isShowSummaryLabel)
        {
            CashflowType = cashflowType;
            Type = type;
            SummaryLabel = $"Total {Type.ToDescriptionString()} {cashflowType.Name}";
            Currency = currency;
            IsSummary = true;
            IsShowSummaryLabel = isShowSummaryLabel;
        }

        public BudgetCashflowDivisionItemDto(BudgetCashflowTypeModel cashflowType, CashType type, bool isShowSummaryLabel) : this(cashflowType)
        {
            Type = type;
            SummaryLabel = $"Total {Type.ToDescriptionString()} {cashflowType.Name}";
            IsSummary = true;
            IsShowSummaryLabel = isShowSummaryLabel;
        }

        public BudgetCashflowDivisionItemDto(BudgetCashflowTypeModel cashflowType, CashType type) : this(cashflowType)
        {
            Type = type;
        }

        public BudgetCashflowDivisionItemDto(BudgetCashflowTypeModel cashflowType, CurrencyDto currency, bool isShowDifferenceLabel) : this(cashflowType)
        {
            IsDifference = true;
            IsShowDifferenceLabel = isShowDifferenceLabel;
            DifferenceLabel = $"Selisih {cashflowType.Name}";
        }

        public BudgetCashflowDivisionItemDto(BudgetCashflowTypeModel cashflowType, bool isShowDifferenceLabel) : this(cashflowType)
        {
            IsDifference = true;
            IsShowDifferenceLabel = isShowDifferenceLabel;
            DifferenceLabel = $"Selisih {cashflowType.Name}";
        }

        public BudgetCashflowDivisionItemDto(BudgetCashflowTypeModel cashflowType, CashType type, BudgetCashflowCategoryModel cashflowCategory, BudgetCashflowSubCategoryModel cashflowSubCategory, CurrencyDto currency, bool isShowSubCategoryLabel) : this(cashflowType, type)
        {
            CashflowType = cashflowType;
            Type = type;
            TypeName = type.ToDescriptionString();
            CashflowCategory = cashflowCategory;
            CashflowSubCategory = cashflowSubCategory;
            Currency = currency;
            IsSubCategory = true;
            IsShowSubCategoryLabel = isShowSubCategoryLabel;
            Items = new List<BudgetCashflowDivisionUnitItemDto>();
        }

        public BudgetCashflowTypeModel CashflowType { get; private set; }
        public string SummaryLabel { get; private set; }
        public bool IsUseSection { get; private set; }
        public int SectionRows { get; private set; }
        public string TypeName { get; private set; }
        public CashType Type { get; private set; }
        public bool IsUseGroup { get; private set; }
        public int GroupRows { get; private set; }
        public bool IsLabelOnly { get; private set; }
        public BudgetCashflowCategoryModel CashflowCategory { get; private set; }
        public BudgetCashflowSubCategoryModel CashflowSubCategory { get; private set; }
        public CurrencyDto Currency { get; private set; }
        public bool IsSummary { get; private set; }
        public bool IsSubCategory { get; private set; }
        public bool IsShowSummaryLabel { get; private set; }
        public bool IsShowSubCategoryLabel { get; private set; }
        public List<BudgetCashflowDivisionUnitItemDto> Items { get; private set; }
        public bool IsRowSummary { get; private set; }
        public double DivisionCurrencyNominalTotal { get; private set; }
        public double DivisionNominalTotal { get; private set; }
        public double DivisionActualTotal { get; private set; }
        public bool IsDifference { get; private set; }
        public bool IsShowDifferenceLabel { get; private set; }
        public string DifferenceLabel { get; private set; }

        public void SetSection(int sectionRows)
        {
            IsUseSection = true;
            SectionRows = sectionRows;
        }

        public void SetGroup(CashType type, int groupRows)
        {
            TypeName = type.ToDescriptionString();
            Type = type;
            IsUseGroup = true;
            GroupRows = groupRows;
        }

        public void SetLabelOnly(BudgetCashflowCategoryModel cashflowCategory)
        {
            IsLabelOnly = true;
            CashflowCategory = cashflowCategory;
        }

        public void SetSubCategory(BudgetCashflowSubCategoryModel cashflowSubCategory, CurrencyDto currency, bool isShowSubCategoryLabel)
        {
            CashflowSubCategory = cashflowSubCategory;
            Currency = currency;
            IsSubCategory = true;
            IsShowSubCategoryLabel = isShowSubCategoryLabel;
        }

        public void SetRowSummary(double divisionCurrencyNominalTotal, double divisionNominalTotal, double divisionActualTotal)
        {
            DivisionCurrencyNominalTotal = divisionCurrencyNominalTotal;
            DivisionNominalTotal = divisionNominalTotal;
            DivisionActualTotal = divisionActualTotal;
        }

        public void InitializeItems()
        {
            Items = new List<BudgetCashflowDivisionUnitItemDto>();
        }
    }
}