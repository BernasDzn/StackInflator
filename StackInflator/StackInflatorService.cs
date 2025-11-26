using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;

public class StackInflatorService
{
    private readonly List<byte[]> _blocks = new List<byte[]>();
    private readonly object _lock = new object();
    public int AllocatedMb { get; private set; } = 0;

    public int BlockCount { get { lock (_lock) { return _blocks.Count; } } }

    public async Task InflateAsync(int maxMb = 1024, int stepMb = 10, ILogger? logger = null)
    {
        logger?.LogInformation("Starting inflation to {MaxMb}MB in steps of {StepMb}MB", maxMb, stepMb);

        while (AllocatedMb < maxMb)
        {
            int allocateMb;
            byte[] block;
            lock (_lock)
            {
                allocateMb = Math.Min(stepMb, maxMb - AllocatedMb);
                block = new byte[allocateMb * 1024 * 1024];
                // touch memory every page to force physical allocation
                for (int i = 0; i < block.Length; i += 4096)
                    block[i] = 1;

                _blocks.Add(block);
                AllocatedMb += allocateMb;
            }

            try
            {
                var (procBytes, totalBytes) = GetProcessAndChildrenMemory(logger);
                logger?.LogInformation("Inflated to {AllocatedMb} MB (proc={ProcMb} MB, proc+children={TotalMb} MB)", AllocatedMb, procBytes / 1024 / 1024, totalBytes / 1024 / 1024);
            }
            catch
            {
                logger?.LogInformation("Inflated to {AllocatedMb} MB", AllocatedMb);
            }
            await Task.Delay(2000);
        }

        try
        {
            var (procBytes, totalBytes) = GetProcessAndChildrenMemory(logger);
            logger?.LogInformation("Inflation complete: {AllocatedMb} MB allocated (proc={ProcMb} MB, proc+children={TotalMb} MB)", AllocatedMb, procBytes / 1024 / 1024, totalBytes / 1024 / 1024);
        }
        catch
        {
            logger?.LogInformation("Inflation complete: {AllocatedMb} MB allocated", AllocatedMb);
        }
    }

    private static (long procBytes, long totalBytes) GetProcessAndChildrenMemory(ILogger? logger = null)
    {
        try
        {
            int currentPid = Environment.ProcessId;
            if (RuntimeInformation.IsOSPlatform(OSPlatform.Linux))
            {
                // Build parent -> children map and memory map from /proc
                var parentToChildren = new Dictionary<int, List<int>>();
                var memMapKb = new Dictionary<int, long>();

                foreach (var dir in Directory.EnumerateDirectories("/proc"))
                {
                    var name = Path.GetFileName(dir);
                    if (!int.TryParse(name, out int pid))
                        continue;

                    try
                    {
                        string statusPath = Path.Combine(dir, "status");
                        if (!File.Exists(statusPath))
                            continue;

                        string[] lines = File.ReadAllLines(statusPath);
                        int ppid = 0;
                        long vmRssKb = 0;
                        foreach (var line in lines)
                        {
                            if (line.StartsWith("PPid:"))
                            {
                                var parts = line.Split(new[] { '\t', ' ' }, StringSplitOptions.RemoveEmptyEntries);
                                if (parts.Length >= 2 && int.TryParse(parts[1], out var v))
                                    ppid = v;
                            }
                            else if (line.StartsWith("VmRSS:"))
                            {
                                var parts = line.Split(new[] { '\t', ' ' }, StringSplitOptions.RemoveEmptyEntries);
                                if (parts.Length >= 2 && long.TryParse(parts[1], out var kb))
                                    vmRssKb = kb;
                            }
                        }

                        if (!parentToChildren.TryGetValue(ppid, out var list))
                        {
                            list = new List<int>();
                            parentToChildren[ppid] = list;
                        }
                        list.Add(pid);

                        if (vmRssKb == 0)
                        {
                            // fallback to stat (rss pages)
                            string statPath = Path.Combine(dir, "stat");
                            if (File.Exists(statPath))
                            {
                                var stat = File.ReadAllText(statPath);
                                var parts = stat.Split(' ');
                                if (parts.Length > 23 && long.TryParse(parts[23], out var rssPages))
                                {
                                    vmRssKb = rssPages * (Environment.SystemPageSize / 1024);
                                }
                            }
                        }

                        memMapKb[pid] = vmRssKb;
                    }
                    catch { continue; }
                }

                // Traverse subtree from currentPid
                var stack = new Stack<int>();
                stack.Push(currentPid);
                long totalKb = 0;
                long procKb = memMapKb.ContainsKey(currentPid) ? memMapKb[currentPid] : 0;
                var visited = new HashSet<int>();
                while (stack.Count > 0)
                {
                    var p = stack.Pop();
                    if (!visited.Add(p)) continue;
                    if (memMapKb.TryGetValue(p, out var kb)) totalKb += kb;
                    if (parentToChildren.TryGetValue(p, out var children))
                    {
                        foreach (var c in children) stack.Push(c);
                    }
                }

                return (procKb * 1024, totalKb * 1024);
            }
            else if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
            {
                // Use Toolhelp snapshot to build parent map
                var parentToChildren = new Dictionary<int, List<int>>();

                var snapshot = CreateToolhelp32Snapshot(SnapshotFlags.Process, 0);
                if (snapshot == IntPtr.Zero) throw new InvalidOperationException("Failed to create process snapshot");
                try
                {
                    PROCESSENTRY32 pe = new PROCESSENTRY32();
                    pe.dwSize = (uint)Marshal.SizeOf(pe);
                    if (Process32First(snapshot, ref pe))
                    {
                        do
                        {
                            int pid = (int)pe.th32ProcessID;
                            int ppid = (int)pe.th32ParentProcessID;
                            if (!parentToChildren.TryGetValue(ppid, out var list)) { list = new List<int>(); parentToChildren[ppid] = list; }
                            parentToChildren[ppid].Add(pid);
                        } while (Process32Next(snapshot, ref pe));
                    }
                }
                finally { CloseHandle(snapshot); }

                // Traverse
                var stack = new Stack<int>();
                stack.Push(currentPid);
                long total = 0;
                long proc = 0;
                var visited = new HashSet<int>();
                while (stack.Count > 0)
                {
                    var p = stack.Pop();
                    if (!visited.Add(p)) continue;
                    try
                    {
                        var pr = Process.GetProcessById(p);
                        long ws = pr.WorkingSet64;
                        if (p == currentPid) proc = ws;
                        total += ws;
                    }
                    catch { }
                    if (parentToChildren.TryGetValue(p, out var children))
                        foreach (var c in children) stack.Push(c);
                }

                return (proc, total);
            }
            else
            {
                // Fallback: just return current process working set
                var p = Process.GetCurrentProcess();
                return (p.WorkingSet64, p.WorkingSet64);
            }
        }
        catch (Exception ex)
        {
            logger?.LogDebug(ex, "Failed to compute process+children memory");
            var p = Process.GetCurrentProcess();
            return (p.WorkingSet64, p.WorkingSet64);
        }
    }

#region Windows Toolhelp P/Invoke
    [Flags]
    private enum SnapshotFlags : uint
    {
        Process = 0x00000002
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct PROCESSENTRY32
    {
        public uint dwSize;
        public uint cntUsage;
        public ulong th32ProcessID;
        public IntPtr th32DefaultHeapID;
        public uint th32ModuleID;
        public uint cntThreads;
        public uint th32ParentProcessID;
        public long pcPriClassBase;
        public uint dwFlags;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)]
        public string szExeFile;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr CreateToolhelp32Snapshot(SnapshotFlags dwFlags, uint th32ProcessID);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool Process32First(IntPtr hSnapshot, ref PROCESSENTRY32 lppe);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool Process32Next(IntPtr hSnapshot, ref PROCESSENTRY32 lppe);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool CloseHandle(IntPtr hObject);
#endregion

    public void Reset(ILogger? logger = null)
    {
        lock (_lock)
        {
            _blocks.Clear();
            AllocatedMb = 0;
        }

        logger?.LogInformation("Reset allocation to 0 MB");
        GC.Collect();
    }
}
