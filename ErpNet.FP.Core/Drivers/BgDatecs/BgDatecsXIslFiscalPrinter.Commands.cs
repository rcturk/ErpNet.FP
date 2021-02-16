﻿namespace ErpNet.FP.Core.Drivers.BgDatecs
{
    using System;
    using System.Collections.Generic;
    using System.Globalization;

    /// <summary>
    /// Fiscal printer using the ISL implementation of Datecs Bulgaria.
    /// </summary>
    /// <seealso cref="ErpNet.FP.Drivers.BgIslFiscalPrinter" />
    public partial class BgDatecsXIslFiscalPrinter : BgIslFiscalPrinter
    {
        protected const byte
           DatecsXCommandOpenStornoDocument = 0x2b;

        public override IDictionary<PaymentType, string> GetPaymentTypeMappings()
        {
            var paymentTypeMappings = new Dictionary<PaymentType, string> {
                { PaymentType.Cash,          "0" },
                { PaymentType.Check,         "3" },
                { PaymentType.Coupons,       "5" },
                { PaymentType.ExtCoupons,    "4" },
                { PaymentType.Card,          "1" }
            };
            ServiceOptions.RemapPaymentTypes(Info.SerialNumber, paymentTypeMappings);
            return paymentTypeMappings;
        }

        public override string GetTaxGroupText(TaxGroup taxGroup)
        {
            return taxGroup switch
            {
                TaxGroup.TaxGroup1 => "1",
                TaxGroup.TaxGroup2 => "2",
                TaxGroup.TaxGroup3 => "3",
                TaxGroup.TaxGroup4 => "4",
                TaxGroup.TaxGroup5 => "5",
                TaxGroup.TaxGroup6 => "6",
                TaxGroup.TaxGroup7 => "7",
                TaxGroup.TaxGroup8 => "8",
                _ => throw new StandardizedStatusMessageException($"Tax group {taxGroup} unsupported", "E411"),
            };
        }

        public override (string, DeviceStatus) SubtotalChangeAmount(Decimal amount)
        {
            // {Print}<SEP>{Display}<SEP>{DiscountType}<SEP>{DiscountValue}<SEP>
            return Request(CommandSubtotal, string.Join("\t",
                "1",
                "0",
                amount < 0 ? "4" : "3",
                Math.Abs(amount).ToString("F2", CultureInfo.InvariantCulture),
                ""));
        }

        public override (string, DeviceStatus) SetDeviceDateTime(DateTime dateTime)
        {
            return Request(CommandSetDateTime, dateTime.ToString("dd-MM-yy HH:mm:ss\t", CultureInfo.InvariantCulture));
        }

        public override DeviceStatusWithCashAmount Cash(Credentials credentials)
        {
            var (response, status) = Request(CommandMoneyTransfer, "0\t0\t");
            var statusEx = new DeviceStatusWithCashAmount(status);
            var tabFields = response.Split('\t');
            if (tabFields.Length != 5)
            {
                statusEx.AddInfo("Error occured while reading cash amount");
                statusEx.AddError("E409", "Invalid format");
            }
            else
            {
                var amountString = tabFields[1];
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

        public override (string, DeviceStatus) GetTaxIdentificationNumber()
        {
            var (response, deviceStatus) = Request(CommandGetTaxIdentificationNumber);
            var commaFields = response.Split('\t');
            if (commaFields.Length == 2)
            {
                return (commaFields[1].Trim(), deviceStatus);
            }
            return (string.Empty, deviceStatus);
        }

        public override (decimal?, DeviceStatus) GetReceiptAmount()
        {
            decimal? receiptAmount = null;

            var (receiptStatusResponse, deviceStatus) = Request(CommandGetReceiptStatus);
            if (!deviceStatus.Ok)
            {
                deviceStatus.AddInfo($"Error occured while reading last receipt status");
                return (null, deviceStatus);
            }

            var fields = receiptStatusResponse.Split('\t');
            if (fields.Length < 8)
            {
                deviceStatus.AddInfo($"Error occured while parsing last receipt status");
                deviceStatus.AddError("E409", "Wrong format of receipt status");
                return (null, deviceStatus);
            }

            try
            {
                var amountString = fields[7];
                if (amountString.Length > 0)
                {
                    receiptAmount = (amountString[0]) switch
                    {
                        '+' => decimal.Parse(amountString.Substring(1), System.Globalization.CultureInfo.InvariantCulture) / 100m,
                        '-' => -decimal.Parse(amountString.Substring(1), System.Globalization.CultureInfo.InvariantCulture) / 100m,
                        _ => decimal.Parse(amountString, System.Globalization.CultureInfo.InvariantCulture),
                    };
                }
            }
            catch (Exception e)
            {
                deviceStatus = new DeviceStatus();
                deviceStatus.AddInfo($"Error occured while parsing the amount of last receipt status");
                deviceStatus.AddError("E409", e.Message);
                return (null, deviceStatus);
            }

            return (receiptAmount, deviceStatus);
        }

        public override (System.DateTime?, DeviceStatus) GetDateTime()
        {
            var (dateTimeResponse, deviceStatus) = Request(CommandGetDateTime);
            if (!deviceStatus.Ok)
            {
                deviceStatus.AddInfo($"Error occured while reading current date and time");
                return (null, deviceStatus);
            }

            var fields = dateTimeResponse.Split('\t');
            if (fields.Length < 2)
            {
                deviceStatus.AddInfo($"Error occured while parsing date and time");
                deviceStatus.AddError("E409", "Wrong format of date and time");
                return (null, deviceStatus);
            }

            var fixedDateAndTimeString = fields[1].Replace(" DST", "");

            try
            {
                var dateTime = DateTime.ParseExact(fixedDateAndTimeString,
                    "dd-MM-yy HH:mm:ss",
                    CultureInfo.InvariantCulture);
                return (dateTime, deviceStatus);
            }
            catch
            {
                deviceStatus.AddInfo($"Error occured while parsing current date and time");
                deviceStatus.AddError("E409", $"Wrong format of date and time");
                return (null, deviceStatus);
            }
        }

        public override (string, DeviceStatus) GetLastDocumentNumber(string closeReceiptResponse)
        {
            var deviceStatus = new DeviceStatus();
            var fields = closeReceiptResponse.Split('\t');
            if (fields.Length < 2)
            {
                deviceStatus.AddInfo($"Error occured while parsing close receipt response");
                deviceStatus.AddError("E409", $"Wrong format of close receipt response");
                return (string.Empty, deviceStatus);
            }
            return (fields[1], deviceStatus);
        }

        public override (string, DeviceStatus) MoneyTransfer(decimal amount)
        {
            // Protocol: {Type}<SEP>{Amount}<SEP>
            return Request(CommandMoneyTransfer, string.Join("\t",
                amount < 0 ? "1" : "0",
                Math.Abs(amount).ToString("F2", CultureInfo.InvariantCulture),
                ""));
        }

        public override (string, DeviceStatus) AddItem(
            int department,
            string itemText,
            decimal unitPrice,
            TaxGroup taxGroup,
            decimal quantity = 0m,
            decimal priceModifierValue = 0m,
            PriceModifierType priceModifierType = PriceModifierType.None)
        {
            string PriceModifierTypeToProtocolValue()
            {
                return priceModifierType switch
                {
                    PriceModifierType.None => "0",
                    PriceModifierType.DiscountPercent => "2",
                    PriceModifierType.DiscountAmount => "4",
                    PriceModifierType.SurchargePercent => "1",
                    PriceModifierType.SurchargeAmount => "3",
                    _ => "",
                };
            }

            // Protocol: {PluName}<SEP>{TaxCd}<SEP>{Price}<SEP>{Quantity}<SEP>{DiscountType}<SEP>{DiscountValue}<SEP>{Department}<SEP>
            var itemData = string.Join("\t",
                itemText.WithMaxLength(Info.ItemTextMaxLength),
                GetTaxGroupText(taxGroup),
                unitPrice.ToString("F2", CultureInfo.InvariantCulture),
                quantity == 0m ? string.Empty : quantity.ToString(CultureInfo.InvariantCulture),
                PriceModifierTypeToProtocolValue(),
                priceModifierValue.ToString("F2", CultureInfo.InvariantCulture),
                department.ToString(),
                "buc.", "");

            return Request(CommandFiscalReceiptSale, itemData);
        }

        public override (string, DeviceStatus) AddComment(string text)
        {
            return Request(
                CommandFiscalReceiptComment,
                text.WithMaxLength(Info.CommentTextMaxLength) + "\t"
            );
        }

        public override (string, DeviceStatus) FullPayment()
        {
            return Request(CommandFiscalReceiptTotal, "0\t\t");
        }

        public override (string, DeviceStatus) AddPayment(decimal amount, PaymentType paymentType)
        {
            // Protocol: {PaidMode}<SEP>{Amount}<SEP>{Type}<SEP>
            var paymentData = string.Join("\t",
                GetPaymentTypeText(paymentType),
                amount.ToString("F2", CultureInfo.InvariantCulture),
                "1",
                "");

            return Request(CommandFiscalReceiptTotal, paymentData);
        }

        public override string GetReversalReasonText(ReversalReason reversalReason)
        {
            return reversalReason switch
            {
                ReversalReason.OperatorError => "0",
                ReversalReason.Refund => "1",
                ReversalReason.TaxBaseReduction => "2",
                _ => "0",
            };
        }

        public override (string, DeviceStatus) OpenReceipt(
            string uniqueSaleNumber,
            string operatorId,
            string operatorPassword)
        {
            string header;

            header = string.Join("\t",
                new string[] {
                    String.IsNullOrEmpty(operatorId) ?
                        Options.ValueOrDefault("Operator.ID", "1")
                        :
                        operatorId,
                    String.IsNullOrEmpty(operatorId) ?
                        Options.ValueOrDefault("Operator.Password", "0001").WithMaxLength(Info.OperatorPasswordMaxLength)
                        :
                        operatorPassword,
                    "1",
                    "",
                    "",
                    ""
                });

            return Request(CommandOpenFiscalReceipt, header);
        }

        public override (string, DeviceStatus) OpenReversalReceipt(
            ReversalReason reason,
            string receiptNumber,
            System.DateTime receiptDateTime,
            string fiscalMemorySerialNumber,
            string uniqueSaleNumber,
            string operatorId,
            string operatorPassword)
        {
            // Protocol: {OpCode}<SEP>{OpPwd}<SEP>{TillNmb}<SEP>{Storno}<SEP>{DocNum}<SEP>{DateTime}<SEP>
            //           {FM Number}<SEP>{Invoice}<SEP>{ToInvoice}<SEP>{Reason}<SEP>{NSale}<SEP>
            var headerData = string.Join("\t",
                String.IsNullOrEmpty(operatorId) ?
                    Options.ValueOrDefault("Operator.ID", "1")
                    :
                    operatorId,
                String.IsNullOrEmpty(operatorId) ?
                    Options.ValueOrDefault("Operator.Password", "0000").WithMaxLength(Info.OperatorPasswordMaxLength)
                    :
                    operatorPassword,
                "1",
                GetReversalReasonText(reason),
                receiptNumber,
                receiptDateTime.ToString("dd-MM-yy HH:mm:ss", CultureInfo.InvariantCulture),
                fiscalMemorySerialNumber,
                "",
                "",
                "",
                uniqueSaleNumber,
                "");

            return Request(DatecsXCommandOpenStornoDocument, headerData.ToString());
        }

        public override (string, DeviceStatus) PrintDailyReport(bool zeroing = true)
        {
            if (zeroing)
            {
                return Request(CommandPrintDailyReport, "Z\t");
            }
            else
            {
                return Request(CommandPrintDailyReport, "X\t");
            }
        }

        // 8 Bytes x 8 bits

        protected static readonly (string?, string, StatusMessageType)[] StatusBitsStrings = new (string?, string, StatusMessageType)[] {
            ("E401", "Syntax error", StatusMessageType.Error),
            ("E402", "Command code is invalid", StatusMessageType.Error),
            ("E103", "The real time clock is not synchronized", StatusMessageType.Error),
            (null, string.Empty, StatusMessageType.Reserved),
            ("E303", "Failure in printing mechanism", StatusMessageType.Error),
            ("E199", "General error", StatusMessageType.Error),
            ("E302", "Cover is open", StatusMessageType.Error),
            (null, string.Empty, StatusMessageType.Reserved),

            ("E403", "Overflow during command execution", StatusMessageType.Error),
            ("E404", "Command is not permitted", StatusMessageType.Error),
            (null, string.Empty, StatusMessageType.Reserved),
            (null, string.Empty, StatusMessageType.Reserved),
            (null, string.Empty, StatusMessageType.Reserved),
            (null, string.Empty, StatusMessageType.Reserved),
            (null, string.Empty, StatusMessageType.Reserved),
            (null, string.Empty, StatusMessageType.Reserved),

            ("E301", "End of paper", StatusMessageType.Error),
            ("W301", "Near paper end", StatusMessageType.Warning),
            ("E206", "EJ is full", StatusMessageType.Error),
            (null, "Fiscal receipt is open", StatusMessageType.Info),
            ("W202", "EJ nearly full", StatusMessageType.Warning),
            (null, "Nonfiscal receipt is open", StatusMessageType.Info),
            (null, string.Empty, StatusMessageType.Reserved),
            (null, string.Empty, StatusMessageType.Reserved),

            (null, string.Empty, StatusMessageType.Reserved),
            (null, string.Empty, StatusMessageType.Reserved),
            (null, string.Empty, StatusMessageType.Reserved),
            (null, string.Empty, StatusMessageType.Reserved),
            (null, string.Empty, StatusMessageType.Reserved),
            (null, string.Empty, StatusMessageType.Reserved),
            (null, string.Empty, StatusMessageType.Reserved),
            (null, string.Empty, StatusMessageType.Reserved),

            ("E203", "Error when trying to access data stored in the FM", StatusMessageType.Error),
            (null, "Tax number is set", StatusMessageType.Info),
            (null, "Serial number and number of FM are set", StatusMessageType.Info),
            ("W201", "There is space for less then 60 reports in Fiscal memory", StatusMessageType.Warning),
            ("E201", "FM full", StatusMessageType.Error),
            ("E299", "FM general error", StatusMessageType.Error),
            ("E205", "Fiscal memory is not found or damaged", StatusMessageType.Error),
            (null, string.Empty, StatusMessageType.Reserved),

            (null, string.Empty, StatusMessageType.Reserved),
            (null, "FM is formatted", StatusMessageType.Info),
            (null, string.Empty, StatusMessageType.Reserved),
            (null, "Device is fiscalized", StatusMessageType.Info),
            (null, "VAT are set at least once", StatusMessageType.Info),
            (null, string.Empty, StatusMessageType.Reserved),
            (null, string.Empty, StatusMessageType.Reserved),
            (null, string.Empty, StatusMessageType.Reserved),

            (null, string.Empty, StatusMessageType.Reserved),
            (null, string.Empty, StatusMessageType.Reserved),
            (null, string.Empty, StatusMessageType.Reserved),
            (null, string.Empty, StatusMessageType.Reserved),
            (null, string.Empty, StatusMessageType.Reserved),
            (null, string.Empty, StatusMessageType.Reserved),
            (null, string.Empty, StatusMessageType.Reserved),
            (null, string.Empty, StatusMessageType.Reserved),

            (null, string.Empty, StatusMessageType.Reserved),
            (null, string.Empty, StatusMessageType.Reserved),
            (null, string.Empty, StatusMessageType.Reserved),
            (null, string.Empty, StatusMessageType.Reserved),
            (null, string.Empty, StatusMessageType.Reserved),
            (null, string.Empty, StatusMessageType.Reserved),
            (null, string.Empty, StatusMessageType.Reserved),
            (null, string.Empty, StatusMessageType.Reserved)
        };

        protected override DeviceStatus ParseStatus(byte[]? status)
        {
            var deviceStatus = new DeviceStatus();
            if (status == null)
            {
                return deviceStatus;
            }
            for (var i = 0; i < status.Length; i++)
            {
                byte mask = 0b10000000;
                byte b = status[i];
                for (var j = 0; j < 8; j++)
                {
                    if ((mask & b) != 0)
                    {
                        var (statusBitsCode, statusBitsText, statusBitStringType) = StatusBitsStrings[i * 8 + (7 - j)];
                        deviceStatus.AddMessage(new StatusMessage
                        {
                            Type = statusBitStringType,
                            Code = statusBitsCode,
                            Text = statusBitsText
                        });
                    }
                    mask >>= 1;
                }
            }
            return deviceStatus;
        }

    }


}
