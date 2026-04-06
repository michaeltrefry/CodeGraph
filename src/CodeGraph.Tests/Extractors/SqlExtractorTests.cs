using Microsoft.Extensions.Logging.Abstractions;
using Shouldly;
using CodeGraph.Extractors.Sql;
using CodeGraph.Models;
using CodeGraph.Services;

namespace CodeGraph.Tests.Extractors;

public class SqlExtractorTests
{
    private static readonly ExtractorContext TestContext = new()
    {
        ProjectName = "TestProject",
        RootPath = "/test"
    };

    private static async Task<ExtractionResult> ExtractSqlAsync(string sql)
    {
        var extractor = new SqlExtractor(NullLogger<SqlExtractor>.Instance);
        return await extractor.ExtractAsync("/test/schema.sql", sql, TestContext);
    }

    [Fact]
    public async Task SupportedExtensions_IncludesSql()
    {
        var extractor = new SqlExtractor(NullLogger<SqlExtractor>.Instance);
        extractor.SupportedExtensions.ShouldContain(".sql");
    }

    // ── CREATE TABLE ──────────────────────────────────────────────

    [Fact]
    public async Task Extracts_CreateTable()
    {
        var sql = """
            CREATE TABLE dbo.Orders (
                OrderId INT NOT NULL PRIMARY KEY,
                CustomerId INT NOT NULL,
                Total DECIMAL(18,2),
                CreatedDate DATETIME
            );
            """;

        var result = await ExtractSqlAsync(sql);

        var table = result.Nodes.ShouldContain(n => n.Label == NodeLabel.Table);
        table.Name.ShouldBe("dbo.Orders");
        table.QualifiedName.ShouldBe("TestProject.dbo.Orders");
        table.Properties["column_count"].ShouldBe(4);

        var columns = (List<Dictionary<string, object>>)table.Properties["columns"];
        columns[0]["name"].ShouldBe("OrderId");
        columns[0]["is_primary_key"].ShouldBe(true);
    }

    [Fact]
    public async Task Extracts_CreateTable_WithTableLevelPrimaryKey()
    {
        var sql = """
            CREATE TABLE Customers (
                CustomerId INT NOT NULL,
                Name NVARCHAR(200),
                CONSTRAINT PK_Customers PRIMARY KEY (CustomerId)
            );
            """;

        var result = await ExtractSqlAsync(sql);

        var table = result.Nodes.ShouldContain(n => n.Label == NodeLabel.Table);
        var columns = (List<Dictionary<string, object>>)table.Properties["columns"];
        var pkCol = columns.Single(c => (string)c["name"] == "CustomerId");
        pkCol["is_primary_key"].ShouldBe(true);
    }

    [Fact]
    public async Task Extracts_CreateTable_WithInlineForeignKey()
    {
        var sql = """
            CREATE TABLE Orders (
                OrderId INT NOT NULL PRIMARY KEY,
                CustomerId INT NOT NULL REFERENCES Customers(CustomerId)
            );
            """;

        var result = await ExtractSqlAsync(sql);

        var fkEdge = result.Edges.ShouldContain(e =>
            e.Type == EdgeType.QUERIES && e.Properties != null &&
            (string)e.Properties["relationship"] == "foreign_key");
        fkEdge.SourceQN.ShouldBe("TestProject.Orders");
        fkEdge.TargetQN.ShouldBe("TestProject.Customers");
    }

    [Fact]
    public async Task Extracts_CreateTable_WithTableLevelForeignKey()
    {
        var sql = """
            CREATE TABLE OrderItems (
                ItemId INT NOT NULL PRIMARY KEY,
                OrderId INT NOT NULL,
                ProductId INT NOT NULL,
                CONSTRAINT FK_OrderItems_Orders FOREIGN KEY (OrderId) REFERENCES Orders(OrderId),
                CONSTRAINT FK_OrderItems_Products FOREIGN KEY (ProductId) REFERENCES Products(ProductId)
            );
            """;

        var result = await ExtractSqlAsync(sql);

        var fkEdges = result.Edges.Where(e =>
            e.Type == EdgeType.QUERIES && e.Properties != null &&
            (string)e.Properties["relationship"] == "foreign_key").ToList();

        fkEdges.Count.ShouldBe(2);
        fkEdges.ShouldContain(e => e.TargetQN == "TestProject.Orders");
        fkEdges.ShouldContain(e => e.TargetQN == "TestProject.Products");
    }

    // ── CREATE VIEW ───────────────────────────────────────────────

    [Fact]
    public async Task Extracts_CreateView_WithTableReferences()
    {
        var sql = """
            CREATE VIEW dbo.vwOrderSummary AS
            SELECT o.OrderId, c.Name, o.Total
            FROM Orders o
            INNER JOIN Customers c ON o.CustomerId = c.CustomerId;
            """;

        var result = await ExtractSqlAsync(sql);

        var view = result.Nodes.ShouldContain(n => n.Label == NodeLabel.View);
        view.Name.ShouldBe("dbo.vwOrderSummary");

        // Should have QUERIES edges to Orders and Customers
        var queryEdges = result.Edges.Where(e =>
            e.Type == EdgeType.QUERIES &&
            e.SourceQN == "TestProject.dbo.vwOrderSummary").ToList();

        queryEdges.ShouldContain(e => e.TargetQN == "TestProject.Orders");
        queryEdges.ShouldContain(e => e.TargetQN == "TestProject.Customers");
    }

    // ── CREATE PROCEDURE ──────────────────────────────────────────

    [Fact]
    public async Task Extracts_CreateProcedure()
    {
        var sql = """
            CREATE PROCEDURE dbo.uspGetOrder
                @OrderId INT,
                @IncludeDetails BIT = 0
            AS
            BEGIN
                SELECT * FROM Orders WHERE OrderId = @OrderId;

                IF @IncludeDetails = 1
                    SELECT * FROM OrderItems WHERE OrderId = @OrderId;
            END;
            """;

        var result = await ExtractSqlAsync(sql);

        var proc = result.Nodes.ShouldContain(n => n.Label == NodeLabel.StoredProcedure);
        proc.Name.ShouldBe("dbo.uspGetOrder");
        proc.Properties["parameter_count"].ShouldBe(2);

        var parameters = (List<Dictionary<string, object>>)proc.Properties["parameters"];
        parameters[0]["name"].ShouldBe("@OrderId");

        // Should query Orders and OrderItems
        var queryEdges = result.Edges.Where(e =>
            e.Type == EdgeType.QUERIES &&
            e.SourceQN == "TestProject.dbo.uspGetOrder").ToList();

        queryEdges.ShouldContain(e => e.TargetQN == "TestProject.Orders");
        queryEdges.ShouldContain(e => e.TargetQN == "TestProject.OrderItems");
    }

    [Fact]
    public async Task Extracts_ProcedureCallingProcedure()
    {
        var sql = """
            CREATE PROCEDURE dbo.uspProcessOrder
                @OrderId INT
            AS
            BEGIN
                EXEC dbo.uspValidateOrder @OrderId;
                UPDATE Orders SET Status = 'Processed' WHERE OrderId = @OrderId;
            END;
            """;

        var result = await ExtractSqlAsync(sql);

        result.Edges.ShouldContain(e =>
            e.Type == EdgeType.CALLS &&
            e.SourceQN == "TestProject.dbo.uspProcessOrder" &&
            e.TargetQN == "TestProject.dbo.uspValidateOrder");
    }

    // ── CREATE FUNCTION ───────────────────────────────────────────

    [Fact]
    public async Task Extracts_CreateFunction()
    {
        var sql = """
            CREATE FUNCTION dbo.fnGetOrderTotal(@OrderId INT)
            RETURNS DECIMAL(18,2)
            AS
            BEGIN
                DECLARE @Total DECIMAL(18,2);
                SELECT @Total = SUM(Price * Quantity) FROM OrderItems WHERE OrderId = @OrderId;
                RETURN @Total;
            END;
            """;

        var result = await ExtractSqlAsync(sql);

        var func = result.Nodes.ShouldContain(n => n.Label == NodeLabel.Function);
        func.Name.ShouldBe("dbo.fnGetOrderTotal");
        func.Properties["kind"].ShouldBe("sql_function");
        func.Properties["parameter_count"].ShouldBe(1);
    }

    // ── CREATE INDEX ──────────────────────────────────────────────

    [Fact]
    public async Task Extracts_CreateIndex()
    {
        var sql = """
            CREATE UNIQUE INDEX IX_Orders_CustomerId ON Orders(CustomerId, CreatedDate);
            """;

        var result = await ExtractSqlAsync(sql);

        var indexEdge = result.Edges.ShouldContain(e =>
            e.Type == EdgeType.DEFINES &&
            e.Properties != null &&
            (string)e.Properties["relationship"] == "index");

        indexEdge.SourceQN.ShouldBe("TestProject.Orders");
        indexEdge.Properties!["index_name"].ShouldBe("IX_Orders_CustomerId");
        indexEdge.Properties!["is_unique"].ShouldBe(true);
    }

    // ── ALTER TABLE ───────────────────────────────────────────────

    [Fact]
    public async Task Extracts_AlterTable_AddForeignKey()
    {
        var sql = """
            ALTER TABLE Orders ADD CONSTRAINT FK_Orders_Customers
                FOREIGN KEY (CustomerId) REFERENCES Customers(CustomerId);
            """;

        var result = await ExtractSqlAsync(sql);

        var fkEdge = result.Edges.ShouldContain(e =>
            e.Type == EdgeType.QUERIES &&
            e.Properties != null &&
            (string)e.Properties["relationship"] == "foreign_key");

        fkEdge.SourceQN.ShouldBe("TestProject.Orders");
        fkEdge.TargetQN.ShouldBe("TestProject.Customers");
    }

    // ── Multiple objects in one file ──────────────────────────────

    [Fact]
    public async Task Extracts_MultipleObjects_FromSingleFile()
    {
        var sql = """
            CREATE TABLE Customers (
                CustomerId INT PRIMARY KEY,
                Name NVARCHAR(200)
            );

            CREATE TABLE Orders (
                OrderId INT PRIMARY KEY,
                CustomerId INT REFERENCES Customers(CustomerId)
            );
            GO

            CREATE VIEW vwCustomerOrders AS
            SELECT c.Name, o.OrderId
            FROM Customers c
            JOIN Orders o ON c.CustomerId = o.CustomerId;
            GO

            CREATE PROCEDURE uspGetCustomerOrders @CustomerId INT
            AS
            BEGIN
                SELECT * FROM vwCustomerOrders WHERE CustomerId = @CustomerId;
            END;
            """;

        var result = await ExtractSqlAsync(sql);

        result.Nodes.Count(n => n.Label == NodeLabel.Table).ShouldBe(2);
        result.Nodes.Count(n => n.Label == NodeLabel.View).ShouldBe(1);
        result.Nodes.Count(n => n.Label == NodeLabel.StoredProcedure).ShouldBe(1);
    }

    // ── Temp tables and variables should be skipped ────────────────

    [Fact]
    public async Task Skips_TempTables_InProcedureBody()
    {
        var sql = """
            CREATE PROCEDURE dbo.uspReport
            AS
            BEGIN
                SELECT * INTO #TempOrders FROM Orders;
                SELECT * FROM #TempOrders;
            END;
            """;

        var result = await ExtractSqlAsync(sql);

        // Should have QUERIES edge to Orders but NOT to #TempOrders
        var queryEdges = result.Edges.Where(e => e.Type == EdgeType.QUERIES).ToList();
        queryEdges.ShouldContain(e => e.TargetQN == "TestProject.Orders");
        queryEdges.ShouldNotContain(e => e.TargetQN.Contains("#TempOrders"));
    }

    // ── Schema handling ───────────────────────────────────────────

    [Fact]
    public async Task Handles_SchemaQualifiedNames()
    {
        var sql = """
            CREATE TABLE dbo.Inventory (
                InventoryId INT PRIMARY KEY,
                ProductId INT
            );
            """;

        var result = await ExtractSqlAsync(sql);

        var table = result.Nodes.ShouldContain(n => n.Label == NodeLabel.Table);
        table.Name.ShouldBe("dbo.Inventory");
        table.QualifiedName.ShouldBe("TestProject.dbo.Inventory");
    }

    // ── Graceful handling of parse errors ──────────────────────────

    [Fact]
    public async Task Handles_PartiallyInvalidSql_GracefullyExtractsWhatItCan()
    {
        var sql = """
            CREATE TABLE ValidTable (Id INT PRIMARY KEY);
            THIS IS NOT VALID SQL;
            CREATE TABLE AnotherValidTable (Id INT PRIMARY KEY);
            """;

        var result = await ExtractSqlAsync(sql);

        // Should still extract what it can
        result.Nodes.Count.ShouldBeGreaterThan(0);
    }
}
