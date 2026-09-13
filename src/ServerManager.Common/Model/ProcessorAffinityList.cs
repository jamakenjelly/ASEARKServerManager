using ServerManagerTool.Common.Utils;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;

namespace ServerManagerTool.Common.Model
{
    public class ProcessorAffinityList : SortableObservableCollection<ProcessorAffinityItem>
    {
        private bool _allProcessors;

        public ProcessorAffinityList(BigInteger affinityValue)
        {
            AllProcessors = true;
            PopulateAffinities(affinityValue);
        }

        public bool AllProcessors
        {
            get { return this._allProcessors; }
            set
            {
                this._allProcessors = value;
                OnPropertyChanged(new System.ComponentModel.PropertyChangedEventArgs(nameof(AllProcessors)));
            }
        }

        public BigInteger AffinityValue
        {
            get
            {
                if (AllProcessors || this.Count(i => i.Selected) == this.Count)
                    return BigInteger.Zero;

                var affinity = BigInteger.Zero;
                foreach (var value in this.Where(i => i.Selected).Select(i => i.AffinityValue))
                    affinity += value;
                return affinity;
            }
        }

        private void PopulateAffinities(BigInteger affinityValue)
        {
            var list = ProcessUtils.GetProcessorAffinityList();
            var packageThreadCounts = ProcessUtils.GetProcessorPackageThreadCounts();
            var index = 0;

            if (!ProcessUtils.IsProcessorAffinityValid(affinityValue))
                affinityValue = BigInteger.Zero;

            foreach (var item in list)
            {
                if (item == 0)
                    continue;

                var (cpuGroup, indexWithinGroup) = ResolveCpuGroup(index, packageThreadCounts);

                this.Add(new ProcessorAffinityItem()
                {
                    Selected = affinityValue == BigInteger.Zero || ((affinityValue & item) == item),
                    AffinityValue = item,
                    Description = $"Thread {index}",
                    CpuGroup = cpuGroup,
                    IsRecommendedForWindows = indexWithinGroup < 2,
                });
                index++;
            }

            var affinity = BigInteger.Zero;
            if (this.Count(i => i.Selected) != this.Count)
            {
                foreach (var value in this.Where(i => i.Selected).Select(i => i.AffinityValue))
                    affinity += value;
            }
            AllProcessors = affinity == BigInteger.Zero;
        }

        // Given an overall logical-thread index, works out which physical CPU
        // package it falls in (based on each package's thread count from WMI)
        // and its position within that package (0-based) - so the first 2
        // threads of every CPU, not just the first 2 threads overall, can be
        // flagged as recommended to leave for Windows.
        private static (int cpuGroup, int indexWithinGroup) ResolveCpuGroup(int overallIndex, IList<int> packageThreadCounts)
        {
            var runningTotal = 0;
            for (var cpuGroup = 0; cpuGroup < packageThreadCounts.Count; cpuGroup++)
            {
                var threadsInThisPackage = packageThreadCounts[cpuGroup];
                if (overallIndex < runningTotal + threadsInThisPackage)
                    return (cpuGroup, overallIndex - runningTotal);

                runningTotal += threadsInThisPackage;
            }

            // Shouldn't happen (counts should always add up to ProcessorCount),
            // but fall back to treating it as its own trailing group rather than
            // throwing.
            return (packageThreadCounts.Count, 0);
        }
    }
}
