using QRCoder;

namespace NovAcces.Web.Services;

/// <summary>
/// Rend un contenu (ici, le jeton QR signé renvoyé par l'API) en image PNG
/// encodée en data URI, directement affichable dans une balise img.
/// </summary>
public static class QrImage
{
    public static string ToDataUri(string content)
    {
        using var generator = new QRCodeGenerator();
        using var data = generator.CreateQrCode(content, QRCodeGenerator.ECCLevel.Q);
        var png = new PngByteQRCode(data).GetGraphic(10);
        return "data:image/png;base64," + Convert.ToBase64String(png);
    }
}
