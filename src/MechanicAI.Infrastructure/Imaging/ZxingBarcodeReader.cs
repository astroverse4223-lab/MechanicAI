using MechanicAI.Application.Abstractions;
using ZXing;
using ZXing.Common;

namespace MechanicAI.Infrastructure.Imaging;

/// <summary>
/// Decodes barcodes from raw pixels. VIN labels on door jambs and windshields typically use
/// Code 39, Code 128, Data Matrix, or QR codes.
/// </summary>
public sealed class ZxingBarcodeReader : MechanicAI.Application.Abstractions.IBarcodeReader
{
    public IReadOnlyList<string> Decode(DecodedPixels pixels)
    {
        if (pixels.Width <= 0 || pixels.Height <= 0 || pixels.Bgra.Length < pixels.Width * pixels.Height * 4) return [];
        var reader = new BarcodeReaderGeneric
        {
            AutoRotate = true,
            Options = new DecodingOptions
            {
                TryHarder = true,
                TryInverted = true,
                PossibleFormats =
                [
                    BarcodeFormat.CODE_39, BarcodeFormat.CODE_128, BarcodeFormat.DATA_MATRIX, BarcodeFormat.QR_CODE, BarcodeFormat.PDF_417,
                ],
            },
        };

        var source = new RGBLuminanceSource(pixels.Bgra, pixels.Width, pixels.Height, RGBLuminanceSource.BitmapFormat.BGRA32);
        var results = reader.DecodeMultiple(source);
        return results?.Select(r => r.Text).Where(t => !string.IsNullOrWhiteSpace(t)).Distinct().ToList() ?? [];
    }
}
