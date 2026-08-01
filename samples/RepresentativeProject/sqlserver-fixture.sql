SET NOCOUNT ON;
SET XACT_ABORT ON;

IF SCHEMA_ID(N'sales') IS NULL EXEC(N'CREATE SCHEMA sales AUTHORIZATION dbo;');
IF SCHEMA_ID(N'audit') IS NULL EXEC(N'CREATE SCHEMA audit AUTHORIZATION dbo;');
IF SCHEMA_ID(N'integration') IS NULL EXEC(N'CREATE SCHEMA integration AUTHORIZATION dbo;');
GO

CREATE TYPE sales.OrderNumber FROM nvarchar(32) NOT NULL;
GO

CREATE TYPE sales.OrderLineInput AS TABLE
(
    ProductCode nvarchar(40) NOT NULL,
    Quantity int NOT NULL,
    UnitPrice decimal(19,4) NOT NULL
);
GO

CREATE SEQUENCE sales.OrderSequence
    AS bigint
    START WITH 100000
    INCREMENT BY 1
    CACHE 100;
GO

CREATE PARTITION FUNCTION pf_OrderYear(date)
AS RANGE RIGHT FOR VALUES ('2024-01-01', '2025-01-01', '2026-01-01');
GO

CREATE PARTITION SCHEME ps_OrderYear
AS PARTITION pf_OrderYear ALL TO ([PRIMARY]);
GO

CREATE TABLE sales.Customer
(
    CustomerId bigint IDENTITY(1,1) NOT NULL,
    ExternalId uniqueidentifier NOT NULL
        CONSTRAINT DF_Customer_ExternalId DEFAULT NEWSEQUENTIALID(),
    DisplayName nvarchar(200) NOT NULL,
    NormalizedName AS UPPER(LTRIM(RTRIM(DisplayName))) PERSISTED,
    Email nvarchar(320) NULL,
    PasswordHash varbinary(64) NOT NULL,
    ApiTokenHash char(64) NULL,
    ProfileImage varbinary(max) NULL,
    Biography nvarchar(max) NULL,
    PreferredLocale nvarchar(20) NOT NULL
        CONSTRAINT DF_Customer_Locale DEFAULT N'en-IN',
    CreatedAt datetime2(7) NOT NULL
        CONSTRAINT DF_Customer_CreatedAt DEFAULT SYSUTCDATETIME(),
    RowVersion rowversion NOT NULL,
    CONSTRAINT PK_Customer PRIMARY KEY CLUSTERED (CustomerId),
    CONSTRAINT UQ_Customer_ExternalId UNIQUE (ExternalId),
    CONSTRAINT CK_Customer_Email CHECK (Email IS NULL OR Email LIKE N'%_@_%._%')
);
GO

CREATE UNIQUE INDEX UX_Customer_Email
    ON sales.Customer(Email)
    WHERE Email IS NOT NULL;
GO

CREATE TABLE sales.[Order]
(
    OrderId bigint NOT NULL
        CONSTRAINT DF_Order_Id DEFAULT NEXT VALUE FOR sales.OrderSequence,
    CustomerId bigint NOT NULL,
    OrderNumber sales.OrderNumber NOT NULL,
    OrderDate date NOT NULL,
    Status varchar(20) NOT NULL,
    Notes nvarchar(max) NULL,
    TotalAmount decimal(19,4) NOT NULL
        CONSTRAINT DF_Order_Total DEFAULT (0),
    SearchDocument xml NULL,
    CONSTRAINT PK_Order PRIMARY KEY NONCLUSTERED (OrderId, OrderDate),
    CONSTRAINT UQ_Order_Number UNIQUE (OrderNumber, OrderDate),
    CONSTRAINT FK_Order_Customer FOREIGN KEY (CustomerId)
        REFERENCES sales.Customer(CustomerId),
    CONSTRAINT CK_Order_Status CHECK (Status IN ('Pending','Paid','Cancelled','Shipped'))
) ON ps_OrderYear(OrderDate);
GO

CREATE INDEX IX_Order_CustomerDate
    ON sales.[Order](CustomerId, OrderDate DESC)
    INCLUDE (Status, TotalAmount);
GO

CREATE TABLE sales.OrderLine
(
    OrderId bigint NOT NULL,
    OrderDate date NOT NULL,
    LineNumber smallint NOT NULL,
    ProductCode nvarchar(40) NOT NULL,
    Quantity int NOT NULL,
    UnitPrice decimal(19,4) NOT NULL,
    LineTotal AS CONVERT(decimal(19,4), Quantity * UnitPrice) PERSISTED,
    CONSTRAINT PK_OrderLine PRIMARY KEY (OrderId, OrderDate, LineNumber),
    CONSTRAINT FK_OrderLine_Order FOREIGN KEY (OrderId, OrderDate)
        REFERENCES sales.[Order](OrderId, OrderDate) ON DELETE CASCADE,
    CONSTRAINT CK_OrderLine_Quantity CHECK (Quantity > 0),
    CONSTRAINT CK_OrderLine_Price CHECK (UnitPrice >= 0)
);
GO

CREATE TABLE audit.CustomerHistory
(
    CustomerId bigint NOT NULL,
    DisplayName nvarchar(200) NOT NULL,
    ValidFrom datetime2(7) NOT NULL,
    ValidTo datetime2(7) NOT NULL
);
GO

CREATE TABLE audit.TemporalCustomer
(
    CustomerId bigint NOT NULL CONSTRAINT PK_TemporalCustomer PRIMARY KEY,
    DisplayName nvarchar(200) NOT NULL,
    ValidFrom datetime2(7) GENERATED ALWAYS AS ROW START NOT NULL,
    ValidTo datetime2(7) GENERATED ALWAYS AS ROW END NOT NULL,
    PERIOD FOR SYSTEM_TIME (ValidFrom, ValidTo)
)
WITH
(
    SYSTEM_VERSIONING = ON
    (
        HISTORY_TABLE = audit.TemporalCustomerHistory,
        DATA_CONSISTENCY_CHECK = ON
    )
);
GO

CREATE TABLE audit.OrderChange
(
    ChangeId bigint IDENTITY(1,1) NOT NULL CONSTRAINT PK_OrderChange PRIMARY KEY,
    OrderId bigint NOT NULL,
    ActionName varchar(10) NOT NULL,
    ChangedAt datetime2(7) NOT NULL CONSTRAINT DF_OrderChange_At DEFAULT SYSUTCDATETIME(),
    ChangedBy sysname NOT NULL CONSTRAINT DF_OrderChange_By DEFAULT ORIGINAL_LOGIN()
);
GO

CREATE OR ALTER VIEW sales.vw_CustomerOrderSummary
AS
SELECT
    c.CustomerId,
    c.DisplayName,
    COUNT_BIG(o.OrderId) AS OrderCount,
    SUM(o.TotalAmount) AS LifetimeValue
FROM sales.Customer AS c
LEFT JOIN sales.[Order] AS o ON o.CustomerId = c.CustomerId
GROUP BY c.CustomerId, c.DisplayName;
GO

CREATE OR ALTER FUNCTION sales.fn_NormalizeOrderNumber(@value nvarchar(32))
RETURNS nvarchar(32)
AS
BEGIN
    RETURN UPPER(REPLACE(LTRIM(RTRIM(@value)), N' ', N''));
END;
GO

CREATE OR ALTER FUNCTION sales.fn_OrdersForCustomer(@customerId bigint)
RETURNS TABLE
AS
RETURN
(
    SELECT OrderId, OrderNumber, OrderDate, Status, TotalAmount
    FROM sales.[Order]
    WHERE CustomerId = @customerId
);
GO

CREATE OR ALTER PROCEDURE sales.usp_CreateOrder
    @CustomerId bigint,
    @OrderNumber sales.OrderNumber,
    @OrderDate date,
    @Lines sales.OrderLineInput READONLY
AS
BEGIN
    SET NOCOUNT ON;
    SET XACT_ABORT ON;

    BEGIN TRANSACTION;
    DECLARE @OrderId bigint = NEXT VALUE FOR sales.OrderSequence;

    INSERT sales.[Order](OrderId, CustomerId, OrderNumber, OrderDate, Status)
    VALUES (@OrderId, @CustomerId, @OrderNumber, @OrderDate, 'Pending');

    INSERT sales.OrderLine(OrderId, OrderDate, LineNumber, ProductCode, Quantity, UnitPrice)
    SELECT @OrderId, @OrderDate,
           CONVERT(smallint, ROW_NUMBER() OVER (ORDER BY ProductCode)),
           ProductCode, Quantity, UnitPrice
    FROM @Lines;

    UPDATE sales.[Order]
    SET TotalAmount =
    (
        SELECT SUM(LineTotal)
        FROM sales.OrderLine
        WHERE OrderId = @OrderId AND OrderDate = @OrderDate
    )
    WHERE OrderId = @OrderId AND OrderDate = @OrderDate;

    COMMIT TRANSACTION;
END;
GO

CREATE OR ALTER TRIGGER sales.tr_Order_Audit
ON sales.[Order]
AFTER INSERT, UPDATE, DELETE
AS
BEGIN
    SET NOCOUNT ON;
    INSERT audit.OrderChange(OrderId, ActionName)
    SELECT COALESCE(i.OrderId, d.OrderId),
           CASE WHEN i.OrderId IS NULL THEN 'DELETE'
                WHEN d.OrderId IS NULL THEN 'INSERT'
                ELSE 'UPDATE' END
    FROM inserted AS i
    FULL OUTER JOIN deleted AS d
      ON d.OrderId = i.OrderId AND d.OrderDate = i.OrderDate;
END;
GO

CREATE SYNONYM integration.CurrentCustomers FOR sales.Customer;
GO

EXEC sys.sp_addextendedproperty
    @name = N'MS_Description',
    @value = N'Customer credentials are application data and must migrate byte-for-byte without entering logs.',
    @level0type = N'SCHEMA', @level0name = N'sales',
    @level1type = N'TABLE', @level1name = N'Customer';
GO

IF DATABASE_PRINCIPAL_ID(N'migration_fixture_reader') IS NULL
    CREATE ROLE migration_fixture_reader AUTHORIZATION dbo;
GRANT SELECT ON SCHEMA::sales TO migration_fixture_reader;
GO

CREATE TABLE integration.ThisIdentifierIsIntentionallyLongToExercisePostgreSqlIdentifierShorteningAndDeterministicCollisionHandling
(
    ThisColumnNameIsAlsoIntentionallyLongToExerciseUtf8IdentifierByteLengthRules nvarchar(100) NULL
);
GO

-- Representative ordinary application data. These are test values, not usable credentials.
INSERT sales.Customer(DisplayName, Email, PasswordHash, ApiTokenHash, ProfileImage, Biography, PreferredLocale)
VALUES
(
    N'Zoë 東京 ग्राहक',
    N'fixture@example.invalid',
    HASHBYTES('SHA2_512', N'fixture-password-material'),
    CONVERT(char(64), HASHBYTES('SHA2_256', N'fixture-token-material'), 2),
    0x00010203FEFF,
    REPLICATE(N'Unicode migration text Ω漢字🙂 ', 200),
    N'hi-IN'
);
GO

-- Deliberate manual-review/unsupported examples.
CREATE TABLE integration.HierarchySample
(
    NodeId hierarchyid NOT NULL,
    NodeName nvarchar(100) NOT NULL,
    CONSTRAINT PK_HierarchySample PRIMARY KEY (NodeId)
);
GO

CREATE OR ALTER PROCEDURE integration.usp_DynamicSqlManualReview
    @SchemaName sysname
AS
BEGIN
    DECLARE @sql nvarchar(max) =
        N'SELECT COUNT_BIG(*) FROM ' + QUOTENAME(@SchemaName) + N'.Customer;';
    EXEC sys.sp_executesql @sql;
END;
GO

