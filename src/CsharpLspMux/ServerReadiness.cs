namespace CsharpLspMux;

/// <summary>
/// Tracks the lifecycle of a child LSP server from process spawn to workspace-ready.
/// </summary>
public enum ServerReadiness
{
    /// <summary>Process started; LSP handshake not yet complete.</summary>
    Starting = 0,
    /// <summary>LSP handshake complete; workspace still loading.</summary>
    Initialized = 1,
    /// <summary>Workspace fully loaded; semantic queries safe to forward.</summary>
    Ready = 2,
}
