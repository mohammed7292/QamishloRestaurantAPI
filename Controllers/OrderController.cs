using Microsoft.AspNetCore.Mvc;
using MySqlConnector;
using QamishloRestaurantAPI.Database;
using QamishloRestaurantAPI.Services;
using System;
using System.Collections.Generic;
using System.Text.Json;
using System.Threading.Tasks;

namespace QamishloRestaurantAPI.Controllers
{
    [ApiController]
    [Route("api/[controller]")]
    public class ordersController : ControllerBase
    {
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
            // =====================================================
            // VALIDATION
            // =====================================================

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

            // =====================================================
            // NORMALIZE ORDER TYPE
            // =====================================================

            string orderType =
                NormalizeOrderType(request.OrderType);

            if (orderType == "")
            {
                return BadRequest(new
                {
                    success = false,
                    message =
                        "Invalid order type. Use Dine In, Takeaway or Delivery."
                });
            }

            // =====================================================
            // DELIVERY VALIDATION
            // =====================================================

            if (orderType == "Delivery")
            {
                if (!request.Latitude.HasValue ||
                    !request.Longitude.HasValue)
                {
                    return BadRequest(new
                    {
                        success = false,
                        message =
                            "Delivery location is required."
                    });
                }
            }

            // =====================================================
            // QUANTITY VALIDATION
            // =====================================================

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
                        message =
                            "Product quantity must be greater than zero."
                    });
                }

                if (item.Quantity > 100)
                {
                    return BadRequest(new
                    {
                        success = false,
                        message =
                            "Product quantity cannot exceed 100."
                    });
                }
            }

            // Discounts are calculated by the server from restaurantsettings.
            // Never trust a customer-supplied discount amount.
            decimal discount = 0m;

            try
            {
                using MySqlConnection connection =
                    DatabaseManager.GetConnection();

                await connection.OpenAsync();

                using MySqlTransaction transaction =
                    await connection.BeginTransactionAsync();

                try
                {
                    // =================================================
                    // 0. VALIDATE RESTAURANT / CUSTOMER APP AVAILABILITY
                    // =================================================
                    const string availabilityQuery = @"
                        SELECT IsCustomerAppEnabled, IsRestaurantOpen, IsDeliveryEnabled,
                               OpeningTime, ClosingTime, DeliveryClosedMessage,
                               FirstOrderDiscountEnabled, FirstOrderDiscountPercent
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

                    // =================================================
                    // 1. FIND OR CREATE CUSTOMER (OPTIONAL FOR WALK-IN ORDERS)
                    // =================================================
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

                    // =================================================
                    // 2. CALCULATE SUBTOTAL
                    // =================================================

                    decimal subtotal = 0m;

                    // Store prices so we don't query them twice
                    Dictionary<int, decimal> productPrices =
                        new Dictionary<int, decimal>();

                    foreach (OrderItemRequest item in request.Items)
                    {
                        string priceQuery = @"
                            SELECT Price
                            FROM products
                            WHERE ProductID = @ProductID
                              AND (IsAvailable = 1 OR (AvailableUntil IS NOT NULL AND AvailableUntil <= UTC_TIMESTAMP()))
                            LIMIT 1;";

                        using (
                            MySqlCommand command =
                                new MySqlCommand(
                                    priceQuery,
                                    connection,
                                    transaction))
                        {
                            command.Parameters.AddWithValue(
                                "@ProductID",
                                item.ProductID);

                            object? result =
                                await command.ExecuteScalarAsync();

                            if (result == null ||
                                result == DBNull.Value)
                            {
                                await transaction.RollbackAsync();

                                return BadRequest(new
                                {
                                    success = false,
                                    message =
                                        $"Product {item.ProductID} is not available."
                                });
                            }

                            decimal price =
                                Convert.ToDecimal(result);

                            productPrices[item.ProductID] =
                                price;

                            subtotal +=
                                price * item.Quantity;
                        }
                    }

                    // =================================================
                    // 2B. APPLY SERVER-VERIFIED FIRST-ORDER DISCOUNT
                    // =================================================
                    if (customerId.HasValue && firstOrderDiscountEnabled && firstOrderDiscountPercent > 0m)
                    {
                        const string previousOrdersQuery = @"
                            SELECT COUNT(*) FROM orders
                            WHERE CustomerID = @CustomerID
                              AND Status <> 'Cancelled';";
                        using (MySqlCommand previousOrdersCommand = new MySqlCommand(previousOrdersQuery, connection, transaction))
                        {
                            previousOrdersCommand.Parameters.AddWithValue("@CustomerID", customerId.Value);
                            int previousOrders = Convert.ToInt32(await previousOrdersCommand.ExecuteScalarAsync());
                            if (previousOrders == 0)
                            {
                                decimal safePercent = Math.Clamp(firstOrderDiscountPercent, 0m, 100m);
                                discount = Math.Round(subtotal * safePercent / 100m, 2, MidpointRounding.AwayFromZero);
                            }
                        }
                    }

                    // =================================================
                    // 3. VALIDATE DELIVERY ZONE + MINIMUM ORDER
                    // Server values are authoritative; never trust the fee sent by the app.
                    // =================================================

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

                        using (MySqlCommand minimumCommand =
                            new MySqlCommand(minimumQuery, connection, transaction))
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

                        using (MySqlCommand zonesCommand =
                            new MySqlCommand(zonesQuery, connection, transaction))
                        using (MySqlDataReader zonesReader =
                            await zonesCommand.ExecuteReaderAsync())
                        {
                            while (await zonesReader.ReadAsync())
                            {
                                int candidateZoneId = zonesReader.GetInt32(
                                    zonesReader.GetOrdinal("DeliveryZoneID"));
                                decimal candidateFee = zonesReader.GetDecimal(
                                    zonesReader.GetOrdinal("DeliveryFee"));
                                string coordinatesJson = zonesReader.GetString(
                                    zonesReader.GetOrdinal("ZoneCoordinates"));

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

                    // =================================================
                    // 4. CALCULATE TOTAL
                    // =================================================

                    decimal total =
                        subtotal +
                        deliveryFee -
                        discount;

                    if (total < 0)
                    {
                        total = 0;
                    }

                    // =================================================
                    // 5. PREPARE NOTES
                    // =================================================

                    string finalNotes =
                        request.Notes?.Trim() ?? "";

                    if (request.Latitude.HasValue &&
                        request.Longitude.HasValue)
                    {
                        string locationText =
                            $"Location: " +
                            $"{request.Latitude.Value:F6}, " +
                            $"{request.Longitude.Value:F6}";

                        if (!string.IsNullOrWhiteSpace(finalNotes))
                        {
                            finalNotes +=
                                Environment.NewLine;
                        }

                        finalNotes += locationText;
                    }

                    // =================================================
                    // 6. CREATE ORDER
                    // =================================================

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
                            'New',
                            @Subtotal,
                            @DeliveryFee,
                            @DiscountAmount,
                            @TotalAmount,
                            @DeliveryAddress,
                            @Latitude,
                            @Longitude,
                            @Notes
                        );";

                    using (
                        MySqlCommand command =
                            new MySqlCommand(
                                insertOrderQuery,
                                connection,
                                transaction))
                    {
                        command.Parameters.AddWithValue(
                            "@CustomerID",
                            (object?)customerId ?? DBNull.Value);

                        // =================================================
                        // CUSTOMER APP orders
                        // =================================================
                        // UserID = 1 means System Administrator.
                        // Later, if needed, this can be replaced
                        // with a real system/API user.
                        // =================================================

                        command.Parameters.AddWithValue(
                            "@UserID",
                            1);

                        command.Parameters.AddWithValue(
                            "@DeliveryZoneID",
                            (object?)deliveryZoneId ?? DBNull.Value);

                        DateTime baghdadOrderDate = ExpensesController.BaghdadNow();
                        command.Parameters.AddWithValue("@OrderDate", baghdadOrderDate);
                        command.Parameters.AddWithValue("@BusinessDate", ExpensesController.BusinessDate(baghdadOrderDate));

                        command.Parameters.AddWithValue(
                            "@OrderType",
                            orderType);

                        command.Parameters.AddWithValue(
                            "@Subtotal",
                            subtotal);

                        command.Parameters.AddWithValue(
                            "@DeliveryFee",
                            deliveryFee);

                        command.Parameters.AddWithValue(
                            "@DiscountAmount",
                            discount);

                        command.Parameters.AddWithValue(
                            "@TotalAmount",
                            total);

                        command.Parameters.AddWithValue(
                            "@DeliveryAddress",
                            string.IsNullOrWhiteSpace(
                                request.Address)
                                ? DBNull.Value
                                : request.Address.Trim());

                        command.Parameters.AddWithValue("@Latitude", (object?)request.Latitude ?? DBNull.Value);
                        command.Parameters.AddWithValue("@Longitude", (object?)request.Longitude ?? DBNull.Value);

                        command.Parameters.AddWithValue(
                            "@Notes",
                            string.IsNullOrWhiteSpace(
                                finalNotes)
                                ? DBNull.Value
                                : finalNotes);

                        await command.ExecuteNonQueryAsync();

                        request.CreatedOrderID =
                            Convert.ToInt32(
                                command.LastInsertedId);
                    }

                    int orderId =
                        request.CreatedOrderID;

                    // =================================================
                    // 7. INSERT ORDER DETAILS
                    // =================================================

                    foreach (OrderItemRequest item in request.Items)
                    {
                        decimal unitPrice =
                            productPrices[item.ProductID];

                        decimal itemTotal =
                            unitPrice *
                            item.Quantity;

                        string insertDetailQuery = @"
                            INSERT INTO orderdetails
                            (
                                OrderID,
                                ProductID,
                                Quantity,
                                UnitPrice,
                                TotalPrice,
                                Notes
                            )
                            VALUES
                            (
                                @OrderID,
                                @ProductID,
                                @Quantity,
                                @UnitPrice,
                                @TotalPrice,
                                @Notes
                            );";

                        using (
                            MySqlCommand command =
                                new MySqlCommand(
                                    insertDetailQuery,
                                    connection,
                                    transaction))
                        {
                            command.Parameters.AddWithValue(
                                "@OrderID",
                                orderId);

                            command.Parameters.AddWithValue(
                                "@ProductID",
                                item.ProductID);

                            command.Parameters.AddWithValue(
                                "@Quantity",
                                item.Quantity);

                            command.Parameters.AddWithValue(
                                "@UnitPrice",
                                unitPrice);

                            command.Parameters.AddWithValue(
                                "@TotalPrice",
                                itemTotal);

                            command.Parameters.AddWithValue(
                                "@Notes",
                                string.IsNullOrWhiteSpace(
                                    item.Notes)
                                    ? DBNull.Value
                                    : item.Notes.Trim());

                            await command.ExecuteNonQueryAsync();
                        }
                    }

                    // =================================================
                    // 8. COMMIT
                    // =================================================

                    await transaction.CommitAsync();

                    // Dispatch a high-priority notification to registered restaurant devices.
                    // Push failures are logged and must never invalidate an accepted customer order.
                    await _firebaseNotifications.NotifyRestaurantNewOrderAsync(
                        orderId, orderType, total);

                    // =================================================
                    // 9. RESPONSE
                    // =================================================

                    return Ok(new
                    {
                        success = true,
                        message = "Order created successfully.",

                        orderId = orderId,

                        customerId = customerId,

                        orderType = orderType,

                        subtotal = subtotal,

                        deliveryFee = deliveryFee,

                        deliveryZoneId = deliveryZoneId,

                        discountAmount = discount,

                        total = total,

                        status = "New",

                        latitude =
                            request.Latitude,

                        longitude =
                            request.Longitude
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
                return StatusCode(
                    500,
                    new
                    {
                        success = false,
                        message = "Failed to create order.",
                        error = "تعذر إكمال العملية بسبب خطأ داخلي."
                    });
            }
        }

        // =========================================================
        // GET ALL orders
        // GET: /api/orders
        // =========================================================

        [HttpGet]
        public async Task<IActionResult> Getorders(
            [FromQuery] string? status = null,
            [FromQuery] string? type = null,
            [FromQuery] string? search = null)
        {
            try
            {
                using MySqlConnection connection =
                    DatabaseManager.GetConnection();

                await connection.OpenAsync();

                string query = @"
                    SELECT
                        o.OrderID,
                        o.CustomerID,
                        o.UserID,
                        o.DeliveryZoneID,
                        o.OrderDate,
                        o.OrderType,
                        o.Status,
                        o.Subtotal,
                        o.DeliveryFee,
                        o.DiscountAmount,
                        o.TotalAmount,
                        o.DeliveryAddress,
                        o.Notes,
                        o.CreatedAt,
                        o.UpdatedAt,
                        o.DeliveryWorkerID,

                        COALESCE(
                            c.FullName,
                            'Walk-in Customer'
                        ) AS CustomerName,

                        COALESCE(
                            c.Phone,
                            ''
                        ) AS CustomerPhone,

                        COALESCE(
                            u.FullName,
                            'Unknown'
                        ) AS UserName,

                        COALESCE(
                            dz.ZoneName,
                            ''
                        ) AS DeliveryZoneName,

                        COALESCE(
                            dw.FullName,
                            ''
                        ) AS DeliveryWorkerName,

                        COALESCE(
                            dw.Phone,
                            ''
                        ) AS DeliveryWorkerPhone

                    FROM orders o

                    LEFT JOIN customers c
                        ON o.CustomerID =
                           c.CustomerID

                    LEFT JOIN users u
                        ON o.UserID =
                           u.UserID

                    LEFT JOIN deliveryzones dz
                        ON o.DeliveryZoneID =
                           dz.DeliveryZoneID

                    LEFT JOIN deliveryworkers dw
                        ON o.DeliveryWorkerID =
                           dw.DeliveryWorkerID

                    WHERE o.IsArchived = 0
                ";

                List<MySqlParameter> parameters =
                    new List<MySqlParameter>();

                // =====================================================
                // STATUS FILTER
                // =====================================================

                if (!string.IsNullOrWhiteSpace(status))
                {
                    query +=
                        " AND o.Status = @Status ";

                    parameters.Add(
                        new MySqlParameter(
                            "@Status",
                            status.Trim()));
                }

                // =====================================================
                // TYPE FILTER
                // =====================================================

                if (!string.IsNullOrWhiteSpace(type))
                {
                    string normalizedType =
                        NormalizeOrderType(type);

                    if (normalizedType != "")
                    {
                        query +=
                            " AND o.OrderType = @OrderType ";

                        parameters.Add(
                            new MySqlParameter(
                                "@OrderType",
                                normalizedType));
                    }
                }

                // =====================================================
                // SEARCH
                // =====================================================

                if (!string.IsNullOrWhiteSpace(search))
                {
                    query += @"
                        AND
                        (
                            CAST(o.OrderID AS CHAR)
                                LIKE @Search

                            OR c.FullName
                                LIKE @Search

                            OR c.Phone
                                LIKE @Search
                        ) ";

                    parameters.Add(
                        new MySqlParameter(
                            "@Search",
                            "%" +
                            search.Trim() +
                            "%"));
                }

                query += @"
                    ORDER BY
                        o.OrderID DESC;";

                using MySqlCommand command =
                    new MySqlCommand(
                        query,
                        connection);

                foreach (MySqlParameter parameter
                         in parameters)
                {
                    command.Parameters.Add(parameter);
                }

                using MySqlDataReader reader =
                    await command.ExecuteReaderAsync();

                List<OrderResponse> orders =
                    new List<OrderResponse>();

                while (await reader.ReadAsync())
                {
                    OrderResponse order =
                        ReadOrder(reader);

                    orders.Add(order);
                }

                return Ok(new
                {
                    success = true,
                    count = orders.Count,
                    orders = orders
                });
            }
            catch (Exception ex)
            {
                return StatusCode(
                    500,
                    new
                    {
                        success = false,
                        message = "Failed to load orders.",
                        error = "تعذر إكمال العملية بسبب خطأ داخلي."
                    });
            }
        }

        // =========================================================
        // GET CUSTOMER ORDER HISTORY
        // GET: /api/orders/customer/5
        // =========================================================

        [HttpGet("customer/{customerId:int}")]
        public async Task<IActionResult> GetCustomerOrderHistory(
            int customerId)
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
                using MySqlConnection connection =
                    DatabaseManager.GetConnection();

                await connection.OpenAsync();

                string query = @"
                    SELECT
                        o.OrderID,
                        o.CustomerID,
                        o.UserID,
                        o.DeliveryZoneID,
                        o.OrderDate,
                        o.OrderType,
                        o.Status,
                        o.Subtotal,
                        o.DeliveryFee,
                        o.DiscountAmount,
                        o.TotalAmount,
                        o.DeliveryAddress,
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
                    LEFT JOIN customers c
                        ON o.CustomerID = c.CustomerID
                    LEFT JOIN users u
                        ON o.UserID = u.UserID
                    LEFT JOIN deliveryzones dz
                        ON o.DeliveryZoneID = dz.DeliveryZoneID
                    LEFT JOIN deliveryworkers dw
                        ON o.DeliveryWorkerID = dw.DeliveryWorkerID
                    WHERE o.CustomerID = @CustomerID
                    ORDER BY o.OrderDate DESC, o.OrderID DESC;";

                using MySqlCommand command =
                    new MySqlCommand(query, connection);

                command.Parameters.AddWithValue(
                    "@CustomerID",
                    customerId);

                using MySqlDataReader reader =
                    await command.ExecuteReaderAsync();

                List<OrderResponse> orders =
                    new List<OrderResponse>();

                while (await reader.ReadAsync())
                {
                    orders.Add(ReadOrder(reader));
                }

                return Ok(new
                {
                    success = true,
                    count = orders.Count,
                    orders = orders
                });
            }
            catch (Exception ex)
            {
                return StatusCode(
                    500,
                    new
                    {
                        success = false,
                        message = "Failed to load customer order history.",
                        error = "تعذر إكمال العملية بسبب خطأ داخلي."
                    });
            }
        }

        // =========================================================
        // GET DELIVERY orders
        // GET: /api/orders/delivery
        // =========================================================

        [HttpGet("delivery")]
        public async Task<IActionResult> GetDeliveryorders(
            [FromQuery] string? status = null)
        {
            try
            {
                using MySqlConnection connection =
                    DatabaseManager.GetConnection();

                await connection.OpenAsync();

                string query = @"
                    SELECT
                        o.OrderID,
                        o.CustomerID,
                        o.UserID,
                        o.DeliveryZoneID,
                        o.OrderDate,
                        o.OrderType,
                        o.Status,
                        o.Subtotal,
                        o.DeliveryFee,
                        o.DiscountAmount,
                        o.TotalAmount,
                        o.DeliveryAddress,
                        o.Notes,
                        o.CreatedAt,
                        o.UpdatedAt,
                        o.DeliveryWorkerID,

                        COALESCE(
                            c.FullName,
                            'Unknown Customer'
                        ) AS CustomerName,

                        COALESCE(
                            c.Phone,
                            ''
                        ) AS CustomerPhone,

                        COALESCE(
                            u.FullName,
                            'Unknown'
                        ) AS UserName,

                        COALESCE(
                            dz.ZoneName,
                            ''
                        ) AS DeliveryZoneName,

                        COALESCE(
                            dw.FullName,
                            ''
                        ) AS DeliveryWorkerName,

                        COALESCE(
                            dw.Phone,
                            ''
                        ) AS DeliveryWorkerPhone

                    FROM orders o

                    LEFT JOIN customers c
                        ON o.CustomerID =
                           c.CustomerID

                    LEFT JOIN users u
                        ON o.UserID =
                           u.UserID

                    LEFT JOIN deliveryzones dz
                        ON o.DeliveryZoneID =
                           dz.DeliveryZoneID

                    LEFT JOIN deliveryworkers dw
                        ON o.DeliveryWorkerID =
                           dw.DeliveryWorkerID

                    WHERE o.OrderType = 'Delivery' AND o.IsArchived = 0
                ";

                if (!string.IsNullOrWhiteSpace(status))
                {
                    query +=
                        " AND o.Status = @Status ";
                }

                query +=
                    " ORDER BY o.OrderID DESC;";

                using MySqlCommand command =
                    new MySqlCommand(
                        query,
                        connection);

                if (!string.IsNullOrWhiteSpace(status))
                {
                    command.Parameters.AddWithValue(
                        "@Status",
                        status.Trim());
                }

                using MySqlDataReader reader =
                    await command.ExecuteReaderAsync();

                List<OrderResponse> orders =
                    new List<OrderResponse>();

                while (await reader.ReadAsync())
                {
                    orders.Add(
                        ReadOrder(reader));
                }

                return Ok(new
                {
                    success = true,
                    count = orders.Count,
                    orders = orders
                });
            }
            catch (Exception ex)
            {
                return StatusCode(
                    500,
                    new
                    {
                        success = false,
                        message =
                            "Failed to load delivery orders.",
                        error = "تعذر إكمال العملية بسبب خطأ داخلي."
                    });
            }
        }

        // =========================================================
        // GET SINGLE ORDER
        // GET: /api/orders/5
        // =========================================================

        [HttpGet("{id:int}")]
        public async Task<IActionResult> GetOrder(
            int id)
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
                using MySqlConnection connection =
                    DatabaseManager.GetConnection();

                await connection.OpenAsync();

                // =================================================
                // ORDER
                // =================================================

                string orderQuery = @"
                    SELECT
                        o.OrderID,
                        o.CustomerID,
                        o.UserID,
                        o.DeliveryZoneID,
                        o.OrderDate,
                        o.OrderType,
                        o.Status,
                        o.Subtotal,
                        o.DeliveryFee,
                        o.DiscountAmount,
                        o.TotalAmount,
                        o.DeliveryAddress,
                        o.Notes,
                        o.CreatedAt,
                        o.UpdatedAt,
                        o.DeliveryWorkerID,

                        COALESCE(
                            c.FullName,
                            'Walk-in Customer'
                        ) AS CustomerName,

                        COALESCE(
                            c.Phone,
                            ''
                        ) AS CustomerPhone,

                        COALESCE(
                            u.FullName,
                            'Unknown'
                        ) AS UserName,

                        COALESCE(
                            dz.ZoneName,
                            ''
                        ) AS DeliveryZoneName,

                        COALESCE(
                            dw.FullName,
                            ''
                        ) AS DeliveryWorkerName,

                        COALESCE(
                            dw.Phone,
                            ''
                        ) AS DeliveryWorkerPhone

                    FROM orders o

                    LEFT JOIN customers c
                        ON o.CustomerID =
                           c.CustomerID

                    LEFT JOIN users u
                        ON o.UserID =
                           u.UserID

                    LEFT JOIN deliveryzones dz
                        ON o.DeliveryZoneID =
                           dz.DeliveryZoneID

                    LEFT JOIN deliveryworkers dw
                        ON o.DeliveryWorkerID =
                           dw.DeliveryWorkerID

                    WHERE o.OrderID = @OrderID
                    LIMIT 1;";

                OrderResponse? order = null;

                using (
                    MySqlCommand command =
                        new MySqlCommand(
                            orderQuery,
                            connection))
                {
                    command.Parameters.AddWithValue(
                        "@OrderID",
                        id);

                    using MySqlDataReader reader =
                        await command.ExecuteReaderAsync();

                    if (await reader.ReadAsync())
                    {
                        order =
                            ReadOrder(reader);
                    }
                }

                if (order == null)
                {
                    return NotFound(new
                    {
                        success = false,
                        message =
                            $"Order #{id} was not found."
                    });
                }

                // =================================================
                // ORDER DETAILS
                // =================================================

                string detailsQuery = @"
                    SELECT
                        od.OrderDetailID,
                        od.OrderID,
                        od.ProductID,
                        p.ProductName,
                        p.CategoryID,
                        p.ImageURL,
                        od.Quantity,
                        od.UnitPrice,
                        od.TotalPrice,
                        od.Notes

                    FROM orderdetails od

                    INNER JOIN products p
                        ON od.ProductID =
                           p.ProductID

                    WHERE od.OrderID = @OrderID

                    ORDER BY
                        od.OrderDetailID ASC;";

                using (
                    MySqlCommand command =
                        new MySqlCommand(
                            detailsQuery,
                            connection))
                {
                    command.Parameters.AddWithValue(
                        "@OrderID",
                        id);

                    using MySqlDataReader reader =
                        await command.ExecuteReaderAsync();

                    while (await reader.ReadAsync())
                    {
                        order.Items.Add(
                            new OrderDetailResponse
                            {
                                OrderDetailID =
                                    reader.GetInt32(
                                        "OrderDetailID"),

                                OrderID =
                                    reader.GetInt32(
                                        "OrderID"),

                                ProductID =
                                    reader.GetInt32(
                                        "ProductID"),

                                ProductName =
                                    reader.GetString(
                                        "ProductName"),

                                CategoryID = reader.GetInt32("CategoryID"),

                                ImageURL =
                                    reader.IsDBNull(
                                        reader.GetOrdinal(
                                            "ImageURL"))
                                        ? null
                                        : reader.GetString(
                                            "ImageURL"),

                                Quantity =
                                    reader.GetInt32(
                                        "Quantity"),

                                UnitPrice =
                                    reader.GetDecimal(
                                        "UnitPrice"),

                                TotalPrice =
                                    reader.GetDecimal(
                                        "TotalPrice"),

                                Notes =
                                    reader.IsDBNull(
                                        reader.GetOrdinal(
                                            "Notes"))
                                        ? null
                                        : reader.GetString(
                                            "Notes")
                            });
                    }
                }

                // =================================================
                // RETURN
                // =================================================

                return Ok(new
                {
                    success = true,
                    order = order
                });
            }
            catch (Exception ex)
            {
                return StatusCode(
                    500,
                    new
                    {
                        success = false,
                        message =
                            "Failed to load order.",
                        error =
                            "تعذر إكمال العملية حالياً. يرجى المحاولة لاحقاً."
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
            [FromQuery] int customerId,
            [FromQuery] string phone)
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
                using MySqlConnection connection =
                    DatabaseManager.GetConnection();

                await connection.OpenAsync();

                string query = @"
                    SELECT
                        o.OrderID,
                        o.CustomerID,
                        o.OrderType,
                        o.Status,
                        o.TotalAmount,
                        o.DeliveryAddress,
                        o.Notes,
                        o.CustomerLatitude,
                        o.CustomerLongitude,
                        o.OrderDate,
                        o.DeliveryWorkerID,

                        COALESCE(
                            c.FullName,
                            ''
                        ) AS CustomerName,

                        COALESCE(
                            c.Phone,
                            ''
                        ) AS CustomerPhone,

                        COALESCE(
                            dw.FullName,
                            ''
                        ) AS DeliveryWorkerName,

                        COALESCE(
                            dw.Phone,
                            ''
                        ) AS DeliveryWorkerPhone,

                        dw.CurrentLatitude AS DriverLatitude,
                        dw.CurrentLongitude AS DriverLongitude,
                        dw.LastLocationAt AS DriverLastLocationAt

                    FROM orders o

                    LEFT JOIN customers c
                        ON o.CustomerID =
                           c.CustomerID

                    LEFT JOIN deliveryworkers dw
                        ON o.DeliveryWorkerID =
                           dw.DeliveryWorkerID

                    WHERE o.OrderID = @OrderID
                      AND o.CustomerID = @CustomerID
                      AND c.Phone = @Phone

                    LIMIT 1;";

                using MySqlCommand command =
                    new MySqlCommand(
                        query,
                        connection);

                command.Parameters.AddWithValue(
                    "@OrderID",
                    id);
                command.Parameters.AddWithValue("@CustomerID", customerId);
                command.Parameters.AddWithValue("@Phone", (phone ?? string.Empty).Trim());

                using MySqlDataReader reader =
                    await command.ExecuteReaderAsync();

                if (!await reader.ReadAsync())
                {
                    return NotFound(new
                    {
                        success = false,
                        message =
                            $"Order #{id} was not found."
                    });
                }

                int? workerID =
                    reader.IsDBNull(
                        reader.GetOrdinal(
                            "DeliveryWorkerID"))
                        ? null
                        : reader.GetInt32(
                            reader.GetOrdinal(
                                "DeliveryWorkerID"));

                ExtractLocation(
                    reader["Notes"] == DBNull.Value
                        ? ""
                        : reader["Notes"]?.ToString() ?? "",
                    out double? notesLatitude,
                    out double? notesLongitude);

                double? latitude = reader.IsDBNull(reader.GetOrdinal("CustomerLatitude"))
                    ? notesLatitude : Convert.ToDouble(reader["CustomerLatitude"]);
                double? longitude = reader.IsDBNull(reader.GetOrdinal("CustomerLongitude"))
                    ? notesLongitude : Convert.ToDouble(reader["CustomerLongitude"]);

                return Ok(new
                {
                    success = true,

                    orderID =
                        reader.GetInt32(
                            reader.GetOrdinal(
                                "OrderID")),

                    customerID =
                        reader.IsDBNull(
                            reader.GetOrdinal(
                                "CustomerID"))
                            ? (int?)null
                            : reader.GetInt32(
                                reader.GetOrdinal(
                                    "CustomerID")),

                    orderType =
                        reader.GetString(
                            reader.GetOrdinal(
                                "OrderType")),

                    status =
                        reader.GetString(
                            reader.GetOrdinal(
                                "Status")),

                    totalAmount =
                        reader.GetDecimal(
                            reader.GetOrdinal(
                                "TotalAmount")),

                    deliveryAddress =
                        reader.IsDBNull(
                            reader.GetOrdinal(
                                "DeliveryAddress"))
                            ? ""
                            : reader.GetString(
                                reader.GetOrdinal(
                                    "DeliveryAddress")),

                    orderDate =
                        reader.GetDateTime(
                            reader.GetOrdinal(
                                "OrderDate")),

                    customerName =
                        reader.GetString(
                            reader.GetOrdinal(
                                "CustomerName")),

                    customerPhone =
                        reader.GetString(
                            reader.GetOrdinal(
                                "CustomerPhone")),

                    deliveryWorkerID =
                        workerID,

                    deliveryWorkerName =
                        reader.GetString(
                            reader.GetOrdinal(
                                "DeliveryWorkerName")),

                    deliveryWorkerPhone =
                        reader.GetString(
                            reader.GetOrdinal(
                                "DeliveryWorkerPhone")),

                    latitude =
                        latitude,

                    longitude =
                        longitude,

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
                return StatusCode(
                    500,
                    new
                    {
                        success = false,
                        message =
                            "Failed to load order tracking.",
                        error =
                            "تعذر إكمال العملية حالياً. يرجى المحاولة لاحقاً."
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
                    message = "Invalid status. Allowed: New, Preparing, Ready, Delivered, Cancelled."
                });
            }

            // IMPORTANT: always use the shared status workflow. When a new
            // Delivery order becomes Preparing, it atomically assigns an
            // active delivery worker. The driver's web app lists orders by
            // DeliveryWorkerID, so bypassing this method left the order out.
            return await UpdateStatusInternal(id, status);
        }

        // =========================================================
        // MARK ORDER AS PREPARING
        // PUT: /api/orders/5/preparing
        // =========================================================

        [HttpPut("{id:int}/preparing")]
        public Task<IActionResult> MarkPreparing(
            int id)
        {
            return UpdateStatusInternal(
                id,
                "Preparing");
        }

        // =========================================================
        // MARK ORDER AS READY
        // PUT: /api/orders/5/ready
        // =========================================================

        [HttpPut("{id:int}/ready")]
        public Task<IActionResult> MarkReady(
            int id)
        {
            return UpdateStatusInternal(
                id,
                "Ready");
        }

        // =========================================================
        // MARK ORDER AS DELIVERED
        // PUT: /api/orders/5/delivered
        // =========================================================

        [HttpPut("{id:int}/delivered")]
        public Task<IActionResult> MarkDelivered(
            int id)
        {
            return UpdateStatusInternal(
                id,
                "Delivered");
        }

        // =========================================================
        // CANCEL ORDER
        // PUT: /api/orders/5/cancel
        // =========================================================

        [HttpPut("{id:int}/cancel")]
        public Task<IActionResult> CancelOrder(
            int id)
        {
            return UpdateStatusInternal(
                id,
                "Cancelled");
        }

        // Customer cancellation is explicitly tied to the customer's ID and phone.
        [HttpPost("{id:int}/cancel-by-customer")]
        public async Task<IActionResult> CancelByCustomer(
            int id,
            [FromBody] CustomerCancelOrderRequest request)
        {
            if (id <= 0 || request == null || request.CustomerID <= 0 ||
                string.IsNullOrWhiteSpace(request.Phone))
                return BadRequest(new { success = false, message = "بيانات إلغاء الطلب غير مكتملة." });

            await using var connection = DatabaseManager.GetConnection();
            await connection.OpenAsync();
            await using var transaction = await connection.BeginTransactionAsync();
            try
            {
                const string verifySql = @"
                    SELECT o.CustomerID, o.Status
                    FROM orders o
                    INNER JOIN customers c ON c.CustomerID=o.CustomerID
                    WHERE o.OrderID=@OrderID AND o.CustomerID=@CustomerID
                      AND c.Phone=@Phone
                    FOR UPDATE;";
                int customerId;
                string status;
                await using (var verify = new MySqlCommand(verifySql, connection, transaction))
                {
                    verify.Parameters.AddWithValue("@OrderID", id);
                    verify.Parameters.AddWithValue("@CustomerID", request.CustomerID);
                    verify.Parameters.AddWithValue("@Phone", request.Phone.Trim());
                    await using var reader = await verify.ExecuteReaderAsync();
                    if (!await reader.ReadAsync())
                    {
                        await transaction.RollbackAsync();
                        return NotFound(new { success = false, message = "لم يتم العثور على الطلب أو أن بيانات الزبون غير مطابقة." });
                    }
                    customerId = reader.GetInt32("CustomerID");
                    status = reader.GetString("Status");
                }

                if (status is not ("New" or "Preparing"))
                {
                    await transaction.RollbackAsync();
                    return Conflict(new { success = false, message = "لا يمكن إلغاء الطلب بعد بدء مرحلة الجاهزية أو التوصيل." });
                }

                const string updateSql = @"
                    UPDATE orders SET Status='Cancelled', DeliveryWorkerID=NULL, UpdatedAt=UTC_TIMESTAMP()
                    WHERE OrderID=@OrderID AND CustomerID=@CustomerID
                      AND Status IN ('New','Preparing');";
                await using (var update = new MySqlCommand(updateSql, connection, transaction))
                {
                    update.Parameters.AddWithValue("@OrderID", id);
                    update.Parameters.AddWithValue("@CustomerID", customerId);
                    if (await update.ExecuteNonQueryAsync() != 1)
                    {
                        await transaction.RollbackAsync();
                        return Conflict(new { success = false, message = "تغيرت حالة الطلب قبل إلغائه. حدّث الصفحة وحاول مجدداً." });
                    }
                }

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

        private async Task<IActionResult> UpdateStatusInternal(int id, string status)
        {
            await using var connection = DatabaseManager.GetConnection();
            await connection.OpenAsync();
            await using var transaction = await connection.BeginTransactionAsync();

            try
            {
                const string checkSql = """
                    SELECT OrderID, CustomerID, OrderType, Status, DeliveryWorkerID, IsArchived
                    FROM orders
                    WHERE OrderID=@OrderID
                    LIMIT 1
                    FOR UPDATE;
                    """;

                string orderType;
                string oldStatus;
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

                    orderType = reader.GetString("OrderType");
                    oldStatus = reader.GetString("Status");
                    customerId = reader.IsDBNull(reader.GetOrdinal("CustomerID")) ? null : reader.GetInt32("CustomerID");
                    currentWorkerId = reader.IsDBNull(reader.GetOrdinal("DeliveryWorkerID")) ? null : reader.GetInt32("DeliveryWorkerID");
                }

                if (oldStatus == "Cancelled" && status != "Cancelled")
                {
                    await transaction.RollbackAsync();
                    return BadRequest(new { success = false, message = "A cancelled order cannot be reopened." });
                }
                if (oldStatus == "Delivered" && status != "Delivered")
                {
                    await transaction.RollbackAsync();
                    return BadRequest(new { success = false, message = "A delivered order cannot change status." });
                }

                bool validTransition =
                    (oldStatus == "New" && (status == "Preparing" || status == "Cancelled")) ||
                    (oldStatus == "Preparing" && (status == "Ready" || status == "Cancelled")) ||
                    (oldStatus == "Ready" && (status == "Delivered" || status == "OutForDelivery" || status == "Cancelled")) ||
                    (oldStatus == "OutForDelivery" && status == "Delivered") ||
                    oldStatus == status;
                if (!validTransition)
                {
                    await transaction.RollbackAsync();
                    return BadRequest(new { success = false, message = $"Cannot change order from {oldStatus} to {status}." });
                }

                int? assignedWorkerId = currentWorkerId;
                string assignedWorkerName = string.Empty;
                string assignedWorkerPhone = string.Empty;

                if (status == "Preparing" && orderType == "Delivery" && currentWorkerId is null && oldStatus == "New")
                {
                    const string workerSql = """
                        SELECT dw.DeliveryWorkerID, dw.FullName, COALESCE(dw.Phone,'') AS Phone
                        FROM deliveryworkers dw
                        WHERE dw.IsActive=1 AND dw.IsOnShift=1
                        ORDER BY (
                            SELECT COUNT(*) FROM orders activeOrders
                            WHERE activeOrders.DeliveryWorkerID=dw.DeliveryWorkerID
                              AND activeOrders.OrderType='Delivery'
                              AND activeOrders.Status IN ('Preparing','Ready','OutForDelivery')
                              AND activeOrders.IsArchived=0
                        ) ASC, dw.DeliveryWorkerID ASC
                        LIMIT 1
                        FOR UPDATE;
                        """;
                    await using var worker = new MySqlCommand(workerSql, connection, transaction);
                    await using var workerReader = await worker.ExecuteReaderAsync();
                    if (!await workerReader.ReadAsync())
                    {
                        await workerReader.CloseAsync();
                        await transaction.RollbackAsync();
                        return Conflict(new { success = false, message = "لا يوجد عامل توصيل نشط وعلى رأس الدوام حالياً." });
                    }
                    assignedWorkerId = workerReader.GetInt32("DeliveryWorkerID");
                    assignedWorkerName = workerReader.GetString("FullName");
                    assignedWorkerPhone = workerReader.GetString("Phone");
                    await workerReader.CloseAsync();
                }

                bool clearWorker = status == "Cancelled";
                const string updateSql = """
                    UPDATE orders
                    SET Status=@Status,
                        DeliveryWorkerID=CASE WHEN @ClearWorker=1 THEN NULL WHEN @AssignedWorkerID IS NOT NULL THEN @AssignedWorkerID ELSE DeliveryWorkerID END,
                        DeliveryAssignedAt=CASE WHEN @NewAssignment=1 THEN UTC_TIMESTAMP() ELSE DeliveryAssignedAt END,
                        PickedUpAt=CASE WHEN @Status='OutForDelivery' AND PickedUpAt IS NULL THEN UTC_TIMESTAMP() ELSE PickedUpAt END,
                        UpdatedAt=UTC_TIMESTAMP()
                    WHERE OrderID=@OrderID AND Status=@OldStatus AND IsArchived=0;
                    """;
                await using (var update = new MySqlCommand(updateSql, connection, transaction))
                {
                    update.Parameters.AddWithValue("@Status", status);
                    update.Parameters.AddWithValue("@ClearWorker", clearWorker);
                    update.Parameters.AddWithValue("@AssignedWorkerID", assignedWorkerId.HasValue && !clearWorker ? assignedWorkerId.Value : DBNull.Value);
                    update.Parameters.AddWithValue("@NewAssignment", assignedWorkerId.HasValue && currentWorkerId is null && status == "Preparing" && oldStatus == "New" && orderType == "Delivery");
                    update.Parameters.AddWithValue("@OrderID", id);
                    update.Parameters.AddWithValue("@OldStatus", oldStatus);
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
                        connection, transaction, customerId.Value, id, status);
                }

                await transaction.CommitAsync();
                return Ok(new
                {
                    success = true,
                    orderId = id,
                    orderType,
                    oldStatus,
                    status,
                    deliveryWorkerID = clearWorker ? null : assignedWorkerId,
                    deliveryWorkerName = clearWorker ? null : assignedWorkerName,
                    deliveryWorkerPhone = clearWorker ? null : assignedWorkerPhone,
                    message = assignedWorkerId.HasValue && oldStatus == "New" && status == "Preparing" && orderType == "Delivery"
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

        private static OrderResponse ReadOrder(
            MySqlDataReader reader)
        {
            OrderResponse order =
                new OrderResponse();

            order.OrderID =
                reader.GetInt32(
                    reader.GetOrdinal(
                        "OrderID"));

            order.CustomerID =
                reader.IsDBNull(
                    reader.GetOrdinal(
                        "CustomerID"))
                    ? (int?)null
                    : reader.GetInt32(
                        reader.GetOrdinal(
                            "CustomerID"));

            order.UserID =
                reader.GetInt32(
                    reader.GetOrdinal(
                        "UserID"));

            order.DeliveryZoneID =
                reader.IsDBNull(
                    reader.GetOrdinal(
                        "DeliveryZoneID"))
                    ? (int?)null
                    : reader.GetInt32(
                        reader.GetOrdinal(
                            "DeliveryZoneID"));

            order.OrderDate =
                reader.GetDateTime(
                    reader.GetOrdinal(
                        "OrderDate"));

            order.OrderType =
                reader.GetString(
                    reader.GetOrdinal(
                        "OrderType"));

            order.Status =
                reader.GetString(
                    reader.GetOrdinal(
                        "Status"));

            order.Subtotal =
                reader.GetDecimal(
                    reader.GetOrdinal(
                        "Subtotal"));

            order.DeliveryFee =
                reader.GetDecimal(
                    reader.GetOrdinal(
                        "DeliveryFee"));

            order.DiscountAmount =
                reader.GetDecimal(
                    reader.GetOrdinal(
                        "DiscountAmount"));

            order.TotalAmount =
                reader.GetDecimal(
                    reader.GetOrdinal(
                        "TotalAmount"));

            order.DeliveryAddress =
                reader.IsDBNull(
                    reader.GetOrdinal(
                        "DeliveryAddress"))
                    ? null
                    : reader.GetString(
                        reader.GetOrdinal(
                            "DeliveryAddress"));

            order.Notes =
                reader.IsDBNull(
                    reader.GetOrdinal(
                        "Notes"))
                    ? null
                    : reader.GetString(
                        reader.GetOrdinal(
                            "Notes"));

            order.CreatedAt =
                reader.GetDateTime(
                    reader.GetOrdinal(
                        "CreatedAt"));

            order.UpdatedAt =
                reader.GetDateTime(
                    reader.GetOrdinal(
                        "UpdatedAt"));

            order.DeliveryWorkerID =
                reader.IsDBNull(
                    reader.GetOrdinal(
                        "DeliveryWorkerID"))
                    ? (int?)null
                    : reader.GetInt32(
                        reader.GetOrdinal(
                            "DeliveryWorkerID"));

            order.DeliveryWorkerName =
                reader.GetString(
                    reader.GetOrdinal(
                        "DeliveryWorkerName"));

            order.DeliveryWorkerPhone =
                reader.GetString(
                    reader.GetOrdinal(
                        "DeliveryWorkerPhone"));

            order.CustomerName =
                reader.GetString(
                    reader.GetOrdinal(
                        "CustomerName"));

            order.CustomerPhone =
                reader.GetString(
                    reader.GetOrdinal(
                        "CustomerPhone"));

            order.UserName =
                reader.GetString(
                    reader.GetOrdinal(
                        "UserName"));

            order.DeliveryZoneName =
                reader.GetString(
                    reader.GetOrdinal(
                        "DeliveryZoneName"));

            // =====================================================
            // EXTRACT GPS LOCATION FROM NOTES
            // =====================================================

            if (!string.IsNullOrWhiteSpace(
                order.Notes))
            {
                ExtractLocation(
                    order.Notes,
                    out double? latitude,
                    out double? longitude);

                order.Latitude =
                    latitude;

                order.Longitude =
                    longitude;
            }

            return order;
        }

        // =========================================================
        // EXTRACT LOCATION
        // =========================================================

        private static void ExtractLocation(
            string notes,
            out double? latitude,
            out double? longitude)
        {
            latitude = null;
            longitude = null;

            const string prefix =
                "Location:";

            int index =
                notes.IndexOf(
                    prefix,
                    StringComparison.OrdinalIgnoreCase);

            if (index < 0)
                return;

            string location =
                notes.Substring(
                    index + prefix.Length)
                .Trim();

            string[] parts =
                location.Split(
                    ',',
                    StringSplitOptions.RemoveEmptyEntries);

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

        // =========================================================
        // NORMALIZE ORDER TYPE
        // =========================================================

        // =========================================================
        // DELIVERY POLYGON CHECK (longitude = X, latitude = Y)
        // ZoneCoordinates format: [{ "lat": 36.1, "lng": 44.0 }, ...]
        // =========================================================
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

        private static string NormalizeOrderType(
            string? value)
        {
            if (string.IsNullOrWhiteSpace(value))
                return "";

            string text =
                value.Trim();

            // English
            if (text.Equals(
                "Delivery",
                StringComparison.OrdinalIgnoreCase))
            {
                return "Delivery";
            }

            if (text.Equals(
                "Takeaway",
                StringComparison.OrdinalIgnoreCase))
            {
                return "Takeaway";
            }

            if (text.Equals(
                "Dine In",
                StringComparison.OrdinalIgnoreCase))
            {
                return "Dine In";
            }

            // Arabic
            if (text == "توصيل")
                return "Delivery";

            if (text == "سفري")
                return "Takeaway";

            if (text == "داخل المطعم")
                return "Dine In";

            // Kurdish
            if (text == "گەیاندن")
                return "Delivery";

            if (text == "وەرگرتن")
                return "Takeaway";

            if (text == "ناو خواردنگە")
                return "Dine In";

            return "";
        }

        // =========================================================
        // NORMALIZE STATUS
        // =========================================================

        private static string NormalizeStatus(
            string? value)
        {
            if (string.IsNullOrWhiteSpace(value))
                return "";

            string text =
                value.Trim();

            // English
            if (text.Equals(
                "New",
                StringComparison.OrdinalIgnoreCase))
            {
                return "New";
            }

            if (text.Equals(
                "Preparing",
                StringComparison.OrdinalIgnoreCase))
            {
                return "Preparing";
            }

            if (text.Equals(
                "Ready",
                StringComparison.OrdinalIgnoreCase))
            {
                return "Ready";
            }

            if (text.Equals(
                "Delivered",
                StringComparison.OrdinalIgnoreCase))
            {
                return "Delivered";
            }

            if (text.Equals(
                "Cancelled",
                StringComparison.OrdinalIgnoreCase))
            {
                return "Cancelled";
            }

            if (text.Equals("OutForDelivery", StringComparison.OrdinalIgnoreCase) ||
                text.Equals("Out for delivery", StringComparison.OrdinalIgnoreCase))
                return "OutForDelivery";

            // Arabic
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

            if (text == "خرج للتوصيل" || text == "بالطريق إليك")
                return "OutForDelivery";

            // Kurdish
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

            return "";
        }
    }

    // =============================================================
    // CREATE ORDER REQUEST
    // =============================================================

    public class CreateOrderRequest
    {
        public string? CustomerName { get; set; }

        public string? CustomerPhone { get; set; }

        public string? Address { get; set; }

        public string? Notes { get; set; }

        public string OrderType { get; set; } =
            "Delivery";

        public decimal DeliveryFee { get; set; } =
            0m;

        public decimal DiscountAmount { get; set; } =
            0m;

        public double? Latitude { get; set; }

        public double? Longitude { get; set; }

        public List<OrderItemRequest> Items { get; set; } =
            new List<OrderItemRequest>();

        // Internal only
        public int CreatedOrderID { get; set; }
    }

    // =============================================================
    // ORDER ITEM REQUEST
    // =============================================================

    public class OrderItemRequest
    {
        public int ProductID { get; set; }

        public int Quantity { get; set; }

        public string? Notes { get; set; }
    }

    // =============================================================
    // UPDATE ORDER STATUS REQUEST
    // =============================================================

    public class UpdateorderstatusRequest
    {
        public string Status { get; set; } = "";
    }

    public sealed class CustomerCancelOrderRequest
    {
        public int CustomerID { get; set; }
        public string Phone { get; set; } = "";
    }

    // =============================================================
    // ORDER RESPONSE
    // =============================================================

    public class OrderResponse
    {
        public int OrderID { get; set; }

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

        // =====================================================
        // CUSTOMER
        // =====================================================

        public string CustomerName { get; set; } = "";

        public string CustomerPhone { get; set; } = "";

        // =====================================================
        // USER
        // =====================================================

        public string UserName { get; set; } = "";

        // =====================================================
        // DELIVERY ZONE
        // =====================================================

        public string DeliveryZoneName { get; set; } = "";

        // =====================================================
        // DELIVERY WORKER
        // =====================================================

        public int? DeliveryWorkerID { get; set; }

        public string DeliveryWorkerName { get; set; } = "";

        public string DeliveryWorkerPhone { get; set; } = "";

        // =====================================================
        // GPS
        // =====================================================

        public double? Latitude { get; set; }

        public double? Longitude { get; set; }

        // =====================================================
        // ITEMS
        // =====================================================

        public List<OrderDetailResponse> Items { get; set; } =
            new List<OrderDetailResponse>();
    }

    // =============================================================
    // ORDER DETAIL RESPONSE
    // =============================================================

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