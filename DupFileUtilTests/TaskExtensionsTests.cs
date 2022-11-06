using Microsoft.VisualStudio.TestTools.UnitTesting;
using Util;

namespace UtilTests
{
    [TestClass()]
    public class TaskExtensionsTests
    {
        [TestMethod()]
        public async Task GetIncompleteTaskTest()
        {
            var task = Task.FromResult(42);
            var result = await task.GetIncompleteTask();
            Assert.AreEqual(42, task.Result);
        }
    }
}