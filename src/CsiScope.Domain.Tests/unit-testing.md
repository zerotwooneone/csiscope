# DIRECTIVE: SYSTEM STANDARDS FOR ROBUST UNIT TESTING

**Context:** This document defines the operational standards for generating unit tests. The primary objective is to minimize "brittleness"—the tendency of a test to fail when internal implementation details change, even if the external behavior remains correct.

**Core Principle:** Test public behavior, not internal implementation.

---

## 1. The TDD Algorithm: Red, Green, Refactor

You must adhere to the **Red-Green-Refactor** cycle when generating code from scratch or implementing new features.



[Image of red green refactor TDD cycle]


### Phase 1: RED (The Failing State)
* **Action:** Write the test *before* the implementation logic.
* **Constraint:** The test must assert a requirement that does not yet exist.
* **Validation:** Run the test. It **must** fail. (A compilation error counts as a failure in typed languages, but a logic failure is preferred).
* **Purpose:** Ensures the test actually detects the absence of the feature.

### Phase 2: GREEN (The Passing State)
* **Action:** Write the implementation code.
* **Constraint:** Write *only* the minimum amount of code required to make the test pass. Do not implement optimization or extra features yet.
* **Validation:** Run the test. It **must** pass.

### Phase 3: REFACTOR (The Cleanup)
* **Action:** Improve the code structure, readability, and performance.
* **Constraint:** Do not change the external behavior.
* **Validation:** Run the test again. It **must** still pass.
* **Critical:** This applies to test code as well. Refactor tests to be cleaner and drier, provided they stay readable.

---

## 2. Anatomy of a Non-Brittle Test

A "brittle" test is one that requires maintenance whenever the underlying code is refactored. To avoid this, follow the **AAA Pattern** and the **Black Box Rule**.

### The Structure: AAA (Arrange, Act, Assert)
Every test must visually separate these three steps.

1.  **Arrange:** Setup the inputs, mocks, and the system under test (SUT).
2.  **Act:** Execute the specific method or behavior being tested.
3.  **Assert:** Verify the result (state change or return value).

### The Black Box Rule
Treat the System Under Test (SUT) as a black box.
* **DO** assert that the output matches the input.
* **DO** assert that side effects (e.g., database saves, events published) occurred via public interfaces.
* **DO NOT** use reflection to check private fields.
* **DO NOT** assert that internal helper methods were called.
* **DO NOT** assert the order of execution inside the black box, unless order is a business requirement.

---

## 3. Mocking Constraints

Over-mocking is the leading cause of brittleness.

| Scenario | Instruction | Reason |
| :--- | :--- | :--- |
| **External Dependencies** (DB, API, File System) | **MOCK IT** | Tests must be fast and deterministic. |
| **Value Objects / Data Models** | **USE REAL OBJECTS** | Mocking simple data structures adds noise and hides serialization issues. |
| **Internal Private Methods** | **NEVER MOCK** | These are implementation details. Test them via the public method that calls them. |
| **Strict Interaction Checks** | **AVOID** | Avoid `Verify(x => x.Method(), Times.Once)`. Only verify interactions if the *side effect* is the primary goal (e.g., sending an email). |

---

## 4. Code Examples: Brittle vs. Robust

### ❌ BAD: Brittle Test Implementation
*Why it fails:* It is tied to the specific implementation steps. If the developer changes the internal logic (e.g., uses a different loop or helper method) but gets the same result, this test will break.

```csharp
// Scenario: Calculating a cart total
[Test]
public void CalculateTotal_Implementation_Check()
{
    // ARRANGE
    var calculator = new CartCalculator();
    var items = new List<Item> { new Item(10), new Item(5) };

    // ACT
    var result = calculator.Calculate(items);

    // ASSERT
    // BRITTLE: Checking a private field via reflection
    var internalSum = GetPrivateField(calculator, "_currentSum");
    Assert.AreEqual(15, internalSum);

    // BRITTLE: Verifying an internal helper was called exactly once
    // If we optimize to not use the helper, the test fails falsely.
    Mock.Get(calculator).Verify(x => x.RunInternalMathLoop(), Times.Once);
}
```

### ✅ GOOD: Robust Test Implementation
*Why it works:* It focuses purely on inputs and outputs. The developer can completely rewrite the internal math logic, and as long as $10 + $5 = $15, this test passes.

```csharp
// Scenario: Calculating a cart total
[Test]
public void Calculate_GivenMultipleItems_ReturnsSumOfPrices()
{
    // ARRANGE
    var calculator = new CartCalculator();
    var items = new List<Item> { new Item(10), new Item(5) };

    // ACT
    var result = calculator.Calculate(items);

    // ASSERT
    // ROBUST: Only checking the return value (Public Contract)
    Assert.AreEqual(15, result);
}
```

---

## 4. The Determinism Rule (Time & Randomness)

Tests must be 100% deterministic. They should never fail due to environmental factors like execution speed, CPU load, timezone differences, or the current clock time.

*   **NEVER** use `DateTime.UtcNow`, `DateTime.Now`, `DateTimeOffset.UtcNow`, or `new Random()` inside your tests.
*   **ALWAYS** use hard-coded, arbitrary values (e.g., `new DateTimeOffset(2025, 1, 1, 12, 0, 0, TimeSpan.Zero)`) for time inputs and setups.
*   When testing services that require the current time, ensure the System Under Test (SUT) relies on an injected time abstraction (e.g., `ITimeProvider`, .NET's `TimeProvider`) rather than calling `DateTimeOffset.UtcNow` directly. In your tests, you can then inject a mocked or fake time provider that returns your hard-coded arbitrary time.

---

## 5. Test Data Management

Complex domain objects can make tests noisy and hard to read. Use test builders or factories to create test data.

### ✅ GOOD: Using Test Builders
```csharp
// ARRANGE
var evaluation = CredentialEvaluationBuilder
    .WithDefaults()
    .WithProviderId(providerId)
    .WithStatus(EvaluationStatus.Approved)
    .Build();
```

### ❌ BAD: Manual Object Construction
```csharp
// ARRANGE
var providerId = ProviderId.New();
var publicId = CredentialEvaluationPublicId.New();
var licenseNumber = LicenseNumber.Create("LICENSE123456").Value!;
var medicalBoard = MedicalBoard.Create("California").Value!;
var licenseExpiryDate = LicenseExpiryDate.Create(DateTimeOffset.UtcNow.AddYears(2), timeProvider).Value!;
var evaluation = CredentialEvaluation.Create(providerId, publicId, licenseNumber, medicalBoard, licenseExpiryDate);
```

---

## 6. Integration vs Unit Test Boundaries

Clear distinction between unit and integration tests prevents confusion and ensures appropriate test strategies.

| Test Type | Purpose | Dependencies | When to Use |
| :--- | :--- | :--- | :--- |
| **Unit Test** | Test single class/method in isolation | Mocked dependencies, in-memory fakes | Testing business logic, domain rules, mappings |
| **Integration Test** | Test interaction between components | Real database, external services (or test doubles) | Testing repository patterns, API endpoints, workflows |

### Guidelines:
- **EF Core InMemory**: Acceptable for repository unit tests (still unit tests, just with in-memory database)
- **WebApplicationFactory**: Integration tests only (full ASP.NET Core pipeline)
- **Temporal Test Server**: Integration tests only (workflow orchestration requires Temporal runtime)

---

## 7. Async Testing Guidelines

When testing async methods, follow these patterns to avoid deadlocks and ensure reliability.

### ✅ GOOD: Proper Async Testing
```csharp
[Fact]
public async Task SaveAsync_WithValidEntity_SavesToDatabase()
{
    // ARRANGE
    var repository = new Repository(mockDbContext.Object);
    var entity = new Entity();

    // ACT
    await repository.SaveAsync(entity);

    // ASSERT
    mockDbContext.Verify(x => x.SaveChangesAsync(It.IsAny<CancellationToken>()), Times.Once);
}
```

### ❌ BAD: Blocking Async Calls
```csharp
[Fact]
public void SaveAsync_WithValidEntity_SavesToDatabase()
{
    // ACT - DEADLOCK RISK
    repository.SaveAsync(entity).Wait(); // Never use .Wait() or .Result
    
    // ACT - DEADLOCK RISK
    var result = repository.SaveAsync(entity).Result; // Never use .Result
}
```

### Mocking Async Methods:
```csharp
// For methods that return Task
mockRepository.Setup(x => x.GetByIdAsync(It.IsAny<Guid>()))
    .ReturnsAsync((Guid id) => Task.FromResult(expectedEntity));

// For methods that return Task with no value
mockRepository.Setup(x => x.SaveAsync(It.IsAny<Entity>()))
    .Returns(Task.CompletedTask);
```

---

## 8. Test Organization

Organize tests to maintain clarity and make them easy to navigate.

### Structure Guidelines:
- **One test class per class under test**: `CredentialEvaluationRepositoryTests` for `CredentialEvaluationRepository`
- **Group related tests**: Use `#region` or nested classes for related functionality
- **Descriptive test names**: Follow the pattern `MethodName_WhenCondition_ExpectedResult`

### Example Organization:
```csharp
public class CredentialEvaluationRepositoryTests
{
    #region SaveAsync Tests
    [Fact]
    public async Task SaveAsync_NewEntity_SavesToDatabase() { }
    
    [Fact]
    public async Task SaveAsync_ExistingEntity_UpdatesDatabase() { }
    #endregion
    
    #region GetByIdAsync Tests
    [Fact]
    public async Task GetByIdAsync_ExistingEntity_ReturnsEntity() { }
    
    [Fact]
    public async Task GetByIdAsync_NonExistentEntity_ReturnsNull() { }
    #endregion
}
```

---

## 9. Summary Checklist for AI Generation

Before finalizing code generation, verify:
1.  Does the test name describe the **behavior**, not the method name? (e.g., `UserIsAdmitted_WhenAgeIsOver18` vs `TestAdmitUser`).
2.  Is the test independent? (It does not rely on the state left by a previous test).
3.  Are we testing the **what** (result), not the **how** (implementation)?
4.  Is the Code Coverage focused on logic branches, not just line execution?
5.  Does the test clean up after itself? (IDisposable, database cleanup, mock resets).
6.  Are assertions focused on business value, not technical details? (e.g., "user is approved" vs "status field equals 1").
