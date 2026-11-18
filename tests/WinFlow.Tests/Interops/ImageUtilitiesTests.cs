using System.Drawing;
using System.IO;
using WinFlow.WindowsInterops.Context;
using Xunit;

namespace WinFlow.Tests.Interops;

public class ImageUtilitiesTests
{
    [Fact]
    public void DownscaleAndEncodeJpeg_LargeImage_DownscalesToMaxDimension()
    {
        // 2000 x 1000 image
        using var bmp = new Bitmap(2000, 1000);
        using (var g = Graphics.FromImage(bmp))
        {
            g.Clear(Color.CornflowerBlue);
        }

        var base64 = ImageUtilities.DownscaleAndEncodeJpeg(bmp, maxDimension: 1024, quality: 75);
        Assert.NotNull(base64);
        Assert.StartsWith("data:image/jpeg;base64,", base64);

        var dataOnly = base64["data:image/jpeg;base64,".Length..];
        var bytes = Convert.FromBase64String(dataOnly);
        using var ms = new MemoryStream(bytes);
        using var resultBmp = new Bitmap(ms);

        Assert.Equal(1024, resultBmp.Width);
        Assert.Equal(512, resultBmp.Height);
    }

    [Fact]
    public void DownscaleAndEncodeJpeg_SmallImage_KeepsOriginalDimensions()
    {
        // 500 x 300 image
        using var bmp = new Bitmap(500, 300);
        using (var g = Graphics.FromImage(bmp))
        {
            g.Clear(Color.Red);
        }

        var base64 = ImageUtilities.DownscaleAndEncodeJpeg(bmp, maxDimension: 1024, quality: 75);
        var dataOnly = base64["data:image/jpeg;base64,".Length..];
        var bytes = Convert.FromBase64String(dataOnly);
        using var ms = new MemoryStream(bytes);
        using var resultBmp = new Bitmap(ms);

        Assert.Equal(500, resultBmp.Width);
        Assert.Equal(300, resultBmp.Height);
    }
}
