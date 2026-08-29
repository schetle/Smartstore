using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Autofac;
using Microsoft.AspNetCore.Http;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Moq;
using NUnit.Framework;
using Smartstore.Caching;
using Smartstore.Collections;
using Smartstore.Core.Data;
using Smartstore.Core.Identity;
using Smartstore.Core.Identity.Rules;
using Smartstore.Core.Rules;
using Smartstore.Core.Rules.Filters;
using Smartstore.Data;
using Smartstore.Data.Providers;
using Smartstore.Scheduling;
using Smartstore.Test.Common;

namespace Smartstore.Core.Tests.Platform.Identity;

/// <summary>
/// Minimal TaskExecutionContext subclass that replaces the heavy SetProgressAsync with a no-op,
/// so tests do not need a real ITaskStore / IAsyncState.
/// </summary>
internal sealed class TestTaskExecutionContext : TaskExecutionContext
{
    public TestTaskExecutionContext(IDictionary<string, string> taskParameters = null)
        : base(
            CreateMockTaskStore(),
            CreateMockAsyncState(),
            new DefaultHttpContext(),
            new ContainerBuilder().Build(),
            CreateExecutionInfo(),
            taskParameters)
    {
    }

    public override Task SetProgressAsync(int? progress, string message)
        => Task.CompletedTask;

    private static ITaskStore CreateMockTaskStore()
    {
        var mock = new Mock<ITaskStore>();
        mock.Setup(x => x.UpdateExecutionInfoAsync(It.IsAny<TaskExecutionInfo>()))
            .Returns(Task.CompletedTask);
        return mock.Object;
    }

    private static Threading.IAsyncState CreateMockAsyncState()
    {
        var mock = new Mock<Threading.IAsyncState>();
        mock.Setup(x => x.GetAsync<TaskDescriptor>(It.IsAny<string>()))
            .ReturnsAsync((TaskDescriptor)null);
        return mock.Object;
    }

    private static TaskExecutionInfo CreateExecutionInfo()
    {
        var descriptor = new TaskDescriptor
        {
            Id = 1,
            Name = "TestTask",
            Type = typeof(TargetGroupEvaluatorTask).AssemblyQualifiedName,
            Enabled = true
        };

        return new TaskExecutionInfo
        {
            Id = 1,
            TaskDescriptorId = 1,
            IsRunning = true,
            MachineName = "test",
            StartedOnUtc = DateTime.UtcNow,
            Task = descriptor
        };
    }
}

/// <summary>
/// DbFactory implementation for SQLite in-memory tests.
/// Uses a shared keep-alive connection so the schema persists across tests.
/// </summary>
internal sealed class SqliteTestDbFactory : DbFactory
{
    private readonly SqliteConnection _connection;

    public SqliteTestDbFactory(SqliteConnection connection)
    {
        _connection = connection;
    }

    public override DbSystemType DbSystem => DbSystemType.Unknown;

    public override System.Data.Common.DbConnectionStringBuilder CreateConnectionStringBuilder(string connectionString)
        => throw new NotImplementedException();

    public override System.Data.Common.DbConnectionStringBuilder CreateConnectionStringBuilder(
        string server, string database, string userName, string password)
        => throw new NotImplementedException();

    public override DataProvider CreateDataProvider(DatabaseFacade database)
        => new TestDataProvider(database);

    public override TContext CreateDbContext<TContext>(string connectionString, int? commandTimeout = null)
        => throw new NotImplementedException();

    public override DbContextOptionsBuilder ConfigureDbContext(DbContextOptionsBuilder builder, string connectionString)
    {
        return builder
            .UseSqlite(_connection)
            .ConfigureWarnings(b =>
            {
                b.Ignore(RelationalEventId.AmbientTransactionWarning);
                b.Ignore(CoreEventId.DetachedLazyLoadingWarning);
            });
    }
}

/// <summary>
/// Functional tests for TargetGroupEvaluatorTask verifying behavioral parity with the
/// legacy SmartStoreNET implementation.
///
/// Extends ServiceTestBase to inherit the Autofac/EngineContext setup required by
/// SmartDbContext, then creates an additional SQLite-backed SmartDbContext (because EF
/// Core InMemory does not support ExecuteDeleteAsync).
/// </summary>
[TestFixture]
public class TargetGroupEvaluatorTaskTests : ServiceTestBase
{
    private const string AclSegmentPattern = "acl:range-*";

    private SqliteConnection _sqliteConnection;
    private SmartDbContext _sqliteDb;
    private Mock<IRuleService> _ruleServiceMock;
    private Mock<ICacheManager> _cacheMock;
    private Mock<ITargetGroupService> _targetGroupServiceMock;
    private Mock<IRuleProviderFactory> _ruleProviderFactoryMock;
    private TargetGroupEvaluatorTask _task;

    [OneTimeSetUp]
    public void SetupTargetGroupTests()
    {
        // Keep a persistent SQLite in-memory connection for the test fixture lifetime.
        _sqliteConnection = new SqliteConnection("DataSource=:memory:");
        _sqliteConnection.Open();

        var factory = new SqliteTestDbFactory(_sqliteConnection);

        // Build SmartDbContext using the explicit factory + connection overload.
        // This requires EngineContext to be initialized, which ServiceTestBase handles.
        var builder = new DbContextOptionsBuilder<SmartDbContext>()
            .UseDbFactory(factory, "DataSource=:memory:", factoryBuilder =>
            {
                factoryBuilder.AddModelAssemblies(new[]
                {
                    typeof(SmartDbContext).Assembly
                });
            });

        _sqliteDb = new SmartDbContext((DbContextOptions<SmartDbContext>)builder.Options);
        _sqliteDb.Database.EnsureCreated();

        // Mock services
        _ruleServiceMock = new Mock<IRuleService>(MockBehavior.Loose);
        _cacheMock = new Mock<ICacheManager>(MockBehavior.Loose);
        _targetGroupServiceMock = new Mock<ITargetGroupService>(MockBehavior.Loose);

        _ruleProviderFactoryMock = new Mock<IRuleProviderFactory>(MockBehavior.Strict);
        _ruleProviderFactoryMock
            .Setup(x => x.GetProvider(RuleScope.Customer, null))
            .Returns(_targetGroupServiceMock.Object);

        _task = new TargetGroupEvaluatorTask(
            _sqliteDb,
            _cacheMock.Object,
            _ruleServiceMock.Object,
            _ruleProviderFactoryMock.Object);
    }

    [OneTimeTearDown]
    public void TeardownTargetGroupTests()
    {
        _sqliteDb?.Dispose();
        _sqliteConnection?.Dispose();
    }

    /// <summary>
    /// Resets per-test state: clears SQLite DB tables in FK-safe order, resets mock call history.
    /// </summary>
    [SetUp]
    public void ResetState()
    {
        // Delete in FK-safe order to avoid constraint violations.
        // 1. CustomerRoleMappings reference CustomerRoles and Customers.
        _sqliteDb.CustomerRoleMappings.RemoveRange(_sqliteDb.CustomerRoleMappings.ToList());
        _sqliteDb.SaveChanges();

        // 2. Detach RuleSet → CustomerRole many-to-many by clearing CustomerRoles collections.
        foreach (var ruleSet in _sqliteDb.RuleSets.Include(x => x.CustomerRoles).ToList())
        {
            ruleSet.CustomerRoles.Clear();
        }
        _sqliteDb.SaveChanges();

        // 3. Now safe to remove rule sets and roles.
        _sqliteDb.RuleSets.RemoveRange(_sqliteDb.RuleSets.ToList());
        _sqliteDb.CustomerRoles.RemoveRange(_sqliteDb.CustomerRoles.ToList());
        _sqliteDb.Customers.RemoveRange(
            _sqliteDb.Customers.Where(c => !c.IsSystemAccount).ToList());
        _sqliteDb.SaveChanges();

        _ruleServiceMock.Reset();
        _cacheMock.Reset();
        _targetGroupServiceMock.Reset();
    }

    // ------------------------------------------------------------------
    // Test 1: Full run — all system mappings deleted, new ones created,
    //         ACL cache invalidated once.
    // ------------------------------------------------------------------
    [Test]
    public async Task FullRun_DeletesOldMappings_CreatesNew_InvalidatesCache()
    {
        // Arrange
        var role1 = SeedRole(id: 101, active: true);
        var role2 = SeedRole(id: 102, active: true);
        SeedRuleSet(id: 201, roleId: role1.Id, isActive: true);
        SeedRuleSet(id: 202, roleId: role2.Id, isActive: true);

        // Customers that the pre-existing mappings reference — must be seeded
        // before the mappings due to FK constraints.
        var existingCustomer1 = SeedCustomer(id: 1001);
        var existingCustomer2 = SeedCustomer(id: 1002);
        var existingCustomer3 = SeedCustomer(id: 1003);
        // Customers the filter will match after the run.
        var customer1 = SeedCustomer(id: 2001);
        var customer2 = SeedCustomer(id: 2002);
        await _sqliteDb.SaveChangesAsync();

        // Pre-existing system mappings (3 rows) that must be deleted.
        SeedSystemMapping(customerId: existingCustomer1.Id, roleId: role1.Id);
        SeedSystemMapping(customerId: existingCustomer2.Id, roleId: role1.Id);
        SeedSystemMapping(customerId: existingCustomer3.Id, roleId: role2.Id);
        await _sqliteDb.SaveChangesAsync();

        var expressionGroup = new FilterExpressionGroup(typeof(Customer));
        _ruleServiceMock
            .Setup(x => x.CreateExpressionGroupAsync(
                It.IsAny<RuleSetEntity>(),
                It.IsAny<IRuleVisitor>(),
                false))
            .ReturnsAsync(expressionGroup);

        // Return matched customers via an EF Core queryable (required for FastPager.ToListAsync).
        var matchedCustomers = _sqliteDb.Customers
            .Where(x => x.Id == customer1.Id || x.Id == customer2.Id)
            .ToPagedList(0, 500);
        _targetGroupServiceMock
            .Setup(x => x.ProcessFilter(
                It.IsAny<FilterExpression[]>(),
                It.IsAny<LogicalRuleOperator>(),
                It.IsAny<int>(),
                It.IsAny<int>()))
            .Returns(matchedCustomers);

        _cacheMock
            .Setup(x => x.RemoveByPatternAsync(AclSegmentPattern))
            .ReturnsAsync(0L);

        var ctx = new TestTaskExecutionContext();

        // Act
        await _task.Run(ctx, CancellationToken.None);

        // Assert — old system mappings gone, 4 new ones created (2 customers × 2 roles).
        var remaining = _sqliteDb.CustomerRoleMappings
            .Where(x => x.IsSystemMapping)
            .ToList();

        Assert.That(remaining, Has.Count.EqualTo(4),
            "Expected 4 new system mappings (2 customers × 2 roles)");
        Assert.That(
            remaining.Any(m => m.CustomerId == customer1.Id && m.CustomerRoleId == role1.Id),
            Is.True, "Customer1 → Role1 mapping missing");
        Assert.That(
            remaining.Any(m => m.CustomerId == customer2.Id && m.CustomerRoleId == role1.Id),
            Is.True, "Customer2 → Role1 mapping missing");
        Assert.That(
            remaining.Any(m => m.CustomerId == customer1.Id && m.CustomerRoleId == role2.Id),
            Is.True, "Customer1 → Role2 mapping missing");
        Assert.That(
            remaining.Any(m => m.CustomerId == customer2.Id && m.CustomerRoleId == role2.Id),
            Is.True, "Customer2 → Role2 mapping missing");

        // ACL cache must be invalidated exactly once.
        _cacheMock.Verify(
            x => x.RemoveByPatternAsync(AclSegmentPattern),
            Times.Once,
            "ACL cache must be invalidated when mappings changed");
    }

    // ------------------------------------------------------------------
    // Test 2: Parameterized run — only the specified role's mappings are
    //         touched; other roles are not affected.
    //
    // This is the core regression test for the role-query scoping fix.
    // Before the fix, CustomerRoleIds was applied to the delete query but
    // NOT to the CustomerRoles load query, causing all roles to be
    // processed regardless of the parameter. After the fix, both are
    // scoped to the specified role IDs.
    // ------------------------------------------------------------------
    [Test]
    public async Task ParameterizedRun_OnlyAffectsSpecifiedRole_QueryScopingFix()
    {
        // Arrange
        var role1 = SeedRole(id: 111, active: true);
        var role2 = SeedRole(id: 112, active: true);
        var role3 = SeedRole(id: 113, active: true);
        SeedRuleSet(id: 211, roleId: role1.Id, isActive: true);
        SeedRuleSet(id: 212, roleId: role2.Id, isActive: true);
        SeedRuleSet(id: 213, roleId: role3.Id, isActive: true);

        // Customers must be seeded before the mappings (FK constraint).
        var existingCust1 = SeedCustomer(id: 3001);
        var existingCust2 = SeedCustomer(id: 3002);
        var existingCust3 = SeedCustomer(id: 3003);
        var matchCustomer = SeedCustomer(id: 4001);
        await _sqliteDb.SaveChangesAsync();

        // Pre-existing system mappings for all 3 roles.
        SeedSystemMapping(customerId: existingCust1.Id, roleId: role1.Id);
        SeedSystemMapping(customerId: existingCust2.Id, roleId: role2.Id);
        SeedSystemMapping(customerId: existingCust3.Id, roleId: role3.Id);
        await _sqliteDb.SaveChangesAsync();

        var expressionGroup = new FilterExpressionGroup(typeof(Customer));
        _ruleServiceMock
            .Setup(x => x.CreateExpressionGroupAsync(
                It.IsAny<RuleSetEntity>(),
                It.IsAny<IRuleVisitor>(),
                false))
            .ReturnsAsync(expressionGroup);

        // Return matched customer via an EF Core queryable.
        var matchedCustomers = _sqliteDb.Customers
            .Where(x => x.Id == matchCustomer.Id)
            .ToPagedList(0, 500);
        _targetGroupServiceMock
            .Setup(x => x.ProcessFilter(
                It.IsAny<FilterExpression[]>(),
                It.IsAny<LogicalRuleOperator>(),
                It.IsAny<int>(),
                It.IsAny<int>()))
            .Returns(matchedCustomers);

        _cacheMock
            .Setup(x => x.RemoveByPatternAsync(AclSegmentPattern))
            .ReturnsAsync(0L);

        // Parameterized run: only role 2.
        var ctx = new TestTaskExecutionContext(new Dictionary<string, string>
        {
            { "CustomerRoleIds", role2.Id.ToString() }
        });

        // Act
        await _task.Run(ctx, CancellationToken.None);

        // Assert — roles 1 and 3 mappings are untouched.
        var role1Mappings = _sqliteDb.CustomerRoleMappings
            .Where(x => x.CustomerRoleId == role1.Id)
            .ToList();
        var role3Mappings = _sqliteDb.CustomerRoleMappings
            .Where(x => x.CustomerRoleId == role3.Id)
            .ToList();

        Assert.That(role1Mappings, Has.Count.EqualTo(1),
            "Role 1 mapping must be untouched — parameterized run must not affect other roles");
        Assert.That(role1Mappings[0].CustomerId, Is.EqualTo(existingCust1.Id));
        Assert.That(role3Mappings, Has.Count.EqualTo(1),
            "Role 3 mapping must be untouched — parameterized run must not affect other roles");
        Assert.That(role3Mappings[0].CustomerId, Is.EqualTo(existingCust3.Id));

        // Role 2: old mapping deleted, new mapping for matched customer created.
        var role2Mappings = _sqliteDb.CustomerRoleMappings
            .Where(x => x.CustomerRoleId == role2.Id && x.IsSystemMapping)
            .ToList();

        Assert.That(role2Mappings, Has.Count.EqualTo(1),
            "Role 2 must have exactly 1 new system mapping after parameterized run");
        Assert.That(role2Mappings[0].CustomerId, Is.EqualTo(matchCustomer.Id));

        // Cache invalidated because role2's mappings changed.
        _cacheMock.Verify(
            x => x.RemoveByPatternAsync(AclSegmentPattern),
            Times.Once,
            "ACL cache must be invalidated when any mappings changed");
    }

    // ------------------------------------------------------------------
    // Test 3: Cooperative cancellation — with a pre-cancelled token,
    //         no mappings are inserted.
    // ------------------------------------------------------------------
    [Test]
    public async Task CancelledToken_NoMappingsInserted()
    {
        // Arrange
        var role = SeedRole(id: 121, active: true);
        SeedRuleSet(id: 221, roleId: role.Id, isActive: true);
        await _sqliteDb.SaveChangesAsync();

        var expressionGroup = new FilterExpressionGroup(typeof(Customer));
        _ruleServiceMock
            .Setup(x => x.CreateExpressionGroupAsync(
                It.IsAny<RuleSetEntity>(),
                It.IsAny<IRuleVisitor>(),
                false))
            .ReturnsAsync(expressionGroup);

        var ctx = new TestTaskExecutionContext();

        using var cts = new CancellationTokenSource();
        cts.Cancel();

        // Act — may throw OperationCanceledException, but no mappings must be inserted.
        try
        {
            await _task.Run(ctx, cts.Token);
        }
        catch (OperationCanceledException)
        {
            // Expected when ExecuteDeleteAsync or other async ops honour the cancelled token.
        }

        // Assert — no system mappings were inserted.
        var insertedCount = _sqliteDb.CustomerRoleMappings.Count(x => x.IsSystemMapping);
        Assert.That(insertedCount, Is.EqualTo(0),
            "No mappings must be inserted when token is pre-cancelled");
    }

    // ------------------------------------------------------------------
    // Test 4: No-op — roles have no active rule sets.
    //         Zero inserts; ACL cache NOT invalidated.
    // ------------------------------------------------------------------
    [Test]
    public async Task NoActiveRuleSets_ZeroInserts_CacheNotInvalidated()
    {
        // Arrange — active role but with no active rule sets.
        var role = SeedRole(id: 131, active: true);
        SeedRuleSet(id: 231, roleId: role.Id, isActive: false);
        await _sqliteDb.SaveChangesAsync();

        var ctx = new TestTaskExecutionContext();

        // Act
        await _task.Run(ctx, CancellationToken.None);

        // Assert
        var insertedCount = _sqliteDb.CustomerRoleMappings.Count(x => x.IsSystemMapping);
        Assert.That(insertedCount, Is.EqualTo(0),
            "No mappings expected when no active rule sets exist");

        _cacheMock.Verify(
            x => x.RemoveByPatternAsync(It.IsAny<string>()),
            Times.Never,
            "ACL cache must NOT be invalidated when nothing changed");
    }

    // ------------------------------------------------------------------
    // Test 5: No-op — filter matches zero customers.
    //         numAdded == 0 && numDeleted == 0 → cache NOT invalidated.
    // ------------------------------------------------------------------
    [Test]
    public async Task FilterMatchesNobody_CacheNotInvalidated()
    {
        // Arrange
        var role = SeedRole(id: 141, active: true);
        SeedRuleSet(id: 241, roleId: role.Id, isActive: true);
        await _sqliteDb.SaveChangesAsync();

        var expressionGroup = new FilterExpressionGroup(typeof(Customer));
        _ruleServiceMock
            .Setup(x => x.CreateExpressionGroupAsync(
                It.IsAny<RuleSetEntity>(),
                It.IsAny<IRuleVisitor>(),
                false))
            .ReturnsAsync(expressionGroup);

        // Filter returns an empty result backed by a real EF Core queryable
        // (ToListAsync inside FastPager requires an IAsyncQueryProvider, which
        //  plain IEnumerable.AsQueryable() does not provide).
        var emptyList = _sqliteDb.Customers
            .Where(x => false)
            .ToPagedList(0, 500);
        _targetGroupServiceMock
            .Setup(x => x.ProcessFilter(
                It.IsAny<FilterExpression[]>(),
                It.IsAny<LogicalRuleOperator>(),
                It.IsAny<int>(),
                It.IsAny<int>()))
            .Returns(emptyList);

        var ctx = new TestTaskExecutionContext();

        // Act
        await _task.Run(ctx, CancellationToken.None);

        // Assert — nothing inserted.
        Assert.That(
            _sqliteDb.CustomerRoleMappings.Count(x => x.IsSystemMapping),
            Is.EqualTo(0));

        // ACL cache must NOT be invalidated because numAdded == 0 && numDeleted == 0.
        _cacheMock.Verify(
            x => x.RemoveByPatternAsync(It.IsAny<string>()),
            Times.Never,
            "ACL cache must NOT be invalidated when nothing changed");
    }

    // ------------------------------------------------------------------
    // Helpers
    // ------------------------------------------------------------------

    private CustomerRole SeedRole(int id, bool active)
    {
        var role = new CustomerRole
        {
            Id = id,
            Active = active,
            Name = $"TestRole_{id}",
            SystemName = $"testrole_{id}"
        };
        _sqliteDb.CustomerRoles.Add(role);
        return role;
    }

    private RuleSetEntity SeedRuleSet(int id, int roleId, bool isActive)
    {
        var ruleSet = new RuleSetEntity
        {
            Id = id,
            IsActive = isActive,
            Scope = RuleScope.Customer,
            LogicalOperator = LogicalRuleOperator.And,
            CreatedOnUtc = DateTime.UtcNow,
            UpdatedOnUtc = DateTime.UtcNow
        };
        var role = _sqliteDb.CustomerRoles.Find(roleId);
        ruleSet.CustomerRoles.Add(role);
        _sqliteDb.RuleSets.Add(ruleSet);
        return ruleSet;
    }

    private CustomerRoleMapping SeedSystemMapping(int customerId, int roleId)
    {
        var mapping = new CustomerRoleMapping
        {
            CustomerId = customerId,
            CustomerRoleId = roleId,
            IsSystemMapping = true
        };
        _sqliteDb.CustomerRoleMappings.Add(mapping);
        return mapping;
    }

    private Customer SeedCustomer(int id)
    {
        var customer = new Customer
        {
            Id = id,
            IsSystemAccount = false,
            Active = true,
            CreatedOnUtc = DateTime.UtcNow,
            CustomerGuid = Guid.NewGuid()
        };
        _sqliteDb.Customers.Add(customer);
        return customer;
    }
}
