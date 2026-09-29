using QRCoder;

namespace Pos.Cloud.Host.Components.Shared;

/// <summary>Código QR del enlace <c>otpauth://</c> como imagen PNG embebida (data URI: la CSP solo permite imágenes propias o data:).</summary>
public static class QrCodes
{
    public static string ToDataUri(string text)
    {
        using var generator = new QRCodeGenerator();
        using var data = generator.CreateQrCode(text, QRCodeGenerator.ECCLevel.M);
        using var png = new PngByteQRCode(data);
        return "data:image/png;base64," + Convert.ToBase64String(png.GetGraphic(6));
    }
}
