using Avalonia;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using ZXing.QrCode;

namespace ZBS.UI.Desktop;

/// <summary>QR-код в Avalonia-битмап (для ссылки веб-пульта: телефон сканирует с экрана).</summary>
public static class QrHelper
{
    public static WriteableBitmap Render(string text, int pixels = 220)
    {
        var writer = new QRCodeWriter();
        var matrix = writer.encode(text, ZXing.BarcodeFormat.QR_CODE, pixels, pixels,
            new Dictionary<ZXing.EncodeHintType, object>
            {
                [ZXing.EncodeHintType.MARGIN] = 1,
                [ZXing.EncodeHintType.ERROR_CORRECTION] = ZXing.QrCode.Internal.ErrorCorrectionLevel.M,
            });

        var bmp = new WriteableBitmap(new PixelSize(matrix.Width, matrix.Height), new Vector(96, 96),
            PixelFormat.Bgra8888, AlphaFormat.Opaque);
        using var fb = bmp.Lock();
        var row = new byte[matrix.Width * 4];
        for (var y = 0; y < matrix.Height; y++)
        {
            for (var x = 0; x < matrix.Width; x++)
            {
                // Чёрное на белом — сканеры любят классику.
                var v = matrix[x, y] ? (byte)0x00 : (byte)0xFF;
                var i = x * 4;
                row[i] = v; row[i + 1] = v; row[i + 2] = v; row[i + 3] = 0xFF;
            }
            System.Runtime.InteropServices.Marshal.Copy(row, 0, fb.Address + y * fb.RowBytes, row.Length);
        }
        return bmp;
    }
}
