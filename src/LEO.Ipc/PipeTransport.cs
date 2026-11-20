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
            // Must be non-zero: with 0-byte buffers a write only completes against a *real* read, but
            // System.IO.Pipelines (used by StreamJsonRpc) first waits with a zero-byte read → both sides stall.
            inBufferSize: 64 * 1024,
            outBufferSize: 64 * 1024,
            CreateControlPipeSecurity());

    private static readonly Lock RpcInitGate = new();

    /// <summary>
    /// Runs StreamJsonRpc's static JSON setup once, on one thread. Its type initializers race when two threads
    /// (or System.Text.Json use elsewhere) hit them at the same moment, which surfaced as a hung first session
    /// or a TypeInitializationException. Call at process start; <see cref="CreateRpc"/> also serializes creation.
    /// </summary>
    public static void Warmup()
    {
        using var ms = new MemoryStream();
        using var rpc = CreateRpc(ms);
    }

    /// <summary>Wraps a connected pipe in a JSON-RPC connection using the shared formatter settings.</summary>
    public static JsonRpc CreateRpc(Stream stream)
    {
        lock (RpcInitGate)
        {
            var formatter = new SystemTextJsonFormatter();
            var handler = new HeaderDelimitedMessageHandler(stream, stream, formatter);
            return new JsonRpc(handler);
        }
    }

    public static async Task<NamedPipeClientStream> ConnectClientAsync(
        string pipeName = LeoInfo.ControlPipeName, int timeoutMs = 2000, CancellationToken ct = default)
    {
        // Anonymous: the service never impersonates us. It identifies the client from its process token
        // (GetNamedPipeClientProcessId → OpenProcessToken), see LEO.Service ClientIdentity.
        var client = new NamedPipeClientStream(".", pipeName, PipeDirection.InOut, PipeOptions.Asynchronous,
            TokenImpersonationLevel.Anonymous);
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
