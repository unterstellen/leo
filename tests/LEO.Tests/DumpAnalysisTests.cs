using Microsoft.Diagnostics.Runtime;
using Xunit.Abstractions;

namespace Leo.Tests;

/// <summary>
/// Dev tool: prints managed thread stacks from a dump of the LEO service.
/// Runs only when LEO_DUMP points at a .dmp file.
/// </summary>
public sealed class DumpAnalysisTests(ITestOutputHelper output)
{
    [Fact]
    public void Print_Managed_Stacks()
    {
        var path = Environment.GetEnvironmentVariable("LEO_DUMP");
        if (string.IsNullOrEmpty(path) || !File.Exists(path)) return;

        using var target = DataTarget.LoadDump(path);
        var clr = target.ClrVersions[0];
        // Use the installed runtime's DAC instead of downloading it from the symbol server.
        var dac = Path.Combine(Path.GetDirectoryName(clr.ModuleInfo.FileName) ?? "", "mscordaccore.dll");
        output.WriteLine($"CLR {clr.Version}, DAC {dac} exists={File.Exists(dac)}");
        using var runtime = File.Exists(dac) ? clr.CreateRuntime(dac) : clr.CreateRuntime();
        foreach (var thread in runtime.Threads)
        {
            var frames = thread.EnumerateStackTrace().Select(f => f.ToString()).Where(f => !string.IsNullOrEmpty(f)).Take(40).ToList();
            if (frames.Count == 0) continue;
            output.WriteLine($"=== Thread {thread.ManagedThreadId} (OS {thread.OSThreadId:x}) ===");
            foreach (var f in frames) output.WriteLine("  " + f);
        }

        // Pending async work: StreamJsonRpc / pipe related state machines still on the heap.
        var counts = runtime.Heap.EnumerateObjects()
            .Select(o => o.Type?.Name ?? "")
            .Where(n => n.Contains("StreamJsonRpc.JsonRpc+<", StringComparison.Ordinal) ||
                        n.Contains("Nerdbank.Streams.PipeExtensions", StringComparison.Ordinal) ||
                        n.Contains("Leo.", StringComparison.Ordinal) && n.Contains("+<", StringComparison.Ordinal))
            .GroupBy(n => n).OrderByDescending(g => g.Count()).Take(30);
        output.WriteLine("=== async state machines on heap ===");
        foreach (var g in counts) output.WriteLine($"  {g.Count(),4}  {g.Key}");

        // Status of each JsonRpc's read loop task (and the task it unwraps).
        foreach (var rpc in runtime.Heap.EnumerateObjects().Where(o => o.Type?.Name == "StreamJsonRpc.JsonRpc"))
        {
            var t = rpc.ReadObjectField("readLinesTask");
            output.WriteLine($"=== readLinesTask of JsonRpc @ {rpc.Address:x}: {Describe(t)}");
            try
            {
                var holder = t.ReadObjectField("m_contingentProperties").ReadObjectField("m_exceptionsHolder");
                var list = holder.ReadObjectField("m_faultExceptions");
                var items = list.ReadObjectField("_items");
                var size = list.ReadField<int>("_size");
                var array = items.AsArray();
                for (var i = 0; i < size; i++)
                {
                    var edi = array.GetObjectValue(i);
                    for (var ex = edi.ReadObjectField("_exception"); !ex.IsNull; ex = ex.ReadObjectField("_innerException"))
                    {
                        var message = ex.ReadObjectField("_message");
                        output.WriteLine($"    EXCEPTION {ex.Type?.Name}: {(message.IsNull ? "" : message.AsString(2000))}");
                        var stack = ex.ReadObjectField("_remoteStackTraceString");
                        if (!stack.IsNull) output.WriteLine("      remote stack: " + stack.AsString(4000));
                        if (ex.AsException() is { } clrEx)
                        {
                            output.WriteLine($"      HRESULT 0x{clrEx.HResult:x8}");
                            foreach (var frame in clrEx.StackTrace) output.WriteLine("      at " + frame);
                        }
                        var fileName = ex.Type?.GetFieldByName("_fileName") is not null ? ex.ReadObjectField("_fileName") : default;
                        if (!fileName.IsNull) output.WriteLine("      file: " + fileName.AsString(500));
                    }
                }
            }
            catch (Exception e) { output.WriteLine("    (couldn't read exception: " + e.Message + ")"); }
            foreach (var f in t.Type!.Fields.Where(f => f.IsObjectReference))
            {
                var inner = t.ReadObjectField(f.Name!);
                if (!inner.IsNull) output.WriteLine($"    .{f.Name} -> {inner.Type?.Name} {Describe(inner)}");
            }
        }

        // Field-level state of every JsonRpc + message handler instance.
        foreach (var obj in runtime.Heap.EnumerateObjects())
        {
            var name = obj.Type?.Name;
            if (name is not ("StreamJsonRpc.JsonRpc" or "StreamJsonRpc.HeaderDelimitedMessageHandler" or "System.IO.Pipes.NamedPipeServerStream")) continue;
            output.WriteLine($"=== {name} @ {obj.Address:x} ===");
            foreach (var field in obj.Type!.Fields)
            {
                string value;
                try
                {
                    value = field.IsObjectReference
                        ? (obj.ReadObjectField(field.Name!) is { IsNull: false } o
                            ? $"{o.Type?.Name}{(o.Type?.IsString == true ? " \"" + o.AsString(200) + "\"" : "")}"
                            : "null")
                        : field.ElementType switch
                        {
                            ClrElementType.Boolean => obj.ReadField<bool>(field.Name!).ToString(),
                            ClrElementType.Int32 => obj.ReadField<int>(field.Name!).ToString(),
                            ClrElementType.Int64 => obj.ReadField<long>(field.Name!).ToString(),
                            _ => $"<{field.ElementType}>",
                        };
                }
                catch (Exception ex) { value = $"<{ex.GetType().Name}>"; }
                output.WriteLine($"  {field.Name} = {value}");
            }
        }
    }

    private static string Describe(ClrObject task)
    {
        if (task.IsNull || task.Type?.GetFieldByName("m_stateFlags") is null) return "";
        var flags = task.ReadField<int>("m_stateFlags");
        var parts = new List<string>();
        if ((flags & 0x10000) != 0) parts.Add("Started");
        if ((flags & 0x20000) != 0) parts.Add("DelegateInvoked");
        if ((flags & 0x200000) != 0) parts.Add("Faulted");
        if ((flags & 0x400000) != 0) parts.Add("Canceled");
        if ((flags & 0x1000000) != 0) parts.Add("RanToCompletion");
        if ((flags & 0x2000000) != 0) parts.Add("WaitingForChildren");
        return $"flags=0x{flags:x} [{string.Join(",", parts)}]";
    }
}
