using System.Reflection;
using InnoPro.Mcp.Server;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using ModelContextProtocol.Protocol;

var builder = Host.CreateApplicationBuilder(args);

// stdout là kênh MCP, mọi log phải ra stderr.
builder.Logging.AddConsole(o => o.LogToStandardErrorThreshold = LogLevel.Trace);

var tools = new RevitTools(new RevitBridgeClient());

builder.Services
    .AddMcpServer(options =>
    {
        options.ServerInfo = new Implementation
        {
            Name = "vn-revit-mcp",
            Version = typeof(RevitTools).Assembly
                .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion.Split('+')[0] ?? "0.0.0",
        };
        options.ServerInstructions =
            "Điều khiển Autodesk Revit (2025+) qua add-in InnoPro. Chiều dài dùng milimét. " +
            "Nếu lệnh báo lỗi kết nối, gọi revit_status và hướng dẫn người dùng mở Revit, bật nút MCP trên tab InnoPro.";
    })
    .WithStdioServerTransport()
    .WithListToolsHandler((request, token) => new ValueTask<ListToolsResult>(tools.ListAsync(token)))
    .WithCallToolHandler((request, token) =>
        new ValueTask<CallToolResult>(tools.CallAsync(request.Params!.Name, request.Params.Arguments, token)));

await builder.Build().RunAsync();
