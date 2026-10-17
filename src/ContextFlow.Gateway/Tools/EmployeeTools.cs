using System.ComponentModel;
using System.Text.Json;
using ContextFlow.Infrastructure.Data;
using Microsoft.AspNetCore.Authorization;
using Microsoft.EntityFrameworkCore;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

namespace ContextFlow.Gateway.Tools;

[McpServerToolType]
public sealed class EmployeeTools
{
    private static readonly JsonSerializerOptions _jsonOptions = new()
    {
        WriteIndented = true
    };

    [McpServerTool(ReadOnly = true)]
    [Authorize(Roles = "Admin,Finance")]
    [Description("Looks up a synthetic employee profile by EmployeeCode. Returns profile details. Available only to Admin and Finance roles.")]
    public static async Task<CallToolResult> GetEmployeeProfile(
        ToolsReadOnlyDbContext readOnlyDb,
        [Description("The unique employee code, e.g. EMP001")] string employeeCode,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(employeeCode))
        {
            return new CallToolResult
            {
                IsError = true,
                Content = [new TextContentBlock { Text = "EmployeeCode must not be empty." }]
            };
        }

        var normalizedCode = employeeCode.Trim().ToUpperInvariant();
        var employee = await readOnlyDb.Employees
            .AsNoTracking()
            .FirstOrDefaultAsync(e => e.EmployeeCode == normalizedCode, cancellationToken);

        if (employee is null)
        {
            return new CallToolResult
            {
                IsError = true,
                Content = [new TextContentBlock { Text = $"Employee with code '{normalizedCode}' was not found." }]
            };
        }

        var json = JsonSerializer.Serialize(new
        {
            employee.EmployeeCode,
            employee.FullName,
            employee.Email,
            employee.Department,
            employee.JobTitle,
            employee.SyntheticSalary,
            employee.HireDate,
            ManagerCode = (string?)null,
            TerminationDate = (DateOnly?)null,
            Notes = (string?)null
        }, _jsonOptions);

        return new CallToolResult
        {
            Content = [new TextContentBlock { Text = json }]
        };
    }
}

