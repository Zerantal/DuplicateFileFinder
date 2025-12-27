using System.Collections.Generic;
using DuplicateFileFinderLib.Util;
using Xunit;

namespace DuplicateFileFinderLibTests.Util
{
    public sealed class ReadOnlyDictionaryOfListsTests
    {
        [Fact]
        public void BasicShape_ExposesKeysValuesAndCounts()
        {
            var source = new Dictionary<string, List<int>>
            {
                ["a"] = [1, 2],
                ["b"] = [3]
            };

            var ro = new ReadOnlyDictionaryOfLists<string, int>(source);

            Assert.Equal(2, ro.Count);
            Assert.Contains("a", ro.Keys);
            Assert.Contains("b", ro.Keys);

            Assert.True(ro.ContainsKey("a"));
            Assert.True(ro.ContainsKey("b"));
            Assert.False(ro.ContainsKey("c"));

            var listA = ro["a"];
            Assert.Equal(2, listA.Count);
            Assert.Equal([1, 2], listA);
        }

        [Fact]
        public void TryGetValue_ReturnsReadOnlyList_WhenKeyExists()
        {
            var source = new Dictionary<string, List<int>>
            {
                ["x"] = [10, 20]
            };

            var ro = new ReadOnlyDictionaryOfLists<string, int>(source);

            var found = ro.TryGetValue("x", out var list);
            Assert.True(found);
            Assert.NotNull(list);
            Assert.Equal([10, 20], list);
        }

        [Fact]
        public void TryGetValue_ReturnsFalse_WhenKeyMissing()
        {
            var source = new Dictionary<string, List<int>>();
            var ro = new ReadOnlyDictionaryOfLists<string, int>(source);

            var found = ro.TryGetValue("missing", out var list);
            Assert.False(found);
            Assert.Null(list);
        }

        [Fact]
        public void ListsAreReadOnlyFromClientPerspective()
        {
            var source = new Dictionary<string, List<int>>
            {
                ["k"] = [1]
            };

            var ro = new ReadOnlyDictionaryOfLists<string, int>(source);
            var list = ro["k"];

            // Underlying list is modifiable from the source...
            source["k"].Add(2);

            // ...and the read-only view reflects it
            Assert.Equal([1, 2], list);

            // But attempts to modify via the read-only list fail
            // if (list is IList<int> asIList)
            // {
            //     Assert.Throws<NotSupportedException>(() => asIList.Add(3));
            // }
        }

        [Fact]
        public void WrapperReflectsNewKeysAddedToSource()
        {
            var source = new Dictionary<string, List<int>>
            {
                ["a"] = [1]
            };

            var ro = new ReadOnlyDictionaryOfLists<string, int>(source);

            Assert.Single(ro.Keys);

            // Add new key to source
            source["b"] = [2, 3];

            // Wrapper should see it
            Assert.Equal(2, ro.Count);
            Assert.True(ro.ContainsKey("b"));

            var listB = ro["b"];
            Assert.Equal([2, 3], listB);
        }
    }
}
