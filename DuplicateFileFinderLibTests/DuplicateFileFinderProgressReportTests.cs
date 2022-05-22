using DuplicateFileFinderLib;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace DuplicateFileFinderLibTests;

[TestClass]
public class DuplicateFileFinderProgressReportTests
{
    [TestMethod]
    public void DuplicateFileFinderProgressReportTest()
    {
        var p = new DuplicateFileFinderProgressReport(-0.2);
        Assert.AreEqual(0.0, p.CurrentProgress, 0.001);

        p = new DuplicateFileFinderProgressReport(1.3);
        Assert.AreEqual(1.0, p.CurrentProgress, 0.0001);

        p = new DuplicateFileFinderProgressReport(0.5);
        Assert.AreEqual(0.5, p.CurrentProgress, 0.0001);
        Assert.IsFalse(p.CommencingNewTask);
        Assert.IsFalse(p.Finished);
        Assert.AreEqual("", p.NewTask);

        p = new DuplicateFileFinderProgressReport("Test Task");
        Assert.IsTrue(p.CommencingNewTask);
        Assert.AreEqual(0.0, p.CurrentProgress, 0.0001);
        Assert.IsFalse(p.Finished);
        Assert.AreEqual("Test Task", p.NewTask);

        p = new DuplicateFileFinderProgressReport();
        Assert.IsTrue(p.Finished);
    }
}