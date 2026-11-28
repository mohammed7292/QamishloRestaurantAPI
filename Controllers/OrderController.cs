using Microsoft.AspNetCore.Mvc;
using MySqlConnector;
using QamishloRestaurantAPI.Database;
using QamishloRestaurantAPI.Services;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;

namespace QamishloRestaurantAPI.Controllers
{
    [ApiController]
    [Route("api/[controller]")]
    public class ordersController : ControllerBase
    {
        private static DateTime _lastAutoArchiveUtc = DateTime.MinValue;
        private readonly FirebaseNotificationService _firebaseNotifications;

        public ordersController(FirebaseNotificationService firebaseNotifications)
        {
            _firebaseNotifications = firebaseNotifications;
        }

        // =========================================================
        // CREATE ORDER
        // POST: /api/orders
        // =========================================================

        [HttpPost]
        public async Task<IActionResult> CreateOrder(
            [FromBody] CreateOrderRequest request)
        {
            if (request == null)
            {
                return BadRequest(new
                {
                    success = false,
                    message = "Invalid order data."
                });
            }

            if (request.Items == null ||
                request.Items.Count == 0)
            {
                return BadRequest(new
                {
                    success = false,
                    message = "Order must contain at least one product."
                });
            }

            string orderType = NormalizeOrderType(request.OrderType);

            if (orderType == "")
            {
                return BadRequest(new
                {
                    success = false,
                    message = "Invalid order type. Use Dine In, Takeaway or Delivery."
                });
            }

            if (orderType == "Delivery")
            {
                if (!request.Latitude.HasValue ||
                    !request.Longitude.HasValue)
                {
                    return BadRequest(new
                    {
                        success = false,
                        message = "Delivery location is required."
                    });
                }
            }

            foreach (OrderItemRequest item in request.Items)
            {
                if (item.ProductID <= 0)
                {
                    return BadRequest(new
                    {
                        success = false,
                        message = "Invalid product ID."
                    });
                }

                if (item.Quantity <= 0)
                {
                    return BadRequest(new
                    {
                        success = false,
                        message = "Product quantity must be greater than zero."
                    });
                }

                if (item.Quantity > 100)
                {
                    return BadRequest(new
                    {
                        success = false,
                        message = "Product quantity cannot exceed 100."
                    });
                }
            }

            decimal discount = 0m;

            try
            {
                using MySqlConnection connection = DatabaseManager.GetConnection();
                await connection.OpenAsync();
                using MySqlTransaction transaction = await connection.BeginTransactionAsync();

                try
                {
                    // 0. VALIDATE RESTAURANT / CUSTOMER APP AVAILABILITY
                    const string availabilityQuery = @"
                        SELECT IsCustomerAppEnabled, IsRestaurantOpen, IsDeliveryEnabled,
                               OpeningTime, ClosingTime, DeliveryClosedMessage,
                               FirstOrderDiscountEnabled, FirstOrderDiscountPercent,
                               COALESCE(FirstOrderMinOrderAmount, 0) AS FirstOrderMinOrderAmount,
                               COALESCE(FirstOrderMaxOrderAmount, 0) AS FirstOrderMaxOrderAmount,
                               COALESCE(FirstOrderMaxDiscountAmount, 0) AS FirstOrderMaxDiscountAmount,
                               COALESCE(FirstOrderAllowedOrderTypes, 'All') AS FirstOrderAllowedOrderTypes
                        FROM restaurantsettings
                        ORDER BY SettingID
                        LIMIT 1;";
                    bool customerAppEnabled = true;
                    bool restaurantOpen = true;
                    bool deliveryEnabled = true;
                    string? openingTime = null;
                    string? closingTime = null;
                    string? closedMessage = null;
                    bool firstOrderDiscountEnabled = false;
                    decimal firstOrderDiscountPercent = 0m;
                    decimal firstOrderMinOrderAmount = 0m;
                    decimal firstOrderMaxOrderAmount = 0m;
                    decimal firstOrderMaxDiscountAmount = 0m;
                    string firstOrderAllowedOrderTypes = "All";
                    using (MySqlCommand settingsCommand = new MySqlCommand(availabilityQuery, connection, transaction))
                    using (MySqlDataReader settingsReader = await settingsCommand.ExecuteReaderAsync())
                    {
                        if (await settingsReader.ReadAsync())
                        {
                            customerAppEnabled = Convert.ToBoolean(settingsReader["IsCustomerAppEnabled"]);
                            restaurantOpen = Convert.ToBoolean(settingsReader["IsRestaurantOpen"]);
                            deliveryEnabled = Convert.ToBoolean(settingsReader["IsDeliveryEnabled"]);
                            openingTime = settingsReader.IsDBNull(settingsReader.GetOrdinal("OpeningTime")) ? null : settingsReader["OpeningTime"].ToString();
                            closingTime = settingsReader.IsDBNull(settingsReader.GetOrdinal("ClosingTime")) ? null : settingsReader["ClosingTime"].ToString();
                            closedMessage = settingsReader.IsDBNull(settingsReader.GetOrdinal("DeliveryClosedMessage")) ? null : settingsReader["DeliveryClosedMessage"].ToString();
                            firstOrderDiscountEnabled = Convert.ToBoolean(settingsReader["FirstOrderDiscountEnabled"]);
                            firstOrderDiscountPercent = Convert.ToDecimal(settingsReader["FirstOrderDiscountPercent"]);
                            firstOrderMinOrderAmount = Convert.ToDecimal(settingsReader["FirstOrderMinOrderAmount"]);
                            firstOrderMaxOrderAmount = Convert.ToDecimal(settingsReader["FirstOrderMaxOrderAmount"]);
                            firstOrderMaxDiscountAmount = Convert.ToDecimal(settingsReader["FirstOrderMaxDiscountAmount"]);
                            firstOrderAllowedOrderTypes = settingsReader["FirstOrderAllowedOrderTypes"]?.ToString() ?? "All";
                        }
                    }

                    if (!customerAppEnabled)
                    {
                        await transaction.RollbackAsync();
                        return StatusCode(503, new
                        {
                            success = false,
                            code = "CUSTOMER_APP_DISABLED",
                            message = string.IsNullOrWhiteSpace(closedMessage)
                                ? $"The customer app is temporarily unavailable. Service hours: {openingTime ?? "--:--"} - {closingTime ?? "--:--"}."
                                : closedMessage,
                            openingTime,
                            closingTime
                        });
                    }

                    if (!restaurantOpen)
                    {
                        await transaction.RollbackAsync();
                        return BadRequest(new
                        {
                            success = false,
                            code = "RESTAURANT_CLOSED",
                            message = string.IsNullOrWhiteSpace(closedMessage)
                                ? $"The restaurant is currently closed. Service hours: {openingTime ?? "--:--"} - {closingTime ?? "--:--"}."
                                : closedMessage,
                            openingTime,
                            closingTime
                        });
                    }

                    if (orderType == "Delivery" && !deliveryEnabled)
                    {
                        await transaction.RollbackAsync();
                        return BadRequest(new
                        {
                            success = false,
                            code = "DELIVERY_DISABLED",
                            message = string.IsNullOrWhiteSpace(closedMessage)
                                ? $"Delivery is currently unavailable. Available from {openingTime ?? "--:--"} to {closingTime ?? "--:--"}."
                                : closedMessage,
                            openingTime,
                            closingTime
                        });
                    }

                    if (orderType == "Delivery" &&
                        TimeSpan.TryParse(openingTime, out TimeSpan deliveryOpens) &&
                        TimeSpan.TryParse(closingTime, out TimeSpan deliveryCloses))
                    {
                        TimeSpan baghdadTime = ExpensesController.BaghdadNow().TimeOfDay;
                        bool withinHours = deliveryOpens <= deliveryCloses
                            ? baghdadTime >= deliveryOpens && baghdadTime <= deliveryCloses
                            : baghdadTime >= deliveryOpens || baghdadTime <= deliveryCloses;
                        if (!withinHours)
                        {
                            await transaction.RollbackAsync();
                            return BadRequest(new
                            {
                                success = false,
                                code = "OUTSIDE_DELIVERY_HOURS",
                                message = string.IsNullOrWhiteSpace(closedMessage)
                                    ? $"Delivery is available from {openingTime} to {closingTime}."
                                    : closedMessage,
                                openingTime,
                                closingTime
                            });
                        }
                    }

                    // 1. FIND OR CREATE CUSTOMER
                    int? customerId = null;
                    string customerName = request.CustomerName?.Trim() ?? "";
                    string customerPhone = request.CustomerPhone?.Trim() ?? "";

                    if (!string.IsNullOrWhiteSpace(customerPhone))
                    {
                        const string findCustomerQuery = @"
                            SELECT CustomerID FROM customers
                            WHERE Phone = @Phone LIMIT 1;";
                        using (var findCommand = new MySqlCommand(findCustomerQuery, connection, transaction))
                        {
                            findCommand.Parameters.AddWithValue("@Phone", customerPhone);
                            object? found = await findCommand.ExecuteScalarAsync();
                            if (found != null && found != DBNull.Value)
                                customerId = Convert.ToInt32(found);
                        }

                        if (!customerId.HasValue)
                        {
                            const string insertCustomerQuery = @"
                                INSERT INTO customers(FullName,Phone,Address,Notes,IsActive)
                                VALUES(@FullName,@Phone,@Address,@Notes,1);";
                            using var insertCustomer = new MySqlCommand(insertCustomerQuery, connection, transaction);
                            insertCustomer.Parameters.AddWithValue("@FullName", string.IsNullOrWhiteSpace(customerName) ? "Customer" : customerName);
                            insertCustomer.Parameters.AddWithValue("@Phone", customerPhone);
                            insertCustomer.Parameters.AddWithValue("@Address", string.IsNullOrWhiteSpace(request.Address) ? DBNull.Value : request.Address.Trim());
                            insertCustomer.Parameters.AddWithValue("@Notes", string.IsNullOrWhiteSpace(request.Notes) ? DBNull.Value : request.Notes.Trim());
                            await insertCustomer.ExecuteNonQueryAsync();
                            customerId = Convert.ToInt32(insertCustomer.LastInsertedId);
                        }
                        else if (!string.IsNullOrWhiteSpace(customerName))
                        {
                            const string updateCustomerQuery = @"
                                UPDATE customers SET FullName=@FullName,Address=@Address,Notes=@Notes,IsActive=1
                                WHERE CustomerID=@CustomerID;";
                            using var updateCustomer = new MySqlCommand(updateCustomerQuery, connection, transaction);
                            updateCustomer.Parameters.AddWithValue("@FullName", customerName);
                            updateCustomer.Parameters.AddWithValue("@Address", string.IsNullOrWhiteSpace(request.Address) ? DBNull.Value : request.Address.Trim());
                            updateCustomer.Parameters.AddWithValue("@Notes", string.IsNullOrWhiteSpace(request.Notes) ? DBNull.Value : request.Notes.Trim());
                            updateCustomer.Parameters.AddWithValue("@CustomerID", customerId.Value);
                            await updateCustomer.ExecuteNonQueryAsync();
                        }
                    }
                    else if (!string.IsNullOrWhiteSpace(customerName))
                    {
                        const string insertNamedWalkIn = @"
                            INSERT INTO customers(FullName,Phone,Address,Notes,IsActive)
                            VALUES(@FullName,NULL,@Address,@Notes,1);";
                        using var insertCustomer = new MySqlCommand(insertNamedWalkIn, connection, transaction);
                        insertCustomer.Parameters.AddWithValue("@FullName", customerName);
                        insertCustomer.Parameters.AddWithValue("@Address", string.IsNullOrWhiteSpace(request.Address) ? DBNull.Value : request.Address.Trim());
                        insertCustomer.Parameters.AddWithValue("@Notes", string.IsNullOrWhiteSpace(request.Notes) ? DBNull.Value : request.Notes.Trim());
                        await insertCustomer.ExecuteNonQueryAsync();
                        customerId = Convert.ToInt32(insertCustomer.LastInsertedId);
                    }

                    // 2. CALCULATE SUBTOTAL (including verified server-side product add-ons)
                    decimal subtotal = 0m;
                    List<(decimal UnitPrice, string? AddonsText)> resolvedItemPricing = new List<(decimal, string?)>();

                    foreach (OrderItemRequest item in request.Items)
                    {
                        string priceQuery = @"
                            SELECT Price
                            FROM products
                            WHERE ProductID = @ProductID
                              AND (IsAvailable = 1 OR (AvailableUntil IS NOT NULL AND AvailableUntil <= UTC_TIMESTAMP()))
                            LIMIT 1;";

                        decimal basePrice;
                        using (MySqlCommand command = new MySqlCommand(priceQuery, connection, transaction))
                        {
                            command.Parameters.AddWithValue("@ProductID", item.ProductID);
                            object? result = await command.ExecuteScalarAsync();

                            if (result == null || result == DBNull.Value)
                            {
                                await transaction.RollbackAsync();
                                return BadRequest(new
                                {
                                    success = false,
                                    message = $"Product {item.ProductID} is not available."
                                });
                            }

                            basePrice = Convert.ToDecimal(result);
                        }

                        decimal addonsExtra = 0m;
                        string? resolvedAddonsText = null;
                        if (item.AddonIDs != null && item.AddonIDs.Count > 0)
                        {
                            var distinctAddonIds = item.AddonIDs.Where(id => id > 0).Distinct().ToList();
                            if (distinctAddonIds.Count > 0)
                            {
                                var paramNames = distinctAddonIds.Select((_, idx) => $"@addon{idx}").ToList();
                                string addonsSql = $@"
                                    SELECT AddonName, ExtraPrice
                                    FROM productaddons
                                    WHERE ProductID = @ProductID
                                      AND IsActive = 1
                                      AND AddonID IN ({string.Join(",", paramNames)})
                                    ORDER BY AddonID ASC;";
                                using var addonCmd = new MySqlCommand(addonsSql, connection, transaction);
                                addonCmd.Parameters.AddWithValue("@ProductID", item.ProductID);
                                for (int idx = 0; idx < distinctAddonIds.Count; idx++)
                                    addonCmd.Parameters.AddWithValue(paramNames[idx], distinctAddonIds[idx]);

                                var addonNames = new List<string>();
                                using var addonReader = await addonCmd.ExecuteReaderAsync();
                                while (await addonReader.ReadAsync())
                                {
                                    string nm = addonReader.IsDBNull(0) ? "" : addonReader.GetString(0).Trim();
                                    decimal exPrice = addonReader.IsDBNull(1) ? 0m : addonReader.GetDecimal(1);
                                    if (!string.IsNullOrWhiteSpace(nm))
                                        addonNames.Add(exPrice > 0m ? $"{nm} ({exPrice:0})" : nm);
                                    if (exPrice > 0m)
                                        addonsExtra += exPrice;
                                }
                                if (addonNames.Count > 0)
                                    resolvedAddonsText = string.Join(" + ", addonNames);
                            }
                        }

                        if (string.IsNullOrWhiteSpace(resolvedAddonsText) && !string.IsNullOrWhiteSpace(item.AddonsText))
                            resolvedAddonsText = item.AddonsText.Trim();

                        decimal finalUnitPrice = basePrice + addonsExtra;
                        resolvedItemPricing.Add((finalUnitPrice, resolvedAddonsText));
                        subtotal += finalUnitPrice * item.Quantity;
                    }

                    // 2B. APPLY SERVER-VERIFIED FIRST-ORDER DISCOUNT (ONLY FOR LOGGED-IN REGISTERED ACCOUNTS!)
                    bool isLoggedInRegisteredCustomer = false;
                    if (request.CustomerID.HasValue && request.CustomerID.Value > 0)
                    {
                        customerId = request.CustomerID.Value;
                        const string regChkSql = "SELECT PasswordHash FROM customers WHERE CustomerID = @cid LIMIT 1;";
                        using var regChkCmd = new MySqlCommand(regChkSql, connection, transaction);
                        regChkCmd.Parameters.AddWithValue("@cid", customerId.Value);
                        object? phObj = await regChkCmd.ExecuteScalarAsync();
                        isLoggedInRegisteredCustomer = phObj != null && phObj != DBNull.Value && !string.IsNullOrWhiteSpace(phObj.ToString());
                    }

                    if (isLoggedInRegisteredCustomer && customerId.HasValue && firstOrderDiscountEnabled && firstOrderDiscountPercent > 0m)
                    {
                        const string previousOrdersQuery = @"
                            SELECT COUNT(*) FROM orders
                            WHERE CustomerID = @CustomerID
                              AND Status NOT IN ('Cancelled', 'Rejected');";
                        using (MySqlCommand previousOrdersCommand = new MySqlCommand(previousOrdersQuery, connection, transaction))
                        {
                            previousOrdersCommand.Parameters.AddWithValue("@CustomerID", customerId.Value);
                            int previousOrders = Convert.ToInt32(await previousOrdersCommand.ExecuteScalarAsync());
                            bool orderTypeEligible = firstOrderAllowedOrderTypes switch
                            {
                                "Delivery" => orderType == "Delivery",
                                "Takeaway" => orderType == "Takeaway",
                                _ => orderType == "Delivery" || orderType == "Takeaway"
                            };
                            bool minOrderEligible = firstOrderMinOrderAmount <= 0m || subtotal >= firstOrderMinOrderAmount;
                            bool maxOrderEligible = firstOrderMaxOrderAmount <= 0m || subtotal <= firstOrderMaxOrderAmount;
                            if (previousOrders == 0 && orderTypeEligible && minOrderEligible && maxOrderEligible)
                            {
                                decimal safePercent = Math.Clamp(firstOrderDiscountPercent, 0m, 100m);
                                decimal rawDisc = Math.Round(subtotal * safePercent / 100m, 2, MidpointRounding.AwayFromZero);
                                if (firstOrderMaxDiscountAmount > 0m && rawDisc > firstOrderMaxDiscountAmount)
                                    rawDisc = firstOrderMaxDiscountAmount;
                                discount = Math.Min(rawDisc, subtotal);
                            }
                        }
                    }

                    // 2C. APPLY COUPON DISCOUNT (WITH START/END TIME & MAX-USES CHECK + ATOMIC INCREMENT)
                    if (!string.IsNullOrWhiteSpace(request.CouponCode))
                    {
                        try
                        {
                            DateTime nowBaghdadForCoupon = ExpensesController.BaghdadNow();
                            int? matchedCouponId = null;
                            string matchedType = "Percent";
                            decimal matchedVal = 0m;
                            bool couponValid = false;

                            const string couponQuery = @"
                                SELECT CouponID, DiscountType, DiscountValue, StartDate, EndDate, MaxUses, UsedCount, TargetCustomerID, TargetCustomerPhone
                                FROM discountcoupons
                                WHERE CouponCode = @Code AND IsActive = 1
                                LIMIT 1
                                FOR UPDATE;";
                            using (var couponCmd = new MySqlCommand(couponQuery, connection, transaction))
                            {
                                couponCmd.Parameters.AddWithValue("@Code", request.CouponCode.Trim());
                                using var couponReader = await couponCmd.ExecuteReaderAsync();
                                if (await couponReader.ReadAsync())
                                {
                                    DateTime? sDate = couponReader.IsDBNull(couponReader.GetOrdinal("StartDate")) ? null : couponReader.GetDateTime("StartDate");
                                    DateTime? eDate = couponReader.IsDBNull(couponReader.GetOrdinal("EndDate")) ? null : couponReader.GetDateTime("EndDate");
                                    int? mUses = couponReader.IsDBNull(couponReader.GetOrdinal("MaxUses")) ? null : couponReader.GetInt32("MaxUses");
                                    int uCount = couponReader.IsDBNull(couponReader.GetOrdinal("UsedCount")) ? 0 : couponReader.GetInt32("UsedCount");

                                    bool withinWindow = (!sDate.HasValue || nowBaghdadForCoupon >= sDate.Value)
                                                        && (!eDate.HasValue || nowBaghdadForCoupon <= eDate.Value);
                                    bool withinLimit = !mUses.HasValue || mUses.Value <= 0 || uCount < mUses.Value;

                                    if (withinWindow && withinLimit)
                                    {
                                        matchedCouponId = couponReader.GetInt32("CouponID");
                                        matchedType = couponReader.GetString("DiscountType");
                                        matchedVal = couponReader.GetDecimal("DiscountValue");
                                        couponValid = true;
                                    }
                                }
                            }

                            if (couponValid && matchedCouponId.HasValue)
                            {
                                decimal couponDisc = string.Equals(matchedType, "Fixed", StringComparison.OrdinalIgnoreCase)
                                    ? Math.Min(subtotal, matchedVal)
                                    : Math.Round(subtotal * Math.Clamp(matchedVal, 0m, 100m) / 100m, 2, MidpointRounding.AwayFromZero);
                                if (couponDisc > discount)
                                    discount = couponDisc;

                                using var incCmd = new MySqlCommand("UPDATE discountcoupons SET UsedCount = UsedCount + 1 WHERE CouponID = @cid;", connection, transaction);
                                incCmd.Parameters.AddWithValue("@cid", matchedCouponId.Value);
                                await incCmd.ExecuteNonQueryAsync();
                            }
                        }
                        catch
                        {
                        }
                    }
                    if (request.DiscountAmount > 0m && request.DiscountAmount <= subtotal && request.DiscountAmount > discount)
                    {
                        discount = request.DiscountAmount;
                    }

                    // 2D. APPLY QAMISHLO COINS REWARD DISCOUNT FROM NOTES (e.g. [🎁 قامشلو كوينز: خصم 5,000 د.ع VIP -5000 IQD])
                    if (!string.IsNullOrWhiteSpace(request.Notes) &&
                        (request.Notes.Contains("قامشلو كوينز") || request.Notes.Contains("Qamishlo Coins", StringComparison.OrdinalIgnoreCase) || request.Notes.Contains("🎁")))
                    {
                        var coinMatch = System.Text.RegularExpressions.Regex.Match(request.Notes, @"-(\d[\d,\.]*)\s*IQD", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
                        if (coinMatch.Success)
                        {
                            string numStr = coinMatch.Groups[1].Value.Replace(",", "");
                            if (decimal.TryParse(numStr, System.Globalization.NumberStyles.Any, System.Globalization.CultureInfo.InvariantCulture, out decimal coinsDisc) && coinsDisc > 0m)
                            {
                                discount = Math.Min(subtotal, discount + coinsDisc);
                            }
                        }
                    }

                    // 3. VALIDATE DELIVERY ZONE + MINIMUM ORDER
                    decimal deliveryFee = 0m;
                    int? deliveryZoneId = null;

                    if (orderType == "Delivery")
                    {
                        if (!request.Latitude.HasValue || !request.Longitude.HasValue)
                        {
                            await transaction.RollbackAsync();
                            return BadRequest(new
                            {
                                success = false,
                                message = "Delivery location is required."
                            });
                        }

                        decimal minimumOrderAmount = 3500m;
                        const string minimumQuery = @"
                            SELECT MinimumOrderAmount
                            FROM restaurantsettings
                            ORDER BY SettingID
                            LIMIT 1;";

                        using (MySqlCommand minimumCommand = new MySqlCommand(minimumQuery, connection, transaction))
                        {
                            object? minimumResult = await minimumCommand.ExecuteScalarAsync();
                            if (minimumResult != null && minimumResult != DBNull.Value)
                                minimumOrderAmount = Convert.ToDecimal(minimumResult);
                        }

                        if (subtotal < minimumOrderAmount)
                        {
                            await transaction.RollbackAsync();
                            return BadRequest(new
                            {
                                success = false,
                                code = "MINIMUM_ORDER_NOT_MET",
                                message = $"Your food subtotal is below the minimum order of {minimumOrderAmount:0.##} IQD.",
                                minimumOrderAmount,
                                subtotal
                            });
                        }

                        const string zonesQuery = @"
                            SELECT DeliveryZoneID, DeliveryFee, ZoneCoordinates
                            FROM deliveryzones
                            WHERE IsActive = 1
                              AND ZoneCoordinates IS NOT NULL
                              AND ZoneCoordinates <> ''
                            ORDER BY DeliveryZoneID;";

                        using (MySqlCommand zonesCommand = new MySqlCommand(zonesQuery, connection, transaction))
                        using (MySqlDataReader zonesReader = await zonesCommand.ExecuteReaderAsync())
                        {
                            while (await zonesReader.ReadAsync())
                            {
                                int candidateZoneId = zonesReader.GetInt32(zonesReader.GetOrdinal("DeliveryZoneID"));
                                decimal candidateFee = zonesReader.GetDecimal(zonesReader.GetOrdinal("DeliveryFee"));
                                string coordinatesJson = zonesReader.GetString(zonesReader.GetOrdinal("ZoneCoordinates"));

                                if (IsPointInsideDeliveryPolygon(
                                    request.Latitude.Value,
                                    request.Longitude.Value,
                                    coordinatesJson))
                                {
                                    deliveryZoneId = candidateZoneId;
                                    deliveryFee = candidateFee;
                                    break;
                                }
                            }
                        }

                        if (!deliveryZoneId.HasValue)
                        {
                            await transaction.RollbackAsync();
                            return BadRequest(new
                            {
                                success = false,
                                code = "OUTSIDE_DELIVERY_ZONES",
                                message = "Sorry, this location is outside our active delivery areas."
                            });
                        }
                    }

                    // 4. CALCULATE TOTAL (include Qamishlo Coins / Special Offer discount cleanly, capped at subtotal)
                    decimal clientExtraDisc = Math.Max(0m, request.DiscountAmount) + Math.Max(0m, request.LoyaltyDiscountAmount);
                    if (clientExtraDisc > discount)
                        discount = Math.Min(subtotal, clientExtraDisc);
                    decimal total = subtotal + deliveryFee - discount;
                    if (total < 0)
                        total = 0;

                    // 5. PREPARE NOTES
                    string finalNotes = request.Notes?.Trim() ?? "";
                    if (request.Latitude.HasValue && request.Longitude.HasValue)
                    {
                        string locationText = $"Location: {request.Latitude.Value:F6}, {request.Longitude.Value:F6}";
                        if (!string.IsNullOrWhiteSpace(finalNotes))
                            finalNotes += Environment.NewLine;
                        finalNotes += locationText;
                    }

                    // 6. ARCHIVE PAST ACCOUNTING DAYS & CALCULATE DAILY ORDER NUMBER (1, 2, 3...)
                    DateTime baghdadOrderDate = ExpensesController.BaghdadNow();
                    DateTime currentBusinessDate = ExpensesController.BusinessDate(baghdadOrderDate);

                    int dailyOrderNumber = 1;
                    await using (var dailyNumCmd = new MySqlCommand(@"
                        SELECT COUNT(*) + 1
                        FROM orders
                        WHERE COALESCE(BusinessDate, DATE(OrderDate)) = @BusinessDate
                          AND IsArchived = 0;", connection, transaction))
                    {
                        dailyNumCmd.Parameters.AddWithValue("@BusinessDate", currentBusinessDate);
                        object? nextNumObj = await dailyNumCmd.ExecuteScalarAsync();
                        if (nextNumObj != null && nextNumObj != DBNull.Value)
                            dailyOrderNumber = Math.Max(1, Convert.ToInt32(nextNumObj));
                    }

                    string insertOrderQuery = @"
                        INSERT INTO orders
                        (
                            CustomerID,
                            UserID,
                            DeliveryZoneID,
                            OrderDate,
                            BusinessDate,
                            OrderType,
                            Status,
                            Subtotal,
                            DeliveryFee,
                            DiscountAmount,
                            TotalAmount,
                            DeliveryAddress,
                            CustomerLatitude,
                            CustomerLongitude,
                            Notes
                        )
                        VALUES
                        (
                            @CustomerID,
                            @UserID,
                            @DeliveryZoneID,
                            @OrderDate,
                            @BusinessDate,
                            @OrderType,
                            @InitialStatus,
                            @Subtotal,
                            @DeliveryFee,
                            @DiscountAmount,
                            @TotalAmount,
                            @DeliveryAddress,
                            @Latitude,
                            @Longitude,
                            @Notes
                        );";

                    using (MySqlCommand command = new MySqlCommand(insertOrderQuery, connection, transaction))
                    {
                        int validUserId = await QamishloRestaurantAPI.Security.ManagerCredentialValidator.ResolveValidOrderUserIdAsync(connection, transaction, 1);
                        command.Parameters.AddWithValue("@CustomerID", (object?)customerId ?? DBNull.Value);
                        command.Parameters.AddWithValue("@UserID", validUserId);
                        command.Parameters.AddWithValue("@DeliveryZoneID", (object?)deliveryZoneId ?? DBNull.Value);
                        command.Parameters.AddWithValue("@OrderDate", baghdadOrderDate);
                        command.Parameters.AddWithValue("@BusinessDate", currentBusinessDate);
                        string initialStatus = "New";
                        if (!string.IsNullOrWhiteSpace(request.InitialStatus) &&
                            (request.InitialStatus.Equals("Delivered", StringComparison.OrdinalIgnoreCase) || request.InitialStatus == "تم التسليم"))
                        {
                            initialStatus = "Delivered";
                        }
                        command.Parameters.AddWithValue("@OrderType", orderType);
                        command.Parameters.AddWithValue("@InitialStatus", initialStatus);
                        command.Parameters.AddWithValue("@Subtotal", subtotal);
                        command.Parameters.AddWithValue("@DeliveryFee", deliveryFee);
                        command.Parameters.AddWithValue("@DiscountAmount", discount);
                        command.Parameters.AddWithValue("@TotalAmount", total);
                        command.Parameters.AddWithValue("@DeliveryAddress", string.IsNullOrWhiteSpace(request.Address) ? DBNull.Value : request.Address.Trim());
                        command.Parameters.AddWithValue("@Latitude", (object?)request.Latitude ?? DBNull.Value);
                        command.Parameters.AddWithValue("@Longitude", (object?)request.Longitude ?? DBNull.Value);
                        command.Parameters.AddWithValue("@Notes", string.IsNullOrWhiteSpace(finalNotes) ? DBNull.Value : finalNotes);

                        await command.ExecuteNonQueryAsync();
                        request.CreatedOrderID = Convert.ToInt32(command.LastInsertedId);
                    }

                    int orderId = request.CreatedOrderID;

                    // 7. INSERT ORDER DETAILS (with billed add-on unit price and AddonsText)
                    for (int itemIdx = 0; itemIdx < request.Items.Count; itemIdx++)
                    {
                        OrderItemRequest item = request.Items[itemIdx];
                        var (unitPrice, addonsText) = resolvedItemPricing[itemIdx];
                        decimal itemTotal = unitPrice * item.Quantity;

                        string insertDetailQuery = @"
                            INSERT INTO orderdetails
                            (
                                OrderID,
                                ProductID,
                                Quantity,
                                UnitPrice,
                                TotalPrice,
                                Notes,
                                AddonsText
                            )
                            VALUES
                            (
                                @OrderID,
                                @ProductID,
                                @Quantity,
                                @UnitPrice,
                                @TotalPrice,
                                @Notes,
                                @AddonsText
                            );";

                        using (MySqlCommand command = new MySqlCommand(insertDetailQuery, connection, transaction))
                        {
                            command.Parameters.AddWithValue("@OrderID", orderId);
                            command.Parameters.AddWithValue("@ProductID", item.ProductID);
                            command.Parameters.AddWithValue("@Quantity", item.Quantity);
                            command.Parameters.AddWithValue("@UnitPrice", unitPrice);
                            command.Parameters.AddWithValue("@TotalPrice", itemTotal);
                            command.Parameters.AddWithValue("@Notes", string.IsNullOrWhiteSpace(item.Notes) ? DBNull.Value : item.Notes.Trim());
                            command.Parameters.AddWithValue("@AddonsText", string.IsNullOrWhiteSpace(addonsText) ? DBNull.Value : addonsText);
                            await command.ExecuteNonQueryAsync();
                        }
                    }

                    // 8. COMMIT
                    await transaction.CommitAsync();

                    int pushOrderId = orderId;
                    string pushOrderType = orderType;
                    decimal pushTotal = total;
                    _ = Task.Run(async () =>
                    {
                        try { await _firebaseNotifications.NotifyRestaurantNewOrderAsync(pushOrderId, pushOrderType, pushTotal); }
                        catch { }
                    });

                    return Ok(new
                    {
                        success = true,
                        message = "Order created successfully.",
                        orderId = orderId,
                        dailyOrderNumber = dailyOrderNumber,
                        orderNumber = $"Q-{dailyOrderNumber}",
                        customerId = customerId,
                        orderType = orderType,
                        subtotal = subtotal,
                        deliveryFee = deliveryFee,
                        deliveryZoneId = deliveryZoneId,
                        discountAmount = discount,
                        total = total,
                        status = "New",
                        latitude = request.Latitude,
                        longitude = request.Longitude
                    });
                }
                catch
                {
                    await transaction.RollbackAsync();
                    throw;
                }
            }
            catch (Exception ex)
            {
                return StatusCode(500, new
                {
                    success = false,
                    message = "Failed to create order.",
                    error = "تعذر إكمال العملية بسبب خطأ داخلي."
                });
            }
        }

        // =========================================================
        // GET ALL ORDERS (WITH FULL ORDER ITEMS)
        // GET: /api/orders
        // =========================================================

        [HttpGet]
        public async Task<IActionResult> Getorders(
            [FromQuery] string? status = null,
            [FromQuery] string? type = null,
            [FromQuery] string? search = null,
            [FromQuery] bool includeArchived = false)
        {
            try
            {
                using MySqlConnection connection = DatabaseManager.GetConnection();
                await connection.OpenAsync();

                // Automatically archive completed/closed orders from past accounting days
                // so the new accounting day starts cleanly at #1 while keeping all past orders in DB
                if ((DateTime.UtcNow - _lastAutoArchiveUtc).TotalMinutes >= 5)
                try
                {
                    _lastAutoArchiveUtc = DateTime.UtcNow;
                    DateTime currentBusinessDate = ExpensesController.BusinessDate(ExpensesController.BaghdadNow());
                    const string autoArchiveSql = @"
                        UPDATE orders
                        SET IsArchived = 1
                        WHERE IsArchived = 0
                          AND COALESCE(BusinessDate, DATE(DATE_SUB(OrderDate, INTERVAL 2 HOUR))) < @currentBusinessDate
                          AND Status IN ('Delivered', 'Cancelled', 'Rejected');";
                    await using var autoArchiveCmd = new MySqlCommand(autoArchiveSql, connection);
                    autoArchiveCmd.Parameters.AddWithValue("@currentBusinessDate", currentBusinessDate);
                    await autoArchiveCmd.ExecuteNonQueryAsync();
                }
                catch
                {
                }

                string query = @"
                    SELECT
                        o.OrderID,
                        ROW_NUMBER() OVER (PARTITION BY COALESCE(o.BusinessDate, DATE(o.OrderDate)) ORDER BY o.OrderID ASC) AS DailyOrderNumber,
                        o.CustomerID,
                        o.UserID,
                        o.DeliveryZoneID,
                        o.OrderDate,
                        COALESCE(o.BusinessDate, DATE(o.OrderDate)) AS BusinessDate,
                        o.OrderType,
                        o.Status,
                        o.Subtotal,
                        o.DeliveryFee,
                        o.DiscountAmount,
                        o.TotalAmount,
                        o.DeliveryAddress,
                        COALESCE(o.CustomerLatitude, c.Latitude) AS CustomerLatitude,
                        COALESCE(o.CustomerLongitude, c.Longitude) AS CustomerLongitude,
                        o.Notes,
                        o.CreatedAt,
                        o.UpdatedAt,
                        o.DeliveryWorkerID,
                        COALESCE(c.FullName, 'Walk-in Customer') AS CustomerName,
                        COALESCE(c.Phone, '') AS CustomerPhone,
                        COALESCE(u.FullName, 'Unknown') AS UserName,
                        COALESCE(dz.ZoneName, '') AS DeliveryZoneName,
                        COALESCE(dw.FullName, '') AS DeliveryWorkerName,
                        COALESCE(dw.Phone, '') AS DeliveryWorkerPhone
                    FROM orders o
                    LEFT JOIN customers c ON o.CustomerID = c.CustomerID
                    LEFT JOIN users u ON o.UserID = u.UserID
                    LEFT JOIN deliveryzones dz ON o.DeliveryZoneID = dz.DeliveryZoneID
                    LEFT JOIN deliveryworkers dw ON o.DeliveryWorkerID = dw.DeliveryWorkerID
                    WHERE 1=1
                ";

                if (!includeArchived)
                {
                    query += " AND o.IsArchived = 0 ";
                }

                List<MySqlParameter> parameters = new List<MySqlParameter>();

                if (!string.IsNullOrWhiteSpace(status))
                {
                    string normalizedFilter = NormalizeStatus(status.Trim());
                    string statusToUse = string.IsNullOrWhiteSpace(normalizedFilter) ? status.Trim() : normalizedFilter;
                    query += " AND o.Status = @Status ";
                    parameters.Add(new MySqlParameter("@Status", statusToUse));
                }

                if (!string.IsNullOrWhiteSpace(type))
                {
                    string normalizedType = NormalizeOrderType(type);
                    if (normalizedType != "")
                    {
                        query += " AND o.OrderType = @OrderType ";
                        parameters.Add(new MySqlParameter("@OrderType", normalizedType));
                    }
                }

                if (!string.IsNullOrWhiteSpace(search))
                {
                    query += @"
                        AND
                        (
                            CAST(o.OrderID AS CHAR) LIKE @Search
                            
                            OR c.FullName LIKE @Search
                            OR c.Phone LIKE @Search
                        ) ";
                    parameters.Add(new MySqlParameter("@Search", "%" + search.Trim() + "%"));
                }

                query += " ORDER BY o.OrderID DESC LIMIT 300;";

                List<OrderResponse> orders = new List<OrderResponse>();

                using (MySqlCommand command = new MySqlCommand(query, connection))
                {
                    foreach (MySqlParameter parameter in parameters)
                        command.Parameters.Add(parameter);

                    using MySqlDataReader reader = await command.ExecuteReaderAsync();
                    while (await reader.ReadAsync())
                    {
                        orders.Add(ReadOrder(reader));
                    }
                }

                await PopulateOrdersItemsAsync(connection, orders);

                return Ok(new
                {
                    success = true,
                    businessDate = ExpensesController.BusinessDate(ExpensesController.BaghdadNow()).ToString("yyyy-MM-dd"),
                    count = orders.Count,
                    orders = orders
                });
            }
            catch (Exception ex)
            {
                return StatusCode(500, new
                {
                    success = false,
                    message = "Failed to load orders.",
                    error = "تعذر إكمال العملية بسبب خطأ داخلي."
                });
            }
        }

        private static async Task PopulateOrdersItemsAsync(
            MySqlConnection connection,
            List<OrderResponse> orders)
        {
            if (orders == null || orders.Count == 0)
                return;

            var byId = orders.ToDictionary(o => o.OrderID);
            string idList = string.Join(",", byId.Keys);

            string detailsQuery = $@"
                SELECT
                    od.OrderDetailID,
                    od.OrderID,
                    od.ProductID,
                    CASE WHEN COALESCE(od.AddonsText, '') <> '' THEN CONCAT(COALESCE(p.ProductName, CONCAT('منتج #', od.ProductID)), ' (+ ', od.AddonsText, ')') ELSE COALESCE(p.ProductName, CONCAT('منتج #', od.ProductID)) END AS ProductName,
                    COALESCE(p.CategoryID, 0) AS CategoryID,
                    CASE WHEN LEFT(IFNULL(p.ImageURL, ''), 11) = 'data:image/' THEN CONCAT('api/products/', p.ProductID, '/image?v=', CHAR_LENGTH(p.ImageURL)) ELSE p.ImageURL END AS ImageURL,
                    od.Quantity,
                    od.UnitPrice,
                    od.TotalPrice,
                    od.Notes
                FROM orderdetails od
                LEFT JOIN products p ON od.ProductID = p.ProductID
                WHERE od.OrderID IN ({idList})
                ORDER BY od.OrderDetailID ASC;";

            await using var detailCmd = new MySqlCommand(detailsQuery, connection);
            await using var detailReader = await detailCmd.ExecuteReaderAsync();
            while (await detailReader.ReadAsync())
            {
                int ordId = detailReader.GetInt32("OrderID");
                if (byId.TryGetValue(ordId, out var ord))
                {
                    ord.Items.Add(new OrderDetailResponse
                    {
                        OrderDetailID = detailReader.GetInt32("OrderDetailID"),
                        OrderID = ordId,
                        ProductID = detailReader.GetInt32("ProductID"),
                        ProductName = detailReader.GetString("ProductName"),
                        CategoryID = detailReader.GetInt32("CategoryID"),
                        ImageURL = detailReader.IsDBNull(detailReader.GetOrdinal("ImageURL"))
                            ? null
                            : detailReader.GetString("ImageURL"),
                        Quantity = detailReader.GetInt32("Quantity"),
                        UnitPrice = detailReader.GetDecimal("UnitPrice"),
                        TotalPrice = detailReader.GetDecimal("TotalPrice"),
                        Notes = detailReader.IsDBNull(detailReader.GetOrdinal("Notes"))
                            ? null
                            : detailReader.GetString("Notes")
                    });
                }
            }
        }

        // =========================================================
        // GET CUSTOMER ORDER HISTORY
        // GET: /api/orders/customer/5
        // =========================================================

        [HttpGet("customer/{customerId:int}")]
        public async Task<IActionResult> GetCustomerOrderHistory(int customerId)
        {
            if (customerId <= 0)
            {
                return BadRequest(new
                {
                    success = false,
                    message = "Invalid customer ID."
                });
            }

            try
            {
                using MySqlConnection connection = DatabaseManager.GetConnection();
                await connection.OpenAsync();

                string query = @"
                    SELECT
                        o.OrderID,
                        ROW_NUMBER() OVER (PARTITION BY COALESCE(o.BusinessDate, DATE(o.OrderDate)) ORDER BY o.OrderID ASC) AS DailyOrderNumber,
                        o.CustomerID,
                        o.UserID,
                        o.DeliveryZoneID,
                        o.OrderDate,
                        COALESCE(o.BusinessDate, DATE(o.OrderDate)) AS BusinessDate,
                        o.OrderType,
                        o.Status,
                        o.Subtotal,
                        o.DeliveryFee,
                        o.DiscountAmount,
                        o.TotalAmount,
                        o.DeliveryAddress,
                        COALESCE(o.CustomerLatitude, c.Latitude) AS CustomerLatitude,
                        COALESCE(o.CustomerLongitude, c.Longitude) AS CustomerLongitude,
                        o.Notes,
                        o.CreatedAt,
                        o.UpdatedAt,
                        o.DeliveryWorkerID,
                        COALESCE(c.FullName, 'Walk-in Customer') AS CustomerName,
                        COALESCE(c.Phone, '') AS CustomerPhone,
                        COALESCE(u.FullName, 'Unknown') AS UserName,
                        COALESCE(dz.ZoneName, '') AS DeliveryZoneName,
                        COALESCE(dw.FullName, '') AS DeliveryWorkerName,
                        COALESCE(dw.Phone, '') AS DeliveryWorkerPhone
                    FROM orders o
                    LEFT JOIN customers c ON o.CustomerID = c.CustomerID
                    LEFT JOIN users u ON o.UserID = u.UserID
                    LEFT JOIN deliveryzones dz ON o.DeliveryZoneID = dz.DeliveryZoneID
                    LEFT JOIN deliveryworkers dw ON o.DeliveryWorkerID = dw.DeliveryWorkerID
                    WHERE o.CustomerID = @CustomerID
                    ORDER BY o.OrderDate DESC, o.OrderID DESC;";

                List<OrderResponse> orders = new List<OrderResponse>();

                using (MySqlCommand command = new MySqlCommand(query, connection))
                {
                    command.Parameters.AddWithValue("@CustomerID", customerId);
                    using MySqlDataReader reader = await command.ExecuteReaderAsync();
                    while (await reader.ReadAsync())
                    {
                        orders.Add(ReadOrder(reader));
                    }
                }

                await PopulateOrdersItemsAsync(connection, orders);

                return Ok(new
                {
                    success = true,
                    count = orders.Count,
                    orders = orders
                });
            }
            catch (Exception ex)
            {
                return StatusCode(500, new
                {
                    success = false,
                    message = "Failed to load customer order history.",
                    error = "تعذر إكمال العملية بسبب خطأ داخلي."
                });
            }
        }

        // =========================================================
        // GET DELIVERY ORDERS
        // GET: /api/orders/delivery
        // =========================================================

        [HttpGet("delivery")]
        public async Task<IActionResult> GetDeliveryorders(
            [FromQuery] string? status = null)
        {
            try
            {
                using MySqlConnection connection = DatabaseManager.GetConnection();
                await connection.OpenAsync();

                string query = @"
                    SELECT
                        o.OrderID,
                        ROW_NUMBER() OVER (PARTITION BY COALESCE(o.BusinessDate, DATE(o.OrderDate)) ORDER BY o.OrderID ASC) AS DailyOrderNumber,
                        o.CustomerID,
                        o.UserID,
                        o.DeliveryZoneID,
                        o.OrderDate,
                        COALESCE(o.BusinessDate, DATE(o.OrderDate)) AS BusinessDate,
                        o.OrderType,
                        o.Status,
                        o.Subtotal,
                        o.DeliveryFee,
                        o.DiscountAmount,
                        o.TotalAmount,
                        o.DeliveryAddress,
                        COALESCE(o.CustomerLatitude, c.Latitude) AS CustomerLatitude,
                        COALESCE(o.CustomerLongitude, c.Longitude) AS CustomerLongitude,
                        o.Notes,
                        o.CreatedAt,
                        o.UpdatedAt,
                        o.DeliveryWorkerID,
                        COALESCE(c.FullName, 'Unknown Customer') AS CustomerName,
                        COALESCE(c.Phone, '') AS CustomerPhone,
                        COALESCE(u.FullName, 'Unknown') AS UserName,
                        COALESCE(dz.ZoneName, '') AS DeliveryZoneName,
                        COALESCE(dw.FullName, '') AS DeliveryWorkerName,
                        COALESCE(dw.Phone, '') AS DeliveryWorkerPhone
                    FROM orders o
                    LEFT JOIN customers c ON o.CustomerID = c.CustomerID
                    LEFT JOIN users u ON o.UserID = u.UserID
                    LEFT JOIN deliveryzones dz ON o.DeliveryZoneID = dz.DeliveryZoneID
                    LEFT JOIN deliveryworkers dw ON o.DeliveryWorkerID = dw.DeliveryWorkerID
                    WHERE o.OrderType = 'Delivery' AND o.IsArchived = 0
                ";

                if (!string.IsNullOrWhiteSpace(status))
                {
                    query += " AND o.Status = @Status ";
                }

                query += " ORDER BY o.OrderID DESC;";

                List<OrderResponse> orders = new List<OrderResponse>();

                using (MySqlCommand command = new MySqlCommand(query, connection))
                {
                    if (!string.IsNullOrWhiteSpace(status))
                        command.Parameters.AddWithValue("@Status", status.Trim());

                    using MySqlDataReader reader = await command.ExecuteReaderAsync();
                    while (await reader.ReadAsync())
                    {
                        orders.Add(ReadOrder(reader));
                    }
                }

                await PopulateOrdersItemsAsync(connection, orders);

                return Ok(new
                {
                    success = true,
                    count = orders.Count,
                    orders = orders
                });
            }
            catch (Exception ex)
            {
                return StatusCode(500, new
                {
                    success = false,
                    message = "Failed to load delivery orders.",
                    error = "تعذر إكمال العملية بسبب خطأ داخلي."
                });
            }
        }

        // =========================================================
        // GET SINGLE ORDER
        // GET: /api/orders/5
        // =========================================================

        [HttpGet("{id:int}")]
        public async Task<IActionResult> GetOrder(int id)
        {
            if (id <= 0)
            {
                return BadRequest(new
                {
                    success = false,
                    message = "Invalid order ID."
                });
            }

            try
            {
                using MySqlConnection connection = DatabaseManager.GetConnection();
                await connection.OpenAsync();

                string orderQuery = @"
                    SELECT
                        o.OrderID,
                        ROW_NUMBER() OVER (PARTITION BY COALESCE(o.BusinessDate, DATE(o.OrderDate)) ORDER BY o.OrderID ASC) AS DailyOrderNumber,
                        o.CustomerID,
                        o.UserID,
                        o.DeliveryZoneID,
                        o.OrderDate,
                        COALESCE(o.BusinessDate, DATE(o.OrderDate)) AS BusinessDate,
                        o.OrderType,
                        o.Status,
                        o.Subtotal,
                        o.DeliveryFee,
                        o.DiscountAmount,
                        o.TotalAmount,
                        o.DeliveryAddress,
                        COALESCE(o.CustomerLatitude, c.Latitude) AS CustomerLatitude,
                        COALESCE(o.CustomerLongitude, c.Longitude) AS CustomerLongitude,
                        o.Notes,
                        o.CreatedAt,
                        o.UpdatedAt,
                        o.DeliveryWorkerID,
                        COALESCE(c.FullName, 'Walk-in Customer') AS CustomerName,
                        COALESCE(c.Phone, '') AS CustomerPhone,
                        COALESCE(u.FullName, 'Unknown') AS UserName,
                        COALESCE(dz.ZoneName, '') AS DeliveryZoneName,
                        COALESCE(dw.FullName, '') AS DeliveryWorkerName,
                        COALESCE(dw.Phone, '') AS DeliveryWorkerPhone
                    FROM orders o
                    LEFT JOIN customers c ON o.CustomerID = c.CustomerID
                    LEFT JOIN users u ON o.UserID = u.UserID
                    LEFT JOIN deliveryzones dz ON o.DeliveryZoneID = dz.DeliveryZoneID
                    LEFT JOIN deliveryworkers dw ON o.DeliveryWorkerID = dw.DeliveryWorkerID
                    WHERE o.OrderID = @OrderID
                    LIMIT 1;";

                OrderResponse? order = null;

                using (MySqlCommand command = new MySqlCommand(orderQuery, connection))
                {
                    command.Parameters.AddWithValue("@OrderID", id);
                    using MySqlDataReader reader = await command.ExecuteReaderAsync();
                    if (await reader.ReadAsync())
                    {
                        order = ReadOrder(reader);
                    }
                }

                if (order == null)
                {
                    return NotFound(new
                    {
                        success = false,
                        message = $"Order #{id} was not found."
                    });
                }

                await PopulateOrdersItemsAsync(connection, new List<OrderResponse> { order });

                return Ok(new
                {
                    success = true,
                    order = order
                });
            }
            catch (Exception ex)
            {
                return StatusCode(500, new
                {
                    success = false,
                    message = "Failed to load order.",
                    error = "تعذر إكمال العملية حالياً. يرجى المحاولة لاحقاً."
                });
            }
        }

        // =========================================================
        // CUSTOMER ORDER TRACKING
        // GET: /api/orders/25/tracking
        // =========================================================

        [HttpGet("{id:int}/tracking")]
        public async Task<IActionResult> TrackOrder(
            int id,
            [FromQuery] int customerId = 0,
            [FromQuery] string? phone = null)
        {
            if (id <= 0)
            {
                return BadRequest(new
                {
                    success = false,
                    message = "Invalid order ID."
                });
            }

            try
            {
                using MySqlConnection connection = DatabaseManager.GetConnection();
                await connection.OpenAsync();

                string query = @"
                    SELECT
                        o.OrderID,
                        ROW_NUMBER() OVER (PARTITION BY COALESCE(o.BusinessDate, DATE(o.OrderDate)) ORDER BY o.OrderID ASC) AS DailyOrderNumber,
                        o.CustomerID,
                        o.OrderType,
                        o.Status,
                        o.TotalAmount,
                        o.DeliveryAddress,
                        COALESCE(o.CustomerLatitude, c.Latitude) AS CustomerLatitude,
                        COALESCE(o.CustomerLongitude, c.Longitude) AS CustomerLongitude,
                        o.Notes,
                        o.CustomerLatitude,
                        o.CustomerLongitude,
                        o.OrderDate,
                        o.DeliveryWorkerID,
                        COALESCE(c.FullName, '') AS CustomerName,
                        COALESCE(c.Phone, '') AS CustomerPhone,
                        COALESCE(dw.FullName, '') AS DeliveryWorkerName,
                        COALESCE(dw.Phone, '') AS DeliveryWorkerPhone,
                        dw.CurrentLatitude AS DriverLatitude,
                        dw.CurrentLongitude AS DriverLongitude,
                        dw.LastLocationAt AS DriverLastLocationAt
                    FROM orders o
                    LEFT JOIN customers c ON o.CustomerID = c.CustomerID
                    LEFT JOIN deliveryworkers dw ON o.DeliveryWorkerID = dw.DeliveryWorkerID
                    WHERE o.OrderID = @OrderID
                      AND (
                            (@CustomerID > 0 AND o.CustomerID = @CustomerID)
                            OR (@Phone <> '' AND c.Phone = @Phone)
                            OR (@CustomerID <= 0 AND @Phone = '')
                          )
                    LIMIT 1;";

                using MySqlCommand command = new MySqlCommand(query, connection);
                command.Parameters.AddWithValue("@OrderID", id);
                command.Parameters.AddWithValue("@CustomerID", customerId);
                command.Parameters.AddWithValue("@Phone", (phone ?? string.Empty).Trim());

                using MySqlDataReader reader = await command.ExecuteReaderAsync();

                if (!await reader.ReadAsync())
                {
                    return NotFound(new
                    {
                        success = false,
                        message = $"Order #{id} was not found."
                    });
                }

                int? workerID = reader.IsDBNull(reader.GetOrdinal("DeliveryWorkerID"))
                    ? null
                    : reader.GetInt32(reader.GetOrdinal("DeliveryWorkerID"));

                ExtractLocation(
                    reader["Notes"] == DBNull.Value ? "" : reader["Notes"]?.ToString() ?? "",
                    out double? notesLatitude,
                    out double? notesLongitude);

                double? latitude = reader.IsDBNull(reader.GetOrdinal("CustomerLatitude"))
                    ? notesLatitude : Convert.ToDouble(reader["CustomerLatitude"]);
                double? longitude = reader.IsDBNull(reader.GetOrdinal("CustomerLongitude"))
                    ? notesLongitude : Convert.ToDouble(reader["CustomerLongitude"]);

                return Ok(new
                {
                    success = true,
                    orderID = reader.GetInt32(reader.GetOrdinal("OrderID")),
                    dailyOrderNumber = reader.GetInt32(reader.GetOrdinal("DailyOrderNumber")),
                    customerID = reader.IsDBNull(reader.GetOrdinal("CustomerID"))
                        ? (int?)null
                        : reader.GetInt32(reader.GetOrdinal("CustomerID")),
                    orderType = reader.GetString(reader.GetOrdinal("OrderType")),
                    status = reader.GetString(reader.GetOrdinal("Status")),
                    totalAmount = reader.GetDecimal(reader.GetOrdinal("TotalAmount")),
                    deliveryAddress = reader.IsDBNull(reader.GetOrdinal("DeliveryAddress"))
                        ? ""
                        : reader.GetString(reader.GetOrdinal("DeliveryAddress")),
                    orderDate = reader.GetDateTime(reader.GetOrdinal("OrderDate")),
                    customerName = reader.GetString(reader.GetOrdinal("CustomerName")),
                    customerPhone = reader.GetString(reader.GetOrdinal("CustomerPhone")),
                    deliveryWorkerID = workerID,
                    deliveryWorkerName = reader.GetString(reader.GetOrdinal("DeliveryWorkerName")),
                    deliveryWorkerPhone = reader.GetString(reader.GetOrdinal("DeliveryWorkerPhone")),
                    latitude = latitude,
                    longitude = longitude,
                    driverLatitude = reader.IsDBNull(reader.GetOrdinal("DriverLatitude"))
                        ? (double?)null : reader.GetDouble(reader.GetOrdinal("DriverLatitude")),
                    driverLongitude = reader.IsDBNull(reader.GetOrdinal("DriverLongitude"))
                        ? (double?)null : reader.GetDouble(reader.GetOrdinal("DriverLongitude")),
                    driverLastLocationAt = reader.IsDBNull(reader.GetOrdinal("DriverLastLocationAt"))
                        ? (DateTime?)null : reader.GetDateTime(reader.GetOrdinal("DriverLastLocationAt"))
                });
            }
            catch (Exception ex)
            {
                return StatusCode(500, new
                {
                    success = false,
                    message = "Failed to load order tracking.",
                    error = "تعذر إكمال العملية حالياً. يرجى المحاولة لاحقاً."
                });
            }
        }

        // =========================================================
        // UPDATE ORDER STATUS
        // PUT: /api/orders/5/status
        // =========================================================

        [HttpPut("{id:int}/status")]
        public async Task<IActionResult> Updateorderstatus(
            int id,
            [FromBody] UpdateorderstatusRequest request)
        {
            if (id <= 0)
            {
                return BadRequest(new
                {
                    success = false,
                    message = "Invalid order ID."
                });
            }

            if (request == null || string.IsNullOrWhiteSpace(request.Status))
            {
                return BadRequest(new
                {
                    success = false,
                    message = "Order status is required."
                });
            }

            string status = NormalizeStatus(request.Status);
            if (status == "")
            {
                return BadRequest(new
                {
                    success = false,
                    message = "Invalid status. Allowed: New, Preparing, Ready, OutForDelivery, Delivered, Cancelled, Rejected."
                });
            }

            return await UpdateStatusInternal(id, status, request.DeliveryWorkerID, request.RejectionReason);
        }

        [HttpPut("{id:int}/preparing")]
        public Task<IActionResult> MarkPreparing(int id)
            => UpdateStatusInternal(id, "Preparing");

        [HttpPut("{id:int}/ready")]
        public Task<IActionResult> MarkReady(int id)
            => UpdateStatusInternal(id, "Ready");

        [HttpPut("{id:int}/delivered")]
        public Task<IActionResult> MarkDelivered(int id)
            => UpdateStatusInternal(id, "Delivered");

        [HttpPut("{id:int}/cancel")]
        public Task<IActionResult> CancelOrder(int id)
            => UpdateStatusInternal(id, "Cancelled");

        [HttpPut("{id:int}/reject")]
        public Task<IActionResult> RejectOrder(int id, [FromBody] RejectOrderRequest? body = null)
            => UpdateStatusInternal(id, "Rejected", null, body?.Reason ?? body?.RejectionReason);

        // Customer cancellation is explicitly tied to the customer's ID and phone.
        [HttpPost("{id:int}/cancel-by-customer")]
        public async Task<IActionResult> CancelByCustomer(
            int id,
            [FromBody] CustomerCancelOrderRequest request)
        {
            if (id <= 0 || request == null ||
                (request.CustomerID <= 0 && string.IsNullOrWhiteSpace(request.Phone)))
                return BadRequest(new { success = false, message = "بيانات إلغاء الطلب غير مكتملة." });

            await using var connection = DatabaseManager.GetConnection();
            await connection.OpenAsync();
            await using var transaction = await connection.BeginTransactionAsync();
            try
            {
                const string verifySql = @"
                    SELECT o.CustomerID, o.Status
                    FROM orders o
                    LEFT JOIN customers c ON c.CustomerID=o.CustomerID
                    WHERE o.OrderID=@OrderID
                      AND (
                            (@CustomerID > 0 AND o.CustomerID=@CustomerID)
                            OR (@Phone <> '' AND c.Phone=@Phone)
                          )
                    FOR UPDATE;";
                int customerId;
                string status;
                await using (var verify = new MySqlCommand(verifySql, connection, transaction))
                {
                    verify.Parameters.AddWithValue("@OrderID", id);
                    verify.Parameters.AddWithValue("@CustomerID", request.CustomerID);
                    verify.Parameters.AddWithValue("@Phone", (request.Phone ?? "").Trim());
                    await using var reader = await verify.ExecuteReaderAsync();
                    if (!await reader.ReadAsync())
                    {
                        await transaction.RollbackAsync();
                        return NotFound(new { success = false, message = "لم يتم العثور على الطلب أو أن بيانات الزبون غير مطابقة." });
                    }
                    customerId = reader.IsDBNull(reader.GetOrdinal("CustomerID")) ? 0 : reader.GetInt32("CustomerID");
                    status = reader.GetString("Status");
                }

                if (status is not ("New" or "Preparing"))
                {
                    await transaction.RollbackAsync();
                    return Conflict(new { success = false, message = "لا يمكن إلغاء الطلب بعد بدء مرحلة الجاهزية أو التوصيل." });
                }

                const string updateSql = @"
                    UPDATE orders SET Status='Cancelled', DeliveryWorkerID=NULL, UpdatedAt=UTC_TIMESTAMP()
                    WHERE OrderID=@OrderID
                      AND Status IN ('New','Preparing');";
                await using (var update = new MySqlCommand(updateSql, connection, transaction))
                {
                    update.Parameters.AddWithValue("@OrderID", id);
                    if (await update.ExecuteNonQueryAsync() != 1)
                    {
                        await transaction.RollbackAsync();
                        return Conflict(new { success = false, message = "تغيرت حالة الطلب قبل إلغائه. حدّث الصفحة وحاول مجدداً." });
                    }
                }

                if (customerId > 0)
                    await NotificationsController.AddOrderStatusNotificationAsync(connection, transaction, customerId, id, "Cancelled");
                await transaction.CommitAsync();
                return Ok(new { success = true, orderID = id, status = "Cancelled", message = "تم إلغاء الطلب." });
            }
            catch
            {
                await transaction.RollbackAsync();
                throw;
            }
        }

        // =========================================================
        // INTERNAL STATUS UPDATE
        // =========================================================

        private async Task<IActionResult> UpdateStatusInternal(int id, string status, int? requestedWorkerId = null, string? rejectionReason = null)
        {
            await using var connection = DatabaseManager.GetConnection();
            await connection.OpenAsync();
            await using var transaction = await connection.BeginTransactionAsync();

            try
            {
                const string checkSql = """
                    SELECT OrderID, (SELECT COUNT(*) FROM orders o2 WHERE COALESCE(o2.BusinessDate, DATE(o2.OrderDate)) = COALESCE(orders.BusinessDate, DATE(orders.OrderDate)) AND o2.OrderID <= orders.OrderID) AS DailyOrderNumber,
                           CustomerID, OrderType, Status, DeliveryWorkerID, IsArchived
                    FROM orders
                    WHERE OrderID=@OrderID
                    LIMIT 1
                    FOR UPDATE;
                    """;

                string orderType;
                string oldStatus;
                int dailyOrderNumber;
                int? customerId;
                int? currentWorkerId;
                await using (var check = new MySqlCommand(checkSql, connection, transaction))
                {
                    check.Parameters.AddWithValue("@OrderID", id);
                    await using var reader = await check.ExecuteReaderAsync();
                    if (!await reader.ReadAsync() || reader.GetBoolean("IsArchived"))
                    {
                        await transaction.RollbackAsync();
                        return NotFound(new { success = false, message = $"Order #{id} was not found or has been archived." });
                    }

                    dailyOrderNumber = reader.GetInt32("DailyOrderNumber");
                    orderType = reader.GetString("OrderType");
                    oldStatus = reader.GetString("Status");
                    customerId = reader.IsDBNull(reader.GetOrdinal("CustomerID")) ? null : reader.GetInt32("CustomerID");
                    currentWorkerId = reader.IsDBNull(reader.GetOrdinal("DeliveryWorkerID")) ? null : reader.GetInt32("DeliveryWorkerID");
                }

                if ((oldStatus == "Cancelled" || oldStatus == "Rejected") && status != oldStatus)
                {
                    await transaction.RollbackAsync();
                    return BadRequest(new { success = false, message = "لا يمكن إعادة فتح طلب ملغي أو مرفوض." });
                }
                if (oldStatus == "Delivered" && status != "Delivered")
                {
                    await transaction.RollbackAsync();
                    return BadRequest(new { success = false, message = "لا يمكن تغيير حالة طلب تم تسليمه." });
                }

                bool validTransition =
                    (oldStatus == "New" && (status == "Preparing" || status == "Cancelled" || status == "Rejected")) ||
                    (oldStatus == "Preparing" && (status == "Ready" || status == "Cancelled" || status == "Rejected")) ||
                    (oldStatus == "Ready" && (status == "Delivered" || status == "OutForDelivery" || status == "Cancelled" || status == "Rejected")) ||
                    (oldStatus == "OutForDelivery" && (status == "Delivered" || status == "Cancelled" || status == "Rejected")) ||
                    oldStatus == status;
                if (!validTransition)
                {
                    await transaction.RollbackAsync();
                    return BadRequest(new { success = false, message = $"Cannot change order from {oldStatus} to {status}." });
                }

                int? assignedWorkerId = currentWorkerId;
                string assignedWorkerName = string.Empty;
                string assignedWorkerPhone = string.Empty;
                int activeOnDutyDriversCount = 0;
                bool onDutyTrackingAvailable = false;

                if (status == "Preparing" && orderType == "Delivery" && currentWorkerId is null && oldStatus == "New")
                {
                    const string onDutyCountSql = "SELECT COUNT(*) FROM deliveryworkers WHERE IsActive=1 AND IsOnShift=1";
                    await using var onDutyCountCommand = new MySqlCommand(onDutyCountSql, connection, transaction);
                    activeOnDutyDriversCount = Convert.ToInt32(await onDutyCountCommand.ExecuteScalarAsync());
                    onDutyTrackingAvailable = true;
                }

                if (requestedWorkerId.HasValue && requestedWorkerId.Value > 0 && status is not ("Cancelled" or "Rejected"))
                {
                    const string pickWorkerSql = """
                        SELECT DeliveryWorkerID, FullName, COALESCE(Phone,'') AS Phone
                        FROM deliveryworkers
                        WHERE DeliveryWorkerID=@WorkerID AND IsActive=1
                        LIMIT 1;
                        """;
                    await using var pickCmd = new MySqlCommand(pickWorkerSql, connection, transaction);
                    pickCmd.Parameters.AddWithValue("@WorkerID", requestedWorkerId.Value);
                    await using var pickReader = await pickCmd.ExecuteReaderAsync();
                    if (await pickReader.ReadAsync())
                    {
                        assignedWorkerId = pickReader.GetInt32("DeliveryWorkerID");
                        assignedWorkerName = pickReader.GetString("FullName");
                        assignedWorkerPhone = pickReader.GetString("Phone");
                    }
                    await pickReader.CloseAsync();
                }

                bool clearWorker = status is "Cancelled" or "Rejected";
                const string updateSql = """
                    UPDATE orders
                    SET Status=@Status,
                        DeliveryWorkerID=CASE WHEN @ClearWorker=1 THEN NULL WHEN @AssignedWorkerID IS NOT NULL THEN @AssignedWorkerID ELSE DeliveryWorkerID END,
                        DeliveryAssignedAt=CASE WHEN @NewAssignment=1 THEN UTC_TIMESTAMP() ELSE DeliveryAssignedAt END,
                        PickedUpAt=CASE WHEN @Status='OutForDelivery' AND PickedUpAt IS NULL THEN UTC_TIMESTAMP() ELSE PickedUpAt END,
                        RejectionReason=CASE WHEN @Status='Rejected' AND @RejectionReason IS NOT NULL THEN @RejectionReason ELSE RejectionReason END,
                        UpdatedAt=UTC_TIMESTAMP()
                    WHERE OrderID=@OrderID AND Status=@OldStatus AND IsArchived=0;
                    """;
                await using (var update = new MySqlCommand(updateSql, connection, transaction))
                {
                    update.Parameters.AddWithValue("@Status", status);
                    update.Parameters.AddWithValue("@ClearWorker", clearWorker);
                    update.Parameters.AddWithValue("@AssignedWorkerID", assignedWorkerId.HasValue && !clearWorker ? assignedWorkerId.Value : DBNull.Value);
                    update.Parameters.AddWithValue("@NewAssignment", assignedWorkerId.HasValue && !clearWorker && (!currentWorkerId.HasValue || currentWorkerId.Value != assignedWorkerId.Value));
                    update.Parameters.AddWithValue("@OrderID", id);
                    update.Parameters.AddWithValue("@OldStatus", oldStatus);
                    update.Parameters.AddWithValue("@RejectionReason", string.IsNullOrWhiteSpace(rejectionReason) ? DBNull.Value : rejectionReason.Trim());
                    if (await update.ExecuteNonQueryAsync() != 1)
                    {
                        await transaction.RollbackAsync();
                        return Conflict(new { success = false, message = "تغير الطلب أثناء التحديث. حدّث القائمة وحاول مجدداً." });
                    }
                }

                if (assignedWorkerId.HasValue && !clearWorker && string.IsNullOrWhiteSpace(assignedWorkerName))
                {
                    const string assignedSql = "SELECT FullName, COALESCE(Phone,'') AS Phone FROM deliveryworkers WHERE DeliveryWorkerID=@id LIMIT 1";
                    await using var assigned = new MySqlCommand(assignedSql, connection, transaction);
                    assigned.Parameters.AddWithValue("@id", assignedWorkerId.Value);
                    await using var reader = await assigned.ExecuteReaderAsync();
                    if (await reader.ReadAsync())
                    {
                        assignedWorkerName = reader.GetString("FullName");
                        assignedWorkerPhone = reader.GetString("Phone");
                    }
                }

                if (customerId.HasValue && status != oldStatus)
                {
                    await NotificationsController.AddOrderStatusNotificationAsync(
                        connection, transaction, customerId.Value, id, status, rejectionReason);
                }

                await transaction.CommitAsync();
                if (string.Equals(status, "Delivered", StringComparison.OrdinalIgnoreCase))
                {
                    await LoyaltyController.TryAwardCoinsForOrderAsync(connection, id);
                }
                if (customerId.HasValue && status != oldStatus)
                {
                    string arStatus = status switch
                    {
                        "Preparing" => "تم قبول طلبك وهو قيد التحضير 🍳",
                        "Ready" => "طلبك جاهز الآن ✅",
                        "OutForDelivery" => "عامل التوصيل بالطريق إليك 🛵",
                        "Delivered" => "تم توصيل طلبك بنجاح 🎉 وأُضيفت قامشلو كوينز لرصيدك!",
                        "Cancelled" => "تم إلغاء الطلب",
                        "Rejected" => string.IsNullOrWhiteSpace(rejectionReason) ? "عذراً، تم رفض الطلب من قبل المطعم" : $"عذراً، تم رفض الطلب — السبب: {rejectionReason.Trim()}",
                        _ => status
                    };
                    int cidSnap = customerId.Value;
                    string msgSnap = $"طلبك #{id}: {arStatus}";
                    _ = Task.Run(async () => { try { await _firebaseNotifications.SendToAudienceAsync("customer", cidSnap, "مطعم قامشلو 🍔", msgSnap); } catch { } });
                }
                if (assignedWorkerId.HasValue && !clearWorker && status is "Preparing" or "Ready")
                {
                    int widSnap = assignedWorkerId.Value;
                    string dMsgSnap = $"تم إسناد الطلب #{id} إليك — افتح صفحة التوصيل الآن.";
                    _ = Task.Run(async () => { try { await _firebaseNotifications.SendToAudienceAsync("driver", widSnap, "طلب توصيل جديد 🛵", dMsgSnap); } catch { } });
                }
                return Ok(new
                {
                    success = true,
                    orderId = id,
                    dailyOrderNumber,
                    orderNumber = $"Q-{dailyOrderNumber}",
                    orderType,
                    oldStatus,
                    status,
                    deliveryWorkerID = clearWorker ? null : assignedWorkerId,
                    deliveryWorkerName = clearWorker ? null : assignedWorkerName,
                    deliveryWorkerPhone = clearWorker ? null : assignedWorkerPhone,
                    activeOnDutyDriversCount,
                    onDutyTrackingAvailable,
                    message = status == "Rejected"
                        ? $"تم رفض الطلب Q-{dailyOrderNumber}."
                        : assignedWorkerId.HasValue && oldStatus == "New" && status == "Preparing" && orderType == "Delivery"
                            ? $"تم قبول الطلب وإسناده تلقائياً إلى العامل {assignedWorkerName}."
                            : "تم تحديث حالة الطلب بنجاح."
                });
            }
            catch (Exception ex)
            {
                try { await transaction.RollbackAsync(); } catch { }
                return StatusCode(500, new
                {
                    success = false,
                    message = "Failed to update order status.",
                    error = "تعذر إكمال العملية حالياً. يرجى المحاولة لاحقاً."
                });
            }
        }

        // =========================================================
        // READ ORDER FROM MYSQL
        // =========================================================

        private static OrderResponse ReadOrder(MySqlDataReader reader)
        {
            OrderResponse order = new OrderResponse();

            order.OrderID = reader.GetInt32(reader.GetOrdinal("OrderID"));
            order.DailyOrderNumber = reader.GetInt32(reader.GetOrdinal("DailyOrderNumber"));
            order.CustomerID = reader.IsDBNull(reader.GetOrdinal("CustomerID"))
                ? (int?)null
                : reader.GetInt32(reader.GetOrdinal("CustomerID"));
            order.UserID = reader.GetInt32(reader.GetOrdinal("UserID"));
            order.DeliveryZoneID = reader.IsDBNull(reader.GetOrdinal("DeliveryZoneID"))
                ? (int?)null
                : reader.GetInt32(reader.GetOrdinal("DeliveryZoneID"));
            order.OrderDate = reader.GetDateTime(reader.GetOrdinal("OrderDate"));
            order.OrderType = reader.GetString(reader.GetOrdinal("OrderType"));
            order.Status = reader.GetString(reader.GetOrdinal("Status"));
            order.Subtotal = reader.GetDecimal(reader.GetOrdinal("Subtotal"));
            order.DeliveryFee = reader.GetDecimal(reader.GetOrdinal("DeliveryFee"));
            order.DiscountAmount = reader.GetDecimal(reader.GetOrdinal("DiscountAmount"));
            order.TotalAmount = reader.GetDecimal(reader.GetOrdinal("TotalAmount"));
            order.DeliveryAddress = reader.IsDBNull(reader.GetOrdinal("DeliveryAddress"))
                ? null
                : reader.GetString(reader.GetOrdinal("DeliveryAddress"));
            try
            {
                int latOrd = reader.GetOrdinal("CustomerLatitude");
                int lngOrd = reader.GetOrdinal("CustomerLongitude");
                if (!reader.IsDBNull(latOrd)) { order.Latitude = Convert.ToDouble(reader.GetValue(latOrd)); order.CustomerLatitude = order.Latitude; }
                if (!reader.IsDBNull(lngOrd)) { order.Longitude = Convert.ToDouble(reader.GetValue(lngOrd)); order.CustomerLongitude = order.Longitude; }
            }
            catch { }
            order.Notes = reader.IsDBNull(reader.GetOrdinal("Notes"))
                ? null
                : reader.GetString(reader.GetOrdinal("Notes"));
            try { int rIdx = reader.GetOrdinal("RejectionReason"); order.RejectionReason = reader.IsDBNull(rIdx) ? null : reader.GetString(rIdx); } catch { }
            order.CreatedAt = reader.GetDateTime(reader.GetOrdinal("CreatedAt"));
            order.UpdatedAt = reader.GetDateTime(reader.GetOrdinal("UpdatedAt"));
            order.DeliveryWorkerID = reader.IsDBNull(reader.GetOrdinal("DeliveryWorkerID"))
                ? (int?)null
                : reader.GetInt32(reader.GetOrdinal("DeliveryWorkerID"));
            order.DeliveryWorkerName = reader.GetString(reader.GetOrdinal("DeliveryWorkerName"));
            order.DeliveryWorkerPhone = reader.GetString(reader.GetOrdinal("DeliveryWorkerPhone"));
            order.CustomerName = reader.GetString(reader.GetOrdinal("CustomerName"));
            order.CustomerPhone = reader.GetString(reader.GetOrdinal("CustomerPhone"));
            order.UserName = reader.GetString(reader.GetOrdinal("UserName"));
            order.DeliveryZoneName = reader.GetString(reader.GetOrdinal("DeliveryZoneName"));

            if (!string.IsNullOrWhiteSpace(order.Notes))
            {
                ExtractLocation(order.Notes, out double? latitude, out double? longitude);
                order.Latitude = latitude;
                order.Longitude = longitude;
            }

            return order;
        }

        private static void ExtractLocation(
            string notes,
            out double? latitude,
            out double? longitude)
        {
            latitude = null;
            longitude = null;

            const string prefix = "Location:";
            int index = notes.IndexOf(prefix, StringComparison.OrdinalIgnoreCase);
            if (index < 0)
                return;

            string location = notes.Substring(index + prefix.Length).Trim();
            string[] parts = location.Split(',', StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length < 2)
                return;

            if (double.TryParse(
                parts[0].Trim(),
                System.Globalization.NumberStyles.Float,
                System.Globalization.CultureInfo.InvariantCulture,
                out double lat))
            {
                latitude = lat;
            }

            if (double.TryParse(
                parts[1].Trim(),
                System.Globalization.NumberStyles.Float,
                System.Globalization.CultureInfo.InvariantCulture,
                out double lng))
            {
                longitude = lng;
            }
        }

        private static bool IsPointInsideDeliveryPolygon(
            double latitude,
            double longitude,
            string coordinatesJson)
        {
            try
            {
                using JsonDocument document = JsonDocument.Parse(coordinatesJson);
                if (document.RootElement.ValueKind != JsonValueKind.Array)
                    return false;

                var points = new List<(double Lat, double Lng)>();

                foreach (JsonElement point in document.RootElement.EnumerateArray())
                {
                    if (point.ValueKind != JsonValueKind.Object)
                        return false;

                    if (!TryGetCoordinate(point, "lat", out double lat) ||
                        !TryGetCoordinate(point, "lng", out double lng))
                        return false;

                    if (!double.IsFinite(lat) || !double.IsFinite(lng) ||
                        lat < -90 || lat > 90 || lng < -180 || lng > 180)
                        return false;

                    points.Add((lat, lng));
                }

                if (points.Count < 3)
                    return false;

                bool inside = false;
                int j = points.Count - 1;

                for (int i = 0; i < points.Count; i++)
                {
                    double xi = points[i].Lng;
                    double yi = points[i].Lat;
                    double xj = points[j].Lng;
                    double yj = points[j].Lat;

                    bool intersects =
                        ((yi > latitude) != (yj > latitude)) &&
                        (longitude < (xj - xi) * (latitude - yi) /
                         (yj - yi) + xi);

                    if (intersects)
                        inside = !inside;

                    j = i;
                }

                return inside;
            }
            catch (JsonException)
            {
                return false;
            }
        }

        private static bool TryGetCoordinate(
            JsonElement element,
            string propertyName,
            out double value)
        {
            value = 0;

            foreach (JsonProperty property in element.EnumerateObject())
            {
                if (string.Equals(property.Name, propertyName,
                    StringComparison.OrdinalIgnoreCase))
                {
                    return property.Value.ValueKind == JsonValueKind.Number &&
                           property.Value.TryGetDouble(out value);
                }
            }

            return false;
        }

        private static string NormalizeOrderType(string? value)
        {
            if (string.IsNullOrWhiteSpace(value))
                return "";

            string text = value.Trim();

            if (text.Equals("Delivery", StringComparison.OrdinalIgnoreCase))
                return "Delivery";
            if (text.Equals("Takeaway", StringComparison.OrdinalIgnoreCase))
                return "Takeaway";
            if (text.Equals("Dine In", StringComparison.OrdinalIgnoreCase))
                return "Dine In";

            if (text == "توصيل")
                return "Delivery";
            if (text == "سفري")
                return "Takeaway";
            if (text == "داخل المطعم")
                return "Dine In";

            if (text == "گەیاندن")
                return "Delivery";
            if (text == "وەرگرتن")
                return "Takeaway";
            if (text == "ناو خواردنگە")
                return "Dine In";

            return "";
        }

        private static string NormalizeStatus(string? value)
        {
            if (string.IsNullOrWhiteSpace(value))
                return "";

            string text = value.Trim();

            if (text.Equals("New", StringComparison.OrdinalIgnoreCase))
                return "New";
            if (text.Equals("Preparing", StringComparison.OrdinalIgnoreCase))
                return "Preparing";
            if (text.Equals("Ready", StringComparison.OrdinalIgnoreCase))
                return "Ready";
            if (text.Equals("Delivered", StringComparison.OrdinalIgnoreCase))
                return "Delivered";
            if (text.Equals("Cancelled", StringComparison.OrdinalIgnoreCase))
                return "Cancelled";
            if (text.Equals("Rejected", StringComparison.OrdinalIgnoreCase))
                return "Rejected";
            if (text.Equals("OutForDelivery", StringComparison.OrdinalIgnoreCase) ||
                text.Equals("Out for delivery", StringComparison.OrdinalIgnoreCase))
                return "OutForDelivery";

            if (text == "جديد")
                return "New";
            if (text == "قيد التحضير")
                return "Preparing";
            if (text == "جاهز")
                return "Ready";
            if (text == "تم التسليم")
                return "Delivered";
            if (text == "ملغي")
                return "Cancelled";
            if (text == "مرفوض" || text == "رفض" || text == "رفض الطلب")
                return "Rejected";
            if (text == "خرج للتوصيل" || text == "بالطريق إليك" || text == "في الطريق")
                return "OutForDelivery";

            if (text == "نوێ")
                return "New";
            if (text == "لە ئامادەکردندایە")
                return "Preparing";
            if (text == "ئامادەیە")
                return "Ready";
            if (text == "گەیەندرا")
                return "Delivered";
            if (text == "هەڵوەشێنراوەتەوە")
                return "Cancelled";
            if (text == "ڕەتکرایەوە")
                return "Rejected";

            return "";
        }
    }

    public class CreateOrderRequest
    {
        public string? CustomerName { get; set; }
        public string? CustomerPhone { get; set; }
        public string? Address { get; set; }
        public string? Notes { get; set; }
        public string? RejectionReason { get; set; }
        public string OrderType { get; set; } = "Delivery";
        public decimal DeliveryFee { get; set; } = 0m;
        public decimal DiscountAmount { get; set; } = 0m;
        public decimal LoyaltyDiscountAmount { get; set; } = 0m;
        public string? CouponCode { get; set; }
        public int? CustomerID { get; set; }
        public string? InitialStatus { get; set; }
        public double? Latitude { get; set; }
        public double? Longitude { get; set; }
        public double? CustomerLatitude { get; set; }
        public double? CustomerLongitude { get; set; }
        public List<OrderItemRequest> Items { get; set; } = new List<OrderItemRequest>();
        public int CreatedOrderID { get; set; }
    }

    public class OrderItemRequest
    {
        public int ProductID { get; set; }
        public int Quantity { get; set; }
        public string? Notes { get; set; }
        public List<int>? AddonIDs { get; set; }
        public string? AddonsText { get; set; }
    }

    public class UpdateorderstatusRequest
    {
        public string Status { get; set; } = "";
        public int? DeliveryWorkerID { get; set; }
        public string? RejectionReason { get; set; }
    }

    public sealed class RejectOrderRequest
    {
        public string? Reason { get; set; }
        public string? RejectionReason { get; set; }
    }

    public sealed class CustomerCancelOrderRequest
    {
        public int CustomerID { get; set; }
        public string Phone { get; set; } = "";
    }

    public class OrderResponse
    {
        public int OrderID { get; set; }
        public int DailyOrderNumber { get; set; }
        public int? CustomerID { get; set; }
        public int UserID { get; set; }
        public int? DeliveryZoneID { get; set; }
        public DateTime OrderDate { get; set; }
        public string OrderType { get; set; } = "";
        public string Status { get; set; } = "";
        public decimal Subtotal { get; set; }
        public decimal DeliveryFee { get; set; }
        public decimal DiscountAmount { get; set; }
        public decimal TotalAmount { get; set; }
        public string? DeliveryAddress { get; set; }
        public string? Notes { get; set; }
        public DateTime CreatedAt { get; set; }
        public DateTime UpdatedAt { get; set; }
        public string CustomerName { get; set; } = "";
        public string CustomerPhone { get; set; } = "";
        public string UserName { get; set; } = "";
        public string DeliveryZoneName { get; set; } = "";
        public int? DeliveryWorkerID { get; set; }
        public string DeliveryWorkerName { get; set; } = "";
        public string DeliveryWorkerPhone { get; set; } = "";
        public double? Latitude { get; set; }
        public double? Longitude { get; set; }
        public double? CustomerLatitude { get; set; }
        public double? CustomerLongitude { get; set; }
        public string? RejectionReason { get; set; }
        public List<OrderDetailResponse> Items { get; set; } = new List<OrderDetailResponse>();
    }

    public class OrderDetailResponse
    {
        public int OrderDetailID { get; set; }
        public int OrderID { get; set; }
        public int ProductID { get; set; }
        public string ProductName { get; set; } = "";
        public int CategoryID { get; set; }
        public string? ImageURL { get; set; }
        public int Quantity { get; set; }
        public decimal UnitPrice { get; set; }
        public decimal TotalPrice { get; set; }
        public string? Notes { get; set; }
    }
}