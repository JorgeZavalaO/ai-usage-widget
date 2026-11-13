using AiUsageWidget.UI;

namespace AiUsageWidget.Tests
{
    public static class PlacementTests
    {
        [Test]
        public static void FarOffscreenPlacement_IsRejected()
        {
            Assert.False(DesktopPlacement.IsVisible(1000000, 1000000, 380, 160));
            Assert.False(DesktopPlacement.IsVisible(-1000000, -1000000, 380, 160));
        }
    }
}
