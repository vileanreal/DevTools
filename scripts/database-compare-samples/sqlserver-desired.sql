/*
  Database Compare sample: desired SQL Server schema.
  Run this script in an empty test database, then save that database as the left connection.
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
    @MinimumRevenue decimal(18,2) = 0,
    @IncludeInactive bit = 0,
    @PageNumber int = 1,
    @PageSize int = 100
AS
BEGIN
    SET NOCOUNT ON;
    SET XACT_ABORT ON;

    SET @PageNumber = CASE WHEN @PageNumber < 1 THEN 1 ELSE @PageNumber END;
    SET @PageSize = CASE WHEN @PageSize < 1 THEN 100 WHEN @PageSize > 1000 THEN 1000 ELSE @PageSize END;

    DECLARE @EffectiveFrom datetime2 = COALESCE(@FromDate, DATEADD(year, -1, sysutcdatetime()));
    DECLARE @EffectiveTo datetime2 = COALESCE(@ToDate, sysutcdatetime());
    DECLARE @Offset int = (@PageNumber - 1) * @PageSize;

    ;WITH FilteredOrders AS (
        SELECT o.Id,
               o.CustomerId,
               o.Status,
               o.Subtotal,
               o.Discount,
               o.Total,
               o.CreatedAt
        FROM dbo.Orders o
        WHERE o.CreatedAt >= @EffectiveFrom
          AND o.CreatedAt < DATEADD(day, 1, @EffectiveTo)
    ),
    CustomerMetrics AS (
        SELECT c.Id AS CustomerId,
               c.CustomerCode,
               c.CustomerName,
               c.Email,
               c.Region,
               c.IsActive,
               COUNT(fo.Id) AS OrderCount,
               SUM(CASE WHEN fo.Status = N'Completed' THEN 1 ELSE 0 END) AS CompletedOrders,
               SUM(CASE WHEN fo.Status = N'Pending' THEN 1 ELSE 0 END) AS PendingOrders,
               SUM(CASE WHEN fo.Status = N'Cancelled' THEN 1 ELSE 0 END) AS CancelledOrders,
               COALESCE(SUM(fo.Subtotal), 0) AS GrossRevenue,
               COALESCE(SUM(fo.Discount), 0) AS Discounts,
               COALESCE(SUM(fo.Total), 0) AS NetRevenue,
               COALESCE(AVG(NULLIF(fo.Total, 0)), 0) AS AverageOrderValue,
               MIN(fo.CreatedAt) AS FirstOrderAt,
               MAX(fo.CreatedAt) AS LastOrderAt
        FROM dbo.Customers c
        LEFT JOIN FilteredOrders fo ON fo.CustomerId = c.Id
        WHERE (@IncludeInactive = 1 OR c.IsActive = 1)
          AND (@Region IS NULL OR c.Region = @Region)
        GROUP BY c.Id, c.CustomerCode, c.CustomerName, c.Email, c.Region, c.IsActive
    ),
    RankedCustomers AS (
        SELECT cm.*,
               DENSE_RANK() OVER (ORDER BY cm.NetRevenue DESC) AS RevenueRank,
               NTILE(4) OVER (ORDER BY cm.NetRevenue DESC) AS RevenueQuartile,
               SUM(cm.NetRevenue) OVER () AS TotalPortfolioRevenue,
               COUNT(*) OVER () AS TotalCustomers
        FROM CustomerMetrics cm
        WHERE cm.NetRevenue >= @MinimumRevenue
    )
    SELECT CustomerId,
           CustomerCode,
           CustomerName,
           Email,
           Region,
           IsActive,
           OrderCount,
           CompletedOrders,
           PendingOrders,
           CancelledOrders,
           GrossRevenue,
           Discounts,
           NetRevenue,
           AverageOrderValue,
           RevenueRank,
           RevenueQuartile,
           TotalPortfolioRevenue,
           CASE WHEN TotalPortfolioRevenue = 0 THEN 0
                ELSE (NetRevenue / TotalPortfolioRevenue) * 100 END AS PortfolioPercent,
           FirstOrderAt,
           LastOrderAt,
           TotalCustomers
    FROM RankedCustomers
    ORDER BY NetRevenue DESC, CustomerName
    OFFSET @Offset ROWS FETCH NEXT @PageSize ROWS ONLY;
END;
GO

CREATE OR ALTER PROCEDURE dbo.usp_RebuildCustomerMetrics
    @CustomerId int = NULL,
    @BatchSize int = 500,
    @DryRun bit = 0
AS
BEGIN
    SET NOCOUNT ON;
    SET XACT_ABORT ON;

    DECLARE @RowsAffected int = 0;
    DECLARE @StartedAt datetime2 = sysutcdatetime();

    ;WITH CandidateCustomers AS (
        SELECT TOP (@BatchSize) c.Id
        FROM dbo.Customers c
        WHERE @CustomerId IS NULL OR c.Id = @CustomerId
        ORDER BY c.Id
    ),
    CalculatedMetrics AS (
        SELECT cc.Id AS CustomerId,
               COUNT(o.Id) AS OrderCount,
               COALESCE(SUM(o.Total), 0) AS LifetimeRevenue,
               MAX(o.CreatedAt) AS LastOrderAt
        FROM CandidateCustomers cc
        LEFT JOIN dbo.Orders o ON o.CustomerId = cc.Id
        GROUP BY cc.Id
    )
    SELECT CustomerId, OrderCount, LifetimeRevenue, LastOrderAt
    INTO #CalculatedMetrics
    FROM CalculatedMetrics;

    IF @DryRun = 0
    BEGIN
        BEGIN TRANSACTION;
        UPDATE c
           SET c.Region = CASE
                WHEN m.LifetimeRevenue >= 100000 THEN N'Enterprise'
                WHEN m.LifetimeRevenue >= 10000 THEN N'Growth'
                ELSE COALESCE(c.Region, N'Standard')
           END
        FROM dbo.Customers c
        JOIN #CalculatedMetrics m ON m.CustomerId = c.Id;
        SET @RowsAffected = @@ROWCOUNT;
        COMMIT TRANSACTION;
    END;

    SELECT @RowsAffected AS RowsAffected,
           @DryRun AS DryRun,
           @StartedAt AS StartedAt,
           sysutcdatetime() AS CompletedAt;
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
