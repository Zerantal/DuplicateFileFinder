using Microsoft.VisualStudio.TestTools.UnitTesting;
using Util;

namespace UtilTests
{
    [TestClass()]
    public class StringUtilTests
    {
        [TestMethod()]
        public void FileSizeToStringTest()
        {
            Assert.AreEqual("523 B", StringUtil.FileSizeToString(523));
            Assert.AreEqual("95.14 KB", StringUtil.FileSizeToString(97423));
            Assert.AreEqual("71.49 MB", StringUtil.FileSizeToString(74965013));
            Assert.AreEqual("7.12 GB", StringUtil.FileSizeToString(7645641651));
            Assert.AreEqual("718.01 TB", StringUtil.FileSizeToString(789463515620523));
        }
    }
}