namespace ContextFlow.Core.Entities;

/// <summary>
/// Synthetic employee profile for testing business data lookup tools.
/// Contains no real PII.
/// </summary>
public sealed class Employee
{
    public int Id { get; set; }

    public required string EmployeeCode { get; set; }

    public required string FullName { get; set; }

    public required string Email { get; set; }

    public required string Department { get; set; }

    public required string JobTitle { get; set; }

    public decimal SyntheticSalary { get; set; }

    public DateOnly HireDate { get; set; }
}

