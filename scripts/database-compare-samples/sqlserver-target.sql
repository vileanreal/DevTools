/*
  Database Compare sample: older SQL Server target schema.
  Run this script in a second empty test database, then save that database as the right connection.
*/
SET NOCOUNT ON;
GO

IF OBJECT_ID(N'dbo.Customers', N'U') IS NULL
BEGIN
    CREATE TABLE dbo.Customers (
        Id int IDENTITY(1,1) NOT NULL CONSTRAINT PK_Customers PRIMARY KEY,
        CustomerCode nvarchar(30) NOT NULL,
        CustomerName nvarchar(200) NOT NULL,
        Email nvarchar(320) NULL,
        Region nvarchar(80) NULL,
        IsActive bit NOT NULL CONSTRAINT DF_Customers_IsActive DEFAULT (1),
        CreatedAt datetime2 NOT NULL CONSTRAINT DF_Customers_CreatedAt DEFAULT (sysutcdatetime()),
        CONSTRAINT UQ_Customers_CustomerCode UNIQUE (CustomerCode)
    );
END;
GO

IF OBJECT_ID(N'dbo.Orders', N'U') IS NULL
BEGIN
    CREATE TABLE dbo.Orders (
        Id bigint IDENTITY(1,1) NOT NULL CONSTRAINT PK_Orders PRIMARY KEY,
        CustomerId int NOT NULL,
        OrderNumber nvarchar(40) NOT NULL,
        Status nvarchar(30) NOT NULL,
        Subtotal decimal(18,2) NOT NULL,
        Discount decimal(18,2) NOT NULL CONSTRAINT DF_Orders_Discount DEFAULT (0),
        Total decimal(18,2) NOT NULL,
        CreatedAt datetime2 NOT NULL CONSTRAINT DF_Orders_CreatedAt DEFAULT (sysutcdatetime()),
        CONSTRAINT FK_Orders_Customers FOREIGN KEY (CustomerId) REFERENCES dbo.Customers(Id),
        CONSTRAINT UQ_Orders_OrderNumber UNIQUE (OrderNumber)
    );
END;
GO

CREATE OR ALTER PROCEDURE dbo.usp_CustomerRevenueReport
    @FromDate datetime2 = NULL,
    @ToDate datetime2 = NULL,
    @Region nvarchar(80) = NULL,
    @PageNumber int = 1,
    @PageSize int = 100
AS
BEGIN
    SET NOCOUNT ON;

    DECLARE @EffectiveFrom datetime2 = COALESCE(@FromDate, DATEADD(month, -6, sysutcdatetime()));
    DECLARE @EffectiveTo datetime2 = COALESCE(@ToDate, sysutcdatetime());
    DECLARE @Offset int = (CASE WHEN @PageNumber < 1 THEN 0 ELSE @PageNumber - 1 END) * @PageSize;

    ;WITH OrderTotals AS (
        SELECT o.CustomerId,
               COUNT(*) AS OrderCount,
               SUM(o.Total) AS TotalRevenue,
               AVG(o.Total) AS AverageOrderValue,
               MIN(o.CreatedAt) AS FirstOrderAt,
               MAX(o.CreatedAt) AS LastOrderAt
        FROM dbo.Orders o
        WHERE o.CreatedAt >= @EffectiveFrom
          AND o.CreatedAt < DATEADD(day, 1, @EffectiveTo)
        GROUP BY o.CustomerId
    ),
    CustomerMetrics AS (
        SELECT c.Id AS CustomerId,
               c.CustomerCode,
               c.CustomerName,
               c.Email,
               c.Region,
               c.IsActive,
               COALESCE(ot.OrderCount, 0) AS OrderCount,
               COALESCE(ot.TotalRevenue, 0) AS TotalRevenue,
               COALESCE(ot.AverageOrderValue, 0) AS AverageOrderValue,
               ot.FirstOrderAt,
               ot.LastOrderAt
        FROM dbo.Customers c
        LEFT JOIN OrderTotals ot ON ot.CustomerId = c.Id
        WHERE c.IsActive = 1
          AND (@Region IS NULL OR c.Region = @Region)
    )
    SELECT CustomerId,
           CustomerCode,
           CustomerName,
           Email,
           Region,
           OrderCount,
           TotalRevenue,
           AverageOrderValue,
           FirstOrderAt,
           LastOrderAt,
           COUNT(*) OVER () AS TotalCustomers
    FROM CustomerMetrics
    ORDER BY TotalRevenue DESC, CustomerName
    OFFSET @Offset ROWS FETCH NEXT @PageSize ROWS ONLY;
END;
GO

CREATE OR ALTER PROCEDURE dbo.usp_LegacyOrderExport
    @FromDate datetime2 = NULL,
    @ToDate datetime2 = NULL
AS
BEGIN
    SET NOCOUNT ON;

    SELECT o.OrderNumber,
           c.CustomerCode,
           c.CustomerName,
           o.Status,
           o.Subtotal,
           o.Discount,
           o.Total,
           o.CreatedAt,
           CASE WHEN o.Status = N'Completed' THEN N'Y' ELSE N'N' END AS IsComplete,
           CONCAT(c.CustomerCode, N'-', o.OrderNumber) AS LegacyExportKey
    FROM dbo.Orders o
    JOIN dbo.Customers c ON c.Id = o.CustomerId
    WHERE (@FromDate IS NULL OR o.CreatedAt >= @FromDate)
      AND (@ToDate IS NULL OR o.CreatedAt < DATEADD(day, 1, @ToDate))
    ORDER BY o.CreatedAt, o.OrderNumber;
END;
GO

CREATE OR ALTER PROCEDURE dbo.usp_ProductCatalog
    @Search nvarchar(100) = NULL
AS
BEGIN
    SET NOCOUNT ON;
    SELECT @Search AS SearchText,
           N'This unchanged procedure verifies the Unchanged category.' AS Description;
END;
GO
