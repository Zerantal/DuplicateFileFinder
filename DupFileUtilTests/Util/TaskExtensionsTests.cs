using Microsoft.VisualStudio.TestTools.UnitTesting;
using DupFileUtil.Util;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using Util;

namespace DupFileUtil.Util.Tests
{
    [TestClass()]
    public class TaskExtensionsTests
    {
        [TestMethod()]
        public async Task GetIncompleteTaskTest()
        {
            var task = System.Threading.Tasks.Task.FromResult(42);
            var result = await task.GetIncompleteTask();
            Assert.AreEqual(42, task.Result);
        }
    }
}