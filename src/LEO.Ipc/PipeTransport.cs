using System.IO.Pipes;
using System.Security.AccessControl;
using System.Security.Principal;
using StreamJsonRpc;
using Leo.Core;

namespace Leo.Ipc;

/// <summary>Named-pipe helpers shared by the service (server) and the UI (client).</summary>
public static class PipeTransport
{
    /// <summary>
    /// ACL for the control pipe: SYSTEM + Administrators full control, interactive users read/write
    /// (fine-grained permission checks happen per call via <see cref="ClientAccess"/>).
    /// Network logons are explicitly denied — the pipe is local-only.
    /// </summary>
    public static PipeSecurity CreateControlPipeSecurity()
    {
        var security = new PipeSecurity();
        security.AddAccessRule(new PipeAccessRule(
            new SecurityIdentifier(WellKnownSidType.NetworkSid, null),
            PipeAccessRights.FullControl, AccessControlType.Deny));
        security.AddAccessRule(new PipeAccessRule(
            new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null),
            PipeAccessRights.FullControl, AccessControlType.Allow));
        security.AddAccessRule(new PipeAccessRule(
            new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null),
            PipeAccessRights.FullControl, AccessControlType.Allow));
        security.AddAccessRule(new PipeAccessRule(
            new SecurityIdentifier(WellKnownSidType.InteractiveSid, null),
            PipeAccessRights.ReadWrite, AccessControlType.Allow));
        // The account hosting the server (e.g. a developer running the service in console mode).
        using var self = WindowsIdentity.GetCurrent();
        if (self.User is { } owner)
        {
            security.AddAccessRule(new PipeAccessRule(owner, PipeAccessRights.FullControl, AccessControlType.Allow));
        }
        return security;
    }

    public static NamedPipeServerStream CreateServerStream(string pipeName) =>
        NamedPipeServerStreamAcl.Create(
            pipeName,
            PipeDirection.InOut,
            NamedPipeServerStream.MaxAllowedServerInstances,
            PipeTransmissionMode.Byte,
            PipeOptions.Asynchronous,
            inBufferSize: 0,
            outBufferSize: 0,
            CreateControlPipeSecurity());

    /// <summary>Wraps a connected pipe in a JSON-RPC connection using the shared formatter settings.</summary>
    public static JsonRpc CreateRpc(Stream stream)
    {
        var formatter = new SystemTextJsonFormatter();
        var handler = new HeaderDelimitedMessageHandler(stream, stream, formatter);
        return new JsonRpc(handler);
    }

    public static async Task<NamedPipeClientStream> ConnectClientAsync(
        string pipeName = LeoInfo.ControlPipeName, int timeoutMs = 2000, CancellationToken ct = default)
    {
        // Identification level: the service may inspect our token (to decide ClientAccess) but not act as us.
        var client = new NamedPipeClientStream(".", pipeName, PipeDirection.InOut, PipeOptions.Asynchronous,
            TokenImpersonationLevel.Identification);
        try
        {
            await client.ConnectAsync(timeoutMs, ct).ConfigureAwait(false);
            return client;
        }
        catch
        {
            await client.DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }
}
