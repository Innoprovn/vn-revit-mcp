using System.Reflection;
using InnoPro.Mcp.Server;
using InnoPro.Mcp.Server.Setup;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using ModelContextProtocol;
using ModelContextProtocol.Protocol;

// "vn-revit-mcp setup|unsetup|clients": cấu hình các AI client trên máy, không chạy MCP server.
if (SetupCli.IsSetupCommand(args)) { Environment.ExitCode = SetupCli.Run(args); return; }

// Chạy từ gói .mcpb: tự cài/cập nhật add-in Revit mang theo trong gói (chạy nền).
args = BundleInstaller.TakeBundleArg(args, out var bundleDir);
if (bundleDir != null) BundleInstaller.EnsureInBackground(bundleDir);

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
        // Báo client biết danh sách tool có thể đổi (sau reload_commands).
        options.Capabilities = new ServerCapabilities
        {
            Tools = new ToolsCapability { ListChanged = true },
            Prompts = new PromptsCapability(),
        };
        options.ServerInstructions =
            "Điều khiển Autodesk Revit (2025+) qua add-in InnoPro. Chiều dài dùng milimét. " +
            "Ngoài các tool đang hiện, còn nhiều lệnh khác: tra bằng find_commands rồi chạy bằng run_command; " +
            "chỉ dùng run_csharp khi thật sự không có lệnh phù hợp. " +
            "Tiết kiệm: find_elements output=ids rồi truyền id sang lệnh khác, không đọc từng phần tử. " +
            "Kết quả dạng {columns, rows} là bảng. " +
            "Nếu lệnh báo lỗi kết nối, gọi revit_status và hướng dẫn người dùng mở Revit, bật nút MCP trên tab InnoPro.";
    })
    .WithStdioServerTransport()
    .WithListToolsHandler((request, token) => new ValueTask<ListToolsResult>(tools.ListAsync(token)))
    .WithCallToolHandler((request, token) =>
        new ValueTask<CallToolResult>(tools.CallAsync(request.Server, request.Params!.Name, request.Params.Arguments, token)))
    .WithListPromptsHandler((request, token) =>
        ValueTask.FromResult(new ListPromptsResult { Prompts = Recipes.Load().Select(Recipes.ToPrompt).ToList() }))
    .WithGetPromptHandler((request, token) =>
    {
        var recipe = Recipes.Load().FirstOrDefault(r => r.Name == request.Params!.Name)
                     ?? throw new McpException($"Không có công thức '{request.Params!.Name}'.");
        var arguments = request.Params.Arguments?.ToDictionary(a => a.Key, a => a.Value.ToString());
        return ValueTask.FromResult(Recipes.Render(recipe, arguments));
    });

await builder.Build().RunAsync();
