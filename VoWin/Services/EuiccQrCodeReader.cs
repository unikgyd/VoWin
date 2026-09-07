using System.IO;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using ZXing;
using ZXing.Common;

namespace VoWin.Services;

internal static class EuiccQrCodeReader
{
    private const long MaxImageBytes = 25 * 1024 * 1024;
    private const long MaxPixels = 100_000_000;

    public static string Read(string path)
    {
        var file = new FileInfo(path);
        if (!file.Exists) throw new FileNotFoundException("二维码图片不存在。", path);
        if (file.Length <= 0 || file.Length > MaxImageBytes)
            throw new InvalidDataException("二维码图片为空或大于 25 MB。");

        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        var decoder = BitmapDecoder.Create(stream, BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.OnLoad);
        if (decoder.Frames.Count == 0) throw new InvalidDataException("图片中没有可读取的画面。");

        foreach (var frame in decoder.Frames)
        {
            if ((long)frame.PixelWidth * frame.PixelHeight > MaxPixels)
                throw new InvalidDataException("二维码图片分辨率过大。");

            BitmapSource bitmap = frame;
            if (bitmap.Format != PixelFormats.Bgra32)
                bitmap = new FormatConvertedBitmap(bitmap, PixelFormats.Bgra32, null, 0);

            var stride = checked(bitmap.PixelWidth * 4);
            var pixels = new byte[checked(stride * bitmap.PixelHeight)];
            bitmap.CopyPixels(pixels, stride, 0);
            var source = new RGBLuminanceSource(
                pixels, bitmap.PixelWidth, bitmap.PixelHeight, RGBLuminanceSource.BitmapFormat.BGRA32);
            var reader = new BarcodeReaderGeneric
            {
                AutoRotate = true,
                Options = new DecodingOptions
                {
                    TryHarder = true,
                    PossibleFormats = [BarcodeFormat.QR_CODE]
                }
            };
            var result = reader.Decode(source);
            if (!string.IsNullOrWhiteSpace(result?.Text)) return result.Text.Trim();
        }

        throw new InvalidDataException("图片中没有识别到有效二维码，请选择原始清晰图片。");
    }
}
