#!/usr/bin/env bash
set -euo pipefail

script_dir="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
project_root="$(cd "$script_dir/.." && pwd)"
output_dir="$project_root/src/DevTools.Web/sample-databases"
left_database="$output_dir/commerce-desired.db"
right_database="$output_dir/commerce-target.db"
backup_dir="$(mktemp -d /tmp/devtools-database-compare-backup.XXXXXX)"

mkdir -p "$output_dir"

for database in "$left_database" "$right_database"; do
    if [[ -f "$database" ]]; then
        mv "$database" "$backup_dir/$(basename "$database")"
    fi
done

sqlite3 "$left_database" <<'SQL'
PRAGMA foreign_keys = ON;

CREATE TABLE Customers (
    Id INTEGER PRIMARY KEY,
    Code TEXT NOT NULL UNIQUE,
    Name TEXT NOT NULL,
    Email TEXT,
    IsActive INTEGER NOT NULL DEFAULT 1,
    CreatedAt TEXT NOT NULL DEFAULT CURRENT_TIMESTAMP
);

CREATE TABLE Products (
    Id INTEGER PRIMARY KEY,
    Sku TEXT NOT NULL UNIQUE,
    Name TEXT NOT NULL,
    Price REAL NOT NULL,
    IsActive INTEGER NOT NULL DEFAULT 1
);

CREATE TABLE Orders (
    Id INTEGER PRIMARY KEY,
    CustomerId INTEGER NOT NULL,
    OrderNumber TEXT NOT NULL UNIQUE,
    Status TEXT NOT NULL DEFAULT 'Pending',
    Subtotal REAL NOT NULL,
    Discount REAL NOT NULL DEFAULT 0,
    Total REAL NOT NULL,
    CreatedAt TEXT NOT NULL DEFAULT CURRENT_TIMESTAMP,
    FOREIGN KEY (CustomerId) REFERENCES Customers(Id)
);

CREATE TABLE OrderItems (
    Id INTEGER PRIMARY KEY,
    OrderId INTEGER NOT NULL,
    ProductId INTEGER NOT NULL,
    Quantity INTEGER NOT NULL DEFAULT 1,
    UnitPrice REAL NOT NULL,
    FOREIGN KEY (OrderId) REFERENCES Orders(Id),
    FOREIGN KEY (ProductId) REFERENCES Products(Id)
);

CREATE TABLE AuditEvents (
    Id INTEGER PRIMARY KEY,
    EventType TEXT NOT NULL,
    EntityName TEXT NOT NULL,
    EntityId TEXT,
    Details TEXT,
    CreatedAt TEXT NOT NULL DEFAULT CURRENT_TIMESTAMP
);

CREATE INDEX IX_Customers_Email ON Customers(Email);
CREATE INDEX IX_Products_Name ON Products(Name);
CREATE INDEX IX_Orders_CustomerId ON Orders(CustomerId);
CREATE INDEX IX_OrderItems_OrderId ON OrderItems(OrderId);

CREATE VIEW ActiveProducts AS
SELECT Id, Sku, Name, Price
FROM Products
WHERE IsActive = 1;

CREATE VIEW CustomerOrderSummary AS
SELECT c.Id AS CustomerId,
       c.Code,
       c.Name,
       COUNT(o.Id) AS OrderCount,
       COALESCE(SUM(o.Total), 0) AS LifetimeValue
FROM Customers c
LEFT JOIN Orders o ON o.CustomerId = c.Id
GROUP BY c.Id, c.Code, c.Name;

CREATE VIEW CustomerRevenueReport AS
WITH OrderTotals AS (
    SELECT o.CustomerId,
           COUNT(*) AS OrderCount,
           SUM(CASE WHEN o.Status = 'Completed' THEN 1 ELSE 0 END) AS CompletedOrders,
           SUM(CASE WHEN o.Status = 'Pending' THEN 1 ELSE 0 END) AS PendingOrders,
           SUM(o.Subtotal) AS GrossRevenue,
           SUM(o.Discount) AS Discounts,
           SUM(o.Total) AS NetRevenue,
           MIN(o.CreatedAt) AS FirstOrderAt,
           MAX(o.CreatedAt) AS LastOrderAt
    FROM Orders o
    GROUP BY o.CustomerId
),
ItemTotals AS (
    SELECT o.CustomerId,
           SUM(oi.Quantity) AS UnitsPurchased,
           COUNT(DISTINCT oi.ProductId) AS DistinctProducts
    FROM Orders o
    JOIN OrderItems oi ON oi.OrderId = o.Id
    GROUP BY o.CustomerId
)
SELECT c.Id AS CustomerId,
       c.Code,
       c.Name,
       c.Email,
       COALESCE(ot.OrderCount, 0) AS OrderCount,
       COALESCE(ot.CompletedOrders, 0) AS CompletedOrders,
       COALESCE(ot.PendingOrders, 0) AS PendingOrders,
       COALESCE(ot.GrossRevenue, 0) AS GrossRevenue,
       COALESCE(ot.Discounts, 0) AS Discounts,
       COALESCE(ot.NetRevenue, 0) AS NetRevenue,
       COALESCE(it.UnitsPurchased, 0) AS UnitsPurchased,
       COALESCE(it.DistinctProducts, 0) AS DistinctProducts,
       ot.FirstOrderAt,
       ot.LastOrderAt
FROM Customers c
LEFT JOIN OrderTotals ot ON ot.CustomerId = c.Id
LEFT JOIN ItemTotals it ON it.CustomerId = c.Id
WHERE c.IsActive = 1;

INSERT INTO Customers (Id, Code, Name, Email) VALUES
    (1, 'CUST-001', 'Northwind Labs', 'ops@northwind.test'),
    (2, 'CUST-002', 'Contoso Retail', 'orders@contoso.test');
INSERT INTO Products (Id, Sku, Name, Price) VALUES
    (1, 'DEV-KEYBOARD', 'Developer Keyboard', 129.50),
    (2, 'DEV-MOUSE', 'Precision Mouse', 69.00);
INSERT INTO Orders (Id, CustomerId, OrderNumber, Status, Subtotal, Discount, Total) VALUES
    (1, 1, 'ORD-1001', 'Completed', 198.50, 10.00, 188.50),
    (2, 2, 'ORD-1002', 'Pending', 129.50, 0.00, 129.50);
INSERT INTO OrderItems (Id, OrderId, ProductId, Quantity, UnitPrice) VALUES
    (1, 1, 1, 1, 129.50),
    (2, 1, 2, 1, 69.00),
    (3, 2, 1, 1, 129.50);
INSERT INTO AuditEvents (Id, EventType, EntityName, EntityId, Details) VALUES
    (1, 'SchemaUpgrade', 'Database', NULL, 'Desired schema includes auditing and order discounts.');
SQL

sqlite3 "$right_database" <<'SQL'
PRAGMA foreign_keys = ON;

CREATE TABLE Customers (
    Id INTEGER PRIMARY KEY,
    Code TEXT NOT NULL UNIQUE,
    Name TEXT,
    CreatedAt TEXT NOT NULL DEFAULT CURRENT_TIMESTAMP
);

CREATE TABLE Products (
    Id INTEGER PRIMARY KEY,
    Sku TEXT NOT NULL UNIQUE,
    Name TEXT NOT NULL,
    Price REAL NOT NULL,
    IsActive INTEGER NOT NULL DEFAULT 1
);

CREATE TABLE Orders (
    Id INTEGER PRIMARY KEY,
    CustomerId INTEGER NOT NULL,
    OrderNumber TEXT NOT NULL UNIQUE,
    Subtotal REAL NOT NULL,
    Total INTEGER NOT NULL,
    CreatedAt TEXT NOT NULL DEFAULT CURRENT_TIMESTAMP,
    FOREIGN KEY (CustomerId) REFERENCES Customers(Id)
);

CREATE TABLE OrderItems (
    Id INTEGER PRIMARY KEY,
    OrderId INTEGER NOT NULL,
    ProductId INTEGER NOT NULL,
    Quantity INTEGER NOT NULL DEFAULT 1,
    UnitPrice REAL NOT NULL,
    FOREIGN KEY (OrderId) REFERENCES Orders(Id),
    FOREIGN KEY (ProductId) REFERENCES Products(Id)
);

CREATE TABLE LegacySettings (
    Id INTEGER PRIMARY KEY,
    SettingKey TEXT NOT NULL UNIQUE,
    SettingValue TEXT
);

CREATE INDEX IX_Products_Name ON Products(Name);
CREATE INDEX IX_Orders_CustomerId ON Orders(CustomerId, CreatedAt);
CREATE INDEX IX_OrderItems_OrderId ON OrderItems(OrderId);

CREATE VIEW ActiveProducts AS
SELECT Id, Sku, Name, Price
FROM Products
WHERE IsActive = 1;

CREATE VIEW CustomerOrderSummary AS
SELECT c.Id AS CustomerId,
       c.Name,
       COUNT(o.Id) AS OrderCount,
       COALESCE(SUM(o.Total), 0) AS LifetimeValue
FROM Customers c
LEFT JOIN Orders o ON o.CustomerId = c.Id
GROUP BY c.Id, c.Name;

CREATE VIEW CustomerRevenueReport AS
WITH OrderTotals AS (
    SELECT o.CustomerId,
           COUNT(*) AS OrderCount,
           SUM(o.Total) AS TotalRevenue,
           MIN(o.CreatedAt) AS FirstOrderAt,
           MAX(o.CreatedAt) AS LastOrderAt
    FROM Orders o
    GROUP BY o.CustomerId
)
SELECT c.Id AS CustomerId,
       c.Code,
       c.Name,
       COALESCE(ot.OrderCount, 0) AS OrderCount,
       COALESCE(ot.TotalRevenue, 0) AS TotalRevenue,
       ot.FirstOrderAt,
       ot.LastOrderAt
FROM Customers c
LEFT JOIN OrderTotals ot ON ot.CustomerId = c.Id;

CREATE VIEW LegacyConfiguration AS
SELECT SettingKey, SettingValue
FROM LegacySettings;

INSERT INTO Customers (Id, Code, Name) VALUES
    (1, 'CUST-001', 'Northwind Labs'),
    (2, 'CUST-002', 'Contoso Retail');
INSERT INTO Products (Id, Sku, Name, Price) VALUES
    (1, 'DEV-KEYBOARD', 'Developer Keyboard', 129.50),
    (2, 'DEV-MOUSE', 'Precision Mouse', 69.00);
INSERT INTO Orders (Id, CustomerId, OrderNumber, Subtotal, Total) VALUES
    (1, 1, 'ORD-1001', 198.50, 198),
    (2, 2, 'ORD-1002', 129.50, 129);
INSERT INTO OrderItems (Id, OrderId, ProductId, Quantity, UnitPrice) VALUES
    (1, 1, 1, 1, 129.50),
    (2, 1, 2, 1, 69.00),
    (3, 2, 1, 1, 129.50);
INSERT INTO LegacySettings (Id, SettingKey, SettingValue) VALUES
    (1, 'OrderMode', 'Legacy');
SQL

printf 'Created database comparison samples:\n'
printf '  Desired (left): %s\n' "$left_database"
printf '  Target  (right): %s\n' "$right_database"
if compgen -G "$backup_dir/*" >/dev/null; then
    printf 'Previous samples moved to: %s\n' "$backup_dir"
else
    rmdir "$backup_dir"
fi
