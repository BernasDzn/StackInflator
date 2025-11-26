using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;

public class StackInflatorService
{
    private readonly List<byte[]> _blocks = new List<byte[]>();
    private readonly object _lock = new object();
    public int AllocatedMb { get; private set; } = 0;

    public int BlockCount { get { lock (_lock) { return _blocks.Count; } } }

    public async Task InflateAsync(int maxMb = 1024, int stepMb = 10, ILogger logger = null)
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

            logger?.LogInformation("Inflated to {AllocatedMb} MB", AllocatedMb);
            await Task.Delay(2000);
        }

        logger?.LogInformation("Inflation complete: {AllocatedMb} MB allocated", AllocatedMb);
    }

    public void Reset(ILogger logger = null)
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
