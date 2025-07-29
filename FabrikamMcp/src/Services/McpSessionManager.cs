using System.Collections.Concurrent;
using System.Text.Json;

namespace FabrikamMcp.Services;

public interface IMcpSessionManager
{
    void RegisterSession(string sessionId, string clientInfo);
    void UpdateSessionActivity(string sessionId);
    bool IsSessionValid(string sessionId);
    void CleanupExpiredSessions();
    SessionInfo? GetSessionInfo(string sessionId);
    Dictionary<string, SessionInfo> GetAllSessions();
}

public class SessionInfo
{
    public string SessionId { get; set; } = "";
    public string ClientInfo { get; set; } = "";
    public DateTime CreatedAt { get; set; }
    public DateTime LastActivity { get; set; }
    public bool IsActive { get; set; }
    public int RequestCount { get; set; }
}

public class McpSessionManager : IMcpSessionManager
{
    private readonly ConcurrentDictionary<string, SessionInfo> _sessions = new();
    private readonly ILogger<McpSessionManager> _logger;
    private readonly TimeSpan _sessionTimeout = TimeSpan.FromMinutes(60); // Match session configuration
    private readonly Timer _cleanupTimer;

    public McpSessionManager(ILogger<McpSessionManager> logger)
    {
        _logger = logger;
        
        // Setup cleanup timer to run every 5 minutes
        _cleanupTimer = new Timer(
            callback: _ => CleanupExpiredSessions(),
            state: null,
            dueTime: TimeSpan.FromMinutes(5),
            period: TimeSpan.FromMinutes(5)
        );
        
        _logger.LogInformation("MCP Session Manager initialized with timeout: {Timeout}", _sessionTimeout);
    }

    public void RegisterSession(string sessionId, string clientInfo)
    {
        var sessionInfo = new SessionInfo
        {
            SessionId = sessionId,
            ClientInfo = clientInfo,
            CreatedAt = DateTime.UtcNow,
            LastActivity = DateTime.UtcNow,
            IsActive = true,
            RequestCount = 1
        };

        _sessions.AddOrUpdate(sessionId, sessionInfo, (key, existing) =>
        {
            existing.LastActivity = DateTime.UtcNow;
            existing.RequestCount++;
            existing.IsActive = true;
            return existing;
        });

        _logger.LogInformation("MCP session registered: {SessionId} | Client: {ClientInfo}", 
            sessionId, clientInfo);
    }

    public void UpdateSessionActivity(string sessionId)
    {
        if (_sessions.TryGetValue(sessionId, out var session))
        {
            session.LastActivity = DateTime.UtcNow;
            session.RequestCount++;
            session.IsActive = true;
            
            _logger.LogDebug("Session activity updated: {SessionId} | Request #{RequestCount}", 
                sessionId, session.RequestCount);
        }
        else
        {
            _logger.LogWarning("Attempted to update activity for unknown session: {SessionId}", sessionId);
        }
    }

    public bool IsSessionValid(string sessionId)
    {
        if (!_sessions.TryGetValue(sessionId, out var session))
        {
            return false;
        }

        var isExpired = DateTime.UtcNow - session.LastActivity > _sessionTimeout;
        if (isExpired)
        {
            session.IsActive = false;
            _logger.LogInformation("Session expired: {SessionId} | Last activity: {LastActivity}", 
                sessionId, session.LastActivity);
            return false;
        }

        return session.IsActive;
    }

    public SessionInfo? GetSessionInfo(string sessionId)
    {
        _sessions.TryGetValue(sessionId, out var session);
        return session;
    }

    public Dictionary<string, SessionInfo> GetAllSessions()
    {
        return _sessions.ToDictionary(kvp => kvp.Key, kvp => kvp.Value);
    }

    public void CleanupExpiredSessions()
    {
        var expiredSessions = new List<string>();
        var cutoffTime = DateTime.UtcNow - _sessionTimeout;

        foreach (var kvp in _sessions)
        {
            if (kvp.Value.LastActivity < cutoffTime)
            {
                expiredSessions.Add(kvp.Key);
            }
        }

        foreach (var sessionId in expiredSessions)
        {
            if (_sessions.TryRemove(sessionId, out var removedSession))
            {
                _logger.LogInformation("Cleaned up expired session: {SessionId} | Duration: {Duration:F1} minutes", 
                    sessionId, (DateTime.UtcNow - removedSession.CreatedAt).TotalMinutes);
            }
        }

        if (expiredSessions.Count > 0)
        {
            _logger.LogInformation("Cleanup completed: Removed {Count} expired sessions", expiredSessions.Count);
        }
    }

    public void Dispose()
    {
        _cleanupTimer?.Dispose();
    }
}