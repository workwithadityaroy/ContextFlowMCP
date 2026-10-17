using ContextFlow.Gateway.Tools;
using ContextFlow.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;
using ModelContextProtocol.AspNetCore;

var builder = WebApplication.CreateBuilder(args);

// Database contexts: App role for governance, SELECT-only for business tables queried by tools
var governanceConnection = builder.Configuration.GetConnectionString("ContextFlowDb")
    ?? throw new InvalidOperationException("Connection string 'ContextFlowDb' was not found in configuration.");

var readOnlyConnection = builder.Configuration.GetConnectionString("ToolsReadOnlyDb")
    ?? throw new InvalidOperationException("Connection string 'ToolsReadOnlyDb' was not found in configuration.");

builder.Services.AddDbContext<ContextFlowDbContext>(options =>
    options.UseNpgsql(governanceConnection));

builder.Services.AddDbContext<ToolsReadOnlyDbContext>(options =>
    options.UseNpgsql(readOnlyConnection));

builder.Services.AddMcpServer()
    .WithHttpTransport()
    .WithToolsFromAssembly();

var app = builder.Build();

app.MapMcp("/mcp");

app.Run();

public partial class Program;
