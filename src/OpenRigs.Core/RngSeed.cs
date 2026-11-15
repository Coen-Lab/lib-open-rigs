using System;

namespace OpenRigs.Core
{
    /// <summary>The seed of a task's random stream: the task file's rngSeed, or a new one when it is null.</summary>
    public static class RngSeed
    {
        /// <summary>The task file's rngSeed as the seed, or a new seed each experiment when it has none.</summary>
        public static int Resolve(double? rngSeed)
        {
            return rngSeed.HasValue ? (int)rngSeed.Value : Guid.NewGuid().GetHashCode();
        }
    }
}
