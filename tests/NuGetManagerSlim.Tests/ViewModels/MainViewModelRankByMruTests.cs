using System.Collections.Generic;
using System.Linq;
using NuGetManagerSlim.Models;
using NuGetManagerSlim.ViewModels;
using Xunit;

namespace NuGetManagerSlim.Tests.ViewModels
{
    public class MainViewModelRankByMruTests
    {
        private static PackageModel P(string id) => new() { PackageId = id };

        [Fact]
        public void EmptyMru_PreservesOrder()
        {
            var results = new[] { P("A"), P("B"), P("C") };
            var ranked = MainViewModel.RankByMru(results, System.Array.Empty<PackageModel>());
            Assert.Equal(new[] { "A", "B", "C" }, ranked.Select(r => r.PackageId));
        }

        [Fact]
        public void EmptyResults_ReturnsEmpty()
        {
            var ranked = MainViewModel.RankByMru(System.Array.Empty<PackageModel>(), new[] { P("A") });
            Assert.Empty(ranked);
        }

        [Fact]
        public void MruHits_BubbleToTopInMruOrder()
        {
            var results = new[] { P("A"), P("B"), P("C"), P("D"), P("E") };
            // MRU order: D was used most recently, then B
            var mru = new[] { P("D"), P("B") };

            var ranked = MainViewModel.RankByMru(results, mru);

            Assert.Equal(new[] { "D", "B", "A", "C", "E" }, ranked.Select(r => r.PackageId));
        }

        [Fact]
        public void MruEntriesNotInResults_AreIgnored()
        {
            var results = new[] { P("A"), P("B") };
            var mru = new[] { P("Z"), P("Y") };
            var ranked = MainViewModel.RankByMru(results, mru);
            Assert.Equal(new[] { "A", "B" }, ranked.Select(r => r.PackageId));
        }

        [Fact]
        public void Ranking_IsCaseInsensitive()
        {
            var results = new[] { P("Alpha"), P("Bravo") };
            var mru = new[] { P("BRAVO") };
            var ranked = MainViewModel.RankByMru(results, mru);
            Assert.Equal(new[] { "Bravo", "Alpha" }, ranked.Select(r => r.PackageId));
        }
    }
}
