using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.IO;

namespace WinFlow.WindowsInterops.Context;

public static class ImageUtilities
{
    public static string DownscaleAndEncodeJpeg(Bitmap bitmap, int maxDimension = 1024, int quality = 75)
    {
        int originalWidth = bitmap.Width;
        int originalHeight = bitmap.Height;

        int newWidth = originalWidth;
        int newHeight = originalHeight;

        if (originalWidth > maxDimension || originalHeight > maxDimension)
        {
            if (originalWidth >= originalHeight)
            {
                newWidth = maxDimension;
                newHeight = (int)Math.Round((double)originalHeight * maxDimension / originalWidth);
            }
            else
            {
                newHeight = maxDimension;
                newWidth = (int)Math.Round((double)originalWidth * maxDimension / originalHeight);
            }
        }

        using var resizedBmp = new Bitmap(newWidth, newHeight, PixelFormat.Format24bppRgb);
        using (var g = Graphics.FromImage(resizedBmp))
        {
            g.InterpolationMode = InterpolationMode.HighQualityBicubic;
            g.SmoothingMode = SmoothingMode.HighQuality;
            g.PixelOffsetMode = PixelOffsetMode.HighQuality;
            g.CompositingQuality = CompositingQuality.HighQuality;
            g.DrawImage(bitmap, 0, 0, newWidth, newHeight);
        }

        using var ms = new MemoryStream();
        var encoder = GetEncoder(ImageFormat.Jpeg);
        if (encoder != null)
        {
            using var encoderParams = new EncoderParameters(1);
            encoderParams.Param[0] = new EncoderParameter(Encoder.Quality, (long)quality);
            resizedBmp.Save(ms, encoder, encoderParams);
        }
        else
        {
            resizedBmp.Save(ms, ImageFormat.Jpeg);
        }

        var base64 = Convert.ToBase64String(ms.ToArray());
        return $"data:image/jpeg;base64,{base64}";
    }

    private static ImageCodecInfo? GetEncoder(ImageFormat format)
    {
        return ImageCodecInfo.GetImageEncoders().FirstOrDefault(codec => codec.FormatID == format.Guid);
    }
}
