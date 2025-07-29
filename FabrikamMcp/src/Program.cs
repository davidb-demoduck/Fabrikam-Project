using FabrikamMcp.Tools;
using FabrikamMcp.Services;
using ModelContextProtocol;
using ModelContextProtocol.Server;
using System.Text.Json;

var builder = WebApplication.CreateBuilder(args);

// Add HttpClient for API calls with extended timeout for long-lived sessions
builder.Services.AddHttpClient();

// Configure ASP.NET Core session services for improved session management
builder.Services.AddSession(options =>
{
    options.IdleTimeout = TimeSpan.FromMinutes(60); // Extend session timeout to 60 minutes
    options.Cookie.HttpOnly = true;
    options.Cookie.IsEssential = true;
    options.Cookie.SameSite = SameSiteMode.None; // Allow cross-site usage for Copilot Studio
    options.Cookie.SecurePolicy = CookieSecurePolicy.SameAsRequest;
});

// Add MCP server services with HTTP transport and Fabrikam business tools
builder.Services.AddMcpServer()
    .WithHttpTransport()
    .WithTools<FabrikamSalesTools>()
    .WithTools<FabrikamInventoryTools>()
    .WithTools<FabrikamCustomerServiceTools>()
    .WithTools<FabrikamProductTools>()
    .WithTools<FabrikamBusinessIntelligenceTools>();

// Add CORS for HTTP transport support in browsers with credentials support
builder.Services.AddCors(options =>
{
    options.AddDefaultPolicy(policy =>
    {
        policy.SetIsOriginAllowed(_ => true) // Allow any origin for development
              .AllowAnyHeader()
              .AllowAnyMethod()
              .AllowCredentials(); // Enable credentials for session cookies
    });
});

// Add memory cache for session management
builder.Services.AddMemoryCache();

// Add distributed memory cache as session store
builder.Services.AddDistributedMemoryCache();

// Register MCP Session Manager as singleton
builder.Services.AddSingleton<IMcpSessionManager, McpSessionManager>();

// Register MCP Error Handler for better session error messages
builder.Services.AddScoped<IMcpErrorHandler, McpErrorHandler>();

// Configure Kestrel for long-lived connections and better session handling
builder.WebHost.ConfigureKestrel(options =>
{
    options.Limits.KeepAliveTimeout = TimeSpan.FromMinutes(10); // Extended keep-alive
    options.Limits.RequestHeadersTimeout = TimeSpan.FromMinutes(5); // Extended request timeout
    options.Limits.MaxRequestBodySize = 10 * 1024 * 1024; // 10MB max request size
});

var app = builder.Build();

// Configure the HTTP request pipeline
if (app.Environment.IsDevelopment())
{
    app.UseDeveloperExceptionPage();
}

// Enable CORS
app.UseCors();

// Enable session middleware for session management
app.UseSession();

// Add request logging for session debugging with MCP session tracking
app.Use(async (context, next) =>
{
    var logger = context.RequestServices.GetRequiredService<ILogger<Program>>();
    var sessionManager = context.RequestServices.GetRequiredService<IMcpSessionManager>();
    var sessionId = context.Session.Id;
    var timestamp = DateTime.UtcNow;
    
    logger.LogInformation("MCP Request: {Method} {Path} | Session: {SessionId} | Time: {Timestamp}", 
        context.Request.Method, context.Request.Path, sessionId, timestamp);
    
    // Track MCP session activity
    if (context.Request.Path.StartsWithSegments("/mcp"))
    {
        var userAgent = context.Request.Headers.UserAgent.ToString();
        sessionManager.RegisterSession(sessionId, userAgent);
    }
    else
    {
        sessionManager.UpdateSessionActivity(sessionId);
    }
    
    await next();
    
    logger.LogInformation("MCP Response: {StatusCode} | Session: {SessionId} | Duration: {Duration}ms", 
        context.Response.StatusCode, sessionId, (DateTime.UtcNow - timestamp).TotalMilliseconds);
});

// Add MCP session validation middleware
app.Use(async (context, next) =>
{
    // Only apply to MCP endpoints
    if (context.Request.Path.StartsWithSegments("/mcp") && context.Request.Method == "POST")
    {
        var sessionManager = context.RequestServices.GetRequiredService<IMcpSessionManager>();
        var errorHandler = context.RequestServices.GetRequiredService<IMcpErrorHandler>();
        var logger = context.RequestServices.GetRequiredService<ILogger<Program>>();
        var sessionId = context.Session.Id;

        // Check if this is an initialize request (allowed for new sessions)
        context.Request.EnableBuffering();
        var body = await new StreamReader(context.Request.Body).ReadToEndAsync();
        context.Request.Body.Position = 0;

        bool isInitializeRequest = body.Contains("\"method\":\"initialize\"");
        
        // Validate session for non-initialize requests
        if (!isInitializeRequest && !sessionManager.IsSessionValid(sessionId))
        {
            var sessionInfo = sessionManager.GetSessionInfo(sessionId);
            object errorResponse;
            
            if (sessionInfo != null)
            {
                // Session exists but expired
                errorResponse = errorHandler.CreateSessionExpiredError(sessionId, sessionInfo.LastActivity);
                logger.LogWarning("Rejecting request for expired session: {SessionId}", sessionId);
            }
            else
            {
                // Session not found
                errorResponse = errorHandler.CreateSessionNotFoundError(sessionId);
                logger.LogWarning("Rejecting request for unknown session: {SessionId}", sessionId);
            }

            context.Response.StatusCode = 404;
            context.Response.ContentType = "application/json";
            await context.Response.WriteAsync(JsonSerializer.Serialize(errorResponse));
            return;
        }
    }

    await next();
});

// Map MCP endpoints to the standard /mcp path
app.MapMcp("/mcp");

// Add status and info endpoints with session health monitoring
app.MapGet("/status", (HttpContext context, IMcpSessionManager sessionManager) =>
{
    var sessionId = context.Session.Id;
    var sessionKeys = new List<string>();
    
    // Try to access session to ensure it's available
    try
    {
        context.Session.SetString("healthcheck", DateTime.UtcNow.ToString());
        var healthCheck = context.Session.GetString("healthcheck");
        sessionKeys.Add("healthcheck");
    }
    catch (Exception ex)
    {
        // Log session access issues
        var logger = context.RequestServices.GetRequiredService<ILogger<Program>>();
        logger.LogWarning("Session access issue: {Error}", ex.Message);
    }

    var mcpSession = sessionManager.GetSessionInfo(sessionId);
    var allSessions = sessionManager.GetAllSessions();

    return new
    {
        Status = "Ready",
        Service = "Fabrikam MCP Server",
        Version = "1.0.0",
        Description = "Model Context Protocol server for Fabrikam Modular Homes business operations",
        Transport = "HTTP",
        SessionManagement = new
        {
            SessionId = sessionId,
            SessionTimeout = "60 minutes",
            KeepAliveTimeout = "10 minutes",
            SessionKeys = sessionKeys,
            SessionEnabled = context.Session != null,
            McpSessionInfo = mcpSession,
            TotalActiveSessions = allSessions.Count(kvp => kvp.Value.IsActive)
        },
        BusinessModules = new[]
        {
            "Sales - Order management and customer analytics",
            "Inventory - Product catalog and stock monitoring", 
            "Customer Service - Support ticket management and resolution",
            "Products - Product catalog, inventory analytics and management",
            "Business Intelligence - Executive dashboards and performance alerts"
        },
        Timestamp = DateTime.UtcNow,
        Environment = app.Environment.EnvironmentName
    };
});

// Add session health endpoint for debugging
app.MapGet("/session-health", (HttpContext context, IMcpSessionManager sessionManager) =>
{
    var sessionId = context.Session.Id;
    var sessionData = new Dictionary<string, object>();
    
    try
    {
        // Set and get test data
        var testKey = "last-access";
        var testValue = DateTime.UtcNow.ToString("O");
        context.Session.SetString(testKey, testValue);
        
        var retrievedValue = context.Session.GetString(testKey);
        
        sessionData["testKey"] = testKey;
        sessionData["testValue"] = testValue;
        sessionData["retrievedValue"] = retrievedValue;
        sessionData["sessionWorking"] = testValue == retrievedValue;
        
        // Get available session keys (this is limited in ASP.NET Core)
        sessionData["sessionId"] = sessionId;
        sessionData["isAvailable"] = context.Session.IsAvailable;
        
        // Get MCP session info
        var mcpSession = sessionManager.GetSessionInfo(sessionId);
        sessionData["mcpSessionInfo"] = mcpSession;
        sessionData["mcpSessionValid"] = sessionManager.IsSessionValid(sessionId);
        
    }
    catch (Exception ex)
    {
        sessionData["error"] = ex.Message;
        sessionData["sessionWorking"] = false;
    }

    return new
    {
        Status = "Session Health Check",
        Timestamp = DateTime.UtcNow,
        SessionData = sessionData,
        Configuration = new
        {
            SessionTimeout = "60 minutes",
            KeepAlive = "10 minutes",
            CookiePolicy = "SameSite=None, Secure=SameAsRequest"
        }
    };
});

// Add sessions management endpoint for monitoring
app.MapGet("/sessions", (IMcpSessionManager sessionManager) =>
{
    var allSessions = sessionManager.GetAllSessions();
    var activeSessions = allSessions.Where(kvp => kvp.Value.IsActive).ToDictionary(kvp => kvp.Key, kvp => kvp.Value);
    
    return new
    {
        Status = "Session Management Overview",
        Timestamp = DateTime.UtcNow,
        Summary = new
        {
            TotalSessions = allSessions.Count,
            ActiveSessions = activeSessions.Count,
            InactiveSessions = allSessions.Count - activeSessions.Count
        },
        ActiveSessions = activeSessions.Take(10), // Limit to 10 for response size
        Configuration = new
        {
            SessionTimeout = "60 minutes",
            CleanupInterval = "5 minutes",
            KeepAliveTimeout = "10 minutes"
        }
    };
});

// Redirect root path to status for convenience
app.MapGet("/", () => Results.Redirect("/status"));

app.Run();
