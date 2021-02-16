﻿namespace ErpNet.FP.Core.Drivers
{
    using System;
    using System.Collections.Generic;
    using System.Globalization;
    using ErpNet.FP.Core.Configuration;

    /// <summary>
    /// Fiscal printer using the ISL implementation.
    /// </summary>
    /// <seealso cref="ErpNet.FP.BgFiscalPrinter" />
    public abstract partial class BgIslFiscalPrinter : BgFiscalPrinter
    {
        protected BgIslFiscalPrinter(
            IChannel channel, 
            ServiceOptions serviceOptions, 
            IDictionary<string, string>? options = null)
        : base(channel, serviceOptions, options) { }

        public override DeviceStatusWithDateTime CheckStatus()
        {
            var (dateTime, status) = GetDateTime();
            var statusEx = new DeviceStatusWithDateTime(status);
            if (dateTime.HasValue)
            {
                statusEx.DeviceDateTime = dateTime.Value;
            }
            else
            {
                statusEx.AddInfo("Error occured while reading current status");
                statusEx.AddError("E409", "Cannot read current date and time");
            }
            return statusEx;
        }

        public override DeviceStatusWithCashAmount Cash(Credentials credentials)
        {
            var (response, status) = Request(CommandMoneyTransfer, "0");
            var statusEx = new DeviceStatusWithCashAmount(status);
            var commaFields = response.Split(',');
            if (commaFields.Length != 4)
            {
                statusEx.AddInfo("Error occured while reading cash amount");
                statusEx.AddError("E409", "Invalid format");
            }
            else
            {
                var amountString = commaFields[1];
                if (amountString.Contains("."))
                {
                    statusEx.Amount = decimal.Parse(amountString, CultureInfo.InvariantCulture);
                }
                else
                {
                    statusEx.Amount = decimal.Parse(amountString, CultureInfo.InvariantCulture) / 100m;
                }
            }
            return statusEx;
        }

        public override DeviceStatus SetDateTime(CurrentDateTime currentDateTime)
        {
            var (_, status) = SetDeviceDateTime(currentDateTime.DeviceDateTime);
            return status;
        }

        public override string GetTaxGroupText(TaxGroup taxGroup)
        {
            return taxGroup switch
            {
                TaxGroup.TaxGroup1 => "А",
                TaxGroup.TaxGroup2 => "Б",
                TaxGroup.TaxGroup3 => "В",
                TaxGroup.TaxGroup4 => "Г",
                TaxGroup.TaxGroup5 => "Д",
                TaxGroup.TaxGroup6 => "Е",
                TaxGroup.TaxGroup7 => "Ж",
                TaxGroup.TaxGroup8 => "З",
                _ => throw new StandardizedStatusMessageException($"Tax group {taxGroup} unsupported", "E411"),
            };
        }

        public override DeviceStatus PrintMoneyDeposit(TransferAmount transferAmount)
        {
            var (_, status) = MoneyTransfer(transferAmount.Amount);
            return status;
        }

        public override DeviceStatus PrintMoneyWithdraw(TransferAmount transferAmount)
        {
            if (transferAmount.Amount < 0m)
            {
                throw new StandardizedStatusMessageException("Withdraw amount must be positive number", "E403");
            }
            var (_, status) = MoneyTransfer(-transferAmount.Amount);
            return status;
        }

        public virtual (ReceiptInfo, DeviceStatus) PrintReceiptBody(Receipt receipt)
        {
            var receiptInfo = new ReceiptInfo();

            var deviceStatus = new DeviceStatus();
            
            uint itemNumber = 0;
            // Receipt items
            if (receipt.Items != null) foreach (var item in receipt.Items)
                {
                    itemNumber++;
                    if (item.Type == ItemType.Sale)
                    {
                        try
                        {
                            (_, deviceStatus) = AddItem(
                                item.Department,
                                item.Text,
                                item.UnitPrice,
                                item.TaxGroup,
                                item.Quantity,
                                item.PriceModifierValue,
                                item.PriceModifierType);
                        }
                        catch (StandardizedStatusMessageException e)
                        {
                            deviceStatus = new DeviceStatus();
                            deviceStatus.AddError(e.Code, e.Message);
                            break;
                        }
                    }
                    if (!deviceStatus.Ok)
                    {
                        deviceStatus.AddInfo($"Error occurred in Item {itemNumber}");
                        return (receiptInfo, deviceStatus);
                    }
                }

            // Receipt payments
            if (receipt.Payments == null || receipt.Payments.Count == 0)
            {
                (_, deviceStatus) = FullPayment();
                if (!deviceStatus.Ok)
                {
                    deviceStatus.AddInfo($"Error occurred while making full payment in cash");
                    return (receiptInfo, deviceStatus);
                }
            }
            string closeReceiptResponse;
            (closeReceiptResponse, deviceStatus) = CloseReceipt();
            if (!deviceStatus.Ok)
            {
                AbortReceipt();
                deviceStatus.AddInfo($"Error occurred while closing the receipt");
                return (receiptInfo, deviceStatus);
            }

            var fiscalMemorySerialNumber = string.Empty;
            (fiscalMemorySerialNumber, deviceStatus) = GetFiscalMemorySerialNumber();
            if (deviceStatus.Ok && !string.IsNullOrEmpty(fiscalMemorySerialNumber))
            {
                receiptInfo.FiscalMemorySerialNumber = fiscalMemorySerialNumber;
            }

            // Get the receipt date and time (current fiscal device date and time)
            DateTime? dateTime;
            (dateTime, deviceStatus) = GetDateTime();
            if (dateTime.HasValue && deviceStatus.Ok)
            {
                receiptInfo.ReceiptDateTime = dateTime.Value;
            }

            // Get receipt amount
            decimal? receiptAmount;
            (receiptAmount, deviceStatus) = GetReceiptAmount();
            if (receiptAmount.HasValue && deviceStatus.Ok)
            {
                receiptInfo.ReceiptAmount = receiptAmount.Value;
            }

            // Get receipt number
            string lastDocumentNumberResponse;
            (lastDocumentNumberResponse, deviceStatus) = GetLastDocumentNumber(closeReceiptResponse);
            if (deviceStatus.Ok && !string.IsNullOrWhiteSpace(lastDocumentNumberResponse))
            {
                receiptInfo.ReceiptNumber = lastDocumentNumberResponse;
            }

            return (receiptInfo, deviceStatus);
        }

        public override (ReceiptInfo, DeviceStatus) PrintReversalReceipt(ReversalReceipt reversalReceipt)
        {
            var receiptInfo = new ReceiptInfo();

            // Abort all unfinished or erroneus receipts
            AbortReceipt();

            // Receipt header
            var (_, deviceStatus) = OpenReversalReceipt(
                reversalReceipt.Reason,
                reversalReceipt.ReceiptNumber,
                reversalReceipt.ReceiptDateTime,
                reversalReceipt.FiscalMemorySerialNumber,
                reversalReceipt.UniqueSaleNumber,
                reversalReceipt.Operator,
                reversalReceipt.OperatorPassword);
            if (!deviceStatus.Ok)
            {
                AbortReceipt();
                deviceStatus.AddInfo($"Error occured while opening new fiscal reversal receipt");
                return (receiptInfo, deviceStatus);
            }

            (receiptInfo, deviceStatus) = PrintReceiptBody(reversalReceipt);
            if (!deviceStatus.Ok)
            {
                AbortReceipt();
                deviceStatus.AddInfo($"Error occured while printing receipt items");
            }

            return (receiptInfo, deviceStatus);
        }

        public override (ReceiptInfo, DeviceStatus) PrintReceipt(Receipt receipt)
        {
            var receiptInfo = new ReceiptInfo();

            // Abort all unfinished or erroneus receipts
            //AbortReceipt();

            // Opening receipt
            var (_, deviceStatus) = OpenReceipt(
                receipt.UniqueSaleNumber,
                receipt.Operator,
                receipt.OperatorPassword
            );
            if (!deviceStatus.Ok)
            {
                AbortReceipt();
                deviceStatus.AddInfo($"Error occured while opening new fiscal receipt");
                return (receiptInfo, deviceStatus);
            }

            // Printing receipt's body
            (receiptInfo, deviceStatus) = PrintReceiptBody(receipt);
            if (!deviceStatus.Ok)
            {
                AbortReceipt();
                deviceStatus.AddInfo($"Error occured while printing receipt items");
            }

            return (receiptInfo, deviceStatus);
        }

        public override DeviceStatus PrintZReport(Credentials credentials)
        {
            var (_, status) = PrintDailyReport(true);
            return status;
        }

        public override DeviceStatus PrintXReport(Credentials credentials)
        {
            var (_, status) = PrintDailyReport(false);
            return status;
        }

        public override DeviceStatus PrintDuplicate(Credentials credentials)
        {
            var (_, status) = Request(CommandPrintLastReceiptDuplicate, "1");
            return status;
        }

        public override DeviceStatusWithDateTime Reset(Credentials credentials)
        {
            AbortReceipt();
            FullPayment();
            CloseReceipt();
            return CheckStatus();
        }
    }
}
