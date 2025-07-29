using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace FabrikamMcp.Services;

/// <summary>
/// Enhanced error response service for MCP session management
/// Provides better error messages and recovery guidance for session issues
/// </summary>
public interface IMcpErrorHandler
{
    object CreateSessionNotFoundError(string? sessionId, string? suggestion = null);
    object CreateSessionExpiredError(string sessionId, DateTime lastActivity);
    object CreateErrorResponse(int code, string message, string? sessionId = null, object? data = null);
}

public class McpErrorHandler : IMcpErrorHandler
{
    private readonly ILogger<McpErrorHandler> _logger;

    public McpErrorHandler(ILogger<McpErrorHandler> logger)
    {
        _logger = logger;
    }

    public object CreateSessionNotFoundError(string? sessionId, string? suggestion = null)
    {
        var message = "Session not found";
        if (!string.IsNullOrEmpty(sessionId))
        {
            message += $" (ID: {sessionId})";
        }

        var defaultSuggestion = "Please refresh your connection or restart the chat session to establish a new MCP session.";
        
        _logger.LogWarning("Session not found error: {SessionId}", sessionId);

        return new
        {
            jsonrpc = "2.0",
            id = "",
            error = new
            {
                code = -32001,
                message = message,
                data = new
                {
                    type = "session_not_found",
                    sessionId = sessionId,
                    suggestion = suggestion ?? defaultSuggestion,
                    timestamp = DateTime.UtcNow,
                    recoveryInstructions = new[]
                    {
                        "Start a new chat session",
                        "Refresh your browser/client connection",
                        "Check network connectivity",
                        "Verify MCP server is running"
                    }
                }
            }
        };
    }

    public object CreateSessionExpiredError(string sessionId, DateTime lastActivity)
    {
        var timespan = DateTime.UtcNow - lastActivity;
        var minutes = Math.Round(timespan.TotalMinutes, 1);
        
        _logger.LogInformation("Session expired: {SessionId} | Inactive for {Minutes} minutes", sessionId, minutes);

        return new
        {
            jsonrpc = "2.0",
            id = "",
            error = new
            {
                code = -32002,
                message = $"Session expired after {minutes} minutes of inactivity",
                data = new
                {
                    type = "session_expired",
                    sessionId = sessionId,
                    lastActivity = lastActivity,
                    inactiveMinutes = minutes,
                    maxIdleTime = "60 minutes",
                    suggestion = "Your session has expired due to inactivity. Please start a new chat session to continue.",
                    timestamp = DateTime.UtcNow,
                    recoveryInstructions = new[]
                    {
                        "Start a new chat session",
                        "Your previous session data has been preserved",
                        "Continue your conversation in the new session"
                    }
                }
            }
        };
    }

    public object CreateErrorResponse(int code, string message, string? sessionId = null, object? data = null)
    {
        _logger.LogError("MCP Error: Code {Code}, Message: {Message}, Session: {SessionId}", code, message, sessionId);

        var errorData = new Dictionary<string, object>
        {
            ["timestamp"] = DateTime.UtcNow,
            ["sessionId"] = sessionId ?? "",
            ["serverVersion"] = "1.0.0"
        };

        if (data != null)
        {
            errorData["details"] = data;
        }

        return new
        {
            jsonrpc = "2.0",
            id = "",
            error = new
            {
                code = code,
                message = message,
                data = errorData
            }
        };
    }
}