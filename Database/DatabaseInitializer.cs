using Microsoft.Extensions.Logging;
using MySqlConnector;
using System;
using System.Threading.Tasks;

namespace QamishloRestaurantAPI.Database;

public static class DatabaseInitializer
{
    public static async Task EnsureSchemaUpdatedAsync(ILogger? logger = null)
    {
        try
        {
            await using var connection = DatabaseManager.GetConnection();
            await connection.OpenAsync();

            // 1. Ensure system/admin UserID = 1 and UserID = 2 exist and are active so FK_Orders_Users never fails
            try
            {
                const string ensureUsersSql = @"
                    INSERT IGNORE INTO users (UserID, FullName, Username, Password, Role, Phone, IsActive)
                    VALUES (1, 'Qamishlo', 'admin', 'hamo7292', 'Admin', '07503809145', 1);
                    UPDATE users SET IsActive = 1 WHERE UserID IN (1, 2);";
                await using var uCmd = new MySqlCommand(ensureUsersSql, connection);
                await uCmd.ExecuteNonQueryAsync();
            }
            catch (Exception ex)
            {
                logger?.LogWarning(ex, "Could not ensure default admin users.");
            }

            // 2. Unarchive delivered orders that were previously auto-archived so all delivered orders appear in reports & history
            try
            {
                const string unarchiveDeliveredSql = @"
                    UPDATE orders
                    SET IsArchived = 0
                    WHERE Status = 'Delivered' AND IsArchived = 1;";
                await using var unarchCmd = new MySqlCommand(unarchiveDeliveredSql, connection);
                await unarchCmd.ExecuteNonQueryAsync();
            }
            catch (Exception ex)
            {
                logger?.LogWarning(ex, "Could not unarchive delivered orders.");
            }

            // 3. Ensure Status ENUM on orders includes 'Rejected' and 'OutForDelivery'
            try
            {
                const string alterStatusEnumSql = @"
                    ALTER TABLE orders
                    MODIFY COLUMN Status ENUM('New','Preparing','Ready','Delivered','Cancelled','OutForDelivery','Rejected')
                    NOT NULL DEFAULT 'New';";
                await using var enumCmd = new MySqlCommand(alterStatusEnumSql, connection);
                await enumCmd.ExecuteNonQueryAsync();
            }
            catch (Exception ex)
            {
                logger?.LogWarning(ex, "Could not alter orders.Status ENUM.");
            }

            // 4. Ensure employeephotostorage table matches the exact API schema (FileName, ContentType, ImageData, UploadedAt)
            try
            {
                const string createPhotoTableSql = @"
                    CREATE TABLE IF NOT EXISTS employeephotostorage (
                        FileName VARCHAR(120) COLLATE utf8mb4_unicode_ci NOT NULL PRIMARY KEY,
                        ContentType VARCHAR(80) COLLATE utf8mb4_unicode_ci NOT NULL DEFAULT 'image/jpeg',
                        ImageData LONGBLOB NOT NULL,
                        UploadedAt DATETIME NOT NULL DEFAULT CURRENT_TIMESTAMP,
                        KEY IDX_EmployeePhotoStorage_UploadedAt (UploadedAt)
                    ) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_unicode_ci;";
                await using var photoTableCmd = new MySqlCommand(createPhotoTableSql, connection);
                await photoTableCmd.ExecuteNonQueryAsync();
            }
            catch (Exception ex)
            {
                logger?.LogWarning(ex, "Could not ensure employeephotostorage table.");
            }

            // 5. Ensure discountcoupons table exists
            try
            {
                const string createCouponsTableSql = @"
                    CREATE TABLE IF NOT EXISTS discountcoupons (
                        CouponID INT NOT NULL AUTO_INCREMENT PRIMARY KEY,
                        CouponCode VARCHAR(80) NOT NULL,
                        Title VARCHAR(200) NOT NULL DEFAULT 'قسيمة خصم من قامشلو',
                        Description TEXT NULL,
                        DiscountType VARCHAR(20) NOT NULL DEFAULT 'Percent',
                        DiscountValue DECIMAL(10,2) NOT NULL DEFAULT 0.00,
                        IsActive TINYINT(1) NOT NULL DEFAULT 1,
                        StartDate DATETIME NULL,
                        EndDate DATETIME NULL,
                        MaxUses INT NULL,
                        UsedCount INT NOT NULL DEFAULT 0,
                        ShowInOffersPage TINYINT(1) NOT NULL DEFAULT 1,
                        CreatedAt DATETIME NOT NULL DEFAULT CURRENT_TIMESTAMP,
                        UNIQUE KEY UQ_DiscountCoupons_Code (CouponCode)
                    ) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_unicode_ci;";
                await using var couponsCmd = new MySqlCommand(createCouponsTableSql, connection);
                await couponsCmd.ExecuteNonQueryAsync();
            }
            catch (Exception ex)
            {
                logger?.LogWarning(ex, "Could not ensure discountcoupons table.");
            }

            // 6. Ensure broadcastnotifications table exists
            try
            {
                const string createBroadcastTableSql = @"
                    CREATE TABLE IF NOT EXISTS broadcastnotifications (
                        BroadcastID BIGINT NOT NULL AUTO_INCREMENT PRIMARY KEY,
                        TitleEN VARCHAR(200) NOT NULL,
                        TitleAR VARCHAR(200) NOT NULL,
                        TitleKU VARCHAR(200) NOT NULL,
                        BodyEN TEXT NOT NULL,
                        BodyAR TEXT NOT NULL,
                        BodyKU TEXT NOT NULL,
                        CouponCode VARCHAR(80) NULL,
                        DiscountText VARCHAR(100) NULL,
                        IsActive TINYINT(1) NOT NULL DEFAULT 1,
                        CreatedAt DATETIME NOT NULL DEFAULT CURRENT_TIMESTAMP
                    ) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_unicode_ci;";
                await using var broadcastCmd = new MySqlCommand(createBroadcastTableSql, connection);
                await broadcastCmd.ExecuteNonQueryAsync();
            }
            catch (Exception ex)
            {
                logger?.LogWarning(ex, "Could not ensure broadcastnotifications table.");
            }

            // 7. V8: Ensure Qamishlo Coins (Loyalty), Order Reviews, Product Add-ons, and Push Subscriptions tables exist
            string[] v8Sqls = new[]
            {
                @"CREATE TABLE IF NOT EXISTS loyaltysettings (
                    SettingID INT NOT NULL PRIMARY KEY,
                    IsEnabled TINYINT(1) NOT NULL DEFAULT 1,
                    EarnPerAmountIQD DECIMAL(10,2) NOT NULL DEFAULT 1000.00,
                    CoinsPerStep INT NOT NULL DEFAULT 1,
                    CoinValueIQD DECIMAL(10,2) NOT NULL DEFAULT 50.00,
                    MinCoinsToRedeem INT NOT NULL DEFAULT 20,
                    UpdatedAt DATETIME NOT NULL DEFAULT CURRENT_TIMESTAMP
                ) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_unicode_ci;",
                @"INSERT IGNORE INTO loyaltysettings (SettingID, IsEnabled, EarnPerAmountIQD, CoinsPerStep, CoinValueIQD, MinCoinsToRedeem)
                  VALUES (1, 1, 1000.00, 1, 50.00, 20);",
                @"CREATE TABLE IF NOT EXISTS customerloyalty (
                    CustomerID INT NOT NULL PRIMARY KEY,
                    CoinsBalance INT NOT NULL DEFAULT 0,
                    TotalEarned INT NOT NULL DEFAULT 0,
                    TotalRedeemed INT NOT NULL DEFAULT 0,
                    UpdatedAt DATETIME NOT NULL DEFAULT CURRENT_TIMESTAMP
                ) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_unicode_ci;",
                @"CREATE TABLE IF NOT EXISTS loyaltytransactions (
                    TransactionID BIGINT NOT NULL AUTO_INCREMENT PRIMARY KEY,
                    CustomerID INT NOT NULL,
                    OrderID INT NULL,
                    DeltaCoins INT NOT NULL,
                    Reason VARCHAR(255) NOT NULL,
                    CreatedAt DATETIME NOT NULL DEFAULT CURRENT_TIMESTAMP,
                    INDEX IX_LoyaltyTx_Customer (CustomerID),
                    INDEX IX_LoyaltyTx_Order (OrderID)
                ) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_unicode_ci;",
                @"CREATE TABLE IF NOT EXISTS orderreviews (
                    ReviewID BIGINT NOT NULL AUTO_INCREMENT PRIMARY KEY,
                    OrderID INT NOT NULL,
                    CustomerID INT NULL,
                    CustomerName VARCHAR(150) NULL,
                    FoodRating TINYINT NOT NULL,
                    DeliveryRating TINYINT NOT NULL,
                    Comment TEXT NULL,
                    CreatedAt DATETIME NOT NULL DEFAULT CURRENT_TIMESTAMP,
                    UNIQUE KEY UQ_OrderReviews_Order (OrderID)
                ) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_unicode_ci;",
                @"CREATE TABLE IF NOT EXISTS productaddons (
                    AddonID INT NOT NULL AUTO_INCREMENT PRIMARY KEY,
                    ProductID INT NOT NULL,
                    AddonName VARCHAR(180) NOT NULL,
                    ExtraPrice DECIMAL(10,2) NOT NULL DEFAULT 0.00,
                    IsActive TINYINT(1) NOT NULL DEFAULT 1,
                    CreatedAt DATETIME NOT NULL DEFAULT CURRENT_TIMESTAMP,
                    INDEX IX_ProductAddons_Product (ProductID)
                ) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_unicode_ci;",
                @"CREATE TABLE IF NOT EXISTS pushsubscriptions (
                    SubscriptionID BIGINT NOT NULL AUTO_INCREMENT PRIMARY KEY,
                    AudienceType VARCHAR(32) NOT NULL,
                    TargetID INT NULL,
                    TokenOrEndpoint VARCHAR(700) NOT NULL,
                    P256dh VARCHAR(255) NULL,
                    Auth VARCHAR(255) NULL,
                    Platform VARCHAR(32) NOT NULL DEFAULT 'web',
                    SoundKey VARCHAR(40) NULL,
                    IsActive TINYINT(1) NOT NULL DEFAULT 1,
                    UpdatedAt DATETIME NOT NULL DEFAULT CURRENT_TIMESTAMP,
                    UNIQUE KEY UQ_PushSub_Token (TokenOrEndpoint(255)),
                    INDEX IX_PushSub_Audience (AudienceType, TargetID)
                ) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_unicode_ci;"
            };
            foreach (var sqlStmt in v8Sqls)
            {
                try
                {
                    await using var v8Cmd = new MySqlCommand(sqlStmt, connection);
                    await v8Cmd.ExecuteNonQueryAsync();
                }
                catch (Exception ex)
                {
                    logger?.LogWarning(ex, "Could not execute V8 schema statement.");
                }
            }

            // 8. V9: Loyalty Rewards & Expiry, Customer BirthDate, Offline Sync ClientOrderGuid, Marketing Campaigns
            string[] v9Sqls = new[]
            {
                @"CREATE TABLE IF NOT EXISTS loyaltyrewards (
                    RewardID INT NOT NULL AUTO_INCREMENT PRIMARY KEY,
                    Title VARCHAR(180) NOT NULL,
                    Description VARCHAR(300) NULL,
                    CoinsCost INT NOT NULL DEFAULT 20,
                    DiscountIQD DECIMAL(10,2) NOT NULL DEFAULT 0.00,
                    RewardType VARCHAR(40) NOT NULL DEFAULT 'Discount',
                    IsActive TINYINT(1) NOT NULL DEFAULT 1,
                    CreatedAt DATETIME NOT NULL DEFAULT CURRENT_TIMESTAMP
                ) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_unicode_ci;",
                @"INSERT IGNORE INTO loyaltyrewards (RewardID, Title, Description, CoinsCost, DiscountIQD, RewardType, IsActive)
                  VALUES
                    (1, 'خصم 1,000 د.ع فوري', 'استبدل 20 قامشلو كوين بخصم فوري 1,000 د.ع', 20, 1000.00, 'Discount', 1),
                    (2, 'بطاطا مقلية مجانية 🍟', 'استبدل 35 قامشلو كوين بوجبة بطاطا مقلية', 35, 1500.00, 'FreeItem', 1),
                    (3, 'خصم 5,000 د.ع VIP', 'استبدل 90 قامشلو كوين بخصم 5,000 د.ع', 90, 5000.00, 'Discount', 1);",
                @"CREATE TABLE IF NOT EXISTS marketingcampaigns (
                    CampaignID BIGINT NOT NULL AUTO_INCREMENT PRIMARY KEY,
                    CampaignType VARCHAR(40) NOT NULL,
                    Title VARCHAR(200) NOT NULL,
                    Message TEXT NOT NULL,
                    CouponCode VARCHAR(60) NULL,
                    TargetCount INT NOT NULL DEFAULT 0,
                    CreatedAt DATETIME NOT NULL DEFAULT CURRENT_TIMESTAMP
                ) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_unicode_ci;",
                @"CREATE TABLE IF NOT EXISTS offlinesyncorders (
                    ClientOrderGuid VARCHAR(80) NOT NULL PRIMARY KEY,
                    OrderID INT NOT NULL,
                    SyncedAt DATETIME NOT NULL DEFAULT CURRENT_TIMESTAMP
                ) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_unicode_ci;"
            };
            foreach (var stmt in v9Sqls)
            {
                try { await using var c = new MySqlCommand(stmt, connection); await c.ExecuteNonQueryAsync(); }
                catch (Exception ex) { logger?.LogWarning(ex, "V9 table ensure warning"); }
            }

            // Optional V9 columns on loyaltysettings & customers
            string[] v9Alters = new[]
            {
                "ALTER TABLE loyaltysettings ADD COLUMN CoinsExpiryDays INT NOT NULL DEFAULT 90;",
                "ALTER TABLE loyaltysettings ADD COLUMN ValidUntilDate DATETIME NULL;",
                "ALTER TABLE customers ADD COLUMN BirthDate DATE NULL;"
            };
            foreach (var alt in v9Alters)
            {
                try { await using var c = new MySqlCommand(alt, connection); await c.ExecuteNonQueryAsync(); }
                catch { }
            }

            // 9. V13 Enterprise Tables (Finance, Inventory/Recipes, KDS, Audit Trail)
            string[] v13Sqls = new[]
            {
                @"CREATE TABLE IF NOT EXISTS cashsessions (
                    CashSessionID BIGINT NOT NULL AUTO_INCREMENT PRIMARY KEY,
                    OpenedBy VARCHAR(120) NOT NULL,
                    OpenedAt DATETIME NOT NULL DEFAULT CURRENT_TIMESTAMP,
                    OpeningBalance DECIMAL(12,2) NOT NULL DEFAULT 0.00,
                    ExpectedCash DECIMAL(12,2) NOT NULL DEFAULT 0.00,
                    ActualCash DECIMAL(12,2) NULL,
                    CashDifference DECIMAL(12,2) NULL,
                    ClosedBy VARCHAR(120) NULL,
                    ClosedAt DATETIME NULL,
                    Status VARCHAR(24) NOT NULL DEFAULT 'Open',
                    Notes VARCHAR(500) NULL
                ) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_unicode_ci;",
                @"CREATE TABLE IF NOT EXISTS cashmovements (
                    MovementID BIGINT NOT NULL AUTO_INCREMENT PRIMARY KEY,
                    CashSessionID BIGINT NOT NULL,
                    MovementType VARCHAR(32) NOT NULL,
                    Amount DECIMAL(12,2) NOT NULL,
                    Description VARCHAR(300) NULL,
                    CreatedBy VARCHAR(120) NOT NULL,
                    CreatedAt DATETIME NOT NULL DEFAULT CURRENT_TIMESTAMP
                ) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_unicode_ci;",
                @"CREATE TABLE IF NOT EXISTS financialledger (
                    TransactionID BIGINT NOT NULL AUTO_INCREMENT PRIMARY KEY,
                    TransactionType VARCHAR(40) NOT NULL,
                    DebitAccount VARCHAR(80) NOT NULL DEFAULT 'Cash',
                    CreditAccount VARCHAR(80) NOT NULL DEFAULT 'SalesRevenue',
                    Amount DECIMAL(12,2) NOT NULL,
                    ReferenceID BIGINT NULL,
                    ReferenceType VARCHAR(50) NULL,
                    ReversesTransactionID BIGINT NULL,
                    Description VARCHAR(400) NULL,
                    CreatedBy VARCHAR(120) NOT NULL DEFAULT 'System',
                    CreatedAt DATETIME NOT NULL DEFAULT CURRENT_TIMESTAMP,
                    AccountingDate DATE NOT NULL,
                    CashSessionID BIGINT NULL
                ) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_unicode_ci;",
                @"CREATE TABLE IF NOT EXISTS inventoryitems (
                    InventoryItemID INT NOT NULL AUTO_INCREMENT PRIMARY KEY,
                    Name VARCHAR(180) NOT NULL,
                    SKU VARCHAR(80) NULL,
                    CategoryName VARCHAR(100) NULL,
                    BaseUnit VARCHAR(30) NOT NULL DEFAULT 'Gram',
                    PurchaseUnit VARCHAR(30) NOT NULL DEFAULT 'Kilogram',
                    ConversionFactor DECIMAL(12,4) NOT NULL DEFAULT 1000.0000,
                    CurrentQuantity DECIMAL(14,4) NOT NULL DEFAULT 0.0000,
                    MinimumQuantity DECIMAL(14,4) NOT NULL DEFAULT 0.0000,
                    MaximumQuantity DECIMAL(14,4) NOT NULL DEFAULT 0.0000,
                    AverageCost DECIMAL(12,4) NOT NULL DEFAULT 0.0000,
                    ExpiryTracking TINYINT(1) NOT NULL DEFAULT 0,
                    ExpiryDate DATE NULL,
                    IsActive TINYINT(1) NOT NULL DEFAULT 1,
                    UpdatedAt DATETIME NOT NULL DEFAULT CURRENT_TIMESTAMP
                ) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_unicode_ci;",
                @"CREATE TABLE IF NOT EXISTS productrecipes (
                    RecipeID INT NOT NULL AUTO_INCREMENT PRIMARY KEY,
                    ProductID INT NOT NULL,
                    InventoryItemID INT NOT NULL,
                    QuantityRequired DECIMAL(12,4) NOT NULL,
                    Unit VARCHAR(30) NOT NULL DEFAULT 'Gram',
                    WastePercent DECIMAL(5,2) NOT NULL DEFAULT 0.00,
                    CreatedAt DATETIME NOT NULL DEFAULT CURRENT_TIMESTAMP,
                    UNIQUE KEY UQ_ProductRecipes_ProdItem (ProductID, InventoryItemID)
                ) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_unicode_ci;",
                @"CREATE TABLE IF NOT EXISTS suppliers (
                    SupplierID INT NOT NULL AUTO_INCREMENT PRIMARY KEY,
                    Name VARCHAR(180) NOT NULL,
                    Phone VARCHAR(50) NULL,
                    Address VARCHAR(255) NULL,
                    BalanceIQD DECIMAL(12,2) NOT NULL DEFAULT 0.00,
                    IsActive TINYINT(1) NOT NULL DEFAULT 1,
                    CreatedAt DATETIME NOT NULL DEFAULT CURRENT_TIMESTAMP
                ) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_unicode_ci;",
                @"CREATE TABLE IF NOT EXISTS purchaseorders (
                    PurchaseOrderID BIGINT NOT NULL AUTO_INCREMENT PRIMARY KEY,
                    SupplierID INT NULL,
                    SupplierName VARCHAR(180) NULL,
                    InvoiceNumber VARCHAR(80) NULL,
                    TotalAmount DECIMAL(12,2) NOT NULL DEFAULT 0.00,
                    PaymentMethod VARCHAR(40) NOT NULL DEFAULT 'Cash',
                    Status VARCHAR(32) NOT NULL DEFAULT 'Received',
                    Notes VARCHAR(400) NULL,
                    CreatedBy VARCHAR(120) NOT NULL DEFAULT 'Manager',
                    CreatedAt DATETIME NOT NULL DEFAULT CURRENT_TIMESTAMP
                ) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_unicode_ci;",
                @"CREATE TABLE IF NOT EXISTS stockmovements (
                    MovementID BIGINT NOT NULL AUTO_INCREMENT PRIMARY KEY,
                    InventoryItemID INT NOT NULL,
                    MovementType VARCHAR(32) NOT NULL,
                    QuantityDelta DECIMAL(14,4) NOT NULL,
                    UnitCost DECIMAL(12,4) NOT NULL DEFAULT 0.0000,
                    ReferenceType VARCHAR(40) NULL,
                    ReferenceID BIGINT NULL,
                    Notes VARCHAR(300) NULL,
                    CreatedBy VARCHAR(120) NOT NULL DEFAULT 'System',
                    CreatedAt DATETIME NOT NULL DEFAULT CURRENT_TIMESTAMP
                ) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_unicode_ci;",
                @"CREATE TABLE IF NOT EXISTS inventoryorderdeductions (
                    OrderID INT NOT NULL PRIMARY KEY,
                    TotalCOGS DECIMAL(12,2) NOT NULL DEFAULT 0.00,
                    DeductedAt DATETIME NOT NULL DEFAULT CURRENT_TIMESTAMP,
                    IsReversed TINYINT(1) NOT NULL DEFAULT 0,
                    ReversedAt DATETIME NULL
                ) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_unicode_ci;",
                @"CREATE TABLE IF NOT EXISTS kitchenstations (
                    StationID INT NOT NULL AUTO_INCREMENT PRIMARY KEY,
                    StationName VARCHAR(100) NOT NULL,
                    StationCode VARCHAR(40) NOT NULL,
                    TargetPrepMinutes INT NOT NULL DEFAULT 15,
                    IsActive TINYINT(1) NOT NULL DEFAULT 1,
                    UNIQUE KEY UQ_KitchenStations_Code (StationCode)
                ) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_unicode_ci;",
                @"INSERT IGNORE INTO kitchenstations (StationID, StationName, StationCode, TargetPrepMinutes, IsActive)
                  VALUES (1,'شاورما — Shawarma','SHAWARMA',10,1),(2,'مشويات — Grill','GRILL',18,1),(3,'مقالي — Frying','FRYING',12,1),(4,'مشروبات — Drinks','DRINKS',5,1),(5,'سندويش — Sandwiches','SANDWICHES',10,1),(6,'المطبخ العام — General Kitchen','GENERAL',15,1);",
                @"CREATE TABLE IF NOT EXISTS kitchenordertracking (
                    TrackingID BIGINT NOT NULL AUTO_INCREMENT PRIMARY KEY,
                    OrderID INT NOT NULL,
                    StationCode VARCHAR(40) NOT NULL DEFAULT 'GENERAL',
                    KdsStatus VARCHAR(32) NOT NULL DEFAULT 'Received',
                    TargetMinutes INT NOT NULL DEFAULT 15,
                    StartedAt DATETIME NULL,
                    ReadyAt DATETIME NULL,
                    CompletedAt DATETIME NULL,
                    UpdatedBy VARCHAR(120) NULL,
                    UpdatedAt DATETIME NOT NULL DEFAULT CURRENT_TIMESTAMP,
                    UNIQUE KEY UQ_Kds_OrderStation (OrderID, StationCode)
                ) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_unicode_ci;",
                @"CREATE TABLE IF NOT EXISTS auditlogs (
                    AuditID BIGINT NOT NULL AUTO_INCREMENT PRIMARY KEY,
                    ActorUsername VARCHAR(120) NOT NULL,
                    ActorRole VARCHAR(40) NOT NULL DEFAULT 'Manager',
                    Action VARCHAR(100) NOT NULL,
                    EntityType VARCHAR(80) NOT NULL,
                    EntityID VARCHAR(80) NULL,
                    Details TEXT NULL,
                    IpAddress VARCHAR(64) NULL,
                    CreatedAt DATETIME NOT NULL DEFAULT CURRENT_TIMESTAMP
                ) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_unicode_ci;"
            };
            foreach (var s13 in v13Sqls)
            {
                try { await using var c = new MySqlCommand(s13, connection); await c.ExecuteNonQueryAsync(); }
                catch (Exception ex) { logger?.LogWarning(ex, "V13 table ensure warning"); }
            }

                        try
            {
                await using var chkPin = new MySqlCommand(
                    "SELECT COUNT(*) FROM INFORMATION_SCHEMA.COLUMNS WHERE TABLE_SCHEMA = DATABASE() AND TABLE_NAME = 'users' AND COLUMN_NAME = 'PinCode';",
                    connection);
                if (Convert.ToInt32(await chkPin.ExecuteScalarAsync()) == 0)
                {
                    await using var addPin = new MySqlCommand("ALTER TABLE users ADD COLUMN PinCode VARCHAR(12) NULL AFTER Phone;", connection);
                    await addPin.ExecuteNonQueryAsync();
                }
                await using var defPin = new MySqlCommand("UPDATE users SET PinCode = '1234' WHERE (PinCode IS NULL OR PinCode = '') AND (FullName LIKE '%Mohammed%' OR Role IN ('Admin','Manager')) LIMIT 1;", connection);
                await defPin.ExecuteNonQueryAsync();
            }
            catch (Exception ex) { logger?.LogWarning(ex, "V14 users.PinCode check warning"); }

            // V14 Schema Ensure (AddonsText on orderdetails, Debts, Attendance, Permissions, Driver->Staff Sync)
            try
            {
                await using var chkAddonsCol = new MySqlCommand(
                    "SELECT COUNT(*) FROM INFORMATION_SCHEMA.COLUMNS WHERE TABLE_SCHEMA = DATABASE() AND TABLE_NAME = 'orderdetails' AND COLUMN_NAME = 'AddonsText';",
                    connection);
                if (Convert.ToInt32(await chkAddonsCol.ExecuteScalarAsync()) == 0)
                {
                    await using var addCol = new MySqlCommand("ALTER TABLE orderdetails ADD COLUMN AddonsText VARCHAR(500) NULL AFTER Notes;", connection);
                    await addCol.ExecuteNonQueryAsync();
                }
            }
            catch (Exception ex) { logger?.LogWarning(ex, "V14 orderdetails.AddonsText check warning"); }

            string[] v14Sqls =
            {
                @"CREATE TABLE IF NOT EXISTS debts (
                    DebtID INT NOT NULL AUTO_INCREMENT PRIMARY KEY,
                    DebtorName VARCHAR(150) NOT NULL,
                    Phone VARCHAR(30) NULL,
                    DebtType VARCHAR(30) NOT NULL DEFAULT 'Customer',
                    OriginalAmount DECIMAL(12,2) NOT NULL DEFAULT 0,
                    PaidAmount DECIMAL(12,2) NOT NULL DEFAULT 0,
                    RemainingAmount DECIMAL(12,2) NOT NULL DEFAULT 0,
                    Reason VARCHAR(300) NULL,
                    DueDate DATE NULL,
                    Status VARCHAR(20) NOT NULL DEFAULT 'Open',
                    CreatedBy VARCHAR(120) NULL,
                    CreatedAt DATETIME NOT NULL DEFAULT CURRENT_TIMESTAMP,
                    UpdatedAt DATETIME NOT NULL DEFAULT CURRENT_TIMESTAMP
                ) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_unicode_ci;",
                @"CREATE TABLE IF NOT EXISTS debtpayments (
                    PaymentID INT NOT NULL AUTO_INCREMENT PRIMARY KEY,
                    DebtID INT NOT NULL,
                    Amount DECIMAL(12,2) NOT NULL,
                    PaymentMethod VARCHAR(40) NOT NULL DEFAULT 'Cash',
                    Notes VARCHAR(255) NULL,
                    RecordedBy VARCHAR(120) NULL,
                    CreatedAt DATETIME NOT NULL DEFAULT CURRENT_TIMESTAMP,
                    INDEX IX_DebtPayments_DebtID (DebtID)
                ) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_unicode_ci;",
                @"CREATE TABLE IF NOT EXISTS employeeshifts (
                    ShiftID INT NOT NULL AUTO_INCREMENT PRIMARY KEY,
                    EmployeeID INT NOT NULL,
                    EmployeeName VARCHAR(150) NOT NULL,
                    StartTime VARCHAR(10) NOT NULL DEFAULT '09:00',
                    EndTime VARCHAR(10) NOT NULL DEFAULT '21:00',
                    GraceMinutes INT NOT NULL DEFAULT 15,
                    WorkDays VARCHAR(80) NOT NULL DEFAULT 'يومياً',
                    UpdatedAt DATETIME NOT NULL DEFAULT CURRENT_TIMESTAMP,
                    UNIQUE KEY UQ_EmployeeShifts_EmployeeID (EmployeeID)
                ) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_unicode_ci;",
                @"CREATE TABLE IF NOT EXISTS employeeattendance (
                    AttendanceID INT NOT NULL AUTO_INCREMENT PRIMARY KEY,
                    EmployeeID INT NOT NULL,
                    EmployeeName VARCHAR(150) NOT NULL,
                    WorkDate DATE NOT NULL,
                    CheckInAt DATETIME NULL,
                    CheckOutAt DATETIME NULL,
                    ScheduledStart VARCHAR(10) NULL,
                    ScheduledEnd VARCHAR(10) NULL,
                    LateMinutes INT NOT NULL DEFAULT 0,
                    WorkedMinutes INT NOT NULL DEFAULT 0,
                    Status VARCHAR(24) NOT NULL DEFAULT 'Present',
                    VerificationMethod VARCHAR(32) NOT NULL DEFAULT 'Fingerprint',
                    DeviceInfo VARCHAR(160) NULL,
                    Notes VARCHAR(255) NULL,
                    CreatedAt DATETIME NOT NULL DEFAULT CURRENT_TIMESTAMP,
                    UNIQUE KEY UQ_EmployeeAttendance_EmpDate (EmployeeID, WorkDate)
                ) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_unicode_ci;",
                @"CREATE TABLE IF NOT EXISTS userpermissions (
                    UserID INT NOT NULL PRIMARY KEY,
                    CanEditPrices TINYINT(1) NOT NULL DEFAULT 0,
                    CanManageExpenses TINYINT(1) NOT NULL DEFAULT 0,
                    CanViewNetProfit TINYINT(1) NOT NULL DEFAULT 0,
                    CanViewReports TINYINT(1) NOT NULL DEFAULT 0,
                    CanManageDiscounts TINYINT(1) NOT NULL DEFAULT 0,
                    CanManageProducts TINYINT(1) NOT NULL DEFAULT 0,
                    CanManageDebts TINYINT(1) NOT NULL DEFAULT 0,
                    CanManageStaff TINYINT(1) NOT NULL DEFAULT 0,
                    CanManagePrinters TINYINT(1) NOT NULL DEFAULT 0,
                    CanCloseCashShift TINYINT(1) NOT NULL DEFAULT 0,
                    CanDeleteOrders TINYINT(1) NOT NULL DEFAULT 0,
                    UpdatedBy VARCHAR(120) NULL,
                    UpdatedAt DATETIME NOT NULL DEFAULT CURRENT_TIMESTAMP
                ) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_unicode_ci;",
                @"INSERT INTO restaurantemployees (FullName, JobTitleEN, JobTitleAR, JobTitleKU, DescriptionEN, DescriptionAR, DescriptionKU, IsActive, ShowInCustomerApp)
                  SELECT dw.FullName, 'Delivery Driver', 'عامل توصيل', 'شۆفێری گەیاندن', 'Qamishlo Fast Food Delivery Team', 'فريق توصيل مطعم قامشلو', 'تیمی گەیاندنی خواردنگەی قامشلو', dw.IsActive, 1
                  FROM deliveryworkers dw
                  WHERE NOT EXISTS (SELECT 1 FROM restaurantemployees re WHERE LOWER(TRIM(re.FullName)) = LOWER(TRIM(dw.FullName)));"
            };
            foreach (var s14 in v14Sqls)
            {
                try { await using var c = new MySqlCommand(s14, connection); await c.ExecuteNonQueryAsync(); }
                catch (Exception ex) { logger?.LogWarning(ex, "V14 table ensure warning"); }
            }




        }
        catch (Exception ex)
        {
            logger?.LogError(ex, "DatabaseInitializer encountered an error during startup schema check.");
        }
    }
}
